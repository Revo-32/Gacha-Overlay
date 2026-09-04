using GachaOverlay.Core.Business;
using GachaOverlay.Core.Timers;

namespace GachaOverlay.Tests;

public sealed class M3AcidBoostCorrectiveTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-04T00:00:00Z");

    [Fact]
    public void FullSupply_NoBoost_CompletesIn150Minutes()
    {
        var (engine, time, _) = CreateOnlineEngine();
        using (engine)
        {
            engine.StartAcid();
            var acid = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
            Assert.Equal(TimeSpan.FromMinutes(150), engine.EstimateRemaining(acid, time.GetUtcNow()));

            time.Advance(TimeSpan.FromMinutes(150));
            Assert.Equal(SharedTimerState.Ready,
                Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid).State);
        }
    }

    [Fact]
    public void FullSupply_AcidBoost_CompletesIn90Minutes()
    {
        var (engine, time, _) = CreateOnlineEngine();
        using (engine)
        {
            engine.StartAcid();
            engine.StartAcidBoost();
            var acid = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
            Assert.Equal(TimeSpan.FromMinutes(90), engine.EstimateRemaining(acid, time.GetUtcNow()));

            time.Advance(TimeSpan.FromMinutes(90));
            Assert.Equal(SharedTimerState.Ready,
                Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid).State);
        }
    }

    [Fact]
    public void FullSupply_Mansion_CompletesIn50Minutes()
    {
        var (engine, time, _) = CreateOnlineEngine();
        using (engine)
        {
            engine.StartAcid();
            engine.StartMansionBoost(acid: true);
            var acid = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
            Assert.Equal(TimeSpan.FromMinutes(50), engine.EstimateRemaining(acid, time.GetUtcNow()));

            time.Advance(TimeSpan.FromMinutes(50));
            Assert.Equal(SharedTimerState.Ready,
                Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid).State);
        }
    }

    [Fact]
    public void FullSupply_AcidBoostAndMansion_CompletesIn30Minutes()
    {
        var (engine, time, _) = CreateOnlineEngine();
        using (engine)
        {
            engine.StartAcid();
            engine.StartAcidBoost();
            engine.StartMansionBoost(acid: true);
            var acid = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
            Assert.Equal(TimeSpan.FromMinutes(30), engine.EstimateRemaining(acid, time.GetUtcNow()));

            time.Advance(TimeSpan.FromMinutes(30));
            Assert.Equal(SharedTimerState.Ready,
                Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid).State);
        }
    }

    [Fact]
    public void MidCycleBoost_With50UnitsRemaining_UsesTrueDoubleRate()
    {
        var (engine, time, _) = CreateOnlineEngine();
        using (engine)
        {
            engine.StartAcid();
            time.Advance(TimeSpan.FromMinutes(75));
            var halfway = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
            Assert.Equal(TimeSpan.FromMinutes(75), halfway.Remaining);

            engine.StartAcidBoost();
            halfway = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
            Assert.Equal(TimeSpan.FromMinutes(37.5), engine.EstimateRemaining(halfway, time.GetUtcNow()));

            time.Advance(TimeSpan.FromMinutes(37.5));
            Assert.Equal(SharedTimerState.Ready,
                Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid).State);
        }
    }

    [Fact]
    public void BoostAllowance_EndsAfterExactly80ProducedUnits()
    {
        var (engine, time, _) = CreateOnlineEngine();
        using (engine)
        {
            engine.StartAcid();
            engine.StartAcidBoost();
            time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(15));
            engine.Update(OnlinePlaytimeAvailability.Online);
            Assert.InRange(engine.GetAcidBoostState()!.RemainingBoostedProductUnits,
                0.999999, 1.000001);

            time.Advance(TimeSpan.FromSeconds(45));
            var acid = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
            Assert.Null(engine.GetAcidBoostState());
            Assert.Equal(TimeSpan.FromMinutes(120), acid.AccumulatedOnlineTime);
        }
    }

    [Fact]
    public void NoSupplyOfflineAndUnknown_DoNotConsumeBoostAllowance()
    {
        var (engine, time, _) = CreateOnlineEngine();
        using (engine)
        {
            engine.StartAcidBoost();
            time.Advance(TimeSpan.FromMinutes(10));
            engine.Update(OnlinePlaytimeAvailability.Online);
            Assert.Equal(80, engine.GetAcidBoostState()!.RemainingBoostedProductUnits);

            engine.StartAcid();
            engine.Update(OnlinePlaytimeAvailability.Offline);
            time.Advance(TimeSpan.FromHours(1));
            engine.Update(OnlinePlaytimeAvailability.Offline);
            Assert.Equal(80, engine.GetAcidBoostState()!.RemainingBoostedProductUnits);

            engine.Update(OnlinePlaytimeAvailability.Unknown);
            time.Advance(TimeSpan.FromHours(1));
            var acid = Find(engine.Update(OnlinePlaytimeAvailability.Unknown), BusinessTimerIds.Acid);
            Assert.Equal(80, engine.GetAcidBoostState()!.RemainingBoostedProductUnits);
            Assert.Equal(TimeSpan.Zero, acid.AccumulatedOnlineTime);
        }
    }

    [Fact]
    public void WallClockExpiry_EndsBoostWhileProductionIsPaused()
    {
        var (engine, time, _) = CreateOnlineEngine();
        using (engine)
        {
            engine.StartAcid();
            engine.StartAcidBoost();
            engine.Update(OnlinePlaytimeAvailability.Offline);
            time.Advance(TimeSpan.FromHours(24));
            var acid = Find(engine.Update(OnlinePlaytimeAvailability.Offline), BusinessTimerIds.Acid);

            Assert.Null(engine.GetAcidBoostState());
            Assert.Equal(TimeSpan.Zero, acid.AccumulatedOnlineTime);
        }
    }

    [Fact]
    public void AcidAndMansion_UseInstantaneousSixTimesRate()
    {
        var (engine, time, _) = CreateOnlineEngine();
        using (engine)
        {
            engine.StartAcid();
            engine.StartAcidBoost();
            engine.StartMansionBoost(acid: true);
            time.Advance(TimeSpan.FromMinutes(1));
            var acid = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);

            Assert.Equal(TimeSpan.FromMinutes(6), acid.AccumulatedOnlineTime);
            Assert.InRange(engine.GetAcidBoostState()!.RemainingBoostedProductUnits,
                75.999999, 76.000001);
        }
    }

    [Fact]
    public void AllowanceExhaustion_ChangesSixTimesToThreeTimesWithoutReset()
    {
        var (engine, time, _) = CreateOnlineEngine();
        using (engine)
        {
            engine.StartAcid();
            engine.StartAcidBoost();
            engine.StartMansionBoost(acid: true);
            time.Advance(TimeSpan.FromMinutes(20));
            var exhausted = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
            Assert.Equal(TimeSpan.FromMinutes(120), exhausted.AccumulatedOnlineTime);
            Assert.Null(engine.GetAcidBoostState());

            time.Advance(TimeSpan.FromMinutes(1));
            var mansionOnly = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
            Assert.Equal(TimeSpan.FromMinutes(123), mansionOnly.AccumulatedOnlineTime);
            Assert.Contains(engine.Update(OnlinePlaytimeAvailability.Online), item =>
                item.TimerId == BusinessTimerIds.MansionAcid && item.State == SharedTimerState.Running);
        }
    }

    [Fact]
    public void Restart_PreservesBoostBoundsAllowanceAndProductionProgress()
    {
        var store = new MemoryTimerStore();
        var time = new ManualTimeProvider(Epoch);
        DateTimeOffset activated;
        DateTimeOffset expires;

        using (var first = new BusinessManagerEngine(new SharedTimerRegistry(store, time)))
        {
            first.Update(OnlinePlaytimeAvailability.Online);
            first.StartAcid();
            first.StartAcidBoost();
            time.Advance(TimeSpan.FromMinutes(10));
            var acid = Find(first.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
            var boost = first.GetAcidBoostState()!;
            Assert.Equal(TimeSpan.FromMinutes(20), acid.AccumulatedOnlineTime);
            activated = boost.ActivatedAtUtc;
            expires = boost.ExpiresAtUtc;
        }

        using var restarted = new BusinessManagerEngine(new SharedTimerRegistry(store, time));
        var restoredAcid = Find(restarted.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
        var restoredBoost = restarted.GetAcidBoostState()!;
        Assert.Equal(TimeSpan.FromMinutes(20), restoredAcid.AccumulatedOnlineTime);
        Assert.Equal(activated, restoredBoost.ActivatedAtUtc);
        Assert.Equal(expires, restoredBoost.ExpiresAtUtc);
        Assert.InRange(restoredBoost.RemainingBoostedProductUnits, 66.666665, 66.666668);
    }

    [Fact]
    public void EvidenceMetadata_UsesGranularAcceptedConfidence()
    {
        AssertEvidence("Mansion business boost duration", MechanicEvidenceConfidence.VerifiedOfficial);
        AssertEvidence("Mansion business boost multiplier", MechanicEvidenceConfidence.VerifiedCommunity);
        AssertEvidence("Original Heists group cooldown", MechanicEvidenceConfidence.VerifiedOfficial);
        AssertEvidence("Doomsday group cooldown", MechanicEvidenceConfidence.VerifiedOfficial);
        AssertEvidence("Casino group cooldown", MechanicEvidenceConfidence.VerifiedOfficial);
        AssertEvidence("Cayo Perico group cooldown", MechanicEvidenceConfidence.VerifiedOfficial);
        AssertEvidence("Cayo Perico solo cooldown", MechanicEvidenceConfidence.VerifiedOfficial);
        AssertEvidence("Cayo Perico hard-mode window", MechanicEvidenceConfidence.VerifiedCommunity);
        AssertEvidence("Kortz contact delay", MechanicEvidenceConfidence.VerifiedCommunity);
        AssertEvidence("Kortz hard-mode window", MechanicEvidenceConfidence.VerifiedCommunity);
    }

    private static (BusinessManagerEngine Engine, ManualTimeProvider Time, MemoryTimerStore Store)
        CreateOnlineEngine()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new MemoryTimerStore();
        var engine = new BusinessManagerEngine(new SharedTimerRegistry(store, time));
        engine.Update(OnlinePlaytimeAvailability.Online);
        return (engine, time, store);
    }

    private static SharedTimerSnapshot Find(IEnumerable<SharedTimerSnapshot> values, string id) =>
        values.Single(item => item.TimerId == id);

    private static void AssertEvidence(string mechanic, MechanicEvidenceConfidence confidence) =>
        Assert.Contains(BusinessMechanicCatalog.Evidence,
            item => item.Mechanic == mechanic && item.Confidence == confidence);

    private sealed class MemoryTimerStore : ISharedTimerStore
    {
        private IReadOnlyList<SharedTimerPersistedEntry> _items = [];
        public IReadOnlyList<SharedTimerPersistedEntry> Load() => _items;
        public bool Save(IReadOnlyCollection<SharedTimerPersistedEntry> entries)
        {
            _items = entries.ToArray();
            return true;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utc;
        private long _timestamp;

        public ManualTimeProvider(DateTimeOffset utc) => _utc = utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            _utc += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }
}

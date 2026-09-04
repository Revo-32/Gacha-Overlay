using System.ComponentModel;
using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Business;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Timers;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests;

public sealed class M3BusinessManagerFinalCorrectiveTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-04T00:00:00Z");
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void BusinessManagerUi_UsesFinalTerminologyStableHardRowsAndEmbeddedMansionControls()
    {
        var viewModel = ReadPresentation("BusinessManagerViewModel.cs");
        var settings = ReadPresentation("FoundationWindow.xaml");

        Assert.DoesNotContain("산성 연구소", viewModel);
        Assert.Contains("Add(\"LSD\"", viewModel);
        Assert.Contains("Content=\"LSD\"", settings);
        Assert.Contains("Content=\"맨션 보유\"", settings);
        Assert.DoesNotContain("공급품", viewModel);
        Assert.Contains("보급품 소모", viewModel);
        Assert.DoesNotContain("보급품 100% → 0%", viewModel);
        Assert.Contains("금고수입", viewModel);
        Assert.DoesNotContain("안전 수입", viewModel);
        Assert.Contains("용의도 Max", viewModel);
        Assert.DoesNotContain("용의도 0% → 100%", viewModel);
        Assert.Contains("Add(\"세차장\"", viewModel);
        foreach (var label in new[] { "구습", "신습", "카습", "페습", "코습" })
        {
            Assert.Contains($"\"{label}\"", viewModel);
            Assert.Contains($"Content=\"{label}\"", settings);
        }
        Assert.Equal(2, Count(viewModel, "HardRow(cooldownId"));
        Assert.DoesNotContain("준비 시작", viewModel);
        Assert.Contains("Row(\"맨션 생산 부스트\", BusinessTimerIds.MansionBunker", viewModel);
        Assert.Contains("Row(\"맨션 생산 부스트\", BusinessTimerIds.MansionAcid", viewModel);
        Assert.Contains("\"자체 부스트\"", viewModel);
    }

    [Fact]
    public void MansionBoost_RemainsOneSharedTarget()
    {
        var time = new ManualTimeProvider(Epoch);
        using var engine = new BusinessManagerEngine(new SharedTimerRegistry(new MemoryTimerStore(), time));

        engine.StartMansionBoost(acid: false);
        Assert.Contains(engine.Update(OnlinePlaytimeAvailability.Unknown),
            item => item.TimerId == BusinessTimerIds.MansionBunker);
        engine.StartMansionBoost(acid: true);
        var switched = engine.Update(OnlinePlaytimeAvailability.Unknown);

        Assert.DoesNotContain(switched, item => item.TimerId == BusinessTimerIds.MansionBunker);
        Assert.Single(switched, item => item.TimerId == BusinessTimerIds.MansionAcid);
    }

    [Fact]
    public void GeneralButtons_RunRealRuntimeProgressAndNotifyOnce()
    {
        var elapsed = TimeSpan.Zero;
        var settings = AppSettings.CreateDefault() with { TimerCompletionSoundEnabled = true };
        using var general = new GtaoTimerHudViewModel(Localization(), settings,
            Dispatcher.CurrentDispatcher, () => elapsed);
        using var business = new BusinessManagerViewModel(
            new BusinessManagerEngine(new SharedTimerRegistry(new MemoryTimerStore())),
            new RemoteOnlinePlaytimeStatusSource(settings), settings,
            Dispatcher.CurrentDispatcher, general);
        business.SetUnlocked(true);
        var soundCount = 0;
        general.CompletionSoundRequested += () => soundCount++;

        business.StartGeneral12Command.Execute(null);
        Assert.Equal("12:00", business.GeneralTimerStatus);
        elapsed += TimeSpan.FromMinutes(1);
        general.Refresh();
        business.Refresh();
        Assert.Equal("11:00", business.GeneralTimerStatus);
        elapsed += TimeSpan.FromMinutes(11);
        general.Refresh();
        general.Refresh();

        Assert.Equal(1, soundCount);
        Assert.True(business.IsGeneralTimerAttentionActive);
    }

    [Fact]
    public void RepresentativeBusinessReadyTransition_NotifiesAndHighlightsOnce()
    {
        var time = new ManualTimeProvider(Epoch);
        var settings = AppSettings.CreateDefault() with
        {
            BusinessSpecialCargoEnabled = true,
            BusinessSpecialCargoWarehouseCount = 1,
        };
        using var general = new GtaoTimerHudViewModel(Localization(), settings,
            Dispatcher.CurrentDispatcher);
        using var business = new BusinessManagerViewModel(
            new BusinessManagerEngine(new SharedTimerRegistry(new MemoryTimerStore(), time)),
            new RemoteOnlinePlaytimeStatusSource(settings), settings,
            Dispatcher.CurrentDispatcher, general);
        business.SetUnlocked(true);
        var row = business.Sections.Single().Rows.Single();
        var highlightTransitions = 0;
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BusinessTimerRowViewModel.IsAttentionActive))
                highlightTransitions++;
        };

        row.PrimaryCommand.Execute(null);
        time.Advance(BusinessMechanicCatalog.WarehouseStaffDispatch);
        business.Refresh();
        business.Refresh();

        Assert.True(row.IsAttentionActive);
        Assert.Equal(1, highlightTransitions);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 1)]
    [InlineData(10, 1)]
    public void EarlyAlert_RespectsOffFiveAndTenWithoutChangingReadyState(
        int alertMinutes,
        int expectedCount)
    {
        var time = new ManualTimeProvider(Epoch);
        using var engine = new BusinessManagerEngine(new SharedTimerRegistry(new MemoryTimerStore(), time));
        var count = 0;
        engine.EarlyAlert += _ => count++;
        engine.StartCargo(1);
        var original = engine.Update(OnlinePlaytimeAvailability.Unknown)
            .Single(item => item.TimerId == BusinessTimerIds.Cargo(1));
        time.Advance(BusinessMechanicCatalog.WarehouseStaffDispatch -
            TimeSpan.FromMinutes(Math.Max(1, alertMinutes)));
        var current = engine.Update(OnlinePlaytimeAvailability.Unknown, alertMinutes)
            .Single(item => item.TimerId == BusinessTimerIds.Cargo(1));
        engine.Update(OnlinePlaytimeAvailability.Unknown, alertMinutes);

        Assert.Equal(expectedCount, count);
        Assert.Equal(SharedTimerState.Running, current.State);
        Assert.Equal(original.ReadyAtUtc, current.ReadyAtUtc);
    }

    [Fact]
    public void OnlinePauseAfterEarlyAlert_DoesNotRepeat()
    {
        var time = new ManualTimeProvider(Epoch);
        using var engine = new BusinessManagerEngine(new SharedTimerRegistry(new MemoryTimerStore(), time));
        var count = 0;
        engine.EarlyAlert += _ => count++;
        engine.Update(OnlinePlaytimeAvailability.Online);
        engine.StartBunker();
        time.Advance(TimeSpan.FromMinutes(131));
        engine.Update(OnlinePlaytimeAvailability.Online, 10);
        engine.Update(OnlinePlaytimeAvailability.Offline, 10);
        time.Advance(TimeSpan.FromHours(2));
        var paused = engine.Update(OnlinePlaytimeAvailability.Offline, 10)
            .Single(item => item.TimerId == BusinessTimerIds.Bunker);

        Assert.Equal(1, count);
        Assert.Equal(SharedTimerState.Paused, paused.State);
        Assert.Equal(TimeSpan.FromMinutes(9), paused.Remaining);
    }

    [Fact]
    public void WallClockRestart_PersistsEarlyAlertIdentity()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new MemoryTimerStore();
        using (var first = new BusinessManagerEngine(new SharedTimerRegistry(store, time)))
        {
            var count = 0;
            first.EarlyAlert += _ => count++;
            first.StartCargo(1);
            time.Advance(TimeSpan.FromMinutes(44));
            first.Update(OnlinePlaytimeAvailability.Unknown, 5);
            Assert.Equal(1, count);
        }

        using var restarted = new BusinessManagerEngine(new SharedTimerRegistry(store, time));
        var replayCount = 0;
        restarted.EarlyAlert += _ => replayCount++;
        restarted.Update(OnlinePlaytimeAvailability.Unknown, 5);
        Assert.Equal(0, replayCount);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(15, 0)]
    public void EarlyAlertSetting_NormalizesToSupportedValues(int value, int expected) =>
        Assert.Equal(expected, BusinessManagerEngine.NormalizeEarlyAlertMinutes(value));

    private static ResourceLocalizationService Localization() =>
        new(SupportedLocales.Korean);

    private static string ReadPresentation(string fileName) => File.ReadAllText(
        Path.Combine(RepositoryRoot, "src", "GachaOverlay.App", "Presentation", fileName));

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
             index += value.Length)
            count++;
        return count;
    }

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

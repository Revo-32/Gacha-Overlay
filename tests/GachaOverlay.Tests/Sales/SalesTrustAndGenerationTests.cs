using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales;

public sealed class SalesTrustAndGenerationTests
{
    [Fact]
    public void Test17_TrustedPending_UntrustedBatchPreservesPending()
    {
        var engine = PendingTrusted();
        engine.ApplyObservationBatch(SalesTestFactory.Batch(
            2, false, SalesObservationStatus.Paused));
        var record = Assert.Single(engine.Records);
        Assert.Equal(SaleDomainState.Pending, record.DomainState);
        Assert.Equal(SaleObservationTrust.TemporarilyUntrusted, record.ObservationTrust);
    }

    [Fact]
    public void Test18_TrustedSold_UntrustedBatchPreservesSold()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustSold(engine, "1");
        engine.ApplyObservationBatch(SalesTestFactory.Batch(
            2, false, SalesObservationStatus.Error));
        var record = Assert.Single(engine.Records);
        Assert.Equal(SaleDomainState.Sold, record.DomainState);
        Assert.Equal(SaleObservationTrust.TemporarilyUntrusted, record.ObservationTrust);
    }

    [Fact]
    public void Test19_NeverObserved_UntrustedBatchRemainsNeverObserved()
    {
        var engine = EngineWithOne();
        engine.ApplyObservationBatch(SalesTestFactory.Batch(
            1, false, SalesObservationStatus.Paused));
        Assert.Equal(
            SaleObservationTrust.NeverObserved,
            Assert.Single(engine.Records).ObservationTrust);
    }

    [Fact]
    public void Test20_UntrustedBatchOmission_IsNotNotSoldEvidence()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustSold(engine, "1");
        engine.ApplyObservationBatch(SalesTestFactory.Batch(
            2, false, SalesObservationStatus.Paused));
        Assert.Equal(SaleDomainState.Sold, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void Test21_TrustedBatchOmission_DoesNotChangeState()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustSold(engine, "1");
        engine.ApplyObservationBatch(SalesTestFactory.Batch(
            2, true, SalesObservationStatus.Live));
        Assert.Equal(SaleDomainState.Sold, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void Test22_PartialBatch_DoesNotChangeUnobservedRecord()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("1"),
            SalesTestFactory.Message("2"),
        });
        engine.ApplyObservationBatch(new SalesObservationBatch(
            1,
            SalesTestFactory.Epoch,
            SalesObservationStatus.Live,
            true,
            SalesObservationCompleteness.Partial,
            new[] { SalesTestFactory.Observation("1", SaleReactionOutcome.Sold, 1) }));
        Assert.Equal(
            SaleObservationTrust.NeverObserved,
            engine.Records.Single(record => record.MessageId == "2").ObservationTrust);
    }

    [Fact]
    public void SoldThenNotObserved_RemainsSold()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustSold(engine, "1", 1);

        engine.ApplyObservationBatch(SalesTestFactory.Batch(
            2,
            true,
            SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.NotObserved, 2)));

        Assert.Equal(SaleDomainState.Sold, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void SoldThenCompleteTrustedNotSold_ReturnsToPending()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustSold(engine, "1", 1);

        SalesTestFactory.TrustPending(engine, "1", 2);

        Assert.Equal(SaleDomainState.Pending, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void PendingThenPartialBatch_RemainsPendingWithoutPositiveNotSoldEvidence()
    {
        var engine = EngineWithOne();
        engine.ApplyObservationBatch(new SalesObservationBatch(
            2,
            SalesTestFactory.Epoch.AddMinutes(2),
            SalesObservationStatus.Partial,
            true,
            SalesObservationCompleteness.Partial,
            Array.Empty<SaleReactionObservation>(),
            SalesCoverageState.Partial));

        var record = Assert.Single(engine.Records);
        Assert.Equal(SaleDomainState.Pending, record.DomainState);
        Assert.Equal(SaleObservationTrust.NeverObserved, record.ObservationTrust);
    }

    [Fact]
    public void Test23_PendingToTrustedSold_LeavesQueue()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustSold(engine, "1");
        Assert.Empty(engine.Current.ActiveItems);
    }

    [Fact]
    public void Test24_CurrentSellerSold_AdvancesNext()
    {
        var engine = TwoEntries();
        SalesTestFactory.TrustSold(engine, "1");
        Assert.Equal("2", engine.Current.CurrentSeller!.MessageId);
    }

    [Fact]
    public void Test25_SoldToTrustedNotSold_ReentersQueue()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustSold(engine, "1", 1);
        SalesTestFactory.TrustPending(engine, "1", 2);
        Assert.Equal("1", Assert.Single(engine.Current.ActiveItems).MessageId);
    }

    [Fact]
    public void Test26_Reentry_UsesOriginalChronologicalPosition()
    {
        var engine = TwoEntries();
        SalesTestFactory.TrustSold(engine, "1", 1);
        SalesTestFactory.TrustPending(engine, "1", 2);
        Assert.Equal(new[] { "1", "2" }, engine.Current.ActiveItems.Select(x => x.MessageId));
    }

    [Fact]
    public void Test27_OldSoldEntryReturning_CanBecomeCurrentSeller()
    {
        var engine = TwoEntries();
        SalesTestFactory.TrustSold(engine, "1", 1);
        Assert.Equal("2", engine.Current.CurrentSeller!.MessageId);
        SalesTestFactory.TrustPending(engine, "1", 2);
        Assert.Equal("1", engine.Current.CurrentSeller!.MessageId);
    }

    [Fact]
    public void Test28_RepeatedSoldObservation_IsIdempotent()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustSold(engine, "1", 1);
        var revision = engine.Current.Revision;
        Assert.False(engine.ApplyObservationBatch(SalesTestFactory.Batch(
            1, true, SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.Sold, 1))));
        Assert.Equal(revision, engine.Current.Revision);
    }

    [Fact]
    public void Test29_RepeatedNotSoldObservation_IsIdempotent()
    {
        var engine = PendingTrusted();
        var revision = engine.Current.Revision;
        Assert.False(engine.ApplyObservationBatch(SalesTestFactory.Batch(
            1, true, SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.NotSold, 1))));
        Assert.Equal(revision, engine.Current.Revision);
    }

    [Fact]
    public void Test30_NewGeneration_IsApplied()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustSold(engine, "1", 10);
        SalesTestFactory.TrustPending(engine, "1", 11);
        Assert.Equal(11, Assert.Single(engine.Records).LastObservationGeneration);
    }

    [Fact]
    public void Test31_OlderGeneration_IsIgnored()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustSold(engine, "1", 11);
        Assert.False(engine.ApplyObservationBatch(SalesTestFactory.Batch(
            10, true, SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.NotSold, 10))));
        Assert.Equal(SaleDomainState.Sold, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void Test32_SameGenerationDuplicate_IsSafe()
    {
        var engine = EngineWithOne();
        var batch = SalesTestFactory.Batch(
            5, true, SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.Sold, 5));
        Assert.True(engine.ApplyObservationBatch(batch));
        Assert.False(engine.ApplyObservationBatch(batch));
    }

    [Fact]
    public void RepeatedOutcomeInNewPollingGeneration_UpdatesFreshnessWithoutUiSnapshot()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustSold(engine, "1", 1);
        var revision = engine.Current.Revision;
        Assert.False(engine.ApplyObservationBatch(SalesTestFactory.Batch(
            2,
            true,
            SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.Sold, 2))));
        Assert.Equal(revision, engine.Current.Revision);
        Assert.Equal(2, Assert.Single(engine.Records).LastObservationGeneration);
    }

    [Fact]
    public void Test33_StaleSourceRevisionObservation_IsIgnored()
    {
        var engine = EngineWithOne();
        var oldRevision = Assert.Single(engine.Records).SourceRevision;
        engine.ApplySourceUpdate(SalesTestFactory.Message("1", content: "edited"));
        engine.ApplyObservationBatch(SalesTestFactory.Batch(
            1, true, SalesObservationStatus.Live,
            SalesTestFactory.Observation(
                "1", SaleReactionOutcome.Sold, 1, sourceRevision: oldRevision)));
        Assert.Equal(SaleDomainState.Pending, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void Test34_FullResyncBatch_PublishesOneAtomicSnapshot()
    {
        var engine = TwoEntries();
        var published = 0;
        engine.SnapshotChanged += _ => published++;
        engine.ApplyObservationBatch(SalesTestFactory.Batch(
            1, true, SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.Sold, 1),
            SalesTestFactory.Observation("2", SaleReactionOutcome.NotSold, 1)));
        Assert.Equal(1, published);
        Assert.Equal("2", engine.Current.CurrentSeller!.MessageId);
    }

    [Fact]
    public void Test35_PartialBatch_DoesNotPublishIntermediateSnapshots()
    {
        var engine = TwoEntries();
        var published = new List<SalesQueueSnapshot>();
        engine.SnapshotChanged += published.Add;
        engine.ApplyObservationBatch(new SalesObservationBatch(
            1,
            SalesTestFactory.Epoch,
            SalesObservationStatus.Live,
            true,
            SalesObservationCompleteness.Partial,
            new[] { SalesTestFactory.Observation("1", SaleReactionOutcome.Sold, 1) }));
        Assert.Single(published);
        Assert.Equal("2", published[0].CurrentSeller!.MessageId);
    }

    private static SalesStateEngine EngineWithOne()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1", seconds: 1));
        return engine;
    }

    private static SalesStateEngine PendingTrusted()
    {
        var engine = EngineWithOne();
        SalesTestFactory.TrustPending(engine, "1");
        return engine;
    }

    private static SalesStateEngine TwoEntries()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("1", seconds: 1),
            SalesTestFactory.Message("2", seconds: 2),
        });
        return engine;
    }
}

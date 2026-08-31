using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales;

public sealed class SalesSourceProjectionTests
{
    [Fact]
    public void Test01_Create_ProducesSaleRecord()
    {
        var engine = SalesTestFactory.Engine();
        Assert.True(engine.ApplySourceCreate(SalesTestFactory.Message("1")));
        Assert.Equal("1", Assert.Single(engine.Records).MessageId);
    }

    [Fact]
    public void Test02_DuplicateCreate_DoesNotDuplicateRecord()
    {
        var engine = SalesTestFactory.Engine();
        var message = SalesTestFactory.Message("1");
        engine.ApplySourceCreate(message);
        Assert.False(engine.ApplySourceCreate(message));
        Assert.Single(engine.Records);
    }

    [Fact]
    public void Test03_Update_UpdatesSameMessageId()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1", globalName: "Old"));
        Assert.True(engine.ApplySourceUpdate(SalesTestFactory.Message(
            "1", globalName: "New", content: "edited")));
        Assert.Equal("1", Assert.Single(engine.Records).MessageId);
        Assert.Equal("New", Assert.Single(engine.Records).AuthorGlobalDisplayName);
    }

    [Fact]
    public void Test04_Update_PreservesQueueOrder()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("1", seconds: 1),
            SalesTestFactory.Message("2", seconds: 2),
        });
        engine.ApplySourceUpdate(SalesTestFactory.Message("1", seconds: 99, content: "edit"));
        Assert.Equal(new[] { "1", "2" }, engine.Current.ActiveItems.Select(x => x.MessageId));
    }

    [Fact]
    public void Test05_Delete_ExcludesQueueEntry()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        engine.ApplySourceDelete("1");
        Assert.Empty(engine.Current.ActiveItems);
        Assert.Equal(SaleDomainState.Deleted, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void Test06_DuplicateDelete_IsSafe()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        Assert.True(engine.ApplySourceDelete("1"));
        Assert.False(engine.ApplySourceDelete("1"));
    }

    [Fact]
    public void Test07_LateObservationAfterDelete_IsIgnored()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        engine.ApplySourceDelete("1");
        engine.ApplyObservationBatch(SalesTestFactory.Batch(
            1, true, SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.NotSold, 1)));
        Assert.Equal(SaleDomainState.Deleted, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void Test08_RebuildFromSourceSnapshot_CreatesAllRecords()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("1"),
            SalesTestFactory.Message("2"),
            SalesTestFactory.Message("3"),
        });
        Assert.Equal(3, engine.Records.Count);
    }

    [Fact]
    public void Test09_SnapshotMissingRecord_DefinesItAsDeleted()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("1"),
            SalesTestFactory.Message("2"),
        });
        engine.ApplySourceSnapshot(new[] { SalesTestFactory.Message("2") });
        Assert.Equal(
            SaleDomainState.Deleted,
            engine.Records.Single(record => record.MessageId == "1").DomainState);
    }

    [Fact]
    public void Test10_NewSource_IsPendingAndNeverObserved()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        var record = Assert.Single(engine.Records);
        Assert.Equal(SaleDomainState.Pending, record.DomainState);
        Assert.Equal(SaleObservationTrust.NeverObserved, record.ObservationTrust);
    }

    [Fact]
    public void Test11_NeverObservedPending_IsImmediatelyActive()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        Assert.Single(engine.Current.ActiveItems);
    }

    [Fact]
    public void Test12_MultipleUnverifiedMessages_AreChronological()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("3", seconds: 3),
            SalesTestFactory.Message("1", seconds: 1),
            SalesTestFactory.Message("2", seconds: 2),
        });
        Assert.Equal(new[] { "1", "2", "3" }, engine.Current.ActiveItems.Select(x => x.MessageId));
    }

    [Fact]
    public void Test13_UnverifiedItem_CanBeCurrentSeller()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        Assert.True(engine.Current.CurrentSeller!.IsProvisional);
    }

    [Fact]
    public void Test14_Snapshot_FlagsUnverifiedActiveItems()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        Assert.True(engine.Current.ContainsUnverifiedActiveItems);
    }

    [Fact]
    public void Test15_ExplicitTrustedNotSold_ProducesTrustedPending()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        SalesTestFactory.TrustPending(engine, "1");
        var record = Assert.Single(engine.Records);
        Assert.Equal(SaleDomainState.Pending, record.DomainState);
        Assert.Equal(SaleObservationTrust.Trusted, record.ObservationTrust);
    }

    [Fact]
    public void Test16_ExplicitTrustedSold_ProducesTrustedSold()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        SalesTestFactory.TrustSold(engine, "1");
        var record = Assert.Single(engine.Records);
        Assert.Equal(SaleDomainState.Sold, record.DomainState);
        Assert.Equal(SaleObservationTrust.Trusted, record.ObservationTrust);
    }
}

using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Sales.Uia;

public sealed class M6SalesIntegrationTests
{
    [Fact]
    public void TrustedSoldFromProductionContract_ReachesM5Engine()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(SalesTestFactory.Message("1")));
        setup.Source.Publish(Batch(setup, "1", SaleReactionOutcome.Sold, 1));
        Assert.Equal(SaleDomainState.Sold, Assert.Single(setup.Engine.Records).DomainState);
        Assert.Empty(setup.Engine.Current.ActiveItems);
    }

    [Fact]
    public void TrustedNotSoldFromProductionContract_RestoresOriginalQueuePosition()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(
            SalesTestFactory.Message("1", seconds: 1),
            SalesTestFactory.Message("2", seconds: 2)));
        setup.Source.Publish(Batch(setup, "1", SaleReactionOutcome.Sold, 1));
        setup.Source.Publish(Batch(setup, "1", SaleReactionOutcome.NotSold, 2));
        Assert.Equal(new[] { "1", "2" },
            setup.Engine.Current.ActiveItems.Select(entry => entry.MessageId));
    }

    [Fact]
    public void NotObservedFromProductionContract_DoesNotMutateDomain()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(SalesTestFactory.Message("1")));
        setup.Source.Publish(Batch(setup, "1", SaleReactionOutcome.NotObserved, 1));
        var record = Assert.Single(setup.Engine.Records);
        Assert.Equal(SaleDomainState.Pending, record.DomainState);
        Assert.Equal(SaleObservationTrust.NeverObserved, record.ObservationTrust);
    }

    [Theory]
    [InlineData(SaleReactionOutcome.NotSold, SaleDomainState.Pending)]
    [InlineData(SaleReactionOutcome.Sold, SaleDomainState.Sold)]
    public void PausedStatus_PreservesLastTrustedDomainState(
        SaleReactionOutcome trustedOutcome,
        SaleDomainState expected)
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(SalesTestFactory.Message("1")));
        setup.Source.Publish(Batch(setup, "1", trustedOutcome, 1));
        setup.Source.Publish(new SalesObservationBatch(
            2,
            SalesTestFactory.Epoch,
            SalesObservationStatus.Paused,
            false,
            SalesObservationCompleteness.Partial,
            Array.Empty<SaleReactionObservation>(),
            StatusReason: SalesObservationReason.TargetChannelNotSelected));
        var record = Assert.Single(setup.Engine.Records);
        Assert.Equal(expected, record.DomainState);
        Assert.Equal(SaleObservationTrust.TemporarilyUntrusted, record.ObservationTrust);
    }

    [Fact]
    public void NewRpcSaleWhilePaused_RemainsNeverObservedAndActive()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        setup.Source.Publish(new SalesObservationBatch(
            1,
            SalesTestFactory.Epoch,
            SalesObservationStatus.Paused,
            false,
            SalesObservationCompleteness.Partial,
            Array.Empty<SaleReactionObservation>()));
        coordinator.ApplySourceState(State(SalesTestFactory.Message("1")));
        var record = Assert.Single(setup.Engine.Records);
        Assert.Equal(SaleObservationTrust.NeverObserved, record.ObservationTrust);
        Assert.Single(setup.Engine.Current.ActiveItems);
    }

    [Fact]
    public void ReturnResync_AppliesSoldAndNotSoldAtomically()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(
            SalesTestFactory.Message("1", seconds: 1),
            SalesTestFactory.Message("2", seconds: 2)));
        setup.Source.Publish(new SalesObservationBatch(
            1,
            SalesTestFactory.Epoch,
            SalesObservationStatus.Live,
            true,
            SalesObservationCompleteness.Full,
            new[]
            {
                Observation(setup, "1", SaleReactionOutcome.Sold, 1),
                Observation(setup, "2", SaleReactionOutcome.NotSold, 1),
            },
            SalesCoverageState.Complete));
        Assert.Equal("2", setup.Engine.Current.CurrentSeller!.MessageId);
        Assert.Equal(1, setup.Engine.Current.ActiveCount);
    }

    [Fact]
    public void DeletedMessage_RejectsLateUiaObservation()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(SalesTestFactory.Message("1")));
        var observation = Observation(setup, "1", SaleReactionOutcome.Sold, 1);
        coordinator.ApplySourceState(State());
        setup.Source.Publish(new SalesObservationBatch(
            1,
            SalesTestFactory.Epoch,
            SalesObservationStatus.Live,
            true,
            SalesObservationCompleteness.Full,
            new[] { observation }));
        Assert.Equal(SaleDomainState.Deleted, Assert.Single(setup.Engine.Records).DomainState);
    }

    [Fact]
    public void SoldRecord_RemainsObservationTarget()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(
            SalesTestFactory.Message("1"),
            SalesTestFactory.Message("2")));
        setup.Source.Publish(Batch(setup, "1", SaleReactionOutcome.Sold, 1));
        Assert.Equal(2, setup.Source.Targets.Targets.Count);
        Assert.Contains(setup.Source.Targets.Targets, target => target.MessageId == "1");
    }

    [Fact]
    public void DeletedRecord_IsRemovedFromObservationTargets()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(
            SalesTestFactory.Message("1"),
            SalesTestFactory.Message("2")));
        coordinator.ApplySourceState(State(SalesTestFactory.Message("2")));
        Assert.DoesNotContain(setup.Source.Targets.Targets, target => target.MessageId == "1");
        Assert.Single(setup.Source.Targets.Targets);
    }

    [Fact]
    public void SourceUpdate_RefreshesTargetSourceRevision()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(SalesTestFactory.Message("1", content: "old")));
        var oldRevision = Assert.Single(setup.Source.Targets.Targets).SourceRevision;
        coordinator.ApplySourceState(State(SalesTestFactory.Message("1", content: "new")));
        Assert.True(Assert.Single(setup.Source.Targets.Targets).SourceRevision > oldRevision);
    }

    [Fact]
    public void TargetSet_UsesResolvedSalesChannelIdentity()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.SetTargetChannel("999", "판매-실측");
        Assert.Equal("999", setup.Source.Targets.SalesChannelId);
        Assert.Equal("판매-실측", setup.Source.Targets.SalesChannelName);
    }

    [Fact]
    public void SalesMasterOff_StopsSensorAndHidesQueue()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(SalesTestFactory.Message("1")));
        coordinator.ApplySettings(AppSettings.CreateDefault() with
        {
            SalesTrackingEnabled = false,
        });
        Assert.False(setup.Source.IsRunning);
        Assert.False(setup.Engine.Current.IsTrackingEnabled);
        Assert.Empty(setup.Engine.Current.ActiveItems);
    }

    [Fact]
    public void SalesMasterOn_RebuildsAllTargetsAndRequestsFreshResync()
    {
        var settings = AppSettings.CreateDefault() with { SalesTrackingEnabled = false };
        var setup = Setup(settings);
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(
            SalesTestFactory.Message("1"),
            SalesTestFactory.Message("2")));
        coordinator.ApplySettings(AppSettings.CreateDefault());
        Assert.True(setup.Source.IsRunning);
        Assert.Equal(SalesObservationStatus.Resyncing, setup.Source.Status);
        Assert.Equal(2, setup.Source.Targets.Targets.Count);
        Assert.Equal(1, setup.Source.ResyncRequestCount);
    }

    [Fact]
    public void MainChatOnlyStateRepublish_DoesNotChangeTargetRevision()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(State(SalesTestFactory.Message("1")));
        var revision = setup.Source.Targets.Revision;
        coordinator.ApplySourceState(new DiscordMessageState(
            1,
            false,
            new[] { SalesTestFactory.Message("main") },
            new[] { SalesTestFactory.Message("1") }));
        Assert.Equal(revision, setup.Source.Targets.Revision);
    }

    private static SalesObservationBatch Batch(
        SetupResult setup,
        string messageId,
        SaleReactionOutcome outcome,
        long generation) => new(
            generation,
            SalesTestFactory.Epoch,
            SalesObservationStatus.Live,
            true,
            SalesObservationCompleteness.Full,
            new[] { Observation(setup, messageId, outcome, generation) },
            SalesCoverageState.Complete);

    private static SaleReactionObservation Observation(
        SetupResult setup,
        string messageId,
        SaleReactionOutcome outcome,
        long generation) => new(
            messageId,
            outcome,
            outcome != SaleReactionOutcome.NotObserved,
            SalesTestFactory.Epoch,
            generation,
            setup.Engine.Records.Single(record => record.MessageId == messageId).SourceRevision);

    private static DiscordMessageState State(params NormalizedDiscordMessage[] messages) =>
        new(1, false, Array.Empty<NormalizedDiscordMessage>(), messages);

    private static SetupResult Setup(AppSettings? settings = null)
    {
        var localization = new ResourceLocalizationService("en", NullAppLogger.Instance);
        var resolver = new GuildDisplayNameResolver(clock: () => SalesTestFactory.Epoch);
        resolver.SetAccountScope("account");
        var engine = new SalesStateEngine(resolver, clock: () => SalesTestFactory.Epoch);
        var source = new MockSalesReactionObservationSource();
        var coordinator = new SalesPresentationCoordinator(
            engine,
            source,
            new SalesQueueViewModel(localization),
            localization,
            NullAppLogger.Instance,
            settings ?? AppSettings.CreateDefault(),
            Dispatcher.CurrentDispatcher);
        return new SetupResult(coordinator, source, engine);
    }

    private sealed record SetupResult(
        SalesPresentationCoordinator Coordinator,
        MockSalesReactionObservationSource Source,
        SalesStateEngine Engine);
}

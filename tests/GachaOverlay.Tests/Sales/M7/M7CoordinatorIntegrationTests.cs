using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Sales.M7;

public sealed class M7CoordinatorIntegrationTests
{
    [Fact]
    public void FullRpcSourceAndSensorState_ReachesCompositeLive()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        PrepareLive(setup, SalesTestFactory.Message("1"));
        Assert.Equal(SalesFeatureHealthState.Live, setup.ViewModel.HealthState);
        Assert.Equal(SalesStatusIconKind.LiveDot, setup.ViewModel.IconKind);
        Assert.Contains("Current Seller", setup.ViewModel.PrimaryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void RpcGenerationAheadOfSalesSource_CannotReuseStaleLive()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        PrepareLive(setup, SalesTestFactory.Message("1"));
        coordinator.ApplyRpcStatus(Status(DiscordConnectionState.Connected, generation: 2));
        DrainDispatcher();
        Assert.Equal(SalesFeatureHealthState.Connecting, setup.ViewModel.HealthState);
        Assert.NotEqual(SalesStatusIconKind.LiveDot, setup.ViewModel.IconKind);
    }

    [Fact]
    public void PausedHealth_PreservesQueueAndUsesDynamicChannelGuidance()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        PrepareLive(setup, SalesTestFactory.Message("1"));
        setup.Source.Publish(new SalesObservationBatch(
            2,
            SalesTestFactory.Epoch.AddMinutes(2),
            SalesObservationStatus.Paused,
            false,
            SalesObservationCompleteness.Partial,
            Array.Empty<SaleReactionObservation>(),
            SalesCoverageState.None,
            SalesObservationReason.TargetChannelNotSelected,
            1,
            0,
            0,
            0,
            1,
            setup.Source.Targets.Revision));
        DrainDispatcher();
        Assert.Equal(SalesFeatureHealthState.Paused, setup.ViewModel.HealthState);
        Assert.Contains("Current Seller", setup.ViewModel.PrimaryLine, StringComparison.Ordinal);
        Assert.Equal("Keep #🚒판매모집 open", setup.ViewModel.SecondaryLine);
    }

    [Fact]
    public void SalesOffOn_HidesThenStartsFreshResyncPresentation()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        PrepareLive(setup, SalesTestFactory.Message("1"));
        coordinator.ApplySettings(AppSettings.CreateDefault() with
        {
            SalesTrackingEnabled = false,
        });
        DrainDispatcher();
        Assert.False(setup.ViewModel.IsVisible);
        Assert.False(setup.Source.IsRunning);

        coordinator.ApplySettings(AppSettings.CreateDefault());
        DrainDispatcher();
        Assert.True(setup.Source.IsRunning);
        Assert.Equal(SalesFeatureHealthState.Resyncing, setup.ViewModel.HealthState);
        Assert.True(setup.ViewModel.IsSpinnerActive);
        Assert.NotEqual(SalesStatusIconKind.LiveDot, setup.ViewModel.IconKind);
    }

    [Fact]
    public void TrustedSoldCurrentChange_EmitsOnePresentationFadeRequest()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        PrepareLive(
            setup,
            SalesTestFactory.Message("1", seconds: 0),
            SalesTestFactory.Message("2", seconds: 1));
        var requests = new List<SalesQueueAnimationRequest>();
        setup.ViewModel.AnimationRequested += requests.Add;
        PublishComplete(
            setup,
            2,
            ("1", SaleReactionOutcome.Sold),
            ("2", SaleReactionOutcome.NotSold));
        DrainDispatcher();
        Assert.Equal("2", setup.Engine.Current.CurrentSeller?.MessageId);
        Assert.Single(requests, SalesQueueAnimationRequest.SoldTransition);

        PublishComplete(
            setup,
            3,
            ("1", SaleReactionOutcome.Sold),
            ("2", SaleReactionOutcome.NotSold));
        DrainDispatcher();
        Assert.Single(requests, SalesQueueAnimationRequest.SoldTransition);
    }

    private static void PrepareLive(
        SetupResult setup,
        params NormalizedDiscordMessage[] messages)
    {
        setup.Coordinator.Start();
        setup.Coordinator.SetTargetChannel("sales", "🚒판매모집");
        setup.Coordinator.ApplyRpcStatus(Status(DiscordConnectionState.Connected));
        setup.Coordinator.ApplySourceState(new DiscordMessageState(
            1,
            false,
            Array.Empty<NormalizedDiscordMessage>(),
            messages));
        PublishComplete(
            setup,
            1,
            messages.Select(message =>
                (message.MessageId, SaleReactionOutcome.NotSold)).ToArray());
        DrainDispatcher();
    }

    private static void PublishComplete(
        SetupResult setup,
        long generation,
        params (string MessageId, SaleReactionOutcome Outcome)[] outcomes)
    {
        var records = setup.Engine.Records.ToDictionary(
            record => record.MessageId,
            record => record.SourceRevision,
            StringComparer.Ordinal);
        var observations = outcomes.Select(item => new SaleReactionObservation(
            item.MessageId,
            item.Outcome,
            true,
            SalesTestFactory.Epoch.AddMinutes(generation),
            generation,
            records[item.MessageId])).ToArray();
        setup.Source.Publish(new SalesObservationBatch(
            generation,
            SalesTestFactory.Epoch.AddMinutes(generation),
            SalesObservationStatus.Live,
            true,
            SalesObservationCompleteness.Full,
            observations,
            SalesCoverageState.Complete,
            SalesObservationReason.None,
            outcomes.Length,
            outcomes.Length,
            outcomes.Count(item => item.Outcome == SaleReactionOutcome.Sold),
            outcomes.Count(item => item.Outcome == SaleReactionOutcome.NotSold),
            0,
            setup.Source.Targets.Revision));
    }

    private static DiscordConnectionStatus Status(
        DiscordConnectionState state,
        long generation = 1) => new(
            state,
            generation,
            state.ToString(),
            SalesTestFactory.Epoch);

    private static SetupResult Setup()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var localization = new ResourceLocalizationService("en", NullAppLogger.Instance);
        var resolver = new GuildDisplayNameResolver(clock: () => SalesTestFactory.Epoch);
        resolver.SetAccountScope("account");
        var engine = new SalesStateEngine(resolver, clock: () => SalesTestFactory.Epoch);
        var source = new MockSalesReactionObservationSource();
        var viewModel = new SalesQueueViewModel(localization);
        var coordinator = new SalesPresentationCoordinator(
            engine,
            source,
            viewModel,
            localization,
            NullAppLogger.Instance,
            AppSettings.CreateDefault(),
            dispatcher);
        return new SetupResult(coordinator, source, engine, viewModel);
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed record SetupResult(
        SalesPresentationCoordinator Coordinator,
        MockSalesReactionObservationSource Source,
        SalesStateEngine Engine,
        SalesQueueViewModel ViewModel);
}

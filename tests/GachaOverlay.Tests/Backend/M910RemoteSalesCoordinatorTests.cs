using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;
using LSOverlay.Protocol;
using SalesTests = GachaOverlay.Tests.Sales.SalesTestFactory;

namespace GachaOverlay.Tests.Backend;

public sealed class M910RemoteSalesCoordinatorTests
{
    [Fact]
    public void TransientRemoteLoss_PreservesLastTrustedQueueButDisablesAuthority()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplyRemoteSalesBootstrap(Bootstrap("generation-1"));
        DrainDispatcher();
        Assert.Equal("1", setup.ViewModel.Presentation.CurrentMessageId);
        Assert.Equal(SalesFeatureHealthState.Live, setup.ViewModel.HealthState);

        coordinator.ApplyRemoteSalesStatus(RemoteSalesStatusNames.Reconnecting);
        DrainDispatcher();

        Assert.Equal("1", setup.ViewModel.Presentation.CurrentMessageId);
        Assert.Equal(SalesFeatureHealthState.Resyncing, setup.ViewModel.HealthState);
        Assert.Single(setup.Engine.Current.ActiveItems);
    }

    [Fact]
    public void AccessRevoked_RedactsPresentationAndRetainsTrustedStateForAuthorizedRecovery()
    {
        var setup = Setup();
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplyRemoteSalesBootstrap(Bootstrap("generation-1"));
        DrainDispatcher();
        var trustedRevision = setup.Engine.Current.Revision;

        coordinator.ApplyRemoteSalesStatus(OverlayTransportProtocol.SalesAccessRevoked);
        DrainDispatcher();

        Assert.Null(setup.ViewModel.Presentation.CurrentMessageId);
        Assert.Equal(SalesFeatureHealthState.Error, setup.ViewModel.HealthState);
        Assert.Single(setup.Engine.Current.ActiveItems);

        coordinator.ApplyRemoteSalesBootstrap(Bootstrap("generation-2"));
        DrainDispatcher();

        Assert.Equal("1", setup.ViewModel.Presentation.CurrentMessageId);
        Assert.Equal(SalesFeatureHealthState.Live, setup.ViewModel.HealthState);
        Assert.Equal(trustedRevision, setup.Engine.Current.Revision);
    }

    private static SetupResult Setup()
    {
        var localization = new ResourceLocalizationService("en", NullAppLogger.Instance);
        var catalog = SalesTests.Catalog(SalesTests.Product(
            "product",
            "999",
            "product"));
        var engine = SalesTests.Engine(catalog);
        var viewModel = new SalesQueueViewModel(localization);
        var coordinator = new SalesPresentationCoordinator(
            engine,
            viewModel,
            localization,
            NullAppLogger.Instance,
            AppSettings.CreateDefault() with { SalesTrackingEnabled = true },
            Dispatcher.CurrentDispatcher);
        return new SetupResult(coordinator, engine, viewModel);
    }

    private static SalesBootstrapResponse Bootstrap(string generation) => new(
        OverlayTransportProtocol.Version,
        new ChatChannelDescriptor(10, 20, "sales", 0, false),
        generation,
        0,
        new[] { Message() },
        new[]
        {
            new SalesCompletionObservation(
                1,
                false,
                false,
                SalesEvidenceCoverage.Complete,
                SalesTests.Epoch),
        },
        SalesBootstrapCoverage.Complete);

    private static ChatMessage Message() => new(
        1,
        10,
        20,
        "Default",
        0,
        new ChatAuthor(30, "seller", "Seller", "Guild Seller", false, false),
        string.Empty,
        SalesTests.Epoch,
        null,
        false,
        false,
        false,
        0,
        new[] { new ChatEmoji(999, "product", false) },
        Array.Empty<ChatAttachment>(),
        Array.Empty<ChatEmbed>(),
        Array.Empty<ChatMention>(),
        Array.Empty<ChatSticker>(),
        Array.Empty<ChatForwardSnapshot>(),
        null,
        Array.Empty<ChatComponent>(),
        null);

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
        SalesStateEngine Engine,
        SalesQueueViewModel ViewModel);
}

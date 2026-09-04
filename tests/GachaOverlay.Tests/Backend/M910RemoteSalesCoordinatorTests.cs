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

    [Fact]
    public void OwnPartiallyParsedCanonicalSale_RecordsOnlyLivePendingToSoldMutation()
    {
        var history = new RecordingSalesHistoryStore();
        var setup = Setup(history);
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.SetAuthenticatedUser("30");
        coordinator.ApplyRemoteSalesBootstrap(Bootstrap("generation-1", "decorative"));
        Assert.Equal(SaleParseStatus.PartiallyParsed, Assert.Single(setup.Engine.Records).ParseStatus);
        Assert.Empty(history.Values);

        coordinator.ApplyRemoteSalesMutation(new SalesMutationEnvelope(
            OverlayTransportProtocol.Version,
            "generation-1",
            1,
            OverlayTransportProtocol.SalesCompletionEvidence,
            20,
            1,
            null,
            new SalesCompletionObservation(
                1,
                true,
                false,
                SalesEvidenceCoverage.Complete,
                SalesTests.Epoch.AddMinutes(1))));

        Assert.Equal(SalesTests.Epoch.AddMinutes(1), history.Values["product"]);
        Assert.Equal(1, history.WriteCount);

        coordinator.ApplyRemoteSalesMutation(new SalesMutationEnvelope(
            OverlayTransportProtocol.Version,
            "generation-1",
            2,
            OverlayTransportProtocol.SalesCompletionEvidence,
            20,
            1,
            null,
            new SalesCompletionObservation(
                1,
                true,
                false,
                SalesEvidenceCoverage.Complete,
                SalesTests.Epoch.AddMinutes(2))));

        Assert.Equal(1, history.WriteCount);
        Assert.Equal(SalesTests.Epoch.AddMinutes(1), history.Values["product"]);
    }

    private static SetupResult Setup(ISalesHistoryStore? history = null)
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
            Dispatcher.CurrentDispatcher,
            salesHistory: history);
        return new SetupResult(coordinator, engine, viewModel);
    }

    private static SalesBootstrapResponse Bootstrap(
        string generation,
        string content = "") => new(
        OverlayTransportProtocol.Version,
        new ChatChannelDescriptor(10, 20, "sales", 0, false),
        generation,
        0,
        new[] { Message(content) },
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

    private static ChatMessage Message(string content) => new(
        1,
        10,
        20,
        "Default",
        0,
        new ChatAuthor(30, "seller", "Seller", "Guild Seller", false, false),
        content,
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

    private sealed class RecordingSalesHistoryStore : ISalesHistoryStore
    {
        public event Action? Changed;

        public Dictionary<string, DateTimeOffset> Values { get; } = new(StringComparer.Ordinal);

        public int WriteCount { get; private set; }

        public IReadOnlyList<SalesHistoryEntry> Snapshot() => Values
            .Select(pair => new SalesHistoryEntry(pair.Key, pair.Value))
            .ToArray();

        public bool RecordSold(IReadOnlyCollection<string> productIds, DateTimeOffset soldAt)
        {
            foreach (var productId in productIds)
            {
                Values[productId] = soldAt;
            }

            WriteCount++;
            Changed?.Invoke();
            return true;
        }

        public bool Clear()
        {
            Values.Clear();
            Changed?.Invoke();
            return true;
        }
    }
}

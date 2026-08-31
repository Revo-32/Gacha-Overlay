using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Sales;

public sealed class SalesSettingsLayoutAndMockTests
{
    [Fact]
    public void Test62_ShowProductOff_HidesMappedProduct()
    {
        var viewModel = ViewModel();
        viewModel.Apply(LiveSnapshot(product: Product()), AppSettings.CreateDefault());
        Assert.DoesNotContain("Product", viewModel.PrimaryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Test63_ShowProductOnWithoutMapping_HasNoEmptyPlaceholder()
    {
        var viewModel = ViewModel();
        viewModel.Apply(
            LiveSnapshot(product: null),
            AppSettings.CreateDefault() with { SalesShowProduct = true });
        Assert.DoesNotContain("Product", viewModel.PrimaryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Test64_SalesTracking_DefaultIsOn() =>
        Assert.True(AppSettings.CreateDefault().SalesTrackingEnabled);

    [Fact]
    public void Test65_ShowCurrentSeller_DefaultIsTrue() =>
        Assert.True(AppSettings.CreateDefault().SalesShowCurrentSeller);

    [Fact]
    public void Test66_ShowWaitingCount_DefaultIsTrue() =>
        Assert.True(AppSettings.CreateDefault().SalesShowWaitingCount);

    [Fact]
    public void Test67_ShowProduct_DefaultIsFalse() =>
        Assert.False(AppSettings.CreateDefault().SalesShowProduct);

    [Fact]
    public void Test68_ShowNextWaitingUser_DefaultIsFalse() =>
        Assert.False(AppSettings.CreateDefault().SalesShowNextWaitingUser);

    [Fact]
    public void Test69_SalesTrackingOff_HidesQueueUi()
    {
        var viewModel = ViewModel();
        viewModel.Apply(
            LiveSnapshot(),
            AppSettings.CreateDefault() with { SalesTrackingEnabled = false });
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public void Test70_OffToOn_AllowsSourceSnapshotRebuild()
    {
        var engine = SalesTestFactory.Engine();
        engine.SetTrackingEnabled(false);
        Assert.False(engine.ApplySourceSnapshot(new[] { SalesTestFactory.Message("1") }));
        engine.SetTrackingEnabled(true);
        Assert.True(engine.ApplySourceSnapshot(new[] { SalesTestFactory.Message("1") }));
        Assert.Single(engine.Current.ActiveItems);
    }

    [Fact]
    public void Test71_LayoutUsesOneLineWhenMeasuredContentFits()
    {
        var result = SalesQueueLayoutPolicy.Decide(new SalesQueueLayoutInput(
            500, 100, 80, 90, 90, SalesQueueVisibleFields.CurrentSeller |
            SalesQueueVisibleFields.WaitingCount));
        Assert.Equal(1, result.LineCount);
    }

    [Fact]
    public void Test72_LayoutUsesTwoLinesWhenRowsFitButOneLineDoesNot()
    {
        var result = SalesQueueLayoutPolicy.Decide(new SalesQueueLayoutInput(
            220, 100, 100, 100, 100,
            SalesQueueVisibleFields.CurrentSeller |
            SalesQueueVisibleFields.WaitingCount |
            SalesQueueVisibleFields.Product |
            SalesQueueVisibleFields.NextWaitingUser));
        Assert.Equal(2, result.LineCount);
    }

    [Fact]
    public void Test73_LayoutInformationPriority_KeepsCurrentSeller()
    {
        var result = SalesQueueLayoutPolicy.Decide(new SalesQueueLayoutInput(
            40, 100, 100, 100, 100, (SalesQueueVisibleFields)15));
        Assert.True(result.VisibleFields.HasFlag(SalesQueueVisibleFields.CurrentSeller));
    }

    [Fact]
    public void Test74_NextWaitingUser_IsDroppedBeforeProduct()
    {
        var result = SalesQueueLayoutPolicy.Decide(new SalesQueueLayoutInput(
            180, 100, 100, 50, 100, (SalesQueueVisibleFields)15));
        Assert.False(result.VisibleFields.HasFlag(SalesQueueVisibleFields.NextWaitingUser));
        Assert.True(result.VisibleFields.HasFlag(SalesQueueVisibleFields.Product));
    }

    [Fact]
    public void Test75_Product_IsDroppedBeforeWaitingCount()
    {
        var result = SalesQueueLayoutPolicy.Decide(new SalesQueueLayoutInput(
            160, 100, 50, 100, 0,
            SalesQueueVisibleFields.CurrentSeller |
            SalesQueueVisibleFields.WaitingCount |
            SalesQueueVisibleFields.Product));
        Assert.False(result.VisibleFields.HasFlag(SalesQueueVisibleFields.Product));
        Assert.True(result.VisibleFields.HasFlag(SalesQueueVisibleFields.WaitingCount));
    }

    [Fact]
    public void Test76_CurrentSeller_IsRetainedLongest()
    {
        var result = SalesQueueLayoutPolicy.Decide(new SalesQueueLayoutInput(
            1, 100, 100, 100, 100, (SalesQueueVisibleFields)15));
        Assert.Equal(SalesQueueVisibleFields.CurrentSeller, result.VisibleFields);
    }

    [Fact]
    public void Test77_QueueEmpty_UsesLocalizedPresentation()
    {
        var viewModel = ViewModel();
        viewModel.Apply(LiveSnapshot(empty: true), AppSettings.CreateDefault());
        Assert.Equal("No one is waiting", viewModel.PrimaryLine);
    }

    [Fact]
    public void Test78_OptionLabels_DoNotExposeObjectToString()
    {
        var localization = new ResourceLocalizationService("en", NullAppLogger.Instance);
        Assert.Equal("Sales tracking", localization["SettingsSalesTracking"]);
        Assert.Equal("Show waiting count", localization["SettingsSalesShowWaitingCount"]);
        Assert.DoesNotContain("{", localization["SettingsSalesShowWaitingCount"]);
    }

    [Fact]
    public void Test79_UnavailableMockSource_ReportsUnavailable()
    {
        using var source = new MockSalesReactionObservationSource();
        Assert.Equal(SalesObservationStatus.Unavailable, source.Status);
    }

    [Fact]
    public void Test80_PausedMockSource_PublishesUntrustedBatch()
    {
        using var source = new MockSalesReactionObservationSource();
        SalesObservationBatch? received = null;
        source.BatchAvailable += batch => received = batch;
        source.Start();
        source.Publish(SalesTestFactory.Batch(1, false, SalesObservationStatus.Paused));
        Assert.Equal(SalesObservationStatus.Paused, received!.SensorStatus);
        Assert.False(received.IsTrusted);
    }

    [Fact]
    public void Test81_MockTrustedSoldBatch_UsesProductionContract()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        using var source = ConnectedSource(engine);
        source.Publish(SalesTestFactory.Batch(
            1, true, SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.Sold, 1)));
        Assert.Equal(SaleDomainState.Sold, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void Test82_MockTrustedNotSoldBatch_ModelsReactionRemoval()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        SalesTestFactory.TrustSold(engine, "1", 1);
        using var source = ConnectedSource(engine);
        source.Publish(SalesTestFactory.Batch(
            2, true, SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.NotSold, 2)));
        Assert.Equal(SaleDomainState.Pending, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void Test83_MockGenerationTransition_RejectsStaleCompletion()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceCreate(SalesTestFactory.Message("1"));
        using var source = ConnectedSource(engine);
        source.Publish(SalesTestFactory.Batch(
            11, true, SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.Sold, 11)));
        source.Publish(SalesTestFactory.Batch(
            10, true, SalesObservationStatus.Live,
            SalesTestFactory.Observation("1", SaleReactionOutcome.NotSold, 10)));
        Assert.Equal(SaleDomainState.Sold, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void Test84_MockResyncSequence_IsExplicit()
    {
        using var source = new MockSalesReactionObservationSource();
        source.Start();
        source.RequestFullResync();
        Assert.Equal(1, source.ResyncRequestCount);
        Assert.Equal(SalesObservationStatus.Resyncing, source.Status);
    }

    [Fact]
    public void Test85_MockDisposal_StopsAndUnsubscribes()
    {
        var source = new MockSalesReactionObservationSource();
        source.Start();
        source.Dispose();
        Assert.False(source.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => source.Publish(
            SalesTestFactory.Batch(1, false, SalesObservationStatus.Paused)));
    }

    [Fact]
    public void Test86_SalesMasterOff_StopsMockWork()
    {
        var setup = Coordinator(AppSettings.CreateDefault());
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySettings(AppSettings.CreateDefault() with
        {
            SalesTrackingEnabled = false,
        });
        Assert.False(setup.Source.IsRunning);
        Assert.Equal(1, setup.Source.StopCount);
    }

    [Fact]
    public void Test87_SalesMasterOn_RebuildsAndRequestsResync()
    {
        var disabled = AppSettings.CreateDefault() with { SalesTrackingEnabled = false };
        var setup = Coordinator(disabled);
        using var coordinator = setup.Coordinator;
        coordinator.Start();
        coordinator.ApplySourceState(new DiscordMessageState(
            1,
            false,
            Array.Empty<NormalizedDiscordMessage>(),
            new[] { SalesTestFactory.Message("1") }));
        coordinator.ApplySettings(AppSettings.CreateDefault());
        Assert.True(setup.Source.IsRunning);
        Assert.Equal(1, setup.Source.ResyncRequestCount);
        Assert.Single(setup.Engine.Records);
    }

    [Fact]
    public void ProductionUnavailablePresentation_DoesNotClaimLiveQueue()
    {
        var viewModel = ViewModel();
        viewModel.Apply(
            LiveSnapshot() with
            {
                IsObservationSourceAvailable = false,
                ObservationStatus = SalesObservationStatus.Unavailable,
            },
            AppSettings.CreateDefault());
        Assert.Contains("Current", viewModel.PrimaryLine, StringComparison.Ordinal);
        Assert.Equal("Sales status sensor is unavailable", viewModel.SecondaryLine);
        Assert.NotEqual(SalesFeatureHealthState.Live, viewModel.HealthState);
    }

    private static SalesQueueViewModel ViewModel() => new(
        new ResourceLocalizationService("en", NullAppLogger.Instance));

    private static SaleProduct Product() => new("p", "Product", "100", "emoji");

    private static SalesQueueSnapshot LiveSnapshot(
        SaleProduct? product = null,
        bool empty = false)
    {
        var current = empty
            ? null
            : new SalesQueueEntry(
                "1", "guild", "author", SalesTestFactory.Epoch,
                "Seller", DiscordDisplayNameSource.RpcGuildNickname, true,
                product, SaleObservationTrust.Trusted);
        var active = current is null
            ? Array.Empty<SalesQueueEntry>()
            : new[] { current };
        return new SalesQueueSnapshot(
            1, true, active, current, active.Length, 0, null,
            false, false, false, true, SalesObservationStatus.Live,
            SalesTestFactory.Epoch);
    }

    private static MockSalesReactionObservationSource ConnectedSource(SalesStateEngine engine)
    {
        var source = new MockSalesReactionObservationSource();
        source.BatchAvailable += batch => engine.ApplyObservationBatch(batch);
        source.Start();
        return source;
    }

    private static CoordinatorSetup Coordinator(AppSettings settings)
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
            settings,
            Dispatcher.CurrentDispatcher);
        return new CoordinatorSetup(coordinator, source, engine);
    }

    private sealed record CoordinatorSetup(
        SalesPresentationCoordinator Coordinator,
        MockSalesReactionObservationSource Source,
        SalesStateEngine Engine);
}

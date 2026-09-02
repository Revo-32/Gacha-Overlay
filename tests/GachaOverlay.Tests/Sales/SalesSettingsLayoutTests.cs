using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Sales;

public sealed class SalesSettingsLayoutTests
{
    [Fact]
    public void ShowProductOff_HidesMappedProduct()
    {
        var viewModel = ViewModel();
        viewModel.Apply(LiveSnapshot(Product()), AppSettings.CreateDefault());
        Assert.DoesNotContain("Product", viewModel.PrimaryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowProductOnWithoutMapping_HasNoEmptyPlaceholder()
    {
        var viewModel = ViewModel();
        viewModel.Apply(
            LiveSnapshot(),
            AppSettings.CreateDefault() with { SalesShowProduct = true });
        Assert.DoesNotContain("Product", viewModel.PrimaryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void SalesDefaults_AreStable()
    {
        var settings = AppSettings.CreateDefault();
        Assert.True(settings.SalesTrackingEnabled);
        Assert.True(settings.SalesShowCurrentSeller);
        Assert.True(settings.SalesShowWaitingCount);
        Assert.False(settings.SalesShowProduct);
        Assert.False(settings.SalesShowNextWaitingUser);
    }

    [Fact]
    public void SalesTrackingOff_HidesQueueUi()
    {
        var viewModel = ViewModel();
        viewModel.Apply(
            LiveSnapshot(),
            AppSettings.CreateDefault() with { SalesTrackingEnabled = false });
        Assert.False(viewModel.IsVisible);
    }

    [Fact]
    public void OffToOn_AllowsAuthoritativeSnapshotRebuild()
    {
        var engine = SalesTestFactory.Engine();
        engine.SetTrackingEnabled(false);
        Assert.False(engine.ApplyAuthoritativeWindowSnapshot(
            new[] { SalesTestFactory.Message("1") }));
        engine.SetTrackingEnabled(true);
        Assert.True(engine.ApplyAuthoritativeWindowSnapshot(
            new[] { SalesTestFactory.Message("1") }));
        Assert.Single(engine.Current.ActiveItems);
    }

    [Fact]
    public void LayoutUsesOneLineWhenMeasuredContentFits()
    {
        var result = SalesQueueLayoutPolicy.Decide(new SalesQueueLayoutInput(
            500, 100, 80, 90, 90,
            SalesQueueVisibleFields.CurrentSeller | SalesQueueVisibleFields.WaitingCount));
        Assert.Equal(1, result.LineCount);
    }

    [Fact]
    public void LayoutUsesTwoLinesWhenRowsFitButOneLineDoesNot()
    {
        var result = SalesQueueLayoutPolicy.Decide(new SalesQueueLayoutInput(
            220, 100, 100, 100, 100, (SalesQueueVisibleFields)15));
        Assert.Equal(2, result.LineCount);
    }

    [Fact]
    public void LayoutInformationPriority_KeepsCurrentSeller()
    {
        var result = SalesQueueLayoutPolicy.Decide(new SalesQueueLayoutInput(
            1, 100, 100, 100, 100, (SalesQueueVisibleFields)15));
        Assert.Equal(SalesQueueVisibleFields.CurrentSeller, result.VisibleFields);
    }

    [Fact]
    public void QueueEmpty_UsesLocalizedPresentation()
    {
        var viewModel = ViewModel();
        viewModel.Apply(LiveSnapshot(empty: true), AppSettings.CreateDefault());
        Assert.Equal("No one is waiting", viewModel.PrimaryLine);
    }

    [Fact]
    public void OptionLabels_DoNotExposeObjectToString()
    {
        var localization = new ResourceLocalizationService("en", NullAppLogger.Instance);
        Assert.Equal("Sales tracking", localization["SettingsSalesTracking"]);
        Assert.DoesNotContain("{", localization["SettingsSalesShowWaitingCount"]);
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
                "Seller", DiscordDisplayNameSource.GuildNickname, true,
                product, SaleObservationTrust.Trusted);
        var active = current is null
            ? Array.Empty<SalesQueueEntry>()
            : new[] { current };
        return new SalesQueueSnapshot(
            1, true, active, current, active.Length, 0, null,
            false, false, false, true, SalesObservationStatus.Live,
            SalesTestFactory.Epoch);
    }
}

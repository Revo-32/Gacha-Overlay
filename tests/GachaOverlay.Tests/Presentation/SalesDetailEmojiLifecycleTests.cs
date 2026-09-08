using System.Windows.Media;
using System.Windows.Media.Imaging;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Sales;
using GachaOverlay.Tests.Sales;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Logging;
using System.Windows.Threading;

namespace GachaOverlay.Tests.Presentation;

[Collection(WpfApplicationCollection.Name)]
public sealed class SalesDetailEmojiLifecycleTests
{
    [Fact]
    public void NewRowsRestartPendingLoadAndRejectStaleResultsAfterEditOrDelete()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var engine = SalesTestFactory.Engine(EmbeddedSalesProductCatalogLoader.Load());
            var message = SalesTestFactory.Message("100", content: "판매 <:SELL_SP:1439136641330708581>");
            engine.ApplySourceCreate(message);
            var settings = AppSettings.CreateDefault();
            var localization = new ResourceLocalizationService("ko");
            var vm = new SalesQueueViewModel(localization);
            var requests = new List<TaskCompletionSource<CachedMediaAsset?>>();
            using var coordinator = new SalesPresentationCoordinator(engine, vm, localization, NullAppLogger.Instance,
                settings, Dispatcher.CurrentDispatcher, detailEmojiLoader: (_, _, _) =>
                {
                    var source = new TaskCompletionSource<CachedMediaAsset?>();
                    requests.Add(source);
                    return source.Task;
                });
            vm.Apply(engine.Current, settings, SalesFeatureHealthSnapshot.Disabled, "판매", SalesQueueChangeContext.None);
            Assert.Single(requests);
            vm.UpdateAvailableWidth(700);
            Assert.Equal(2, requests.Count); // No new network Sales snapshot is required.
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 255, 0, 255 }, 4);
            bitmap.Freeze();
            var asset = new CachedMediaAsset(bitmap, null, 1);
            requests[0].SetResult(asset);
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(20));
            Assert.Null(Emoji(vm).Image);
            requests[1].SetResult(asset);
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(20));
            Assert.Same(bitmap, Emoji(vm).Image);
            vm.UpdateHudContext(true, false, true, false);
            Assert.Equal(2, requests.Count); // A retained static image needs no new load.
            Assert.Same(bitmap, Emoji(vm).Image);
            engine.ApplySourceUpdate(message with { Content = "수정 <:other:223456789012345678>" });
            vm.Apply(engine.Current, settings, SalesFeatureHealthSnapshot.Disabled, "판매", SalesQueueChangeContext.None);
            Assert.Equal(3, requests.Count);
            Assert.Null(Emoji(vm).Image);
            Assert.Equal("223456789012345678", Emoji(vm).Identity);
            engine.ApplySourceDelete("100");
            vm.Apply(engine.Current, settings, SalesFeatureHealthSnapshot.Disabled, "판매", SalesQueueChangeContext.None);
            requests[2].SetResult(asset);
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(20));
            Assert.Empty(vm.DetailItems);
            coordinator.Dispose();
            engine.ApplySourceCreate(message);
            vm.Apply(engine.Current, settings, SalesFeatureHealthSnapshot.Disabled, "판매", SalesQueueChangeContext.None);
            Assert.Equal(3, requests.Count); // Event subscription was released.
        });
    }

    private static ChatTokenViewModel Emoji(SalesQueueViewModel vm) =>
        Assert.Single(Assert.Single(vm.DetailItems).DetailTokens.Where(t => t.Kind == ChatTokenKind.CustomEmoji));

    [Fact]
    public void LoadedEmojiSurvivesResizeLockAndLocalizationWithoutWaitingForSalesSnapshot()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var engine = SalesTestFactory.Engine(EmbeddedSalesProductCatalogLoader.Load());
            engine.ApplySourceCreate(SalesTestFactory.Message("100", content: "판매 <:SELL_SP:1439136641330708581>"));
            var vm = new SalesQueueViewModel(new ResourceLocalizationService("ko"));
            vm.Apply(engine.Current, AppSettings.CreateDefault(), SalesFeatureHealthSnapshot.Disabled, "판매", SalesQueueChangeContext.None);
            var token = Assert.Single(Assert.Single(vm.DetailItems).DetailTokens.Where(t => t.Kind == ChatTokenKind.CustomEmoji));
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 255, 0, 255 }, 4);
            bitmap.Freeze();
            token.Image = bitmap;
            foreach (var change in new Action[] { () => vm.UpdateAvailableWidth(700),
                () => vm.UpdateHudContext(true, false, true, false), () => vm.UpdateHudContext(true, false, true, true), vm.RefreshLocalization })
            {
                change();
                var refreshed = Assert.Single(Assert.Single(vm.DetailItems).DetailTokens.Where(t => t.Kind == ChatTokenKind.CustomEmoji));
                Assert.Same(bitmap, refreshed.Image);
                Assert.Same(token, refreshed);
            }
        });
    }
}

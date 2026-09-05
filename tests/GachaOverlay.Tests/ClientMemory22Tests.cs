using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests;

[Collection(GachaOverlay.Tests.Presentation.WpfApplicationCollection.Name)]
public sealed class ClientMemory22Tests
{
    [Fact]
    public void EnlargedPreviewKeepsClickTimeFrame_WhenPlaybackSurfaceAdvances()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var surface = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Pbgra32, null);
            surface.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 0, 0, 255, 255 }, 4, 0);
            var snapshot = Assert.IsAssignableFrom<BitmapSource>(ChatPresentationCoordinator.SnapshotForPreview(surface));
            Assert.True(snapshot.IsFrozen);
            surface.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 255, 0, 0, 255 }, 4, 0);
            var pixels = new byte[4];
            snapshot.CopyPixels(pixels, 4, 0);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels);
            Assert.Same(snapshot, ChatPresentationCoordinator.SnapshotForPreview(snapshot));
        });
    }

    [Fact]
    public void DensityChangesKeepOnlySelectedTree_AndKeepMessageIdentity()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var settings = AppSettings.CreateDefault();
            var typography = new ChatTypographyResolver(NullAppLogger.Instance).Resolve(settings.ChatFontPreset);
            using var vm = new ChatMessageViewModel(new ChatMessagePresentation("message", "작성자", DateTimeOffset.UnixEpoch,
                [new ChatToken(ChatTokenKind.Text, "한글 Test")], "한글 Test", [], [], 0, false, 1, 1), new ResourceLocalizationService(), _ => { });
            var view = new ChatMessageView { DataContext = vm };
            var window = new Window
            {
                Content = view,
                Width = 420,
                Height = 300,
                Left = -12000,
                Top = -12000,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            try
            {
                window.Show();
                for (var cycle = 0; cycle < 4; cycle++)
                    foreach (var mode in new[] { "Balanced", "Compact", "UltraCompact" })
                    {
                        vm.ApplySettings(settings with { ChatLayoutMode = mode == "Compact" ? ChatLayoutMode.Compact : ChatLayoutMode.Balanced },
                            mode == "UltraCompact" ? ChatResponsiveLevel.UltraCompact : ChatResponsiveLevel.Full, typography);
                        window.UpdateLayout();
                        MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(20));
                        var names = new[] { "BalancedNickname", "CompactNickname", "UltraCompactNickname" };
                        foreach (var name in names)
                        {
                            var element = GachaOverlay.Tests.TestSupport.VisualLookup.Find(view, name);
                            if (name == mode + "Nickname") Assert.NotNull(element);
                            else Assert.Null(element);
                        }
                        Assert.Same(vm, view.DataContext);
                        Assert.Equal("message", vm.MessageId);
                    }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void MutableEmojiPixelsChangeWithoutTextRelayout()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var surface = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Pbgra32, null);
            var token = new ChatTokenViewModel(new ChatToken(ChatTokenKind.CustomEmoji, ":test:")) { Image = surface };
            var view = Text(string.Empty);
            view.Tokens = new[] { token };
            surface.WritePixels(new Int32Rect(0, 0, 2, 2), new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, 8, 0);
            var before = Render(view);
            var builds = view.LayoutBuildCount;
            surface.WritePixels(new Int32Rect(0, 0, 2, 2), new byte[] { 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255 }, 8, 0);
            token.Image = surface;
            Assert.NotEqual(before, Render(view));
            Assert.Equal(builds, view.LayoutBuildCount);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        });
    }

    [Fact]
    public void ReusablePresentationMatchesImmutableSnapshots_AndDoesNotUpscale()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var bytes = MediaLatencyProfile211Tests.Fixture(96, 10);
            using var worker = new DiscordMediaAssetService.FrameDecoder(bytes, 384);
            var surface = new WriteableBitmap(96, 96, 96, 96, PixelFormats.Pbgra32, null);
            foreach (var frame in new[] { 0, 1, 8, 3, 11, 0 })
            {
                var pixels = worker.DecodePixels(frame);
                surface.WritePixels(new Int32Rect(0, 0, pixels.Width, pixels.Height), pixels.Address, pixels.Bytes, pixels.Stride);
                using var reference = new DiscordMediaAssetService.FrameDecoder(bytes, 384);
                var expected = new byte[pixels.Bytes];
                var actual = new byte[pixels.Bytes];
                reference.Decode(frame).Image.CopyPixels(expected, pixels.Stride, 0);
                surface.CopyPixels(actual, pixels.Stride, 0);
                Assert.Equal(expected, actual);
            }
        });
    }

    [Fact]
    public void PlayerRetainsOneSurface_AndDisposalPreventsFurtherPresentation()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var metrics = new RuntimeMetricsCollector();
            using var scheduler = new MediaAnimationScheduler(Dispatcher.CurrentDispatcher, metrics, NullAppLogger.Instance);
            BitmapSource? first = null;
            var callbacks = 0;
            var player = scheduler.Register(MediaLatencyProfile211Tests.Fixture(96, 10), 384, image =>
            {
                if (first is null) first = image;
                else Assert.Same(first, image);
                callbacks++;
            });
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromSeconds(1));
            Assert.True(callbacks >= 3);
            player.Dispose();
            var count = callbacks;
            first = null;
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(250));
            Assert.Equal(count, callbacks);
            Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationActivePlayers));
            Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationDecoderCount));
            Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationSchedulerActive));
            Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault("media.animation.presentation_surfaces"));
        });
    }

    [Fact]
    public void FormatterIsDispatcherAndModeScoped_AndUnloadingOneLayoutKeepsOthersValid()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Assert.Same(DispatcherTextFormatters.Get(dispatcher, TextFormattingMode.Display),
                DispatcherTextFormatters.Get(dispatcher, TextFormattingMode.Display));
            Assert.NotSame(DispatcherTextFormatters.Get(dispatcher, TextFormattingMode.Display),
                DispatcherTextFormatters.Get(dispatcher, TextFormattingMode.Ideal));
            var first = Text("한글 One");
            var second = Text("한글 Two");
            var before = Render(second);
            _ = Render(first);
            first.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            second.InvalidateVisual();
            Assert.Equal(before, Render(second));
            second.Text = "변경 Update";
            Assert.NotEqual(before, Render(second));
            second.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            dispatcher.InvokeShutdown();
        });
    }

    private static CrispOutlinedText Text(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontFamily = new FontFamily("Segoe UI"),
        Foreground = Brushes.White,
        OutlineBrush = Brushes.Black,
        Width = 400,
        Height = 90
    };

    private static byte[] Render(FrameworkElement view)
    {
        view.Measure(new System.Windows.Size(400, 90));
        view.Arrange(new Rect(0, 0, 400, 90));
        view.UpdateLayout();
        var image = new RenderTargetBitmap(400, 90, 96, 96, PixelFormats.Pbgra32);
        image.Render(view);
        var pixels = new byte[400 * 90 * 4];
        image.CopyPixels(pixels, 400 * 4, 0);
        return pixels;
    }
}

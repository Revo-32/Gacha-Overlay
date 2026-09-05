using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests;

[Collection(Presentation.WpfApplicationCollection.Name)]
public sealed class ClientMemoryBFinalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmojiAnimationTargetsCurrentTokens_AfterSettingsAndVisibility(bool forwarded)
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var settings = AppSettings.CreateDefault() with { ChatCustomEmojiEnabled = false };
            var chat = new ChatViewModel();
            var metrics = new RuntimeMetricsCollector();
            var localization = new ResourceLocalizationService();
            using var coordinator = new ChatPresentationCoordinator(chat,
                new DiscordMediaAssetService(NullAppLogger.Instance, metrics), localization,
                NullAppLogger.Instance, settings, new ChatTypographyResolver(NullAppLogger.Instance));
            var message = Message() with
            {
                Content = "<a:fixture:123456789012345678>",
                CustomEmojis = [new DiscordCustomEmoji("123456789012345678", "fixture", true)]
            };
            coordinator.ApplyState(new DiscordMessageState(1, false, [message], []), null);
            var item = chat.Messages[0];
            var bytes = MediaLatencyProfile211Tests.Fixture(96, 10);
            var asset = new CachedMediaAsset(DiscordMediaAssetService.DecodeSkiaFrame(bytes, 96, 0).Image, bytes, 96);
            var forward = new ChatForwardMessageViewModel(new ChatForwardPresentation("fixture", [], [], 0)
            {
                Tokens = [new ChatToken(ChatTokenKind.CustomEmoji, ":fixture:", "123456789012345678", IsAnimatedEmoji: true)]
            }, localization);
            item.ForwardedMessages.Add(forward);
            foreach (var token in item.Tokens.Concat(forward.Tokens)) token.Image = asset.Preview;
            var tokens = forwarded ? forward.Tokens : item.Tokens;
            var initial = tokens[0];
            var admit = typeof(ChatPresentationCoordinator).GetMethod("StartEmojiAnimation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object[] arguments = [item, item.EnrichmentIdentity, asset, "123456789012345678", forwarded ? forward : item];
            admit.Invoke(coordinator, arguments);
            coordinator.ApplySettings(settings with { ChatCustomEmojiEnabled = true });
            if (!forwarded) Assert.NotSame(initial, item.Tokens[0]);
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(450));
            Assert.IsType<WriteableBitmap>(tokens[0].Image);
            var previous = tokens[0].Image;
            coordinator.SetAnimationsVisible(false);
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(150));
            coordinator.SetAnimationsVisible(true);
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(450));
            Assert.NotSame(previous, tokens[0].Image);
            admit.Invoke(coordinator, arguments);
            Assert.Equal(1, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationActivePlayers));
            coordinator.ApplyState(new DiscordMessageState(1, false, [], []), null);
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(150));
            Assert.Equal(0, coordinator.AnimationBindingCount);
            Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationDecoderCount));
        });
    }

    [Fact]
    public void ProfileRepeatedTextPaint()
    {
        var path = Environment.GetEnvironmentVariable("LSO_CLIENT_TEXT_PROFILE");
        if (string.IsNullOrWhiteSpace(path)) return;
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var controls = Enumerable.Range(0, 20).Select(_ => CreateText()).ToArray();
            var draw = typeof(CrispOutlinedText).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var text in controls) Draw(text);
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocation = GC.GetTotalAllocatedBytes();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var repeat = 0; repeat < 60; repeat++)
                foreach (var text in controls)
                {
                    using var context = new DrawingVisual().RenderOpen();
                    draw.Invoke(text, [context]);
                }
            var result = new
            {
                boundary = "Synthetic direct paint only; fixed 1200 redraws, no network/GC/bitmap capture in timed region. Not app-wide CPU.",
                elapsedMs = watch.Elapsed.TotalMilliseconds,
                cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocation,
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(result));
            foreach (var text in controls) text.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        });
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("settings")]
    [InlineData("responsive")]
    [InlineData("offon")]
    public void CachedAnimationSurvivesLifecycleWithoutDuplicatePlayers(string transition)
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var metrics = new RuntimeMetricsCollector();
            var chat = new ChatViewModel();
            var settings = AppSettings.CreateDefault();
            using var coordinator = new ChatPresentationCoordinator(chat,
                new DiscordMediaAssetService(NullAppLogger.Instance, metrics), new ResourceLocalizationService(),
                NullAppLogger.Instance, settings, new ChatTypographyResolver(NullAppLogger.Instance));
            coordinator.ApplyState(new DiscordMessageState(1, false, [Message()], []), null);
            var item = chat.Messages[0];
            var bytes = MediaLatencyProfile211Tests.Fixture(96, 10);
            var asset = new CachedMediaAsset(DiscordMediaAssetService.DecodeSkiaFrame(bytes, 96, 0).Image, bytes, 96);
            item.Thumbnail = asset.Preview;
            if (transition == "hidden") coordinator.SetAnimationsVisible(false);
            // Synthetic post-download completion only: no HTTP, ApplicationHost or credentials.
            var method = typeof(ChatPresentationCoordinator).GetMethod("StartAnimation", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var arguments = new List<object> { item, item.EnrichmentIdentity, asset, (Action<ImageSource>)(frame => item.Thumbnail = frame) };
            if (method.GetParameters().Length == 6)
            {
                arguments.Add(item);
                arguments.Add(Enum.Parse(method.GetParameters()[5].ParameterType, "Thumbnail"));
            }
            method.Invoke(coordinator, arguments.ToArray());
            switch (transition)
            {
                case "hidden": coordinator.SetAnimationsVisible(true); break;
                case "settings": coordinator.ApplySettings(settings with { ChatFontSizePoints = 20 }); break;
                case "responsive":
                    coordinator.ApplyResponsiveLevel(GachaOverlay.Core.Chat.ChatResponsiveLevel.UltraCompact);
                    coordinator.ApplyResponsiveLevel(GachaOverlay.Core.Chat.ChatResponsiveLevel.Full);
                    break;
                case "offon":
                    coordinator.ApplySettings(settings with { AnimatedMediaPlaybackEnabled = false });
                    Assert.Equal(0, Gauge(RuntimeMetricNames.MediaAnimationActivePlayers));
                    coordinator.ApplySettings(settings);
                    break;
            }
            var decoded = Counter(RuntimeMetricNames.MediaAnimationFrameDecoded);
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(450));
            Assert.Equal(1, Gauge(RuntimeMetricNames.MediaAnimationActivePlayers));
            Assert.True(Counter(RuntimeMetricNames.MediaAnimationFrameDecoded) > decoded);
            method.Invoke(coordinator, arguments.ToArray());
            Assert.Equal(1, Gauge(RuntimeMetricNames.MediaAnimationActivePlayers));
            coordinator.SetAnimationsVisible(false);
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(150));
            Assert.Equal(0, Gauge(RuntimeMetricNames.MediaAnimationActivePlayers));
            Assert.Equal(0, Gauge(RuntimeMetricNames.MediaAnimationDecoderCount));
            coordinator.ApplyState(new DiscordMessageState(1, false, [], []), null);
            coordinator.SetAnimationsVisible(true);
            Assert.Equal(0, coordinator.AnimationBindingCount);
            Assert.Equal(0, Gauge(RuntimeMetricNames.MediaAnimationActivePlayers));
            double Gauge(string key) => metrics.Snapshot().Gauges.GetValueOrDefault(key);
            long Counter(string key) => metrics.Snapshot().Counters.GetValueOrDefault(key);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentPreviewIsClearedWhenItsMessageChanges(bool remove)
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var chat = new ChatViewModel();
            using var coordinator = new ChatPresentationCoordinator(chat,
                new DiscordMediaAssetService(NullAppLogger.Instance), new ResourceLocalizationService(),
                NullAppLogger.Instance, AppSettings.CreateDefault(), new ChatTypographyResolver(NullAppLogger.Instance));
            coordinator.ApplyState(new DiscordMessageState(1, false, [Message()], []), null);
            var item = chat.Messages[0];
            item.Thumbnail = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Pbgra32, null);
            // CanEnlarge is a presentation policy, bypassed solely for this ownership regression.
            typeof(ChatMessageViewModel).GetProperty("CanEnlarge")!.SetValue(item, true);
            item.PreviewCommand.Execute(null);
            Assert.NotNull(chat.PreviewImage);
            Assert.NotSame(item.Thumbnail, chat.PreviewImage);
            coordinator.ApplyState(new DiscordMessageState(1, false,
                remove ? [] : [Message() with { Content = "edited", EditedAt = DateTimeOffset.UnixEpoch.AddMinutes(1) }], []), null);
            Assert.Null(chat.PreviewImage);
        });
    }

    private static NormalizedDiscordMessage Message() => new("test", "channel", "author", "Tester", "Tester",
        "Synthetic 한글", DateTimeOffset.UnixEpoch, null, [], [], [], []);

    private static CrispOutlinedText CreateText() => new()
    {
        Text = "한글 outline 日本語 and English",
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 22,
        Foreground = Brushes.White,
        OutlineBrush = Brushes.Black,
        Width = 400,
        Height = 90
    };

    private static byte[] Draw(CrispOutlinedText text)
    {
        text.Measure(new System.Windows.Size(text.Width, 90));
        text.Arrange(new Rect(0, 0, text.Width, 90));
        text.UpdateLayout();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            typeof(CrispOutlinedText).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(text, [context]);
        var bitmap = new RenderTargetBitmap(400, 90, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[400 * 90 * 4];
        bitmap.CopyPixels(pixels, 1600, 0);
        return pixels;
    }
}

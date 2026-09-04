using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;

namespace GachaOverlay.Tests.Presentation;

[Collection(WpfApplicationCollection.Name)]
public sealed class M753WpfLayoutTests
{
    [Theory]
    [InlineData(ChatLayoutMode.Compact, ChatResponsiveLevel.Full)]
    [InlineData(ChatLayoutMode.Balanced, ChatResponsiveLevel.Full)]
    [InlineData(ChatLayoutMode.Balanced, ChatResponsiveLevel.Reduced)]
    public void AdjacentTextMessages_MinimumSpacing_HasAtMostTwoDipAdditionalGap(
        ChatLayoutMode mode,
        ChatResponsiveLevel responsive)
    {
        RunSta(() =>
        {
            var settings = AppSettings.CreateDefault() with
            {
                ChatLayoutMode = mode,
                ChatLineHeightMultiplier = 1,
                ChatMessageSpacing = -2,
                ChatShowImages = true,
            };
            using var firstModel = Model("1", settings, responsive);
            using var secondModel = Model("2", settings, responsive);
            var first = new ChatMessageView { DataContext = firstModel };
            var second = new ChatMessageView { DataContext = secondModel };
            var panel = new StackPanel();
            panel.Children.Add(first);
            panel.Children.Add(second);
            var window = ShowForLayout(panel, 420, 300);

            var firstBody = RenderedBody(first);
            var secondFirstLine = FirstRenderedLine(second);
            var firstBounds = BoundsIn(firstBody, panel);
            var secondBounds = BoundsIn(secondFirstLine, panel);
            var gap = secondBounds.Top - firstBounds.Bottom;
            Assert.InRange(gap, 0, 2.01);
            Assert.All(Descendants<ChatMediaView>(panel), media =>
                Assert.Equal(Visibility.Collapsed, media.Visibility));
            window.Close();
        });
    }

    [Fact]
    public void TextOnlyMessage_HasNoHiddenMediaRowOrFixedMinimumHeight()
    {
        RunSta(() =>
        {
            var settings = AppSettings.CreateDefault() with
            {
                ChatLayoutMode = ChatLayoutMode.Compact,
                ChatLineHeightMultiplier = 1,
                ChatMessageSpacing = -2,
            };
            using var model = Model("1", settings, ChatResponsiveLevel.Full);
            var view = new ChatMessageView { DataContext = model };
            var window = ShowForLayout(view, 420, 180);

            Assert.Equal(0, view.MinHeight);
            Assert.False(model.HasVisibleMedia);
            Assert.All(Descendants<ChatMediaView>(view), media =>
                Assert.Equal(Visibility.Collapsed, media.Visibility));
            window.Close();
        });
    }

    [Fact]
    public void MediaMessage_UsesSeparateSmallBottomGap()
    {
        var source = File.ReadAllText(Source("ChatMediaView.xaml"));

        Assert.Contains("Margin=\"0,3,0,4\"", source);
        Assert.Contains("HasVisibleMedia", File.ReadAllText(Source("ChatMessageView.xaml")));
    }

    [Fact]
    public void NicknameAndBody_UseTheSameUnifiedTextRenderer()
    {
        var source = File.ReadAllText(Source("ChatMessageView.xaml"));

        Assert.Equal(10, source.Split("<local:CrispOutlinedText", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("CrispTextOutline", source);
        Assert.DoesNotContain("ChatRichTextBlock", source);
        Assert.DoesNotContain("Padding=\"3,1\"", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, ChatResponsiveLevel.Full, 96)]
    [InlineData(1.5, ChatResponsiveLevel.Full, 144)]
    [InlineData(6, ChatResponsiveLevel.Reduced, 144)]
    [InlineData(10, ChatResponsiveLevel.Full, 192)]
    [InlineData(10, ChatResponsiveLevel.Reduced, 192)]
    [InlineData(10, ChatResponsiveLevel.UltraCompact, 192)]
    public void OutlinePaintBounds_RenderWithoutEdgeClippingAtHighDpi(
        double thickness,
        ChatResponsiveLevel responsive,
        double dpi)
    {
        RunSta(() =>
        {
            var settings = AppSettings.CreateDefault() with
            {
                ChatNicknameOutlineThickness = thickness,
                ChatMessageOutlineThickness = thickness,
                ChatMessageSpacing = 1.25,
            };
            using var model = Model("outline", settings, responsive);
            var view = new ChatMessageView { DataContext = model };
            var window = ShowForLayout(view, 520, 240);
            var surface = Assert.IsType<Border>(view.FindName("MessageSurface"));
            var first = FirstRenderedLine(view);
            var body = RenderedBody(view);
            var firstBounds = BoundsIn(first, surface);
            var bodyBounds = BoundsIn(body, surface);

            Assert.True(firstBounds.Top >= -0.1);
            Assert.True(bodyBounds.Left >= -0.1);
            Assert.True(bodyBounds.Right <= surface.ActualWidth + 0.1);
            Assert.True(bodyBounds.Bottom <= surface.ActualHeight + 0.1);

            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(view.ActualWidth * dpi / 96)),
                Math.Max(1, (int)Math.Ceiling(view.ActualHeight * dpi / 96)),
                dpi,
                dpi,
                PixelFormats.Pbgra32);
            bitmap.Render(view);
            Assert.True(bitmap.PixelWidth > 0);
            Assert.True(bitmap.PixelHeight > 0);
            window.Close();
        });
    }

    [Theory]
    [InlineData((int)SalesPreviewScenario.Error)]
    [InlineData((int)SalesPreviewScenario.Paused)]
    [InlineData((int)SalesPreviewScenario.Degraded)]
    [InlineData((int)SalesPreviewScenario.Disconnected)]
    [InlineData((int)SalesPreviewScenario.Resyncing)]
    public void EveryStatusIcon_IsFullyContainedBySafeHost(int scenarioValue)
    {
        RunSta(() =>
        {
            using var preview = new SalesPreviewViewModel(
                new ResourceLocalizationService(),
                AppSettings.CreateDefault());
            preview.SelectedScenario = (SalesPreviewScenario)scenarioValue;
            var host = new SalesStatusIconHost { DataContext = preview.Sales };
            var window = ShowForLayout(host, 28, 28);

            Assert.Equal(28, host.ActualWidth);
            Assert.Equal(28, host.ActualHeight);
            foreach (var element in Descendants<FrameworkElement>(host)
                         .Where(element => element.RenderSize.Width > 0 && element.RenderSize.Height > 0))
            {
                var bounds = BoundsIn(element, host);
                Assert.True(bounds.Left >= -0.1, $"{element.GetType().Name} left={bounds.Left}");
                Assert.True(bounds.Top >= -0.1, $"{element.GetType().Name} top={bounds.Top}");
                Assert.True(bounds.Right <= 28.1, $"{element.GetType().Name} right={bounds.Right}");
                Assert.True(bounds.Bottom <= 28.1, $"{element.GetType().Name} bottom={bounds.Bottom}");
                Assert.True(element.Margin.Left >= 0 && element.Margin.Top >= 0 &&
                    element.Margin.Right >= 0 && element.Margin.Bottom >= 0);
            }

            window.Close();
        });
    }

    [Fact]
    public void PreviewAndProductionShareSalesQueueStatusHost()
    {
        var queue = File.ReadAllText(Source("SalesQueueView.xaml"));
        var preview = File.ReadAllText(Source("SalesPreviewWindow.xaml"));

        Assert.Equal(1, queue.Split("<local:SalesStatusIconHost", StringSplitOptions.None).Length - 1);
        Assert.Contains("<local:SalesQueueView", preview);
        Assert.DoesNotContain("Margin=\"-", File.ReadAllText(Source("SalesStatusIconHost.xaml")), StringComparison.Ordinal);
    }

    private static ChatMessageViewModel Model(
        string id,
        AppSettings settings,
        ChatResponsiveLevel responsive)
    {
        var model = new ChatMessageViewModel(
            new ChatMessagePresentation(
                id,
                "Seller",
                DateTimeOffset.UtcNow,
                new[] { new ChatToken(ChatTokenKind.Text, "one line") },
                "one line",
                Array.Empty<ChatMediaCandidate>(),
                Array.Empty<ChatStickerPresentation>(),
                0,
                false,
                1,
                1),
            new ResourceLocalizationService(),
            _ => { });
        model.ApplySettings(settings, responsive, Typography());
        return model;
    }

    private static CrispOutlinedText RenderedBody(ChatMessageView view)
    {
        var model = Assert.IsType<ChatMessageViewModel>(view.DataContext);
        var name = model.IsUltraCompact
            ? "UltraCompactBody"
            : model.IsCompact
                ? "CompactBody"
                : "BalancedBody";
        return Assert.IsType<CrispOutlinedText>(view.FindName(name));
    }

    private static FrameworkElement FirstRenderedLine(ChatMessageView view)
    {
        var model = Assert.IsType<ChatMessageViewModel>(view.DataContext);
        if (!model.IsBalanced)
        {
            return RenderedBody(view);
        }

        return Assert.IsType<CrispOutlinedText>(view.FindName("BalancedNickname"));
    }

    private static Rect BoundsIn(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(), element.RenderSize));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static ResolvedChatTypography Typography()
    {
        var role = new ResolvedChatFontRole(
            new FontFamily("Segoe UI"),
            FontWeights.Normal,
            "Segoe UI",
            ChatFontResolutionSource.System,
            false,
            null);
        return new ResolvedChatTypography(ChatFontPreset.Kimm, "Test", role, role);
    }

    private static Window ShowForLayout(FrameworkElement content, double width, double height)
    {
        var window = new Window
        {
            Content = content,
            Width = width,
            Height = height,
            Left = -10000,
            Top = -10000,
            Opacity = 0,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static string Source(string fileName) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "..",
        "src",
        "GachaOverlay.App",
        "Presentation",
        fileName));
}

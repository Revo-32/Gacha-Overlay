using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Providers;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Themes;
using GachaOverlay.Infrastructure.Localization;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests.Presentation;

[Collection(WpfApplicationCollection.Name)]
public sealed class M10WpfPolishTests
{
    [Fact]
    public void CompletionButtonStaysOnMainBarAndDetailsContainNoButtons() => RunSta(() =>
    {
        var vm = new SalesQueueViewModel(new ResourceLocalizationService("ko"));
        vm.ConfigureStatusAction((_, _, _) => Task.FromResult<SalesStatusActionResponse?>(null));
        vm.ApplyRemoteStatusContext(new Dictionary<string, SalesCompletionObservation>(), EffectiveSalesSource.RemotePrimary);
        var view = new SalesQueueView { DataContext = vm };
        using var host = Host(view, 520, 330);
        vm.Apply(Queue(), AppSettings.CreateDefault(),
            SalesFeatureHealthEvaluator.Evaluate(new(true, RemoteSalesPresentationPhase.Live, true,
                SalesCoverageState.Complete, DateTimeOffset.UtcNow, 3, 3)), "#sales", SalesQueueChangeContext.None);
        host.Window.UpdateLayout();

        Assert.False(vm.IsQueueDetailExpanded);
        Assert.True(view.CompleteOwnSaleButton.IsVisible);
        Assert.True(view.CompleteOwnSaleButton.IsEnabled);
        Assert.Equal("판매완료", view.CompleteOwnSaleButton.Content);
        Assert.Same(vm.OwnCompletionItem!.SetCompletedCommand, view.CompleteOwnSaleButton.Command);
        vm.ToggleDetailCommand.Execute(null);
        host.Window.UpdateLayout();
        Assert.Empty(Descendants<Button>(view.DetailRows));
        Assert.True(view.CompleteOwnSaleButton.IsVisible);
        vm.UpdateHudContext(true, false, true, false);
        host.Window.UpdateLayout();
        Assert.True(view.CompleteOwnSaleButton.IsVisible);
        Assert.False(view.CompleteOwnSaleButton.IsEnabled);
        Assert.True(vm.IsQueueDetailExpanded);
        vm.UpdateHudContext(true, false, true, true);
        host.Window.UpdateLayout();
        Assert.True(view.CompleteOwnSaleButton.IsEnabled);
        vm.ToggleDetailCommand.Execute(null);
        host.Window.UpdateLayout();
        Assert.True(view.CompleteOwnSaleButton.IsVisible);
        view.CompleteOwnSaleButton.Command.Execute(null);
        host.Window.UpdateLayout();
        Assert.True(view.CompletionFeedback.IsVisible);
        Assert.False(vm.IsQueueDetailExpanded);
    });

    [Fact]
    public void EmptyViewportWheelReadsHistoryAndNewMessagesPreserveAnchor() => RunSta(() =>
    {
        var vm = new ChatViewModel { IsHudUnlocked = true };
        var view = new ChatView { DataContext = vm };
        // Fixed-height rows isolate scroll mechanics from separately-tested media/typography.
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(FrameworkElement.HeightProperty, 45d);
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ChatMessageViewModel.PlainText)));
        template.VisualTree = text;
        view.MessageItems.ItemTemplate = template;
        using var host = Host(view, 440, 300);
        var localization = new ResourceLocalizationService("ko");
        var owned = new List<ChatMessageViewModel>();
        ChatMessageViewModel Item(int id)
        {
            var item = new ChatMessageViewModel(new(id.ToString(), "Tester", null, Array.Empty<ChatToken>(),
                "test-" + id, Array.Empty<ChatMediaCandidate>(), Array.Empty<ChatStickerPresentation>(), 0, false, 1, 1),
                localization, _ => { });
            owned.Add(item);
            return item;
        }
        try
        {
            for (var i = 1; i <= 20; i++) vm.Messages.Add(Item(i));
            host.Window.UpdateLayout();
            vm.JumpToLatest();
            Pump();
            Assert.True(view.MessageScroller.ScrollableHeight > 300);
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            view.MessageScroller.RaiseEvent(wheel);
            Pump();
            Assert.True(wheel.Handled);
            Assert.False(vm.ScrollState.IsFollowingLatest);
            var anchor = vm.Messages[10];
            var element = (FrameworkElement)view.MessageItems.ItemContainerGenerator.ContainerFromItem(anchor);
            var before = element.TranslatePoint(new Point(), view.MessageScroller).Y;
            vm.BeginMessageUpdate();
            vm.Messages.RemoveAt(0);
            vm.Messages.Add(Item(21));
            vm.NotifyNewMessage();
            vm.EndMessageUpdate();
            host.Window.UpdateLayout();
            Pump();
            var after = element.TranslatePoint(new Point(), view.MessageScroller).Y;
            Assert.InRange(Math.Abs(before - after), 0, 2);
            Assert.Equal(1, vm.ScrollState.UnreadCount);
            Assert.True(vm.IsJumpVisible);
            vm.IsHudUnlocked = false;
            var lockedWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            view.MessageScroller.RaiseEvent(lockedWheel);
            Assert.False(lockedWheel.Handled);
            vm.IsHudUnlocked = true;
            vm.JumpToLatest();
            Pump();
            Assert.Equal(0, vm.ScrollState.UnreadCount);
            Assert.InRange(view.MessageScroller.ScrollableHeight - view.MessageScroller.VerticalOffset, 0, 2);
        }
        finally { foreach (var item in owned) item.Dispose(); }
    });

    [Fact]
    public void SoldTransitionIsBoundedNonInteractiveAndViewTimersDetachOnUnload() => RunSta(() =>
    {
        var vm = new SalesQueueViewModel(new ResourceLocalizationService("ko"));
        var view = new SalesQueueView { DataContext = vm };
        using var host = Host(view, 560, 360);
        var snapshot = Queue();
        vm.Apply(snapshot, AppSettings.CreateDefault());
        vm.ToggleDetailCommand.Execute(null);
        host.Window.UpdateLayout();
        Assert.Equal(Visibility.Visible, view.QueueDetailPanel.Visibility);
        var after = snapshot with
        {
            ActiveItems = snapshot.ActiveItems.Skip(1).ToArray(),
            CurrentSeller = snapshot.ActiveItems[1],
            ActiveCount = 2,
            WaitingCount = 1
        };
        vm.Apply(after, AppSettings.CreateDefault(),
            SalesFeatureHealthEvaluator.Evaluate(new(true, RemoteSalesPresentationPhase.Live, true,
                SalesCoverageState.Complete, DateTimeOffset.UtcNow, 2, 2)), "#sales",
            new(true, "1", "2", SalesQueueChangeReason.TrustedSold, 2, new[] { "1" }));
        Assert.DoesNotContain(vm.DetailItems, item => item.MessageId == "1");
        if (SystemParameters.ClientAreaAnimation)
        {
            var departing = Assert.Single(view.DetailRows.Items.Cast<SalesQueueDetailItem>().Where(item => item.IsDeparting));
            Assert.False(departing.IsStatusActionEnabled);
            Pump(260);
            Assert.DoesNotContain(view.DetailRows.Items.Cast<SalesQueueDetailItem>(), item => item.IsDeparting);
        }
        for (var i = 0; i < 3; i++)
        {
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Empty(view.DetailRows.Items);
            Assert.False(Field<DispatcherTimer>(view, "_ageTimer").IsEnabled);
            Assert.False(Field<DispatcherTimer>(view, "_departureTimer").IsEnabled);
            Assert.Equal(0, HandlerCount(vm, "DetailItemsRefreshed"));
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(1, HandlerCount(vm, "DetailItemsRefreshed"));
        }
    });

    [Fact]
    public void LockedExpandedDetailStaysVisibleWithGlobalNativeClickThrough() => RunSta(() =>
    {
        var vm = new SalesQueueViewModel(new ResourceLocalizationService("ko"));
        var view = new SalesQueueView { DataContext = vm };
        using var host = Host(view, 520, 330);
        var locked = false;
        using var interop = new WindowInteropService(host.Window, () => locked, NullAppLogger.Instance);
        interop.Initialize();
        vm.Apply(Queue(), AppSettings.CreateDefault());
        vm.ToggleDetailCommand.Execute(null);
        Assert.True(vm.IsQueueDetailExpanded);
        locked = true;
        vm.UpdateHudContext(true, false, true, false);
        Assert.True(interop.ApplyClickThrough(true));
        Assert.True(vm.IsQueueDetailExpanded);
        Assert.False(view.QueueDetailScroller.IsEnabled);
        Assert.False(view.QueueDetailPanel.IsHitTestVisible);
        Assert.Equal(Visibility.Visible, view.QueueDetailPanel.Visibility);
        locked = false;
        Assert.True(interop.ApplyClickThrough(false));
        vm.UpdateHudContext(true, false, true, true);
        Assert.True(view.QueueDetailScroller.IsEnabled);
        Assert.True(vm.IsQueueDetailExpanded);
    });

    [Theory]
    [InlineData("en", 18, "18 / 30", false)]
    [InlineData("ko", 29, "29 / 30", false)]
    [InlineData("ko", 30, "풀세션", true)]
    [InlineData("en", 31, "Full Session", true)]
    [InlineData("ja", 32, "満員", true)]
    public void SessionBadgeNormalizesPresentationOnly(string locale, int current, string text, bool full)
    {
        var vm = new SessionHudViewModel(new ResourceLocalizationService(locale), AppSettings.CreateDefault());
        vm.UpdateRemoteState(true, SessionRemoteState.Live);
        var source = new HostPresenceSnapshot(1, HostPresenceState.GtaOnline, current, 32, DateTimeOffset.UtcNow);
        vm.ApplyBootstrap(new(OverlayTransportProtocol.Version, "test", 0, 7, new[] { source }));
        var item = Assert.Single(vm.Items);
        Assert.Equal(text, item.Value);
        Assert.Equal(full, item.IsFull);
        Assert.Equal(current, source.CurrentPlayers);
        Assert.Equal(32, source.MaximumPlayers);
        Assert.False(item.IsLabelVisible);
    }

    [Theory]
    [InlineData(ColorThemeId.GitHubDark)]
    [InlineData(ColorThemeId.OneDarkPro)]
    [InlineData(ColorThemeId.Nord)]
    [InlineData(ColorThemeId.TokyoNight)]
    [InlineData(ColorThemeId.Monokai)]
    public void CompactSalesRowsRenderAcrossThemesWithoutChangingOrder(ColorThemeId theme) => RunSta(() =>
    {
        var vm = new SalesQueueViewModel(new ResourceLocalizationService("ko"));
        var view = new SalesQueueView { DataContext = vm };
        var colors = ColorThemeManager.CreateResources(ColorThemeCatalog.Get(theme));
        view.Resources.MergedDictionaries.Add(colors);
        foreach (var key in new[] { "SalesSurfaceEffectiveBrush", "SalesDetailSurfaceEffectiveBrush" })
            view.Resources[key] = colors["SurfaceBaseBrush"];
        view.Resources["SalesNextSurfaceEffectiveBrush"] = colors["SurfaceSelectedBrush"];
        view.Resources["SalesCurrentSurfaceEffectiveBrush"] = colors["AccentSubtleBrush"];
        view.Resources["SalesBorderEffectiveBrush"] = colors["BorderSubtleBrush"];
        using var host = Host(view, 560, 360);
        vm.Apply(Queue(), AppSettings.CreateDefault() with { ColorTheme = theme });
        vm.ToggleDetailCommand.Execute(null);
        host.Window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(560, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        Assert.Equal(new[] { "1", "2", "3" }, vm.DetailItems.Select(item => item.MessageId));
        Assert.All(vm.DetailItems, item => Assert.False(item.ProductName.Contains('\n')));
        var output = Environment.GetEnvironmentVariable("LSOVERLAY_M10_VISUAL_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"sales-{theme}.png"));
            encoder.Save(file);
        }
    });

    private static SalesQueueSnapshot Queue()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[] { "ItoToko", "DE-SSANTA", "-TheFirstStar-" }.Select((name, index) =>
            new SalesQueueEntry((index + 1).ToString(), "guild", index == 0 ? "self" : "other",
                now.AddMinutes(-index - 2), name, DiscordDisplayNameSource.GuildNickname, true,
                new SaleProduct("bunker", "벙커 · 나클", "1", "bunker"), SaleObservationTrust.Trusted)).ToArray();
        return SalesQueueSnapshot.Empty with
        {
            ActiveItems = entries,
            CurrentSeller = entries[0],
            ActiveCount = 3,
            WaitingCount = 2,
            NextWaitingEntry = entries[1],
            ObservationStatus = SalesObservationStatus.Live,
            IsObservationSourceAvailable = true,
            AuthenticatedUserId = "self",
            CurrentSellerIsSelf = true
        };
    }

    private static WpfHost Host(FrameworkElement view, double width, double height)
    {
        var window = new Window
        {
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            Content = view
        };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/GachaOverlay.App;component/Themes/DesignTokens.xaml", UriKind.Relative) });
        view.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/GachaOverlay.App;component/Themes/DesignTokens.xaml", UriKind.Relative) });
        // Standalone controls have no Application resource scope; full App styles are
        // verified separately by the published offline UI verifier.
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
        view.Resources[typeof(TextBlock)] = textStyle;
        window.Show();
        window.UpdateLayout();
        Pump();
        return new(window);
    }

    private sealed record WpfHost(Window Window) : IDisposable { public void Dispose() => Window.Close(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static int HandlerCount(object owner, string name) => Field<Delegate?>(owner, name)?.GetInvocationList().Length ?? 0;
    private static void Pump(int milliseconds = 25)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        EventHandler handler = (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Tick += handler;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= handler; }
    }
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Offline WPF test did not complete");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}

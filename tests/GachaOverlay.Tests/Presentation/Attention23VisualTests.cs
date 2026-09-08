using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Attention;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Themes;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Discord.Messages;
using LSOverlay.Protocol;
using GachaOverlay.Infrastructure.Attention;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Presentation;

[Collection(WpfApplicationCollection.Name)]
public sealed class Attention23VisualTests
{
    [Fact]
    public void ActualViewsAcrossFiveThemesThreeDensitiesThreeScales()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            using var directory = new TemporaryDirectory();
            var output = Path.Combine(SalesRendering23Tests.Root(), "artifacts/validation-2.3-attention/visual");
            Directory.CreateDirectory(output);
            var history = new NotificationHistory();
            history.SetOwner("self");
            history.ObserveChat(Attention23Tests.Message(mention: "self", content: "새 알림 — 확인해 주세요"), true);
            history.ObserveChat(Attention23Tests.Message("101", replyAuthor: "self", content: "답장 알림"), true);
            var path = directory.File("attention.json");
            Assert.True(new JsonNotificationStore(path).Save(history.Snapshot()));
            using var notifications = new NotificationCenterViewModel(path, Dispatcher.CurrentDispatcher);
            Wait(() => notifications.Items.Count == 2);
            notifications.ToggleCommand.Execute(null);
            Assert.Equal(2, notifications.UnreadCount); // Opening is not acknowledgement.
            var localization = new ResourceLocalizationService("ko");
            var typography = new ChatTypographyResolver(NullAppLogger.Instance);
            var views = new List<ChatMessageViewModel>();
            var root = new StackPanel { Margin = new Thickness(12), Width = 600 };
            Resources(root);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var chat = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            grid.Children.Add(chat);
            var center = new NotificationCenterView { DataContext = notifications, VerticalAlignment = VerticalAlignment.Top, MaxHeight = 300 };
            Grid.SetColumn(center, 1);
            grid.Children.Add(center);
            root.Children.Add(grid);
            foreach (var attention in new[] { ChatAttention.Normal, ChatAttention.Normal, ChatAttention.DirectSelfMention, ChatAttention.ReplyToSelf, ChatAttention.EveryoneHere })
            {
                var label = attention switch
                {
                    ChatAttention.DirectSelfMention => "@나 직접 멘션",
                    ChatAttention.ReplyToSelf => "내 메시지에 달린 답장",
                    ChatAttention.EveryoneHere => "@everyone / @here 공지",
                    _ => views.Count == 0 ? "일반 메시지" : "@다른사람 멘션",
                };
                var tokens = new[] { new ChatToken(attention == ChatAttention.DirectSelfMention || views.Count == 1 ? ChatTokenKind.Mention : ChatTokenKind.Text,
                    label, IsSelfMention: attention == ChatAttention.DirectSelfMention) };
                var vm = new ChatMessageViewModel(new(views.Count.ToString(), "테스트 작성자", DateTimeOffset.Now, tokens, label,
                    [], [], 0, attention == ChatAttention.DirectSelfMention, 1, 1)
                { Attention = attention }, localization, _ => { });
                views.Add(vm);
                chat.Children.Add(new ChatMessageView { DataContext = vm, Margin = new Thickness(0, 0, 10, 12) });
            }
            var raw = new[]
            {
                new ChatTokenViewModel(new(ChatTokenKind.Text, "원문 판매 ")),
                new ChatTokenViewModel(new(ChatTokenKind.CustomEmoji, ":SELL_SP:", "123")),
                new ChatTokenViewModel(new(ChatTokenKind.Text, " 순서 유지 ")),
                new ChatTokenViewModel(new(ChatTokenKind.CustomEmoji, ":fallback:", "456")),
            };
            var emoji = new DrawingImage(new GeometryDrawing(Brushes.Gold, null, new EllipseGeometry(new Point(8, 8), 7, 7)));
            emoji.Freeze();
            raw[1].Image = emoji;
            var rawText = new CrispOutlinedText { Tokens = raw, FontSize = 14, EmojiExtent = 18, OutlineEnabled = false, TextWrapping = TextWrapping.Wrap };
            rawText.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            root.Children.Add(rawText);
            root.Children.Add(new TextBlock { Text = "GTA 감지됨  /  GTA 미감지  /  생산 타이머 일시 정지", Margin = new Thickness(0, 10, 0, 0) });
            foreach (var theme in ColorThemeCatalog.All)
            {
                var resources = ColorThemeManager.CreateResources(theme);
                root.Resources.MergedDictionaries.Add(resources);
                foreach (var density in new[] { "Balanced", "Compact", "UltraCompact" })
                {
                    var settings = AppSettings.CreateDefault() with { ChatLayoutMode = density == "Balanced" ? ChatLayoutMode.Balanced : ChatLayoutMode.Compact, ChatFontSizePoints = 12 };
                    foreach (var vm in views) vm.ApplySettings(settings, density == "UltraCompact" ? ChatResponsiveLevel.UltraCompact : ChatResponsiveLevel.Full, typography.Resolve(settings.ChatFontPreset));
                    foreach (var scale in new[] { 1d, 1.25, 1.5 })
                    {
                        root.Measure(new Size(624, 1000));
                        root.Arrange(new Rect(0, 0, 624, root.DesiredSize.Height));
                        root.UpdateLayout();
                        Assert.True(root.ActualHeight is > 100 and < 1000);
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(624 * scale), (int)Math.Ceiling((root.ActualHeight + 24) * scale),
                            96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(output, $"{theme.Id}-{density}-{scale:0.00}.png"));
                        encoder.Save(file);
                    }
                }
                root.Resources.MergedDictionaries.Remove(resources);
            }
            foreach (var vm in views) vm.Dispose();
            notifications.Items[0].MarkReadCommand.Execute(null);
            Assert.Equal(1, notifications.UnreadCount);
            notifications.MarkAllReadCommand.Execute(null);
            Assert.False(notifications.HasUnread);
            notifications.Close();
            Assert.False(notifications.IsOpen);
        });
    }

    [Fact]
    public void NotificationMutationLifecycleAndPollingDoNotDuplicate()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            using var directory = new TemporaryDirectory();
            using var vm = new NotificationCenterViewModel(directory.File("attention.json"), Dispatcher.CurrentDispatcher);
            vm.SetOwner("self");
            vm.Sales("post", false);
            vm.Sales("post", true);
            vm.Gta(new(true, true, true, new(10, 1, "GTA5"), 1), false);
            Wait(() => vm.Items.Count == 3);
            for (var i = 0; i < 10; i++)
            {
                vm.Sales("post", true);
                vm.Gta(new(true, false, true, new(10, 1, "GTA5"), 1), false);
            }
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(30));
            Assert.Equal(3, vm.Items.Count);
            vm.Gta(new(false, false, false, null, 2), true);
            Wait(() => vm.Items.Count == 4);
            vm.ToggleCommand.Execute(null);
            vm.Close();
            Assert.Equal(4, vm.UnreadCount);
        });
    }

    private static void Wait(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(predicate());
    }

    [Fact]
    public void IntegratedHudLockOpacityBadgeSalesAndCompanionStates()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            using var temporary = new TemporaryDirectory();
            var output = Path.Combine(SalesRendering23Tests.Root(), "artifacts/validation-2.3-attention/visual");
            Directory.CreateDirectory(output);
            var localization = new ResourceLocalizationService("ko");
            var settings = AppSettings.CreateDefault() with { MinimalHudMode = true, HudSurfaceOpacity = 0.9 };
            var chat = new ChatViewModel();
            var sales = new SalesQueueViewModel(localization);
            using var timers = new GtaoTimerHudViewModel(localization, settings, Dispatcher.CurrentDispatcher);
            using var notifications = new NotificationCenterViewModel(temporary.File("hud-history.json"), Dispatcher.CurrentDispatcher);
            var shell = new HudShellViewModel(localization, chat, sales, new SessionHudViewModel(localization, settings), timers)
            { Notifications = notifications };
            shell.ApplySettings(settings);
            var item = new SalesQueueEntry("200", "1", "self", DateTimeOffset.Now, "판매자", DiscordDisplayNameSource.GuildNickname,
                true, null, SaleObservationTrust.Trusted, [], SaleParseStatus.Unknown,
                "원문 판매 <:SELL_SP:123456789012345678> <:fallback:223456789012345678> " + new string('가', 150));
            var snapshot = SalesQueueSnapshot.Empty with
            {
                ActiveItems = [item],
                CurrentSeller = item,
                ActiveCount = 1,
                CurrentSellerIsSelf = true,
                AuthenticatedUserId = "self",
                IsObservationSourceAvailable = true,
                ObservationStatus = SalesObservationStatus.Live,
            };
            sales.Apply(snapshot, settings, SalesFeatureHealthEvaluator.Evaluate(new(true, RemoteSalesPresentationPhase.Live,
                true, SalesCoverageState.Complete, DateTimeOffset.Now, 1, 1)), "판매", SalesQueueChangeContext.None);
            sales.UpdateHudContext(true, false, true, true);
            var window = new HudWindow { DataContext = shell, Width = 620, Height = 600, Left = -12000, Top = -12000, ShowActivated = false };
            Resources(window);
            try
            {
                window.Show();
                window.SetAppearance(settings);
                shell.Update(new(false, true, HudVisibilityMode.Always, true, true), "Live", "Live", null);
                var center = (NotificationCenterView)window.FindName("NotificationContent");
                notifications.SetOwner("self");
                MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(50));
                Capture(window, "hud-badge-zero");
                notifications.Sales("200", true);
                Wait(() => notifications.UnreadCount == 1);
                Capture(window, "hud-badge-one-sales-raw");
                var salesView = (SalesQueueView)window.FindName("SalesContent");
                var bar = (Border)salesView.FindName("QueueBar");
                var content = (StackPanel)salesView.FindName("ContentHost");
                var statusText = (TextBlock)salesView.FindName("PrimaryStatusText");
                Assert.InRange(content.TranslatePoint(new Point(0, 0), bar).X, 40, 55);
                Assert.Equal(VerticalAlignment.Center, content.VerticalAlignment);
                Assert.Equal(TextAlignment.Left, statusText.TextAlignment);
                Assert.Equal(14, statusText.FontSize);
                notifications.Sales("201", false);
                Wait(() => notifications.UnreadCount == 2);
                notifications.ToggleCommand.Execute(null);
                window.UpdateLayout();
                Assert.True(center.IsHitTestVisible && center.IsEnabled);
                Assert.True(center.IsVisible);
                Assert.True(center.ActualWidth > 500 && center.ActualHeight > 350);
                var bell = (System.Windows.Controls.Button)window.FindName("FloatingNotificationButton");
                var gear = (System.Windows.Controls.Button)window.FindName("FloatingSettingsButton");
                Assert.True(bell.TranslatePoint(new Point(bell.ActualWidth, 0), window).X < gear.TranslatePoint(new Point(0, 0), window).X);
                Assert.Equal(gear.TranslatePoint(new Point(0, 0), window).Y, bell.TranslatePoint(new Point(0, 0), window).Y);
                Assert.Equal(2, notifications.UnreadCount);
                Capture(window, "hud-notifications-open");
                var panelBottom = center.TranslatePoint(new Point(0, center.ActualHeight), window).Y;
                var salesTop = ((SalesQueueView)window.FindName("SalesContent")).TranslatePoint(new Point(0, 0), window).Y;
                Assert.True(panelBottom <= salesTop, $"Notification {panelBottom} overlaps Sales {salesTop}");
                shell.Update(new(true, true, HudVisibilityMode.Always, true, true), "Live", "Live", null);
                MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(20));
                Assert.False(center.IsHitTestVisible);
                Assert.False(notifications.IsOpen);
                window.SetAppearance(settings with { HudSurfaceOpacity = 0 });
                Assert.Equal(255, Assert.IsType<SolidColorBrush>(center.PanelBackground).Color.A);
                Capture(window, "hud-locked-zero-opacity");
            }
            finally { window.AllowClose = true; window.Close(); }

            foreach (var state in new[] { "detected", "absent", "active-lost" })
            {
                var companion = new GtaCompanionWindow
                {
                    Left = -12000,
                    Top = -12000,
                    ShowActivated = false,
                    DataContext = new
                    {
                        IsInteractive = true,
                        ShowDaily = false,
                        ShowWeekly = false,
                        ShowEvents = false,
                        ClientDetectionText = state == "detected" ? "GTA 감지됨" : "GTA 미감지",
                        ClientDetectionHint = "생산 타이머 일시 정지",
                        ClientDetectionNeedsAttention = state == "active-lost",
                    }
                };
                Resources(companion);
                try { companion.Show(); companion.SetSurfaceOpacity(0.9); Capture(companion, "companion-" + state); }
                finally { companion.AllowClose = true; companion.Close(); }
            }

            void Capture(Window target, string name)
            {
                target.UpdateLayout();
                MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(15));
                var bitmap = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(target);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, name + ".png"));
                encoder.Save(file);
            }
        });
    }

    [Fact]
    public void SelfMentionBackgroundSettingPersistsAndNotificationUsesChatTypography()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            using var temporary = new TemporaryDirectory();
            var store = new GachaOverlay.Infrastructure.Settings.JsonSettingsStore(temporary.File("settings.json"));
            Assert.True(store.Load().ChatSelfMentionBackgroundEnabled);
            var settings = AppSettings.CreateDefault() with { ChatFontPreset = ChatFontPreset.WantedSans, ChatFontSizePoints = 20 };
            var typography = new ChatTypographyResolver(NullAppLogger.Instance).Resolve(settings.ChatFontPreset);
            using var notification = new NotificationCenterViewModel(temporary.File("notifications.json"), Dispatcher.CurrentDispatcher);
            notification.ApplySettings(settings);
            Assert.Equal(12 * 96d / 72d, notification.FontSizeDip);
            notification.ApplySettings(settings with { ChatFontSizePoints = 16 });
            Assert.Equal(12 * 96d / 72d, notification.FontSizeDip);
            Assert.Equal(20, settings.ChatFontSizePoints);
            Assert.Equal(typography.Message.FontFamily, notification.MessageFontFamily);
            Assert.Equal(typography.Message.FontWeight, notification.MessageFontWeight);
            Assert.Equal(typography.Nickname.FontWeight, notification.ContextFontWeight);
            using var vm = new ChatMessageViewModel(new("1", "작성자", DateTimeOffset.Now,
                [new(ChatTokenKind.Mention, "@나", IsSelfMention: true)], "@나", [], [], 0, true, 1, 1)
            { Attention = ChatAttention.DirectSelfMention }, new ResourceLocalizationService("ko"), _ => { });
            var view = new ChatMessageView { DataContext = vm };
            Resources(view);
            foreach (var enabled in new[] { true, false, true })
            {
                settings = settings with { ChatSelfMentionBackgroundEnabled = enabled };
                Assert.True(store.Save(settings));
                Assert.Equal(enabled, new GachaOverlay.Infrastructure.Settings.JsonSettingsStore(temporary.File("settings.json")).Load().ChatSelfMentionBackgroundEnabled);
                vm.ApplySettings(settings, ChatResponsiveLevel.Full, typography);
                view.Measure(new Size(500, 250));
                view.Arrange(new Rect(0, 0, 500, 250));
                view.UpdateLayout();
                var surface = (Border)view.FindName("MessageSurface");
                Assert.Equal(enabled, Assert.IsType<SolidColorBrush>(surface.Background).Color.A > 0);
                foreach (var text in Descendants(view).OfType<CrispOutlinedText>().Where(x => x.Name.EndsWith("Body", StringComparison.Ordinal)))
                    Assert.Equal(enabled, Assert.IsType<SolidColorBrush>(text.SelfMentionBackground).Color.A > 0);
                Assert.Equal(ChatAttention.DirectSelfMention, vm.Attention);
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Resources(FrameworkElement target)
    {
        // Production merges these in Application scope. Standalone STA tests have
        // no Application; flatten the same unmodified dictionaries for static lookup.
        var themes = Path.Combine(SalesRendering23Tests.Root(), "src/GachaOverlay.App/Themes");
        var design = System.Xml.Linq.XDocument.Load(Path.Combine(themes, "DesignTokens.xaml"));
        var modern = System.Xml.Linq.XDocument.Load(Path.Combine(themes, "ModernControls.xaml"));
        design.Root!.Add(modern.Root!.Nodes());
        target.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(design.ToString()));
        target.Resources.MergedDictionaries.Add(ColorThemeManager.CreateResources(ColorThemeCatalog.Get(ColorThemeCatalog.DefaultTheme)));
    }
}

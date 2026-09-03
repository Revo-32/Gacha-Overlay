using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Providers;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Themes;
using GachaOverlay.Infrastructure.Localization;
using LSOverlay.Protocol;

namespace GachaOverlay.App.Services;

// Explicit offline entry point: synthetic data only; never creates ApplicationHost,
// starts hooks/HTTP/Discord, or reads/writes the user's profile or credential.
internal static class M10UiVerification
{
    public static async Task<int> RunAsync(System.Windows.Application app, string output)
    {
        if (!Path.IsPathFullyQualified(output) || Directory.Exists(output) || File.Exists(output)) return 2;
        Directory.CreateDirectory(output);
        var stage = "Initialize";
        HudWindow? hud = null;
        FoundationWindow? settingsWindow = null;
        var chatRows = new List<ChatMessageViewModel>();
        FoundationViewModel? settingsVm = null;
        try
        {
            var localization = new ResourceLocalizationService("ko");
            var store = new SyntheticSettingsStore();
            var typography = new ChatTypographyResolver(NullAppLogger.Instance);
            var chat = new ChatViewModel { IsHudUnlocked = true };
            var sales = new SalesQueueViewModel(localization);
            sales.ConfigureStatusAction((_, _, _) => Task.FromResult<SalesStatusActionResponse?>(null));
            sales.ApplyRemoteStatusContext(new Dictionary<string, SalesCompletionObservation>(), EffectiveSalesSource.RemotePrimary);
            var session = new SessionHudViewModel(localization, store.Current);
            var shell = new HudShellViewModel(localization, chat, sales, session);
            shell.ApplySettings(store.Current);
            shell.Update(new(false, true, HudVisibilityMode.Always, true, true), "Live", "Live", null);
            session.UpdateRemoteState(true, SessionRemoteState.Live);
            session.ApplyBootstrap(new(OverlayTransportProtocol.Version, "synthetic", 0, 7,
                new[] { new HostPresenceSnapshot(1, HostPresenceState.GtaOnline, 18, 32, DateTimeOffset.UtcNow) }));
            var names = new[] { "ItoToko", "Long-Nickname-Layout-Check", "DE-SSANTA", "-TheFirstStar-" };
            for (var i = 0; i < 9; i++)
            {
                var content = i % 2 == 0 ? "테스트 메시지입니다. 메인 채팅과 판매 목록을 확인합니다." : "M10 · Chat readability / 한글 · 日本語";
                var row = new ChatMessageViewModel(new((i + 1).ToString(), names[i % names.Length], DateTimeOffset.UtcNow,
                    new[] { new ChatToken(ChatTokenKind.Text, content) }, content,
                    Array.Empty<ChatMediaCandidate>(), Array.Empty<ChatStickerPresentation>(), 0, false, 1, 1),
                    localization, _ => { });
                row.ApplySettings(store.Current, ChatResponsiveLevel.Full, typography.Resolve(store.Current.ChatFontPreset));
                chatRows.Add(row);
                chat.Messages.Add(row);
            }
            var entries = names.Take(3).Select((name, i) => new SalesQueueEntry((i + 1).ToString(), "synthetic",
                i == 0 ? "self" : "other", DateTimeOffset.UtcNow.AddMinutes(-3 - i), name,
                DiscordDisplayNameSource.GuildNickname, true, null, SaleObservationTrust.Trusted,
                new[] { new SaleProduct("spa", "스패", "1", "spa", 3), new SaleProduct("bunker", "벙커", "2", "bunker") })).ToArray();
            var queue = SalesQueueSnapshot.Empty with
            {
                ActiveItems = entries,
                CurrentSeller = entries[0],
                ActiveCount = 3,
                WaitingCount = 2,
                NextWaitingEntry = entries[1],
                AuthenticatedUserId = "self",
                CurrentSellerIsSelf = true,
                IsObservationSourceAvailable = true,
                ObservationStatus = SalesObservationStatus.Live
            };
            sales.Apply(queue, store.Current,
                SalesFeatureHealthEvaluator.Evaluate(new(true, RemoteSalesPresentationPhase.Live, true,
                    SalesCoverageState.Complete, DateTimeOffset.UtcNow, 3, 3)), "#sales", SalesQueueChangeContext.None);
            sales.UpdateHudContext(true, false, true, true);
            sales.ToggleDetailCommand.Execute(null);
            hud = new HudWindow { Width = 620, Height = 830, Left = -10000, Top = -10000, ShowActivated = false, DataContext = shell };
            var themeManager = new ColorThemeManager(app);
            foreach (var theme in ColorThemeCatalog.All)
            {
                stage = "Hud-" + theme.Id;
                themeManager.Apply(theme.Id);
                hud.SetAppearance(store.Current with { ColorTheme = theme.Id });
                hud.Show();
                await LayoutAsync(hud);
                Capture(hud, Path.Combine(output, "hud-" + theme.Id + ".png"));
            }
            stage = "CollapsedSales";
            sales.ToggleDetailCommand.Execute(null);
            await LayoutAsync(hud);
            Capture(hud, Path.Combine(output, "hud-sales-collapsed.png"));
            sales.ToggleDetailCommand.Execute(null);
            stage = "FullBadge";
            session.ApplyPresence(new(1, HostPresenceState.GtaOnline, 32, 32, DateTimeOffset.UtcNow));
            shell.Update(new(true, true, HudVisibilityMode.Always, true, true), "Live", "Live", null);
            sales.UpdateHudContext(true, false, true, false);
            await LayoutAsync(hud);
            Capture(hud, Path.Combine(output, "hud-locked-full.png"));

            stage = "Settings";
            var remote = new RemoteChatSettingsViewModel(localization,
                RemoteChatSnapshot.Disconnected(AppSettings.CreateDefault().RemoteBackendBaseUrl) with { Health = RemoteChatHealthState.LoginRequired },
                _ => Task.FromResult(false), () => Task.CompletedTask, () => { },
                () => Task.FromResult(false), () => Task.CompletedTask, _ => Task.FromResult(false));
            settingsVm = new FoundationViewModel(store, localization, NullAppLogger.Instance, typography, () => { }, _ => { }, () => { },
                remoteChatSettings: remote);
            settingsWindow = new FoundationWindow { Left = -10000, Top = -10000, ShowActivated = false, DataContext = settingsVm };
            foreach (var category in new[] { SettingsCategory.Discord, SettingsCategory.Hud, SettingsCategory.Hotkeys, SettingsCategory.Developer })
            {
                settingsVm.SelectedSettingsCategory = category;
                settingsWindow.Show();
                await LayoutAsync(settingsWindow);
                Capture(settingsWindow, Path.Combine(output, "settings-" + category + ".png"));
                settingsWindow.Hide();
            }
            stage = "Onboarding";
            using (var onboardingVm = new OnboardingViewModel(settingsVm, store, localization, () => { }, true))
            {
                var onboarding = new OnboardingWindow { Left = -10000, Top = -10000, ShowActivated = false, DataContext = onboardingVm };
                try
                {
                    onboarding.Show();
                    await LayoutAsync(onboarding);
                    Capture(onboarding, Path.Combine(output, "onboarding.png"));
                }
                finally { onboarding.Close(); }
            }
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                Status = "PASS",
                Synthetic = true,
                NetworkStarted = false,
                UserProfileRead = false,
                Themes = 5,
                UserPhysicalInput = "NOT RUN",
                Images = Directory.GetFiles(output, "*.png").Length,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                Status = "FAIL",
                Stage = stage,
                ExceptionType = exception.GetType().Name,
                InnerType = exception.InnerException?.GetType().Name
            }));
            return 1;
        }
        finally
        {
            settingsWindow?.Hide();
            if (settingsWindow is not null) { settingsWindow.AllowClose = true; settingsWindow.Close(); }
            if (hud is not null) { hud.AllowClose = true; hud.Close(); }
            settingsVm?.Dispose();
            foreach (var row in chatRows) row.Dispose();
        }
    }

    private static async Task LayoutAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }

    private sealed class SyntheticSettingsStore : ISettingsStore
    {
        public AppSettings Current { get; private set; } = AppSettings.CreateDefault() with
        { Language = "ko", ChatFontSizePoints = 14, HudSurfaceOpacity = 0.95 };
        public AppSettings Load() => Current;
        public bool Save(AppSettings settings) { Current = settings; return true; }
        public bool Update(Func<AppSettings, AppSettings> update) => Save(update(Current));
    }
}

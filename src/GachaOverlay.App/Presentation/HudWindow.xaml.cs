using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Themes;

namespace GachaOverlay.App.Presentation;

public partial class HudWindow : Window
{
    private AppSettings _appearanceSettings = AppSettings.CreateDefault();

    public HudWindow()
    {
        InitializeComponent();
        SetSurfaceOpacity(HudSettingsDefaults.SurfaceOpacity);
        ChatContent.AvailableSizeChanged += size => ChatAvailableSizeChanged?.Invoke(size);
    }

    public event Action? DragRequested;

    public event Action? SettingsRequested;

    public event Action<System.Windows.Size>? ChatAvailableSizeChanged;

    public bool AllowClose { get; set; }

    public void SetSurfaceOpacity(double opacity)
    {
        SetAppearance(AppSettings.CreateDefault() with { HudSurfaceOpacity = opacity });
    }

    public void SetAppearance(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _appearanceSettings = settings;
        RefreshTheme();
    }

    public void RefreshTheme()
    {
        var settings = _appearanceSettings;
        var normalized = HudSettingsDefaults.NormalizeSurfaceOpacity(settings.HudSurfaceOpacity);
        var chromeAlpha = HudSurfaceOpacityPolicy.CalculateAlpha(normalized, settings.HudChromeOpacity);
        var chatAlpha = HudSurfaceOpacityPolicy.CalculateAlpha(normalized, settings.ChatSurfaceOpacity);
        HudSurface.Background = ColorThemeManager.CreateOpacityBrush(
            SemanticColorToken.SurfaceBase,
            chromeAlpha);
        HudSurface.BorderBrush = ColorThemeManager.CreateOpacityBrush(
            SemanticColorToken.BorderSubtle,
            chromeAlpha == 0 ? (byte)0 : (byte)Math.Max(28, chromeAlpha / 2));
        ChatSurface.Background = ColorThemeManager.CreateOpacityBrush(
            SemanticColorToken.SurfaceRaised,
            chatAlpha);
        ChatSurface.BorderBrush = ColorThemeManager.CreateOpacityBrush(
            SemanticColorToken.BorderSubtle,
            chatAlpha == 0 ? (byte)0 : (byte)Math.Max(24, chatAlpha / 3));
        var chromeStatusBrush = ColorThemeManager.CreateOpacityBrush(
            SemanticColorToken.AccentSubtle,
            chromeAlpha);
        LockBadgeSurface.Background = chromeStatusBrush;
        FloatingEditSurface.Background = chromeStatusBrush;
        SalesContent.SetSurfaceOpacity(
            normalized,
            settings.SalesSurfaceOpacity,
            settings.QueueDetailSurfaceOpacity);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        SettingsRequested?.Invoke();
    }

    private void OnSettingsMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        // This HUD deliberately returns MA_NOACTIVATE. ButtonBase's default release
        // click relies on focus/capture surviving until mouse-up, which a foreground
        // game can interrupt. Dispatch this navigation action before ButtonBase
        // tries to focus/capture. Handling the preview also prevents a second Click
        // and prevents the parent drag surface from starting a move.
        if (eventArgs.ChangedButton == MouseButton.Left && sender is UIElement { IsEnabled: true })
        {
            eventArgs.Handled = true;
            SettingsRequested?.Invoke();
        }
    }

    private void OnDragSurfaceMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left)
        {
            if (WpfHitTestAncestry.HasInteractiveAncestor(
                    eventArgs.OriginalSource as DependencyObject))
            {
                eventArgs.Handled = true;
                return;
            }

            DragRequested?.Invoke();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (AllowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }
}

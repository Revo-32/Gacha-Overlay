using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Themes;

namespace GachaOverlay.App.Presentation;

public partial class BusinessManagerWindow : Window
{
    private double _surfaceOpacity = HudSettingsDefaults.SurfaceOpacity;
    public BusinessManagerWindow() { InitializeComponent(); RefreshTheme(); }
    public event Action? DragRequested;
    public bool AllowClose { get; set; }
    public void SetSurfaceOpacity(double opacity) { _surfaceOpacity = HudSettingsDefaults.NormalizeSurfaceOpacity(opacity); RefreshTheme(); }
    public void RefreshTheme()
    {
        var alpha = HudSurfaceOpacityPolicy.CalculateAlpha(_surfaceOpacity, 1);
        Surface.Background = ColorThemeManager.CreateOpacityBrush(SemanticColorToken.SurfaceBase, alpha);
        Surface.BorderBrush = ColorThemeManager.CreateOpacityBrush(SemanticColorToken.BorderSubtle,
            alpha == 0 ? (byte)0 : (byte)Math.Max(28, alpha / 2));
        Resources["BusinessCardSurfaceBrush"] = ColorThemeManager.CreateOpacityBrush(SemanticColorToken.SurfaceRaised, alpha);
    }
    private void OnDragMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left ||
            WpfHitTestAncestry.HasInteractiveAncestor(eventArgs.OriginalSource as DependencyObject)) return;
        DragRequested?.Invoke();
    }
    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (AllowClose) return;
        eventArgs.Cancel = true;
        Hide();
    }
}

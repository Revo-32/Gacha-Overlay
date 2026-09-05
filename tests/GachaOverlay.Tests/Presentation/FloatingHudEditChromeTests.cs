using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Themes;

namespace GachaOverlay.Tests.Presentation;

[Collection(WpfApplicationCollection.Name)]
public sealed class FloatingHudEditChromeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditChromeRemainsUsableAtZeroOpacityAndDisappearsWhenLocked(bool business)
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            Window window = business ? new BusinessManagerWindow() : new GtaCompanionWindow();
            try
            {
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/GachaOverlay.App;component/Themes/DesignTokens.xaml", UriKind.Relative) });
                window.Resources[typeof(TextBlock)] = new Style(typeof(TextBlock));
                window.Resources.MergedDictionaries.Add(ColorThemeManager.CreateResources(
                    ColorThemeCatalog.Get(ColorThemeCatalog.DefaultTheme)));
                var state = new EditState();
                window.DataContext = state;
                window.Left = -12000;
                window.Top = -12000;
                window.Width = 500;
                window.Height = 600;
                window.Show();
                var root = Assert.IsType<Grid>(window.FindName("EditRoot"));
                var outline = Assert.IsType<Border>(window.FindName("EditOutline"));
                var surface = Assert.IsType<Border>(window.FindName("Surface"));

                foreach (var opacity in new[] { 0d, 1d, 0d })
                {
                    if (window is BusinessManagerWindow manager) manager.SetSurfaceOpacity(opacity);
                    else ((GtaCompanionWindow)window).SetSurfaceOpacity(opacity);
                    foreach (var unlocked in new[] { false, true, false })
                    {
                        state.IsInteractive = unlocked;
                        root.Measure(new Size(500, 600));
                        root.Arrange(new Rect(0, 0, 500, 600));
                        root.UpdateLayout();
                        MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(10));
                        Assert.Equal(unlocked ? Visibility.Visible : Visibility.Collapsed, outline.Visibility);
                        Assert.Equal(unlocked ? (byte)1 : (byte)0,
                            Assert.IsType<SolidColorBrush>(root.Background).Color.A);
                        Assert.Equal(opacity == 0 ? (byte)0 : (byte)255,
                            Assert.IsType<SolidColorBrush>(surface.Background).Color.A);
                        Assert.False(outline.IsHitTestVisible);
                        Assert.Equal(new Thickness(1), outline.BorderThickness);
                        Assert.Equal((byte)255, Assert.IsType<SolidColorBrush>(outline.BorderBrush).Color.A);
                        if (unlocked)
                        {
                            Assert.NotNull(root.InputHitTest(new Point(0.5, 0.5)));
                            Assert.NotNull(root.InputHitTest(new Point(4, 300)));
                            Assert.NotNull(root.InputHitTest(new Point(496, 596)));
                        }
                    }
                }
            }
            finally
            {
                if (window is BusinessManagerWindow manager) manager.AllowClose = true;
                else ((GtaCompanionWindow)window).AllowClose = true;
                window.Close();
            }
        });
    }

    public sealed class EditState : DependencyObject
    {
        public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.Register(
            nameof(IsInteractive), typeof(bool), typeof(EditState), new PropertyMetadata(false));
        public bool IsInteractive
        {
            get => (bool)GetValue(IsInteractiveProperty);
            set => SetValue(IsInteractiveProperty, value);
        }
    }
}

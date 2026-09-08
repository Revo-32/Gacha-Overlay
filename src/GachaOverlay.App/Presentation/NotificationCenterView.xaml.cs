using System.Windows.Controls;

namespace GachaOverlay.App.Presentation;

public partial class NotificationCenterView : System.Windows.Controls.UserControl
{
    public static readonly System.Windows.DependencyProperty PanelBackgroundProperty = System.Windows.DependencyProperty.Register(
        nameof(PanelBackground), typeof(System.Windows.Media.Brush), typeof(NotificationCenterView));
    public System.Windows.Media.Brush? PanelBackground
    {
        get => (System.Windows.Media.Brush?)GetValue(PanelBackgroundProperty);
        set => SetValue(PanelBackgroundProperty, value);
    }
    public NotificationCenterView()
    {
        InitializeComponent();
        SetResourceReference(PanelBackgroundProperty, "SurfaceRaisedBrush");
    }
}

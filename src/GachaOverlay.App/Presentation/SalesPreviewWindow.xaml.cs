namespace GachaOverlay.App.Presentation;

public partial class SalesPreviewWindow : System.Windows.Window
{
    public SalesPreviewWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PreviewView.SetSurfaceOpacity(0.78, 1, 1);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    public void RefreshTheme() => PreviewView.SetSurfaceOpacity(0.78, 1, 1);
}

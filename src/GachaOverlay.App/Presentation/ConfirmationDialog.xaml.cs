namespace GachaOverlay.App.Presentation;

public partial class ConfirmationDialog : System.Windows.Window
{
    public ConfirmationDialog() => InitializeComponent();

    private void OnConfirm(object sender, System.Windows.RoutedEventArgs e) =>
        DialogResult = true;
}

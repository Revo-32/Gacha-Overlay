using System.Windows;
using System.Windows.Controls;

namespace GachaOverlay.App.Presentation;

public partial class OnboardingWindow : Window
{
    public OnboardingWindow()
    {
        InitializeComponent();
    }

    private void OnDiscordClientSecretChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is OnboardingViewModel viewModel && sender is PasswordBox passwordBox)
        {
            viewModel.Settings.SetDiscordClientSecret(passwordBox.Password);
        }
    }
}

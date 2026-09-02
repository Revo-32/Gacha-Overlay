using System.Windows;

namespace GachaOverlay.App.Presentation;

public partial class OnboardingWindow : Window
{
    public OnboardingWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (DataContext is OnboardingViewModel viewModel)
                viewModel.Settings.RemoteChatSettings?.CancelPairingCommand.Execute(null);
        };
    }
}

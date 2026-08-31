using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Presentation;

public partial class FoundationWindow : Window, ISettingsWindowHandle
{
    private SettingsCategory? _visibleCategory;

    public FoundationWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public bool AllowClose { get; set; }

    public IntPtr NativeHandle => new WindowInteropHelper(this).Handle;

    public void ShowAndActivate(SettingsCategory? category = null)
    {
        if (category.HasValue && DataContext is FoundationViewModel categoryViewModel)
        {
            categoryViewModel.OpenCategory(category.Value);
        }

        if (DataContext is FoundationViewModel diagnosticsViewModel)
        {
            diagnosticsViewModel.RefreshDiagnostics();
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void CloseForApplicationExit()
    {
        AllowClose = true;
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not FoundationViewModel viewModel)
        {
            return;
        }

        _visibleCategory = viewModel.SelectedSettingsCategory;
        viewModel.RefreshDiagnostics();
        RestoreScroll(viewModel, _visibleCategory.Value);
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!IsLoaded || DataContext is not FoundationViewModel viewModel)
        {
            return;
        }

        if (_visibleCategory.HasValue && _visibleCategory != viewModel.SelectedSettingsCategory)
        {
            viewModel.SaveCategoryScrollPosition(
                _visibleCategory.Value,
                CategoryScrollViewer.VerticalOffset);
        }

        _visibleCategory = viewModel.SelectedSettingsCategory;
        RestoreScroll(viewModel, _visibleCategory.Value);
    }

    private void RestoreScroll(FoundationViewModel viewModel, SettingsCategory category) =>
        Dispatcher.BeginInvoke(() => CategoryScrollViewer.ScrollToVerticalOffset(
            viewModel.GetCategoryScrollPosition(category)));

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_visibleCategory.HasValue && DataContext is FoundationViewModel viewModel)
        {
            viewModel.SaveCategoryScrollPosition(
                _visibleCategory.Value,
                CategoryScrollViewer.VerticalOffset);
        }

        if (AllowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }

    private void OnDiscordClientSecretChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is FoundationViewModel viewModel && sender is PasswordBox passwordBox)
        {
            viewModel.SetDiscordClientSecret(passwordBox.Password);
        }
    }

    private void OnDiscordSecretRevealTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (DataContext is FoundationViewModel viewModel &&
            sender is System.Windows.Controls.TextBox { Visibility: Visibility.Visible } textBox)
        {
            viewModel.SetDiscordClientSecret(textBox.Text);
        }
    }

    private void OnToggleDiscordSecretReveal(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not System.Windows.Controls.Button { Parent: Grid grid } button)
        {
            return;
        }

        var passwordBox = grid.Children.OfType<PasswordBox>().FirstOrDefault();
        var revealBox = grid.Children.OfType<System.Windows.Controls.TextBox>().FirstOrDefault();
        if (passwordBox is null || revealBox is null)
        {
            return;
        }

        if (revealBox.Visibility == Visibility.Visible)
        {
            passwordBox.Password = revealBox.Text;
            revealBox.Visibility = Visibility.Collapsed;
            passwordBox.Visibility = Visibility.Visible;
            button.Content = "◉";
            passwordBox.Focus();
            return;
        }

        revealBox.Text = passwordBox.Password;
        passwordBox.Visibility = Visibility.Collapsed;
        revealBox.Visibility = Visibility.Visible;
        button.Content = "⊘";
        revealBox.Focus();
        revealBox.CaretIndex = revealBox.Text.Length;
    }
}

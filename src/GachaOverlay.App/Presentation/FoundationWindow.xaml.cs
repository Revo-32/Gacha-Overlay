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

}

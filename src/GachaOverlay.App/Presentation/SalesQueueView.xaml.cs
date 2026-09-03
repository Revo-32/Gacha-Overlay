using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Media.Animation;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Hud;
using System.Windows.Media;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Themes;

namespace GachaOverlay.App.Presentation;

public partial class SalesQueueView : System.Windows.Controls.UserControl
{
    private SalesQueueViewModel? _viewModel;
    private bool _unloaded;
    private readonly ObservableCollection<SalesQueueDetailItem> _displayRows = new();
    private readonly DispatcherTimer _departureTimer = new() { Interval = SalesAnimationDurations.SoldTransition };
    private readonly DispatcherTimer _ageTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _detailWasExpanded;

    public SalesQueueView()
    {
        InitializeComponent();
        DetailRows.ItemsSource = _displayRows;
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void SetSurfaceOpacity(double globalOpacity, double localOpacity, double detailOpacity)
    {
        var alpha = HudSurfaceOpacityPolicy.CalculateAlpha(globalOpacity, localOpacity);
        Resources["SalesSurfaceEffectiveBrush"] = ColorThemeManager.CreateOpacityBrush(
            SemanticColorToken.SurfaceBase,
            alpha);
        Resources["SalesCurrentSurfaceEffectiveBrush"] = ColorThemeManager.CreateOpacityBrush(
            SemanticColorToken.AccentSubtle,
            alpha);
        Resources["SalesNextSurfaceEffectiveBrush"] = ColorThemeManager.CreateOpacityBrush(
            SemanticColorToken.SurfaceSelected,
            alpha);
        Resources["SalesDetailSurfaceEffectiveBrush"] = ColorThemeManager.CreateOpacityBrush(
            SemanticColorToken.SurfaceBase,
            HudSurfaceOpacityPolicy.CalculateAlpha(globalOpacity, detailOpacity));
        AccentSweep.Background = ColorThemeManager.CreateOpacityBrush(
            SemanticColorToken.AccentPrimary,
            alpha);
        Resources["SalesBorderEffectiveBrush"] = ColorThemeManager.CreateOpacityBrush(
            SemanticColorToken.BorderSubtle,
            alpha == 0 ? (byte)0 : (byte)Math.Max(24, alpha / 3));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
        => BindViewModel(_unloaded ? null : args.NewValue as SalesQueueViewModel);

    private void BindViewModel(SalesQueueViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.AnimationRequested -= OnAnimationRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.DetailItemsRefreshed -= SyncDetailRows;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.AnimationRequested += OnAnimationRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.DetailItemsRefreshed += SyncDetailRows;
        }

        SyncDetailRows(Array.Empty<string>());
        UpdateSpinner();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SalesQueueViewModel.IsQueueDetailPanelVisible))
        {
            if (_viewModel?.DetailItems.Count > 0) _detailWasExpanded = _viewModel.IsQueueDetailPanelVisible;
            UpdateDetailPanel();
        }
        if (args.PropertyName is nameof(SalesQueueViewModel.IsSpinnerActive) or
            nameof(SalesQueueViewModel.IsVisible))
        {
            UpdateSpinner();
        }
    }

    private void OnRowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is System.Windows.Controls.Border { DataContext: SalesQueueDetailItem { IsDeparting: true } } row)
        {
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, SalesAnimationDurations.SoldTransition));
            row.BeginAnimation(HeightProperty, new DoubleAnimation(Math.Max(0, row.ActualHeight), 0, SalesAnimationDurations.SoldTransition));
        }
    }

    private void OnRowUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is System.Windows.Controls.Border row)
        {
            row.BeginAnimation(OpacityProperty, null);
            row.BeginAnimation(HeightProperty, null);
        }
    }

    private void SyncDetailRows(IReadOnlyList<string> sold)
    {
        _departureTimer.Stop();
        _departureTimer.Tick -= FinishDepartures;
        var incoming = _viewModel?.DetailItems.ToArray() ?? Array.Empty<SalesQueueDetailItem>();
        var ids = incoming.Select(item => item.MessageId).ToHashSet(StringComparer.Ordinal);
        var departing = sold.Count > 0 && !_unloaded && IsVisible && _detailWasExpanded && SystemParameters.ClientAreaAnimation
            ? _displayRows.Where(item => !item.IsDeparting && sold.Contains(item.MessageId) && !ids.Contains(item.MessageId)).Take(30).ToArray()
            : Array.Empty<SalesQueueDetailItem>();
        _displayRows.Clear();
        foreach (var item in incoming) _displayRows.Add(item);
        foreach (var item in departing)
        {
            item.MarkDeparting();
            _displayRows.Insert(Math.Clamp(item.Position - 1, 0, _displayRows.Count), item);
        }
        if (departing.Length > 0)
        {
            _departureTimer.Tick += FinishDepartures;
            _departureTimer.Start();
        }
        UpdateDetailPanel();
        _detailWasExpanded = _viewModel?.IsQueueDetailPanelVisible == true;
    }

    private void FinishDepartures(object? sender, EventArgs args)
    {
        _departureTimer.Stop();
        _departureTimer.Tick -= FinishDepartures;
        foreach (var item in _displayRows.Where(item => item.IsDeparting).ToArray()) _displayRows.Remove(item);
        UpdateDetailPanel();
    }

    private void UpdateDetailPanel()
    {
        QueueDetailPanel.Visibility = !_unloaded &&
            (_viewModel?.IsQueueDetailPanelVisible == true || _displayRows.Any(item => item.IsDeparting))
            ? Visibility.Visible : Visibility.Collapsed;
        _ageTimer.Stop();
        _ageTimer.Tick -= UpdateAges;
        if (!_unloaded && IsVisible && QueueDetailPanel.Visibility == Visibility.Visible)
        {
            _viewModel?.RefreshRelativeAges(DateTimeOffset.UtcNow);
            _ageTimer.Tick += UpdateAges;
            _ageTimer.Start();
        }
    }

    private void UpdateAges(object? sender, EventArgs args) => _viewModel?.RefreshRelativeAges(DateTimeOffset.UtcNow);

    private void OnAnimationRequested(SalesQueueAnimationRequest request)
    {
        if (_unloaded || !IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            ResetPresentationAnimations();
            return;
        }

        ResetPresentationAnimations();
        switch (request)
        {
            case SalesQueueAnimationRequest.SoldTransition:
                ContentHost.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0.35, 1, SalesAnimationDurations.SoldTransition)
                    {
                        EasingFunction = new QuadraticEase
                        {
                            EasingMode = EasingMode.EaseOut,
                        },
                    },
                    HandoffBehavior.SnapshotAndReplace);
                break;
            case SalesQueueAnimationRequest.CurrentTurnEnter:
                AccentSweep.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimationUsingKeyFrames
                    {
                        Duration = SalesAnimationDurations.CurrentTurnEnter,
                        KeyFrames =
                        {
                            new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)),
                            new EasingDoubleKeyFrame(0.28, KeyTime.FromPercent(0.35)),
                            new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)),
                        },
                    },
                    HandoffBehavior.SnapshotAndReplace);
                break;
            case SalesQueueAnimationRequest.NextTurnEnter:
                ContentHost.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0.55, 1, SalesAnimationDurations.NextTurnEnter),
                    HandoffBehavior.SnapshotAndReplace);
                break;
        }
    }

    private void UpdateSpinner()
    {
        var shouldRun = !_unloaded &&
            IsVisible &&
            _viewModel is { IsSpinnerActive: true, IsVisible: true };
        if (!shouldRun)
        {
            StatusIcon.SetSpinnerActive(false);
            return;
        }

        StatusIcon.SetSpinnerActive(true);
    }

    private void ResetPresentationAnimations()
    {
        ContentHost.BeginAnimation(OpacityProperty, null);
        ContentHost.Opacity = 1;
        AccentSweep.BeginAnimation(OpacityProperty, null);
        AccentSweep.Opacity = 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _unloaded = false;
        BindViewModel(DataContext as SalesQueueViewModel);
        UpdateSpinner();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _unloaded = true;
        _departureTimer.Stop();
        _departureTimer.Tick -= FinishDepartures;
        _ageTimer.Stop();
        _ageTimer.Tick -= UpdateAges;
        _displayRows.Clear();
        BindViewModel(null);
        UpdateSpinner();
        ResetPresentationAnimations();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!IsVisible) FinishDepartures(null, EventArgs.Empty);
        UpdateDetailPanel();
        UpdateSpinner();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (DataContext is SalesQueueViewModel viewModel)
        {
            viewModel.UpdateAvailableWidth(args.NewSize.Width);
        }
    }
}

using System.ComponentModel;
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

    public SalesQueueView()
    {
        InitializeComponent();
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
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.AnimationRequested += OnAnimationRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateSpinner();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SalesQueueViewModel.IsSpinnerActive) or
            nameof(SalesQueueViewModel.IsVisible))
        {
            UpdateSpinner();
        }
    }

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
        BindViewModel(null);
        UpdateSpinner();
        ResetPresentationAnimations();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args) =>
        UpdateSpinner();

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (DataContext is SalesQueueViewModel viewModel)
        {
            viewModel.UpdateAvailableWidth(args.NewSize.Width);
        }
    }
}

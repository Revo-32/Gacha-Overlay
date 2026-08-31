using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Presentation;

internal sealed class SalesQueueViewModel : INotifyPropertyChanged
{
    private readonly ILocalizationService _localization;
    private SalesQueueSnapshot _snapshot = SalesQueueSnapshot.Empty;
    private SalesFeatureHealthSnapshot _health = SalesFeatureHealthSnapshot.Disabled;
    private SalesQueuePresentationState _presentation = SalesQueuePresentationState.Hidden;
    private AppSettings _settings = AppSettings.CreateDefault();
    private string _salesChannelName = "#sales";
    private double _availableWidth = 420d;
    private bool _isUltraCompact;
    private bool _isHudVisible = true;
    private bool _animationsEnabled = true;
    private bool _isVisible;
    private bool _isSecondLineVisible;
    private string _primaryLine = string.Empty;
    private string _secondaryLine = string.Empty;
    private string _accessibleStatus = string.Empty;
    private SalesObservationStatus _observationStatus = SalesObservationStatus.Unavailable;
    private SalesFeatureHealthState _healthState = SalesFeatureHealthState.Disabled;
    private SalesStatusIconKind _iconKind;
    private SalesQueueAccentKind _accentKind;
    private bool _isSpinnerActive;
    private bool _containsUnverifiedActiveItems;
    private bool _isHudUnlocked = true;
    private bool _isQueueDetailAvailable;
    private bool _isQueueDetailExpanded;
    private bool _isQueueDetailInteractive;
    private double _detailMaxHeight = 280;

    public SalesQueueViewModel(ILocalizationService localization)
    {
        _localization = localization;
        ToggleDetailCommand = new RelayCommand(ToggleDetail, () => IsQueueDetailInteractive);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<SalesQueueAnimationRequest>? AnimationRequested;

    public ObservableCollection<SalesQueueDetailItem> DetailItems { get; } = new();

    public RelayCommand ToggleDetailCommand { get; }

    public SalesQueuePresentationState Presentation => _presentation;

    public ILocalizationService Localization => _localization;

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetField(ref _isVisible, value);
    }

    public bool IsSecondLineVisible
    {
        get => _isSecondLineVisible;
        private set => SetField(ref _isSecondLineVisible, value);
    }

    public string PrimaryLine
    {
        get => _primaryLine;
        private set => SetField(ref _primaryLine, value);
    }

    public string SecondaryLine
    {
        get => _secondaryLine;
        private set => SetField(ref _secondaryLine, value);
    }

    public string AccessibleStatus
    {
        get => _accessibleStatus;
        private set => SetField(ref _accessibleStatus, value);
    }

    public SalesObservationStatus ObservationStatus
    {
        get => _observationStatus;
        private set => SetField(ref _observationStatus, value);
    }

    public SalesFeatureHealthState HealthState
    {
        get => _healthState;
        private set => SetField(ref _healthState, value);
    }

    public SalesStatusIconKind IconKind
    {
        get => _iconKind;
        private set
        {
            if (!SetField(ref _iconKind, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsLiveIndicatorVisible));
            OnPropertyChanged(nameof(IsSpinnerVisible));
            OnPropertyChanged(nameof(IsWarningVisible));
            OnPropertyChanged(nameof(IsErrorVisible));
        }
    }

    public SalesQueueAccentKind AccentKind
    {
        get => _accentKind;
        private set => SetField(ref _accentKind, value);
    }

    public bool IsSpinnerActive
    {
        get => _isSpinnerActive;
        private set => SetField(ref _isSpinnerActive, value);
    }

    public bool IsLiveIndicatorVisible => IconKind == SalesStatusIconKind.LiveDot;

    public bool IsSpinnerVisible => IconKind == SalesStatusIconKind.Spinner;

    public bool IsWarningVisible => IconKind == SalesStatusIconKind.Warning;

    public bool IsErrorVisible => IconKind == SalesStatusIconKind.Error;

    public bool ContainsUnverifiedActiveItems
    {
        get => _containsUnverifiedActiveItems;
        private set => SetField(ref _containsUnverifiedActiveItems, value);
    }

    public bool IsQueueDetailAvailable
    {
        get => _isQueueDetailAvailable;
        private set
        {
            if (SetField(ref _isQueueDetailAvailable, value))
            {
                OnPropertyChanged(nameof(IsQueueDetailPanelVisible));
            }
        }
    }

    public bool IsQueueDetailExpanded
    {
        get => _isQueueDetailExpanded;
        private set
        {
            if (SetField(ref _isQueueDetailExpanded, value))
            {
                OnPropertyChanged(nameof(IsQueueDetailPanelVisible));
                OnPropertyChanged(nameof(DetailChevronAngle));
            }
        }
    }

    public bool IsQueueDetailInteractive
    {
        get => _isQueueDetailInteractive;
        private set
        {
            if (SetField(ref _isQueueDetailInteractive, value))
            {
                ToggleDetailCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsQueueDetailPanelVisible =>
        IsQueueDetailAvailable && IsQueueDetailExpanded;

    public double DetailChevronAngle => IsQueueDetailExpanded ? 180 : 0;

    public double DetailMaxHeight
    {
        get => _detailMaxHeight;
        private set => SetField(ref _detailMaxHeight, value);
    }

    public void Apply(SalesQueueSnapshot snapshot, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Apply(
            snapshot,
            settings,
            settings.SalesTrackingEnabled
                ? CreateLegacyHealth(snapshot)
                : SalesFeatureHealthSnapshot.Disabled,
            "#sales",
            SalesQueueChangeContext.None);
    }

    public void Apply(
        SalesQueueSnapshot snapshot,
        AppSettings settings,
        SalesFeatureHealthSnapshot health,
        string salesChannelName,
        SalesQueueChangeContext change)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(change);
        _snapshot = snapshot;
        _settings = settings;
        DetailMaxHeight = ChatSettings.NormalizeQueueDetailMaxHeight(
            settings.SalesQueueDetailMaxHeight);
        _health = health;
        _salesChannelName = salesChannelName;
        Recalculate(change, allowAnimation: true);
    }

    public void UpdateAvailableWidth(double availableWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0 ||
            Math.Abs(_availableWidth - availableWidth) < 0.5d)
        {
            return;
        }

        _availableWidth = availableWidth;
        Recalculate(SalesQueueChangeContext.None, allowAnimation: false);
    }

    public void UpdateHudContext(
        bool isHudVisible,
        bool isUltraCompact,
        bool animationsEnabled,
        bool isHudUnlocked = true)
    {
        if (_isHudVisible == isHudVisible &&
            _isUltraCompact == isUltraCompact &&
            _animationsEnabled == animationsEnabled &&
            _isHudUnlocked == isHudUnlocked)
        {
            return;
        }

        _isHudVisible = isHudVisible;
        _isUltraCompact = isUltraCompact;
        _animationsEnabled = animationsEnabled;
        _isHudUnlocked = isHudUnlocked;
        Recalculate(SalesQueueChangeContext.None, allowAnimation: false);
    }

    public void RefreshLocalization() =>
        Recalculate(SalesQueueChangeContext.None, allowAnimation: false);

    private void Recalculate(SalesQueueChangeContext change, bool allowAnimation)
    {
        var strings = CreateStrings();
        var current = _snapshot.CurrentSeller;
        var next = _snapshot.NextWaitingEntry;
        var currentText = current is null
            ? string.Empty
            : Format(strings.CurrentSellerFormat, current.DisplayName);
        var waitingText = Format(strings.WaitingCountFormat, _snapshot.WaitingCount);
        var productText = current is null || current.AllProducts.Count == 0
            ? string.Empty
            : SalesProductSummaryFormatter.Format(current.AllProducts);
        var nextText = next is null
            ? string.Empty
            : Format(strings.NextSellerFormat, next.DisplayName);
        var measurements = new SalesQueueFieldMeasurements(
            Measure(currentText),
            Measure(waitingText),
            Measure(productText),
            Measure(nextText));
        var nextPresentation = SalesQueuePresentationFactory.Create(
            new SalesQueuePresentationInput(
                _snapshot,
                _health,
                new SalesQueueDisplayOptions(
                    _settings.SalesShowCurrentSeller,
                    _settings.SalesShowWaitingCount,
                    _settings.SalesShowProduct,
                    _settings.SalesShowNextWaitingUser),
                strings,
                _salesChannelName,
                Math.Max(0, _availableWidth - 48d),
                measurements,
                _presentation,
                allowAnimation ? change : SalesQueueChangeContext.None,
                _isUltraCompact,
                _isHudVisible,
                allowAnimation && _animationsEnabled));
        ApplyPresentation(nextPresentation);
    }

    private void ApplyPresentation(SalesQueuePresentationState presentation)
    {
        _presentation = presentation;
        IsVisible = presentation.IsVisible;
        IsSecondLineVisible = presentation.IsTwoLine;
        PrimaryLine = presentation.PrimaryText;
        SecondaryLine = presentation.SecondaryText;
        AccessibleStatus = presentation.AccessibleStatus;
        ObservationStatus = _snapshot.ObservationStatus;
        HealthState = _health.State;
        IconKind = presentation.IconKind;
        AccentKind = presentation.AccentKind;
        IsSpinnerActive = presentation.IsSpinnerActive;
        ContainsUnverifiedActiveItems = _snapshot.ContainsUnverifiedActiveItems;
        RefreshDetailItems();
        if (presentation.AnimationRequest != SalesQueueAnimationRequest.None)
        {
            AnimationRequested?.Invoke(presentation.AnimationRequest);
        }
    }

    private void ToggleDetail()
    {
        if (IsQueueDetailInteractive)
        {
            IsQueueDetailExpanded = !IsQueueDetailExpanded;
        }
    }

    private void RefreshDetailItems()
    {
        DetailItems.Clear();
        for (var index = 0; index < _snapshot.ActiveItems.Count; index++)
        {
            var entry = _snapshot.ActiveItems[index];
            DetailItems.Add(new SalesQueueDetailItem(
                index + 1,
                entry.DisplayName,
                SalesProductSummaryFormatter.Format(entry.AllProducts),
                index == 0,
                string.Equals(
                    entry.AuthorId,
                    _snapshot.AuthenticatedUserId,
                    StringComparison.Ordinal) ||
                (index == 0 && _snapshot.CurrentSellerIsSelf) ||
                (index == 1 && _snapshot.NextSellerIsSelf),
                entry.IsExactGuildNickname,
                _localization["SalesDetailCurrent"],
                _localization["SalesDetailSelf"]));
        }

        IsQueueDetailAvailable = IsVisible && _isHudVisible &&
            !_isUltraCompact && DetailItems.Count > 0;
        IsQueueDetailInteractive = IsQueueDetailAvailable && _isHudUnlocked;
        if (!IsQueueDetailAvailable)
        {
            IsQueueDetailExpanded = false;
        }
    }

    private SalesQueuePresentationStrings CreateStrings() => new(
        _localization["SalesHealthLiveAccessible"],
        _localization["SalesHealthConnecting"],
        _localization["SalesHealthResyncing"],
        _localization["SalesHealthOpenChannelFormat"],
        _localization["SalesHealthDegraded"],
        _localization["SalesHealthDisconnected"],
        _localization["SalesHealthSensorError"],
        _localization["SalesCurrentSellerFormat"],
        _localization["SalesWaitingCountFormat"],
        _localization["SalesProductFormat"],
        _localization["SalesNextSellerFormat"],
        _localization["SalesQueueEmpty"],
        _localization["SalesNoDisplayFields"],
        _localization["SalesNextTurnSelf"],
        _localization["SalesCurrentTurnSelf"]);

    private static SalesFeatureHealthSnapshot CreateLegacyHealth(
        SalesQueueSnapshot snapshot)
    {
        var coverage = snapshot.ObservationStatus == SalesObservationStatus.Live
            ? SalesCoverageState.Complete
            : snapshot.ObservationStatus == SalesObservationStatus.Partial
                ? SalesCoverageState.Partial
                : SalesCoverageState.None;
        var state = snapshot.ObservationStatus switch
        {
            SalesObservationStatus.Disabled => SalesFeatureHealthState.Disabled,
            SalesObservationStatus.Paused => SalesFeatureHealthState.Paused,
            SalesObservationStatus.Resyncing => SalesFeatureHealthState.Resyncing,
            SalesObservationStatus.Live => SalesFeatureHealthState.Live,
            SalesObservationStatus.Partial => SalesFeatureHealthState.Degraded,
            SalesObservationStatus.AccessibilityUnavailable or
            SalesObservationStatus.Error or
            SalesObservationStatus.Unavailable => SalesFeatureHealthState.Error,
            _ => SalesFeatureHealthState.Resyncing,
        };
        return new SalesFeatureHealthSnapshot(
            state,
            state switch
            {
                SalesFeatureHealthState.Disabled =>
                    SalesFeatureHealthReason.SalesTrackingDisabled,
                SalesFeatureHealthState.Paused =>
                    SalesFeatureHealthReason.TargetChannelNotSelected,
                SalesFeatureHealthState.Resyncing =>
                    SalesFeatureHealthReason.ResyncInProgress,
                SalesFeatureHealthState.Degraded =>
                    SalesFeatureHealthReason.CoveragePartial,
                SalesFeatureHealthState.Error =>
                    SalesFeatureHealthReason.SensorFailure,
                _ => SalesFeatureHealthReason.None,
            },
            SalesObservationReason.None,
            snapshot.ObservationStatus,
            coverage,
            state == SalesFeatureHealthState.Live,
            state == SalesFeatureHealthState.Live ? snapshot.UpdatedAt : null,
            snapshot.ActiveCount,
            snapshot.ActiveCount);
    }

    private static string Format(string format, object value) =>
        string.Format(CultureInfo.CurrentUICulture, format, value);

    private static double Measure(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0d;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12d,
            System.Windows.Media.Brushes.Transparent,
            1d);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed record SalesQueueDetailItem(
    int Position,
    string DisplayName,
    string ProductName,
    bool IsCurrent,
    bool IsSelf,
    bool IsExactGuildNickname,
    string CurrentLabel,
    string SelfLabel);

using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using LSOverlay.Protocol;

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
    private Func<ulong, SalesStatus, CancellationToken,
        Task<SalesStatusActionResponse?>>? _statusAction;
    private IReadOnlyDictionary<string, SalesCompletionObservation> _remoteEvidence =
        new Dictionary<string, SalesCompletionObservation>(StringComparer.Ordinal);
    private EffectiveSalesSource _effectiveSalesSource = EffectiveSalesSource.RemoteStarting;
    private IReadOnlyList<SalesStatusActionTarget> _statusActionTargets =
        Array.Empty<SalesStatusActionTarget>();
    private readonly Dictionary<string, PendingStatusAction> _pendingStatusActions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failedStatusActions =
        new(StringComparer.Ordinal);

    public SalesQueueViewModel(ILocalizationService localization)
    {
        _localization = localization;
        ToggleDetailCommand = new RelayCommand(ToggleDetail, () => IsQueueDetailInteractive);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<SalesQueueAnimationRequest>? AnimationRequested;
    public event Action<IReadOnlyList<string>>? DetailItemsRefreshed;
    private IReadOnlyList<string> _trustedSoldIds = Array.Empty<string>();

    public ObservableCollection<SalesQueueDetailItem> DetailItems { get; } = new();

    // Complete only the first own active post, never the current seller's post
    // when it belongs to someone else. The tooltip identifies this queue entry.
    public SalesQueueDetailItem? OwnCompletionItem { get; private set; }

    public bool IsOwnCompletionVisible => IsVisible && _isHudVisible && OwnCompletionItem is not null;

    public bool IsCompletionFeedbackVisible => IsOwnCompletionVisible &&
        !string.IsNullOrWhiteSpace(OwnCompletionItem?.StatusText);

    public string OwnCompletionHint => OwnCompletionItem is { } item
        ? string.Format(CultureInfo.CurrentCulture, _localization["SalesCompleteOwnPostHint"], item.Position, item.ProductName)
        : string.Empty;

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

    public void ConfigureStatusAction(
        Func<ulong, SalesStatus, CancellationToken,
            Task<SalesStatusActionResponse?>> action) =>
        _statusAction = action ?? throw new ArgumentNullException(nameof(action));

    public void ApplyRemoteStatusContext(
        IReadOnlyDictionary<string, SalesCompletionObservation> evidence,
        EffectiveSalesSource effectiveSource,
        IReadOnlyList<SalesStatusActionTarget>? statusActionTargets = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        _remoteEvidence = evidence;
        _effectiveSalesSource = effectiveSource;
        _statusActionTargets = statusActionTargets ??
            Array.Empty<SalesStatusActionTarget>();
        var retainedIds = evidence.Keys.Concat(_statusActionTargets.Select(target =>
            target.MessageId.ToString(CultureInfo.InvariantCulture))).ToHashSet(StringComparer.Ordinal);
        foreach (var messageId in _failedStatusActions.Keys.Where(id => !retainedIds.Contains(id)).ToArray())
        {
            _failedStatusActions.Remove(messageId);
        }
        foreach (var pending in _pendingStatusActions.ToArray())
        {
            if (!retainedIds.Contains(pending.Key))
            {
                pending.Value.Confirmation.TrySetResult(false);
                _pendingStatusActions.Remove(pending.Key);
                continue;
            }
            if (evidence.TryGetValue(pending.Key, out var observation) &&
                observation.Coverage == SalesEvidenceCoverage.Complete &&
                observation.MatchesBotStatus(pending.Value.DesiredStatus))
            {
                pending.Value.Confirmation.TrySetResult(true);
                _pendingStatusActions.Remove(pending.Key);
                _failedStatusActions.Remove(pending.Key);
            }
        }
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
        _trustedSoldIds = change.Reason == SalesQueueChangeReason.TrustedSold
            ? change.ConfirmedSoldMessageIds ?? (change.PreviousCurrentSellerMessageId is { } id ? new[] { id } : Array.Empty<string>())
            : Array.Empty<string>();
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
                Math.Max(0, _availableWidth - 48d - (HasOwnActivePost() ? 88d : 0d)),
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
            var isSelf = IsOwnActivePost(entry, index);
            _remoteEvidence.TryGetValue(entry.MessageId, out var evidence);
            var isPending = _pendingStatusActions.TryGetValue(
                entry.MessageId,
                out var pending);
            var actionEnabled = isSelf &&
                ulong.TryParse(entry.MessageId, out _) &&
                _statusAction is not null &&
                _settings.SalesTrackingEnabled &&
                _effectiveSalesSource == EffectiveSalesSource.RemotePrimary &&
                _health.State == SalesFeatureHealthState.Live &&
                _snapshot.ObservationStatus == SalesObservationStatus.Live &&
                _isHudUnlocked &&
                evidence?.BotCompletedMarkerPresent != true &&
                !isPending;
            DetailItems.Add(new SalesQueueDetailItem(
                index + 1,
                entry.MessageId,
                entry.DisplayName,
                entry.AllProducts.Count == 0 ? _localization["SalesDetailRequired"] : SalesProductSummaryFormatter.Format(entry.AllProducts),
                index == 0,
                isSelf,
                entry.IsExactGuildNickname,
                _localization["SalesDetailCurrent"],
                _localization["SalesDetailSelf"],
                actionEnabled,
                StatusText(evidence, isPending ? pending!.DesiredStatus : null,
                    _failedStatusActions.GetValueOrDefault(entry.MessageId)),
                _localization["SalesStatusCompleted"],
                status => ExecuteStatusActionAsync(entry.MessageId, status),
                entry.CreatedAt, entry.DetailSource, index == 1 && isSelf));
        }

        OwnCompletionItem = DetailItems.FirstOrDefault(item => item.IsSelf);
        OnPropertyChanged(nameof(OwnCompletionItem));
        OnPropertyChanged(nameof(IsOwnCompletionVisible));
        OnPropertyChanged(nameof(IsCompletionFeedbackVisible));
        OnPropertyChanged(nameof(OwnCompletionHint));

        IsQueueDetailAvailable = IsVisible && _isHudVisible &&
            !_isUltraCompact && DetailItems.Count > 0;
        IsQueueDetailInteractive = IsQueueDetailAvailable && _isHudUnlocked;
        if (!IsQueueDetailAvailable)
        {
            IsQueueDetailExpanded = false;
        }
        RefreshRelativeAges(DateTimeOffset.UtcNow);
        DetailItemsRefreshed?.Invoke(_animationsEnabled ? _trustedSoldIds : Array.Empty<string>());
        _trustedSoldIds = Array.Empty<string>();
    }

    public void RefreshRelativeAges(DateTimeOffset now)
    {
        foreach (var item in DetailItems) item.RefreshAge(now, _localization);
    }

    private bool HasOwnActivePost() => _snapshot.ActiveItems
        .Where((entry, index) => IsOwnActivePost(entry, index)).Any();

    private bool IsOwnActivePost(SalesQueueEntry entry, int index) =>
        !string.IsNullOrWhiteSpace(_snapshot.AuthenticatedUserId)
            ? string.Equals(entry.AuthorId, _snapshot.AuthenticatedUserId, StringComparison.Ordinal)
            : (index == 0 && _snapshot.CurrentSellerIsSelf) ||
              (index == 1 && _snapshot.NextSellerIsSelf);

    internal async Task ExecuteStatusActionAsync(string messageId, SalesStatus status)
    {
        // The product UI supports completion only. Keep legacy protocol states
        // compatible on the wire, but never dispatch them from this client UI.
        if (status != SalesStatus.Completed ||
            _statusAction is null ||
            !ulong.TryParse(messageId, out var parsedMessageId) ||
            DetailItems.FirstOrDefault(item => item.MessageId == messageId)
                is not { IsSelf: true, IsStatusActionEnabled: true } ||
            _pendingStatusActions.ContainsKey(messageId))
        {
            return;
        }

        var operationId = Guid.NewGuid();
        var confirmation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStatusActions[messageId] = new PendingStatusAction(
            operationId,
            status,
            confirmation);
        _failedStatusActions.Remove(messageId);
        RefreshDetailItems();

        SalesStatusActionResponse? response;
        try
        {
            response = await _statusAction(
                parsedMessageId,
                status,
                CancellationToken.None);
        }
        catch (Exception)
        {
            response = null;
        }

        if (!IsPending(messageId, operationId))
        {
            return;
        }

        if (response is null || response.Disposition is not (
                SalesStatusActionDisposition.Accepted or
                SalesStatusActionDisposition.NoOp))
        {
            var key = response?.Disposition switch
            {
                SalesStatusActionDisposition.RejectedUnauthorized =>
                    "SalesStatusPermissionDenied",
                SalesStatusActionDisposition.RejectedUnavailable or
                SalesStatusActionDisposition.RejectedRateLimited or null =>
                    "SalesStatusNotAvailable",
                _ => "SalesStatusActionFailed",
            };
            FailPending(messageId, operationId, _localization[key]);
            return;
        }

        if (_remoteEvidence.TryGetValue(messageId, out var current) &&
            current.Coverage == SalesEvidenceCoverage.Complete &&
            current.MatchesBotStatus(status))
        {
            _pendingStatusActions.Remove(messageId);
            confirmation.TrySetResult(true);
            RefreshDetailItems();
            return;
        }

        var completed = await Task.WhenAny(
            confirmation.Task,
            Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed != confirmation.Task && IsPending(messageId, operationId))
        {
            FailPending(
                messageId,
                operationId,
                _localization["SalesStatusActionFailed"]);
        }
    }

    private bool IsPending(string messageId, Guid operationId) =>
        _pendingStatusActions.TryGetValue(messageId, out var pending) &&
        pending.OperationId == operationId;

    private void FailPending(string messageId, Guid operationId, string failureText)
    {
        if (!IsPending(messageId, operationId))
        {
            return;
        }

        _pendingStatusActions.Remove(messageId);
        _failedStatusActions[messageId] = failureText;
        RefreshDetailItems();
    }

    private string StatusText(
        SalesCompletionObservation? evidence,
        SalesStatus? pending,
        string? failureText)
    {
        if (pending.HasValue)
        {
            return _localization["SalesStatusActionPending"];
        }

        if (!string.IsNullOrWhiteSpace(failureText))
        {
            return failureText;
        }

        if (evidence?.BotCompletedMarkerPresent == true)
        {
            return _localization["SalesStatusCompleted"];
        }

        return string.Empty;
    }

    private SalesQueuePresentationStrings CreateStrings() => new(
        _localization["SalesHealthLiveAccessible"],
        _localization["SalesHealthConnecting"],
        _localization["SalesHealthResyncing"],
        _localization["SalesHealthRemoteConnecting"],
        _localization["SalesHealthRemoteSynchronizing"],
        _localization["SalesHealthRemoteResyncing"],
        _localization["SalesHealthRemoteReconnecting"],
        _localization["SalesHealthPaused"],
        _localization["SalesHealthDegraded"],
        _localization["SalesHealthDisconnected"],
        _localization["SalesHealthRemoteError"],
        _localization["SalesCurrentSellerFormat"],
        _localization["SalesWaitingCountFormat"],
        _localization["SalesProductFormat"],
        _localization["SalesNextSellerFormat"],
        _localization["SalesQueueEmpty"],
        _localization["SalesNoDisplayFields"],
        _localization["SalesNextTurnSelf"],
        _localization["SalesCurrentTurnSelf"],
        _localization["SalesHealthRemoteUnavailable"],
        _localization["SalesHealthRemoteAccessRevoked"],
        _localization["SalesDetailRequired"]);

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

internal sealed class SalesQueueDetailItem : INotifyPropertyChanged
{
    private readonly Func<SalesStatus, Task> _action;

    public SalesQueueDetailItem(
        int position,
        string messageId,
        string displayName,
        string productName,
        bool isCurrent,
        bool isSelf,
        bool isExactGuildNickname,
        string currentLabel,
        string selfLabel,
        bool isStatusActionEnabled,
        string statusText,
        string completedLabel,
        Func<SalesStatus, Task> action,
        DateTimeOffset? createdAt = null, string? detailSource = null, bool isNextSelf = false)
    {
        CreatedAt = createdAt;
        DetailSource = detailSource;
        IsNextSelf = isNextSelf;
        Position = position;
        MessageId = messageId;
        DisplayName = displayName;
        ProductName = productName;
        IsCurrent = isCurrent;
        IsSelf = isSelf;
        IsExactGuildNickname = isExactGuildNickname;
        CurrentLabel = currentLabel;
        SelfLabel = selfLabel;
        _isStatusActionEnabled = isStatusActionEnabled;
        StatusText = statusText;
        CompletedLabel = completedLabel;
        _action = action;
        SetCompletedCommand = Command(SalesStatus.Completed);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public DateTimeOffset? CreatedAt { get; }
    public string? DetailSource { get; }
    public bool HasDetailSource => !string.IsNullOrWhiteSpace(DetailSource);
    public bool IsNextSelf { get; }
    public bool IsCurrentSelf => IsSelf && IsCurrent;
    public string RelativeAge { get; private set; } = "";
    private readonly bool _isStatusActionEnabled;
    public bool IsDeparting { get; private set; }
    public void MarkDeparting()
    {
        IsDeparting = true;
        foreach (var name in new[] { nameof(IsDeparting), nameof(IsStatusActionEnabled), nameof(IsStatusActionVisible) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public void RefreshAge(DateTimeOffset now, ILocalizationService localization)
    {
        var minutes = CreatedAt.HasValue ? Math.Max(0, (now - CreatedAt.Value).TotalMinutes) : -1;
        var label = minutes < 0 ? "" : minutes < 1 ? localization["SalesAgeJustNow"] :
            string.Format(CultureInfo.CurrentUICulture, localization[minutes < 60 ? "SalesAgeMinutes" : "SalesAgeHours"],
                (int)(minutes < 60 ? minutes : minutes / 60));
        if (RelativeAge == label) return;
        RelativeAge = label;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RelativeAge)));
    }
    public int Position { get; }
    public string MessageId { get; }
    public string DisplayName { get; }
    public string ProductName { get; }
    public bool IsCurrent { get; }
    public bool IsSelf { get; }
    public bool IsExactGuildNickname { get; }
    public string CurrentLabel { get; }
    public string SelfLabel { get; }
    public bool IsStatusActionVisible => IsSelf && !IsDeparting;
    public bool IsStatusActionEnabled => _isStatusActionEnabled && !IsDeparting;
    public string StatusText { get; }
    public string CompletedLabel { get; }
    public AsyncRelayCommand SetCompletedCommand { get; }

    private AsyncRelayCommand Command(SalesStatus status) =>
        new(() => _action(status), () => IsStatusActionEnabled);
}

internal sealed record PendingStatusAction(
    Guid OperationId,
    SalesStatus DesiredStatus,
    TaskCompletionSource<bool> Confirmation);

internal sealed record SalesStatusActionTarget(
    string MessageId,
    string DisplayName,
    string ProductName,
    bool IsExactGuildNickname);

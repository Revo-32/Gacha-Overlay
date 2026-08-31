namespace GachaOverlay.Core.Sales;

public enum SaleReactionOutcome
{
    NotObserved,
    Sold,
    NotSold,
}

public enum SalesObservationCompleteness
{
    Partial,
    Full,
}

public enum SalesCoverageState
{
    None,
    Partial,
    Complete,
}

public enum SalesObservationReason
{
    None,
    SalesTrackingDisabled,
    SourceNotReady,
    DiscordNotRunning,
    DiscordWindowNotFound,
    AccessibilityTreeUnavailable,
    TargetChannelNotSelected,
    TargetChannelUnknown,
    CoverageIncomplete,
    SourceChangedDuringScan,
    ElementUnavailable,
    WindowChanged,
    ScanFailed,
}

public enum SalesTargetChannelStatus
{
    Unknown,
    Selected,
    NotSelected,
}

public enum SalesChannelEvidenceSource
{
    None,
    WindowTitleExact,
    ChannelIdAnchor,
    SelectedChannelItem,
    MessageContainerChannelId,
}

public enum ManualSalesResyncResult
{
    Requested,
    Coalesced,
    TrackingDisabled,
    DiscordDisconnected,
    TargetChannelUnavailable,
}

public sealed record SaleReactionObservation(
    string MessageId,
    SaleReactionOutcome Outcome,
    bool HasTrustedEvidence,
    DateTimeOffset ObservedAt,
    long Generation,
    long? SourceRevision = null);

public sealed record SalesObservationBatch(
    long Generation,
    DateTimeOffset ObservedAt,
    SalesObservationStatus SensorStatus,
    bool IsTrusted,
    SalesObservationCompleteness Completeness,
    IReadOnlyCollection<SaleReactionObservation> Observations,
    SalesCoverageState Coverage = SalesCoverageState.None,
    SalesObservationReason StatusReason = SalesObservationReason.None,
    int TargetMessageCount = 0,
    int ObservedMessageCount = 0,
    int SoldCount = 0,
    int NotSoldCount = 0,
    int NotObservedCount = 0,
    long TargetSetRevision = 0);

public sealed record SalesObservationTarget(
    string MessageId,
    long SourceRevision);

public sealed record SalesObservationTargetSet(
    long Revision,
    long SourceGeneration,
    bool IsSourceReady,
    string SalesChannelId,
    string SalesChannelName,
    IReadOnlyList<SalesObservationTarget> Targets)
{
    public static SalesObservationTargetSet Empty { get; } = new(
        0,
        0,
        false,
        string.Empty,
        string.Empty,
        Array.Empty<SalesObservationTarget>());
}

public sealed record SalesSensorHealth(
    SalesObservationStatus Status,
    SalesObservationReason Reason,
    SalesCoverageState Coverage,
    bool IsComplete,
    DateTimeOffset? LastSuccessfulScanAt,
    DateTimeOffset? LastCompleteResyncAt,
    int TargetMessageCount,
    int ObservedMessageCount,
    int SoldCount,
    int NotSoldCount,
    int NotObservedCount,
    bool DiscordWindowAvailable,
    bool AccessibilityReady,
    SalesTargetChannelStatus TargetChannelStatus,
    SalesChannelEvidenceSource ChannelEvidenceSource,
    long WindowHandle,
    int DiscordProcessId,
    long SessionGeneration,
    long ScanGeneration,
    long TargetSetRevision,
    int ScannedNodeCount,
    int ReactionGroupCount,
    long ScanDurationMilliseconds,
    int CoalescedRequestCount,
    int WindowReacquisitionCount,
    int UiaExceptionCount)
{
    public static SalesSensorHealth Disabled { get; } = new(
        SalesObservationStatus.Disabled,
        SalesObservationReason.SalesTrackingDisabled,
        SalesCoverageState.None,
        false,
        null,
        null,
        0,
        0,
        0,
        0,
        0,
        false,
        false,
        SalesTargetChannelStatus.Unknown,
        SalesChannelEvidenceSource.None,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0);
}

public interface ISalesReactionObservationSource : IDisposable
{
    event Action<SalesObservationBatch>? BatchAvailable;

    event Action<SalesSensorHealth>? HealthChanged;

    SalesObservationStatus Status { get; }

    SalesSensorHealth Health { get; }

    bool IsRunning { get; }

    void UpdateTargets(SalesObservationTargetSet targetSet);

    void Start();

    void Stop();

    void RequestFullResync();
}

public sealed class UnavailableSalesReactionObservationSource : ISalesReactionObservationSource
{
    public event Action<SalesObservationBatch>? BatchAvailable
    {
        add { }
        remove { }
    }

    public event Action<SalesSensorHealth>? HealthChanged
    {
        add { }
        remove { }
    }

    public SalesObservationStatus Status => SalesObservationStatus.Unavailable;

    public SalesSensorHealth Health => SalesSensorHealth.Disabled with
    {
        Status = SalesObservationStatus.Unavailable,
        Reason = SalesObservationReason.AccessibilityTreeUnavailable,
    };

    public bool IsRunning { get; private set; }

    public void UpdateTargets(SalesObservationTargetSet targetSet) =>
        ArgumentNullException.ThrowIfNull(targetSet);

    public void Start() => IsRunning = true;

    public void Stop() => IsRunning = false;

    public void RequestFullResync()
    {
        // M6 supplies the first production implementation. Unavailable never claims a resync.
    }

    public void Dispose() => Stop();
}

public sealed class MockSalesReactionObservationSource : ISalesReactionObservationSource
{
    private bool _disposed;
    private SalesSensorHealth _health = SalesSensorHealth.Disabled;

    public event Action<SalesObservationBatch>? BatchAvailable;

    public event Action<SalesSensorHealth>? HealthChanged;

    public SalesObservationStatus Status { get; private set; } =
        SalesObservationStatus.Unavailable;

    public bool IsRunning { get; private set; }

    public SalesSensorHealth Health => _health;

    public SalesObservationTargetSet Targets { get; private set; } =
        SalesObservationTargetSet.Empty;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int ResyncRequestCount { get; private set; }

    public int TargetUpdateCount { get; private set; }

    public void UpdateTargets(SalesObservationTargetSet targetSet)
    {
        ArgumentNullException.ThrowIfNull(targetSet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Targets = targetSet;
        TargetUpdateCount++;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        StartCount++;
        Status = SalesObservationStatus.Resyncing;
        SetHealth(_health with
        {
            Status = SalesObservationStatus.Resyncing,
            Reason = SalesObservationReason.None,
            SessionGeneration = _health.SessionGeneration + 1,
        });
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        StopCount++;
        Status = SalesObservationStatus.Disabled;
        SetHealth(SalesSensorHealth.Disabled with
        {
            SessionGeneration = _health.SessionGeneration,
        });
    }

    public void RequestFullResync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ResyncRequestCount++;
        Status = SalesObservationStatus.Resyncing;
        SetHealth(_health with
        {
            Status = SalesObservationStatus.Resyncing,
            Reason = SalesObservationReason.None,
        });
    }

    public bool Publish(SalesObservationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsRunning)
        {
            return false;
        }

        Status = batch.SensorStatus;
        SetHealth(_health with
        {
            Status = batch.SensorStatus,
            Reason = batch.StatusReason,
            Coverage = batch.Coverage,
            IsComplete = batch.Coverage == SalesCoverageState.Complete,
            LastSuccessfulScanAt = batch.IsTrusted ? batch.ObservedAt : _health.LastSuccessfulScanAt,
            LastCompleteResyncAt = batch.Coverage == SalesCoverageState.Complete
                ? batch.ObservedAt
                : _health.LastCompleteResyncAt,
            TargetMessageCount = batch.TargetMessageCount,
            ObservedMessageCount = batch.ObservedMessageCount,
            SoldCount = batch.SoldCount,
            NotSoldCount = batch.NotSoldCount,
            NotObservedCount = batch.NotObservedCount,
            ScanGeneration = batch.Generation,
            TargetSetRevision = batch.TargetSetRevision,
        });
        BatchAvailable?.Invoke(batch);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        BatchAvailable = null;
        HealthChanged = null;
        _disposed = true;
    }

    private void SetHealth(SalesSensorHealth health)
    {
        _health = health;
        HealthChanged?.Invoke(health);
    }
}

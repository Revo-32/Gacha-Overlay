using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.App.Services.Sales;

internal sealed record DiscordUiaSensorOptions(
    TimeSpan PollInterval,
    TimeSpan AccessibilityRetryInterval,
    TimeSpan UnavailableInitialRetryInterval,
    TimeSpan UnavailableMaximumRetryInterval,
    TimeSpan ShutdownJoinTimeout)
{
    public static DiscordUiaSensorOptions Default { get; } = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(5));
}

internal sealed class DiscordUiaSalesReactionObservationSource :
    ISalesReactionObservationSource
{
    private readonly object _sync = new();
    private readonly IDiscordAccessibilityAdapter _adapter;
    private readonly IAppLogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly DiscordUiaSensorOptions _options;
    private readonly IRuntimeMetrics? _metrics;
    private readonly AutoResetEvent _scanRequested = new(false);
    private readonly Dictionary<string, SaleReactionOutcome> _lastLoggedOutcomes =
        new(StringComparer.Ordinal);
    private SalesObservationTargetSet _targets = SalesObservationTargetSet.Empty;
    private SalesSensorHealth _health = SalesSensorHealth.Disabled;
    private CancellationTokenSource? _shutdown;
    private Thread? _worker;
    private bool _running;
    private bool _disposed;
    private bool _fullResyncRequested;
    private int _requestPending;
    private int _coalescedRequestCount;
    private int _unavailableRetryExponent;
    private long _sessionGeneration;
    private long _scanGeneration;
    private long _lastWindowHandle;
    private string _lastSummary = string.Empty;
    private int _unchangedSummaryCount;

    public DiscordUiaSalesReactionObservationSource(
        IDiscordAccessibilityAdapter adapter,
        IAppLogger? logger = null,
        Func<DateTimeOffset>? clock = null,
        DiscordUiaSensorOptions? options = null,
        IRuntimeMetrics? metrics = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger ?? NullAppLogger.Instance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _options = options ?? DiscordUiaSensorOptions.Default;
        _metrics = metrics;
        ValidateOptions(_options);
    }

    public event Action<SalesObservationBatch>? BatchAvailable;

    public event Action<SalesSensorHealth>? HealthChanged;

    public SalesObservationStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _health.Status;
            }
        }
    }

    public SalesSensorHealth Health
    {
        get
        {
            lock (_sync)
            {
                return _health;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    public void UpdateTargets(SalesObservationTargetSet targetSet)
    {
        ArgumentNullException.ThrowIfNull(targetSet);
        var immutable = targetSet with
        {
            Targets = targetSet.Targets
                .GroupBy(target => target.MessageId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderBy(target => target.MessageId, StringComparer.Ordinal)
                .ToArray(),
        };
        bool publishResyncing;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _targets = immutable;
            _fullResyncRequested = true;
            publishResyncing = _running;
        }

        if (publishResyncing)
        {
            PublishStatusBatch(
                SalesObservationStatus.Resyncing,
                immutable.IsSourceReady
                    ? SalesObservationReason.None
                    : SalesObservationReason.SourceNotReady);
        }

        RequestScan();
    }

    public void Start()
    {
        SalesSensorHealth health;
        Thread worker;
        SalesObservationReason initialReason;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _running = true;
            _fullResyncRequested = true;
            _shutdown = new CancellationTokenSource();
            _sessionGeneration++;
            _unavailableRetryExponent = 0;
            _lastWindowHandle = 0;
            _lastSummary = string.Empty;
            _unchangedSummaryCount = 0;
            _lastLoggedOutcomes.Clear();
            _adapter.ResetSession();
            initialReason = _targets.IsSourceReady
                ? SalesObservationReason.None
                : SalesObservationReason.SourceNotReady;
            _health = SalesSensorHealth.Disabled with
            {
                Status = SalesObservationStatus.Resyncing,
                Reason = initialReason,
                SessionGeneration = _sessionGeneration,
                ScanGeneration = _scanGeneration,
                TargetSetRevision = _targets.Revision,
                TargetMessageCount = _targets.Targets.Count,
            };
            health = _health;
            worker = new Thread(() => WorkerLoop(_shutdown.Token, _sessionGeneration))
            {
                IsBackground = true,
                Name = "GachaOverlay Discord UIA Sales Sensor",
            };
            worker.SetApartmentState(ApartmentState.STA);
            _worker = worker;
        }

        _logger.Information("UIA", "Sales sensor starting mode=Polling intervalMs=1000.");
        PublishHealth(health);
        PublishStatusBatch(
            SalesObservationStatus.Resyncing,
            initialReason);
        worker.Start();
        RequestScan();
    }

    public void Stop()
    {
        Thread? worker;
        CancellationTokenSource? shutdown;
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            worker = _worker;
            shutdown = _shutdown;
            _worker = null;
            _shutdown = null;
            _requestPending = 0;
            _fullResyncRequested = false;
        }

        shutdown?.Cancel();
        _scanRequested.Set();
        if (worker is not null && worker != Thread.CurrentThread &&
            !worker.Join(_options.ShutdownJoinTimeout))
        {
            _logger.Warning(
                "UIA",
                "Sales sensor worker did not join before timeout; background thread will not keep the process alive.");
        }

        shutdown?.Dispose();
        _adapter.ResetSession();
        SalesSensorHealth health;
        lock (_sync)
        {
            _lastLoggedOutcomes.Clear();
            _lastWindowHandle = 0;
            _health = SalesSensorHealth.Disabled with
            {
                SessionGeneration = _sessionGeneration,
                ScanGeneration = _scanGeneration,
                TargetSetRevision = _targets.Revision,
                TargetMessageCount = _targets.Targets.Count,
                CoalescedRequestCount = _coalescedRequestCount,
            };
            health = _health;
        }

        _logger.Information("UIA", "Sales sensor stopped reason=SalesTrackingDisabled.");
        _metrics?.SetGauge(RuntimeMetricNames.UiaScanInProgress, 0);
        _metrics?.SetState(RuntimeMetricNames.UiaState, SalesObservationStatus.Disabled.ToString());
        PublishHealth(health);
    }

    public void RequestFullResync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _fullResyncRequested = true;
            if (!_running)
            {
                return;
            }
        }

        PublishStatusBatch(
            SalesObservationStatus.Resyncing,
            SalesObservationReason.None);
        RequestScan();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
        }

        Stop();
        lock (_sync)
        {
            _disposed = true;
            BatchAvailable = null;
            HealthChanged = null;
        }

        _adapter.Dispose();
        _scanRequested.Dispose();
    }

    private void WorkerLoop(CancellationToken cancellationToken, long sessionGeneration)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _metrics?.Increment(RuntimeMetricNames.UiaPolls);
                _ = Interlocked.Exchange(ref _requestPending, 0);
                SalesObservationTargetSet targetSet;
                bool fullResync;
                lock (_sync)
                {
                    if (!_running || sessionGeneration != _sessionGeneration)
                    {
                        return;
                    }

                    targetSet = _targets;
                    fullResync = _fullResyncRequested;
                    _fullResyncRequested = false;
                }

                if (!targetSet.IsSourceReady)
                {
                    PublishStatusBatch(
                        SalesObservationStatus.Resyncing,
                        SalesObservationReason.SourceNotReady);
                }
                else
                {
                    RunScan(targetSet, fullResync, sessionGeneration, cancellationToken);
                }

                var delay = GetNextDelay();
                var signaled = WaitHandle.WaitAny(
                    new[] { cancellationToken.WaitHandle, _scanRequested },
                    delay);
                if (signaled == 0)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("UIA", "Sales sensor worker failed.", exception);
            PublishStatusBatch(
                SalesObservationStatus.Error,
                SalesObservationReason.ScanFailed);
        }
    }

    private void RunScan(
        SalesObservationTargetSet targetSet,
        bool fullResync,
        long sessionGeneration,
        CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _scanGeneration);
        _metrics?.Increment(RuntimeMetricNames.UiaScans);
        _metrics?.SetGauge(RuntimeMetricNames.UiaScanInProgress, 1);
        if (fullResync)
        {
            _logger.Information(
                "UIA",
                $"Resync started generation={generation} target={targetSet.Targets.Count}.");
        }

        DiscordAccessibilitySnapshot snapshot;
        try
        {
            snapshot = _adapter.Scan(
                new DiscordAccessibilityScanRequest(
                    sessionGeneration,
                    generation,
                    targetSet,
                    fullResync),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _metrics?.SetGauge(RuntimeMetricNames.UiaScanInProgress, 0);
            throw;
        }
        catch (Exception exception)
        {
            _metrics?.Increment(RuntimeMetricNames.UiaFailedScans);
            _metrics?.SetGauge(RuntimeMetricNames.UiaScanInProgress, 0);
            _logger.Warning(
                "UIA",
                $"Scan failed exception={exception.GetType().Name}; session will be reacquired.");
            _adapter.ResetSession();
            PublishStatusBatch(
                SalesObservationStatus.Error,
                SalesObservationReason.ScanFailed);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_running || sessionGeneration != _sessionGeneration)
            {
                _metrics?.SetGauge(RuntimeMetricNames.UiaScanInProgress, 0);
                return;
            }
        }

        var batch = DiscordSalesObservationInterpreter.Interpret(
            snapshot,
            targetSet,
            generation,
            _clock());
        SalesObservationTargetSet latestTargets;
        lock (_sync)
        {
            latestTargets = _targets;
        }

        if (batch.IsTrusted && latestTargets.Revision != targetSet.Revision)
        {
            var trustedCount = batch.Observations.Count(observation =>
                observation.HasTrustedEvidence &&
                observation.Outcome != SaleReactionOutcome.NotObserved);
            batch = batch with
            {
                SensorStatus = SalesObservationStatus.Partial,
                Completeness = SalesObservationCompleteness.Partial,
                Coverage = trustedCount > 0
                    ? SalesCoverageState.Partial
                    : SalesCoverageState.None,
                StatusReason = SalesObservationReason.SourceChangedDuringScan,
            };
            lock (_sync)
            {
                _fullResyncRequested = true;
            }

            RequestScan();
        }

        var previousStatus = Status;
        if ((snapshot.WindowChanged ||
                previousStatus is SalesObservationStatus.Paused or
                SalesObservationStatus.Unavailable or
                SalesObservationStatus.AccessibilityUnavailable or
                SalesObservationStatus.Error) &&
            batch.SensorStatus is SalesObservationStatus.Live or SalesObservationStatus.Partial)
        {
            PublishStatusBatch(
                SalesObservationStatus.Resyncing,
                snapshot.WindowChanged
                    ? SalesObservationReason.WindowChanged
                    : SalesObservationReason.None);
        }

        PublishBatch(batch, snapshot, sessionGeneration);
        _metrics?.SetGauge(RuntimeMetricNames.UiaScanInProgress, 0);
    }

    private void PublishBatch(
        SalesObservationBatch batch,
        DiscordAccessibilitySnapshot snapshot,
        long sessionGeneration)
    {
        SalesSensorHealth health;
        var now = batch.ObservedAt;
        lock (_sync)
        {
            var lastSuccessful = batch.IsTrusted
                ? now
                : _health.LastSuccessfulScanAt;
            var lastComplete = batch.Coverage == SalesCoverageState.Complete &&
                _health.Status != SalesObservationStatus.Live
                ? now
                : _health.LastCompleteResyncAt;
            _health = new SalesSensorHealth(
                batch.SensorStatus,
                batch.StatusReason,
                batch.Coverage,
                batch.Coverage == SalesCoverageState.Complete,
                lastSuccessful,
                lastComplete,
                batch.TargetMessageCount,
                batch.ObservedMessageCount,
                batch.SoldCount,
                batch.NotSoldCount,
                batch.NotObservedCount,
                snapshot.WindowAvailable,
                snapshot.AccessibilityReady,
                snapshot.TargetChannelStatus,
                snapshot.ChannelEvidenceSource,
                snapshot.WindowHandle,
                snapshot.ProcessId,
                sessionGeneration,
                batch.Generation,
                batch.TargetSetRevision,
                snapshot.ScannedNodeCount,
                snapshot.ReactionGroupCount,
                snapshot.ScanDurationMilliseconds,
                _coalescedRequestCount,
                snapshot.WindowReacquisitionCount,
                snapshot.UiaExceptionCount);
            health = _health;
            _unavailableRetryExponent = batch.SensorStatus is
                SalesObservationStatus.Unavailable or
                SalesObservationStatus.AccessibilityUnavailable or
                SalesObservationStatus.Error
                    ? Math.Min(_unavailableRetryExponent + 1, 8)
                    : 0;
        }

        _metrics?.RecordDuration(
            RuntimeMetricNames.UiaScanDuration,
            TimeSpan.FromMilliseconds(Math.Max(0, snapshot.ScanDurationMilliseconds)));
        _metrics?.SetGauge(RuntimeMetricNames.UiaScannedNodes, snapshot.ScannedNodeCount);
        _metrics?.SetState(RuntimeMetricNames.UiaState, batch.SensorStatus.ToString());
        _metrics?.Increment(batch.SensorStatus switch
        {
            SalesObservationStatus.Live when batch.Coverage == SalesCoverageState.Complete =>
                RuntimeMetricNames.UiaCompleteScans,
            SalesObservationStatus.Live or SalesObservationStatus.Partial =>
                RuntimeMetricNames.UiaPartialScans,
            _ => RuntimeMetricNames.UiaUnavailableScans,
        });
        if (batch.IsTrusted && batch.Coverage == SalesCoverageState.Complete)
        {
            _metrics?.SetGauge(
                RuntimeMetricNames.UiaLastCompleteUnixSeconds,
                now.ToUnixTimeSeconds());
        }

        LogWindowAndScan(batch, snapshot);
        PublishHealth(health);
        InvokeBatchAvailable(batch);
    }

    private void PublishStatusBatch(
        SalesObservationStatus status,
        SalesObservationReason reason)
    {
        SalesObservationTargetSet targets;
        long generation;
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            targets = _targets;
            generation = _scanGeneration;
        }

        var batch = new SalesObservationBatch(
            generation,
            _clock(),
            status,
            false,
            SalesObservationCompleteness.Partial,
            Array.Empty<SaleReactionObservation>(),
            SalesCoverageState.None,
            reason,
            targets.Targets.Count,
            0,
            0,
            0,
            targets.Targets.Count,
            targets.Revision);
        SalesSensorHealth health;
        lock (_sync)
        {
            _unavailableRetryExponent = status is
                SalesObservationStatus.Unavailable or
                SalesObservationStatus.AccessibilityUnavailable or
                SalesObservationStatus.Error
                    ? Math.Min(_unavailableRetryExponent + 1, 8)
                    : status == SalesObservationStatus.Resyncing
                        ? _unavailableRetryExponent
                        : 0;
            _health = _health with
            {
                Status = status,
                Reason = reason,
                Coverage = SalesCoverageState.None,
                IsComplete = false,
                TargetMessageCount = targets.Targets.Count,
                ObservedMessageCount = 0,
                NotObservedCount = targets.Targets.Count,
                ScanGeneration = generation,
                TargetSetRevision = targets.Revision,
            };
            health = _health;
        }

        PublishHealth(health);
        InvokeBatchAvailable(batch);
    }

    private void RequestScan()
    {
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }
        }

        if (Interlocked.Exchange(ref _requestPending, 1) == 1)
        {
            Interlocked.Increment(ref _coalescedRequestCount);
            _metrics?.Increment(RuntimeMetricNames.UiaCoalescedPolls);
        }

        _scanRequested.Set();
    }

    private TimeSpan GetNextDelay()
    {
        SalesObservationStatus status;
        int exponent;
        lock (_sync)
        {
            status = _health.Status;
            exponent = _unavailableRetryExponent;
        }

        return status switch
        {
            SalesObservationStatus.Live or
            SalesObservationStatus.Partial or
            SalesObservationStatus.Paused or
            SalesObservationStatus.Resyncing => _options.PollInterval,
            SalesObservationStatus.AccessibilityUnavailable =>
                _options.AccessibilityRetryInterval,
            _ => TimeSpan.FromMilliseconds(Math.Min(
                _options.UnavailableMaximumRetryInterval.TotalMilliseconds,
                _options.UnavailableInitialRetryInterval.TotalMilliseconds *
                Math.Pow(2, Math.Max(0, exponent - 1)))),
        };
    }

    private void LogWindowAndScan(
        SalesObservationBatch batch,
        DiscordAccessibilitySnapshot snapshot)
    {
        if (snapshot.WindowAvailable && snapshot.WindowHandle != _lastWindowHandle)
        {
            if (_lastWindowHandle == 0)
            {
                _logger.Information(
                    "UIA",
                    $"Discord window acquired pid={snapshot.ProcessId} hwnd=0x{snapshot.WindowHandle:X}.");
            }
            else
            {
                _logger.Information(
                    "UIA",
                    $"Discord window changed old=0x{_lastWindowHandle:X} new=0x{snapshot.WindowHandle:X}.");
            }

            _lastWindowHandle = snapshot.WindowHandle;
        }
        else if (!snapshot.WindowAvailable)
        {
            _lastWindowHandle = 0;
        }

        var summary = string.Join(
            '|',
            batch.SensorStatus,
            batch.StatusReason,
            batch.TargetMessageCount,
            batch.ObservedMessageCount,
            batch.SoldCount,
            batch.NotSoldCount,
            batch.NotObservedCount,
            batch.Coverage,
            snapshot.ChannelEvidenceSource,
            snapshot.WindowHandle);
        _unchangedSummaryCount++;
        if (!string.Equals(summary, _lastSummary, StringComparison.Ordinal) ||
            _unchangedSummaryCount >= 30)
        {
            _logger.Information(
                "UIA",
                $"Scan completed generation={batch.Generation} status={batch.SensorStatus} reason={batch.StatusReason} target={batch.TargetMessageCount} observed={batch.ObservedMessageCount} reactionGroups={snapshot.ReactionGroupCount} sold={batch.SoldCount} notSold={batch.NotSoldCount} notObserved={batch.NotObservedCount} coverage={batch.Coverage} channel={snapshot.TargetChannelStatus} evidence={snapshot.ChannelEvidenceSource} nodes={snapshot.ScannedNodeCount} durationMs={snapshot.ScanDurationMilliseconds}.");
            _lastSummary = summary;
            _unchangedSummaryCount = 0;
            var notObservedSample = batch.Observations
                .Where(observation =>
                    observation.Outcome == SaleReactionOutcome.NotObserved)
                .Take(5)
                .Select(observation => Sanitize(observation.MessageId))
                .ToArray();
            if (notObservedSample.Length > 0)
            {
                _logger.Information(
                    "UIA",
                    $"NotObserved sample={string.Join(',', notObservedSample)}.");
            }
        }

        foreach (var observation in batch.Observations.Where(observation =>
                     observation.HasTrustedEvidence &&
                     observation.Outcome != SaleReactionOutcome.NotObserved))
        {
            if (_lastLoggedOutcomes.TryGetValue(observation.MessageId, out var previous) &&
                previous == observation.Outcome)
            {
                continue;
            }

            _lastLoggedOutcomes[observation.MessageId] = observation.Outcome;
            _logger.Information(
                "UIA",
                $"Observation {observation.Outcome} message={Sanitize(observation.MessageId)} generation={observation.Generation}.");
        }
    }

    private void InvokeBatchAvailable(SalesObservationBatch batch)
    {
        try
        {
            BatchAvailable?.Invoke(batch);
        }
        catch (Exception exception)
        {
            _logger.Error("UIA", "Sales observation subscriber failed.", exception);
        }
    }

    private void PublishHealth(SalesSensorHealth health)
    {
        try
        {
            HealthChanged?.Invoke(health);
        }
        catch (Exception exception)
        {
            _logger.Error("UIA", "Sales sensor health subscriber failed.", exception);
        }
    }

    private static void ValidateOptions(DiscordUiaSensorOptions options)
    {
        if (options.PollInterval <= TimeSpan.Zero ||
            options.AccessibilityRetryInterval <= TimeSpan.Zero ||
            options.UnavailableInitialRetryInterval <= TimeSpan.Zero ||
            options.UnavailableMaximumRetryInterval < options.UnavailableInitialRetryInterval ||
            options.ShutdownJoinTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static string Sanitize(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }
}

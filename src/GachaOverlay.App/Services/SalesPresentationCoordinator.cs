using System.Diagnostics;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Hud.Presentation;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Services;

internal sealed class SalesPresentationCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly SalesStateEngine _engine;
    private readonly ISalesReactionObservationSource _observationSource;
    private readonly SalesQueueViewModel _viewModel;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly UiUpdateCoalescer _uiUpdates;
    private IReadOnlyList<NormalizedDiscordMessage> _latestSource =
        Array.Empty<NormalizedDiscordMessage>();
    private SalesObservationTargetSet _observationTargets =
        SalesObservationTargetSet.Empty;
    private SalesQueueSnapshot _pendingSnapshot;
    private SalesSensorHealth _sensorHealth = SalesSensorHealth.Disabled;
    private DiscordConnectionStatus _rpcStatus = DiscordConnectionStatus.Initial;
    private SalesQueueChangeContext _pendingChange = SalesQueueChangeContext.None;
    private SalesQueueChangeContext _publishingChange = SalesQueueChangeContext.None;
    private SalesFeatureHealthSnapshot? _lastHealth;
    private SalesQueuePresentationState? _lastPresentation;
    private AppSettings _settings;
    private string _salesChannelId;
    private string _salesChannelName = DiscordTargetOptions.DefaultSalesChannelName;
    private long _sourceGeneration;
    private long _targetSetRevision;
    private bool _sourceReady;
    private bool _sourceSubscribed;
    private bool _started;
    private bool _disposed;
    private readonly IRuntimeMetrics? _metrics;

    public SalesPresentationCoordinator(
        SalesStateEngine engine,
        ISalesReactionObservationSource observationSource,
        SalesQueueViewModel viewModel,
        ILocalizationService localization,
        IAppLogger logger,
        AppSettings initialSettings,
        System.Windows.Threading.Dispatcher dispatcher,
        IRuntimeMetrics? metrics = null)
    {
        _engine = engine;
        _observationSource = observationSource;
        _viewModel = viewModel;
        _localization = localization;
        _logger = logger;
        _settings = initialSettings;
        _salesChannelId = initialSettings.DiscordSalesChannelId ?? string.Empty;
        _pendingSnapshot = engine.Current;
        _metrics = metrics;
        _uiUpdates = new UiUpdateCoalescer(
            new DispatcherCallbackScheduler(dispatcher),
            requestCount =>
            {
                if (requestCount > 1)
                {
                    _metrics?.Increment(RuntimeMetricNames.UiUpdatesCoalesced, requestCount - 1);
                }

                ApplyPendingSnapshot();
            },
            exception => logger.Error("SALES", "Sales presentation update failed.", exception));
    }

    public SalesStateEngine Engine => _engine;

    public SalesFeatureHealthSnapshot GetHealthSnapshot()
    {
        lock (_sync)
        {
            return _lastHealth ?? SalesFeatureHealthSnapshot.Disabled;
        }
    }

    public IReadOnlyList<SalesEmojiInventoryItem> GetEmojiInventory()
    {
        IReadOnlyList<NormalizedDiscordMessage> source;
        lock (_sync)
        {
            source = _latestSource;
        }

        var products = _engine.ProductCatalog.Products;
        return source
            .SelectMany(message => message.CustomEmojis.Select(emoji => new
            {
                Message = message,
                Emoji = emoji,
            }))
            .GroupBy(
                item => (item.Message.GuildId, item.Emoji.EmojiId))
            .Select(group =>
            {
                var representative = group.First().Emoji;
                return new SalesEmojiInventoryItem(
                    group.Key.EmojiId,
                    representative.Name,
                    group.Key.GuildId,
                    representative.Animated,
                    group.Count(),
                    products.Any(product =>
                        string.Equals(product.EmojiId, group.Key.EmojiId, StringComparison.Ordinal) &&
                        (string.IsNullOrWhiteSpace(product.GuildId) ||
                         string.Equals(product.GuildId, group.Key.GuildId, StringComparison.Ordinal))));
            })
            .OrderBy(item => item.EmojiName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.EmojiId, StringComparer.Ordinal)
            .ToArray();
    }

    public void ReplaceProductCatalog(SalesProductCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _engine.ReplaceProductCatalog(catalog);
        IReadOnlyList<NormalizedDiscordMessage> source;
        lock (_sync)
        {
            source = _latestSource;
        }

        _engine.RemapProducts(source);
        _uiUpdates.Request();
    }

    public ManualSalesResyncResult RequestManualResync()
    {
        _metrics?.Increment(RuntimeMetricNames.SalesManualResync);
        lock (_sync)
        {
            if (!_settings.SalesTrackingEnabled)
            {
                return ManualSalesResyncResult.TrackingDisabled;
            }

            if (_rpcStatus.State != DiscordConnectionState.Connected)
            {
                return ManualSalesResyncResult.DiscordDisconnected;
            }

            if (string.IsNullOrWhiteSpace(_salesChannelId) ||
                _sensorHealth.TargetChannelStatus == SalesTargetChannelStatus.NotSelected)
            {
                return ManualSalesResyncResult.TargetChannelUnavailable;
            }

            if (_sensorHealth.Status == SalesObservationStatus.Resyncing)
            {
                _metrics?.Increment(RuntimeMetricNames.SalesResyncAttempts);
                _observationSource.RequestFullResync();
                return ManualSalesResyncResult.Coalesced;
            }
        }

        _metrics?.Increment(RuntimeMetricNames.SalesResyncAttempts);
        _observationSource.RequestFullResync();
        _logger.Information("SALES", "Manual full resync requested from Settings.");
        return ManualSalesResyncResult.Requested;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _engine.SnapshotChanged += OnSnapshotChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        _engine.SetTrackingEnabled(_settings.SalesTrackingEnabled);
        if (_settings.SalesTrackingEnabled)
        {
            RefreshObservationTargets(force: true);
            StartObservationSource();
        }

        OnSnapshotChanged(_engine.Current);
    }

    public void ApplySourceState(DiscordMessageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_sync)
        {
            _latestSource = state.SalesSource.ToArray();
            _sourceGeneration = state.Generation;
            _sourceReady = !state.IsBootstrapping;
            if (string.IsNullOrWhiteSpace(_salesChannelId))
            {
                _salesChannelId = state.SalesSource
                    .Select(message => message.ChannelId)
                    .FirstOrDefault(channelId => !string.IsNullOrWhiteSpace(channelId)) ??
                    string.Empty;
            }
        }

        if (_settings.SalesTrackingEnabled)
        {
            _engine.ApplySourceSnapshot(state.SalesSource);
            RefreshObservationTargets();
        }

        _uiUpdates.Request();
    }

    public void ApplyRpcStatus(DiscordConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_sync)
        {
            _rpcStatus = status;
        }

        _uiUpdates.Request();
    }

    public void SetTargetChannel(string channelId, string channelName)
    {
        var normalizedId = channelId?.Trim() ?? string.Empty;
        var normalizedName = string.IsNullOrWhiteSpace(channelName)
            ? DiscordTargetOptions.DefaultSalesChannelName
            : channelName.Trim();
        lock (_sync)
        {
            if (string.Equals(_salesChannelId, normalizedId, StringComparison.Ordinal) &&
                string.Equals(_salesChannelName, normalizedName, StringComparison.Ordinal))
            {
                return;
            }

            _salesChannelId = normalizedId;
            _salesChannelName = normalizedName;
        }

        RefreshObservationTargets(force: true);
        _uiUpdates.Request();
    }

    public void SetAuthenticatedUser(string userId) =>
        _engine.SetAuthenticatedUser(userId);

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var wasEnabled = _settings.SalesTrackingEnabled;
        _settings = settings;
        if (!string.IsNullOrWhiteSpace(settings.DiscordSalesChannelId))
        {
            SetTargetChannel(settings.DiscordSalesChannelId, _salesChannelName);
        }

        if (wasEnabled != settings.SalesTrackingEnabled)
        {
            _engine.SetTrackingEnabled(settings.SalesTrackingEnabled);
            if (settings.SalesTrackingEnabled)
            {
                IReadOnlyList<NormalizedDiscordMessage> source;
                lock (_sync)
                {
                    source = _latestSource;
                }

                _engine.ApplySourceSnapshot(source);
                RefreshObservationTargets(force: true);
                StartObservationSource();
                _metrics?.Increment(RuntimeMetricNames.SalesResyncAttempts);
                _observationSource.RequestFullResync();
            }
            else
            {
                StopObservationSource();
            }
        }

        OnSnapshotChanged(_engine.Current);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.SnapshotChanged -= OnSnapshotChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        StopObservationSource();
        _observationSource.Dispose();
        _uiUpdates.Dispose();
    }

    private void StartObservationSource()
    {
        if (!_sourceSubscribed)
        {
            _observationSource.BatchAvailable += OnObservationBatch;
            _observationSource.HealthChanged += OnSensorHealthChanged;
            _sourceSubscribed = true;
        }

        _observationSource.Start();
        OnSensorHealthChanged(_observationSource.Health);
    }

    private void StopObservationSource()
    {
        _observationSource.Stop();
        if (_sourceSubscribed)
        {
            _observationSource.BatchAvailable -= OnObservationBatch;
            _observationSource.HealthChanged -= OnSensorHealthChanged;
            _sourceSubscribed = false;
        }
    }

    private void OnObservationBatch(SalesObservationBatch batch)
    {
        var previous = _engine.Current;
        var trustedSoldCurrent = batch.IsTrusted &&
            previous.CurrentSeller is not null &&
            batch.Observations.Any(observation =>
                observation.HasTrustedEvidence &&
                observation.Outcome == SaleReactionOutcome.Sold &&
                string.Equals(
                    observation.MessageId,
                    previous.CurrentSeller.MessageId,
                    StringComparison.Ordinal));
        lock (_sync)
        {
            _publishingChange = trustedSoldCurrent
                ? new SalesQueueChangeContext(
                    true,
                    previous.CurrentSeller?.MessageId,
                    null,
                    SalesQueueChangeReason.TrustedSold,
                    previous.Revision + 1)
                : SalesQueueChangeContext.None;
        }

        try
        {
            _engine.ApplyObservationBatch(batch);
        }
        finally
        {
            lock (_sync)
            {
                _publishingChange = SalesQueueChangeContext.None;
            }
        }
    }

    private void OnSensorHealthChanged(SalesSensorHealth health)
    {
        SalesSensorHealth previous;
        lock (_sync)
        {
            previous = _sensorHealth;
            _sensorHealth = health;
        }

        _metrics?.SetState(RuntimeMetricNames.SalesState, health.Status.ToString());
        _metrics?.SetGauge(RuntimeMetricNames.SalesSold, health.SoldCount);
        _metrics?.SetGauge(RuntimeMetricNames.SalesCoverageTarget, health.TargetMessageCount);
        _metrics?.SetGauge(RuntimeMetricNames.SalesCoverageObserved, health.ObservedMessageCount);
        if (health.LastCompleteResyncAt.HasValue)
        {
            _metrics?.SetGauge(
                RuntimeMetricNames.SalesLastCompleteUnixSeconds,
                health.LastCompleteResyncAt.Value.ToUnixTimeSeconds());
        }
        if (previous.Status != health.Status)
        {
            _metrics?.Increment(RuntimeMetricNames.SalesHealthTransitions);
            if (health.Status == SalesObservationStatus.Live && health.IsComplete)
            {
                _metrics?.Increment(RuntimeMetricNames.SalesResyncSucceeded);
            }
            else if (health.Status is SalesObservationStatus.Error or
                     SalesObservationStatus.Unavailable or
                     SalesObservationStatus.AccessibilityUnavailable)
            {
                _metrics?.Increment(RuntimeMetricNames.SalesResyncFailed);
            }
        }

        _uiUpdates.Request();
    }

    private void RefreshObservationTargets(bool force = false)
    {
        SalesObservationTargetSet targetSet;
        lock (_sync)
        {
            var targets = _engine.Records
                .Where(record => record.DomainState != SaleDomainState.Deleted)
                .Select(record => new SalesObservationTarget(
                    record.MessageId,
                    record.SourceRevision))
                .OrderBy(target => target.MessageId, StringComparer.Ordinal)
                .ToArray();
            var unchanged = !force &&
                _observationTargets.SourceGeneration == _sourceGeneration &&
                _observationTargets.IsSourceReady == _sourceReady &&
                string.Equals(
                    _observationTargets.SalesChannelId,
                    _salesChannelId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    _observationTargets.SalesChannelName,
                    _salesChannelName,
                    StringComparison.Ordinal) &&
                _observationTargets.Targets.SequenceEqual(targets);
            if (unchanged)
            {
                return;
            }

            targetSet = new SalesObservationTargetSet(
                ++_targetSetRevision,
                _sourceGeneration,
                _sourceReady,
                _salesChannelId,
                _salesChannelName,
                targets);
            _observationTargets = targetSet;
        }

        _observationSource.UpdateTargets(targetSet);
    }

    private void OnSnapshotChanged(SalesQueueSnapshot snapshot)
    {
        _metrics?.SetGauge(RuntimeMetricNames.SalesActiveQueue, snapshot.ActiveCount);
        lock (_sync)
        {
            _pendingSnapshot = snapshot;
            _pendingChange = _publishingChange.Reason == SalesQueueChangeReason.None
                ? SalesQueueChangeContext.None
                : _publishingChange with
                {
                    CurrentSellerChanged = !string.Equals(
                        _publishingChange.PreviousCurrentSellerMessageId,
                        snapshot.CurrentSeller?.MessageId,
                        StringComparison.Ordinal),
                    NewCurrentSellerMessageId = snapshot.CurrentSeller?.MessageId,
                    StateRevision = snapshot.Revision,
                };
        }

        _uiUpdates.Request();
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        _engine.SetLocale(_localization.CurrentLocale);
        _uiUpdates.Request();
    }

    private void ApplyPendingSnapshot()
    {
        var started = Stopwatch.GetTimestamp();
        SalesQueueSnapshot snapshot;
        SalesSensorHealth sensorHealth;
        DiscordConnectionStatus rpcStatus;
        SalesObservationTargetSet observationTargets;
        SalesQueueChangeContext change;
        string salesChannelName;
        bool sourceReady;
        lock (_sync)
        {
            snapshot = _pendingSnapshot;
            sensorHealth = _sensorHealth;
            rpcStatus = _rpcStatus;
            observationTargets = _observationTargets;
            change = _pendingChange;
            salesChannelName = _salesChannelName;
            sourceReady = _sourceReady &&
                rpcStatus.State == DiscordConnectionState.Connected &&
                _sourceGeneration == rpcStatus.Generation;
            _pendingChange = SalesQueueChangeContext.None;
        }

        var currentGenerationResyncComplete = sensorHealth.Status == SalesObservationStatus.Live &&
            sensorHealth.Coverage == SalesCoverageState.Complete &&
            sensorHealth.IsComplete &&
            sensorHealth.LastCompleteResyncAt.HasValue &&
            sensorHealth.TargetSetRevision == observationTargets.Revision;
        var health = SalesFeatureHealthEvaluator.Evaluate(new SalesFeatureHealthInput(
            _settings.SalesTrackingEnabled,
            rpcStatus.State,
            sourceReady,
            sensorHealth,
            currentGenerationResyncComplete));
        if (health != _lastHealth)
        {
            _logger.Information(
                "SALES-HEALTH",
                $"state={health.State} reason={health.Reason} coverage={health.Coverage} target={health.TargetMessageCount} observed={health.ObservedMessageCount}.");
            _lastHealth = health;
        }

        _metrics?.SetState(RuntimeMetricNames.SalesState, health.State.ToString());

        _viewModel.Apply(snapshot, _settings, health, salesChannelName, change);
        var presentation = _viewModel.Presentation;
        if (presentation != _lastPresentation)
        {
            _logger.Information(
                "QUEUE-UI",
                $"mode={presentation.ContentMode} health={presentation.HealthMode} current={Sanitize(presentation.CurrentMessageId)} next={Sanitize(presentation.NextMessageId)} waiting={snapshot.WaitingCount} animation={presentation.AnimationRequest}.");
            _lastPresentation = presentation;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        _metrics?.RecordDuration(RuntimeMetricNames.HudUpdateDuration, elapsed);
        if (elapsed >= TimeSpan.FromMilliseconds(50))
        {
            _metrics?.Increment(RuntimeMetricNames.DispatcherLongOperations);
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length > 80 ? sanitized[..80] : sanitized;
    }
}

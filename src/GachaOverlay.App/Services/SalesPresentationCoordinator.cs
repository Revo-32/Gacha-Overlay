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
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace GachaOverlay.App.Services;

/// <summary>
/// Projects the canonical Remote Sales window into the existing Sales domain and HUD.
/// Remote Sales is the sole production authority for queue and completion state.
/// </summary>
internal sealed class SalesPresentationCoordinator : IDisposable
{
    private const int AuthoritativeWindowSize = AuthoritativeSalesWindow.Size;
    private readonly object _sync = new();
    private readonly SalesStateEngine _engine;
    private readonly SalesQueueViewModel _viewModel;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly UiUpdateCoalescer _uiUpdates;
    private readonly IRuntimeMetrics? _metrics;
    private readonly ISalesTurnNotificationObserver? _turnNotification;
    private readonly Dictionary<string, NormalizedDiscordMessage> _remoteSource =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SalesCompletionObservation> _remoteEvidence =
        new(StringComparer.Ordinal);
    private IReadOnlyList<NormalizedDiscordMessage> _latestSource =
        Array.Empty<NormalizedDiscordMessage>();
    private SalesQueueSnapshot _pendingSnapshot;
    private SalesQueueChangeContext _pendingChange = SalesQueueChangeContext.None;
    private SalesQueueChangeContext _publishingChange = SalesQueueChangeContext.None;
    private SalesFeatureHealthSnapshot? _lastHealth;
    private SalesQueuePresentationState? _lastPresentation;
    private AppSettings _settings;
    private long _observationGeneration;
    private bool _remoteCanonicalReady;
    private string? _remoteGeneration;
    private long _remoteLatestSequence;
    private RemoteSalesPresentationPhase _remoteSalesPhase;
    private EffectiveSalesSource _effectiveSalesSource = EffectiveSalesSource.RemoteStarting;
    private DateTimeOffset? _remoteSalesReadyAt;
    private bool _started;
    private bool _disposed;

    public SalesPresentationCoordinator(
        SalesStateEngine engine,
        SalesQueueViewModel viewModel,
        ILocalizationService localization,
        IAppLogger logger,
        AppSettings initialSettings,
        System.Windows.Threading.Dispatcher dispatcher,
        IRuntimeMetrics? metrics = null,
        ISalesTurnNotificationObserver? turnNotification = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));
        _pendingSnapshot = engine.Current;
        _remoteSalesPhase = initialSettings.SalesTrackingEnabled
            ? RemoteSalesPresentationPhase.Connecting
            : RemoteSalesPresentationPhase.Disabled;
        _metrics = metrics;
        _turnNotification = turnNotification;
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
            .GroupBy(item => (item.Message.GuildId, item.Emoji.EmojiId))
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
        OnSnapshotChanged(_engine.Current);
    }

    public void ApplyRemoteSalesBootstrap(SalesBootstrapResponse bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (!_settings.SalesTrackingEnabled)
        {
            return;
        }

        if (!IsCanonicalBootstrap(bootstrap))
        {
            lock (_sync)
            {
                InvalidateRemoteAuthorityUnderLock(RemoteSalesPresentationPhase.Resyncing);
            }

            _metrics?.Increment(RuntimeMetricNames.RemotePromotionFailures);
            _logger.Warning(
                "REMOTE-SALES",
                $"Non-canonical bootstrap rejected coverage={bootstrap.Coverage} messages={bootstrap.RecentMessages.Count} observations={bootstrap.CompletionObservations.Count}.");
            _uiUpdates.Request();
            return;
        }

        IReadOnlyList<NormalizedDiscordMessage> productionSource;
        lock (_sync)
        {
            _remoteSource.Clear();
            foreach (var message in bootstrap.RecentMessages)
            {
                var normalized = RemoteChatIngressAdapter.MapNormalizedMessage(message);
                _remoteSource[normalized.MessageId] = normalized;
            }

            _remoteEvidence.Clear();
            foreach (var observation in bootstrap.CompletionObservations)
            {
                _remoteEvidence[Id(observation.MessageId)] = observation;
            }

            TrimRemoteCachesUnderLock();
            _remoteCanonicalReady = true;
            _remoteGeneration = bootstrap.Generation;
            _remoteLatestSequence = bootstrap.LatestSequence;
            _remoteSalesPhase = RemoteSalesPresentationPhase.Live;
            _remoteSalesReadyAt = DateTimeOffset.UtcNow;
            productionSource = ComposeRemoteSourceUnderLock();
            _latestSource = productionSource;
        }

        _engine.ApplyAuthoritativeWindowSnapshot(productionSource);
        ApplyRemoteEvidence(bootstrap.CompletionObservations);
        _metrics?.Increment(RuntimeMetricNames.RemotePromotionSucceeded);
        _uiUpdates.Request();
        _logger.Information(
            "REMOTE-SALES",
            $"Canonical bootstrap promoted coverage={bootstrap.Coverage} messages={bootstrap.RecentMessages.Count} observations={bootstrap.CompletionObservations.Count}.");
    }

    public void ApplyRemoteSalesMutation(SalesMutationEnvelope mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (!_settings.SalesTrackingEnabled)
        {
            return;
        }

        var messageId = Id(mutation.MessageId);
        IReadOnlyList<NormalizedDiscordMessage>? productionSource = null;
        lock (_sync)
        {
            if (!_remoteCanonicalReady ||
                _remoteSalesPhase != RemoteSalesPresentationPhase.Live ||
                !string.Equals(_remoteGeneration, mutation.Generation, StringComparison.Ordinal) ||
                mutation.Sequence != _remoteLatestSequence + 1)
            {
                InvalidateRemoteAuthorityUnderLock(RemoteSalesPresentationPhase.Resyncing);
                _logger.Warning(
                    "REMOTE-SALES",
                    $"Mutation cursor rejected generation={Sanitize(mutation.Generation)} sequence={mutation.Sequence}; canonical resync required.");
                _uiUpdates.Request();
                return;
            }

            _remoteLatestSequence = mutation.Sequence;
            if (mutation.EventType == OverlayTransportProtocol.SalesMessageDelete)
            {
                _remoteSource.Remove(messageId);
                _remoteEvidence.Remove(messageId);
            }
            else if (mutation.Message is not null)
            {
                var normalized = RemoteChatIngressAdapter.MapNormalizedMessage(mutation.Message);
                _remoteSource[normalized.MessageId] = normalized;
            }

            if (mutation.CompletionObservation is { } observation)
            {
                _remoteEvidence[Id(observation.MessageId)] = observation;
            }

            TrimRemoteCachesUnderLock();
            productionSource = ComposeRemoteSourceUnderLock();
            _latestSource = productionSource;
        }

        if (mutation.EventType == OverlayTransportProtocol.SalesMessageDelete)
        {
            _engine.ApplySourceDelete(messageId);
        }

        _engine.ApplyAuthoritativeWindowSnapshot(productionSource);
        if (mutation.CompletionObservation is { } completion)
        {
            ApplyRemoteEvidence(new[] { completion });
        }

        _uiUpdates.Request();
    }

    public void ApplyRemoteSalesStatus(string status)
    {
        var phase = MapRemoteSalesPhase(status);
        lock (_sync)
        {
            if (phase == RemoteSalesPresentationPhase.Live && _remoteCanonicalReady)
            {
                _remoteSalesPhase = RemoteSalesPresentationPhase.Live;
            }
            else
            {
                InvalidateRemoteAuthorityUnderLock(phase);
            }
        }

        _logger.Information("REMOTE-SALES", $"State={status}.");
        _metrics?.SetState(RuntimeMetricNames.RemoteSalesState, status);
        _uiUpdates.Request();
    }

    public void SetAuthenticatedUser(string userId)
    {
        _turnNotification?.ResetBaseline();
        _engine.SetAuthenticatedUser(userId);
    }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var wasEnabled = _settings.SalesTrackingEnabled;
        _settings = settings;
        if (wasEnabled != settings.SalesTrackingEnabled)
        {
            if (settings.SalesTrackingEnabled)
            {
                _engine.SetTrackingEnabled(true);
                _engine.ApplyAuthoritativeWindowSnapshot(Array.Empty<NormalizedDiscordMessage>());
                lock (_sync)
                {
                    _remoteSource.Clear();
                    _remoteEvidence.Clear();
                    _latestSource = Array.Empty<NormalizedDiscordMessage>();
                    InvalidateRemoteAuthorityUnderLock(RemoteSalesPresentationPhase.Connecting);
                    _remoteSalesReadyAt = null;
                }
            }
            else
            {
                lock (_sync)
                {
                    _remoteSource.Clear();
                    _remoteEvidence.Clear();
                    _latestSource = Array.Empty<NormalizedDiscordMessage>();
                    InvalidateRemoteAuthorityUnderLock(RemoteSalesPresentationPhase.Disabled);
                    _remoteSalesReadyAt = null;
                }

                _engine.SetTrackingEnabled(false);
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
        _uiUpdates.Dispose();
    }

    private void ApplyRemoteEvidence(
        IReadOnlyCollection<SalesCompletionObservation> observations)
    {
        var complete = observations
            .Where(observation => observation.Coverage == SalesEvidenceCoverage.Complete)
            .ToArray();
        _metrics?.Increment(RuntimeMetricNames.RemoteSalesObservations, complete.Length);
        long generation;
        lock (_sync)
        {
            generation = ++_observationGeneration;
        }

        var observedAt = complete.Length == 0
            ? _remoteSalesReadyAt ?? DateTimeOffset.UtcNow
            : complete.Max(item => item.ObservedAt);
        var batch = new SalesObservationBatch(
            generation,
            observedAt,
            SalesObservationStatus.Live,
            true,
            SalesObservationCompleteness.Full,
            complete.Select(item => new SaleReactionObservation(
                Id(item.MessageId),
                item.IsSold ? SaleReactionOutcome.Sold : SaleReactionOutcome.NotSold,
                true,
                item.ObservedAt,
                generation)).ToArray(),
            SalesCoverageState.Complete,
            TargetMessageCount: complete.Length,
            ObservedMessageCount: complete.Length,
            SoldCount: complete.Count(item => item.IsSold),
            NotSoldCount: complete.Count(item => !item.IsSold));
        ApplyObservationBatch(batch);
    }

    private void ApplyObservationBatch(SalesObservationBatch batch)
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
        SalesQueueChangeContext change;
        RemoteSalesPresentationPhase phase;
        bool canonicalReady;
        DateTimeOffset? readyAt;
        IReadOnlyDictionary<string, SalesCompletionObservation> evidence;
        EffectiveSalesSource effectiveSource;
        bool effectiveSourceChanged;
        int targetCount;
        int observedCount;
        SalesCoverageState coverage;
        lock (_sync)
        {
            snapshot = _pendingSnapshot;
            change = _pendingChange;
            _pendingChange = SalesQueueChangeContext.None;
            phase = _remoteSalesPhase;
            canonicalReady = _remoteCanonicalReady;
            readyAt = _remoteSalesReadyAt;
            evidence = new Dictionary<string, SalesCompletionObservation>(
                _remoteEvidence,
                StringComparer.Ordinal);
            targetCount = _remoteSource.Count;
            observedCount = _remoteSource.Keys.Count(id =>
                _remoteEvidence.TryGetValue(id, out var observation) &&
                observation.Coverage == SalesEvidenceCoverage.Complete);
            coverage = canonicalReady && observedCount == targetCount
                ? SalesCoverageState.Complete
                : observedCount > 0
                    ? SalesCoverageState.Partial
                    : SalesCoverageState.None;
            effectiveSource = ResolveDecisionUnderLock().EffectiveSource;
            effectiveSourceChanged = effectiveSource != _effectiveSalesSource;
            _effectiveSalesSource = effectiveSource;
        }

        if (effectiveSourceChanged)
        {
            _metrics?.Increment(RuntimeMetricNames.EffectiveSalesSourceTransitions);
            _metrics?.SetState(RuntimeMetricNames.EffectiveSalesSource, effectiveSource.ToString());
            if (effectiveSource == EffectiveSalesSource.RemotePrimary)
            {
                _metrics?.Increment(RuntimeMetricNames.RemotePrimaryTransitions);
            }
            else if (effectiveSource == EffectiveSalesSource.RemoteRecovering)
            {
                _metrics?.Increment(RuntimeMetricNames.RemoteRecoveryTransitions);
            }
        }

        var health = SalesFeatureHealthEvaluator.Evaluate(new SalesFeatureHealthInput(
            _settings.SalesTrackingEnabled,
            phase,
            canonicalReady,
            coverage,
            canonicalReady && coverage == SalesCoverageState.Complete ? readyAt : null,
            targetCount,
            observedCount));
        if (health != _lastHealth)
        {
            _logger.Information(
                "SALES-HEALTH",
                $"state={health.State} reason={health.Reason} source={effectiveSource} coverage={health.Coverage} target={health.TargetMessageCount} observed={health.ObservedMessageCount}.");
            _lastHealth = health;
        }

        _metrics?.SetState(RuntimeMetricNames.SalesState, health.State.ToString());
        _metrics?.SetGauge(RuntimeMetricNames.SalesCoverageTarget, targetCount);
        _metrics?.SetGauge(RuntimeMetricNames.SalesCoverageObserved, observedCount);
        if (readyAt.HasValue)
        {
            _metrics?.SetGauge(
                RuntimeMetricNames.SalesLastCompleteUnixSeconds,
                readyAt.Value.ToUnixTimeSeconds());
        }

        var presentationSnapshot = effectiveSource == EffectiveSalesSource.AccessRevoked
            ? RedactForAccessRevocation(snapshot)
            : snapshot;
        var activeIds = presentationSnapshot.ActiveItems
            .Select(entry => entry.MessageId)
            .ToHashSet(StringComparer.Ordinal);
        var statusActionTargets = effectiveSource != EffectiveSalesSource.RemotePrimary ||
            string.IsNullOrWhiteSpace(presentationSnapshot.AuthenticatedUserId)
                ? Array.Empty<SalesStatusActionTarget>()
                : _engine.Records
                    .Where(record =>
                        record.DomainState != SaleDomainState.Deleted &&
                        string.Equals(
                            record.AuthorId,
                            presentationSnapshot.AuthenticatedUserId,
                            StringComparison.Ordinal) &&
                        !activeIds.Contains(record.MessageId) &&
                        evidence.TryGetValue(record.MessageId, out var observation) &&
                        observation.Coverage == SalesEvidenceCoverage.Complete &&
                        observation.HasAnyBotStatus)
                    .Select(record => new SalesStatusActionTarget(
                        record.MessageId,
                        record.DisplayName.DisplayName,
                        SalesProductSummaryFormatter.Format(record.AllProducts),
                        record.DisplayName.IsExactGuildNickname))
                    .ToArray();

        _viewModel.ApplyRemoteStatusContext(evidence, effectiveSource, statusActionTargets);
        _viewModel.Apply(
            presentationSnapshot,
            _settings,
            health,
            ProductionServerProfile.SalesChannelName,
            change);
        var presentation = _viewModel.Presentation;
        try
        {
            _turnNotification?.Observe(presentation, effectiveSourceChanged);
        }
        catch (Exception exception)
        {
            _logger.Error(
                "SALES-SOUND",
                "Sales turn notification failed without affecting Sales presentation.",
                exception);
        }

        if (presentation != _lastPresentation)
        {
            _logger.Information(
                "QUEUE-UI",
                $"mode={presentation.ContentMode} health={presentation.HealthMode} current={Sanitize(presentation.CurrentMessageId)} next={Sanitize(presentation.NextMessageId)} waiting={presentationSnapshot.WaitingCount} animation={presentation.AnimationRequest}.");
            _lastPresentation = presentation;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        _metrics?.RecordDuration(RuntimeMetricNames.HudUpdateDuration, elapsed);
        if (elapsed >= TimeSpan.FromMilliseconds(50))
        {
            _metrics?.Increment(RuntimeMetricNames.DispatcherLongOperations);
        }
    }

    private static SalesQueueSnapshot RedactForAccessRevocation(SalesQueueSnapshot snapshot) =>
        snapshot with
        {
            ActiveItems = Array.Empty<SalesQueueEntry>(),
            CurrentSeller = null,
            ActiveCount = 0,
            WaitingCount = 0,
            NextWaitingEntry = null,
            CurrentSellerIsSelf = false,
            NextSellerIsSelf = false,
            ContainsUnverifiedActiveItems = false,
            IsObservationSourceAvailable = false,
            ObservationStatus = SalesObservationStatus.Unavailable,
        };

    private SalesAcquisitionDecision ResolveDecisionUnderLock() =>
        SalesAcquisitionPolicy.Evaluate(new SalesAcquisitionPolicyInput(
            _settings.SalesTrackingEnabled,
            _remoteSalesPhase,
            _remoteCanonicalReady));

    private void InvalidateRemoteAuthorityUnderLock(RemoteSalesPresentationPhase phase)
    {
        _remoteSalesPhase = phase;
        _remoteCanonicalReady = false;
        _remoteGeneration = null;
        _remoteLatestSequence = 0;
    }

    private IReadOnlyList<NormalizedDiscordMessage> ComposeRemoteSourceUnderLock() =>
        _remoteSource.Values
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal)
            .ToArray();

    private static bool IsCanonicalBootstrap(SalesBootstrapResponse bootstrap)
    {
        if (bootstrap.ProtocolVersion != OverlayTransportProtocol.Version ||
            string.IsNullOrWhiteSpace(bootstrap.Generation) ||
            bootstrap.Coverage != SalesBootstrapCoverage.Complete ||
            bootstrap.RecentMessages.Count > AuthoritativeWindowSize)
        {
            return false;
        }

        var messageIds = bootstrap.RecentMessages.Select(message => message.MessageId).ToHashSet();
        var observations = bootstrap.CompletionObservations
            .GroupBy(observation => observation.MessageId)
            .ToArray();
        return observations.All(group =>
                   group.Count() == 1 &&
                   group.Single().Coverage == SalesEvidenceCoverage.Complete) &&
               observations.Select(group => group.Key).ToHashSet().SetEquals(messageIds);
    }

    private void TrimRemoteCachesUnderLock()
    {
        foreach (var id in _remoteSource.Values
                     .OrderByDescending(message => message.CreatedAt)
                     .ThenByDescending(message => message.MessageId, StringComparer.Ordinal)
                     .Skip(AuthoritativeWindowSize)
                     .Select(message => message.MessageId)
                     .ToArray())
        {
            _remoteSource.Remove(id);
            _remoteEvidence.Remove(id);
        }

        foreach (var id in _remoteEvidence.Keys.Where(id => !_remoteSource.ContainsKey(id)).ToArray())
        {
            _remoteEvidence.Remove(id);
        }
    }

    private static RemoteSalesPresentationPhase MapRemoteSalesPhase(string status) =>
        status switch
        {
            RemoteSalesStatusNames.Connecting => RemoteSalesPresentationPhase.Connecting,
            RemoteSalesStatusNames.Bootstrapping => RemoteSalesPresentationPhase.Bootstrapping,
            RemoteSalesStatusNames.Reconnecting => RemoteSalesPresentationPhase.Reconnecting,
            RemoteSalesStatusNames.CredentialUnavailable =>
                RemoteSalesPresentationPhase.CredentialUnavailable,
            RemoteSalesStatusNames.Disabled => RemoteSalesPresentationPhase.Disabled,
            OverlayTransportProtocol.SalesReady => RemoteSalesPresentationPhase.Live,
            OverlayTransportProtocol.SalesResyncRequired =>
                RemoteSalesPresentationPhase.Resyncing,
            OverlayTransportProtocol.SalesAuthorizationUnavailable =>
                RemoteSalesPresentationPhase.AuthorizationUnavailable,
            OverlayTransportProtocol.SalesAccessRevoked =>
                RemoteSalesPresentationPhase.AccessRevoked,
            OverlayTransportProtocol.SalesChannelUnavailable =>
                RemoteSalesPresentationPhase.ChannelUnavailable,
            OverlayTransportProtocol.SalesFailed => RemoteSalesPresentationPhase.Failed,
            _ => RemoteSalesPresentationPhase.Failed,
        };

    private static string Id(ulong value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

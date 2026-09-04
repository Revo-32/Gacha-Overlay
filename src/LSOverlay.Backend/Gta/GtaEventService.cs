using System.Text.Json;
using Discord;
using GachaOverlay.Core.Gta;
using LSOverlay.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LSOverlay.Backend.Gta;

internal sealed record GtaEventCandidateDiagnostic(
    ulong SourceMessageId,
    DateTimeOffset ObservedAt,
    string Reason);

internal sealed record GtaEventServiceDiagnostics(
    long HydrationSuccesses,
    long HydrationFailures,
    long TrustedWeeks,
    long CandidateWeeks,
    long TrustedCampaigns,
    long SnapshotRevision,
    bool HasActiveWeek,
    bool HasStagedWeek,
    GtaCompanionDataState DataState,
    IReadOnlyList<GtaEventCandidateDiagnostic> Candidates,
    IReadOnlyList<GtaUnknownVocabularyEntry> UnknownVocabulary);

internal sealed class GtaEventService
{
    public const int MaximumHydrationMessages = 100;
    public const int MaximumCandidates = 24;
    public static readonly TimeSpan MinimumHydrationInterval = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions ComparisonJson = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _hydrationGate = new(1, 1);
    private readonly Configuration.BackendConfiguration _configuration;
    private readonly IGtaEventDiscordSource _source;
    private readonly IGtaEventStore _store;
    private readonly GtaEventClassifier _classifier;
    private readonly GtaEventParser _parser;
    private readonly GtaEventResolver _resolver;
    private readonly GtaKoreanFormatter _formatter;
    private readonly GtaUnknownVocabularyReport _unknown;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GtaEventService> _logger;
    private readonly Queue<GtaEventCandidateDiagnostic> _candidates = new();
    private GtaCompanionSnapshot _snapshot = new(
        OverlayTransportProtocol.Version,
        0,
        GtaCompanionDataState.Unavailable,
        DateTimeOffset.MinValue,
        null,
        null);
    private string _snapshotSignature = string.Empty;
    private DateTimeOffset _lastHydrationAt = DateTimeOffset.MinValue;
    private long _hydrationSuccesses;
    private long _hydrationFailures;
    private long _trustedWeeks;
    private long _candidateWeeks;
    private long _trustedCampaigns;

    public GtaEventService(
        Configuration.BackendConfiguration configuration,
        IGtaEventDiscordSource source,
        IGtaEventStore store,
        GtaEventClassifier classifier,
        GtaEventParser parser,
        GtaEventResolver resolver,
        GtaKoreanFormatter formatter,
        GtaUnknownVocabularyReport unknown,
        TimeProvider timeProvider,
        ILogger<GtaEventService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _unknown = unknown ?? throw new ArgumentNullException(nameof(unknown));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var now = _timeProvider.GetUtcNow();
        var promoted = _resolver.Restore(_store.Load(), now);
        if (promoted)
        {
            _ = _store.Save(_resolver.TrustedState);
        }

        RebuildSnapshot(now, force: true);
        _logger.LogInformation(
            "GTA events Last-Good restored active={Active} staged={Staged} store={StoreFile}.",
            _resolver.TrustedState.ActiveWeek is not null,
            _resolver.TrustedState.StagedWeek is not null,
            System.IO.Path.GetFileName(_store.Path));
    }

    public event Action<GtaCompanionSnapshot>? SnapshotChanged;

    public GtaCompanionSnapshot CaptureSnapshot()
    {
        lock (_sync) return _snapshot;
    }

    public GtaEventServiceDiagnostics CaptureDiagnostics()
    {
        lock (_sync)
        {
            return new GtaEventServiceDiagnostics(
                _hydrationSuccesses,
                _hydrationFailures,
                _trustedWeeks,
                _candidateWeeks,
                _trustedCampaigns,
                _snapshot.Revision,
                _resolver.TrustedState.ActiveWeek is not null,
                _resolver.TrustedState.StagedWeek is not null,
                _snapshot.State,
                _candidates.ToArray(),
                _unknown.Snapshot());
        }
    }

    public Task ReceiveCreateAsync(ulong guildId, IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (guildId != _configuration.TargetGuildId ||
            message.Channel.Id != GtaCompanionProtocolPolicy.ProductionEventChannelId)
        {
            return Task.CompletedTask;
        }

        ProcessDocument(_source.Build(message));
        return Task.CompletedTask;
    }

    public async Task ReceiveUpdateAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken = default)
    {
        if (guildId != _configuration.TargetGuildId ||
            channelId != GtaCompanionProtocolPolicy.ProductionEventChannelId)
        {
            return;
        }

        var fetched = await _source.GetMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (fetched.Status == GtaEventSourceStatus.Available && fetched.Document is not null)
        {
            ProcessDocument(fetched.Document);
        }
    }

    public void ReceiveDelete(ulong guildId, ulong channelId, ulong messageId)
    {
        if (guildId != _configuration.TargetGuildId ||
            channelId != GtaCompanionProtocolPolicy.ProductionEventChannelId)
        {
            return;
        }

        lock (_sync)
        {
            if (_candidates.Count > 0)
            {
                var retained = _candidates.Where(item => item.SourceMessageId != messageId).ToArray();
                _candidates.Clear();
                foreach (var item in retained) _candidates.Enqueue(item);
            }
        }

        // Deletion is not evidence that trusted event semantics became false.
        _logger.LogInformation("GTA event source deletion observed; Last-Good state retained.");
    }

    public async Task HydrateAsync(bool force, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            if (!force && now - _lastHydrationAt < MinimumHydrationInterval)
            {
                return;
            }
        }

        if (!await _hydrationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                now = _timeProvider.GetUtcNow();
                if (!force && now - _lastHydrationAt < MinimumHydrationInterval)
                {
                    return;
                }

                _lastHydrationAt = now;
            }

            var result = await _source.GetRecentAsync(MaximumHydrationMessages, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status != GtaEventSourceStatus.Available)
            {
                lock (_sync) _hydrationFailures++;
                LogHydrationFailure(result.Status);
                return;
            }

            var trustedWeeks = 0;
            var trustedCampaigns = 0;
            var anyTrustedChange = false;
            foreach (var document in result.Documents.Take(MaximumHydrationMessages))
            {
                var outcome = ProcessDocumentCore(document);
                anyTrustedChange |= outcome.Changed;
                if (outcome.Kind == GtaEventClassificationKind.WeeklyBulletin) trustedWeeks++;
                if (outcome.Kind == GtaEventClassificationKind.MultiWeekCampaign) trustedCampaigns++;
                if (trustedWeeks >= 2 && trustedCampaigns >= 1)
                {
                    break;
                }
            }

            lock (_sync) _hydrationSuccesses++;
            RebuildSnapshot(_timeProvider.GetUtcNow(), force: anyTrustedChange);
            _logger.LogInformation(
                "GTA event hydration complete examined={Count} trusted_weekly={Weekly} trusted_campaign={Campaign}.",
                Math.Min(result.Documents.Count, MaximumHydrationMessages),
                trustedWeeks,
                trustedCampaigns);
        }
        finally
        {
            _hydrationGate.Release();
        }
    }

    public void EvaluateTime()
    {
        var now = _timeProvider.GetUtcNow();
        bool transitioned;
        lock (_sync)
        {
            transitioned = _resolver.EvaluateTransitions(now);
            if (transitioned)
            {
                _ = _store.Save(_resolver.TrustedState);
            }
        }

        RebuildSnapshot(now, force: transitioned);
    }

    internal void ProcessDocument(CanonicalEventDocument document)
    {
        var outcome = ProcessDocumentCore(document);
        RebuildSnapshot(_timeProvider.GetUtcNow(), force: outcome.Changed);
    }

    private (GtaEventClassificationKind Kind, bool Changed) ProcessDocumentCore(
        CanonicalEventDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.ChannelId != GtaCompanionProtocolPolicy.ProductionEventChannelId)
        {
            return (GtaEventClassificationKind.Ignore, false);
        }

        var classification = _classifier.Classify(document);
        var parsed = _parser.Parse(document, classification);
        var now = _timeProvider.GetUtcNow();
        var changed = false;
        lock (_sync)
        {
            switch (classification.Kind)
            {
                case GtaEventClassificationKind.WeeklyBulletin when parsed.Week is not null:
                    changed = _resolver.ApplyWeek(parsed.Week, now);
                    _trustedWeeks++;
                    break;
                case GtaEventClassificationKind.MultiWeekCampaign when parsed.Campaign is not null:
                    changed = _resolver.ApplyCampaign(parsed.Campaign, now);
                    _trustedCampaigns++;
                    break;
                case GtaEventClassificationKind.Candidate:
                    _candidateWeeks++;
                    AddCandidate(new GtaEventCandidateDiagnostic(
                        document.SourceMessageId,
                        now,
                        classification.Reason));
                    break;
            }

            if (changed && !_store.Save(_resolver.TrustedState))
            {
                _logger.LogWarning("GTA trusted state changed in memory but Last-Good persistence failed.");
            }
        }

        return (classification.Kind, changed);
    }

    private void RebuildSnapshot(DateTimeOffset now, bool force)
    {
        GtaCompanionSnapshot? published = null;
        lock (_sync)
        {
            var resolved = _resolver.Resolve(now);
            var candidate = BuildSnapshot(resolved, 0);
            var signature = JsonSerializer.Serialize(
                candidate with { Revision = 0, GeneratedAt = DateTimeOffset.MinValue },
                ComparisonJson);
            if (!force && signature == _snapshotSignature)
            {
                return;
            }

            if (signature == _snapshotSignature && _snapshot.Revision > 0)
            {
                return;
            }

            _snapshotSignature = signature;
            _snapshot = candidate with { Revision = checked(_snapshot.Revision + 1) };
            published = _snapshot;
        }

        if (published is null) return;
        try
        {
            SnapshotChanged?.Invoke(published);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "GTA snapshot subscriber failed category={Category}.",
                exception.GetType().Name);
        }
    }

    private GtaCompanionSnapshot BuildSnapshot(GtaResolvedEventState state, long revision)
    {
        var truncated = false;
        GtaCompanionWeek? week = null;
        if (state.CurrentWeek is { } sourceWeek)
        {
            var bonuses = MapItems(sourceWeek.Bonuses, GtaCompanionProtocolPolicy.MaximumBonuses, ref truncated);
            var discounts = MapItems(sourceWeek.Discounts, GtaCompanionProtocolPolicy.MaximumDiscounts, ref truncated);
            var free = MapItems(sourceWeek.FreeItems, GtaCompanionProtocolPolicy.MaximumFreeItems, ref truncated);
            var other = MapItems(sourceWeek.OtherEvents, GtaCompanionProtocolPolicy.MaximumDetailSections, ref truncated);
            week = new GtaCompanionWeek(
                sourceWeek.WeekKey,
                sourceWeek.EffectiveFrom,
                sourceWeek.EffectiveTo,
                Bound(sourceWeek.Theme is null ? null : _formatter.TranslateKnownTerms(sourceWeek.Theme), 256),
                sourceWeek.WeeklyChallenge is null ? null : new GtaCompanionChallenge(
                    sourceWeek.WeeklyChallenge.ChallengeKey,
                    Bound(_formatter.FormatChallenge(sourceWeek.WeeklyChallenge), 512)!,
                    Bound(_formatter.FormatReward(sourceWeek.WeeklyChallenge), 256),
                    sourceWeek.WeeklyChallenge.Requirements.Select(requirement =>
                        Bound(_formatter.TranslateKnownTerms(requirement), 256)!).Take(8).ToArray()),
                bonuses,
                discounts,
                free,
                other);
        }

        GtaCompanionCampaign? campaign = null;
        if (state.Campaign is { } sourceCampaign)
        {
            var currentKey = state.CurrentWeek?.WeekKey;
            var upcoming = sourceCampaign.PlannedWeeks
                .Where(item => item.WeekKey != currentKey)
                .Take(GtaCompanionProtocolPolicy.MaximumUpcomingWeeks)
                .Select(item => new GtaCompanionCampaignWeek(
                    item.WeekKey,
                    Bound(_formatter.FormatCampaignText(item.Label), 256)!,
                    item.EffectiveFrom,
                    item.EffectiveTo))
                .ToArray();
            truncated |= sourceCampaign.PlannedWeeks.Count > upcoming.Length;
            campaign = new GtaCompanionCampaign(
                sourceCampaign.CampaignKey,
                Bound(_formatter.FormatCampaignText(sourceCampaign.Title), 256)!,
                sourceCampaign.StartAt,
                sourceCampaign.EndAt,
                sourceCampaign.Goals.Take(GtaCompanionProtocolPolicy.MaximumCampaignEntries)
                    .Select(goal => Bound(_formatter.FormatCampaignText(goal), 256)!).ToArray(),
                sourceCampaign.Rewards.Take(GtaCompanionProtocolPolicy.MaximumCampaignEntries)
                    .Select(reward => Bound(_formatter.FormatCampaignText(reward), 256)!).ToArray(),
                upcoming);
            truncated |= sourceCampaign.Goals.Count > GtaCompanionProtocolPolicy.MaximumCampaignEntries ||
                sourceCampaign.Rewards.Count > GtaCompanionProtocolPolicy.MaximumCampaignEntries;
        }

        return new GtaCompanionSnapshot(
            OverlayTransportProtocol.Version,
            revision,
            state.Availability switch
            {
                GtaResolvedAvailability.Available => GtaCompanionDataState.Available,
                GtaResolvedAvailability.Preparing => GtaCompanionDataState.Preparing,
                _ => GtaCompanionDataState.Unavailable,
            },
            state.EvaluatedAt,
            week,
            campaign,
            truncated);
    }

    private IReadOnlyList<GtaCompanionItem> MapItems(
        IReadOnlyList<GtaSemanticEventItem> source,
        int maximum,
        ref bool truncated)
    {
        if (source.Count > maximum) truncated = true;
        return source.Take(maximum).Select(item => new GtaCompanionItem(
            item.ItemKey,
            item.Kind switch
            {
                GtaEventItemKind.Bonus => GtaCompanionItemKind.Bonus,
                GtaEventItemKind.Discount => GtaCompanionItemKind.Discount,
                GtaEventItemKind.FreeItem => GtaCompanionItemKind.FreeItem,
                GtaEventItemKind.LoginReward => GtaCompanionItemKind.LoginReward,
                GtaEventItemKind.RotatingContent => GtaCompanionItemKind.RotatingContent,
                _ => GtaCompanionItemKind.Note,
            },
            Bound(_formatter.FormatItem(item), 512)!,
            Bound(item.OriginalLabel, 512)!,
            item.Multiplier,
            item.DiscountPercent,
            item.RewardTypes.Select(reward => reward.ToString()).ToArray(),
            item.DateScope?.StartAt,
            item.DateScope?.EndAt)).ToArray();
    }

    private void AddCandidate(GtaEventCandidateDiagnostic candidate)
    {
        var retained = _candidates.Where(item => item.SourceMessageId != candidate.SourceMessageId).ToArray();
        _candidates.Clear();
        foreach (var item in retained.TakeLast(MaximumCandidates - 1)) _candidates.Enqueue(item);
        _candidates.Enqueue(candidate);
    }

    private void LogHydrationFailure(GtaEventSourceStatus status)
    {
        if (status is GtaEventSourceStatus.ViewChannelRequired or GtaEventSourceStatus.ReadHistoryRequired)
        {
            var permission = status == GtaEventSourceStatus.ViewChannelRequired
                ? "View Channel"
                : "Read Message History";
            _logger.LogWarning("EVENT CHANNEL READ PERMISSION REQUIRED: {Permission}.", permission);
            return;
        }

        _logger.LogWarning("GTA event hydration unavailable status={Status}; Last-Good retained.", status);
    }

    private static string? Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maximum ? value : value[..maximum].TrimEnd();
}

internal sealed class GtaEventResetWorker : BackgroundService
{
    private readonly GtaEventService _service;

    public GtaEventResetWorker(GtaEventService service)
    {
        _service = service;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            _service.EvaluateTime();
        }
    }
}

using System.Diagnostics;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;
using Microsoft.Extensions.Logging;

namespace LSOverlay.Backend.Sales;

internal sealed class RemoteSalesActionService
{
    internal const int MaximumRequestsPerMinute = 8;
    internal const int DedupeCapacity = 256;
    internal const int VersionCapacity = 256;
    internal const int RateWindowCapacity = ClientCredentialRegistry.MaximumCredentials;
    private const int GateStripeCount = 64;
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    private readonly object _sync = new();
    private readonly Configuration.BackendConfiguration _configuration;
    private readonly IChatAuthorizationService _authorization;
    private readonly ISalesStatusDiscordSource _source;
    private readonly ActiveSalesStreamRegistry _streams;
    private readonly RemoteSalesService _sales;
    private readonly TransportMetrics _metrics;
    private readonly ILogger<RemoteSalesActionService> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, GateStripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly Dictionary<(Guid ClientId, Guid RequestId),
        Task<SalesStatusActionResponse>> _dedupe = new();
    private readonly Queue<(Guid ClientId, Guid RequestId)> _dedupeOrder = new();
    private readonly Dictionary<ulong, (long Version, long Touched)> _versions = new();
    private readonly Dictionary<ulong, Queue<DateTimeOffset>> _rateWindows = new();
    private long _versionCounter;
    private long _touchCounter;
    private int _outstandingActions;

    public RemoteSalesActionService(
        Configuration.BackendConfiguration configuration,
        IChatAuthorizationService authorization,
        ISalesStatusDiscordSource source,
        ActiveSalesStreamRegistry streams,
        RemoteSalesService sales,
        TransportMetrics metrics,
        ILogger<RemoteSalesActionService> logger)
        : this(configuration, authorization, source, streams, sales, metrics, logger,
            () => DateTimeOffset.UtcNow)
    {
    }

    internal RemoteSalesActionService(
        Configuration.BackendConfiguration configuration,
        IChatAuthorizationService authorization,
        ISalesStatusDiscordSource source,
        ActiveSalesStreamRegistry streams,
        RemoteSalesService sales,
        TransportMetrics metrics,
        ILogger<RemoteSalesActionService> logger,
        Func<DateTimeOffset> clock)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _sales = sales ?? throw new ArgumentNullException(nameof(sales));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<SalesStatusActionResponse> SetStatusAsync(
        AuthenticatedClientIdentity identity,
        SalesStatusActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(request);
        OverlayProtocolJson.EnsureVersion(request.ProtocolVersion);
        if (request.MessageId == 0 ||
            request.ClientRequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.SalesGeneration) ||
            !Enum.IsDefined(request.DesiredStatus))
        {
            return Task.FromResult(Response(
                request,
                SalesStatusActionDisposition.RejectedInvalidState));
        }

        var key = (identity.ClientInstallationId, request.ClientRequestId);
        lock (_sync)
        {
            if (_dedupe.TryGetValue(key, out var existing))
            {
                _metrics.Increment(TransportMetric.SalesStatusDeduplicated);
                return existing.WaitAsync(cancellationToken);
            }

            // Evicting a dedupe entry does not stop its operation. Bound unfinished
            // work independently, including requests still waiting on a stripe gate.
            if (_outstandingActions >= DedupeCapacity)
            {
                return Task.FromResult(Response(request, SalesStatusActionDisposition.RejectedUnavailable));
            }

            var completion = new TaskCompletionSource<SalesStatusActionResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _dedupe.Add(key, completion.Task);
            _dedupeOrder.Enqueue(key);
            while (_dedupeOrder.Count > DedupeCapacity)
            {
                _dedupe.Remove(_dedupeOrder.Dequeue());
            }

            _outstandingActions++;
            _ = CompleteAsync(identity, request, completion);
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task CompleteAsync(
        AuthenticatedClientIdentity identity,
        SalesStatusActionRequest request,
        TaskCompletionSource<SalesStatusActionResponse> completion)
    {
        var started = Stopwatch.GetTimestamp();
        SalesStatusActionResponse response;
        try
        {
            response = await ExecuteAsync(identity, request, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            _metrics.Increment(TransportMetric.SalesStatusFailed);
            response = Response(request, SalesStatusActionDisposition.Failed);
        }

        try
        {
            _logger.LogInformation(
                "Sales status action {Action} completed as {Disposition} in {ElapsedMilliseconds} ms.",
                request.DesiredStatus,
                response.Disposition,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            lock (_sync) { _outstandingActions--; }
            completion.TrySetResult(response);
        }
    }

    private async Task<SalesStatusActionResponse> ExecuteAsync(
        AuthenticatedClientIdentity identity,
        SalesStatusActionRequest request,
        CancellationToken cancellationToken)
    {
        _metrics.Increment(TransportMetric.SalesStatusRequested);
        if (identity.GuildId != _configuration.TargetGuildId)
        {
            return Response(request, SalesStatusActionDisposition.RejectedUnauthorized);
        }

        if (!TryConsumeRate(identity.DiscordUserId))
        {
            _metrics.Increment(TransportMetric.SalesStatusRateLimited);
            return Response(request, SalesStatusActionDisposition.RejectedRateLimited);
        }

        var version = NextVersion(request.MessageId);
        var gate = _gates[(int)(request.MessageId % GateStripeCount)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsLatest(request.MessageId, version) ||
                !_streams.IsCurrentGeneration(request.SalesGeneration))
            {
                return Response(request, SalesStatusActionDisposition.RejectedStale);
            }

            var access = await _authorization.AuthorizeChannelAsync(
                    identity,
                    _configuration.SalesChannelId,
                    forceRefresh: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (access.Status != ChatAuthorizationStatus.Authorized)
            {
                return Response(
                    request,
                    access.Status == ChatAuthorizationStatus.AccessRevoked
                        ? SalesStatusActionDisposition.RejectedUnauthorized
                        : SalesStatusActionDisposition.RejectedUnavailable);
            }

            if (access.BotReactionAuthorizedChannels is null ||
                !access.BotReactionAuthorizedChannels.Any(channel =>
                    channel.ChannelId == _configuration.SalesChannelId))
            {
                return Response(
                    request,
                    SalesStatusActionDisposition.RejectedUnavailable);
            }

            var canonical = await _source.GetMessageAsync(
                    _configuration.SalesChannelId,
                    request.MessageId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (canonical.Status != SalesStatusDiscordResult.Success ||
                canonical.Message is null)
            {
                return Response(request, Map(canonical.Status));
            }

            if (canonical.Message.AuthorId != identity.DiscordUserId)
            {
                _metrics.Increment(TransportMetric.SalesStatusNotOwner);
                return Response(request, SalesStatusActionDisposition.RejectedNotOwner);
            }

            if (!IsLatest(request.MessageId, version))
            {
                return Response(request, SalesStatusActionDisposition.RejectedStale);
            }

            var plan = CreatePlan(canonical.Message.Observation, request.DesiredStatus);
            if (plan.Count == 0)
            {
                _metrics.Increment(TransportMetric.SalesStatusNoOp);
                return Response(request, SalesStatusActionDisposition.NoOp);
            }

            foreach (var mutation in plan)
            {
                var result = mutation.Add
                    ? await _source.AddOwnReactionAsync(
                            canonical.Message,
                            mutation.Status,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await _source.RemoveOwnReactionAsync(
                            canonical.Message,
                            mutation.Status,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (result != SalesStatusDiscordResult.Success)
                {
                    return Response(request, Map(result));
                }
            }

            // Publish one authoritative canonical read-back. Gateway events may
            // coalesce with this request, but UI never treats the write response
            // itself as evidence that Discord accepted the final state.
            await _sales.RefreshCanonicalMessageAsync(request.MessageId, cancellationToken)
                .ConfigureAwait(false);
            _metrics.Increment(TransportMetric.SalesStatusAccepted);
            return Response(
                request,
                SalesStatusActionDisposition.Accepted,
                awaitingOfficialReadBack: true);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static IReadOnlyList<SalesStatusMutation> CreatePlan(
        SalesCompletionObservation observation,
        SalesStatus desired)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var present = new Dictionary<SalesStatus, bool>
        {
            [SalesStatus.Selling] = observation.BotSellingMarkerPresent,
            [SalesStatus.Negotiating] = observation.BotNegotiatingMarkerPresent,
            [SalesStatus.Completed] = observation.BotCompletedMarkerPresent,
        };
        var plan = new List<SalesStatusMutation>(4);
        if (desired != SalesStatus.Clear && !present[desired])
        {
            plan.Add(new SalesStatusMutation(desired, Add: true));
        }

        foreach (var status in new[]
                 {
                     SalesStatus.Selling,
                     SalesStatus.Negotiating,
                     SalesStatus.Completed,
                 })
        {
            if (present[status] && (desired == SalesStatus.Clear || status != desired))
            {
                plan.Add(new SalesStatusMutation(status, Add: false));
            }
        }

        return plan;
    }

    private bool TryConsumeRate(ulong userId)
    {
        lock (_sync)
        {
            var now = _clock();
            if (!_rateWindows.TryGetValue(userId, out var requests))
            {
                foreach (var window in _rateWindows.Values)
                {
                    while (window.TryPeek(out var timestamp) &&
                           now - timestamp >= RateWindow)
                    {
                        window.Dequeue();
                    }
                }

                foreach (var stale in _rateWindows.Where(pair => pair.Value.Count == 0)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _rateWindows.Remove(stale);
                }

                if (_rateWindows.Count >= RateWindowCapacity)
                {
                    var oldest = _rateWindows.MinBy(pair =>
                        pair.Value.TryPeek(out var timestamp)
                            ? timestamp
                            : DateTimeOffset.MinValue).Key;
                    _rateWindows.Remove(oldest);
                }

                requests = new Queue<DateTimeOffset>();
                _rateWindows[userId] = requests;
            }

            while (requests.TryPeek(out var timestamp) && now - timestamp >= RateWindow)
            {
                requests.Dequeue();
            }

            if (requests.Count >= MaximumRequestsPerMinute)
            {
                return false;
            }

            requests.Enqueue(now);
            return true;
        }
    }

    private long NextVersion(ulong messageId)
    {
        lock (_sync)
        {
            var version = checked(++_versionCounter);
            _versions[messageId] = (version, checked(++_touchCounter));
            if (_versions.Count > VersionCapacity)
            {
                var oldest = _versions.Where(pair => pair.Key != messageId)
                    .MinBy(pair => pair.Value.Touched).Key;
                _versions.Remove(oldest);
            }

            return version;
        }
    }

    private bool IsLatest(ulong messageId, long version)
    {
        lock (_sync)
        {
            return _versions.TryGetValue(messageId, out var current) &&
                current.Version == version;
        }
    }

    private static SalesStatusActionDisposition Map(SalesStatusDiscordResult result) =>
        result switch
        {
            SalesStatusDiscordResult.AccessDenied =>
                SalesStatusActionDisposition.RejectedUnavailable,
            SalesStatusDiscordResult.NotFound =>
                SalesStatusActionDisposition.RejectedMessageMissing,
            SalesStatusDiscordResult.RateLimited =>
                SalesStatusActionDisposition.RejectedRateLimited,
            SalesStatusDiscordResult.Unavailable =>
                SalesStatusActionDisposition.RejectedUnavailable,
            _ => SalesStatusActionDisposition.Failed,
        };

    private static SalesStatusActionResponse Response(
        SalesStatusActionRequest request,
        SalesStatusActionDisposition disposition,
        bool awaitingOfficialReadBack = false) => new(
            OverlayTransportProtocol.Version,
            request.ClientRequestId,
            disposition,
            awaitingOfficialReadBack);
}

internal sealed record SalesStatusMutation(SalesStatus Status, bool Add);

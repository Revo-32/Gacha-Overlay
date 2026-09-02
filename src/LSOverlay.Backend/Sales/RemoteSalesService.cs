using Discord;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Security;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Sales;

internal sealed record SalesBootstrapResult(
    ChatAuthorizationStatus Status,
    SalesBootstrapResponse? Response,
    string? Reason = null);

internal sealed record SalesSubscriptionResult(
    ChatAuthorizationStatus Status,
    SalesResumeResult? Resume,
    string? Reason = null);

internal sealed class RemoteSalesService
{
    private readonly BackendConfiguration _configuration;
    private readonly IChatAuthorizationService _authorization;
    private readonly IChatDiscordSource _source;
    private readonly DiscordChatMessageNormalizer _normalizer;
    private readonly ActiveSalesStreamRegistry _streams;
    private readonly CanonicalMessageRefreshCoalescer _refreshes;

    public RemoteSalesService(
        BackendConfiguration configuration,
        IChatAuthorizationService authorization,
        IChatDiscordSource source,
        DiscordChatMessageNormalizer normalizer,
        ActiveSalesStreamRegistry streams)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _refreshes = new CanonicalMessageRefreshCoalescer(
            RefreshCanonicalAsync,
            _ => _streams.PublishResyncRequired());
    }

    public ulong ChannelId => _configuration.SalesChannelId;

    public async Task<SalesBootstrapResult> BootstrapAsync(
        AuthenticatedClientIdentity identity,
        SalesBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(request);
        OverlayProtocolJson.EnsureVersion(request.ProtocolVersion);
        var access = await AuthorizeAsync(identity, forceRefresh: false, cancellationToken)
            .ConfigureAwait(false);
        if (access.Status != ChatAuthorizationStatus.Authorized || access.Channel is null)
        {
            return new SalesBootstrapResult(access.Status, null, access.Status.ToString());
        }

        // Activate before REST so mutations racing with the snapshot enter the journal.
        var capture = _streams.Activate();
        var recent = await _source.GetRecentMessagesAsync(
                ChannelId,
                ActiveSalesStreamRegistry.AuthoritativeWindowSize,
                cancellationToken)
            .ConfigureAwait(false);
        if (recent.Status != ChatSourceStatus.Available)
        {
            return new SalesBootstrapResult(
                recent.Status == ChatSourceStatus.NotFound
                    ? ChatAuthorizationStatus.ChannelUnavailable
                    : ChatAuthorizationStatus.AuthorizationUnavailable,
                null,
                recent.Status.ToString());
        }

        var messages = await _normalizer.NormalizeManyAsync(
                identity.GuildId,
                recent.Messages,
                cancellationToken)
            .ConfigureAwait(false);
        var observations = recent.Messages
            .Select(message => CreateObservation(message, SalesEvidenceCoverage.Complete))
            .ToArray();

        var completed = _streams.CompleteBootstrap(capture, messages, observations);
        if (completed.Disposition != SalesResumeDisposition.Resumable)
        {
            return new SalesBootstrapResult(
                ChatAuthorizationStatus.ChannelUnavailable,
                null,
                completed.Disposition.ToString());
        }

        var coverage = DetermineBootstrapCoverage(recent.Messages.Count);
        return new SalesBootstrapResult(
            ChatAuthorizationStatus.Authorized,
            new SalesBootstrapResponse(
                OverlayTransportProtocol.Version,
                access.Channel,
                completed.Generation,
                completed.LatestSequence,
                completed.Messages,
                completed.Observations,
                coverage));
    }

    internal static SalesBootstrapCoverage DetermineBootstrapCoverage(int messageCount)
    {
        if (messageCount is < 0 or > ActiveSalesStreamRegistry.AuthoritativeWindowSize)
        {
            throw new ArgumentOutOfRangeException(nameof(messageCount));
        }

        // The registry's retained latest-message window is the product's complete
        // authoritative Sales domain. Reaching the window capacity is not proof of
        // missing data inside that domain and must not prevent Remote promotion.
        return SalesBootstrapCoverage.Complete;
    }

    public async Task<SalesSubscriptionResult> SubscribeAsync(
        AuthenticatedClientIdentity identity,
        string generation,
        long afterSequence,
        bool forceAuthorizationRefresh,
        CancellationToken cancellationToken)
    {
        var access = await AuthorizeAsync(
                identity,
                forceAuthorizationRefresh,
                cancellationToken)
            .ConfigureAwait(false);
        if (access.Status != ChatAuthorizationStatus.Authorized)
        {
            return new SalesSubscriptionResult(access.Status, null, access.Status.ToString());
        }

        var resume = _streams.PrepareResume(generation, afterSequence);
        return resume.Disposition == SalesResumeDisposition.Resumable
            ? new SalesSubscriptionResult(ChatAuthorizationStatus.Authorized, resume)
            : new SalesSubscriptionResult(
                ChatAuthorizationStatus.ChannelUnavailable,
                resume,
                resume.Disposition.ToString());
    }

    public Task<ChatAuthorizationResult> RefreshAuthorizationAsync(
        AuthenticatedClientIdentity identity,
        CancellationToken cancellationToken) =>
        AuthorizeAsync(identity, forceRefresh: true, cancellationToken);

    public async Task ReceiveCreateAsync(
        ulong guildId,
        IMessage message,
        CancellationToken cancellationToken = default)
    {
        if (!Accepts(guildId, message.Channel.Id) || !_streams.IsActive)
        {
            return;
        }

        _streams.PublishUpsert(
            OverlayTransportProtocol.SalesMessageCreate,
            await _normalizer.NormalizeAsync(guildId, message, cancellationToken)
                .ConfigureAwait(false),
            CreateObservation(message, SalesEvidenceCoverage.Complete));
    }

    public Task ReceiveUpdateAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken = default) =>
        Accepts(guildId, channelId) && _streams.IsActive
            ? _refreshes.RequestAsync(channelId, messageId, cancellationToken)
            : Task.CompletedTask;

    public void ReceiveDelete(ulong guildId, ulong channelId, ulong messageId)
    {
        if (Accepts(guildId, channelId) && _streams.PublishDelete(channelId, messageId))
        {
            // A real Discord delete can expose the next-older post at the edge of
            // the authoritative window. Ask clients for a Sales-only canonical
            // refresh after preserving the exact-delete mutation.
            _streams.PublishResyncRequired();
        }
    }

    public Task ReceiveReactionChangedAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken = default)
    {
        if (!Accepts(guildId, channelId) || !_streams.IsActive)
        {
            return Task.CompletedTask;
        }

        // A single REMOVE event only represents one user's reaction. Canonical REST
        // refresh is therefore required before publishing an absent marker.
        return _refreshes.RequestAsync(channelId, messageId, cancellationToken);
    }

    public Task RefreshCanonicalMessageAsync(
        ulong messageId,
        CancellationToken cancellationToken = default) =>
        _streams.IsActive
            ? _refreshes.RequestAsync(ChannelId, messageId, cancellationToken)
            : Task.CompletedTask;

    public void MarkUncertain() => _streams.PublishResyncRequired();

    public void ReceiveChannelDeleted(ulong guildId, ulong channelId)
    {
        if (!Accepts(guildId, channelId))
        {
            return;
        }

        _authorization.InvalidateGuild(guildId);
        _streams.PublishResyncRequired();
    }

    private async Task<ChatAuthorizationResult> AuthorizeAsync(
        AuthenticatedClientIdentity identity,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (identity.GuildId != _configuration.TargetGuildId)
        {
            return new ChatAuthorizationResult(
                ChatAuthorizationStatus.AccessRevoked,
                null,
                Array.Empty<ChatChannelDescriptor>(),
                DateTimeOffset.UtcNow);
        }

        return await _authorization.AuthorizeChannelAsync(
                identity,
                ChannelId,
                forceRefresh,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RefreshCanonicalAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken)
    {
        if (channelId != ChannelId || !_streams.IsActive)
        {
            return;
        }

        var result = await _source.GetMessageAsync(channelId, messageId, cancellationToken)
            .ConfigureAwait(false);
        switch (result.Status)
        {
            case ChatSourceStatus.Available when result.Message is not null:
                _streams.PublishUpsert(
                    OverlayTransportProtocol.SalesMessageUpdate,
                    await _normalizer.NormalizeAsync(
                            _configuration.TargetGuildId,
                            result.Message,
                            cancellationToken)
                        .ConfigureAwait(false),
                    CreateObservation(result.Message, SalesEvidenceCoverage.Complete));
                break;
            case ChatSourceStatus.NotFound:
                if (_streams.PublishDelete(channelId, messageId))
                {
                    _streams.PublishResyncRequired();
                }
                break;
            default:
                _streams.PublishResyncRequired();
                break;
        }
    }

    private bool Accepts(ulong guildId, ulong channelId) =>
        guildId == _configuration.TargetGuildId && channelId == ChannelId;

    internal static SalesCompletionObservation CreateObservation(
        IMessage message,
        SalesEvidenceCoverage coverage)
    {
        var sold = false;
        var closed = false;
        var botSelling = false;
        var botNegotiating = false;
        var botCompleted = false;
        foreach (var pair in message.Reactions)
        {
            if (pair.Value.ReactionCount <= 0)
            {
                continue;
            }

            var id = pair.Key is Emote custom ? custom.Id : (ulong?)null;
            sold |= RemoteSalesPolicy.IsSoldMarker(id, pair.Key.Name);
            closed |= RemoteSalesPolicy.IsClosedMarker(id, pair.Key.Name);
            if (pair.Value.IsMe)
            {
                botSelling |= RemoteSalesPolicy.IsSellingMarker(id, pair.Key.Name);
                botNegotiating |= RemoteSalesPolicy.IsNegotiatingMarker(id, pair.Key.Name);
                botCompleted |= RemoteSalesPolicy.IsSoldMarker(id, pair.Key.Name);
            }
        }

        return new SalesCompletionObservation(
            message.Id,
            sold,
            closed,
            coverage,
            DateTimeOffset.UtcNow,
            botSelling,
            botNegotiating,
            botCompleted);
    }
}

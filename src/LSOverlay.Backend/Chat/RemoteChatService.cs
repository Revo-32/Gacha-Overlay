using Discord;
using LSOverlay.Backend.Security;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Chat;

internal sealed record ChatBootstrapResult(
    ChatAuthorizationStatus Status,
    ChatBootstrapResponse? Response,
    string? Reason = null);

internal sealed record ChatSubscriptionResult(
    ChatAuthorizationStatus Status,
    ChatResumeResult? Resume,
    string? Reason = null);

internal sealed class RemoteChatService
{
    private readonly IChatAuthorizationService _authorization;
    private readonly IChatDiscordSource _source;
    private readonly DiscordChatMessageNormalizer _normalizer;
    private readonly CanonicalRemoteAuthorResolver _authors;
    private readonly ActiveChatStreamRegistry _streams;
    private readonly CanonicalMessageRefreshCoalescer _updates;

    public RemoteChatService(
        IChatAuthorizationService authorization,
        IChatDiscordSource source,
        DiscordChatMessageNormalizer normalizer,
        CanonicalRemoteAuthorResolver authors,
        ActiveChatStreamRegistry streams)
    {
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _authors = authors ?? throw new ArgumentNullException(nameof(authors));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _updates = new CanonicalMessageRefreshCoalescer(
            RefreshCanonicalAsync,
            channelId => _streams.PublishResyncRequired(channelId));
    }

    public async Task<ChatAuthorizationResult> GetCatalogAsync(
        AuthenticatedClientIdentity identity,
        CancellationToken cancellationToken) =>
        await _authorization.GetCatalogAsync(identity, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ChatBootstrapResult> BootstrapAsync(
        AuthenticatedClientIdentity identity,
        ChatBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(request);
        OverlayProtocolJson.EnsureVersion(request.ProtocolVersion);
        var access = await _authorization.AuthorizeChannelAsync(
                identity,
                request.ChannelId,
                forceRefresh: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (access.Status != ChatAuthorizationStatus.Authorized || access.Channel is null)
        {
            return new ChatBootstrapResult(access.Status, null, access.Status.ToString());
        }

        ChatBootstrapCapture capture;
        try
        {
            // Activation precedes REST so gateway mutations are journaled during the fetch.
            capture = _streams.Activate(access.Channel);
        }
        catch (InvalidOperationException)
        {
            return new ChatBootstrapResult(
                ChatAuthorizationStatus.ChannelUnavailable,
                null,
                "ActiveChannelCapacity");
        }

        var recent = await _source.GetRecentMessagesAsync(
                request.ChannelId,
                ActiveChatStreamRegistry.BootstrapMessageLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (recent.Status != ChatSourceStatus.Available)
        {
            return new ChatBootstrapResult(
                recent.Status == ChatSourceStatus.NotFound
                    ? ChatAuthorizationStatus.ChannelUnavailable
                    : ChatAuthorizationStatus.AuthorizationUnavailable,
                null,
                recent.Status.ToString());
        }

        var normalized = await _normalizer.NormalizeManyAsync(
                identity.GuildId,
                recent.Messages,
                cancellationToken)
            .ConfigureAwait(false);

        var completed = _streams.CompleteBootstrap(capture, normalized);
        if (completed.Disposition != ChatResumeDisposition.Resumable)
        {
            return new ChatBootstrapResult(
                ChatAuthorizationStatus.ChannelUnavailable,
                null,
                completed.Disposition.ToString());
        }

        return new ChatBootstrapResult(
            ChatAuthorizationStatus.Authorized,
            new ChatBootstrapResponse(
                OverlayTransportProtocol.Version,
                access.Channel,
                completed.Generation,
                completed.LatestSequence,
                completed.Messages));
    }

    public async Task<ChatSubscriptionResult> SubscribeAsync(
        AuthenticatedClientIdentity identity,
        ulong channelId,
        string generation,
        long afterSequence,
        bool forceAuthorizationRefresh,
        CancellationToken cancellationToken)
    {
        var access = await _authorization.AuthorizeChannelAsync(
                identity,
                channelId,
                forceAuthorizationRefresh,
                cancellationToken)
            .ConfigureAwait(false);
        if (access.Status != ChatAuthorizationStatus.Authorized)
        {
            return new ChatSubscriptionResult(access.Status, null, access.Status.ToString());
        }

        var resume = _streams.PrepareResume(channelId, generation, afterSequence);
        return resume.Disposition == ChatResumeDisposition.Resumable
            ? new ChatSubscriptionResult(ChatAuthorizationStatus.Authorized, resume)
            : new ChatSubscriptionResult(
                ChatAuthorizationStatus.ChannelUnavailable,
                resume,
                resume.Disposition.ToString());
    }

    public bool IsActive(ulong channelId) => _streams.IsActive(channelId);

    public async Task<ChatAuthorizationResult> RefreshAuthorizationAsync(
        AuthenticatedClientIdentity identity,
        ulong channelId,
        CancellationToken cancellationToken) =>
        await _authorization.AuthorizeChannelAsync(
                identity,
                channelId,
                forceRefresh: true,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task ReceiveCreateAsync(
        ulong guildId,
        IMessage message,
        CancellationToken cancellationToken = default)
    {
        if (!_streams.IsActive(message.Channel.Id))
        {
            return;
        }

        _streams.PublishUpsert(
            OverlayTransportProtocol.ChatMessageCreate,
            await _normalizer.NormalizeAsync(guildId, message, cancellationToken)
                .ConfigureAwait(false));
    }

    public Task ReceiveUpdateAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken = default) =>
        _streams.IsActive(channelId)
            ? _updates.RequestAsync(channelId, messageId, cancellationToken)
            : Task.CompletedTask;

    public void ReceiveDelete(ulong channelId, ulong messageId) =>
        _streams.PublishDelete(channelId, messageId);

    public void InvalidateGuildAuthorization(ulong guildId) =>
        _authorization.InvalidateGuild(guildId);

    public void InvalidateAuthor(ulong guildId, ulong authorId) =>
        _authors.Invalidate(guildId, authorId);

    public void ReceiveRoleCatalogChanged(ulong guildId)
    {
        _authorization.InvalidateGuild(guildId);
        _authors.InvalidateGuild(guildId);
        _streams.PublishResyncRequiredForGuild(guildId);
    }

    public void ReceiveMemberRolesChanged(ulong guildId, ulong authorId)
    {
        _authorization.InvalidateGuild(guildId);
        _authors.Invalidate(guildId, authorId);
        _streams.PublishResyncRequiredForAuthor(guildId, authorId);
    }

    public void ReceiveChannelDeleted(ulong guildId, ulong channelId)
    {
        _authorization.InvalidateGuild(guildId);
        _streams.RemoveChannel(channelId);
    }

    private async Task RefreshCanonicalAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken)
    {
        if (!_streams.IsActive(channelId))
        {
            return;
        }

        var result = await _source.GetMessageAsync(channelId, messageId, cancellationToken)
            .ConfigureAwait(false);
        switch (result.Status)
        {
            case ChatSourceStatus.Available when result.Message is not null:
                var guildId = result.Message.Channel is IGuildChannel guildChannel
                    ? guildChannel.GuildId
                    : 0;
                _streams.PublishUpsert(
                    OverlayTransportProtocol.ChatMessageUpdate,
                    await _normalizer.NormalizeAsync(
                            guildId,
                            result.Message,
                            cancellationToken)
                        .ConfigureAwait(false));
                break;
            case ChatSourceStatus.NotFound:
                _streams.PublishDelete(channelId, messageId);
                break;
            default:
                _streams.PublishResyncRequired(channelId);
                break;
        }
    }
}

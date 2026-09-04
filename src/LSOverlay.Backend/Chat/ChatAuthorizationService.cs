using LSOverlay.Backend.Security;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Chat;

internal enum ChatAuthorizationStatus
{
    Authorized,
    AccessRevoked,
    AuthorizationUnavailable,
    ChannelUnavailable,
}

internal sealed record ChatAuthorizationResult(
    ChatAuthorizationStatus Status,
    ChatChannelDescriptor? Channel,
    IReadOnlyList<ChatChannelDescriptor> AuthorizedChannels,
    DateTimeOffset ValidUntil,
    IReadOnlyList<ChatChannelDescriptor>? BotReactionAuthorizedChannels = null)
{
    public static ChatAuthorizationResult Unavailable(DateTimeOffset now) => new(
        ChatAuthorizationStatus.AuthorizationUnavailable,
        null,
        Array.Empty<ChatChannelDescriptor>(),
        now);
}

internal interface IChatAuthorizationService
{
    Task<ChatAuthorizationResult> GetCatalogAsync(
        AuthenticatedClientIdentity identity,
        CancellationToken cancellationToken);

    Task<ChatAuthorizationResult> AuthorizeChannelAsync(
        AuthenticatedClientIdentity identity,
        ulong channelId,
        bool forceRefresh,
        CancellationToken cancellationToken);

    void InvalidateGuild(ulong guildId);
}

internal sealed class ChatAuthorizationService : IChatAuthorizationService
{
    public static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(2);
    public const int MaximumLeases = Security.ClientCredentialRegistry.MaximumCredentials;

    private sealed record Lease(
        ChatAuthorizationResult Result,
        DateTimeOffset ExpiresAt);

    private readonly object _sync = new();
    private readonly IChatDiscordSource _source;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<(ulong GuildId, ulong UserId), Lease> _leases = new();
    private readonly Dictionary<(ulong GuildId, ulong UserId), Task<Lease>> _refreshes = new();
    private readonly Dictionary<ulong, long> _guildVersions = new();

    public ChatAuthorizationService(IChatDiscordSource source)
        : this(source, () => DateTimeOffset.UtcNow)
    {
    }

    internal ChatAuthorizationService(
        IChatDiscordSource source,
        Func<DateTimeOffset> clock)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ChatAuthorizationResult> GetCatalogAsync(
        AuthenticatedClientIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return (await GetLeaseAsync(identity, forceRefresh: false, cancellationToken)
            .ConfigureAwait(false)).Result;
    }

    public async Task<ChatAuthorizationResult> AuthorizeChannelAsync(
        AuthenticatedClientIdentity identity,
        ulong channelId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (channelId == 0)
        {
            return new ChatAuthorizationResult(
                ChatAuthorizationStatus.ChannelUnavailable,
                null,
                Array.Empty<ChatChannelDescriptor>(),
                _clock());
        }

        var lease = await GetLeaseAsync(identity, forceRefresh, cancellationToken)
            .ConfigureAwait(false);
        if (lease.Result.Status != ChatAuthorizationStatus.Authorized)
        {
            return lease.Result;
        }

        var channel = lease.Result.AuthorizedChannels.FirstOrDefault(candidate =>
            candidate.ChannelId == channelId);
        return channel is null
            ? lease.Result with
            {
                Status = ChatAuthorizationStatus.AccessRevoked,
                Channel = null,
            }
            : lease.Result with { Channel = channel };
    }

    public void InvalidateGuild(ulong guildId)
    {
        lock (_sync)
        {
            _guildVersions[guildId] = checked(_guildVersions.GetValueOrDefault(guildId) + 1);
            foreach (var key in _leases.Keys.Where(key => key.GuildId == guildId).ToArray())
            {
                _leases.Remove(key);
            }
        }
    }

    private Task<Lease> GetLeaseAsync(
        AuthenticatedClientIdentity identity,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var key = (identity.GuildId, identity.DiscordUserId);
        lock (_sync)
        {
            var now = _clock();
            if (!forceRefresh &&
                _leases.TryGetValue(key, out var cached) &&
                cached.ExpiresAt > now)
            {
                return Task.FromResult(cached);
            }

            if (_refreshes.TryGetValue(key, out var refresh))
            {
                return refresh.WaitAsync(cancellationToken);
            }

            if (_refreshes.Count >= MaximumLeases)
            {
                return Task.FromResult(new Lease(ChatAuthorizationResult.Unavailable(now), now));
            }

            var completion = new TaskCompletionSource<Lease>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _refreshes.Add(key, completion.Task);
            var guildVersion = _guildVersions.GetValueOrDefault(identity.GuildId);
            _ = CompleteRefreshAsync(identity, key, guildVersion, completion);
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task CompleteRefreshAsync(
        AuthenticatedClientIdentity identity,
        (ulong GuildId, ulong UserId) key,
        long guildVersion,
        TaskCompletionSource<Lease> completion)
    {
        try
        {
            completion.TrySetResult(await RefreshCoreAsync(identity, key, guildVersion)
                .ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_sync)
            {
                _refreshes.Remove(key);
            }
        }
    }

    private async Task<Lease> RefreshCoreAsync(
        AuthenticatedClientIdentity identity,
        (ulong GuildId, ulong UserId) key,
        long guildVersion)
    {
        Lease lease;
        var source = await _source.GetGuildAsync(identity, CancellationToken.None)
            .ConfigureAwait(false);
        var now = _clock();
        if (source.Status == ChatSourceStatus.Unavailable)
        {
            lease = new Lease(ChatAuthorizationResult.Unavailable(now), now);
        }
        else if (source.Status != ChatSourceStatus.Available || source.Guild is null)
        {
            lease = new Lease(new ChatAuthorizationResult(
                ChatAuthorizationStatus.AccessRevoked,
                null,
                Array.Empty<ChatChannelDescriptor>(),
                now.Add(LeaseLifetime)), now.Add(LeaseLifetime));
        }
        else
        {
            var guild = source.Guild;
            var authorized = guild.Channels
                .Where(channel =>
                    channel.Descriptor.ChannelId != GtaCompanionProtocolPolicy.ProductionEventChannelId &&
                    CanRead(guild, guild.User, channel) &&
                    CanRead(guild, guild.Bot, channel))
                .Select(channel => channel.Descriptor)
                .OrderBy(channel => channel.Position)
                .ThenBy(channel => channel.ChannelId)
                .ToArray();
            var reactionAuthorized = guild.Channels
                .Where(channel =>
                    channel.Descriptor.ChannelId != GtaCompanionProtocolPolicy.ProductionEventChannelId &&
                    CanRead(guild, guild.User, channel) &&
                    CanAddReactions(guild, guild.Bot, channel))
                .Select(channel => channel.Descriptor)
                .OrderBy(channel => channel.Position)
                .ThenBy(channel => channel.ChannelId)
                .ToArray();
            var expiresAt = now.Add(LeaseLifetime);
            lease = new Lease(new ChatAuthorizationResult(
                ChatAuthorizationStatus.Authorized,
                null,
                authorized,
                expiresAt,
                reactionAuthorized), expiresAt);
        }

        lock (_sync)
        {
            if (_guildVersions.GetValueOrDefault(identity.GuildId) != guildVersion)
            {
                var invalidatedAt = _clock();
                return new Lease(
                    ChatAuthorizationResult.Unavailable(invalidatedAt),
                    invalidatedAt);
            }

            if (_leases.Count >= MaximumLeases && !_leases.ContainsKey(key))
            {
                var oldest = _leases.MinBy(pair => pair.Value.ExpiresAt).Key;
                _leases.Remove(oldest);
            }

            _leases[key] = lease;
        }

        return lease;
    }

    private static bool CanRead(
        ChatGuildSnapshot guild,
        ChatMemberSnapshot member,
        ChatChannelSnapshot channel)
    {
        var permissions = DiscordPermissionEvaluator.Compute(
            guild.GuildId,
            member.UserId,
            member.RoleIds,
            guild.Roles,
            channel.Overwrites);
        return DiscordPermissionEvaluator.CanRead(permissions);
    }

    private static bool CanAddReactions(
        ChatGuildSnapshot guild,
        ChatMemberSnapshot member,
        ChatChannelSnapshot channel)
    {
        var permissions = DiscordPermissionEvaluator.Compute(
            guild.GuildId,
            member.UserId,
            member.RoleIds,
            guild.Roles,
            channel.Overwrites);
        return DiscordPermissionEvaluator.CanAddReactions(permissions);
    }
}

using Discord;
using Discord.Net;
using Discord.WebSocket;
using LSOverlay.Backend.Discord;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Chat;

internal sealed record RemoteAuthorObservation(
    ulong AuthorId,
    string? Username,
    string? GlobalDisplayName,
    string? ExactGuildNickname,
    bool IsBot,
    bool IsWebhook)
{
    public static RemoteAuthorObservation From(IUser author)
    {
        ArgumentNullException.ThrowIfNull(author);
        return new RemoteAuthorObservation(
            author.Id,
            author.Username,
            author.GlobalName,
            (author as IGuildUser)?.Nickname,
            author.IsBot,
            author.IsWebhook);
    }
}

internal enum RemoteGuildMemberResolutionStatus
{
    Available,
    NotFound,
    Unavailable,
}

internal sealed record RemoteGuildMemberResolution(
    RemoteGuildMemberResolutionStatus Status,
    string? GuildNickname = null);

internal interface IRemoteGuildMemberSource
{
    Task<RemoteGuildMemberResolution> ResolveAsync(
        ulong guildId,
        ulong authorId,
        CancellationToken cancellationToken);
}

internal sealed class DiscordNetRemoteGuildMemberSource : IRemoteGuildMemberSource
{
    private readonly DiscordSocketClient _client;
    private readonly TargetGuildFilter _guildFilter;

    public DiscordNetRemoteGuildMemberSource(
        DiscordSocketClient client,
        TargetGuildFilter guildFilter)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _guildFilter = guildFilter ?? throw new ArgumentNullException(nameof(guildFilter));
    }

    public async Task<RemoteGuildMemberResolution> ResolveAsync(
        ulong guildId,
        ulong authorId,
        CancellationToken cancellationToken)
    {
        if (!_guildFilter.Accepts(guildId))
        {
            return new RemoteGuildMemberResolution(
                RemoteGuildMemberResolutionStatus.NotFound);
        }

        try
        {
            var cached = _client.GetGuild(guildId)?.GetUser(authorId);
            if (cached is not null)
            {
                return new RemoteGuildMemberResolution(
                    RemoteGuildMemberResolutionStatus.Available,
                    cached.Nickname);
            }

            var member = await _client.Rest.GetGuildUserAsync(guildId, authorId)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return member is null
                ? new RemoteGuildMemberResolution(RemoteGuildMemberResolutionStatus.NotFound)
                : new RemoteGuildMemberResolution(
                    RemoteGuildMemberResolutionStatus.Available,
                    member.Nickname);
        }
        catch (HttpException exception)
            when (exception.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            return new RemoteGuildMemberResolution(
                RemoteGuildMemberResolutionStatus.NotFound);
        }
        catch (Exception exception) when (IsTemporary(exception, cancellationToken))
        {
            return new RemoteGuildMemberResolution(
                RemoteGuildMemberResolutionStatus.Unavailable);
        }
    }

    private static bool IsTemporary(
        Exception exception,
        CancellationToken cancellationToken) =>
        exception is HttpException or TimeoutException ||
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}

internal sealed class CanonicalRemoteAuthorResolver
{
    internal const int MaximumCacheEntries = 512;
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan UnavailableBackoffLifetime = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly IRemoteGuildMemberSource _source;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<AuthorCacheKey, AuthorCacheEntry> _cache = new();
    private long _accessStamp;

    public CanonicalRemoteAuthorResolver(
        IRemoteGuildMemberSource source,
        Func<DateTimeOffset>? utcNow = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal int CachedEntryCount
    {
        get
        {
            lock (_sync)
            {
                RemoveExpiredLocked(_utcNow());
                return _cache.Count;
            }
        }
    }

    public async Task<ChatAuthor> ResolveAsync(
        ulong guildId,
        RemoteAuthorObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var key = new AuthorCacheKey(guildId, observation.AuthorId);
        var currentNickname = Normalize(observation.ExactGuildNickname);
        if (currentNickname is not null)
        {
            SetCache(key, currentNickname);
            return CreateAuthor(observation, currentNickname);
        }

        if (TryGetCache(key, out var cachedNickname))
        {
            return CreateAuthor(observation, cachedNickname);
        }

        string? resolvedNickname = null;
        if (guildId != 0 && observation.AuthorId != 0 && !observation.IsWebhook)
        {
            var resolved = await _source.ResolveAsync(
                    guildId,
                    observation.AuthorId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resolved.Status is RemoteGuildMemberResolutionStatus.Available or
                RemoteGuildMemberResolutionStatus.NotFound)
            {
                resolvedNickname = Normalize(resolved.GuildNickname);
                SetCache(key, resolvedNickname);
            }
            else
            {
                SetCache(key, null, UnavailableBackoffLifetime);
            }
        }

        return CreateAuthor(observation, resolvedNickname);
    }

    public void Invalidate(ulong guildId, ulong authorId)
    {
        lock (_sync)
        {
            _cache.Remove(new AuthorCacheKey(guildId, authorId));
        }
    }

    public void InvalidateGuild(ulong guildId)
    {
        lock (_sync)
        {
            foreach (var key in _cache.Keys.Where(key => key.GuildId == guildId).ToArray())
            {
                _cache.Remove(key);
            }
        }
    }

    private static ChatAuthor CreateAuthor(
        RemoteAuthorObservation observation,
        string? guildNickname)
    {
        var username = Normalize(observation.Username) ?? "Unknown";
        var displayName = guildNickname ??
            Normalize(observation.GlobalDisplayName) ??
            username;
        return new ChatAuthor(
            observation.AuthorId,
            username,
            displayName,
            guildNickname,
            observation.IsBot,
            observation.IsWebhook);
    }

    private bool TryGetCache(AuthorCacheKey key, out string? nickname)
    {
        lock (_sync)
        {
            var now = _utcNow();
            if (!_cache.TryGetValue(key, out var entry))
            {
                nickname = null;
                return false;
            }

            if (entry.ExpiresAt <= now)
            {
                _cache.Remove(key);
                nickname = null;
                return false;
            }

            nickname = entry.GuildNickname;
            _cache[key] = entry with { AccessStamp = ++_accessStamp };
            return true;
        }
    }

    private void SetCache(
        AuthorCacheKey key,
        string? nickname,
        TimeSpan? lifetime = null)
    {
        lock (_sync)
        {
            var now = _utcNow();
            RemoveExpiredLocked(now);
            if (!_cache.ContainsKey(key) && _cache.Count >= MaximumCacheEntries)
            {
                var oldest = _cache.MinBy(pair => pair.Value.AccessStamp).Key;
                _cache.Remove(oldest);
            }

            _cache[key] = new AuthorCacheEntry(
                nickname,
                now + (lifetime ?? CacheLifetime),
                ++_accessStamp);
        }
    }

    private void RemoveExpiredLocked(DateTimeOffset now)
    {
        foreach (var key in _cache
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _cache.Remove(key);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct AuthorCacheKey(ulong GuildId, ulong AuthorId);

    private sealed record AuthorCacheEntry(
        string? GuildNickname,
        DateTimeOffset ExpiresAt,
        long AccessStamp);
}

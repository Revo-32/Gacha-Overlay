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
    bool IsWebhook,
    IReadOnlyCollection<ulong>? RoleIds = null)
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
            author.IsWebhook,
            (author as IGuildUser)?.RoleIds.ToArray());
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
    string? GuildNickname = null,
    IReadOnlyCollection<ulong>? RoleIds = null,
    ChatAuthorStyle? RoleStyle = null);

internal sealed record RemoteRoleDefinition(
    ulong Id,
    int Position,
    uint Color,
    string? IconHash,
    string? UnicodeEmoji);

internal static class RemoteRoleStyleSelector
{
    public static ChatAuthorStyle? Select(
        IReadOnlyCollection<ulong>? memberRoleIds,
        IEnumerable<RemoteRoleDefinition> guildRoles)
    {
        if (memberRoleIds is null || memberRoleIds.Count == 0)
        {
            return null;
        }

        var roleIds = memberRoleIds.ToHashSet();
        var roles = guildRoles
            .Where(role => roleIds.Contains(role.Id))
            .OrderByDescending(role => role.Position)
            .ThenByDescending(role => role.Id)
            .ToArray();
        var colorRole = roles.FirstOrDefault(role => role.Color != 0);
        var iconRole = roles.FirstOrDefault(role =>
            !string.IsNullOrWhiteSpace(role.IconHash) ||
            !string.IsNullOrWhiteSpace(role.UnicodeEmoji));
        if (colorRole is null && iconRole is null)
        {
            return null;
        }

        ChatRoleIcon? icon = null;
        if (!string.IsNullOrWhiteSpace(iconRole?.IconHash))
        {
            icon = new ChatRoleIcon(
                "image",
                iconRole.IconHash!,
                $"https://cdn.discordapp.com/role-icons/{iconRole.Id}/{iconRole.IconHash}.png?size=32&quality=lossless");
        }
        else if (!string.IsNullOrWhiteSpace(iconRole?.UnicodeEmoji))
        {
            icon = new ChatRoleIcon("unicode", iconRole.UnicodeEmoji!);
        }

        return new ChatAuthorStyle(
            colorRole?.Id,
            colorRole?.Color,
            iconRole?.Id,
            icon);
    }
}

internal interface IRemoteGuildRoleStyleSource
{
    ChatAuthorStyle? ResolveRoleStyle(
        ulong guildId,
        IReadOnlyCollection<ulong>? roleIds);
}

internal interface IRemoteGuildMemberSource
{
    Task<RemoteGuildMemberResolution> ResolveAsync(
        ulong guildId,
        ulong authorId,
        CancellationToken cancellationToken);
}

internal sealed class DiscordNetRemoteGuildMemberSource :
    IRemoteGuildMemberSource,
    IRemoteGuildRoleStyleSource
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
                var roleIds = cached.Roles.Select(role => role.Id).ToArray();
                return new RemoteGuildMemberResolution(
                    RemoteGuildMemberResolutionStatus.Available,
                    cached.Nickname,
                    roleIds,
                    ResolveRoleStyle(guildId, roleIds));
            }

            var member = await _client.Rest.GetGuildUserAsync(guildId, authorId)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return member is null
                ? new RemoteGuildMemberResolution(RemoteGuildMemberResolutionStatus.NotFound)
                : new RemoteGuildMemberResolution(
                    RemoteGuildMemberResolutionStatus.Available,
                    member.Nickname,
                    member.RoleIds.ToArray(),
                    ResolveRoleStyle(guildId, member.RoleIds));
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

    public ChatAuthorStyle? ResolveRoleStyle(
        ulong guildId,
        IReadOnlyCollection<ulong>? roleIds)
    {
        var guild = _client.GetGuild(guildId);
        return guild is null
            ? null
            : RemoteRoleStyleSelector.Select(
                roleIds,
                guild.Roles.Select(role => new RemoteRoleDefinition(
                    role.Id,
                    role.Position,
                    role.Colors.PrimaryColor.RawValue,
                    role.Icon,
                    role.Emoji?.Name)));
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
        var observedStyle = (_source as IRemoteGuildRoleStyleSource)?
            .ResolveRoleStyle(guildId, observation.RoleIds);
        if (currentNickname is not null || observedStyle is not null)
        {
            SetCache(key, currentNickname, observedStyle);
            return CreateAuthor(observation, currentNickname, observedStyle);
        }

        if (TryGetCache(key, out var cachedNickname, out var cachedStyle))
        {
            return CreateAuthor(observation, cachedNickname, cachedStyle);
        }

        string? resolvedNickname = null;
        ChatAuthorStyle? resolvedStyle = null;
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
                resolvedStyle = resolved.RoleStyle ??
                    (_source as IRemoteGuildRoleStyleSource)?
                    .ResolveRoleStyle(guildId, resolved.RoleIds);
                SetCache(key, resolvedNickname, resolvedStyle);
            }
            else
            {
                SetCache(key, null, null, UnavailableBackoffLifetime);
            }
        }

        return CreateAuthor(observation, resolvedNickname, resolvedStyle);
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
        string? guildNickname,
        ChatAuthorStyle? roleStyle)
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
            observation.IsWebhook)
        {
            RoleStyle = roleStyle,
        };
    }

    private bool TryGetCache(
        AuthorCacheKey key,
        out string? nickname,
        out ChatAuthorStyle? roleStyle)
    {
        lock (_sync)
        {
            var now = _utcNow();
            if (!_cache.TryGetValue(key, out var entry))
            {
                nickname = null;
                roleStyle = null;
                return false;
            }

            if (entry.ExpiresAt <= now)
            {
                _cache.Remove(key);
                nickname = null;
                roleStyle = null;
                return false;
            }

            nickname = entry.GuildNickname;
            roleStyle = entry.RoleStyle;
            _cache[key] = entry with { AccessStamp = ++_accessStamp };
            return true;
        }
    }

    private void SetCache(
        AuthorCacheKey key,
        string? nickname,
        ChatAuthorStyle? roleStyle,
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
                roleStyle,
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
        ChatAuthorStyle? RoleStyle,
        DateTimeOffset ExpiresAt,
        long AccessStamp);
}

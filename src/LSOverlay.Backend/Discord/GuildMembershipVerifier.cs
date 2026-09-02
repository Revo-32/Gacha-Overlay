using System.Net;
using Discord.Net;
using Discord.WebSocket;
using LSOverlay.Backend.Security;

namespace LSOverlay.Backend.Discord;

internal enum GuildMembershipStatus
{
    Member,
    NotMember,
    VerificationUnavailable,
}

internal interface IGuildMembershipVerifier
{
    Task<GuildMembershipStatus> VerifyAsync(
        AuthenticatedClientIdentity identity,
        CancellationToken cancellationToken);
}

internal sealed class DiscordGuildMembershipVerifier : IGuildMembershipVerifier
{
    public const int MaximumCacheEntries = Security.ClientCredentialRegistry.MaximumCredentials;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private sealed record CacheEntry(
        GuildMembershipStatus Status,
        DateTimeOffset ExpiresAt);

    private readonly object _sync = new();
    private readonly Dictionary<(ulong GuildId, ulong UserId), CacheEntry> _cache = new();
    private readonly Func<AuthenticatedClientIdentity, CancellationToken, Task<GuildMembershipStatus>> _lookup;
    private readonly Func<DateTimeOffset> _clock;

    public DiscordGuildMembershipVerifier(DiscordSocketClient client)
        : this(client, () => DateTimeOffset.UtcNow)
    {
    }

    internal DiscordGuildMembershipVerifier(
        DiscordSocketClient client,
        Func<DateTimeOffset> clock)
        : this((identity, cancellationToken) => LookupAsync(client, identity, cancellationToken), clock)
    {
        ArgumentNullException.ThrowIfNull(client);
    }

    internal DiscordGuildMembershipVerifier(
        Func<AuthenticatedClientIdentity, CancellationToken, Task<GuildMembershipStatus>> lookup,
        Func<DateTimeOffset> clock)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<GuildMembershipStatus> VerifyAsync(
        AuthenticatedClientIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        var key = (identity.GuildId, identity.DiscordUserId);
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > _clock())
            {
                return cached.Status;
            }
        }

        GuildMembershipStatus status;
        try
        {
            status = await _lookup(identity, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            status = GuildMembershipStatus.NotMember;
        }
        catch (Exception exception) when (
            exception is HttpException or HttpRequestException or TimeoutException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            status = GuildMembershipStatus.VerificationUnavailable;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Startup/network uncertainty is not a membership decision. Caching it
        // for the five-minute membership lease poisons every reconnect retry.
        // Keep failing closed now, but let the next request verify again.
        if (status == GuildMembershipStatus.VerificationUnavailable)
        {
            return status;
        }

        lock (_sync)
        {
            if (_cache.Count >= MaximumCacheEntries && !_cache.ContainsKey(key))
            {
                var oldest = _cache.MinBy(pair => pair.Value.ExpiresAt).Key;
                _cache.Remove(oldest);
            }

            _cache[key] = new CacheEntry(status, _clock().Add(CacheLifetime));
        }

        return status;
    }

    private static async Task<GuildMembershipStatus> LookupAsync(
        DiscordSocketClient client,
        AuthenticatedClientIdentity identity,
        CancellationToken cancellationToken)
    {
        var rest = client.Rest;
        if (rest.CurrentUser is null)
        {
            return GuildMembershipStatus.VerificationUnavailable;
        }

        var user = await rest
            .GetGuildUserAsync(identity.GuildId, identity.DiscordUserId)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return user is null ? GuildMembershipStatus.NotMember : GuildMembershipStatus.Member;
    }
}

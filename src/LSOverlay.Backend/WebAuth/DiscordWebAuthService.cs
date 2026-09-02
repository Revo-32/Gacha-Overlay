using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;
using Microsoft.Extensions.Hosting;

namespace LSOverlay.Backend.WebAuth;

internal sealed class DiscordWebAuthService
{
    public const int MaximumSessions = 128;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private sealed class Entry
    {
        public required Guid Installation { get; init; }
        public required byte[] ClaimHash { get; init; }
        public required byte[] StateHash { get; init; }
        public required string? Verifier { get; set; }
        public required DateTimeOffset Expires { get; init; }
        public DiscordWebAuthStatus Status { get; set; }
        public DiscordWebAuthFailure Failure { get; set; }
        public bool StateUsed { get; set; }
        public ulong UserId { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly BackendConfiguration _config;
    private readonly IDiscordIdentityClient _discord;
    private readonly IGuildMembershipVerifier _membership;
    private readonly ClientCredentialRegistry _credentials;
    private readonly TransportMetrics _metrics;
    private readonly Func<DateTimeOffset> _clock;

    public DiscordWebAuthService(BackendConfiguration config, IDiscordIdentityClient discord,
        IGuildMembershipVerifier membership, ClientCredentialRegistry credentials, TransportMetrics metrics,
        Func<DateTimeOffset>? clock = null)
    {
        _config = config; _discord = discord; _membership = membership; _credentials = credentials;
        _metrics = metrics; _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public DiscordWebAuthStartResponse Start(Guid installation)
    {
        if (installation == Guid.Empty) throw new ArgumentException("Installation is required.");
        var options = _config.WebAuth ?? throw new InvalidOperationException("Web authentication is disabled.");
        lock (_sync)
        {
            Sweep();
            if (_entries.Count >= MaximumSessions || _credentials.IsFaulted)
                throw new InvalidOperationException("Web authentication is temporarily unavailable.");
            var session = Guid.NewGuid();
            var claim = CryptographicSecrets.CreateClaimSecret();
            var state = CryptographicSecrets.CreateClaimSecret();
            var verifier = CryptographicSecrets.CreateClaimSecret();
            var challenge = Convert.ToBase64String(CryptographicSecrets.Hash(verifier)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var expires = _clock().Add(Lifetime);
            _entries.Add(session, new Entry
            {
                Installation = installation,
                ClaimHash = CryptographicSecrets.Hash(claim),
                StateHash = CryptographicSecrets.Hash(state),
                Verifier = verifier,
                Expires = expires,
            });
            var url = "https://discord.com/oauth2/authorize?response_type=code&scope=identify" +
                "&client_id=" + Uri.EscapeDataString(options.ClientId) +
                "&redirect_uri=" + Uri.EscapeDataString(options.RedirectUri.AbsoluteUri) +
                "&state=" + state + "&code_challenge_method=S256&code_challenge=" + challenge;
            _metrics.Increment(TransportMetric.WebAuthStarted);
            return new(OverlayTransportProtocol.Version, session, claim, url, expires);
        }
    }

    public async Task<DiscordWebAuthFailure> CompleteAsync(string? state, string? code, string? error,
        CancellationToken cancellationToken)
    {
        Entry? entry;
        string verifier;
        lock (_sync)
        {
            Sweep();
            if (state is null || state.Length != 43) return DiscordWebAuthFailure.InvalidRequest;
            entry = _entries.Values.FirstOrDefault(candidate =>
                CryptographicSecrets.FixedTimeEquals(state, candidate.StateHash));
            if (entry is null || entry.StateUsed || entry.Status != DiscordWebAuthStatus.Pending)
                return DiscordWebAuthFailure.InvalidRequest;
            entry.StateUsed = true; // Consume before any await; parallel callbacks cannot exchange twice.
            verifier = entry.Verifier!;
            entry.Verifier = null;
        }

        var failure = DiscordWebAuthFailure.None;
        ulong userId = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (!string.IsNullOrEmpty(error))
                failure = error == "access_denied" ? DiscordWebAuthFailure.Cancelled : DiscordWebAuthFailure.TemporaryFailure;
            else if (string.IsNullOrWhiteSpace(code) || code.Length > 2048)
                failure = DiscordWebAuthFailure.InvalidRequest;
            else
            {
                userId = await _discord.IdentifyAsync(code, verifier, timeout.Token).ConfigureAwait(false);
                if (userId == 0) throw new InvalidDataException("Invalid identity.");
                var membership = await _membership.VerifyAsync(new AuthenticatedClientIdentity(
                    entry.Installation, userId, _config.TargetGuildId), timeout.Token).ConfigureAwait(false);
                failure = membership switch
                {
                    GuildMembershipStatus.Member => DiscordWebAuthFailure.None,
                    GuildMembershipStatus.NotMember => DiscordWebAuthFailure.NotMember,
                    _ => DiscordWebAuthFailure.VerificationUnavailable,
                };
            }
            timeout.Token.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
            System.Text.Json.JsonException or InvalidOperationException or FormatException or OperationCanceledException or ArgumentException)
        {
            failure = DiscordWebAuthFailure.TemporaryFailure;
        }

        lock (_sync)
        {
            if (entry.Expires <= _clock()) failure = DiscordWebAuthFailure.SessionExpired;
            if (entry.Status != DiscordWebAuthStatus.Pending) return DiscordWebAuthFailure.InvalidRequest;
            entry.Failure = failure;
            entry.Status = failure == DiscordWebAuthFailure.None ? DiscordWebAuthStatus.Approved : DiscordWebAuthStatus.Denied;
            entry.UserId = failure == DiscordWebAuthFailure.None ? userId : 0;
            _metrics.Increment(failure == DiscordWebAuthFailure.None ? TransportMetric.WebAuthApproved :
                failure is DiscordWebAuthFailure.TemporaryFailure or DiscordWebAuthFailure.VerificationUnavailable ?
                TransportMetric.WebAuthTemporaryFailure : TransportMetric.WebAuthDenied);
            return failure;
        }
    }

    public DiscordWebAuthClaimResult Claim(Guid session, string secret, bool cancel = false)
    {
        lock (_sync)
        {
            Sweep();
            if (secret.Length != 43 || !_entries.TryGetValue(session, out var entry) ||
                !CryptographicSecrets.FixedTimeEquals(secret, entry.ClaimHash))
                throw new UnauthorizedAccessException("Web authentication session is unavailable.");
            if (cancel && entry.Status != DiscordWebAuthStatus.Claimed)
            {
                entry.Status = DiscordWebAuthStatus.Denied;
                entry.Failure = DiscordWebAuthFailure.Cancelled;
                entry.StateUsed = true;
                entry.Verifier = null;
            }
            if (entry.Status != DiscordWebAuthStatus.Approved)
                return new(OverlayTransportProtocol.Version, entry.Status, entry.Failure);

            // Commit consumption BEFORE persistence: even uncertain disk/delivery failure must
            // never issue twice. Retry requires a fresh browser login, replacing this installation.
            entry.Status = DiscordWebAuthStatus.Claimed;
            var credential = _credentials.Issue(entry.Installation, entry.UserId, _config.TargetGuildId);
            _metrics.Increment(TransportMetric.WebAuthClaimed);
            return new(OverlayTransportProtocol.Version, DiscordWebAuthStatus.Approved,
                AccessToken: credential.AccessToken, CredentialExpiresAt: credential.ExpiresAt);
        }
    }

    public void Sweep()
    {
        lock (_sync)
        {
            foreach (var id in _entries.Where(pair => pair.Value.Expires <= _clock()).Select(pair => pair.Key).ToArray())
            {
                if (_entries[id].Status is DiscordWebAuthStatus.Pending or DiscordWebAuthStatus.Approved)
                    _metrics.Increment(TransportMetric.WebAuthExpired);
                _entries.Remove(id);
            }
        }
    }

    internal int Count { get { lock (_sync) { Sweep(); return _entries.Count; } } }
}

internal sealed class WebAuthExpiryWorker(DiscordWebAuthService sessions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) sessions.Sweep();
    }
}

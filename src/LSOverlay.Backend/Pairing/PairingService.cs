using LSOverlay.Backend.Security;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Pairing;

internal enum PairingApprovalResult
{
    Approved,
    AlreadyApproved,
    InvalidGuild,
    InvalidCaller,
    UnknownCode,
    Expired,
    Consumed,
    ApprovedByAnotherUser,
}

internal sealed record PendingPairingCreation(
    Guid PairingId,
    string UserCode,
    string PairingClaimSecret,
    DateTimeOffset ExpiresAt);

internal sealed record PairingClaimResult(
    PairingState State,
    IssuedClientCredential? Credential = null);

internal sealed class PairingService
{
    public const int MaximumActivePairings = 64;
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(2);

    private sealed class Entry
    {
        public required Guid PairingId { get; init; }
        public required Guid ClientInstallationId { get; init; }
        public required string NormalizedUserCode { get; init; }
        public required byte[] PairingClaimHash { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public ulong? ApprovedDiscordUserId { get; set; }
        public bool Consumed { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly ClientCredentialRegistry _credentials;
    private readonly ulong _targetGuildId;
    private readonly Func<DateTimeOffset> _clock;

    public PairingService(
        ClientCredentialRegistry credentials,
        Configuration.BackendConfiguration configuration)
        : this(credentials, configuration.TargetGuildId)
    {
    }

    internal PairingService(
        ClientCredentialRegistry credentials,
        ulong targetGuildId,
        Func<DateTimeOffset>? clock = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _targetGuildId = targetGuildId != 0
            ? targetGuildId
            : throw new ArgumentOutOfRangeException(nameof(targetGuildId));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public PendingPairingCreation Create(Guid clientInstallationId)
    {
        if (clientInstallationId == Guid.Empty)
        {
            throw new ArgumentException("Client installation ID is required.", nameof(clientInstallationId));
        }

        lock (_sync)
        {
            var now = _clock();
            ReclaimCapacity(now);
            if (_entries.Count >= MaximumActivePairings)
            {
                throw new InvalidOperationException("Pairing capacity is temporarily exhausted.");
            }

            string userCode;
            string normalized;
            do
            {
                userCode = CryptographicSecrets.CreateUserCode();
                normalized = CryptographicSecrets.NormalizeUserCode(userCode);
            }
            while (_entries.Values.Any(entry =>
                !entry.Consumed && entry.ExpiresAt > now &&
                entry.NormalizedUserCode == normalized));

            var claimSecret = CryptographicSecrets.CreateClaimSecret();
            var entry = new Entry
            {
                PairingId = Guid.NewGuid(),
                ClientInstallationId = clientInstallationId,
                NormalizedUserCode = normalized,
                PairingClaimHash = CryptographicSecrets.Hash(claimSecret),
                CreatedAt = now,
                ExpiresAt = now.Add(PairingLifetime),
            };
            _entries.Add(entry.PairingId, entry);
            return new PendingPairingCreation(
                entry.PairingId,
                userCode,
                claimSecret,
                entry.ExpiresAt);
        }
    }

    public PairingApprovalResult Approve(
        ulong guildId,
        ulong discordUserId,
        bool callerIsBot,
        string userCode)
    {
        if (guildId != _targetGuildId)
        {
            return PairingApprovalResult.InvalidGuild;
        }

        if (discordUserId == 0 || callerIsBot)
        {
            return PairingApprovalResult.InvalidCaller;
        }

        var normalized = CryptographicSecrets.NormalizeUserCode(userCode ?? string.Empty);
        lock (_sync)
        {
            var entry = _entries.Values.FirstOrDefault(candidate =>
                candidate.NormalizedUserCode == normalized);
            if (entry is null)
            {
                return PairingApprovalResult.UnknownCode;
            }

            if (entry.ExpiresAt <= _clock())
            {
                return PairingApprovalResult.Expired;
            }

            if (entry.Consumed)
            {
                return PairingApprovalResult.Consumed;
            }

            if (entry.ApprovedDiscordUserId is ulong approved)
            {
                return approved == discordUserId
                    ? PairingApprovalResult.AlreadyApproved
                    : PairingApprovalResult.ApprovedByAnotherUser;
            }

            entry.ApprovedDiscordUserId = discordUserId;
            return PairingApprovalResult.Approved;
        }
    }

    public PairingClaimResult Claim(Guid pairingId, string claimSecret)
    {
        if (pairingId == Guid.Empty || string.IsNullOrWhiteSpace(claimSecret))
        {
            return new PairingClaimResult(PairingState.Expired);
        }

        lock (_sync)
        {
            if (!_entries.TryGetValue(pairingId, out var entry) ||
                !CryptographicSecrets.FixedTimeEquals(claimSecret, entry.PairingClaimHash))
            {
                throw new UnauthorizedAccessException("Pairing claim authentication failed.");
            }

            if (entry.ExpiresAt <= _clock())
            {
                return new PairingClaimResult(PairingState.Expired);
            }

            if (entry.Consumed)
            {
                return new PairingClaimResult(PairingState.Consumed);
            }

            if (entry.ApprovedDiscordUserId is not ulong userId)
            {
                return new PairingClaimResult(PairingState.Pending);
            }

            var credential = _credentials.Issue(
                entry.ClientInstallationId,
                userId,
                _targetGuildId);
            entry.Consumed = true;
            return new PairingClaimResult(PairingState.Approved, credential);
        }
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    private void ReclaimCapacity(DateTimeOffset now)
    {
        var removable = _entries.Values
            .Where(entry => entry.ExpiresAt <= now || entry.Consumed)
            .OrderBy(entry => entry.CreatedAt)
            .Select(entry => entry.PairingId)
            .ToArray();
        foreach (var id in removable)
        {
            _entries.Remove(id);
        }
    }
}

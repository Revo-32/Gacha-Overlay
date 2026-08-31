namespace GachaOverlay.Core.Discord.Messages;

public readonly record struct GuildDisplayNameKey(string GuildId, string AuthorId);

public sealed record GuildDisplayNameCacheEntry(
    string GuildId,
    string AuthorId,
    string DisplayName,
    DiscordDisplayNameSource ObservationSource,
    double Confidence,
    DateTimeOffset ObservedAt,
    long Revision);

public sealed record GuildDisplayNameCacheDocument(
    int Version,
    string AccountUserId,
    IReadOnlyList<GuildDisplayNameCacheEntry> Entries)
{
    public const int CurrentVersion = 1;
}

public interface IGuildDisplayNameCacheStore
{
    GuildDisplayNameCacheDocument Load(string accountUserId);

    void Save(GuildDisplayNameCacheDocument document);
}

public sealed record GuildNicknameObservation(
    string GuildId,
    string AuthorId,
    string? MessageId,
    string DisplayName,
    DiscordDisplayNameSource Source,
    double Confidence,
    DateTimeOffset ObservedAt);

public sealed record GuildDisplayNameRequest(
    string GuildId,
    string AuthorId,
    string? CurrentExactGuildNickname,
    string? GlobalDisplayName,
    string? Username,
    string? PreviousVerifiedGuildNickname = null,
    DiscordDisplayNameSource PreviousSource = DiscordDisplayNameSource.Unknown,
    DiscordDisplayNameSource PreviousObservationSource = DiscordDisplayNameSource.Unknown);

public sealed record GuildDisplayNameResolution(
    string DisplayName,
    DiscordDisplayNameSource Source,
    bool IsExactGuildNickname,
    double Confidence,
    DateTimeOffset? ObservedAt,
    long Revision,
    string? FallbackReason,
    DiscordDisplayNameSource ObservationSource = DiscordDisplayNameSource.Unknown);

public interface IGuildDisplayNameResolver
{
    void SetAccountScope(string accountUserId);

    GuildDisplayNameResolution Resolve(GuildDisplayNameRequest request);

    GuildDisplayNameResolution? Observe(GuildNicknameObservation observation);
}

public interface IGuildNicknameObservationSink
{
    bool ObserveGuildNickname(GuildNicknameObservation observation);
}

// M6 production UIA sensors will publish observations through this contract. M4.5 does not
// implement a sensor and never changes the Discord channel visible to the user.
public interface IGuildNicknameObservationSource
{
    event Action<GuildNicknameObservation>? ObservationAvailable;
}

public sealed class GuildDisplayNameResolver : IGuildDisplayNameResolver
{
    public const int DefaultMaximumEntries = 512;
    private const string SessionAccountScope = "__session__";

    private readonly object _sync = new();
    private readonly IGuildDisplayNameCacheStore _store;
    private readonly int _maximumEntries;
    private readonly Func<DateTimeOffset> _clock;
    private Dictionary<GuildDisplayNameKey, GuildDisplayNameCacheEntry> _entries = new();
    private string _accountUserId = SessionAccountScope;
    private long _revision;

    public GuildDisplayNameResolver(
        IGuildDisplayNameCacheStore? store = null,
        int maximumEntries = DefaultMaximumEntries,
        Func<DateTimeOffset>? clock = null)
    {
        if (maximumEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        _store = store ?? new InMemoryGuildDisplayNameCacheStore();
        _maximumEntries = maximumEntries;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void SetAccountScope(string accountUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUserId);

        lock (_sync)
        {
            if (string.Equals(_accountUserId, accountUserId, StringComparison.Ordinal))
            {
                return;
            }

            var document = _store.Load(accountUserId);
            _accountUserId = accountUserId;
            _entries = document.Version == GuildDisplayNameCacheDocument.CurrentVersion &&
                string.Equals(document.AccountUserId, accountUserId, StringComparison.Ordinal)
                    ? document.Entries
                        .Where(IsValidEntry)
                        .GroupBy(
                            entry => new GuildDisplayNameKey(entry.GuildId, entry.AuthorId))
                        .Select(group => group.OrderByDescending(entry => entry.Revision).First())
                        .OrderByDescending(entry => entry.ObservedAt)
                        .ThenByDescending(entry => entry.Revision)
                        .Take(_maximumEntries)
                        .ToDictionary(
                            entry => new GuildDisplayNameKey(entry.GuildId, entry.AuthorId))
                    : new Dictionary<GuildDisplayNameKey, GuildDisplayNameCacheEntry>();
            _revision = _entries.Count == 0 ? 0 : _entries.Values.Max(entry => entry.Revision);
        }
    }

    public GuildDisplayNameResolution Resolve(GuildDisplayNameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AuthorId);

        if (!string.IsNullOrWhiteSpace(request.CurrentExactGuildNickname))
        {
            var observedAt = _clock();
            var observed = Observe(new GuildNicknameObservation(
                request.GuildId,
                request.AuthorId,
                null,
                request.CurrentExactGuildNickname,
                DiscordDisplayNameSource.RpcGuildNickname,
                1d,
                observedAt));
            if (observed is not null)
            {
                return observed;
            }
        }

        lock (_sync)
        {
            if (_entries.TryGetValue(
                    new GuildDisplayNameKey(request.GuildId, request.AuthorId),
                    out var cached))
            {
                return new GuildDisplayNameResolution(
                    cached.DisplayName,
                    DiscordDisplayNameSource.CachedGuildNickname,
                    true,
                    cached.Confidence,
                    cached.ObservedAt,
                    cached.Revision,
                    "VerifiedGuildAuthorCache",
                    cached.ObservationSource);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.PreviousVerifiedGuildNickname) &&
            IsExactSource(request.PreviousObservationSource))
        {
            return new GuildDisplayNameResolution(
                request.PreviousVerifiedGuildNickname,
                request.PreviousSource,
                true,
                1d,
                null,
                0,
                "ExistingMessageVerifiedNickname",
                request.PreviousObservationSource);
        }

        if (!string.IsNullOrWhiteSpace(request.GlobalDisplayName))
        {
            return new GuildDisplayNameResolution(
                request.GlobalDisplayName,
                DiscordDisplayNameSource.GlobalDisplayName,
                false,
                0.5d,
                null,
                0,
                "GuildNicknameUnavailable");
        }

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            return new GuildDisplayNameResolution(
                request.Username,
                DiscordDisplayNameSource.Username,
                false,
                0.25d,
                null,
                0,
                "GuildAndGlobalNamesUnavailable");
        }

        return new GuildDisplayNameResolution(
            string.Empty,
            DiscordDisplayNameSource.Unknown,
            false,
            0d,
            null,
            0,
            "NoAuthorNameAvailable");
    }

    public GuildDisplayNameResolution? Observe(GuildNicknameObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.GuildId) ||
            string.IsNullOrWhiteSpace(observation.AuthorId) ||
            string.IsNullOrWhiteSpace(observation.DisplayName) ||
            !IsExactSource(observation.Source) ||
            double.IsNaN(observation.Confidence) ||
            double.IsInfinity(observation.Confidence) ||
            observation.Confidence <= 0d)
        {
            return null;
        }

        lock (_sync)
        {
            var revision = ++_revision;
            var entry = new GuildDisplayNameCacheEntry(
                observation.GuildId,
                observation.AuthorId,
                observation.DisplayName,
                observation.Source,
                Math.Min(1d, observation.Confidence),
                observation.ObservedAt,
                revision);
            _entries[new GuildDisplayNameKey(observation.GuildId, observation.AuthorId)] = entry;
            TrimOldestEntries();
            Persist();

            return new GuildDisplayNameResolution(
                entry.DisplayName,
                entry.ObservationSource,
                true,
                entry.Confidence,
                entry.ObservedAt,
                entry.Revision,
                null,
                entry.ObservationSource);
        }
    }

    public static bool IsExactSource(DiscordDisplayNameSource source) => source is
        DiscordDisplayNameSource.RpcGuildNickname or
        DiscordDisplayNameSource.UiAutomationGuildNickname or
        DiscordDisplayNameSource.ManualOverride;

    private static bool IsValidEntry(GuildDisplayNameCacheEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.GuildId) &&
        !string.IsNullOrWhiteSpace(entry.AuthorId) &&
        !string.IsNullOrWhiteSpace(entry.DisplayName) &&
        IsExactSource(entry.ObservationSource) &&
        entry.Confidence > 0d &&
        !double.IsNaN(entry.Confidence) &&
        !double.IsInfinity(entry.Confidence);

    private void TrimOldestEntries()
    {
        if (_entries.Count <= _maximumEntries)
        {
            return;
        }

        foreach (var key in _entries
                     .OrderBy(pair => pair.Value.ObservedAt)
                     .ThenBy(pair => pair.Value.Revision)
                     .Take(_entries.Count - _maximumEntries)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.Remove(key);
        }
    }

    private void Persist()
    {
        _store.Save(new GuildDisplayNameCacheDocument(
            GuildDisplayNameCacheDocument.CurrentVersion,
            _accountUserId,
            _entries.Values
                .OrderByDescending(entry => entry.ObservedAt)
                .ThenByDescending(entry => entry.Revision)
                .ToArray()));
    }
}

public sealed class InMemoryGuildDisplayNameCacheStore : IGuildDisplayNameCacheStore
{
    private readonly Dictionary<string, GuildDisplayNameCacheDocument> _documents =
        new(StringComparer.Ordinal);

    public GuildDisplayNameCacheDocument Load(string accountUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUserId);
        return _documents.TryGetValue(accountUserId, out var document)
            ? document with { Entries = document.Entries.ToArray() }
            : new GuildDisplayNameCacheDocument(
                GuildDisplayNameCacheDocument.CurrentVersion,
                accountUserId,
                Array.Empty<GuildDisplayNameCacheEntry>());
    }

    public void Save(GuildDisplayNameCacheDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _documents[document.AccountUserId] = document with
        {
            Entries = document.Entries.ToArray(),
        };
    }
}

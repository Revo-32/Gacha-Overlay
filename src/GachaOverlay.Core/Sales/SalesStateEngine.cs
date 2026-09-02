using System.Security.Cryptography;
using System.Text;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.Core.Sales;

public sealed class SalesStateEngine
{
    public const int DefaultTombstoneLimit = 256;

    private readonly object _sync = new();
    private readonly IGuildDisplayNameResolver _displayNameResolver;
    private SalesProductCatalog _productCatalog;
    private readonly IAppLogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _tombstoneLimit;
    private readonly Dictionary<string, SaleRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _unknownTombstones = new(StringComparer.Ordinal);
    private string _locale;
    private string? _authenticatedUserId;
    private bool _trackingEnabled = true;
    private long _sourceRevision;
    private long _lastObservationGeneration;
    private long _snapshotRevision;
    private SalesObservationStatus _observationStatus = SalesObservationStatus.Unavailable;
    private SalesQueueSnapshot _current = SalesQueueSnapshot.Empty;

    public SalesStateEngine(
        IGuildDisplayNameResolver displayNameResolver,
        SalesProductCatalog? productCatalog = null,
        IAppLogger? logger = null,
        string locale = SupportedLocales.English,
        Func<DateTimeOffset>? clock = null,
        int tombstoneLimit = DefaultTombstoneLimit)
    {
        ArgumentNullException.ThrowIfNull(displayNameResolver);
        if (tombstoneLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tombstoneLimit));
        }

        _displayNameResolver = displayNameResolver;
        _productCatalog = productCatalog ?? SalesProductCatalog.Empty;
        _logger = logger ?? NullAppLogger.Instance;
        _locale = SupportedLocales.NormalizeOrEnglish(locale);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _tombstoneLimit = tombstoneLimit;
        _current = BuildSnapshot(incrementRevision: false);
    }

    public event Action<SalesQueueSnapshot>? SnapshotChanged;

    public SalesQueueSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public IReadOnlyList<SaleRecord> Records
    {
        get
        {
            lock (_sync)
            {
                return _records.Values
                    .OrderBy(record => record, SaleRecordOrdering.Instance)
                    .ToArray();
            }
        }
    }

    public SalesProductCatalog ProductCatalog
    {
        get
        {
            lock (_sync)
            {
                return _productCatalog;
            }
        }
    }

    public void SetAuthenticatedUser(string? userId)
    {
        SalesQueueSnapshot? snapshot = null;
        lock (_sync)
        {
            var normalized = string.IsNullOrWhiteSpace(userId) ? null : userId;
            if (string.Equals(_authenticatedUserId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _authenticatedUserId = normalized;
            snapshot = BuildSnapshot();
        }

        Publish(snapshot);
    }

    public bool SetTrackingEnabled(bool enabled)
    {
        SalesQueueSnapshot snapshot;
        lock (_sync)
        {
            if (_trackingEnabled == enabled)
            {
                return false;
            }

            _trackingEnabled = enabled;
            snapshot = BuildSnapshot();
        }

        _logger.Information("SALES", enabled ? "Tracking enabled." : "Tracking disabled.");
        Publish(snapshot);
        return true;
    }

    public bool SetLocale(string locale)
    {
        var normalized = SupportedLocales.NormalizeOrEnglish(locale);
        SalesQueueSnapshot? snapshot = null;
        lock (_sync)
        {
            if (string.Equals(_locale, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _locale = normalized;
            var changed = false;
            foreach (var pair in _records.ToArray())
            {
                if (pair.Value.AllProducts.Count == 0)
                {
                    continue;
                }

                var localized = pair.Value.AllProducts
                    .Select(product => _productCatalog.Relocalize(
                        pair.Value.GuildId,
                        product,
                        _locale))
                    .ToArray();
                if (localized.SequenceEqual(pair.Value.AllProducts))
                {
                    continue;
                }

                _records[pair.Key] = pair.Value with
                {
                    Product = localized.FirstOrDefault(),
                    Products = localized,
                };
                changed = true;
            }

            if (changed)
            {
                snapshot = BuildSnapshot();
            }
        }

        Publish(snapshot);
        return snapshot is not null;
    }

    public void ReplaceProductCatalog(SalesProductCatalog productCatalog)
    {
        ArgumentNullException.ThrowIfNull(productCatalog);
        lock (_sync)
        {
            _productCatalog = productCatalog;
        }
    }

    public bool RemapProducts(IEnumerable<NormalizedDiscordMessage> sourceMessages)
    {
        ArgumentNullException.ThrowIfNull(sourceMessages);
        SalesQueueSnapshot? snapshot = null;
        lock (_sync)
        {
            var source = sourceMessages
                .GroupBy(message => message.MessageId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var changed = false;
            foreach (var pair in _records.ToArray())
            {
                if (pair.Value.DomainState == SaleDomainState.Deleted ||
                    !source.TryGetValue(pair.Key, out var message))
                {
                    continue;
                }

                var products = MapProducts(pair.Value.GuildId, message);
                if (products.SequenceEqual(pair.Value.AllProducts))
                {
                    continue;
                }

                _records[pair.Key] = pair.Value with
                {
                    Product = products.FirstOrDefault(),
                    Products = products,
                };
                changed = true;
            }

            if (changed)
            {
                snapshot = BuildSnapshot();
            }
        }

        Publish(snapshot);
        return snapshot is not null;
    }

    public bool ApplySourceSnapshot(IEnumerable<NormalizedDiscordMessage> sourceMessages)
    {
        ArgumentNullException.ThrowIfNull(sourceMessages);
        SalesQueueSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!_trackingEnabled)
            {
                return false;
            }

            var source = sourceMessages
                .GroupBy(message => message.MessageId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var changed = false;
            foreach (var message in source)
            {
                seen.Add(message.MessageId);
                changed |= UpsertSource(message, allowRevive: true, isCreate: false);
            }

            foreach (var record in _records.Values
                         .Where(record =>
                             record.DomainState != SaleDomainState.Deleted &&
                             !seen.Contains(record.MessageId))
                         .ToArray())
            {
                changed |= DeleteSourceCore(record.MessageId);
            }

            if (changed)
            {
                TrimTombstones();
                snapshot = BuildSnapshot();
            }
        }

        Publish(snapshot);
        return snapshot is not null;
    }

    public bool ApplyAuthoritativeWindowSnapshot(
        IEnumerable<NormalizedDiscordMessage> sourceMessages)
    {
        ArgumentNullException.ThrowIfNull(sourceMessages);
        SalesQueueSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!_trackingEnabled)
            {
                return false;
            }

            var source = sourceMessages
                .GroupBy(message => message.MessageId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();
            if (source.Length > AuthoritativeSalesWindow.Size)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceMessages),
                    $"The authoritative Sales window cannot exceed {AuthoritativeSalesWindow.Size} messages.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var changed = false;
            foreach (var message in source)
            {
                seen.Add(message.MessageId);
                changed |= UpsertSource(message, allowRevive: true, isCreate: false);
            }

            foreach (var record in _records.Values
                         .Where(record =>
                             record.DomainState != SaleDomainState.Deleted &&
                             !seen.Contains(record.MessageId))
                         .ToArray())
            {
                _records.Remove(record.MessageId);
                _sourceRevision++;
                _logger.Information(
                    "SALES",
                    $"Source left authoritative window message={Sanitize(record.MessageId)}.");
                changed = true;
            }

            if (changed)
            {
                snapshot = BuildSnapshot();
            }
        }

        Publish(snapshot);
        return snapshot is not null;
    }

    public bool ApplySourceCreate(NormalizedDiscordMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        SalesQueueSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!_trackingEnabled)
            {
                return false;
            }

            if (_records.TryGetValue(message.MessageId, out var existing) &&
                existing.DomainState != SaleDomainState.Deleted)
            {
                return false;
            }

            if (UpsertSource(message, allowRevive: true, isCreate: true))
            {
                snapshot = BuildSnapshot();
            }
        }

        Publish(snapshot);
        return snapshot is not null;
    }

    public bool ApplySourceUpdate(NormalizedDiscordMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        SalesQueueSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!_trackingEnabled ||
                !_records.TryGetValue(message.MessageId, out var existing) ||
                existing.DomainState == SaleDomainState.Deleted)
            {
                return false;
            }

            if (UpsertSource(message, allowRevive: false, isCreate: false))
            {
                snapshot = BuildSnapshot();
            }
        }

        Publish(snapshot);
        return snapshot is not null;
    }

    public bool ApplySourceDelete(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        SalesQueueSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!_trackingEnabled)
            {
                return false;
            }

            if (DeleteSourceCore(messageId))
            {
                TrimTombstones();
                snapshot = BuildSnapshot();
            }
        }

        Publish(snapshot);
        return snapshot is not null;
    }

    public bool ApplyObservationBatch(SalesObservationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        SalesQueueSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!_trackingEnabled)
            {
                return false;
            }

            if (batch.Generation < _lastObservationGeneration)
            {
                _logger.Information(
                    "SALES",
                    $"Observation ignored reason=StaleGeneration generation={batch.Generation} current={_lastObservationGeneration}.");
                return false;
            }

            var changed = _observationStatus != batch.SensorStatus;
            _observationStatus = batch.SensorStatus;
            _lastObservationGeneration = Math.Max(
                _lastObservationGeneration,
                batch.Generation);

            if (!batch.IsTrusted || batch.SensorStatus is not
                (SalesObservationStatus.Live or SalesObservationStatus.Partial))
            {
                foreach (var pair in _records.ToArray())
                {
                    if (pair.Value.DomainState == SaleDomainState.Deleted ||
                        pair.Value.ObservationTrust != SaleObservationTrust.Trusted)
                    {
                        continue;
                    }

                    _records[pair.Key] = pair.Value with
                    {
                        ObservationTrust = SaleObservationTrust.TemporarilyUntrusted,
                    };
                    changed = true;
                }

                if (changed)
                {
                    snapshot = BuildSnapshot();
                }
            }
            else
            {
                foreach (var observation in batch.Observations)
                {
                    changed |= ApplyTrustedObservation(batch, observation);
                }

                if (changed)
                {
                    snapshot = BuildSnapshot();
                }
            }
        }

        Publish(snapshot);
        return snapshot is not null;
    }

    public bool RefreshDisplayNames()
    {
        SalesQueueSnapshot? snapshot = null;
        lock (_sync)
        {
            var changed = false;
            foreach (var pair in _records.ToArray())
            {
                var record = pair.Value;
                if (record.DomainState == SaleDomainState.Deleted)
                {
                    continue;
                }

                var resolution = ResolveDisplayName(
                    record.GuildId,
                    record.AuthorId,
                    record.AuthorUsername,
                    record.AuthorGlobalDisplayName,
                    record.AuthorGuildNickname,
                    record.AuthorGuildNicknameObservationSource,
                    currentMessageHasExactGuildNickname: false);
                if (resolution == record.DisplayName)
                {
                    continue;
                }

                _records[pair.Key] = record with { DisplayName = resolution };
                changed = true;
            }

            if (changed)
            {
                snapshot = BuildSnapshot();
            }
        }

        Publish(snapshot);
        return snapshot is not null;
    }

    private bool UpsertSource(
        NormalizedDiscordMessage message,
        bool allowRevive,
        bool isCreate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.MessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.GuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.AuthorId);

        var fingerprint = CreateFingerprint(message);
        if (_records.TryGetValue(message.MessageId, out var existing))
        {
            if (existing.DomainState == SaleDomainState.Deleted && !allowRevive)
            {
                return false;
            }

            if (existing.DomainState != SaleDomainState.Deleted &&
                string.Equals(existing.SourceFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            var currentMessageHasExactGuildNickname =
                message.AuthorDisplayNameSource == DiscordDisplayNameSource.GuildNickname;
            var resolution = ResolveDisplayName(
                existing.GuildId,
                existing.AuthorId,
                message.AuthorUsername,
                message.AuthorDisplayName,
                message.AuthorGuildNickname,
                message.AuthorGuildNicknameObservationSource,
                currentMessageHasExactGuildNickname);
            var products = MapProducts(existing.GuildId, message);
            var updated = existing with
            {
                SourceRevision = ++_sourceRevision,
                SourceFingerprint = fingerprint,
                AuthorUsername = message.AuthorUsername,
                AuthorGlobalDisplayName = message.AuthorDisplayName,
                AuthorGuildNickname = message.AuthorGuildNickname,
                AuthorGuildNicknameObservationSource =
                    message.AuthorGuildNicknameObservationSource,
                DisplayName = resolution,
                Product = products.FirstOrDefault(),
                Products = products,
                DomainState = existing.DomainState == SaleDomainState.Deleted
                    ? SaleDomainState.Pending
                    : existing.DomainState,
                ObservationTrust = existing.DomainState == SaleDomainState.Deleted
                    ? SaleObservationTrust.NeverObserved
                    : existing.ObservationTrust,
                LastTrustedObservationAt = existing.DomainState == SaleDomainState.Deleted
                    ? null
                    : existing.LastTrustedObservationAt,
                LastObservationGeneration = existing.DomainState == SaleDomainState.Deleted
                    ? 0
                    : existing.LastObservationGeneration,
                DeletedAt = null,
            };
            _records[message.MessageId] = updated;
            _unknownTombstones.Remove(message.MessageId);
            _logger.Information(
                "SALES",
                $"Source updated message={Sanitize(message.MessageId)} revision={updated.SourceRevision}.");
            return true;
        }

        var displayName = ResolveDisplayName(
            message.GuildId,
            message.AuthorId,
            message.AuthorUsername,
            message.AuthorDisplayName,
            message.AuthorGuildNickname,
            message.AuthorGuildNicknameObservationSource,
            message.AuthorDisplayNameSource == DiscordDisplayNameSource.GuildNickname);
        var createdProducts = MapProducts(message.GuildId, message);
        var record = new SaleRecord(
            message.MessageId,
            message.GuildId,
            message.ChannelId,
            message.AuthorId,
            message.CreatedAt,
            ++_sourceRevision,
            fingerprint,
            message.AuthorUsername,
            message.AuthorDisplayName,
            message.AuthorGuildNickname,
            message.AuthorGuildNicknameObservationSource,
            displayName,
            createdProducts.FirstOrDefault(),
            SaleDomainState.Pending,
            SaleObservationTrust.NeverObserved,
            null,
            0,
            null,
            createdProducts);
        _records.Add(message.MessageId, record);
        _unknownTombstones.Remove(message.MessageId);
        _logger.Information(
            "SALES",
            $"Source added message={Sanitize(message.MessageId)} revision={record.SourceRevision} mode={(isCreate ? "Create" : "Snapshot")}.");
        return true;
    }

    private bool DeleteSourceCore(string messageId)
    {
        var revision = ++_sourceRevision;
        if (!_records.TryGetValue(messageId, out var existing))
        {
            if (_unknownTombstones.ContainsKey(messageId))
            {
                return false;
            }

            _unknownTombstones[messageId] = revision;
            return true;
        }

        if (existing.DomainState == SaleDomainState.Deleted)
        {
            return false;
        }

        _records[messageId] = existing with
        {
            SourceRevision = revision,
            DomainState = SaleDomainState.Deleted,
            DeletedAt = _clock(),
        };
        _logger.Information("SALES", $"Source deleted message={Sanitize(messageId)}.");
        return true;
    }

    private bool ApplyTrustedObservation(
        SalesObservationBatch batch,
        SaleReactionObservation observation)
    {
        if (observation.Generation != batch.Generation ||
            observation.Outcome == SaleReactionOutcome.NotObserved ||
            !observation.HasTrustedEvidence ||
            _unknownTombstones.ContainsKey(observation.MessageId) ||
            !_records.TryGetValue(observation.MessageId, out var record) ||
            record.DomainState == SaleDomainState.Deleted ||
            observation.Generation < record.LastObservationGeneration ||
            (observation.SourceRevision.HasValue &&
                observation.SourceRevision.Value < record.SourceRevision))
        {
            return false;
        }

        var targetState = observation.Outcome == SaleReactionOutcome.Sold
            ? SaleDomainState.Sold
            : SaleDomainState.Pending;
        if (record.DomainState == targetState &&
            record.ObservationTrust == SaleObservationTrust.Trusted)
        {
            if (observation.Generation > record.LastObservationGeneration ||
                observation.ObservedAt > record.LastTrustedObservationAt)
            {
                _records[observation.MessageId] = record with
                {
                    LastTrustedObservationAt = observation.ObservedAt,
                    LastObservationGeneration = observation.Generation,
                };
            }

            return false;
        }

        _records[observation.MessageId] = record with
        {
            DomainState = targetState,
            ObservationTrust = SaleObservationTrust.Trusted,
            LastTrustedObservationAt = observation.ObservedAt,
            LastObservationGeneration = observation.Generation,
        };
        _logger.Information(
            "SALES",
            $"Observation applied message={Sanitize(observation.MessageId)} outcome={observation.Outcome} generation={observation.Generation}.");
        return true;
    }

    private IReadOnlyList<SaleProduct> MapProducts(
        string guildId,
        NormalizedDiscordMessage message)
    {
        var products = _productCatalog.MapAll(guildId, message.CustomEmojis, _locale);
        if (products.Count > 0)
        {
            _logger.Information(
                "PRODUCT",
                $"Mapped message={Sanitize(message.MessageId)} products={products.Count} summary={Sanitize(SalesProductSummaryFormatter.Format(products))}.");
        }
        else if (message.CustomEmojis.Count > 0)
        {
            _logger.Information(
                "PRODUCT",
                $"Mapping missing message={Sanitize(message.MessageId)} emoji={Sanitize(message.CustomEmojis[0].EmojiId)}.");
        }

        return products;
    }

    private GuildDisplayNameResolution ResolveDisplayName(
        string guildId,
        string authorId,
        string username,
        string? globalName,
        string? guildNickname,
        DiscordDisplayNameSource observationSource,
        bool currentMessageHasExactGuildNickname)
    {
        var exactObservationSource = observationSource == DiscordDisplayNameSource.Unknown &&
            currentMessageHasExactGuildNickname
                ? DiscordDisplayNameSource.GuildNickname
                : observationSource;
        return _displayNameResolver.Resolve(new GuildDisplayNameRequest(
            guildId,
            authorId,
            currentMessageHasExactGuildNickname ? guildNickname : null,
            globalName,
            username,
            guildNickname,
            currentMessageHasExactGuildNickname
                ? DiscordDisplayNameSource.GuildNickname
                : DiscordDisplayNameSource.CachedGuildNickname,
            exactObservationSource));
    }

    private SalesQueueSnapshot BuildSnapshot(bool incrementRevision = true)
    {
        var active = _trackingEnabled
            ? _records.Values
                .Where(record => record.ParticipatesInQueue)
                .OrderBy(record => record, SaleRecordOrdering.Instance)
                .Select(ToQueueEntry)
                .ToArray()
            : Array.Empty<SalesQueueEntry>();
        var current = active.FirstOrDefault();
        var next = active.Skip(1).FirstOrDefault();
        if (incrementRevision)
        {
            _snapshotRevision++;
        }

        _current = new SalesQueueSnapshot(
            _snapshotRevision,
            _trackingEnabled,
            active,
            current,
            active.Length,
            Math.Max(0, active.Length - 1),
            next,
            current is not null &&
                string.Equals(current.AuthorId, _authenticatedUserId, StringComparison.Ordinal),
            next is not null &&
                string.Equals(next.AuthorId, _authenticatedUserId, StringComparison.Ordinal),
            active.Any(entry => entry.IsProvisional),
            _observationStatus is not
                (SalesObservationStatus.Disabled or
                SalesObservationStatus.Unavailable or
                SalesObservationStatus.Error),
            _observationStatus,
            _clock(),
            _authenticatedUserId);
        return _current;
    }

    private static SalesQueueEntry ToQueueEntry(SaleRecord record) => new(
        record.MessageId,
        record.GuildId,
        record.AuthorId,
        record.CreatedAt,
        record.DisplayName.DisplayName,
        record.DisplayName.Source,
        record.DisplayName.IsExactGuildNickname,
        record.Product,
        record.ObservationTrust,
        record.AllProducts);

    private void TrimTombstones()
    {
        var deleted = _records.Values
            .Where(record => record.DomainState == SaleDomainState.Deleted)
            .OrderBy(record => record.DeletedAt)
            .ThenBy(record => record.SourceRevision)
            .ToArray();
        var total = deleted.Length + _unknownTombstones.Count;
        var remove = total - _tombstoneLimit;
        foreach (var record in deleted.Take(Math.Max(0, remove)).ToArray())
        {
            _records.Remove(record.MessageId);
            remove--;
            if (remove <= 0)
            {
                return;
            }
        }

        foreach (var messageId in _unknownTombstones
                     .OrderBy(pair => pair.Value)
                     .Take(Math.Max(0, remove))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _unknownTombstones.Remove(messageId);
        }
    }

    private static string CreateFingerprint(NormalizedDiscordMessage message)
    {
        var value = string.Join(
            '\u001f',
            message.GuildId,
            message.ChannelId,
            message.AuthorId,
            message.CreatedAt?.UtcTicks.ToString() ?? string.Empty,
            message.AuthorUsername,
            message.AuthorDisplayName ?? string.Empty,
            message.AuthorGuildNickname ?? string.Empty,
            message.AuthorDisplayNameSource,
            message.AuthorGuildNicknameObservationSource,
            message.Content,
            string.Join(
                '\u001e',
                message.CustomEmojis.Select(emoji =>
                    $"{emoji.EmojiId}\u001d{emoji.Name}\u001d{emoji.Animated}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private void Publish(SalesQueueSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        _logger.Information(
            "QUEUE",
            $"Revision={snapshot.Revision} active={snapshot.ActiveCount} waiting={snapshot.WaitingCount} current={Sanitize(snapshot.CurrentSeller?.MessageId ?? "none")} status={snapshot.ObservationStatus}.");
        SnapshotChanged?.Invoke(snapshot);
    }

    private static string Sanitize(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }

    private sealed class SaleRecordOrdering : IComparer<SaleRecord>
    {
        public static SaleRecordOrdering Instance { get; } = new();

        public int Compare(SaleRecord? left, SaleRecord? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var leftTime = EffectiveTimestamp(left);
            var rightTime = EffectiveTimestamp(right);
            var timestamp = leftTime.CompareTo(rightTime);
            if (timestamp != 0)
            {
                return timestamp;
            }

            if (ulong.TryParse(left.MessageId, out var leftSnowflake) &&
                ulong.TryParse(right.MessageId, out var rightSnowflake))
            {
                return leftSnowflake.CompareTo(rightSnowflake);
            }

            return string.Compare(left.MessageId, right.MessageId, StringComparison.Ordinal);
        }

        private static DateTimeOffset EffectiveTimestamp(SaleRecord record)
        {
            if (record.CreatedAt.HasValue)
            {
                return record.CreatedAt.Value;
            }

            if (ulong.TryParse(record.MessageId, out var snowflake))
            {
                const ulong discordEpochMilliseconds = 1420070400000;
                var milliseconds = (snowflake >> 22) + discordEpochMilliseconds;
                if (milliseconds <= long.MaxValue)
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
                }
            }

            return DateTimeOffset.MaxValue;
        }
    }
}

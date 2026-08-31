using System.Text.Json;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Normalization;

namespace GachaOverlay.Infrastructure.Discord.Forward;

public sealed class ForwardMessageResolver
{
    public const int DefaultCapacity = 64;
    public static readonly TimeSpan DefaultNegativeCacheTtl = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly IDiscordMessageNormalizer _normalizer;
    private readonly IAppLogger _logger;
    private readonly int _capacity;
    private readonly TimeSpan _negativeCacheTtl;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<DiscordForwardSourceKey, CacheEntry> _cache = new();
    private readonly LinkedList<DiscordForwardSourceKey> _lru = new();
    private readonly Dictionary<LookupKey, Task<DiscordForwardContent?>> _inFlight =
        new();
    private long _generation;

    public ForwardMessageResolver(
        IDiscordMessageNormalizer normalizer,
        IAppLogger logger,
        int capacity = DefaultCapacity,
        TimeSpan? negativeCacheTtl = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(logger);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _normalizer = normalizer;
        _logger = logger;
        _capacity = capacity;
        _negativeCacheTtl = negativeCacheTtl ?? DefaultNegativeCacheTtl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int CachedEntryCount
    {
        get
        {
            lock (_sync)
            {
                return _cache.Count;
            }
        }
    }

    public int InFlightCount
    {
        get
        {
            lock (_sync)
            {
                return _inFlight.Count;
            }
        }
    }

    public void BeginGeneration(long generation)
    {
        lock (_sync)
        {
            if (generation == _generation)
            {
                return;
            }

            _generation = generation;
            _cache.Clear();
            _lru.Clear();
            _inFlight.Clear();
        }
    }

    public Task<DiscordForwardContent?> ResolveAsync(
        DiscordForwardSourceKey sourceKey,
        Func<string, CancellationToken, Task<JsonElement>> getChannelAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceKey);
        ArgumentNullException.ThrowIfNull(getChannelAsync);

        lock (_sync)
        {
            if (TryGetCached(sourceKey, out var cached))
            {
                _logger.Information(
                    "FORWARD",
                    $"sourceChannel={sourceKey.ChannelId} sourceMessage={sourceKey.MessageId} " +
                    $"cache={(cached is null ? "NegativeHit" : "Hit")}.");
                return Task.FromResult(cached);
            }

            var lookupKey = new LookupKey(_generation, sourceKey);
            if (_inFlight.TryGetValue(lookupKey, out var pending))
            {
                _logger.Information(
                    "FORWARD",
                    $"sourceChannel={sourceKey.ChannelId} sourceMessage={sourceKey.MessageId} " +
                    "lookup=SingleFlightJoined.");
                return pending;
            }

            var task = ResolveCoreAsync(
                lookupKey,
                getChannelAsync,
                cancellationToken);
            _inFlight.Add(lookupKey, task);
            return task;
        }
    }

    private async Task<DiscordForwardContent?> ResolveCoreAsync(
        LookupKey lookupKey,
        Func<string, CancellationToken, Task<JsonElement>> getChannelAsync,
        CancellationToken cancellationToken)
    {
        var sourceKey = lookupKey.SourceKey;
        await Task.Yield();
        try
        {
            _logger.Information(
                "FORWARD",
                $"sourceChannel={sourceKey.ChannelId} sourceMessage={sourceKey.MessageId} " +
                "cache=Miss lookup=Started.");
            var response = await getChannelAsync(sourceKey.ChannelId, cancellationToken)
                .ConfigureAwait(false);
            var found = _normalizer.TryNormalizeForwardSource(response, sourceKey, out var content) &&
                content is not null;
            lock (_sync)
            {
                if (lookupKey.Generation == _generation)
                {
                    AddCacheEntry(
                        sourceKey,
                        found ? content : null,
                        found ? null : _timeProvider.GetUtcNow() + _negativeCacheTtl);
                }
            }

            _logger.Information(
                "FORWARD",
                $"sourceChannel={sourceKey.ChannelId} sourceMessage={sourceKey.MessageId} " +
                $"lookup={(found ? "Found" : "NotFound")}.");
            return found ? content : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                if (lookupKey.Generation == _generation)
                {
                    AddCacheEntry(
                        sourceKey,
                        null,
                        _timeProvider.GetUtcNow() + _negativeCacheTtl);
                }
            }

            _logger.Warning(
                "FORWARD",
                $"sourceChannel={sourceKey.ChannelId} sourceMessage={sourceKey.MessageId} " +
                $"lookup=Failed reason={exception.GetType().Name}.");
            return null;
        }
        finally
        {
            lock (_sync)
            {
                _inFlight.Remove(lookupKey);
            }
        }
    }

    private bool TryGetCached(
        DiscordForwardSourceKey sourceKey,
        out DiscordForwardContent? content)
    {
        content = null;
        if (!_cache.TryGetValue(sourceKey, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt is not null && entry.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            RemoveCacheEntry(sourceKey, entry);
            return false;
        }

        _lru.Remove(entry.Node);
        _lru.AddLast(entry.Node);
        content = entry.Content;
        return true;
    }

    private void AddCacheEntry(
        DiscordForwardSourceKey sourceKey,
        DiscordForwardContent? content,
        DateTimeOffset? expiresAt)
    {
        if (_cache.TryGetValue(sourceKey, out var previous))
        {
            RemoveCacheEntry(sourceKey, previous);
        }

        var node = _lru.AddLast(sourceKey);
        _cache.Add(sourceKey, new CacheEntry(content, expiresAt, node));
        while (_cache.Count > _capacity && _lru.First is not null)
        {
            var oldestKey = _lru.First.Value;
            if (_cache.TryGetValue(oldestKey, out var oldest))
            {
                RemoveCacheEntry(oldestKey, oldest);
            }
        }
    }

    private void RemoveCacheEntry(
        DiscordForwardSourceKey sourceKey,
        CacheEntry entry)
    {
        _cache.Remove(sourceKey);
        _lru.Remove(entry.Node);
    }

    private sealed record CacheEntry(
        DiscordForwardContent? Content,
        DateTimeOffset? ExpiresAt,
        LinkedListNode<DiscordForwardSourceKey> Node);

    private readonly record struct LookupKey(
        long Generation,
        DiscordForwardSourceKey SourceKey);
}

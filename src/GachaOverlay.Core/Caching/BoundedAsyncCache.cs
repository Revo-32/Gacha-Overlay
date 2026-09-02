namespace GachaOverlay.Core.Caching;

public enum BoundedCacheEvent
{
    Hit,
    Miss,
    FailureCooldown,
    StaleCompletion,
    Evicted,
}

public sealed class BoundedAsyncCache<TValue> : IDisposable
    where TValue : class
{
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly TimeSpan _failureCooldown;
    private readonly Func<string, Task<TValue?>> _loader;
    private readonly Action<BoundedCacheEvent>? _observer;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<TValue?>> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);
    private long _clock;
    private long _generation;
    private int _outstandingLoads;
    private bool _disposed;

    public BoundedAsyncCache(
        int capacity,
        Func<string, Task<TValue?>> loader,
        TimeSpan? failureCooldown = null,
        Action<BoundedCacheEvent>? observer = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _failureCooldown = failureCooldown ?? TimeSpan.FromMinutes(1);
        _observer = observer;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public int FailureCooldownCount
    {
        get
        {
            lock (_sync)
            {
                return _retryAfter.Count;
            }
        }
    }

    public int InFlightCount
    {
        get { lock (_sync) { return _outstandingLoads; } }
    }

    public async Task<TValue?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Task<TValue?> task;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var cached))
            {
                _entries[key] = cached with { LastUse = ++_clock };
                _observer?.Invoke(BoundedCacheEvent.Hit);
                return cached.Value;
            }

            if (_retryAfter.TryGetValue(key, out var retryAfter) &&
                retryAfter > DateTimeOffset.UtcNow)
            {
                _observer?.Invoke(BoundedCacheEvent.FailureCooldown);
                return null;
            }

            _observer?.Invoke(BoundedCacheEvent.Miss);

            if (!_inFlight.TryGetValue(key, out task!))
            {
                // Clearing a generation must not admit unlimited replacement downloads
                // while the retired loaders are still running. Do not queue more work.
                if (_outstandingLoads >= _capacity)
                {
                    return null;
                }

                var generation = _generation;
                _outstandingLoads++;
                task = Task.Run(() => LoadAndStoreAsync(key, generation));
                _inFlight[key] = task;
            }
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Clear()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _generation++;
            _entries.Clear();
            _inFlight.Clear();
            _retryAfter.Clear();
        }
    }

    public long EstimateSize(Func<TValue, long> estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        lock (_sync)
        {
            long total = 0;
            foreach (var entry in _entries.Values)
            {
                var value = Math.Max(0, estimate(entry.Value));
                total = value > long.MaxValue - total ? long.MaxValue : total + value;
            }

            return total;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _entries.Clear();
            _inFlight.Clear();
            _retryAfter.Clear();
        }
    }

    private async Task<TValue?> LoadAndStoreAsync(string key, long generation)
    {
        try
        {
            return await LoadAndStoreCoreAsync(key, generation).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync) { _outstandingLoads--; }
        }
    }

    private async Task<TValue?> LoadAndStoreCoreAsync(string key, long generation)
    {
        TValue? value;
        try
        {
            value = await _loader(key).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                if (!_disposed && generation == _generation)
                {
                    _inFlight.Remove(key);
                    _retryAfter[key] = DateTimeOffset.UtcNow.Add(_failureCooldown);
                    TrimFailureCooldowns();
                }
            }

            throw;
        }

        lock (_sync)
        {
            if (_disposed || generation != _generation)
            {
                _observer?.Invoke(BoundedCacheEvent.StaleCompletion);
                return value;
            }

            _inFlight.Remove(key);
            if (value is null)
            {
                _retryAfter[key] = DateTimeOffset.UtcNow.Add(_failureCooldown);
                TrimFailureCooldowns();
                return value;
            }

            _retryAfter.Remove(key);
            _entries[key] = new CacheEntry(value, ++_clock);
            while (_entries.Count > _capacity)
            {
                var oldest = _entries.MinBy(pair => pair.Value.LastUse).Key;
                _entries.Remove(oldest);
                _observer?.Invoke(BoundedCacheEvent.Evicted);
            }
        }

        return value;
    }

    private void TrimFailureCooldowns()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _retryAfter
                     .Where(pair => pair.Value <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _retryAfter.Remove(expired);
        }

        while (_retryAfter.Count > _capacity)
        {
            var earliest = _retryAfter.MinBy(pair => pair.Value).Key;
            _retryAfter.Remove(earliest);
        }
    }

    private sealed record CacheEntry(TValue Value, long LastUse);
}

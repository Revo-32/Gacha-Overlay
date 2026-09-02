using System.Collections.Concurrent;

namespace GachaOverlay.Core.Diagnostics;

public interface IRuntimeMetrics
{
    void Increment(string name, long amount = 1);

    void SetGauge(string name, double value);

    void SetState(string name, string value);

    void RecordDuration(string name, TimeSpan duration);

    RuntimeMetricsSnapshot Snapshot();
}

public static class RuntimeMetricNames
{
    public const string ChatActiveMainMessages = "chat.active_main.count";
    public const string ChatNormalizationDuration = "chat.normalization.duration";
    public const string ChatPresentationDuration = "chat.presentation.duration";
    public const string ChatRetentionEvictions = "chat.retention.eviction.count";
    public const string ChatStaleDiscards = "chat.stale.discard.count";
    public const string OpaqueAttempts = "chat.opaque.attempt.count";
    public const string OpaqueSucceeded = "chat.opaque.success.count";
    public const string OpaqueFailed = "chat.opaque.failure.count";
    public const string ForwardAttempts = "chat.forward.attempt.count";
    public const string ForwardSucceeded = "chat.forward.success.count";
    public const string ForwardFailed = "chat.forward.failure.count";
    public const string MediaActiveDownloads = "media.active_download.count";
    public const string MediaDownloadSucceeded = "media.download.success.count";
    public const string MediaDownloadFailed = "media.download.failure.count";
    public const string MediaDecodeDuration = "media.decode.duration";
    public const string MediaCacheHit = "media.cache.hit.count";
    public const string MediaCacheMiss = "media.cache.miss.count";
    public const string MediaStaleCompletion = "media.stale_completion.count";
    public const string MediaCacheItems = "media.cache.item.count";
    public const string MediaDecodedBytesEstimate = "media.decoded_bytes.estimate";
    public const string SalesActiveQueue = "sales.active_queue.count";
    public const string SalesSold = "sales.sold.count";
    public const string SalesResyncAttempts = "sales.resync.attempt.count";
    public const string SalesResyncSucceeded = "sales.resync.success.count";
    public const string SalesResyncFailed = "sales.resync.failure.count";
    public const string SalesManualResync = "sales.resync.manual.count";
    public const string SalesHealthTransitions = "sales.health.transition.count";
    public const string SalesCoverageTarget = "sales.coverage.target.count";
    public const string SalesCoverageObserved = "sales.coverage.observed.count";
    public const string SalesLastCompleteUnixSeconds = "sales.last_complete.unix_seconds";
    public const string SalesState = "sales.state";
    public const string RemoteSalesState = "sales.remote.state";
    public const string RemoteSalesObservations = "sales.remote.observation.count";
    public const string EffectiveSalesSource = "sales.effective_source";
    public const string EffectiveSalesSourceTransitions =
        "sales.effective_source.transition.count";
    public const string RemotePrimaryTransitions = "sales.remote_primary.transition.count";
    public const string RemoteRecoveryTransitions = "sales.remote_recovery.transition.count";
    public const string RemotePromotionSucceeded = "sales.remote_promotion.success.count";
    public const string RemotePromotionFailures = "sales.remote_promotion.failure.count";
    public const string HudUpdateDuration = "wpf.hud_update.duration";
    public const string SettingsUpdateDuration = "wpf.settings_update.duration";
    public const string DispatcherLongOperations = "wpf.dispatcher.long_operation.count";
    public const string UiUpdatesCoalesced = "wpf.update.coalesced.count";
    public const string DiagnosticExports = "diagnostics.export.count";
    public const string DiagnosticExportFailures = "diagnostics.export.failure.count";
    public const string DiagnosticExportDuration = "diagnostics.export.duration";
}

public sealed record DurationMetricSnapshot(
    long Count,
    int RetainedSampleCount,
    double AverageMilliseconds,
    double MaximumMilliseconds,
    double P95Milliseconds,
    double P99Milliseconds);

public sealed record RuntimeMetricsSnapshot(
    DateTimeOffset StartedAt,
    DateTimeOffset CapturedAt,
    double UptimeSeconds,
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyDictionary<string, double> Gauges,
    IReadOnlyDictionary<string, string> States,
    IReadOnlyDictionary<string, DurationMetricSnapshot> Durations);

public sealed class RuntimeMetricsCollector : IRuntimeMetrics
{
    public const int DefaultDurationSampleCapacity = 256;
    private readonly ConcurrentDictionary<string, Counter> _counters =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BoundedDurationMetric> _durations =
        new(StringComparer.Ordinal);
    private readonly object _valueSync = new();
    private readonly Dictionary<string, double> _gauges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);
    private readonly int _durationSampleCapacity;
    private readonly Func<DateTimeOffset> _clock;

    public RuntimeMetricsCollector(
        int durationSampleCapacity = DefaultDurationSampleCapacity,
        Func<DateTimeOffset>? clock = null)
    {
        if (durationSampleCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSampleCapacity));
        }

        _durationSampleCapacity = durationSampleCapacity;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        StartedAt = _clock();
    }

    public DateTimeOffset StartedAt { get; }

    public void Increment(string name, long amount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _counters.GetOrAdd(name, static _ => new Counter()).Add(amount);
    }

    public void SetGauge(string name, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!double.IsFinite(value))
        {
            return;
        }

        lock (_valueSync)
        {
            _gauges[name] = value;
        }
    }

    public void SetState(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_valueSync)
        {
            _states[name] = value ?? string.Empty;
        }
    }

    public void RecordDuration(string name, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var milliseconds = duration.TotalMilliseconds;
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
        {
            return;
        }

        _durations.GetOrAdd(
                name,
                _ => new BoundedDurationMetric(_durationSampleCapacity))
            .Record(milliseconds);
    }

    public RuntimeMetricsSnapshot Snapshot()
    {
        var capturedAt = _clock();
        Dictionary<string, double> gauges;
        Dictionary<string, string> states;
        lock (_valueSync)
        {
            gauges = _gauges.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            states = _states.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        return new RuntimeMetricsSnapshot(
            StartedAt,
            capturedAt,
            Math.Max(0, (capturedAt - StartedAt).TotalSeconds),
            _counters.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Value,
                StringComparer.Ordinal),
            gauges,
            states,
            _durations.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Snapshot(),
                StringComparer.Ordinal));
    }

    public void Reset()
    {
        _counters.Clear();
        _durations.Clear();
        lock (_valueSync)
        {
            _gauges.Clear();
            _states.Clear();
        }
    }

    private sealed class Counter
    {
        private long _value;

        public long Value => Interlocked.Read(ref _value);

        public void Add(long amount) => Interlocked.Add(ref _value, amount);
    }
}

public sealed class BoundedDurationMetric
{
    private readonly object _sync = new();
    private readonly double[] _samples;
    private int _next;
    private int _retained;
    private long _count;

    public BoundedDurationMetric(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _samples = new double[capacity];
    }

    public int Capacity => _samples.Length;

    public void Record(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
        {
            return;
        }

        lock (_sync)
        {
            _samples[_next] = milliseconds;
            _next = (_next + 1) % _samples.Length;
            _retained = Math.Min(_retained + 1, _samples.Length);
            _count++;
        }
    }

    public DurationMetricSnapshot Snapshot()
    {
        double[] samples;
        long count;
        lock (_sync)
        {
            samples = _samples.Take(_retained).ToArray();
            count = _count;
        }

        if (samples.Length == 0)
        {
            return new DurationMetricSnapshot(count, 0, 0, 0, 0, 0);
        }

        Array.Sort(samples);
        return new DurationMetricSnapshot(
            count,
            samples.Length,
            samples.Average(),
            samples[^1],
            Percentile(samples, 0.95),
            Percentile(samples, 0.99));
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var index = Math.Clamp(
            (int)Math.Ceiling(percentile * sorted.Count) - 1,
            0,
            sorted.Count - 1);
        return sorted[index];
    }
}

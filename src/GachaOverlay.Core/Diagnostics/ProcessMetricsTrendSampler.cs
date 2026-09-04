namespace GachaOverlay.Core.Diagnostics;

public sealed record ProcessMetricTrend(
    double ObservationDurationSeconds,
    int SampleCount,
    double? Start,
    double? Minimum,
    double? Average,
    double? Maximum,
    double? Current,
    double? StartToCurrentDelta,
    double? Recent60MinutesDelta,
    double? Recent120MinutesDelta);

public sealed record ProcessMetricsTrendSummary(
    DateTimeOffset CapturedAt,
    double DurationSeconds,
    int SampleCount,
    IReadOnlyDictionary<string, ProcessMetricTrend> Metrics);

public sealed record ProcessMetricsTrendCapture(
    ProcessMetricsSnapshot Current,
    IReadOnlyList<ProcessMetricsSnapshot> Samples,
    ProcessMetricsTrendSummary Summary);

public sealed class ProcessMetricsTrendSampler : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);
    public const int DefaultCapacity = 720;

    private readonly object _sync = new();
    private readonly ProcessMetricsSampler _sampler;
    private readonly ProcessMetricsSnapshot?[] _samples;
    private readonly Timer? _timer;
    private int _next;
    private int _count;
    private bool _disposed;

    public ProcessMetricsTrendSampler(
        ProcessMetricsSampler? sampler = null,
        TimeSpan? interval = null,
        int capacity = DefaultCapacity,
        bool startTimer = true)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        var sampleInterval = interval ?? DefaultInterval;
        if (sampleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _sampler = sampler ?? new ProcessMetricsSampler();
        _samples = new ProcessMetricsSnapshot[capacity];
        CaptureSample();
        if (startTimer)
        {
            _timer = new Timer(
                static state => ((ProcessMetricsTrendSampler)state!).CaptureSample(),
                this,
                sampleInterval,
                sampleInterval);
        }
    }

    public int Capacity => _samples.Length;

    public ProcessMetricsTrendCapture Capture()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = _sampler.Sample();
            AddUnderLock(current);
            var frozen = CopyOrderedUnderLock();
            return new ProcessMetricsTrendCapture(
                current,
                frozen,
                BuildSummary(frozen, current.CapturedAt));
        }
    }

    private void CaptureSample()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            AddUnderLock(_sampler.Sample());
        }
    }

    private void AddUnderLock(ProcessMetricsSnapshot sample)
    {
        _samples[_next] = sample;
        _next = (_next + 1) % _samples.Length;
        _count = Math.Min(_count + 1, _samples.Length);
    }

    private ProcessMetricsSnapshot[] CopyOrderedUnderLock()
    {
        var result = new ProcessMetricsSnapshot[_count];
        var start = (_next - _count + _samples.Length) % _samples.Length;
        for (var index = 0; index < _count; index++)
        {
            result[index] = _samples[(start + index) % _samples.Length]!;
        }

        return result;
    }

    private static ProcessMetricsTrendSummary BuildSummary(
        IReadOnlyList<ProcessMetricsSnapshot> samples,
        DateTimeOffset capturedAt)
    {
        var metrics = new Dictionary<string, ProcessMetricTrend>(StringComparer.Ordinal)
        {
            ["PrivateWorkingSetBytes"] = Trend(samples, sample => sample.PrivateWorkingSetBytes),
            ["TotalWorkingSetBytes"] = Trend(samples, sample => sample.TotalWorkingSetBytes),
            ["PrivateCommitBytes"] = Trend(samples, sample => sample.PrivateCommitBytes),
            ["ManagedHeapBytes"] = Trend(samples, sample => sample.ManagedHeapBytes),
            ["HandleCount"] = Trend(samples, sample => sample.HandleCount),
            ["ThreadCount"] = Trend(samples, sample => sample.ThreadCount),
            ["GdiObjectCount"] = Trend(samples, sample => sample.GdiObjectCount),
            ["UserObjectCount"] = Trend(samples, sample => sample.UserObjectCount),
            ["CpuPercent"] = Trend(samples, sample => sample.CpuPercent),
        };
        return new ProcessMetricsTrendSummary(
            capturedAt,
            samples.Count == 0
                ? 0
                : Math.Max(0, (samples[^1].CapturedAt - samples[0].CapturedAt).TotalSeconds),
            samples.Count,
            metrics);
    }

    private static ProcessMetricTrend Trend(
        IReadOnlyList<ProcessMetricsSnapshot> samples,
        Func<ProcessMetricsSnapshot, double?> selector)
    {
        var values = samples
            .Select(sample => new { sample.CapturedAt, Value = selector(sample) })
            .Where(item => item.Value.HasValue)
            .Select(item => new TimedValue(item.CapturedAt, item.Value!.Value))
            .ToArray();
        if (values.Length == 0)
        {
            return new ProcessMetricTrend(0, 0, null, null, null, null, null, null, null, null);
        }

        var start = values[0].Value;
        var current = values[^1].Value;
        return new ProcessMetricTrend(
            Math.Max(0, (values[^1].CapturedAt - values[0].CapturedAt).TotalSeconds),
            values.Length,
            start,
            values.Min(item => item.Value),
            values.Average(item => item.Value),
            values.Max(item => item.Value),
            current,
            current - start,
            RecentDelta(values, TimeSpan.FromMinutes(60)),
            RecentDelta(values, TimeSpan.FromMinutes(120)));
    }

    private static double? RecentDelta(
        IReadOnlyList<TimedValue> values,
        TimeSpan period)
    {
        var current = values[^1];
        var cutoff = current.CapturedAt - period;
        TimedValue? baseline = null;
        for (var index = values.Count - 1; index >= 0; index--)
        {
            if (values[index].CapturedAt <= cutoff)
            {
                baseline = values[index];
                break;
            }
        }

        return baseline is null ? null : current.Value - baseline.Value;
    }

    private sealed record TimedValue(DateTimeOffset CapturedAt, double Value);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
        }
    }
}

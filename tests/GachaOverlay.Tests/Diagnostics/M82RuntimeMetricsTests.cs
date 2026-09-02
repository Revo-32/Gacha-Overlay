using GachaOverlay.Core.Diagnostics;

namespace GachaOverlay.Tests.Diagnostics;

public sealed class M82RuntimeMetricsTests
{
    [Fact]
    public void Duration_history_is_bounded_and_percentiles_are_deterministic()
    {
        var metric = new BoundedDurationMetric(100);

        for (var value = 1; value <= 200; value++)
        {
            metric.Record(value);
        }

        var snapshot = metric.Snapshot();
        Assert.Equal(200, snapshot.Count);
        Assert.Equal(100, snapshot.RetainedSampleCount);
        Assert.Equal(200, snapshot.MaximumMilliseconds);
        Assert.Equal(195, snapshot.P95Milliseconds);
        Assert.Equal(199, snapshot.P99Milliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(256)]
    public void Duration_snapshot_supports_zero_single_and_bounded_sample_counts(int count)
    {
        var metric = new BoundedDurationMetric(256);
        for (var index = 0; index < count; index++)
        {
            metric.Record(index + 1);
        }

        var snapshot = metric.Snapshot();

        Assert.Equal(count, snapshot.Count);
        Assert.Equal(count, snapshot.RetainedSampleCount);
        Assert.True(double.IsFinite(snapshot.AverageMilliseconds));
        Assert.True(double.IsFinite(snapshot.MaximumMilliseconds));
        Assert.True(double.IsFinite(snapshot.P95Milliseconds));
        Assert.True(double.IsFinite(snapshot.P99Milliseconds));
        if (count == 0)
        {
            Assert.Equal(0, snapshot.AverageMilliseconds);
            Assert.Equal(0, snapshot.MaximumMilliseconds);
            Assert.Equal(0, snapshot.P95Milliseconds);
            Assert.Equal(0, snapshot.P99Milliseconds);
        }
    }

    [Fact]
    public async Task Counters_are_thread_safe()
    {
        var metrics = new RuntimeMetricsCollector();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 10_000; index++)
            {
                metrics.Increment("parallel");
            }
        })));

        Assert.Equal(80_000, metrics.Snapshot().Counters["parallel"]);
    }

    [Fact]
    public void Snapshot_and_reset_keep_only_current_bounded_state()
    {
        var metrics = new RuntimeMetricsCollector(durationSampleCapacity: 3);
        metrics.Increment("events", 4);
        metrics.SetGauge("workers", 2);
        metrics.SetState("health", "Live");
        metrics.RecordDuration("scan", TimeSpan.FromMilliseconds(1));
        metrics.RecordDuration("scan", TimeSpan.FromMilliseconds(2));
        metrics.RecordDuration("scan", TimeSpan.FromMilliseconds(3));
        metrics.RecordDuration("scan", TimeSpan.FromMilliseconds(4));

        var before = metrics.Snapshot();
        Assert.Equal(4, before.Counters["events"]);
        Assert.Equal(2, before.Gauges["workers"]);
        Assert.Equal("Live", before.States["health"]);
        Assert.Equal(4, before.Durations["scan"].Count);
        Assert.Equal(3, before.Durations["scan"].RetainedSampleCount);

        metrics.Reset();

        var after = metrics.Snapshot();
        Assert.Empty(after.Counters);
        Assert.Empty(after.Gauges);
        Assert.Empty(after.States);
        Assert.Empty(after.Durations);
        Assert.True(after.UptimeSeconds >= 0);
    }

    [Fact]
    public async Task Process_snapshot_exposes_required_resource_metrics()
    {
        var sampler = new ProcessMetricsSampler();
        var first = sampler.Sample();
        await Task.Delay(20);
        var second = sampler.Sample();

        Assert.True(second.UptimeSeconds >= first.UptimeSeconds);
        Assert.True(second.WorkingSetBytes > 0);
        Assert.True(second.PrivateBytes > 0);
        Assert.True(second.HandleCount is null or >= 0);
        Assert.True(second.ThreadCount > 0);
        Assert.True(second.GcTotalMemoryBytes > 0);
        Assert.True(second.Gen0Collections >= 0);
        Assert.True(second.Gen1Collections >= 0);
        Assert.True(second.Gen2Collections >= 0);
        Assert.True(second.CpuPercent is null or >= 0);
    }
}

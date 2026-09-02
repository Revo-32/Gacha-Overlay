using System.Diagnostics;
using System.Text.Json;
using GachaOverlay.Core.Caching;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Hud;
using GachaOverlay.Tests.Discord.Messages;
using Xunit.Abstractions;

namespace GachaOverlay.Tests.Diagnostics;

public sealed class M82SyntheticReplaySoakTests
{
    private readonly ITestOutputHelper _output;

    public M82SyntheticReplaySoakTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Bounded_components_survive_controlled_replay_without_backlog_growth()
    {
        const int messageEvents = 5_000;
        const int failedMediaKeys = 5_000;
        var processSampler = new ProcessMetricsSampler();
        var start = processSampler.Sample();
        var metrics = new RuntimeMetricsCollector(durationSampleCapacity: 128);
        var pipeline = new DiscordMessagePipeline(metrics: metrics);
        var targets = new DiscordTargetChannels(
            "guild",
            "Guild",
            "main",
            "Main",
            "sales",
            "Sales");
        Assert.True(pipeline.StartBootstrap(1, targets));
        Assert.True(pipeline.CompleteBootstrap(
            1,
            Array.Empty<DiscordMessagePatch>(),
            Array.Empty<DiscordMessagePatch>()));
        var stopwatch = Stopwatch.StartNew();

        for (var index = 1; index <= messageEvents; index++)
        {
            Assert.True(pipeline.ReceiveLive(
                1,
                DiscordMessageMutation.Create(TestMessageFactory.FullPatch(index))));
            metrics.RecordDuration("synthetic.message", TimeSpan.FromTicks(index % 100 + 1));
        }

        using var cache = new BoundedAsyncCache<ReplayValue>(
            24,
            _ => Task.FromResult<ReplayValue?>(null),
            failureCooldown: TimeSpan.FromHours(1));
        for (var index = 0; index < failedMediaKeys; index++)
        {
            Assert.Null(await cache.GetAsync($"media-{index}"));
        }

        var hud = new HudStateService();
        var initialHudState = hud.Current;
        for (var index = 0; index < 100; index++)
        {
            hud.ToggleUserVisibility();
            hud.ToggleLock();
        }

        stopwatch.Stop();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var finish = processSampler.Sample();
        var metricSnapshot = metrics.Snapshot();

        Assert.Equal(DiscordMessagePipeline.MainChatRetentionLimit, pipeline.Current.MainChat.Count);
        Assert.Equal(
            messageEvents - DiscordMessagePipeline.MainChatRetentionLimit,
            metricSnapshot.Counters[RuntimeMetricNames.ChatRetentionEvictions]);
        Assert.Equal(128, metricSnapshot.Durations["synthetic.message"].RetainedSampleCount);
        Assert.InRange(cache.FailureCooldownCount, 0, 24);
        Assert.Equal(initialHudState.UserHudEnabled, hud.Current.UserHudEnabled);
        Assert.Equal(initialHudState.IsLocked, hud.Current.IsLocked);

        _output.WriteLine(JsonSerializer.Serialize(new
        {
            Scenario = "Synthetic replay: 5,000 chat + 5,000 failed media keys + 100 F9/F10 pairs",
            DurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            Start = new
            {
                WorkingSetMiB = start.WorkingSetBytes / 1024d / 1024d,
                PrivateMiB = start.PrivateBytes / 1024d / 1024d,
                start.HandleCount,
                start.ThreadCount,
                GcMiB = start.GcTotalMemoryBytes / 1024d / 1024d,
            },
            Finish = new
            {
                WorkingSetMiB = finish.WorkingSetBytes / 1024d / 1024d,
                PrivateMiB = finish.PrivateBytes / 1024d / 1024d,
                finish.HandleCount,
                finish.ThreadCount,
                GcMiB = finish.GcTotalMemoryBytes / 1024d / 1024d,
            },
            ActiveMain = pipeline.Current.MainChat.Count,
            RetentionEvictions = metricSnapshot.Counters[RuntimeMetricNames.ChatRetentionEvictions],
            DurationSamplesRetained = metricSnapshot.Durations["synthetic.message"].RetainedSampleCount,
            FailureCooldownKeys = cache.FailureCooldownCount,
        }));
    }

    private sealed record ReplayValue(string Value);
}

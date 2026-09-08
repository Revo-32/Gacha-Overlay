using GachaOverlay.App.Services;
using GachaOverlay.Core.Hud.Game;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Timers;
using GachaOverlay.Core.Business;

namespace GachaOverlay.Tests;

public sealed class GtaDetection23Tests
{
    private static readonly GtaProcessIdentity A = new(100, 1, "GTA5");
    private static readonly GtaProcessIdentity B = new(101, 2, "GTA5_Enhanced");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BootstrapOrLaterStartAndAltTab(bool runningBeforeOverlay)
    {
        var tracker = new GtaProcessTracker();
        if (!runningBeforeOverlay) tracker.Reconcile(new(true, [], 0));
        tracker.Reconcile(new(true, [A], A.Pid));
        Assert.True(tracker.Current.ProcessRunning && tracker.Current.ValidTrackedProcess && tracker.Current.Foreground);
        var generation = tracker.Current.Generation;
        tracker.Reconcile(new(true, [A], 900));
        Assert.True(tracker.Current.ProcessRunning && tracker.Current.ValidTrackedProcess);
        Assert.False(tracker.Current.Foreground);
        Assert.Equal(generation, tracker.Current.Generation);
        tracker.Reconcile(new(true, [A], A.Pid));
        Assert.True(tracker.Current.Foreground);
    }

    [Fact]
    public void RestartAndStaleExitCannotLoseNewGeneration()
    {
        var tracker = new GtaProcessTracker();
        tracker.Reconcile(new(true, [A], 100));
        var old = tracker.Current.Generation;
        tracker.Exited(A, old);
        Assert.False(tracker.Current.ProcessRunning);
        tracker.Reconcile(new(true, [B], 101));
        tracker.Exited(A, old);
        Assert.Equal(B, tracker.Current.Process);
        Assert.True(tracker.Current.Generation > old);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneFailureOrAbsentSampleDoesNotFlapButTwoConfirmLoss(bool succeeded)
    {
        var tracker = new GtaProcessTracker();
        tracker.Reconcile(new(true, [A], 100));
        tracker.Reconcile(new(succeeded, [], 0));
        Assert.True(tracker.Current.ProcessRunning);
        tracker.Reconcile(new(true, [A], 100));
        Assert.True(tracker.Current.ProcessRunning);
        tracker.Reconcile(new(succeeded, [], 0));
        tracker.Reconcile(new(succeeded, [], 0));
        Assert.False(tracker.Current.ProcessRunning);
    }

    [Fact]
    public void DuplicatePollsAndUiActionsDoNotChangeProcessState()
    {
        var tracker = new GtaProcessTracker();
        var changes = 0;
        tracker.Changed += _ => changes++;
        for (var i = 0; i < 30; i++) tracker.Reconcile(new(true, [A], 100));
        Assert.Equal(1, changes);
        var source = new RemoteOnlinePlaytimeStatusSource(AppSettings.CreateDefault());
        source.ApplyProcess(tracker.Current);
        source.ApplySettings(AppSettings.CreateDefault());
        Assert.Equal(OnlinePlaytimeAvailability.Online, source.Current);
        tracker.Reconcile(new(true, [A], 900));
        source.ApplyProcess(tracker.Current);
        Assert.Equal(OnlinePlaytimeAvailability.Online, source.Current);
        tracker.Exited(A, tracker.Current.Generation);
        source.ApplyProcess(tracker.Current);
        Assert.Equal(OnlinePlaytimeAvailability.Offline, source.Current);
    }

    [Fact]
    public void RecognizedNamesRemainExactlyLegacyAndEnhanced()
    {
        Assert.Equal(new[] { "GTA5", "GTA5_Enhanced" }, TargetGameMatcher.DefaultProcessNames);
        Assert.False(new TargetGameMatcher().IsTarget("GTAVLauncher"));
    }

    [Fact]
    public void BunkerAccumulatesLocalProcessTimeIncludingAltTabAndStopsAfterLoss()
    {
        var clock = new Clock();
        using var engine = new BusinessManagerEngine(new SharedTimerRegistry(new Store(), clock));
        var tracker = new GtaProcessTracker();
        var source = new RemoteOnlinePlaytimeStatusSource(AppSettings.CreateDefault());
        tracker.Changed += source.ApplyProcess;
        tracker.Reconcile(new(true, [A], 100));
        engine.Update(source.Current);
        engine.StartBunker();
        clock.Advance(30);
        Assert.Equal(TimeSpan.FromSeconds(30), engine.Update(source.Current).Single().AccumulatedOnlineTime);
        tracker.Reconcile(new(true, [A], 900));
        clock.Advance(30);
        Assert.Equal(TimeSpan.FromSeconds(60), engine.Update(source.Current).Single().AccumulatedOnlineTime);
        tracker.Exited(A, tracker.Current.Generation);
        engine.Update(source.Current);
        clock.Advance(60);
        var paused = engine.Update(source.Current).Single();
        Assert.Equal(SharedTimerState.Paused, paused.State);
        Assert.Equal(TimeSpan.FromSeconds(60), paused.AccumulatedOnlineTime);
        tracker.Reconcile(new(true, [B], 101));
        engine.Update(source.Current);
        clock.Advance(30);
        Assert.Equal(TimeSpan.FromSeconds(90), engine.Update(source.Current).Single().AccumulatedOnlineTime);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-08T00:00:00Z").AddTicks(_ticks);
        public void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class Store : ISharedTimerStore
    {
        public IReadOnlyList<SharedTimerPersistedEntry> Load() => [];
        public bool Save(IReadOnlyCollection<SharedTimerPersistedEntry> entries) => true;
    }
}

using LSOverlay.Backend.Events;
using LSOverlay.Backend.Presence;
using LSOverlay.Backend.Runtime;

namespace GachaOverlay.Tests.Backend;

public sealed class BackendStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PresenceStore_StartsConfiguredHostsAsAwaitingInStableOrder()
    {
        var store = new TrackedHostPresenceStore(new ulong[] { 9, 7 }, () => Now);

        Assert.Equal(2, store.Count);
        Assert.Equal(1, store.GetStableIndex(9));
        Assert.Equal(2, store.GetStableIndex(7));
        Assert.All(store.Snapshot(), item =>
            Assert.Equal(BackendDiscordPresenceStatus.AwaitingPresence, item.DiscordStatus));
    }

    [Fact]
    public void PresenceStore_DiscardsUntrackedHostsWithoutAllocatingState()
    {
        var store = new TrackedHostPresenceStore(new ulong[] { 9 }, () => Now);
        var next = Presence(8, BackendDiscordPresenceStatus.Online, true, 1, 30);

        Assert.False(store.TryUpdate(next, out var changed));
        Assert.Null(changed);
        Assert.Equal(1, store.Count);
        Assert.Equal((ulong)9, Assert.Single(store.Snapshot()).HostId);
    }

    [Fact]
    public void PresenceStore_SuppressesSemanticDuplicatesAndRetainsLatestObservationOnlyOnChange()
    {
        var store = new TrackedHostPresenceStore(new ulong[] { 9 }, () => Now);
        var first = Presence(9, BackendDiscordPresenceStatus.Online, true, 1, 30);
        var duplicate = first with { ObservedAt = Now.AddMinutes(1) };

        Assert.True(store.TryUpdate(first, out var changed));
        Assert.Same(first, changed);
        Assert.False(store.TryUpdate(duplicate, out changed));
        Assert.Null(changed);
        Assert.Equal(Now, Assert.Single(store.Snapshot()).ObservedAt);
    }

    [Fact]
    public void PresenceStore_PublishesStructuredPartyChangeExactlyOnce()
    {
        var store = new TrackedHostPresenceStore(new ulong[] { 9 }, () => Now);
        var eleven = Presence(9, BackendDiscordPresenceStatus.Online, true, 11, 32);
        var twelve = Presence(9, BackendDiscordPresenceStatus.Online, true, 12, 32);

        Assert.True(store.TryUpdate(eleven, out _));
        Assert.True(store.TryUpdate(twelve, out var changed));
        Assert.Equal(12, changed?.CurrentPlayers);
        Assert.False(store.TryUpdate(twelve with { ObservedAt = Now.AddSeconds(1) }, out _));
    }

    [Fact]
    public void PresenceStore_PublishesGtaActivityDisappearanceOnce()
    {
        var store = new TrackedHostPresenceStore(new ulong[] { 9 }, () => Now);
        var gta = Presence(9, BackendDiscordPresenceStatus.Online, true, 11, 32);
        var noActivity = Presence(9, BackendDiscordPresenceStatus.Online, false, null, null);

        Assert.True(store.TryUpdate(gta, out _));
        Assert.True(store.TryUpdate(noActivity, out var changed));
        Assert.False(changed?.GtaActivityPresent);
        Assert.False(store.TryUpdate(noActivity with { ObservedAt = Now.AddSeconds(1) }, out _));
    }

    [Fact]
    public void Journal_EvictsOldestAndKeepsMonotonicSequencePerGeneration()
    {
        var journal = new BackendEventJournal(generation: 7, capacity: 2);
        journal.Append(Presence(1, BackendDiscordPresenceStatus.Online, false, null, null));
        journal.Append(Presence(2, BackendDiscordPresenceStatus.Online, false, null, null));
        journal.Append(Presence(3, BackendDiscordPresenceStatus.Online, false, null, null));

        var entries = journal.Snapshot();
        Assert.Equal(2, journal.Count);
        Assert.Equal(new long[] { 2, 3 }, entries.Select(item => item.Position.EventSequence));
        Assert.All(entries, item => Assert.Equal(7, item.Position.BootstrapGeneration));
        Assert.Equal(new ulong[] { 2, 3 }, entries.Select(item =>
            Assert.IsType<TrackedHostPresenceSnapshot>(item.Signal).HostId));
    }

    [Fact]
    public void Journal_IsThreadSafeAndRemainsBounded()
    {
        var journal = new BackendEventJournal(generation: 8, capacity: 64);

        Parallel.For(0, 512, index => journal.Append(Presence(
            (ulong)index + 1,
            BackendDiscordPresenceStatus.Online,
            false,
            null,
            null)));

        var entries = journal.Snapshot();
        Assert.Equal(64, entries.Count);
        Assert.Equal(64, entries.Select(item => item.Position.EventSequence).Distinct().Count());
        Assert.True(entries.Zip(entries.Skip(1), (left, right) =>
            left.Position.EventSequence < right.Position.EventSequence).All(value => value));
    }

    [Fact]
    public void Health_DeduplicatesSameStateAndReason()
    {
        var health = new BackendConnectionHealth();
        var changes = new List<BackendConnectionHealthSnapshot>();
        health.Changed += changes.Add;

        Assert.True(health.Transition(
            BackendConnectionHealthState.Starting,
            BackendConnectionHealthReason.Startup));
        Assert.False(health.Transition(
            BackendConnectionHealthState.Starting,
            BackendConnectionHealthReason.Startup));
        Assert.Single(changes);
    }

    [Fact]
    public void Health_RemembersFaultAcrossGracefulStopForProcessExitCode()
    {
        var health = new BackendConnectionHealth();
        health.Transition(
            BackendConnectionHealthState.Faulted,
            BackendConnectionHealthReason.AuthenticationFailed);
        health.Transition(
            BackendConnectionHealthState.Stopped,
            BackendConnectionHealthReason.GracefulShutdown);

        Assert.True(health.HasFaulted);
        Assert.Equal(BackendConnectionHealthState.Stopped, health.Current.State);
    }

    [Fact]
    public void Metrics_UseFixedCountersAndAtomicSnapshots()
    {
        var metrics = new BackendMetrics();
        Parallel.For(0, 1000, _ => metrics.Increment(BackendMetric.PresenceReceived));

        Assert.Equal(1000, metrics.Get(BackendMetric.PresenceReceived));
        Assert.Equal(1000, metrics.Snapshot().PresenceReceived);
    }

    private static TrackedHostPresenceSnapshot Presence(
        ulong id,
        BackendDiscordPresenceStatus status,
        bool online,
        int? current,
        int? maximum) => new(
            id,
            status,
            online,
            online,
            current,
            maximum,
            Now);
}

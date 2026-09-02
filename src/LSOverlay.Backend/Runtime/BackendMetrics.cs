namespace LSOverlay.Backend.Runtime;

internal enum BackendMetric
{
    DiscordConnected,
    DiscordDisconnected,
    DiscordReady,
    MessageCreate,
    MessageUpdate,
    MessageDelete,
    MessageFilteredOtherGuild,
    ReactionAdd,
    ReactionRemove,
    ReactionClear,
    ReactionFilteredOtherGuild,
    PresenceReceived,
    PresenceTracked,
    PresenceDiscardedUntracked,
    PresenceFilteredOtherGuild,
    PresenceGtaActivityMatch,
    PresenceStructuredPartyAvailable,
    PresenceNormalizedChange,
}

internal sealed record BackendMetricsSnapshot(
    long DiscordConnected,
    long DiscordDisconnected,
    long DiscordReady,
    long MessageCreate,
    long MessageUpdate,
    long MessageDelete,
    long MessageFilteredOtherGuild,
    long ReactionAdd,
    long ReactionRemove,
    long ReactionClear,
    long ReactionFilteredOtherGuild,
    long PresenceReceived,
    long PresenceTracked,
    long PresenceDiscardedUntracked,
    long PresenceFilteredOtherGuild,
    long PresenceGtaActivityMatch,
    long PresenceStructuredPartyAvailable,
    long PresenceNormalizedChange);

internal sealed class BackendMetrics
{
    private readonly long[] _counters = new long[Enum.GetValues<BackendMetric>().Length];

    public void Increment(BackendMetric metric) =>
        Interlocked.Increment(ref _counters[(int)metric]);

    public long Get(BackendMetric metric) =>
        Interlocked.Read(ref _counters[(int)metric]);

    public BackendMetricsSnapshot Snapshot() => new(
        Get(BackendMetric.DiscordConnected),
        Get(BackendMetric.DiscordDisconnected),
        Get(BackendMetric.DiscordReady),
        Get(BackendMetric.MessageCreate),
        Get(BackendMetric.MessageUpdate),
        Get(BackendMetric.MessageDelete),
        Get(BackendMetric.MessageFilteredOtherGuild),
        Get(BackendMetric.ReactionAdd),
        Get(BackendMetric.ReactionRemove),
        Get(BackendMetric.ReactionClear),
        Get(BackendMetric.ReactionFilteredOtherGuild),
        Get(BackendMetric.PresenceReceived),
        Get(BackendMetric.PresenceTracked),
        Get(BackendMetric.PresenceDiscardedUntracked),
        Get(BackendMetric.PresenceFilteredOtherGuild),
        Get(BackendMetric.PresenceGtaActivityMatch),
        Get(BackendMetric.PresenceStructuredPartyAvailable),
        Get(BackendMetric.PresenceNormalizedChange));
}

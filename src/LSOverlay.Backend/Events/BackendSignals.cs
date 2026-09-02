namespace LSOverlay.Backend.Events;

internal interface IBackendSignal
{
    DateTimeOffset ObservedAt { get; }
}

internal enum BackendMessageOperation
{
    Create,
    Update,
    Delete,
}

internal sealed record BackendMessageSignal(
    BackendMessageOperation Operation,
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    ulong? AuthorId,
    DateTimeOffset? CreatedAt,
    DateTimeOffset ObservedAt,
    int AttachmentCount,
    int EmbedCount,
    int StickerCount,
    int ComponentCount,
    int ForwardedSnapshotCount,
    bool HasReferencedMessage,
    bool HasPoll) : IBackendSignal;

internal enum BackendReactionOperation
{
    Add,
    Remove,
    ClearAll,
    ClearEmoji,
}

internal sealed record BackendReactionSignal(
    BackendReactionOperation Operation,
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    ulong? UserId,
    ulong? EmojiId,
    string? EmojiName,
    DateTimeOffset ObservedAt) : IBackendSignal;

internal enum BackendDiscordPresenceStatus
{
    AwaitingPresence,
    Offline,
    Online,
    Idle,
    DoNotDisturb,
}

internal sealed record TrackedHostPresenceSnapshot(
    ulong HostId,
    BackendDiscordPresenceStatus DiscordStatus,
    bool GtaActivityPresent,
    bool GtaOnlineActive,
    int? CurrentPlayers,
    int? MaximumPlayers,
    DateTimeOffset ObservedAt) : IBackendSignal
{
    public bool SemanticallyEquals(TrackedHostPresenceSnapshot other) =>
        HostId == other.HostId &&
        DiscordStatus == other.DiscordStatus &&
        GtaActivityPresent == other.GtaActivityPresent &&
        GtaOnlineActive == other.GtaOnlineActive &&
        CurrentPlayers == other.CurrentPlayers &&
        MaximumPlayers == other.MaximumPlayers;
}

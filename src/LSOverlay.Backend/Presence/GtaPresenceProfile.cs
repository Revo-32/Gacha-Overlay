namespace LSOverlay.Backend.Presence;

internal sealed record GtaPresenceProfile(
    string DisplayLabel,
    ulong ApplicationId,
    string OnlineState)
{
    public static GtaPresenceProfile Enhanced { get; } = new(
        "Grand Theft Auto V Enhanced",
        1329870933695135785,
        "GTA Online");
}

internal sealed record BackendActivityCandidate(
    ulong ApplicationId,
    string Name,
    bool IsPlaying,
    string? State,
    long? PartyMembers,
    long? PartyCapacity);

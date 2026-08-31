namespace GachaOverlay.Core.Discord.Connection;

public enum DiscordServerDiscoveryState
{
    Ready,
    DiscordNotRunning,
    CredentialsMissing,
    TargetGuildMissing,
    Failed,
}

public sealed record DiscordMainChannelOption(string ChannelId, string Name)
{
    public string DisplayText => $"#{Name.TrimStart('#')}";
}

public sealed record DiscordServerDiscoverySnapshot(
    DiscordServerDiscoveryState State,
    string? GuildName,
    string? SalesChannelName,
    IReadOnlyList<DiscordMainChannelOption> MainChannels,
    long RequestRevision,
    bool IsStale = false)
{
    public static DiscordServerDiscoverySnapshot Unavailable(
        DiscordServerDiscoveryState state,
        long revision = 0) =>
        new(state, null, null, Array.Empty<DiscordMainChannelOption>(), revision);
}

public enum MainChannelSwitchStatus
{
    Succeeded,
    NoChange,
    NotConnected,
    InvalidChannel,
    Superseded,
    Failed,
    PersistenceFailed,
}

public sealed record MainChannelSwitchResult(
    MainChannelSwitchStatus Status,
    string? ChannelId = null,
    string? ChannelName = null)
{
    public bool IsSuccess => Status is MainChannelSwitchStatus.Succeeded or MainChannelSwitchStatus.NoChange;
}

namespace GachaOverlay.Core.Discord.Connection;

public enum DiscordConnectionState
{
    Disconnected,
    Connecting,
    Authenticating,
    Connected,
    Reconnecting,
    ConfigurationRequired,
    Faulted,
}

public sealed record DiscordConnectionStatus(
    DiscordConnectionState State,
    long Generation,
    string Detail,
    DateTimeOffset ChangedAt)
{
    public static DiscordConnectionStatus Initial { get; } = new(
        DiscordConnectionState.Disconnected,
        0,
        "NotStarted",
        DateTimeOffset.UtcNow);
}

namespace GachaOverlay.App.Services;

internal static class RemoteSalesStatusNames
{
    public const string Disabled = "Disabled";
    public const string Connecting = "Connecting";
    public const string Bootstrapping = "Bootstrapping";
    public const string Reconnecting = "Reconnecting";
    public const string CredentialUnavailable = "CredentialUnavailable";
}

internal enum RemoteChatHealthState
{
    PairingRequired,
    PairingInProgress,
    Authenticating,
    Connecting,
    Bootstrapping,
    ChannelSelectionRequired,
    Live,
    Reconnecting,
    AuthorizationUnavailable,
    AccessRevoked,
    Disconnected,
    Error,
}

internal sealed record RemoteChannelOption(
    string ChannelId,
    string Name,
    string GuildId,
    int Position,
    bool IsAnnouncement)
{
    public string DisplayName => Name.StartsWith('#') ? Name : $"#{Name}";
}

internal sealed record RemoteChatSnapshot(
    string BackendBaseUrl,
    RemoteChatHealthState Health,
    string Detail,
    bool HasProtectedCredential,
    string? PairingCode,
    DateTimeOffset? PairingExpiresAt,
    IReadOnlyList<RemoteChannelOption> Channels,
    string? SelectedChannelId)
{
    public string RemoteSalesStatus { get; init; } = RemoteSalesStatusNames.Disabled;

    public static RemoteChatSnapshot Disconnected(string backendBaseUrl) => new(
        backendBaseUrl,
        RemoteChatHealthState.Disconnected,
        "NotStarted",
        false,
        null,
        null,
        Array.Empty<RemoteChannelOption>(),
        null);
}

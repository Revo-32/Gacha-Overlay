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
    LoginRequired,
    LoginInProgress,
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
    DateTimeOffset? WebAuthExpiresAt,
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
        Array.Empty<RemoteChannelOption>(),
        null);
}

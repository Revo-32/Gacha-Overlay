namespace GachaOverlay.App.Services;

internal enum RemoteCredentialStatus
{
    Missing,
    Available,
    Unreadable,
}

internal interface IRemoteAccessCredentialStore
{
    RemoteCredentialStatus Status { get; }

    bool TryLoad(out string? accessToken);

    bool Save(string accessToken);

    bool Clear();
}

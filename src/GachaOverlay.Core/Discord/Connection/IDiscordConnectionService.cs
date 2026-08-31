using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Core.Discord.Connection;

public interface IDiscordConnectionService : IAsyncDisposable
{
    event Action<DiscordConnectionStatus>? StatusChanged;

    event Action<DiscordMessageState>? MessageStateChanged;

    event Action<DiscordTargetChannels>? TargetChannelsResolved;

    event Action<DiscordAuthenticatedUser>? AuthenticatedUserChanged;

    DiscordConnectionStatus Status { get; }

    DiscordMessageState MessageState { get; }

    DiscordAuthenticatedUser? AuthenticatedUser { get; }

    void Start(CancellationToken applicationStopping);

    void RequestReconnect();

    Task<MainChannelSwitchResult> SwitchMainChannelAsync(
        DiscordMainChannelOption channel,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new MainChannelSwitchResult(MainChannelSwitchStatus.NotConnected));
}

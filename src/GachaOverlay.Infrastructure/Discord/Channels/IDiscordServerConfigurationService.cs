using GachaOverlay.Core.Discord.Connection;

namespace GachaOverlay.Infrastructure.Discord.Channels;

public interface IDiscordServerConfigurationService
{
    Task<DiscordServerDiscoverySnapshot> DiscoverAsync(
        bool forceRefresh,
        CancellationToken cancellationToken = default);

    Task<bool> ValidateMainChannelAsync(
        DiscordMainChannelOption channel,
        CancellationToken cancellationToken = default);

    void Invalidate();
}

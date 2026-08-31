using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Infrastructure.Discord.Channels;

public interface IDiscordChannelResolver
{
    Task<DiscordTargetChannels> ResolveAsync(
        IDiscordRpcClient rpcClient,
        DiscordTargetOptions options,
        CancellationToken cancellationToken);
}

public sealed class DiscordChannelResolutionException : Exception
{
    public DiscordChannelResolutionException(string message)
        : base(message)
    {
    }
}

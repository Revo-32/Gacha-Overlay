using GachaOverlay.Core.Logging;

namespace GachaOverlay.Infrastructure.Discord.Rpc;

public sealed class DiscordRpcClientFactory : IDiscordRpcClientFactory
{
    private readonly IAppLogger _logger;

    public DiscordRpcClientFactory(IAppLogger logger)
    {
        _logger = logger;
    }

    public IDiscordRpcClient Create() =>
        new DiscordRpcClient(new DiscordRpcTransport(), _logger);
}

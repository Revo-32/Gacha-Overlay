using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Infrastructure.Discord.Authentication;

public sealed record DiscordAuthenticationResult(
    string UserId,
    string Username);

public interface IDiscordAuthenticationService
{
    Task<DiscordAuthenticationResult> AuthenticateAsync(
        IDiscordRpcClient rpcClient,
        DiscordCredentials credentials,
        CancellationToken cancellationToken);
}

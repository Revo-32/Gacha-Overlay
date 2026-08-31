using System.Text.Json;

namespace GachaOverlay.Infrastructure.Discord.Rpc;

public interface IDiscordRpcClient : IAsyncDisposable
{
    event Action<JsonElement>? DispatchReceived;

    Task<string> ConnectAsync(CancellationToken cancellationToken);

    Task<JsonElement> HandshakeAsync(string clientId, CancellationToken cancellationToken);

    Task<JsonElement> CommandAsync(
        string command,
        object arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SubscribeAsync(
        string eventName,
        object arguments,
        CancellationToken cancellationToken = default);

    Task<JsonElement> UnsubscribeAsync(
        string eventName,
        object arguments,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This Discord RPC client does not support unsubscribe.");

    Task<Exception?> WaitForDisconnectAsync(CancellationToken cancellationToken);
}

public interface IDiscordRpcClientFactory
{
    IDiscordRpcClient Create();
}

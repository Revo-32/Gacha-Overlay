namespace GachaOverlay.Infrastructure.Discord.Rpc;

public interface IDiscordRpcTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    Task<string> ConnectAsync(CancellationToken cancellationToken);

    Task<DiscordRpcPacket> ReadAsync(CancellationToken cancellationToken);

    Task WriteAsync(
        int opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);
}

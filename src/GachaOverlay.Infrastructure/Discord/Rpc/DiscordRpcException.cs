namespace GachaOverlay.Infrastructure.Discord.Rpc;

public sealed class DiscordRpcException : Exception
{
    public DiscordRpcException(string code, string rpcMessage)
        : base($"Discord RPC error {code}: {rpcMessage}")
    {
        Code = code;
        RpcMessage = rpcMessage;
    }

    public string Code { get; }

    public string RpcMessage { get; }
}

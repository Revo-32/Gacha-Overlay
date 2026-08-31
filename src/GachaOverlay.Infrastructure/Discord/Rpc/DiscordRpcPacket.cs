namespace GachaOverlay.Infrastructure.Discord.Rpc;

public readonly record struct DiscordRpcPacket(int Opcode, byte[] Payload);

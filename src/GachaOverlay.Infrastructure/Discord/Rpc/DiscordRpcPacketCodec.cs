using System.Buffers.Binary;

namespace GachaOverlay.Infrastructure.Discord.Rpc;

public static class DiscordRpcPacketCodec
{
    public const int HeaderSize = 8;
    public const int MaxPayloadSize = 16 * 1024 * 1024;

    public static async Task<DiscordRpcPacket> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderSize];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);

        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

        if (length is < 0 or > MaxPayloadSize)
        {
            throw new InvalidDataException($"Invalid Discord RPC payload size: {length}.");
        }

        var payload = new byte[length];
        if (length > 0)
        {
            await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }

        return new DiscordRpcPacket(opcode, payload);
    }

    public static async Task WriteAsync(
        Stream stream,
        int opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (payload.Length > MaxPayloadSize)
        {
            throw new InvalidDataException($"Discord RPC payload is too large: {payload.Length}.");
        }

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The Discord IPC pipe closed mid-frame.");
            }

            offset += read;
        }
    }
}

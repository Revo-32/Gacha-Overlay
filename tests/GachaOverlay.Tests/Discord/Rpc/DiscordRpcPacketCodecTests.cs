using System.Buffers.Binary;
using System.Text;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Tests.Discord.Rpc;

public sealed class DiscordRpcPacketCodecTests
{
    [Fact]
    public async Task WriteAndRead_RoundTripsLittleEndianFrame()
    {
        var payload = Encoding.UTF8.GetBytes("{\"evt\":\"READY\"}");
        await using var stream = new MemoryStream();

        await DiscordRpcPacketCodec.WriteAsync(stream, 1, payload, CancellationToken.None);
        stream.Position = 0;
        var packet = await DiscordRpcPacketCodec.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(1, packet.Opcode);
        Assert.Equal(payload, packet.Payload);
    }

    [Fact]
    public async Task Read_HandlesPartialHeaderAndPayloadReads()
    {
        var payload = Encoding.UTF8.GetBytes("partial-read-payload");
        await using var source = new MemoryStream();
        await DiscordRpcPacketCodec.WriteAsync(source, 3, payload, CancellationToken.None);
        await using var chunked = new ChunkedReadStream(source.ToArray(), maxChunkSize: 2);

        var packet = await DiscordRpcPacketCodec.ReadAsync(chunked, CancellationToken.None);

        Assert.Equal(3, packet.Opcode);
        Assert.Equal(payload, packet.Payload);
    }

    [Fact]
    public async Task Read_RejectsOversizedPayloadBeforeAllocation()
    {
        var header = new byte[DiscordRpcPacketCodec.HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(4, 4),
            DiscordRpcPacketCodec.MaxPayloadSize + 1);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => DiscordRpcPacketCodec.ReadAsync(stream, CancellationToken.None));
    }

    private sealed class ChunkedReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _maxChunkSize;

        public ChunkedReadStream(byte[] data, int maxChunkSize)
        {
            _inner = new MemoryStream(data);
            _maxChunkSize = maxChunkSize;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, _maxChunkSize));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, _maxChunkSize)], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

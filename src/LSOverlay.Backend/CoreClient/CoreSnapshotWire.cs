using System.Security.Cryptography;
using System.Text.Json;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.CoreClient;

/// <summary>Pure Core wire prototype, not registered by the Production API.</summary>
public static class CoreSnapshotWire
{
    public static void ValidateHello(CoreSessionStart hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        OverlayProtocolJson.EnsureVersion(hello.ProtocolVersion);
        if (hello.Type != CoreClientProtocol.SessionStart || hello.Capabilities is null ||
            hello.Capabilities.Count != 1 || hello.Capabilities[0] != CoreClientProtocol.Capability ||
            hello.AfterRevision < 0 || (hello.Generation is null) != (hello.AfterRevision is null) ||
            hello.Generation is { Length: < 1 or > 64 })
            throw new InvalidDataException("Core capability or resume request is invalid.");
    }

    public static IReadOnlyList<byte[]> Encode(CoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        OverlayProtocolJson.EnsureVersion(snapshot.ProtocolVersion);
        if (snapshot.Generation.Length is < 1 or > 64 || snapshot.Revision < 0)
            throw new InvalidDataException("Invalid Core snapshot identity.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, OverlayProtocolJson.Options);
        if (payload.Length > CoreClientProtocol.MaximumSnapshotBytes)
            throw new InvalidDataException("Core snapshot exceeds its explicit bounded assembly budget.");
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var count = (payload.Length + CoreClientProtocol.ChunkPayloadBytes - 1) / CoreClientProtocol.ChunkPayloadBytes;
        if (count is < 1 or > CoreClientProtocol.MaximumChunks) throw new InvalidDataException("Invalid Core chunk count.");
        var id = Guid.NewGuid().ToString("N");
        var frames = new byte[count][];
        for (var index = 0; index < count; index++)
        {
            var offset = index * CoreClientProtocol.ChunkPayloadBytes;
            var length = Math.Min(CoreClientProtocol.ChunkPayloadBytes, payload.Length - offset);
            frames[index] = JsonSerializer.SerializeToUtf8Bytes(new CoreSnapshotChunk(
                OverlayTransportProtocol.Version, CoreClientProtocol.SnapshotChunk, id,
                snapshot.Generation, snapshot.Revision, index, count, payload.Length,
                digest, Convert.ToBase64String(payload, offset, length)), OverlayProtocolJson.Options);
            if (frames[index].Length > OverlayTransportProtocol.MaximumInboundWebSocketBytes)
                throw new InvalidDataException("Core frame exceeds the unchanged 16 KiB transport boundary.");
        }
        return frames;
    }

    public static bool CanResume(CoreSessionStart hello, CoreSnapshot snapshot)
    {
        ValidateHello(hello);
        return hello.Generation == snapshot.Generation && hello.AfterRevision == snapshot.Revision;
    }
}

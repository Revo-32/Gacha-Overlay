using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace LSOverlay.CoreMedia;

public sealed record MediaProfile(int Width, int Height)
{
    public const string Pipeline = "core-png-sequence-srgb-v1";
    public void Validate()
    {
        if (Width is < 1 or > 8192 || Height is < 1 or > 8192 || (long)Width * Height > 16_777_216)
            throw new InvalidDataException("Invalid physical media profile.");
    }
    public string Key(string sourceHash)
    {
        Validate();
        if (sourceHash.Length != 64 || sourceHash.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid source digest.");
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"{sourceHash.ToLowerInvariant()}|{Width}|{Height}|{Pipeline}"))).ToLowerInvariant();
    }
}

public sealed record MediaFrame(int DurationMs, long Offset, int Length, byte[] Hash);
public sealed record MediaPackage(int Width, int Height, int TotalPlays, IReadOnlyList<MediaFrame> Frames)
{
    public const int MaximumFrames = 10_000;
    public const long MaximumBytes = 512L * 1024 * 1024;
    public const int HeaderBytes = 24, IndexBytes = 48;
    public static ReadOnlySpan<byte> Magic => "LSCMED1\0"u8;

    public static MediaPackage Read(Stream stream)
    {
        if (!stream.CanSeek || stream.Length is < HeaderBytes or > MaximumBytes) throw new InvalidDataException("Invalid media package size.");
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Invalid media package signature.");
        int width = reader.ReadInt32(), height = reader.ReadInt32(), count = reader.ReadInt32(), plays = reader.ReadInt32();
        new MediaProfile(width, height).Validate();
        if (count is < 1 or > MaximumFrames || plays < 0) throw new InvalidDataException("Invalid media timeline.");
        long next = HeaderBytes + (long)count * IndexBytes, duration = 0;
        var frames = new MediaFrame[count];
        for (var i = 0; i < count; i++)
        {
            var delay = reader.ReadInt32(); var offset = reader.ReadInt64(); var length = reader.ReadInt32(); var hash = reader.ReadBytes(32);
            if (delay is < 0 or > 86_400_000 || offset != next || length is < 8 or > 64 * 1024 * 1024 || hash.Length != 32 || offset > stream.Length - length)
                throw new InvalidDataException("Invalid media frame index.");
            frames[i] = new(delay, offset, length, hash); next += length; duration += delay;
        }
        if (next != stream.Length || (count > 1 && duration == 0)) throw new InvalidDataException("Invalid media duration/trailing data.");
        return new(width, height, plays, frames);
    }

    public byte[] ReadFrame(Stream source, int index)
    {
        var frame = Frames[index]; source.Position = frame.Offset;
        var bytes = new byte[frame.Length]; source.ReadExactly(bytes);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), frame.Hash)) throw new InvalidDataException("Corrupt media frame.");
        return bytes;
    }
}

// Invoke in the isolated worker process, not the request/Chat thread. A parent
// deadline/container memory/CPU limit can terminate a hung native decoder.
public static class MediaConverter
{
    public const long MaximumSourceBytes = 64L * 1024 * 1024;
    public static MediaPackage Convert(string sourcePath, Stream destination, MediaProfile profile, CancellationToken cancellationToken)
    {
        profile.Validate();
        if (!destination.CanSeek || !destination.CanWrite || destination.Length != 0) throw new InvalidDataException("Fresh seekable output required.");
        var sourceLength = new FileInfo(sourcePath).Length;
        if (sourceLength is < 1 or > MaximumSourceBytes) throw new InvalidDataException("Source byte limit exceeded.");
        using var codec = SKCodec.Create(sourcePath) ?? throw new InvalidDataException("Unsupported image.");
        var info = codec.Info;
        if (info.Width is < 1 or > 16384 || info.Height is < 1 or > 16384 || (long)info.Width * info.Height > 67_108_864)
            throw new InvalidDataException("Source pixel limit exceeded.");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp))
            throw new InvalidDataException("Unsupported media codec.");
        var count = Math.Max(1, codec.FrameCount);
        if (count > MediaPackage.MaximumFrames) throw new InvalidDataException("Source frame limit exceeded.");
        var timeline = codec.FrameInfo;
        if (count > 1 && timeline.Length != count) throw new InvalidDataException("Incomplete animation index.");
        var rotated = (int)codec.EncodedOrigin >= 5;
        var logicalWidth = rotated ? info.Height : info.Width;
        var logicalHeight = rotated ? info.Width : info.Height;
        var scale = Math.Min(1.0, Math.Min((double)profile.Width / logicalWidth, (double)profile.Height / logicalHeight));
        var width = Math.Max(1, (int)Math.Round(logicalWidth * scale));
        var height = Math.Max(1, (int)Math.Round(logicalHeight * scale));
        var plays = count == 1 ? 1 : codec.RepetitionCount == -1 ? 0 : checked(codec.RepetitionCount + 1);
        using var color = SKColorSpace.CreateSrgb();
        using var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul, color));
        using var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, color));
        using var canvas = new SKCanvas(output);
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(MediaPackage.Magic); writer.Write(width); writer.Write(height); writer.Write(count); writer.Write(plays);
        // Index reservation is charged to the same bounded output stream.
        writer.Write(new byte[checked(count * MediaPackage.IndexBytes)]);
        var frames = new MediaFrame[count];
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Correctness-first server composition. Unlike the old client, this
            // dependency work occurs once per derivative, never every loop/client.
            var result = codec.GetPixels(decoded.Info, decoded.GetPixels(), new SKCodecOptions(index) { PriorFrame = -1 });
            if (result != SKCodecResult.Success) throw new InvalidDataException("Incomplete or malformed image frame.");
            canvas.Clear(SKColors.Transparent); canvas.ResetMatrix();
            canvas.Scale((float)width / logicalWidth, (float)height / logicalHeight);
            canvas.Concat(Orientation(codec.EncodedOrigin, info.Width, info.Height));
            using var image = SKImage.FromBitmap(decoded);
            var sampling = width == logicalWidth && height == logicalHeight
                ? new SKSamplingOptions(SKFilterMode.Nearest) // identity/orientation must not blur existing pixels
                : new SKSamplingOptions(SKCubicResampler.Mitchell);
            canvas.DrawImage(image, 0, 0, sampling);
            canvas.Flush();
            using var encodedImage = SKImage.FromBitmap(output);
            using var png = encodedImage.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("PNG encode failed.");
            if (png.Size > 64 * 1024 * 1024 || destination.Position > MediaPackage.MaximumBytes - png.Size)
                throw new InvalidDataException("Derivative byte budget exceeded; quality was not reduced.");
            var delay = count == 1 ? 0 : timeline[index].Duration;
            if (delay is < 0 or > 86_400_000) throw new InvalidDataException("Invalid source frame duration.");
            var bytes = png.ToArray();
            frames[index] = new(delay, destination.Position, bytes.Length, SHA256.HashData(bytes));
            writer.Write(bytes);
        }
        if (count > 1 && frames.Sum(frame => (long)frame.DurationMs) == 0) throw new InvalidDataException("Animation has no duration.");
        destination.Position = MediaPackage.HeaderBytes;
        foreach (var frame in frames) { writer.Write(frame.DurationMs); writer.Write(frame.Offset); writer.Write(frame.Length); writer.Write(frame.Hash); }
        destination.Position = destination.Length; destination.Flush();
        return new(width, height, plays, frames);
    }

    private static SKMatrix Orientation(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity,
    };
}

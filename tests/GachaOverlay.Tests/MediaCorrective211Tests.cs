using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;
using SkiaSharp;

namespace GachaOverlay.Tests;

public sealed class MediaCorrective211Tests
{
    [Fact]
    public void TransparentGif_RestorePreviousAndBackground_SurviveRandomSeekAndReuse()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            using var stream = new MemoryStream();
            stream.Write("GIF89a"u8);
            stream.Write([2, 0, 2, 0, 0x80, 0, 0, 0, 0, 0, 255, 255, 255]);
            foreach (var (x, y, disposal) in new[] { (0, 0, 1), (1, 0, 3), (0, 1, 2), (1, 1, 1) })
            {
                stream.Write([0x21, 0xf9, 4, (byte)(disposal * 4 + 1), 10, 0, 0, 0]);
                stream.Write([0x2c, (byte)x, 0, (byte)y, 0, 1, 0, 1, 0, 0, 2, 2, 0x4c, 1, 0]);
            }
            stream.WriteByte(0x3b);
            using var decoder = new DiscordMediaAssetService.FrameDecoder(stream.ToArray(), 2);
            foreach (var frame in new[] { 0, 1, 2, 3, 0, 3, 1, 2 })
            {
                var pixels = Pixels(decoder.Decode(frame).Image);
                Assert.Equal(255, pixels[3]); // First persistent white pixel.
                Assert.Equal(frame == 1 ? 255 : 0, pixels[7]); // RestorePrevious clears (1,0).
                Assert.Equal(frame == 2 ? 255 : 0, pixels[11]); // RestoreBackground clears (0,1).
                Assert.Equal(frame == 3 ? 255 : 0, pixels[15]);
            }
        });
    }

    [Fact]
    public void SyntheticAnimatedWebP_PreservesColorsAlphaAndLoopSeek()
    {
        using var content = new MemoryStream();
        using var writer = new BinaryWriter(content);
        writer.Write("WEBP"u8);
        Chunk(writer, "VP8X", [0x12, 0, 0, 0, 1, 0, 0, 1, 0, 0]);
        Chunk(writer, "ANIM", [0, 0, 0, 0, 0, 0]);
        var references = new List<byte[]>();
        foreach (var color in new[] { new SKColor(200, 50, 100, 128), new SKColor(20, 200, 30, 255) })
        {
            using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(color);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, 100);
            var bytes = encoded.ToArray();
            references.Add(Pixels(DiscordMediaAssetService.DecodeSkiaFrame(bytes, 2, 0).Image));
            using var frame = new MemoryStream();
            frame.Write([0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 100, 0, 0, 2]);
            for (var offset = 12; offset < bytes.Length;)
            {
                var size = BitConverter.ToInt32(bytes, offset + 4);
                var padded = size + (size & 1) + 8;
                var kind = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
                if (kind is "ALPH" or "VP8 " or "VP8L") frame.Write(bytes, offset, padded);
                offset += padded;
            }
            Chunk(writer, "ANMF", frame.ToArray());
        }
        using var file = new MemoryStream();
        using var fileWriter = new BinaryWriter(file);
        fileWriter.Write("RIFF"u8);
        fileWriter.Write((int)content.Length);
        fileWriter.Write(content.ToArray());
        using var decoder = new DiscordMediaAssetService.FrameDecoder(file.ToArray(), 2);
        foreach (var index in new[] { 0, 1, 0, 1, 1, 0 })
        {
            var decoded = decoder.Decode(index);
            Assert.Equal(2, decoded.FrameCount);
            Assert.Equal(TimeSpan.FromMilliseconds(100), decoded.Duration);
            Assert.Equal(references[index], Pixels(decoded.Image));
        }
    }

    private static void Chunk(BinaryWriter writer, string name, byte[] data)
    {
        writer.Write(System.Text.Encoding.ASCII.GetBytes(name));
        writer.Write(data.Length);
        writer.Write(data);
        if ((data.Length & 1) != 0) writer.Write((byte)0);
    }

    [Fact]
    public void ReusedDecoder_SeeksLoopsAndFrozenFramesOwnTheirPixels()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var bytes = MediaLatencyProfile211Tests.Fixture(96, 10);
            using var decoder = new DiscordMediaAssetService.FrameDecoder(bytes, 384);
            var first = decoder.Decode(0).Image;
            var saved = Pixels(first);
            Assert.Equal(96, first.PixelWidth); // Never upscale small source media.
            foreach (var index in new[] { 1, 8, 4, 11, 0, 5, 0 })
            {
                var image = decoder.Decode(index).Image;
                using var fresh = new DiscordMediaAssetService.FrameDecoder(bytes, 384);
                Assert.Equal(Pixels(fresh.Decode(index).Image), Pixels(image));
                Assert.Equal(saved, Pixels(first));
                Assert.True(image.IsFrozen);
            }
            Assert.Equal(0, decoder.SelectFrame(TimeSpan.Zero).Frame);
            Assert.Equal(0, decoder.SelectFrame(TimeSpan.FromMilliseconds(99)).Frame);
            Assert.Equal(1, decoder.SelectFrame(TimeSpan.FromMilliseconds(100)).Frame);
            Assert.Equal(0, decoder.SelectFrame(TimeSpan.FromMilliseconds(1200)).Frame);
            Assert.Equal(35, decoder.SelectFrame(TimeSpan.FromMilliseconds(3599)).Ordinal);
        });
    }

    [Fact]
    public void LargeGif_UsesExisting384PixelTarget_WithoutFrameArrayAllocation()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            using var decoder = new DiscordMediaAssetService.FrameDecoder(MediaLatencyProfile211Tests.Fixture(768, 10), 384);
            Assert.Equal(384 * 384 * 4, decoder.BufferBytes);
            decoder.Decode(0);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 24; i++) Assert.Equal(384, decoder.Decode(i % 12).Image.PixelWidth);
            // Old BGRA arrays alone would be 14 MiB; allow plenty of WPF wrapper overhead.
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 2 * 1024 * 1024);
        });
    }

    [Fact]
    public void PremultipliedAlpha_IsPreservedInSharedSkiaPath()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(200, 100, 50, 128));
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var decoded = DiscordMediaAssetService.DecodeSkiaFrame(png.ToArray(), 64, 0).Image;
        Assert.Equal(PixelFormats.Pbgra32, decoded.Format);
        var pixels = Pixels(decoded);
        Assert.InRange(pixels[0], 24, 26);
        Assert.InRange(pixels[1], 49, 51);
        Assert.InRange(pixels[2], 99, 101);
        Assert.Equal(128, pixels[3]);
    }

    [Fact]
    public void BusyDispatcher_DoesNotQueueMultipleFrames_AndCancelledPlayersReleaseResources()
    {
        MediaLatencyProfile211Tests.RunSta(() =>
        {
            var bytes = MediaLatencyProfile211Tests.Fixture(96, 2);
            var metrics = new RuntimeMetricsCollector();
            using var scheduler = new MediaAnimationScheduler(Dispatcher.CurrentDispatcher, metrics, NullAppLogger.Instance);
            var presented = 0;
            using var registration = scheduler.Register(bytes, 96, _ => presented++);
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(200));
            Assert.True(presented > 0);
            var before = metrics.Snapshot().Counters.GetValueOrDefault(RuntimeMetricNames.MediaAnimationFrameDecoded);
            // Block UI without pumping: workers can finish at most one already-scheduled decode.
            Thread.Sleep(150);
            var after = metrics.Snapshot().Counters.GetValueOrDefault(RuntimeMetricNames.MediaAnimationFrameDecoded);
            Assert.InRange(after - before, 0, 1);
            registration.Dispose();
            var callbacksAtDispose = presented;
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal(callbacksAtDispose, presented);
            Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationDecoderCount));
            Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault("media.animation.working_bytes"));
            var ticks = metrics.Snapshot().Counters.GetValueOrDefault("media.animation.scheduler_tick.count");
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(ticks, metrics.Snapshot().Counters.GetValueOrDefault("media.animation.scheduler_tick.count"));
            Assert.Equal(1, metrics.Snapshot().Counters.GetValueOrDefault("media.animation.decoder_created.count"));
        });
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }
}

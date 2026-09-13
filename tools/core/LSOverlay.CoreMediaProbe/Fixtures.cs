using System.Text;
using SkiaSharp;

namespace LSOverlay.CoreMediaProbe;

internal static class Fixtures
{
    public static byte[] Png(int seed = 0, int width = 9, int height = 5)
    {
        using var color = SKColorSpace.CreateSrgb();
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, color));
        var random = new Random(seed);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) bitmap.SetPixel(x, y, new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 128));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Tiny, generated, public-safe GIF: variable timing, restore previous,
    // restore background, transparency and finite/infinite loop controls.
    public static byte[] Gif(ushort loops = 0, ushort scale = 1)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("GIF89a"u8); writer.Write((ushort)(8 * scale)); writer.Write((ushort)(8 * scale)); writer.Write((byte)0x81); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 });
        writer.Write(new byte[] { 0x21, 0xff, 11 }); writer.Write("NETSCAPE2.0"u8); writer.Write(new byte[] { 3, 1 }); writer.Write(loops); writer.Write((byte)0);
        void Scaled(ushort x, ushort y, ushort w, ushort h, int color, ushort delay, int disposal) => Frame(writer, (ushort)(x * scale), (ushort)(y * scale), (ushort)(w * scale), (ushort)(h * scale), color, delay, disposal);
        Scaled(0, 0, 8, 8, 1, 7, 1);
        Scaled(2, 2, 2, 2, 2, 13, 3);
        Scaled(4, 4, 2, 2, 3, 9, 2);
        Scaled(0, 0, 2, 2, 2, 11, 1);
        Scaled(7, 7, 1, 1, 0, 5, 1);
        writer.Write((byte)0x3b); return stream.ToArray();
    }
    private static void Frame(BinaryWriter writer, ushort x, ushort y, ushort width, ushort height, int pixel, ushort delay, int disposal)
    {
        writer.Write(new byte[] { 0x21, 0xf9, 4, (byte)((disposal << 2) | 1) }); writer.Write(delay); writer.Write(new byte[] { 0, 0, 0x2c });
        writer.Write(x); writer.Write(y); writer.Write(width); writer.Write(height); writer.Write((byte)0); writer.Write((byte)2);
        var bytes = new List<byte>(); int accumulator = 0, bits = 0;
        void Code(int code) { accumulator |= code << bits; bits += 3; while (bits >= 8) { bytes.Add((byte)accumulator); accumulator >>= 8; bits -= 8; } }
        for (var i = 0; i < width * height; i++) { Code(4); Code(pixel); }
        Code(5); if (bits > 0) bytes.Add((byte)accumulator);
        for (var start = 0; start < bytes.Count; start += 255) { var count = Math.Min(255, bytes.Count - start); writer.Write((byte)count); writer.Write(bytes.GetRange(start, count).ToArray()); }
        writer.Write((byte)0);
    }
}

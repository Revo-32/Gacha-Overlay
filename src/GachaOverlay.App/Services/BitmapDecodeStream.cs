using System.IO;

namespace GachaOverlay.App.Services;

// BitmapImage keeps its StreamSource even with OnLoad. Retire the encoded buffer
// after decoding, without changing decoded pixels or keeping the buffer via WPF.
internal sealed class BitmapDecodeStream : Stream
{
    private Stream? _source;

    public BitmapDecodeStream(Stream source) => _source = source;

    private Stream Source => _source ?? throw new ObjectDisposedException(nameof(BitmapDecodeStream));
    public override bool CanRead => _source?.CanRead == true;
    public override bool CanSeek => _source?.CanSeek == true;
    public override bool CanWrite => false;
    public override long Length => Source.Length;
    public override long Position { get => Source.Position; set => Source.Position = value; }
    public override void Flush() => Source.Flush();
    public override int Read(byte[] buffer, int offset, int count) => Source.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => Source.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var source = _source;
            _source = null;
            source?.Dispose();
        }
        base.Dispose(disposing);
    }
}

using System.Diagnostics;
using System.Text.Json;
using LSOverlay.CoreMedia;
using LSOverlay.CoreMediaProbe;
using SkiaSharp;

if (args.Length != 2 || args[0] != "--verify" || !Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Use --verify <new absolute artifact directory>.");
var root = Path.GetFullPath(args[1]);
DerivativeCache.CheckAncestors(root);
if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Output must not already exist.");
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool value, string name) { if (!value) throw new InvalidDataException("FAIL: " + name); checks.Add(name); }
async Task Reject(Func<Task> body, string name)
{
    try { await body(); }
    catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OperationCanceledException or InvalidOperationException) { checks.Add(name); return; }
    throw new InvalidDataException("Did not reject: " + name);
}
var timer = Stopwatch.StartNew();
await NativeFixtures.Export(Path.Combine(root, "native"));
await SecurityTests.Run(root, Check, Reject);
var cacheRoot = Path.Combine(root, "cache");
using (var cache = new DerivativeCache(cacheRoot))
{
    var gif = Fixtures.Gif();
    var key = await cache.GetOrCreateAsync(new MemoryStream(gif), new(80, 80));
    using (var lease = cache.Open(key))
    {
        var media = MediaPackage.Read(lease);
        Check(media.Width == 8 && media.Height == 8, "no upscaling");
        Check(media.TotalPlays == 0, "infinite loop preserved");
        Check(media.Frames.Select(f => f.DurationMs).SequenceEqual(new[] { 70, 130, 90, 110, 50 }), "all frames and variable durations preserved");
        var frames = Enumerable.Range(0, 5).Select(i => SKBitmap.Decode(media.ReadFrame(lease, i))).ToArray();
        try
        {
            Check(frames[0].GetPixel(0, 0) == SKColors.Red, "GIF base composition");
            Check(frames[1].GetPixel(2, 2) == SKColors.Lime && frames[1].GetPixel(0, 0) == SKColors.Red, "GIF partial rect composition");
            Check(frames[2].GetPixel(2, 2) == SKColors.Red && frames[2].GetPixel(4, 4) == SKColors.Blue, "GIF restore previous");
            Check(frames[3].GetPixel(4, 4).Alpha == 0 && frames[3].GetPixel(0, 0) == SKColors.Lime, "GIF restore background alpha");
            Check(frames[4].GetPixel(7, 7) == SKColors.Red && frames[4].GetPixel(4, 4).Alpha == 0, "GIF transparent patch retains composed surface");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
        await Reject(() => { cache.Dispose(); return Task.CompletedTask; }, "leased cache cannot be disposed");
    }
    File.Copy(Path.Combine(cacheRoot, key + ".lscm"), Path.Combine(root, "animation.lscm"));
    var again = await cache.GetOrCreateAsync(new MemoryStream(gif), new(80, 80));
    Check(again == key && cache.Hits == 1 && cache.Conversions == 1, "identical source reuses derivative");
    var keys = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => cache.GetOrCreateAsync(new MemoryStream(gif), new(80, 80))));
    Check(keys.All(k => k == key) && cache.Conversions == 1, "concurrent same-media work coalesced");
    var finite = await cache.GetOrCreateAsync(new MemoryStream(Fixtures.Gif(2)), new(8, 8));
    using (var lease = cache.Open(finite)) Check(MediaPackage.Read(lease).TotalPlays == 3, "finite loop count preserved");
    var png = Fixtures.Png();
    var pngKey = await cache.GetOrCreateAsync(new MemoryStream(png), new(9, 5));
    using (var lease = cache.Open(pngKey))
    {
        var package = MediaPackage.Read(lease); using var before = SKBitmap.Decode(png); using var after = SKBitmap.Decode(package.ReadFrame(lease, 0));
        Check(package.Frames.Count == 1 && package.TotalPlays == 1, "static frame policy");
        Check(before.Bytes.SequenceEqual(after.Bytes), "static sRGB pixels and alpha preserved");
    }
    File.Copy(Path.Combine(cacheRoot, pngKey + ".lscm"), Path.Combine(root, "static.lscm"));
    var small = await cache.GetOrCreateAsync(new MemoryStream(png), new(3, 3));
    using (var lease = cache.Open(small)) { var media = MediaPackage.Read(lease); Check(media.Width == 3 && media.Height == 2, "physical profile preserves aspect ratio within pixel rounding"); }
    Check(small != pngKey, "profile changes cache identity");
    var corruptPath = Path.Combine(cacheRoot, key + ".lscm");
    using (var file = new FileStream(corruptPath, FileMode.Open, FileAccess.ReadWrite)) { file.Position = file.Length - 1; file.WriteByte(0); }
    var rebuilt = await cache.GetOrCreateAsync(new MemoryStream(gif), new(80, 80));
    Check(rebuilt == key && cache.Conversions == 5, "corrupt derivative rebuilt");
    await Reject(() => cache.GetOrCreateAsync(new MemoryStream(new byte[] { 1, 2, 3 }), new(16, 16)), "malformed image rejected");
    await Reject(() => cache.GetOrCreateAsync(new MemoryStream(gif[..40]), new(16, 16)), "truncated image rejected");
    await Reject(() => cache.GetOrCreateAsync(new MemoryStream(png), new(0, 16)), "invalid profile rejected");
    await Reject(() => cache.GetOrCreateAsync(new MemoryStream(png), new(8192, 8192)), "profile pixel bomb rejected");
    await Reject(() => { cache.Open("../other"); return Task.CompletedTask; }, "cache traversal rejected");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Reject(() => cache.GetOrCreateAsync(new MemoryStream(gif), new(16, 16), canceled.Token), "cancellation rejected");
    Check(!Directory.EnumerateFiles(cacheRoot).Any(p => p.EndsWith(".pending") || p.EndsWith(".source")), "temporary originals and pending outputs cleaned");
}
using (var reopened = new DerivativeCache(cacheRoot))
{
    await reopened.GetOrCreateAsync(new MemoryStream(Fixtures.Gif()), new(80, 80));
    Check(reopened.Hits == 1 && reopened.Conversions == 0, "worker restart retains disposable derivative");
    await Reject(() => { using var other = new DerivativeCache(cacheRoot); return Task.CompletedTask; }, "second cache owner rejected");
}
using (var cache = new DerivativeCache(Path.Combine(root, "queue")))
{
    using var cancel = new CancellationTokenSource();
    var blocker = new BlockingStream();
    var jobs = Enumerable.Range(0, 4).Select(_ => cache.GetOrCreateAsync(blocker, new(16, 16), cancel.Token)).ToArray();
    await Reject(() => cache.GetOrCreateAsync(new MemoryStream(Fixtures.Gif()), new(16, 16)), "worker saturation rejected without unbounded queue");
    cancel.Cancel(); foreach (var job in jobs) await Reject(() => job, "queued cancellation drains");
    Check(!Directory.EnumerateFiles(cache.Root).Any(p => p.EndsWith(".source")), "canceled active input cleaned");
}
using (var cache = new DerivativeCache(Path.Combine(root, "eviction"), new(16000, 12000, 8000)))
{
    var key = await cache.GetOrCreateAsync(new MemoryStream(Fixtures.Png(1, 24, 24)), new(24, 24));
    using (var lease = cache.Open(key))
    {
        for (var i = 2; i < 14; i++) await cache.GetOrCreateAsync(new MemoryStream(Fixtures.Png(i, 24, 24)), new(24, 24));
        Check(cache.Evictions > 0 && File.Exists(Path.Combine(cache.Root, key + ".lscm")), "LRU skips active leases");
        Check(Directory.GetFiles(cache.Root).Sum(p => new FileInfo(p).Length) < 16000, "cache hard budget bounded");
        var package = MediaPackage.Read(lease); Check(package.ReadFrame(lease, 0).Length > 0, "leased response survives eviction");
    }
}
using (var cache = new DerivativeCache(Path.Combine(root,"file-budget"),new(MaximumFiles:8)))
{
    for (var i=0; i<12; i++) await cache.GetOrCreateAsync(new MemoryStream(Fixtures.Png(i)),new(9,5));
    Check(cache.Evictions > 0 && Directory.GetFiles(cache.Root).Length <= 8,"file count bounded before cache byte limit");
}
using (var cache = new DerivativeCache(Path.Combine(root,"file-budget"),new(MaximumFiles:8)))
    Check(Directory.GetFiles(cache.Root).Length <= 8,"file-count budget remains valid after restart");
var foreign = Path.Combine(root, "foreign"); Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "keep.txt"), "owned by caller");
await Reject(() => { using var cache = new DerivativeCache(foreign); return Task.CompletedTask; }, "foreign directory rejected");
Check(File.ReadAllText(Path.Combine(foreign, "keep.txt")) == "owned by caller", "foreign data preserved");
var report = new { status = "PASS", assertions = checks.Count, elapsedMs = timer.ElapsedMilliseconds, checks, synthetic = true, realGifComparison = false };
File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", assertions = checks.Count, elapsedMs = timer.ElapsedMilliseconds }));

sealed class BlockingStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { await Task.Delay(Timeout.Infinite, token); return 0; }
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

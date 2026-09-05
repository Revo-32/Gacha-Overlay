using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace GachaOverlay.Tests;

public sealed class MediaLatencyProfile211Tests
{
    [Fact]
    public void LocalReleaseComparison()
    {
        var output = Environment.GetEnvironmentVariable("LSO_MEDIA_PROFILE");
        if (string.IsNullOrEmpty(output)) return;
        RunSta(() =>
        {
            var a = Fixture(384, 10);
            var b = Fixture(768, 10);
            var c = Fixture(384, 2);
            var staticPixels = new byte[1024 * 768 * 4];
            var staticEncoder = new PngBitmapEncoder();
            staticEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1024, 768, 96, 96,
                PixelFormats.Bgra32, null, staticPixels, 4096)));
            using var staticStream = new MemoryStream();
            staticEncoder.Save(staticStream);
            var staticBytes = staticStream.ToArray();
            var results = new List<object>();
            var probes = new List<object>();
            foreach (var fixture in new[] { ("A", a), ("B", b), ("C", c) })
            {
                try
                {
                    var allocated = GC.GetTotalAllocatedBytes();
                    var clock = Stopwatch.StartNew();
                    for (var i = 0; i < 120; i++)
                        _ = DiscordMediaAssetService.DecodeSkiaFrame(fixture.Item2, 384, i % 12);
                    probes.Add(new
                    {
                        fixture = fixture.Item1,
                        milliseconds = clock.Elapsed.TotalMilliseconds,
                        allocated = GC.GetTotalAllocatedBytes() - allocated,
                        failure = (string?)null
                    });
                }
                catch (Exception ex) { probes.Add(new { fixture = fixture.Item1, failure = ex.Message }); }
            }
            var dispatcher = Dispatcher.CurrentDispatcher;
            foreach (var scenario in new[] { ("Idle", 0, false), ("Static1", 0, true), ("GIF1", 1, false),
                         ("GIF1+Static1", 1, true), ("GIF3", 3, false), ("GIF5", 5, false), ("Cleanup", 0, false) })
            {
                var metrics = new RuntimeMetricsCollector();
                using var scheduler = new MediaAnimationScheduler(dispatcher, metrics, NullAppLogger.Instance);
                var images = Enumerable.Range(0, scenario.Item2 + 1).Select(_ => new Image { Width = 256, Height = 256 }).ToArray();
                if (scenario.Item3)
                {
                    images[^1].Source = DiscordMediaAssetService.DecodeImage(new MemoryStream(staticBytes), 384);
                }
                var handles = Enumerable.Range(0, scenario.Item2).Select(i =>
                    scheduler.Register(i % 2 == 0 ? a : c, 384, value => images[i].Source = value)).ToArray();
                Pump(TimeSpan.FromSeconds(1));
                var baseline = metrics.Snapshot();
                var pipeline = new DiscordMessagePipeline();
                var client = new LSOverlayRemoteClient(new Uri("http://127.0.0.1:1")); // Never connected.
                using var adapter = new RemoteChatIngressAdapter(pipeline, client, 1, "7");
                Assert.True(adapter.ApplyBootstrap(new ChatBootstrapResponse(OverlayTransportProtocol.Version,
                    new ChatChannelDescriptor(1, 2, "synthetic", 0, false), "fixture", 0, [])));
                var mutation = typeof(RemoteChatIngressAdapter).GetMethod("OnMutation", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var storeSamples = new List<double>();
                var uiSamples = new List<double>();
                var totalSamples = new List<double>();
                var label = new TextBlock();
                long received = 0;
                var applied = 0;
                pipeline.StateChanged += state =>
                {
                    var stored = Stopwatch.GetTimestamp();
                    var start = received;
                    storeSamples.Add(Stopwatch.GetElapsedTime(start, stored).TotalMilliseconds);
                    dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        label.Text = state.MainChat.Last().Content;
                        uiSamples.Add(Stopwatch.GetElapsedTime(stored).TotalMilliseconds);
                        totalSamples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        applied++;
                    });
                };
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes();
                var clock = Stopwatch.StartNew();
                var sender = Task.Run(async () =>
                {
                    for (ulong i = 1; i <= 100; i++)
                    {
                        var message = new ChatMessage(i, 1, 2, "Default", 0,
                            new ChatAuthor(7, "fixture", "fixture", null, false, false), $"message-{i}",
                            DateTimeOffset.UnixEpoch.AddSeconds(i), null, false, false, false, 0, [], [], [], [], [], [], null, [], null);
                        received = Stopwatch.GetTimestamp();
                        mutation.Invoke(adapter, [new ChatMutationEnvelope(OverlayTransportProtocol.Version, "fixture", (long)i,
                            OverlayTransportProtocol.ChatMessageCreate, 2, i, message)]);
                        await Task.Delay(50);
                    }
                });
                Pump(TimeSpan.FromSeconds(5.5));
                while (!sender.IsCompleted || applied != 100) { Assert.True(clock.Elapsed.TotalSeconds < 15); Pump(TimeSpan.FromMilliseconds(10)); }
                Assert.Null(sender.Exception);
                Assert.Equal(20, pipeline.Current.MainChat.Count);
                Assert.Equal("message-100", label.Text);
                process.Refresh();
                var snapshot = metrics.Snapshot();
                long Count(string name) => snapshot.Counters.GetValueOrDefault(name) - baseline.Counters.GetValueOrDefault(name);
                results.Add(new
                {
                    scenario = scenario.Item1,
                    seconds = clock.Elapsed.TotalSeconds,
                    privateWorkingSet = PrivateWorkingSet(process),
                    privateBytes = process.PrivateMemorySize64,
                    workingSet = process.WorkingSet64,
                    managedHeap = GC.GetTotalMemory(false),
                    allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                    cpuPercent = (process.TotalProcessorTime - cpu).TotalMilliseconds / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
                    process.HandleCount,
                    threads = process.Threads.Count,
                    gdi = GetGuiResources(process.Handle, 0),
                    user = GetGuiResources(process.Handle, 1),
                    decodedFps = Count(RuntimeMetricNames.MediaAnimationFrameDecoded) / clock.Elapsed.TotalSeconds,
                    presentedFps = Count(RuntimeMetricNames.MediaAnimationFramesPresented) / clock.Elapsed.TotalSeconds,
                    skippedFps = Count(RuntimeMetricNames.MediaAnimationFramesSkipped) / clock.Elapsed.TotalSeconds,
                    receiveToStore = Summary(storeSamples),
                    storeToWpfStateProxy = Summary(uiSamples),
                    total = Summary(totalSamples),
                    metrics = snapshot,
                });
                foreach (var handle in handles) handle.Dispose();
                foreach (var image in images) image.Source = null;
                Pump(TimeSpan.FromMilliseconds(200));
                Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationActivePlayers));
                Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationDecoderCount));
                var disposal = client.DisposeAsync().AsTask();
                while (!disposal.IsCompleted) Pump(TimeSpan.FromMilliseconds(10));
                Assert.Null(disposal.Exception);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                boundary = "Release testhost: actual Remote adapter + store; Render-priority TextBlock state proxy, NOT full client/rendered pixels",
                fixtures = "A=384x384/10fps; B=768x768/10fps probe; C=384x384/50fps; 12 frames each; STATIC=1024x768 PNG. GIF3/5 alternate A/C. 256 DIP Image, not attached to a Window.",
                probes,
                results,
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }

    internal static byte[] Fixture(int size, ushort delay)
    {
        var encoder = new GifBitmapEncoder();
        for (var frame = 0; frame < 12; frame++)
        {
            var pixels = new byte[size * size * 3];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)((i / 31 + frame * 19) % 256);
            var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr24, null, pixels, size * 3);
            var metadata = new BitmapMetadata("gif");
            metadata.SetQuery("/grctlext/Delay", delay);
            encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
        }
        using var stream = new MemoryStream();
        encoder.Save(stream);
        // WPF's GIF encoder does not reliably preserve frame delay metadata.
        // Rewrite graphic-control extensions in the generated file, not compressed image data.
        var encoded = stream.ToArray();
        using var timed = new MemoryStream();
        var offset = 13 + ((encoded[10] & 128) != 0 ? 3 * (1 << ((encoded[10] & 7) + 1)) : 0);
        timed.Write(encoded, 0, offset);
        while (offset < encoded.Length)
        {
            var start = offset;
            var marker = encoded[offset++];
            if (marker == 0x3b) { timed.WriteByte(marker); break; }
            if (marker == 0x21)
            {
                var kind = encoded[offset++];
                while (encoded[offset] != 0) offset += 1 + encoded[offset];
                offset++;
                if (kind != 0xf9) timed.Write(encoded, start, offset - start);
            }
            else if (marker == 0x2c)
            {
                timed.Write([0x21, 0xf9, 4, 0, (byte)delay, (byte)(delay >> 8), 0, 0]);
                var packed = encoded[offset + 8];
                offset += 9 + ((packed & 128) != 0 ? 3 * (1 << ((packed & 7) + 1)) : 0);
                offset++; // LZW code size.
                while (encoded[offset] != 0) offset += 1 + encoded[offset];
                offset++;
                timed.Write(encoded, start, offset - start);
            }
            else throw new InvalidDataException("Unexpected synthetic GIF block.");
        }
        var result = timed.ToArray();
        using var data = SkiaSharp.SKData.CreateCopy(result);
        using var codec = SkiaSharp.SKCodec.Create(data);
        Assert.All(codec.FrameInfo, frame => Assert.Equal(delay * 10, frame.Duration));
        return result;
    }

    private static object Summary(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return new { count = sorted.Length, median = sorted[sorted.Length / 2], p95 = sorted[(int)((sorted.Length - 1) * .95)], max = sorted[^1] };
    }

    private static long PrivateWorkingSet(Process process)
    {
        var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        return GetProcessMemoryInfo(process.Handle, ref counters, counters.Size) ? (long)counters.PrivateWorkingSetSize : -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize, SharedCommitUsage;
    }
    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    internal static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    internal static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

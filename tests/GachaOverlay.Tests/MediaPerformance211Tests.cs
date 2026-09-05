using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.Tests;

public sealed class MediaPerformance211Tests
{
    // Opt-in local profiling, not a CI performance assertion. No network or user settings.
    [Fact]
    public void FixedFixtureProfile()
    {
        var output = Environment.GetEnvironmentVariable("LSO_PROFILE_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var bytes = Fixture();
                var results = new List<object>();
                foreach (var scenario in new[] { ("idle", 0, false), ("static", 0, true),
                             ("gif1", 1, false), ("gif1_static", 1, true), ("gif3", 3, false), ("gif5", 5, false) })
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    var metrics = new RuntimeMetricsCollector();
                    using var scheduler = new MediaAnimationScheduler(dispatcher, metrics, NullAppLogger.Instance);
                    var frames = new BitmapSource?[scenario.Item2 + 1];
                    if (scenario.Item3) frames[^1] = DiscordMediaAssetService.DecodeSkiaFrame(bytes, 384, 0).Image;
                    var registrations = Enumerable.Range(0, scenario.Item2)
                        .Select(i => scheduler.Register(bytes, 384, value => frames[i] = value)).ToArray();
                    var uiDelays = new List<double>();
                    using var cancel = new CancellationTokenSource();
                    var probe = Task.Run(async () =>
                    {
                        while (!cancel.IsCancellationRequested)
                        {
                            var queued = Stopwatch.GetTimestamp();
                            await dispatcher.InvokeAsync(() => uiDelays.Add(Stopwatch.GetElapsedTime(queued).TotalMilliseconds),
                                DispatcherPriority.Render);
                            await Task.Delay(20);
                        }
                    });
                    using var process = Process.GetCurrentProcess();
                    var cpu = process.TotalProcessorTime;
                    var allocated = GC.GetTotalAllocatedBytes();
                    var clock = Stopwatch.StartNew();
                    Pump(TimeSpan.FromSeconds(3));
                    cancel.Cancel();
                    while (!probe.IsCompleted) Pump(TimeSpan.FromMilliseconds(10));
                    process.Refresh();
                    var snapshot = metrics.Snapshot();
                    var sorted = uiDelays.OrderBy(x => x).ToArray();
                    results.Add(new
                    {
                        scenario = scenario.Item1,
                        source = "384x384; 12 deterministic GIF frames; same bytes for all players",
                        compressedBytes = bytes.Length,
                        wallSeconds = clock.Elapsed.TotalSeconds,
                        cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                        cpuMachinePercent = (process.TotalProcessorTime - cpu).TotalMilliseconds /
                            clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
                        process.WorkingSet64,
                        process.PrivateMemorySize64,
                        managedHeap = GC.GetTotalMemory(false),
                        allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                        process.HandleCount,
                        threads = process.Threads.Count,
                        uiProxyAverageMs = sorted.Average(),
                        uiProxyP95Ms = sorted[(int)((sorted.Length - 1) * .95)],
                        uiProxyMaxMs = sorted[^1],
                        metrics = snapshot,
                    });
                    foreach (var registration in registrations) registration.Dispose();
                    Pump(TimeSpan.FromMilliseconds(100));
                    Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationActivePlayers));
                    Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationDecoderCount));
                    Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationFrameBuffers));
                    Array.Clear(frames);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static byte[] Fixture()
    {
        var encoder = new GifBitmapEncoder();
        for (var frame = 0; frame < 12; frame++)
        {
            var pixels = new byte[384 * 384 * 3];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)((i / 31 + frame * 19) % 256);
            var image = BitmapSource.Create(384, 384, 96, 96, PixelFormats.Bgr24, null, pixels, 384 * 3);
            image.Freeze();
            encoder.Frames.Add(BitmapFrame.Create(image));
        }
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}

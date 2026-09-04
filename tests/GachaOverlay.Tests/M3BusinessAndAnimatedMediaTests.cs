using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Business;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Timers;
using GachaOverlay.Infrastructure.Settings;
using SkiaSharp;

namespace GachaOverlay.Tests;

public sealed class M3BusinessAndAnimatedMediaTests
{
    [Fact]
    public void MechanicCatalog_DerivesAcceptedReferenceAnchors()
    {
        Assert.Equal(TimeSpan.FromMinutes(140), BusinessMechanicCatalog.BunkerNormalSupply);
        Assert.Equal(TimeSpan.FromMinutes(46) + TimeSpan.FromSeconds(40),
            BusinessMechanicCatalog.BunkerNormalSupply / BusinessMechanicCatalog.MansionMultiplier);
        Assert.Equal(TimeSpan.FromMinutes(90),
            BusinessMechanicCatalog.AcidBoostAllowanceWork /
            BusinessMechanicCatalog.AcidOwnBoostMultiplier +
            (BusinessMechanicCatalog.AcidNormalSupply - BusinessMechanicCatalog.AcidBoostAllowanceWork));
        Assert.Equal(TimeSpan.FromMinutes(50),
            BusinessMechanicCatalog.AcidNormalSupply / BusinessMechanicCatalog.MansionMultiplier);
        Assert.Equal(TimeSpan.FromMinutes(30),
            BusinessMechanicCatalog.AcidBoostAllowanceWork /
            (BusinessMechanicCatalog.MansionMultiplier * BusinessMechanicCatalog.AcidOwnBoostMultiplier) +
            (BusinessMechanicCatalog.AcidNormalSupply - BusinessMechanicCatalog.AcidBoostAllowanceWork) /
            BusinessMechanicCatalog.MansionMultiplier);

        var anchors = new Dictionary<int, int>
        {
            [50_000] = 96,
            [45_000] = 144,
            [25_000] = 192,
            [24_000] = 240,
            [20_000] = 432,
            [10_000] = 480,
        };
        foreach (var anchor in anchors)
            Assert.Equal(TimeSpan.FromMinutes(anchor.Value),
                BusinessMechanicCatalog.NightclubTimeUntilBelowTarget(anchor.Key, true));

        Assert.Equal(TimeSpan.FromHours(8), BusinessMechanicCatalog.CarWashTimeUntilMinimum(1));
        Assert.Equal(TimeSpan.FromMinutes(576), BusinessMechanicCatalog.CarWashTimeUntilMinimum(2));
        Assert.Equal(TimeSpan.FromMinutes(624), BusinessMechanicCatalog.CarWashTimeUntilMinimum(3));
    }

    [Fact]
    public void BusinessEngine_PausesUnknownPresence_AndAppliesMansionRateWithoutReset()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
        using var engine = new BusinessManagerEngine(new SharedTimerRegistry(new MemoryTimerStore(), time));
        engine.Update(OnlinePlaytimeAvailability.Online);
        engine.StartBunker();
        time.Advance(TimeSpan.FromMinutes(30));
        var normal = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Bunker);
        Assert.Equal(TimeSpan.FromMinutes(30), normal.AccumulatedOnlineTime);

        engine.StartMansionBoost(acid: false);
        time.Advance(TimeSpan.FromMinutes(20));
        var boosted = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Bunker);
        Assert.Equal(TimeSpan.FromMinutes(90), boosted.AccumulatedOnlineTime);
        Assert.Equal(TimeSpan.FromMinutes(16) + TimeSpan.FromSeconds(40),
            engine.EstimateRemaining(boosted, time.GetUtcNow()));

        engine.Update(OnlinePlaytimeAvailability.Unknown);
        time.Advance(TimeSpan.FromHours(24));
        var paused = Find(engine.Update(OnlinePlaytimeAvailability.Unknown), BusinessTimerIds.Bunker);
        Assert.Equal(TimeSpan.FromMinutes(90), paused.AccumulatedOnlineTime);
        Assert.Equal(SharedTimerState.Paused, paused.State);

        engine.Update(OnlinePlaytimeAvailability.Online);
        time.Advance(TimeSpan.FromMinutes(50));
        var ready = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Bunker);
        Assert.Equal(SharedTimerState.Ready, ready.State);
    }

    [Fact]
    public void AcidProduction_ComposesLimitedOwnBoostAndMansionWithoutResettingProgress()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
        using var engine = new BusinessManagerEngine(new SharedTimerRegistry(new MemoryTimerStore(), time));
        engine.Update(OnlinePlaytimeAvailability.Online);
        engine.StartAcid();
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(30),
            Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid).AccumulatedOnlineTime);

        engine.StartAcidBoost();
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromMinutes(60),
            Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid).AccumulatedOnlineTime);

        engine.StartMansionBoost(acid: true);
        time.Advance(TimeSpan.FromMinutes(15));
        var combined = Find(engine.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
        Assert.Equal(TimeSpan.FromMinutes(150), combined.AccumulatedOnlineTime);
        Assert.Equal(SharedTimerState.Ready, combined.State);
    }

    [Fact]
    public void StaffDispatches_AreIndependentWallClockTimers_AndCarWashUsesDiscreteCycleTargets()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
        using var engine = new BusinessManagerEngine(new SharedTimerRegistry(new MemoryTimerStore(), time));
        engine.StartCargo(1);
        time.Advance(TimeSpan.FromMinutes(10));
        engine.StartCargo(2);
        time.Advance(TimeSpan.FromMinutes(38));
        var snapshots = engine.Update(OnlinePlaytimeAvailability.Unknown);
        Assert.Equal(SharedTimerState.Ready, Find(snapshots, BusinessTimerIds.Cargo(1)).State);
        Assert.Equal(TimeSpan.FromMinutes(10), Find(snapshots, BusinessTimerIds.Cargo(2)).Remaining);

        engine.StartCarWash(3);
        var carWash = Find(engine.Update(OnlinePlaytimeAvailability.Unknown), BusinessTimerIds.CarWash);
        Assert.Equal(TimerClockMode.OnlinePlaytime, carWash.ClockMode);
        Assert.Equal(13 * BusinessMechanicCatalog.CarWashCycle, carWash.RequiredDuration);
        Assert.Equal(SharedTimerState.Paused, carWash.State);
    }

    [Fact]
    public void Cayo_TransitionsToHardModeWindow_AndRaisesEachCompletionOnce()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
        using var engine = new BusinessManagerEngine(new SharedTimerRegistry(new MemoryTimerStore(), time));
        var completed = new List<string>();
        engine.Ready += item => completed.Add(item.TimerId);
        engine.StartHeist(BusinessHeistKind.CayoGroup);
        time.Advance(TimeSpan.FromMinutes(48));
        var cooldownReady = engine.Update(OnlinePlaytimeAvailability.Unknown);
        Assert.Contains(cooldownReady, item => item.TimerId == BusinessTimerIds.CayoHardMode &&
            item.State == SharedTimerState.Running);
        Assert.Single(completed);
        engine.Update(OnlinePlaytimeAvailability.Unknown);
        Assert.Single(completed);

        time.Advance(TimeSpan.FromMinutes(48));
        var expired = engine.Update(OnlinePlaytimeAvailability.Unknown);
        Assert.DoesNotContain(expired, item => item.TimerId == BusinessTimerIds.CayoHardMode);
        Assert.Single(completed);
        engine.Update(OnlinePlaytimeAvailability.Unknown);
        Assert.Single(completed);
    }

    [Fact]
    public void OnlineProduction_RestartDoesNotGuessClosedTime()
    {
        var store = new MemoryTimerStore();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
        using (var first = new BusinessManagerEngine(new SharedTimerRegistry(store, time)))
        {
            first.Update(OnlinePlaytimeAvailability.Online);
            first.StartAcid();
            time.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(TimeSpan.FromMinutes(20),
                Find(first.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid).AccumulatedOnlineTime);
        }

        time.Advance(TimeSpan.FromHours(8));
        using var restarted = new BusinessManagerEngine(new SharedTimerRegistry(store, time));
        var restored = Find(restarted.Update(OnlinePlaytimeAvailability.Online), BusinessTimerIds.Acid);
        Assert.Equal(TimeSpan.FromMinutes(20), restored.AccumulatedOnlineTime);
    }

    [Fact]
    public void BusinessPersistence_AvoidsRedundantStartupAndActionWrites_AndFlushesOnDispose()
    {
        var store = new MemoryTimerStore();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
        var engine = new BusinessManagerEngine(new SharedTimerRegistry(store, time));
        Assert.Equal(0, store.SaveCount);
        engine.Update(OnlinePlaytimeAvailability.Online);
        Assert.Equal(0, store.SaveCount);
        engine.StartBunker();
        var savesAfterAction = store.SaveCount;
        Assert.Equal(1, savesAfterAction);

        for (var index = 0; index < 10; index++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            engine.Update(OnlinePlaytimeAvailability.Online);
        }

        Assert.Equal(savesAfterAction, store.SaveCount);
        engine.Dispose();
        Assert.True(store.SaveCount > savesAfterAction);
    }

    [Fact]
    public void BusinessUpdate_AdvancesSharedRegistryExactlyOncePerRefresh()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
        using var engine = new BusinessManagerEngine(
            new SharedTimerRegistry(new MemoryTimerStore(), time));
        var timestampReads = time.TimestampReadCount;

        engine.Update(OnlinePlaytimeAvailability.Online);

        Assert.Equal(timestampReads + 1, time.TimestampReadCount);
    }

    [Fact]
    public void Settings_DefaultsAndM3MediaPolicy_AreSafe()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Equal(22, settings.SchemaVersion);
        Assert.False(settings.BusinessManagerEnabled);
        Assert.True(settings.AnimatedMediaPlaybackEnabled);
        Assert.True(string.IsNullOrEmpty(settings.BusinessManagerVisibilityHotkey.Key));
        Assert.Equal(5, Enumerable.Range(1, 5).Count());
    }

    [Fact]
    public void SettingsV21_MigratesM3DefaultsWithoutEnablingBusinessManager()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LSOverlay-M3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                { "schemaVersion": 21, "businessManagerEnabled": true,
                  "animatedMediaPlaybackEnabled": false,
                  "bunkerTimerHotkey": { "key": "F6" } }
                """);
            var settings = new JsonSettingsStore(path).Load();
            Assert.Equal(22, settings.SchemaVersion);
            Assert.False(settings.BusinessManagerEnabled);
            Assert.True(settings.AnimatedMediaPlaybackEnabled);
            Assert.Equal("F6", settings.BunkerTimerHotkey.Key);
            Assert.Equal(1, settings.BusinessSpecialCargoWarehouseCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AnimatedGif_DecodesMultipleFramesAsFrozenBitmap()
    {
        var bytes = CreateTwoFrameGif();
        var decoded = DiscordMediaAssetService.DecodeSkiaFrame(bytes, 64, 1);
        Assert.True(decoded.FrameCount >= 2);
        Assert.True(decoded.Image.IsFrozen);
    }

    [Fact]
    public void AnimationLifecycle_TwentyDecodeAndDisposeCyclesReturnEveryOwnerToZero()
    {
        RunSta(() =>
        {
            var bytes = CreateTwoFrameGif();
            var metrics = new RuntimeMetricsCollector();
            using var scheduler = new MediaAnimationScheduler(
                Dispatcher.CurrentDispatcher,
                metrics,
                NullAppLogger.Instance);
            for (var cycle = 0; cycle < 20; cycle++)
            {
                var decodedBefore = Counter(metrics, RuntimeMetricNames.MediaAnimationFrameDecoded);
                using var registration = scheduler.Register(bytes, 64, _ => { });
                Assert.Equal(1, metrics.Snapshot().Gauges[RuntimeMetricNames.MediaAnimationActivePlayers]);
                PumpUntil(
                    () => Counter(metrics, RuntimeMetricNames.MediaAnimationFrameDecoded) > decodedBefore,
                    TimeSpan.FromSeconds(2));
                registration.Dispose();
                PumpUntil(
                    () => Gauge(metrics, RuntimeMetricNames.MediaAnimationDecoderCount) == 0 &&
                          Gauge(metrics, RuntimeMetricNames.MediaAnimationFrameBuffers) == 0,
                    TimeSpan.FromSeconds(2));
                Assert.Equal(0, Gauge(metrics, RuntimeMetricNames.MediaAnimationActivePlayers));
            }

            Assert.Equal(20, Counter(metrics, RuntimeMetricNames.MediaAnimationDisposals));
            Assert.True(Counter(metrics, RuntimeMetricNames.MediaAnimationFrameDecoded) >= 20);
            Assert.Equal(0, Gauge(metrics, RuntimeMetricNames.MediaAnimationActivePlayers));
            Assert.Equal(0, Gauge(metrics, RuntimeMetricNames.MediaAnimationDecoderCount));
            Assert.Equal(0, Gauge(metrics, RuntimeMetricNames.MediaAnimationFrameBuffers));
            Assert.Equal(0, Gauge(metrics, RuntimeMetricNames.MediaAnimationSchedulerActive));
        });
    }

    [Fact]
    public void StaticWebP_DecodesThroughTheSharedSkiaPath()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2));
        bitmap.Erase(SKColors.DeepSkyBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 90);
        var decoded = DiscordMediaAssetService.DecodeSkiaFrame(data.ToArray(), 64, 0);
        Assert.Equal(1, decoded.FrameCount);
        Assert.True(decoded.Image.IsFrozen);
    }

    [Theory]
    [InlineData(8193, 1, 1)]
    [InlineData(8192, 8192, 1)]
    [InlineData(1, 1, 2001)]
    public void AnimatedMediaSafety_RejectsOversizedCanvasPixelCountAndFrameCount(
        int width,
        int height,
        int frameCount)
    {
        var validate = typeof(DiscordMediaAssetService).GetMethod(
            "ValidateMedia",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            validate.Invoke(null, [new SKImageInfo(width, height), frameCount]));

        Assert.IsType<InvalidDataException>(failure.InnerException);
    }

    [Fact]
    public void Presentation_WiresIndependentBusinessHudAndSingleAnimationSetting()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var settingsXaml = File.ReadAllText(Path.Combine(root, "src", "GachaOverlay.App", "Presentation", "FoundationWindow.xaml"));
        var businessXaml = File.ReadAllText(Path.Combine(root, "src", "GachaOverlay.App", "Presentation", "BusinessManagerWindow.xaml"));
        Assert.Contains("BusinessManagerTemplate", settingsXaml);
        Assert.Contains("AnimatedMediaPlaybackEnabled", settingsXaml);
        Assert.Contains("GeneralTimerPresets", settingsXaml);
        Assert.DoesNotContain("ItemsSource=\"{Binding BunkerTimerPresets}\"", settingsXaml);
        Assert.DoesNotContain("ItemsSource=\"{Binding LsdTimerPresets}\"", settingsXaml);
        Assert.Contains("IsHitTestVisible=\"{Binding IsInteractive}\"", businessXaml);
        Assert.Contains("Visibility=\"{Binding IsInteractive, Converter={StaticResource BoolVisibility}}\"", businessXaml);
        Assert.DoesNotContain("Settings", businessXaml, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("business-manager", BusinessManagerWindowController.WindowId);
    }

    private static SharedTimerSnapshot Find(IEnumerable<SharedTimerSnapshot> values, string id) =>
        values.Single(item => item.TimerId == id);

    private static byte[] CreateTwoFrameGif()
    {
        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Pixel(0, 0, 0)));
        encoder.Frames.Add(BitmapFrame.Create(Pixel(255, 255, 255)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource Pixel(byte blue, byte green, byte red)
    {
        var value = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null,
            new[] { blue, green, red }, 3);
        value.Freeze();
        return value;
    }

    private static long Counter(RuntimeMetricsCollector metrics, string name) =>
        metrics.Snapshot().Counters.GetValueOrDefault(name);

    private static double Gauge(RuntimeMetricsCollector metrics, string name) =>
        metrics.Snapshot().Gauges.GetValueOrDefault(name);

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Dispatcher operation did not settle in time.");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(10),
                DispatcherPriority.Background,
                (_, _) => frame.Continue = false,
                Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class MemoryTimerStore : ISharedTimerStore
    {
        private IReadOnlyList<SharedTimerPersistedEntry> _items = [];
        public int SaveCount { get; private set; }
        public IReadOnlyList<SharedTimerPersistedEntry> Load() => _items;
        public bool Save(IReadOnlyCollection<SharedTimerPersistedEntry> entries)
        { SaveCount++; _items = entries.ToArray(); return true; }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utc;
        private long _timestamp;
        public ManualTimeProvider(DateTimeOffset utc) => _utc = utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utc;
        public int TimestampReadCount { get; private set; }
        public override long GetTimestamp()
        {
            TimestampReadCount++;
            return _timestamp;
        }
        public void Advance(TimeSpan elapsed) { _utc += elapsed; _timestamp += elapsed.Ticks; }
    }
}

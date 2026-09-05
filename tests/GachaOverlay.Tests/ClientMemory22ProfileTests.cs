using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Gta;
using GachaOverlay.Infrastructure.Gta;
using LSOverlay.Protocol;
using GachaOverlay.Core.Themes;

namespace GachaOverlay.Tests;

// Opt-in, synthetic-only. No ApplicationHost, credentials, Remote connection or user settings.
// Uses production WPF views/VMs/decoder on an STA. Testhost/runtime overhead is included.
public sealed class ClientMemory22ProfileTests
{
    [Fact]
    public void ProfileProductionViews()
    {
        var output = Environment.GetEnvironmentVariable("LSO_CLIENT_PROFILE");
        if (string.IsNullOrEmpty(output)) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Profile(output); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(15)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Profile(string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var file in new[] { "DesignTokens", "ModernControls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"/GachaOverlay.App;component/Themes/{file}.xaml", UriKind.Relative) });
        var themeManager = new ColorThemeManager(app);
        themeManager.Apply(ColorThemeCatalog.DefaultTheme);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var localization = new ResourceLocalizationService();
        var settings = AppSettings.CreateDefault();
        var typography = new ChatTypographyResolver(NullAppLogger.Instance).Resolve(settings.ChatFontPreset);
        var chat = new ChatViewModel();
        ChatMessageViewModel Message(int id)
        {
            var text = $"한글 테스트 {id} — Message with wrapping and outlines. @Tester 😀";
            var vm = new ChatMessageViewModel(new ChatMessagePresentation(id.ToString(), $"Tester_{id % 4}",
                DateTimeOffset.UnixEpoch, [new ChatToken(ChatTokenKind.Text, text)], text, [], [], 0, false, 1, id),
                localization, _ => { });
            vm.ApplySettings(settings, default, typography);
            return vm;
        }
        var view = new ChatView { DataContext = chat, Width = 620, Height = 680 };
        var panel = new StackPanel();
        panel.Children.Add(view);
        var media = new WrapPanel();
        panel.Children.Add(media);
        // Real HWND/composition; non-activating diagnostic window outside the desktop viewport.
        var window = new Window
        {
            Content = panel,
            Width = 660,
            Height = 1000,
            Left = -12000,
            Top = -12000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        var gif = MediaLatencyProfile211Tests.Fixture(384, 10);
        var fastGif = MediaLatencyProfile211Tests.Fixture(384, 2);
        if (Environment.GetEnvironmentVariable("LSO_CLIENT_VISUAL") == "1")
        {
            for (var i = 0; i < 20; i++) chat.Messages.Add(Message(i));
            var preview = DiscordMediaAssetService.DecodeSkiaFrame(gif, 96, 0).Image;
            var decorated = chat.Messages[0];
            decorated.Update(new ChatMessagePresentation("0", "한글 Latin 長い名前", DateTimeOffset.UnixEpoch,
                [new ChatToken(ChatTokenKind.Text, "내용 "), new ChatToken(ChatTokenKind.Mention, "@Tester", "test", true),
                 new ChatToken(ChatTokenKind.CustomEmoji, ":emoji:", "fixture"), new ChatToken(ChatTokenKind.Text, " 줄바꿈 테스트 😀")],
                "내용 @Tester 줄바꿈 테스트", [new ChatMediaCandidate("https://invalid.example/fixture", "image/gif", 96, 96)], [], 0, true, 1, 1)
            {
                AuthorStyle = new DiscordAuthorStyle("role", 0xC586FF, "role", new DiscordRoleIcon("unicode", "★")),
                Reactions = [new DiscordMessageReaction(new DiscordCustomEmoji("", "👍", false), 3)]
            });
            foreach (var token in decorated.Tokens.Where(token => token.Kind == ChatTokenKind.CustomEmoji)) token.Image = preview;
            decorated.Thumbnail = preview;
            decorated.ApplySettings(settings, ChatResponsiveLevel.Full, typography);
            // Keep the decorated row at the bottom so Latest20 auto-scroll includes it.
            chat.Messages.Move(0, 19);
            foreach (var theme in Enum.GetValues<ColorThemeId>())
                foreach (var density in new[] { "Balanced", "Compact", "UltraCompact" })
                {
                    themeManager.Apply(theme);
                    var visualSettings = settings with { ChatLayoutMode = density == "Compact" ? ChatLayoutMode.Compact : ChatLayoutMode.Balanced };
                    foreach (var message in chat.Messages)
                        message.ApplySettings(visualSettings, density == "UltraCompact" ? ChatResponsiveLevel.UltraCompact : ChatResponsiveLevel.Full, typography);
                    MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(100));
                    ((ScrollViewer)view.FindName("MessageScroller")).ScrollToEnd();
                    MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(50));
                    foreach (var dpi in new[] { 96d, 144d, 192d })
                        Capture(view, Path.ChangeExtension(output, $".{theme}.{density}.{dpi}.png"), dpi);
                }
            foreach (var message in chat.Messages) message.Dispose();
            window.Close(); app.Shutdown();
            return;
        }
        var metrics = new RuntimeMetricsCollector();
        using var scheduler = new MediaAnimationScheduler(dispatcher, metrics, NullAppLogger.Instance);
        var players = new List<IDisposable>();
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "LSO-memory-synthetic-" + Guid.NewGuid().ToString("N")));
        var store = new JsonSettingsStore(Path.Combine(directory.FullName, "settings.json"));
        store.Load();
        using var settingsVm = new FoundationViewModel(store, localization, NullAppLogger.Instance,
            new ChatTypographyResolver(NullAppLogger.Instance), () => { }, _ => { }, () => { });
        FoundationWindow? settingsWindow = null;
        GtaCompanionWindow? companionWindow = null;
        GtaCompanionViewModel? companionVm = null;
        var results = new List<object>();
        var scenarios = Environment.GetEnvironmentVariable("LSO_CLIENT_EXTENDED") == "1"
            ? new[] { "Sales30", "GTA", "AnimatedEmoji", "Activity", "Cleanup" }
            : new[] { "Idle", "Chat20", "Static1", "Static5", "GIF1", "GIF3", "GIF5", "Mixed", "Cleanup", "SettingsOpen", "SettingsClosed", "Lifecycle" };
        if (Environment.GetEnvironmentVariable("LSO_CLIENT_BFINAL") == "1")
            scenarios = ["Idle", "Chat20", "Static1", "Static5", "GIF1", "GIF3", "GIF5", "Sales30", "SalesMedia", "GTA", "SettingsOpen", "SettingsClosed", "Mixed", "Cleanup"];
        if (Environment.GetEnvironmentVariable("LSO_CLIENT_SALES_MEDIA") == "1")
        {
            scenarios = ["Sales30", "SalesMedia", "Cleanup"];
            // Sales gets the full diagnostic HWND, not a clipped child below the 680-DIP chat.
            panel.Children.Remove(media);
            window.Content = media;
        }
        var ownerScenario = Environment.GetEnvironmentVariable("LSO_CLIENT_OWNER_SCENARIO");
        if (!string.IsNullOrWhiteSpace(ownerScenario))
            scenarios = ownerScenario switch
            {
                "Chat20" => ["Chat20"],
                "Mixed" => ["Chat20", "Mixed"],
                "GIF5" => ["Chat20", "GIF5"],
                "SalesMedia" => ["Sales30", "SalesMedia"],
                "GTA" => ["Chat20", "GTA"],
                "SettingsClosed" => ["Chat20", "SettingsOpen", "SettingsClosed"],
                _ => throw new InvalidOperationException("Unsupported ownership checkpoint."),
            };
        if (Environment.GetEnvironmentVariable("LSO_CLIENT_EXTENDED") == "1")
            for (var i = 0; i < 20; i++) chat.Messages.Add(Message(i));
        foreach (var scenario in scenarios)
        {
            foreach (var player in players) player.Dispose();
            players.Clear(); media.Children.Clear();
            if (Environment.GetEnvironmentVariable("LSO_CLIENT_BFINAL") == "1")
            {
                if (scenario is "Sales30" or "SalesMedia")
                {
                    panel.Children.Remove(media);
                    window.Content = media; // Full Sales HWND, not clipped below Chat.
                }
                else if (!panel.Children.Contains(media))
                {
                    window.Content = panel;
                    panel.Children.Add(media);
                }
            }
            if (scenario == "Chat20") for (var i = 0; i < 20; i++) chat.Messages.Add(Message(i));
            var count = scenario switch { "Static1" or "GIF1" => 1, "GIF3" => 3, "Static5" or "GIF5" or "Mixed" or "Activity" => 5, _ => 0 };
            for (var i = 0; i < count; i++)
            {
                var image = new System.Windows.Controls.Image { Width = 120, Height = 120 };
                media.Children.Add(image);
                if (scenario.StartsWith("Static", StringComparison.Ordinal))
                {
                    using var encoded = new MemoryStream(gif, writable: false);
                    image.Source = DiscordMediaAssetService.DecodeImage(encoded, 384);
                }
                else players.Add(scheduler.Register(i % 2 == 0 ? gif : fastGif, 384, value => image.Source = value));
            }
            var opened = Stopwatch.StartNew();
            if (scenario is "Sales30" or "SalesMedia")
            {
                var sales = new SalesQueueViewModel(localization);
                var entries = Enumerable.Range(0, 30).Select(i => new SalesQueueEntry(i.ToString(), "synthetic", i.ToString(),
                    DateTimeOffset.UnixEpoch, $"Tester_{i}", DiscordDisplayNameSource.GuildNickname, true,
                    new SaleProduct("bunker", "벙커", "1439136641330708581", "SELL_SP"), SaleObservationTrust.Trusted,
                    DetailSource: "판매 테스트 <:SELL_SP:1439136641330708581> 일반 텍스트")).ToArray();
                sales.Apply(new SalesQueueSnapshot(1, true, entries, entries[0], 30, 29, entries[1], false, false, false,
                    true, SalesObservationStatus.Live, DateTimeOffset.UnixEpoch), settings);
                sales.UpdateHudContext(true, false, false, true);
                sales.ToggleDetailCommand.Execute(null);
                var salesView = new SalesQueueView { DataContext = sales, Width = 620 };
                salesView.SetSurfaceOpacity(1, 1, 1);
                media.Children.Add(salesView);
                window.UpdateLayout();
                MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(100));
                Assert.True(sales.IsVisible);
                Assert.True(sales.IsQueueDetailPanelVisible);
                Assert.Equal(30, sales.DetailItems.Count);
                // Width changes legitimately rebuild detail VMs. Register the final visible tokens.
                if (scenario == "SalesMedia")
                    foreach (var item in sales.DetailItems.Take(5))
                        foreach (var token in item.DetailTokens.Where(token => token.Kind == ChatTokenKind.CustomEmoji))
                            players.Add(scheduler.Register(fastGif, 64, frame => token.Image = frame));
                if (scenario == "SalesMedia") Assert.Equal(5, players.Count);
            }
            if (scenario == "GTA")
            {
                companionVm = new GtaCompanionViewModel(new GtaCompanionStateManager(
                    new JsonGtaCompanionStateStore(Path.Combine(directory.FullName, "gta.json")), DateTimeOffset.UtcNow),
                    localization, settings, dispatcher);
                companionVm.ApplySnapshot(new GtaCompanionSnapshot(1, 1, GtaCompanionDataState.Available, DateTimeOffset.UnixEpoch,
                    new GtaCompanionWeek("synthetic", DateTimeOffset.UnixEpoch, null, "주간 이벤트",
                        new GtaCompanionChallenge("challenge", "연락책 임무 완료", "GTA$ 보상", []),
                        [new GtaCompanionItem("bonus", GtaCompanionItemKind.Bonus, "임무 보상 2배", "임무", 2, null, [], null, null)], [], [], []),
                    new GtaCompanionCampaign("campaign", "기간 이벤트", null, null, ["목표"], ["보상"], [])));
                companionWindow = new GtaCompanionWindow
                {
                    DataContext = companionVm,
                    Left = -12000,
                    Top = -12000,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                companionWindow.Show();
            }
            if (scenario == "AnimatedEmoji")
            {
                for (var i = 0; i < 5; i++)
                {
                    var token = new ChatTokenViewModel(new ChatToken(ChatTokenKind.CustomEmoji, ":animated:"));
                    var textView = new CrispOutlinedText { Tokens = new[] { token }, FontSize = 22, EmojiExtent = 24, Width = 40, Height = 40 };
                    media.Children.Add(textView);
                    players.Add(scheduler.Register(fastGif, 64, image => token.Image = image));
                }
            }
            if (scenario == "SettingsOpen")
            {
                settingsWindow = new FoundationWindow { DataContext = settingsVm, Left = -12000, Top = -12000, ShowActivated = false, ShowInTaskbar = false };
                settingsWindow.Show();
                foreach (var category in Enum.GetValues<SettingsCategory>())
                { settingsVm.OpenCategory(category); MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(50)); }
            }
            if (scenario == "SettingsClosed")
            {
                settingsWindow!.Hide();
                MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(100));
            }
            if (scenario == "Lifecycle")
                for (var cycle = 0; cycle < 20; cycle++)
                {
                    using (scheduler.Register(gif, 384, _ => { }))
                        MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(80));
                    settingsWindow!.Show(); settingsVm.OpenCategory(SettingsCategory.Chat);
                    MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(100));
                    var scroll = (ScrollViewer)settingsWindow.FindName("CategoryScrollViewer");
                    scroll.ScrollToVerticalOffset(140);
                    MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(50));
                    var offset = scroll.VerticalOffset;
                    settingsWindow.Hide();
                    MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(50));
                    settingsWindow.Show();
                    MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(100));
                    Assert.Equal(offset, scroll.VerticalOffset, 1);
                    Assert.Same(settingsVm, settingsWindow.DataContext);
                    settingsWindow.Hide();
                }
            var openMs = opened.Elapsed.TotalMilliseconds;
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromSeconds(2));
            if (Environment.GetEnvironmentVariable("LSO_CLIENT_OWNER_SCENARIO") == scenario)
            {
                using var checkpointProcess = Process.GetCurrentProcess();
                var checkpoint = output + ".owner-ready.json";
                File.WriteAllText(checkpoint, JsonSerializer.Serialize(new
                {
                    Boundary = "LSOverlay synthetic testhost; no ApplicationHost/credentials/network",
                    ProcessId = Environment.ProcessId,
                    Executable = Environment.ProcessPath,
                    ProcessStartedUtc = checkpointProcess.StartTime.ToUniversalTime(),
                    Scenario = scenario,
                }));
                var wait = Stopwatch.StartNew();
                var ownerSequence = 0;
                while (!File.Exists(checkpoint + ".continue") && wait.Elapsed < TimeSpan.FromSeconds(45))
                {
                    if (scenario == "Mixed")
                    {
                        // Ownership-only allocation audit; same replace/resize mechanism, fixed cadence.
                        chat.BeginMessageUpdate();
                        var old = chat.Messages[0]; chat.Messages.RemoveAt(0); old.Dispose();
                        chat.Messages.Add(Message(1000 + ++ownerSequence));
                        chat.EndMessageUpdate();
                        if (ownerSequence % 2 == 0) view.Width = view.Width == 620 ? 580 : 620;
                    }
                    MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(50));
                }
                Assert.True(File.Exists(checkpoint + ".continue"), "Synthetic ownership collector did not complete.");
            }
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocation = GC.GetTotalAllocatedBytes();
            var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var samples = new List<double>();
            var drift = new List<object>();
            var nextDrift = Stopwatch.GetTimestamp();
            var previousCpu = process.TotalProcessorTime;
            var previousStamp = Stopwatch.GetTimestamp();
            var ioStart = Io(process);
            using var cancel = new CancellationTokenSource();
            var probe = Task.Run(async () =>
            {
                var sequence = 0;
                while (!cancel.IsCancellationRequested)
                {
                    var queued = Stopwatch.GetTimestamp();
                    await dispatcher.InvokeAsync(() =>
                    {
                        samples.Add(Stopwatch.GetElapsedTime(queued).TotalMilliseconds);
                        if ((scenario == "Mixed" || scenario == "Activity") && ++sequence % 5 == 0)
                        {
                            chat.BeginMessageUpdate();
                            var old = chat.Messages[0]; chat.Messages.RemoveAt(0); old.Dispose();
                            chat.Messages.Add(Message(100 + sequence));
                            chat.EndMessageUpdate();
                            if (sequence % 10 == 0) view.Width = view.Width == 620 ? 580 : 620;
                            if (scenario == "Activity")
                            {
                                var current = chat.Messages[10];
                                var edited = "수정 UPDATE " + sequence;
                                current.Update(new ChatMessagePresentation(current.MessageId, "Tester", DateTimeOffset.UnixEpoch,
                                    [new ChatToken(ChatTokenKind.Text, edited)], edited, [], [], 0, false, 1, sequence));
                                var scroller = (ScrollViewer)view.FindName("MessageScroller");
                                chat.ObserveUserScroll(sequence % 20 == 0 ? 0 : scroller.ScrollableHeight, scroller.ScrollableHeight);
                                scroller.ScrollToVerticalOffset(sequence % 20 == 0 ? 0 : scroller.ScrollableHeight);
                            }
                        }
                        if (Stopwatch.GetTimestamp() >= nextDrift)
                        {
                            process.Refresh();
                            var stamp = Stopwatch.GetTimestamp();
                            var currentCpu = process.TotalProcessorTime;
                            drift.Add(new
                            {
                                elapsed = Stopwatch.GetElapsedTime(previousStamp).TotalSeconds,
                                workingSet = process.WorkingSet64,
                                privateBytes = process.PrivateMemorySize64,
                                heap = GC.GetTotalMemory(false),
                                process.HandleCount,
                                cpuCorePercent = (currentCpu - previousCpu).TotalSeconds / Stopwatch.GetElapsedTime(previousStamp).TotalSeconds * 100,
                                metrics = metrics.Snapshot()
                            });
                            previousCpu = currentCpu; previousStamp = stamp;
                            nextDrift = stamp + Stopwatch.Frequency * 10;
                        }
                    }, DispatcherPriority.Render);
                    await Task.Delay(20);
                }
            });
            var clock = Stopwatch.StartNew();
            var seconds = (scenario == "Mixed" || scenario == "Activity") && int.TryParse(Environment.GetEnvironmentVariable("LSO_CLIENT_SOAK_SECONDS"), out var soak) ? soak : 5;
            MediaLatencyProfile211Tests.Pump(TimeSpan.FromSeconds(seconds));
            cancel.Cancel();
            while (!probe.IsCompleted) MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(10));
            process.Refresh();
            var gc = GC.GetGCMemoryInfo();
            var tree = CountTree(window);
            samples.Sort();
            results.Add(new
            {
                scenario,
                openMs,
                seconds = clock.Elapsed.TotalSeconds,
                workingSet = process.WorkingSet64,
                privateBytes = process.PrivateMemorySize64,
                privateWorkingSet = MediaLatencyProfile211Tests.PrivateWorkingSet(process),
                managedHeap = GC.GetTotalMemory(false),
                gc.HeapSizeBytes,
                gc.FragmentedBytes,
                loh = gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3].SizeAfterBytes : 0,
                poh = gc.GenerationInfo.Length > 4 ? gc.GenerationInfo[4].SizeAfterBytes : 0,
                gc.PinnedObjectsCount,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocation,
                collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
                cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                process.HandleCount,
                threads = process.Threads.Count,
                gdi = GetGuiResources(process.Handle, 0),
                user = GetGuiResources(process.Handle, 1),
                uiMedianMs = samples[samples.Count / 2],
                uiP95Ms = samples[(int)((samples.Count - 1) * .95)],
                tree,
                settingsTree = settingsWindow is null ? null : CountTree(settingsWindow),
                companionTree = companionWindow is null ? null : CountTree(companionWindow),
                drift,
                ioReadBytes = Io(process).ReadBytes - ioStart.ReadBytes,
                ioWriteBytes = Io(process).WriteBytes - ioStart.WriteBytes,
                // Virtual module address ranges, NOT attributable resident/private bytes.
                moduleAddressBytes = process.Modules.Cast<ProcessModule>().Sum(module => (long)module.ModuleMemorySize),
                metrics = metrics.Snapshot()
            });
            if (scenario == "Chat20") Capture(view, Path.ChangeExtension(output, ".chat.png"));
            if (scenario is "Sales30" or "SalesMedia") Capture(media, Path.ChangeExtension(output, ".sales.png"));
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                boundary = "v2 themed synthetic Release testhost; actual ColorThemeManager, WPF views/HWNDs and scheduler. Not connected Remote/app-wide idle. No credentials/network. Off-desktop HWND; deterministic RenderTargetBitmap capture.",
                results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var player in players) player.Dispose();
        scheduler.StopAll();
        MediaLatencyProfile211Tests.Pump(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationDecoderCount));
        Assert.Equal(0, metrics.Snapshot().Gauges.GetValueOrDefault(RuntimeMetricNames.MediaAnimationSchedulerActive));
        settingsWindow?.CloseForApplicationExit();
        if (companionWindow is not null) { companionWindow.AllowClose = true; companionWindow.Close(); }
        companionVm?.Dispose();
        window.Close();
        foreach (var message in chat.Messages) message.Dispose();
        app.Shutdown();
    }

    internal static object CountTree(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var visual = 0; var bindings = 0; var text = 0;
        void Visit(DependencyObject value)
        {
            if (!seen.Add(value)) return;
            if (value is CrispOutlinedText) text++;
            var values = value.GetLocalValueEnumerator();
            while (values.MoveNext()) if (BindingOperations.IsDataBound(value, values.Current.Property)) bindings++;
            if (value is Visual || value is System.Windows.Media.Media3D.Visual3D)
            { visual++; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) Visit(VisualTreeHelper.GetChild(value, i)); }
            foreach (var child in LogicalTreeHelper.GetChildren(value).OfType<DependencyObject>()) Visit(child);
        }
        Visit(root);
        return new { dependencyObjects = seen.Count, visual, bindings, outlinedText = text };
    }

    private static void Capture(FrameworkElement view, string output, double dpi = 96)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * dpi / 96),
            (int)Math.Ceiling(view.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(output); encoder.Save(stream);
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static IoCounters Io(Process process)
    {
        GetProcessIoCounters(process.Handle, out var counters);
        return counters;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadCount, WriteCount, OtherCount, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}

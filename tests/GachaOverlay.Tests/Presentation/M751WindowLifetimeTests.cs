using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Threading;
using GachaOverlay.App.Lifecycle;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.Tests.Presentation;

[Collection(WpfApplicationCollection.Name)]
public sealed class M751WindowLifetimeTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Fact]
    public void ApplicationLifetime_IsExplicitAndShutdownHasOneCentralCallSite()
    {
        var appXaml = File.ReadAllText(Source("App.xaml"));
        var appCode = File.ReadAllText(Source("App.xaml.cs"));

        Assert.Contains("ShutdownMode=\"OnExplicitShutdown\"", appXaml);
        Assert.Contains("ShutdownMode = ShutdownMode.OnExplicitShutdown", appCode);
        Assert.Equal(1, appCode.Split("Shutdown(exitCode)", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("MainWindow =", appCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitExitRequest_IsIdempotent()
    {
        var dispatcher = new RecordingDispatcher();
        var shutdowns = 0;
        var lifetime = new ApplicationLifetimeService(
            dispatcher,
            () => NullAppLogger.Instance,
            _ => shutdowns++);

        Assert.True(lifetime.RequestExit(ApplicationExitSource.TrayExit, 0));
        Assert.False(lifetime.RequestExit(ApplicationExitSource.TrayExit, 0));
        Assert.True(lifetime.IsExitRequested);
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public void BackgroundExitRequest_MarshalsToUiDispatcher()
    {
        var dispatcher = new RecordingDispatcher { HasAccess = false };
        var shutdowns = 0;
        var lifetime = new ApplicationLifetimeService(
            dispatcher,
            () => NullAppLogger.Instance,
            _ => shutdowns++);

        lifetime.RequestExit(ApplicationExitSource.TrayExit, 0);

        Assert.Equal(1, dispatcher.BeginInvokeCount);
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public void GearAndTrayOpen_ReuseOneSettingsWindowAndNeverRequestExit()
    {
        var created = 0;
        var exits = 0;
        var window = new FakeSettingsWindow();
        using var service = new SettingsWindowService(
            new RecordingDispatcher(),
            () =>
            {
                created++;
                return window;
            },
            NullAppLogger.Instance);

        Assert.True(service.Open(SettingsOpenSource.HudGear, SettingsCategory.Hud));
        service.Hide();
        Assert.True(service.Open(SettingsOpenSource.Tray, null));

        Assert.Equal(1, created);
        Assert.Equal(2, window.ShowCount);
        Assert.Equal(new SettingsCategory?[] { SettingsCategory.Hud, null }, window.Categories);
        Assert.Equal(0, exits);
    }

    [Fact]
    public void SettingsOpen_FromBackground_MarshalsToUiDispatcher()
    {
        var dispatcher = new RecordingDispatcher { HasAccess = false };
        using var service = new SettingsWindowService(
            dispatcher,
            () => new FakeSettingsWindow(),
            NullAppLogger.Instance);

        Assert.True(service.Open(SettingsOpenSource.HudGear, SettingsCategory.Hud));

        Assert.Equal(1, dispatcher.InvokeCount);
    }

    [Fact]
    public void SettingsOpenFailure_LogsSourceAndDoesNotRequestApplicationExit()
    {
        var logger = new RecordingLogger();
        var notices = 0;
        using var service = new SettingsWindowService(
            new RecordingDispatcher(),
            () => throw new InvalidOperationException("test failure"),
            logger,
            () => notices++);

        Assert.False(service.Open(SettingsOpenSource.HudGear, SettingsCategory.Hud));

        Assert.Equal(1, notices);
        Assert.Contains(logger.Errors, entry =>
            entry.Contains("source=HudGear", StringComparison.Ordinal) &&
            entry.Contains("category=Hud", StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsDispose_ClosesExactlyOnceAndBlocksReopen()
    {
        var window = new FakeSettingsWindow();
        var service = new SettingsWindowService(
            new RecordingDispatcher(),
            () => window,
            NullAppLogger.Instance);
        service.Open(SettingsOpenSource.Tray, null);

        service.PrepareForApplicationExit();
        service.Dispose();
        service.Dispose();

        Assert.Equal(1, window.CloseCount);
        Assert.False(service.Open(SettingsOpenSource.HudGear, SettingsCategory.Hud));
    }

    [Fact]
    public void RunContentElement_UsesSafeAncestryAndFindsInteractiveButton()
    {
        RunSta(() =>
        {
            var run = new Run("Settings");
            var text = new TextBlock();
            text.Inlines.Add(run);
            var button = new Button { Content = text };
            var window = new Window
            {
                Content = button,
                Left = -10000,
                Top = -10000,
                Opacity = 0,
                ShowInTaskbar = false,
            };
            window.Show();
            window.UpdateLayout();

            Assert.Same(text, WpfHitTestAncestry.GetParent(run));
            Assert.True(WpfHitTestAncestry.HasInteractiveAncestor(button));
            Assert.True(WpfHitTestAncestry.HasInteractiveAncestor(run));
            window.Close();
        });
    }

    [Fact]
    public void GearClick_RaisesSettingsOnceAndDoesNotRaiseDrag()
    {
        RunSta(() =>
        {
            var window = new HudWindow();
            var settings = 0;
            var drags = 0;
            window.SettingsRequested += () => settings++;
            window.DragRequested += () => drags++;

            window.HeaderSettingsButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.Equal(1, settings);
            Assert.Equal(0, drags);
            window.AllowClose = true;
            window.Close();
        });
    }

    [Fact]
    public void WpfStaSmoke_ReopensSettingsWithoutDestroyingBorrowedHudSource()
    {
        RunSta(() =>
        {
            var hud = new HudWindow
            {
                Left = -10000,
                Top = -10000,
                Opacity = 0,
                ShowInTaskbar = false,
            };
            var interop = new WindowInteropService(hud, () => false, NullAppLogger.Instance);
            interop.Initialize();
            hud.Show();
            var hudHandle = new WindowInteropHelper(hud).Handle;
            var borrowedSource = HwndSource.FromHwnd(hudHandle);
            SmokeSettingsWindow? settings = null;
            var created = 0;
            var logger = new RecordingLogger();
            using var settingsService = new SettingsWindowService(
                new UiDispatcherAdapter(Dispatcher.CurrentDispatcher),
                () =>
                {
                    created++;
                    settings = new SmokeSettingsWindow
                    {
                        Left = -10000,
                        Top = -10000,
                        Opacity = 0,
                        ShowInTaskbar = false,
                    };
                    return settings;
                },
                logger);

            for (var index = 0; index < 10; index++)
            {
                if (!settingsService.Open(SettingsOpenSource.HudGear, SettingsCategory.Hud))
                {
                    throw new InvalidOperationException(string.Join(Environment.NewLine, logger.Errors));
                }

                Assert.True(WindowInteropService.IsNativeWindowValid(hudHandle));
                settingsService.Hide();
            }

            Assert.Equal(1, created);
            Assert.NotNull(settings);
            Assert.True(interop.IsHookAttached);
            hud.Hide();
            Assert.True(interop.IsHookAttached);

            interop.Dispose();
            Assert.False(interop.IsHookAttached);
            Assert.True(WindowInteropService.IsNativeWindowValid(hudHandle));
            Assert.Same(borrowedSource, HwndSource.FromHwnd(hudHandle));

            settingsService.PrepareForApplicationExit();
            hud.AllowClose = true;
            hud.Close();
        });
    }

    [Fact]
    public void SourceAudit_HasNoManualWpfHwndDestructionOrChildDispatcherShutdown()
    {
        var appSources = Directory.GetFiles(
            Path.Combine(RepositoryRoot, "src", "GachaOverlay.App"),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => Path.GetFileName(path) != "DiscordQuickFocusHook.cs") // Dedicated non-UI hook thread owns its message loop.
            .Select(File.ReadAllText)
            .ToArray();
        var combined = string.Join(Environment.NewLine, appSources);

        Assert.DoesNotContain("DestroyWindow(", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("InvokeShutdown(", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginInvokeShutdown(", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.Exit(", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Kill(", combined, StringComparison.Ordinal);
        Assert.Contains("Never Dispose it", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayOpen_RequestsLastVisitedCategory()
    {
        var window = new FakeSettingsWindow();
        using var service = new SettingsWindowService(
            new RecordingDispatcher(),
            () => window,
            NullAppLogger.Instance);

        service.Open(SettingsOpenSource.Tray, null);

        Assert.Null(Assert.Single(window.Categories));
    }

    [Fact]
    public void RepeatedOpen_ActivatesOneExistingInstance()
    {
        var created = 0;
        var window = new FakeSettingsWindow();
        using var service = new SettingsWindowService(
            new RecordingDispatcher(),
            () =>
            {
                created++;
                return window;
            },
            NullAppLogger.Instance);

        for (var index = 0; index < 100; index++)
        {
            service.Open(SettingsOpenSource.HudGear, SettingsCategory.Hud);
        }

        Assert.Equal(1, created);
        Assert.Equal(100, window.ShowCount);
        Assert.Same(window, service.CurrentWindow);
    }

    [Fact]
    public void HiddenSettingsWindow_IsReusable()
    {
        var created = 0;
        var window = new FakeSettingsWindow();
        using var service = new SettingsWindowService(
            new RecordingDispatcher(),
            () =>
            {
                created++;
                return window;
            },
            NullAppLogger.Instance);
        service.Open(SettingsOpenSource.Tray, null);

        service.Hide();
        service.Open(SettingsOpenSource.Tray, null);

        Assert.Equal(1, created);
        Assert.True(window.IsVisible);
    }

    [Fact]
    public void UnexpectedClosedInstance_IsNotReused()
    {
        var windows = new List<FakeSettingsWindow>();
        using var service = new SettingsWindowService(
            new RecordingDispatcher(),
            () =>
            {
                var window = new FakeSettingsWindow();
                windows.Add(window);
                return window;
            },
            NullAppLogger.Instance);
        service.Open(SettingsOpenSource.Tray, null);

        windows[0].RaiseClosed();
        service.Open(SettingsOpenSource.Tray, null);

        Assert.Equal(2, windows.Count);
        Assert.Same(windows[1], service.CurrentWindow);
    }

    [Fact]
    public void DispatcherShutdown_BlocksSettingsCreation()
    {
        var created = 0;
        using var service = new SettingsWindowService(
            new RecordingDispatcher { HasShutdownStarted = true },
            () =>
            {
                created++;
                return new FakeSettingsWindow();
            },
            NullAppLogger.Instance);

        Assert.False(service.Open(SettingsOpenSource.Tray, null));
        Assert.Equal(0, created);
    }

    [Fact]
    public void BackgroundDispose_MarshalsSettingsCloseToUiDispatcher()
    {
        var dispatcher = new RecordingDispatcher { HasAccess = false };
        var window = new FakeSettingsWindow();
        var service = new SettingsWindowService(dispatcher, () => window, NullAppLogger.Instance);
        service.Open(SettingsOpenSource.Tray, null);

        service.Dispose();

        Assert.Equal(2, dispatcher.InvokeCount);
        Assert.Equal(1, window.CloseCount);
    }

    [Fact]
    public void FailureNotificationException_IsLoggedWithoutEscapingOpenBoundary()
    {
        var logger = new RecordingLogger();
        using var service = new SettingsWindowService(
            new RecordingDispatcher(),
            () => throw new InvalidOperationException("open"),
            logger,
            () => throw new InvalidOperationException("notice"));

        Assert.False(service.Open(SettingsOpenSource.Tray, null));
        Assert.Contains(logger.Errors, entry => entry.Contains(
            "notification could not be shown",
            StringComparison.Ordinal));
    }

    [Fact]
    public void PrepareForApplicationExit_BlocksAllSettingsSources()
    {
        using var service = new SettingsWindowService(
            new RecordingDispatcher(),
            () => new FakeSettingsWindow(),
            NullAppLogger.Instance);

        service.PrepareForApplicationExit();

        Assert.False(service.Open(SettingsOpenSource.HudGear, SettingsCategory.Hud));
        Assert.False(service.Open(SettingsOpenSource.Tray, null));
        Assert.False(service.Open(SettingsOpenSource.ConnectionGate, null));
    }

    [Theory]
    [InlineData((int)ApplicationExitSource.TrayExit)]
    [InlineData((int)ApplicationExitSource.StartupFailure)]
    [InlineData((int)ApplicationExitSource.FatalUnhandledException)]
    public void EveryLegitimateExitSource_UsesOrderedShutdownOnce(int sourceValue)
    {
        var source = (ApplicationExitSource)sourceValue;
        var shutdowns = 0;
        var lifetime = new ApplicationLifetimeService(
            new RecordingDispatcher(),
            () => NullAppLogger.Instance,
            _ => shutdowns++);

        lifetime.RequestExit(source, 0);
        lifetime.RequestExit(source, 0);

        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public void AuxiliaryWindows_DoNotContainApplicationShutdownCalls()
    {
        foreach (var relativePath in new[]
                 {
                     Path.Combine("Presentation", "FoundationWindow.xaml.cs"),
                     Path.Combine("Presentation", "ProductMappingManagerWindow.xaml.cs"),
                     Path.Combine("Presentation", "SalesPreviewWindow.xaml.cs"),
                     Path.Combine("Presentation", "HudWindow.xaml.cs"),
                 })
        {
            var source = File.ReadAllText(Source(relativePath));
            Assert.DoesNotContain("Shutdown(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("InvokeShutdown", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ApplicationHost_PassesShutdownOnlyToTrayExit()
    {
        var source = File.ReadAllText(Source("Lifecycle", "ApplicationHost.cs"));

        Assert.Equal(3, source.Split("_requestShutdown", StringSplitOptions.None).Length - 1);
        Assert.Contains("_remoteChatCoordinator?.RefreshAsync(),\n            _requestShutdown", source.Replace("\r\n", "\n"));
        Assert.DoesNotContain("_requestShutdown();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidOrUninitializedHudHandle_SkipsAllNativeOperations()
    {
        RunSta(() =>
        {
            var hud = new HudWindow();
            using var interop = new WindowInteropService(hud, () => false, NullAppLogger.Instance);

            Assert.False(interop.ApplyClickThrough(true));
            Assert.False(interop.ApplyTopmost());
            Assert.False(interop.SetWindowLocation(10, 10));
            Assert.False(interop.TryGetWindowRectangle(out _));
            Assert.Equal(96, interop.GetWindowDpi());
        });
    }

    [Fact]
    public void HudHide_DoesNotRemoveHookOrInvalidateHandle()
    {
        RunSta(() =>
        {
            var hud = CreateInvisibleHud();
            using var interop = new WindowInteropService(hud, () => false, NullAppLogger.Instance);
            interop.Initialize();
            hud.Show();
            var handle = interop.Handle;

            hud.Hide();

            Assert.True(interop.IsHookAttached);
            Assert.True(WindowInteropService.IsNativeWindowValid(handle));
            hud.AllowClose = true;
            hud.Close();
        });
    }

    [Fact]
    public void HudClosed_RemovesHookExactlyOnceAndInvalidatesHandle()
    {
        RunSta(() =>
        {
            var logger = new RecordingLogger();
            var hud = CreateInvisibleHud();
            var interop = new WindowInteropService(hud, () => false, logger);
            interop.Initialize();
            hud.Show();
            hud.AllowClose = true;

            hud.Close();
            interop.Dispose();
            interop.Dispose();

            Assert.False(interop.IsHookAttached);
            Assert.Equal(1, logger.InformationMessages.Count(entry => entry.Contains(
                "hook attached",
                StringComparison.Ordinal)));
            Assert.Equal(1, logger.InformationMessages.Count(entry => entry.Contains(
                "hook removed",
                StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void GearRoutedEvent_IsMarkedHandledBeforeReachingWindow()
    {
        RunSta(() =>
        {
            var hud = new HudWindow();
            bool? handledAtWindow = null;
            hud.AddHandler(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                new RoutedEventHandler((_, eventArgs) => handledAtWindow = eventArgs.Handled),
                true);

            hud.HeaderSettingsButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.True(handledAtWindow);
            hud.AllowClose = true;
            hud.Close();
        });
    }

    [Theory]
    [InlineData((int)SettingsOpenSource.HudGear, (int)SettingsCategory.Hud)]
    [InlineData((int)SettingsOpenSource.Tray, -1)]
    [InlineData((int)SettingsOpenSource.ConnectionGate, -1)]
    public void EverySettingsOpenSource_OnlyShowsWindow(int sourceValue, int categoryValue)
    {
        var window = new FakeSettingsWindow();
        using var service = new SettingsWindowService(
            new RecordingDispatcher(),
            () => window,
            NullAppLogger.Instance);
        var category = categoryValue < 0 ? null : (SettingsCategory?)categoryValue;

        Assert.True(service.Open((SettingsOpenSource)sourceValue, category));
        Assert.Equal(1, window.ShowCount);
        Assert.Equal(0, window.CloseCount);
    }

    [Fact]
    public void ClosedEvent_ClearsCurrentSettingsWindow()
    {
        var window = new FakeSettingsWindow();
        using var service = new SettingsWindowService(
            new RecordingDispatcher(),
            () => window,
            NullAppLogger.Instance);
        service.Open(SettingsOpenSource.Tray, null);

        window.RaiseClosed();

        Assert.Null(service.CurrentWindow);
    }

    [Fact]
    public void InteropDispose_RemovesHookButLeavesWpfWindowHandleAlive()
    {
        RunSta(() =>
        {
            var hud = CreateInvisibleHud();
            var interop = new WindowInteropService(hud, () => false, NullAppLogger.Instance);
            interop.Initialize();
            hud.Show();
            var handle = interop.Handle;

            interop.Dispose();

            Assert.False(interop.IsHookAttached);
            Assert.True(WindowInteropService.IsNativeWindowValid(handle));
            hud.AllowClose = true;
            hud.Close();
        });
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
        thread.Join();
        Assert.Null(failure);
    }

    private static HudWindow CreateInvisibleHud() => new()
    {
        Left = -10000,
        Top = -10000,
        Opacity = 0,
        ShowInTaskbar = false,
    };

    private static string Source(params string[] parts) => Path.Combine(
        new[] { RepositoryRoot, "src", "GachaOverlay.App" }.Concat(parts).ToArray());

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        public bool HasAccess { get; init; } = true;

        public bool HasShutdownStarted { get; init; }

        public int InvokeCount { get; private set; }

        public int BeginInvokeCount { get; private set; }

        public bool CheckAccess() => HasAccess;

        public void Invoke(Action action)
        {
            InvokeCount++;
            action();
        }

        public void BeginInvoke(Action action)
        {
            BeginInvokeCount++;
            action();
        }
    }

    private sealed class FakeSettingsWindow : ISettingsWindowHandle
    {
        public event EventHandler? Closed;

        public bool IsLoaded { get; private set; }

        public bool IsVisible { get; private set; }

        public IntPtr NativeHandle => IntPtr.Zero;

        public int ShowCount { get; private set; }

        public int CloseCount { get; private set; }

        public List<SettingsCategory?> Categories { get; } = new();

        public void ShowAndActivate(SettingsCategory? category = null)
        {
            ShowCount++;
            IsLoaded = true;
            IsVisible = true;
            Categories.Add(category);
        }

        public void Hide() => IsVisible = false;

        public void CloseForApplicationExit()
        {
            CloseCount++;
            IsVisible = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseClosed()
        {
            IsVisible = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class SmokeSettingsWindow : Window, ISettingsWindowHandle
    {
        public SmokeSettingsWindow() => Content = new TextBlock { Text = "Settings" };

        public IntPtr NativeHandle => new WindowInteropHelper(this).Handle;

        public void ShowAndActivate(SettingsCategory? category = null)
        {
            if (!IsVisible)
            {
                Show();
            }

            Activate();
        }

        public void CloseForApplicationExit() => Close();
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Errors { get; } = new();

        public List<string> InformationMessages { get; } = new();

        public void Information(string category, string message) =>
            InformationMessages.Add($"{category}:{message}");

        public void Warning(string category, string message)
        {
        }

        public void Error(string category, string message, Exception? exception = null) =>
            Errors.Add($"{category}:{message}:{exception?.Message}");
    }
}

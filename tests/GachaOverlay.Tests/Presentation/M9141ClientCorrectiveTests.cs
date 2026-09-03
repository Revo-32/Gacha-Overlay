using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using GachaOverlay.Core.Settings;
using GachaOverlay.App.Lifecycle;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Diagnostics;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Presentation;

[Collection(WpfApplicationCollection.Name)]
public sealed class M9141ClientCorrectiveTests
{
    [Fact]
    public async Task Actual_application_snapshot_exports_valid_json()
    {
        using var temporary = new TemporaryDirectory();
        // Do not Start: exercise the actual snapshot builder without user data or networking.
        var host = new ApplicationHost(null!, () => { });
        var request = host.BuildDiagnosticRequest(temporary.File("snapshot.zip"));
        var result = await new DiagnosticBundleExporter(NullAppLogger.Instance).ExportAsync(request);
        Assert.True(result.IsSuccess, $"{result.FailureStage}/{result.FailureType}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rendered_gear_center_belongs_to_button(bool minimal) => RunSta(() =>
    {
        var window = new HudWindow
        {
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            DataContext = new { IsHudChromeVisible = !minimal, IsFloatingEditStripVisible = minimal, IsUnlocked = true },
        };
        try
        {
            var tokens = new ResourceDictionary
            {
                Source = new Uri("/GachaOverlay.App;component/Themes/DesignTokens.xaml", UriKind.Relative),
            };
            window.Resources.MergedDictionaries.Add(tokens);
            window.Show();
            window.UpdateLayout();
            var button = minimal ? window.FloatingSettingsButton : window.HeaderSettingsButton;
            var center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
            var hit = window.InputHitTest(center) as DependencyObject;
            Assert.True(WpfHitTestAncestry.HasInteractiveAncestor(hit), $"Hit {hit?.GetType().Name}, center {center}");
        }
        finally
        {
            window.AllowClose = true;
            window.Close();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lock_unlock_hide_show_preserve_gear_but_block_locked_mouse_input(bool minimal) => RunSta(() =>
    {
        var window = new HudWindow { Left = -10000, Top = -10000, ShowActivated = false };
        var locked = false;
        using var interop = new WindowInteropService(window, () => locked, NullAppLogger.Instance);
        try
        {
            interop.Initialize();
            var requests = 0;
            window.SettingsRequested += () => requests++;
            for (var i = 0; i < 4; i++)
            {
                locked = i % 2 == 0;
                window.DataContext = new { IsUnlocked = !locked, IsHudChromeVisible = !minimal, IsFloatingEditStripVisible = minimal && !locked };
                Assert.True(interop.ApplyClickThrough(locked));
                window.Show();
                window.UpdateLayout();
                var button = minimal ? window.FloatingSettingsButton : window.HeaderSettingsButton;
                Assert.Equal(!locked, button.IsEnabled);
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                });
                window.Hide();
            }
            Assert.Equal(2, requests);
            Assert.True(interop.IsHookAttached);
        }
        finally { window.AllowClose = true; window.Close(); }
    });

    // Runs inside the suite's single Application scope so the real window has
    // the same application-level StaticResources as the published executable.
    internal static void AssertSettingsReuse()
    {
        var created = 0;
        using var service = new SettingsWindowService(new UiDispatcherAdapter(Dispatcher.CurrentDispatcher), () =>
        {
            created++;
            return new FoundationWindow { Left = -10000, Top = -10000, ShowActivated = false };
        }, NullAppLogger.Instance);
        Assert.True(service.Open(SettingsOpenSource.Tray, null));
        var window = Assert.IsType<FoundationWindow>(service.CurrentWindow);
        var handle = new WindowInteropHelper(window).Handle;
        window.Close(); // Real OnClosing hides rather than destroys the cached window.
        Assert.False(window.IsVisible);
        Assert.True(service.Open(SettingsOpenSource.HudGear, SettingsCategory.Hud));
        window.WindowState = WindowState.Minimized;
        Assert.True(service.Open(SettingsOpenSource.HudGear, SettingsCategory.Hud));
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.True(service.Open(SettingsOpenSource.Tray, null));
        Assert.Same(window, service.CurrentWindow);
        Assert.Equal(handle, new WindowInteropHelper(window).Handle);
        Assert.Equal(1, created);
    }

    [Fact]
    public void Default_release_click_loses_the_request_when_capture_did_not_survive() => RunSta(() =>
    {
        // Regression control: former ButtonBase release path cannot navigate from
        // an uncaptured/inactive click. The production preview tests cover the fix.
        var button = new Button();
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
        });
        Assert.Equal(0, clicks);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gear_mouse_down_does_not_require_activation_or_mouse_capture(bool minimal) => RunSta(() =>
    {
        var window = new HudWindow();
        try
        {
            var requests = 0;
            var drags = 0;
            window.SettingsRequested += () => requests++;
            window.DragRequested += () => drags++;
            var button = minimal ? window.FloatingSettingsButton : window.HeaderSettingsButton;
            Assert.False(window.IsActive);
            Assert.False(button.IsMouseCaptured);
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            });
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            });
            Assert.Equal(1, requests);
            Assert.Equal(0, drags);
            Assert.False(button.IsMouseCaptured);
            Assert.False(window.IsActive);
        }
        finally { window.AllowClose = true; window.Close(); }
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

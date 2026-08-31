using System.Windows;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Services;

internal enum SettingsOpenSource
{
    HudGear,
    Tray,
    ConnectionGate,
}

internal interface ISettingsWindowHandle
{
    event EventHandler? Closed;

    bool IsLoaded { get; }

    bool IsVisible { get; }

    IntPtr NativeHandle { get; }

    void ShowAndActivate(SettingsCategory? category = null);

    void Hide();

    void CloseForApplicationExit();
}

internal sealed class SettingsWindowService : IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<ISettingsWindowHandle> _windowFactory;
    private readonly IAppLogger _logger;
    private readonly Action _showOpenFailure;
    private ISettingsWindowHandle? _window;
    private bool _applicationExiting;
    private bool _disposed;

    public SettingsWindowService(
        IUiDispatcher dispatcher,
        Func<ISettingsWindowHandle> windowFactory,
        IAppLogger logger,
        Action? showOpenFailure = null)
    {
        _dispatcher = dispatcher;
        _windowFactory = windowFactory;
        _logger = logger;
        _showOpenFailure = showOpenFailure ?? (() => { });
    }

    public ISettingsWindowHandle? CurrentWindow => _window;

    public bool Open(SettingsOpenSource source, SettingsCategory? requestedCategory)
    {
        if (_applicationExiting || _disposed || _dispatcher.HasShutdownStarted)
        {
            _logger.Warning(
                "WINDOW",
                $"Settings open ignored source={source} category={FormatCategory(requestedCategory)} state=ApplicationExiting.");
            return false;
        }

        var opened = false;
        void OpenOnUi()
        {
            if (_applicationExiting || _disposed)
            {
                return;
            }

            _logger.Information(
                "WINDOW",
                $"Settings open requested source={source} category={FormatCategory(requestedCategory)}.");
            try
            {
                if (_window is null)
                {
                    _window = _windowFactory();
                    _window.Closed += OnWindowClosed;
                    _logger.Information("WINDOW", "Settings window created.");
                }

                _window.ShowAndActivate(requestedCategory);
                opened = true;
                _logger.Information(
                    "WINDOW",
                    $"Settings shown source={source} category={FormatCategory(requestedCategory)} hwnd={FormatHandle(_window.NativeHandle)} valid={IsHandleValid(_window.NativeHandle)}.");
            }
            catch (Exception exception)
            {
                _logger.Error(
                    "WINDOW",
                    $"Settings open failed source={source} category={FormatCategory(requestedCategory)} lifecycle={DescribeWindow()}.",
                    exception);
                TryShowOpenFailure();
            }
        }

        try
        {
            if (_dispatcher.CheckAccess())
            {
                OpenOnUi();
            }
            else
            {
                _dispatcher.Invoke(OpenOnUi);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(
                "WINDOW",
                $"Settings UI dispatch failed source={source} category={FormatCategory(requestedCategory)} lifecycle={DescribeWindow()}.",
                exception);
            TryShowOpenFailure();
        }

        return opened;
    }

    public void Hide()
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        void HideOnUi()
        {
            if (_window is null || !_window.IsVisible)
            {
                return;
            }

            _window.Hide();
            _logger.Information("WINDOW", "Settings hidden reason=User.");
        }

        if (_dispatcher.CheckAccess())
        {
            HideOnUi();
        }
        else
        {
            _dispatcher.Invoke(HideOnUi);
        }
    }

    public void PrepareForApplicationExit() => _applicationExiting = true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _applicationExiting = true;
        if (_dispatcher.HasShutdownStarted)
        {
            _window = null;
            return;
        }

        void CloseOnUi()
        {
            var window = _window;
            _window = null;
            if (window is null)
            {
                return;
            }

            window.Closed -= OnWindowClosed;
            window.CloseForApplicationExit();
            _logger.Information("WINDOW", "Settings closed reason=ApplicationExit.");
        }

        if (_dispatcher.CheckAccess())
        {
            CloseOnUi();
        }
        else
        {
            _dispatcher.Invoke(CloseOnUi);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        if (ReferenceEquals(sender, _window))
        {
            _window!.Closed -= OnWindowClosed;
            _window = null;
        }

        _logger.Information("WINDOW", "Settings Closed event observed.");
    }

    private void TryShowOpenFailure()
    {
        try
        {
            _showOpenFailure();
        }
        catch (Exception exception)
        {
            _logger.Error("WINDOW", "Settings open failure notification could not be shown.", exception);
        }
    }

    private string DescribeWindow() => _window is null
        ? "NotCreated"
        : $"Loaded={_window.IsLoaded},Visible={_window.IsVisible},Hwnd={FormatHandle(_window.NativeHandle)},Valid={IsHandleValid(_window.NativeHandle)}";

    private static string FormatCategory(SettingsCategory? category) =>
        category?.ToString() ?? "LastVisited";

    private static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";

    private static bool IsHandleValid(IntPtr handle) =>
        handle != IntPtr.Zero && WindowInteropService.IsNativeWindowValid(handle);
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GachaOverlay.Core.Hud.Geometry;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.App.Services;

internal sealed class WindowInteropService : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmHotkey = 0x0312;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int MaNoActivate = 3;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Window _window;
    private readonly Func<bool> _isLocked;
    private readonly IAppLogger _logger;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _hookAttached;
    private bool _closedSubscribed;
    private bool _disposed;

    public WindowInteropService(Window window, Func<bool> isLocked, IAppLogger logger)
    {
        _window = window;
        _isLocked = isLocked;
        _logger = logger;
    }

    public event Action<int>? HotkeyPressed;

    public event Action? DisplayTopologyChanged;

    public IntPtr Handle => IsNativeWindowValid(_handle) ? _handle : IntPtr.Zero;

    internal bool IsHookAttached => _hookAttached;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hookAttached && IsNativeWindowValid(_handle))
        {
            return;
        }

        DetachBorrowedHook();
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        if (!IsNativeWindowValid(_handle))
        {
            _handle = IntPtr.Zero;
            throw new Win32Exception(1400, "The HUD HWND is invalid after initialization.");
        }

        _source = HwndSource.FromHwnd(_handle)
            ?? throw new InvalidOperationException("The HUD HWND source is unavailable.");
        _source.AddHook(WindowProcedure);
        _hookAttached = true;
        if (!_closedSubscribed)
        {
            _window.Closed += OnWindowClosed;
            _closedSubscribed = true;
        }

        _logger.Information("WINDOW", $"HUD hook attached hwnd=0x{_handle.ToInt64():X} ownership=Borrowed.");
        ApplyToolWindowStyle();
    }

    public bool ApplyClickThrough(bool enabled)
    {
        if (!TryGetValidHandle("click-through update", out var handle))
        {
            return false;
        }

        try
        {
            var current = GetExtendedStyle(handle);
            var next = enabled
                ? current | WsExTransparent | WsExToolWindow
                : (current & ~WsExTransparent) | WsExToolWindow;
            if (next != current)
            {
                SetExtendedStyle(handle, next);
                EnsureNativeSuccess(
                    SetWindowPos(
                        handle,
                        IntPtr.Zero,
                        0,
                        0,
                        0,
                        0,
                        SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged),
                    "The HUD extended style could not be refreshed.");
            }

            return true;
        }
        catch (Exception exception)
        {
            _logger.Error("HUD", "Click-through style update failed.", exception);
            return false;
        }
    }

    public bool ApplyTopmost()
    {
        if (!TryGetValidHandle("topmost update", out var handle))
        {
            return false;
        }

        try
        {
            EnsureNativeSuccess(
                SetWindowPos(
                    handle,
                    HwndTopmost,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpNoActivate),
                "The HUD could not be placed in the topmost band.");
            return true;
        }
        catch (Exception exception)
        {
            _logger.Error("HUD", "Topmost update failed.", exception);
            return false;
        }
    }

    public bool SetWindowBounds(HudWindowGeometry geometry)
    {
        if (!TryGetValidHandle("bounds update", out var handle))
        {
            return false;
        }

        try
        {
            EnsureNativeSuccess(
                SetWindowPos(
                    handle,
                    IntPtr.Zero,
                    RoundToInt(geometry.X),
                    RoundToInt(geometry.Y),
                    Math.Max(1, RoundToInt(geometry.Width)),
                    Math.Max(1, RoundToInt(geometry.Height)),
                    SwpNoZOrder | SwpNoActivate),
                "The HUD bounds could not be applied.");
            return true;
        }
        catch (Exception exception)
        {
            _logger.Error("HUD", "Window placement update failed.", exception);
            return false;
        }
    }

    public bool SetWindowLocation(double x, double y)
    {
        if (!TryGetValidHandle("location update", out var handle))
        {
            return false;
        }

        return SetWindowPos(
            handle,
            IntPtr.Zero,
            RoundToInt(x),
            RoundToInt(y),
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    public bool TryGetWindowRectangle(out HudRectangle rectangle)
    {
        rectangle = default;
        if (!TryGetValidHandle("rectangle read", out var handle) ||
            !GetWindowRect(handle, out var native))
        {
            return false;
        }

        rectangle = new HudRectangle(
            native.Left,
            native.Top,
            native.Right - native.Left,
            native.Bottom - native.Top);
        return rectangle.IsFiniteAndPositive;
    }

    public double GetWindowDpi()
    {
        if (!TryGetValidHandle("DPI read", out var handle))
        {
            return 96;
        }

        try
        {
            var dpi = GetDpiForWindow(handle);
            return dpi is >= 48 and <= 768 ? dpi : 96;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_closedSubscribed)
        {
            _window.Closed -= OnWindowClosed;
            _closedSubscribed = false;
        }

        DetachBorrowedHook();
    }

    private IntPtr WindowProcedure(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotkey)
        {
            HotkeyPressed?.Invoke(wParam.ToInt32());
            return IntPtr.Zero;
        }

        // The HUD is intentionally shown without activation. When GTA owns the
        // foreground, WPF/Windows can otherwise consume the first click only to
        // activate this window. MA_NOACTIVATE keeps GTA foreground and, unlike
        // MA_NOACTIVATEANDEAT, still delivers that same click for drag/resize.
        if (message == WmMouseActivate && !_isLocked())
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        if (message is WmDisplayChange or WmDpiChanged)
        {
            DisplayTopologyChanged?.Invoke();
            return IntPtr.Zero;
        }

        if (message != WmNcHitTest || _isLocked() || _window.WindowState != WindowState.Normal)
        {
            return IntPtr.Zero;
        }

        if (!GetWindowRect(hwnd, out var bounds))
        {
            return IntPtr.Zero;
        }

        var packed = lParam.ToInt64();
        var x = unchecked((short)(packed & 0xFFFF));
        var y = unchecked((short)((packed >> 16) & 0xFFFF));
        if (IsInteractiveAt(new System.Windows.Point(x, y)))
        {
            return IntPtr.Zero;
        }

        var region = HudResizeHitTestPolicy.Resolve(
            x - bounds.Left,
            y - bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            GetWindowDpi());
        var result = region switch
        {
            HudResizeRegion.TopLeft => HtTopLeft,
            HudResizeRegion.TopRight => HtTopRight,
            HudResizeRegion.BottomLeft => HtBottomLeft,
            HudResizeRegion.BottomRight => HtBottomRight,
            HudResizeRegion.Left => HtLeft,
            HudResizeRegion.Right => HtRight,
            HudResizeRegion.Top => HtTop,
            HudResizeRegion.Bottom => HtBottom,
            _ => 0,
        };

        if (result != 0)
        {
            handled = true;
            return new IntPtr(result);
        }

        return IntPtr.Zero;
    }

    private bool IsInteractiveAt(System.Windows.Point screenPoint)
    {
        var clientPoint = _window.PointFromScreen(screenPoint);
        var current = _window.InputHitTest(clientPoint) as DependencyObject;
        return WpfHitTestAncestry.HasInteractiveAncestor(current);
    }

    private void ApplyToolWindowStyle()
    {
        if (!TryGetValidHandle("tool-window style update", out var handle))
        {
            return;
        }

        try
        {
            var current = GetExtendedStyle(handle);
            SetExtendedStyle(handle, current | WsExToolWindow);
        }
        catch (Exception exception)
        {
            _logger.Error("HUD", "Tool-window style update failed.", exception);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        _logger.Information("WINDOW", $"HUD Closed event hwnd=0x{_handle.ToInt64():X}.");
        DetachBorrowedHook();
    }

    private void DetachBorrowedHook()
    {
        if (_hookAttached && _source is not null)
        {
            try
            {
                _source.RemoveHook(WindowProcedure);
                _logger.Information(
                    "WINDOW",
                    $"HUD hook removed hwnd=0x{_handle.ToInt64():X} ownership=Borrowed.");
            }
            catch (InvalidOperationException exception)
            {
                _logger.Warning(
                    "WINDOW",
                    $"HUD hook was already unavailable during cleanup: {exception.Message}");
            }
        }

        // HwndSource.FromHwnd returns a borrowed WPF-owned source. Never Dispose it.
        _hookAttached = false;
        _source = null;
        _handle = IntPtr.Zero;
    }

    private bool TryGetValidHandle(string operation, out IntPtr handle)
    {
        handle = _handle;
        if (IsNativeWindowValid(handle))
        {
            return true;
        }

        if (handle != IntPtr.Zero)
        {
            _logger.Warning(
                "WINDOW",
                $"HUD {operation} skipped because hwnd=0x{handle.ToInt64():X} is invalid.");
        }

        handle = IntPtr.Zero;
        return false;
    }

    internal static bool IsNativeWindowValid(IntPtr handle) =>
        handle != IntPtr.Zero && IsWindow(handle);

    private static long GetExtendedStyle(IntPtr handle) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(handle, GwlExStyle).ToInt64()
            : GetWindowLong32(handle, GwlExStyle);

    private static void SetExtendedStyle(IntPtr handle, long style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = IntPtr.Size == 8
            ? SetWindowLongPtr64(handle, GwlExStyle, new IntPtr(style))
            : new IntPtr(SetWindowLong32(handle, GwlExStyle, unchecked((int)style)));
        if (previous == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static int RoundToInt(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static void EnsureNativeSuccess(bool success, string message)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), message);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

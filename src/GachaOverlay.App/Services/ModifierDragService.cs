using System.Diagnostics;
using System.Runtime.InteropServices;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.App.Services;

internal sealed class ModifierDragService : IDisposable
{
    private const int WhMouseLowLevel = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int VirtualKeyAlt = 0x12;

    private readonly WindowInteropService _interop;
    private readonly IAppLogger _logger;
    private readonly MouseHookDelegate _callback;
    private IntPtr _hook;
    private bool _dragging;
    private NativePoint _startCursor;
    private double _startX;
    private double _startY;
    private bool _disposed;

    public ModifierDragService(WindowInteropService interop, IAppLogger logger)
    {
        _interop = interop;
        _logger = logger;
        _callback = OnMouseHook;
    }

    public event Action? DragCompleted;

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled && _hook == IntPtr.Zero)
        {
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            var moduleHandle = module?.ModuleName is { Length: > 0 } name
                ? GetModuleHandle(name)
                : IntPtr.Zero;
            _hook = SetWindowsHookEx(WhMouseLowLevel, _callback, moduleHandle, 0);
            if (_hook == IntPtr.Zero)
            {
                _logger.Warning(
                    "HUD",
                    $"Optional modifier-drag hook could not be enabled error={Marshal.GetLastPInvokeError()}.");
            }
        }
        else if (!enabled && _hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _dragging = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SetEnabled(false);
        _disposed = true;
    }

    private IntPtr OnMouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _hook != IntPtr.Zero)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<LowLevelMouseData>(lParam);
            if (message == WmLeftButtonDown &&
                GetAsyncKeyState(VirtualKeyAlt) < 0 &&
                _interop.TryGetWindowRectangle(out var bounds) &&
                data.Point.X >= bounds.X &&
                data.Point.X <= bounds.Right &&
                data.Point.Y >= bounds.Y &&
                data.Point.Y <= bounds.Bottom)
            {
                _dragging = true;
                _startCursor = data.Point;
                _startX = bounds.X;
                _startY = bounds.Y;
            }
            else if (message == WmMouseMove && _dragging)
            {
                _interop.SetWindowLocation(
                    _startX + data.Point.X - _startCursor.X,
                    _startY + data.Point.Y - _startCursor.Y);
            }
            else if (message == WmLeftButtonUp && _dragging)
            {
                _dragging = false;
                DragCompleted?.Invoke();
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private delegate IntPtr MouseHookDelegate(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        MouseHookDelegate callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);
}

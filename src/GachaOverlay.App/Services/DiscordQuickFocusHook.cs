using System.Runtime.InteropServices;
using System.Windows.Threading;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.App.Services;

/// <summary>Owned by GlobalHotkeyService. Callback never enumerates Discord or waits on the UI.</summary>
internal sealed class DiscordQuickFocusHook : IDisposable
{
    private readonly DiscordQuickFocusPolicy _policy = new();
    private readonly IDiscordForegroundService _foreground;
    private readonly Dispatcher _ui;
    private readonly IAppLogger _logger;
    private readonly HookCallback _callback;
    private readonly Thread _thread;
    private Dispatcher? _hookDispatcher;
    private nint _hook;
    private volatile bool _disposed;
    private volatile bool _enabled;
    private int _activationPending;

    public DiscordQuickFocusHook(Dispatcher ui, IAppLogger logger, IDiscordForegroundService? foreground = null)
    {
        _ui = ui;
        _logger = logger;
        _foreground = foreground ?? new DiscordForegroundService();
        _callback = OnKeyboard;
        _thread = new Thread(Run) { IsBackground = true, Name = "LS Overlay quick focus" };
        _thread.Start();
    }

    public void SetEnabled(bool enabled) => _enabled = enabled;

    private void Run()
    {
        _hookDispatcher = Dispatcher.CurrentDispatcher;
        if (_disposed) return;
        _policy.Reset((GetAsyncKeyState(0x54) & 0x8000) != 0);
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == 0) { _logger.Warning("HOTKEY", "Quick Discord focus unavailable; other shortcuts remain active."); return; }
        try
        {
            if (!_disposed) Dispatcher.Run();
        }
        finally
        {
            if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
            _policy.Reset();
        }
    }

    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code < 0 || _disposed) return CallNextHookEx(_hook, code, message, data);
        var key = Marshal.PtrToStructure<KeyboardData>(data);
        if (key.VirtualKey != 0x54) return CallNextHookEx(_hook, code, message, data);
        var down = message == 0x100 || message == 0x104;
        var up = message == 0x101 || message == 0x105;
        if (!down && !up) return CallNextHookEx(_hook, code, message, data);
        try
        {
            var modifiers = Pressed(0x10) || Pressed(0x11) || Pressed(0x12) || Pressed(0x5B) || Pressed(0x5C);
            var decision = _policy.HandleT(down, down && _enabled && _foreground.IsGtaEnhancedForeground(),
                modifiers, (key.Flags & 0x10) != 0, _enabled);
            if (decision.RequestFocus && Interlocked.Exchange(ref _activationPending, 1) == 0)
                _ui.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    try { if (!_disposed && _enabled && _foreground.IsGtaEnhancedForeground()) _foreground.TryActivateDiscord(); }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    finally { Interlocked.Exchange(ref _activationPending, 0); }
                });
            if (decision.Consume) return 1; // includes repeat and physical key-up after foreground changes
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Foreground races are safe no-ops; never log keys, titles or user input.
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private static bool Pressed(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        _hookDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }
    private delegate nint HookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
}

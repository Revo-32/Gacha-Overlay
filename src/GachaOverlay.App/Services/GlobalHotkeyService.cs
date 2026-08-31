using System.ComponentModel;
using System.Runtime.InteropServices;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Services;

internal sealed class GlobalHotkeyService : IGlobalHotkeyRegistrar, IDisposable
{
    private const int LockToggleId = 0x5A01;
    private const int VisibilityToggleId = 0x5A02;
    private const uint ModNoRepeat = 0x4000;

    private readonly WindowInteropService _interop;
    private readonly IAppLogger _logger;
    private readonly HotkeyBindingManager _bindings;
    private bool _disposed;

    public GlobalHotkeyService(WindowInteropService interop, IAppLogger logger)
    {
        _interop = interop;
        _logger = logger;
        _bindings = new HotkeyBindingManager(this);
        _interop.HotkeyPressed += OnHotkeyPressed;
    }

    public event Action? LockToggleRequested;

    public event Action? VisibilityToggleRequested;

    public bool Bind(AppSettings settings) =>
        TryBind(settings.HudLockHotkey, settings.HudVisibilityHotkey);

    public bool TryBind(HotkeySetting lockSetting, HotkeySetting visibilitySetting)
    {
        var plan = CreateRegistrationPlan(lockSetting, visibilitySetting);
        var lockGesture = plan.LockToggle;
        var visibilityGesture = plan.VisibilityToggle;
        if (lockGesture == visibilityGesture)
        {
            _logger.Warning("HOTKEY", "Duplicate LockToggle and VisibilityToggle gestures were rejected.");
            return false;
        }

        var previousVisibility = _bindings.GetActiveGesture(VisibilityToggleId);
        var previousLock = _bindings.GetActiveGesture(LockToggleId);
        var visibilityResult = _bindings.Rebind(VisibilityToggleId, visibilityGesture);
        if (!visibilityResult.Success)
        {
            LogBindingFailure("VisibilityToggle", visibilityResult);
            return false;
        }

        var lockResult = _bindings.Rebind(LockToggleId, lockGesture);
        if (!lockResult.Success)
        {
            RestoreBinding(VisibilityToggleId, previousVisibility);
            LogBindingFailure("LockToggle", lockResult);
            return false;
        }

        _logger.Information("HOTKEY", $"Registered VisibilityToggle gesture={visibilityGesture}.");
        _logger.Information("HOTKEY", $"Registered LockToggle gesture={lockGesture}.");
        return true;
    }

    internal static HudHotkeyRegistrationPlan CreateRegistrationPlan(
        HotkeySetting lockSetting,
        HotkeySetting visibilitySetting) =>
        new(
            ParseOrDefault(visibilitySetting, HotkeySetting.DefaultVisibilityToggle),
            ParseOrDefault(lockSetting, HotkeySetting.DefaultLockToggle));

    public bool TryRegister(int id, HotkeyGesture gesture)
    {
        var handle = _interop.Handle;
        if (handle == IntPtr.Zero)
        {
            _logger.Warning("HOTKEY", $"Registration skipped id={id}; HUD HWND is unavailable.");
            return false;
        }

        var registered = RegisterHotKey(
            handle,
            id,
            unchecked((uint)gesture.Modifiers) | ModNoRepeat,
            unchecked((uint)gesture.VirtualKey));
        if (!registered)
        {
            _logger.Warning(
                "HOTKEY",
                $"Registration failed id={id} gesture={gesture} error={Marshal.GetLastPInvokeError()}.");
        }

        return registered;
    }

    public bool TryUnregister(int id)
    {
        var handle = _interop.Handle;
        if (handle == IntPtr.Zero)
        {
            return true;
        }

        var removed = UnregisterHotKey(handle, id);
        var error = removed ? 0 : Marshal.GetLastPInvokeError();
        if (!removed)
        {
            if (error != 1419)
            {
                _logger.Warning("HOTKEY", $"Unregister failed id={id} error={error}.");
            }
        }

        return removed || error == 1419;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _interop.HotkeyPressed -= OnHotkeyPressed;
        _bindings.Dispose();
    }

    private static HotkeyGesture ParseOrDefault(
        HotkeySetting configured,
        HotkeySetting fallback)
    {
        if (!HotkeyGesture.TryParse(configured, out var gesture))
        {
            HotkeyGesture.TryParse(fallback, out gesture);
        }

        return gesture;
    }

    private void RestoreBinding(int id, HotkeyGesture? previous)
    {
        if (previous is null)
        {
            _bindings.Unbind(id);
            return;
        }

        _bindings.Rebind(id, previous.Value);
    }

    private void LogBindingFailure(string name, HotkeyRebindResult result)
    {
        var active = result.ActiveGesture?.ToString() ?? "none";
        _logger.Warning(
            "HOTKEY",
            $"Binding {name} failed; active={active} previous_restored={result.PreviousBindingRestored}.");
    }

    private void OnHotkeyPressed(int id)
    {
        if (id == LockToggleId)
        {
            _logger.Information("HOTKEY", "Trigger LockToggle.");
            LockToggleRequested?.Invoke();
        }
        else if (id == VisibilityToggleId)
        {
            _logger.Information("HOTKEY", "Trigger VisibilityToggle.");
            VisibilityToggleRequested?.Invoke();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(
        IntPtr hwnd,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

internal sealed record HudHotkeyRegistrationPlan(
    HotkeyGesture VisibilityToggle,
    HotkeyGesture LockToggle);

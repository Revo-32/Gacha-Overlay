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
    private const int PreviousChannelId = 0x5A03;
    private const int NextChannelId = 0x5A04;
    private AppSettings _lastSettings = AppSettings.CreateDefault();
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

    public event Action<int>? ChannelStepRequested;

    public bool Bind(AppSettings settings)
    {
        if (_disposed) return false;
        var plan = CreateRegistrationPlan(settings.HudLockHotkey, settings.HudVisibilityHotkey);
        if (!TryOptional(settings.PreviousMainChannelHotkey, out var previous) ||
            !TryOptional(settings.NextMainChannelHotkey, out var next)) return false;
        var desired = new Dictionary<int, HotkeyGesture?>
        {
            [VisibilityToggleId] = plan.VisibilityToggle,
            [LockToggleId] = plan.LockToggle,
            [PreviousChannelId] = previous,
            [NextChannelId] = next,
        };
        var assigned = desired.Values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        if (assigned.Distinct().Count() != assigned.Length) return false;
        var old = desired.Keys.ToDictionary(id => id, _bindings.GetActiveGesture);
        // Release changed bindings together so swapping two configured actions is safe.
        foreach (var pair in desired.Where(pair => old[pair.Key] != pair.Value))
        {
            if (!_bindings.Unbind(pair.Key)) { RestoreAll(old); return false; }
        }
        foreach (var pair in desired)
        {
            if (pair.Value is { } gesture && !_bindings.Rebind(pair.Key, gesture).Success)
            { RestoreAll(old); return false; }
        }
        _lastSettings = settings;
        return true;
    }

    public bool TryBind(HotkeySetting lockSetting, HotkeySetting visibilitySetting) =>
        Bind(_lastSettings with { HudLockHotkey = lockSetting, HudVisibilityHotkey = visibilitySetting });

    private static bool TryOptional(HotkeySetting? setting, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (setting is null || string.IsNullOrWhiteSpace(setting.Key)) return true;
        if (!HotkeyGesture.TryParse(setting, out var parsed)) return false;
        gesture = parsed;
        return true;
    }

    private void RestoreAll(IReadOnlyDictionary<int, HotkeyGesture?> previous)
    {
        foreach (var id in previous.Keys) _bindings.Unbind(id);
        foreach (var pair in previous) RestoreBinding(pair.Key, pair.Value);
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
        if (_disposed) return;
        if (id == PreviousChannelId) { ChannelStepRequested?.Invoke(-1); return; }
        if (id == NextChannelId) { ChannelStepRequested?.Invoke(1); return; }
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

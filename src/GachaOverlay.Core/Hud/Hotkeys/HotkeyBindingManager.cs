namespace GachaOverlay.Core.Hud.Hotkeys;

public interface IGlobalHotkeyRegistrar
{
    bool TryRegister(int id, HotkeyGesture gesture);

    bool TryUnregister(int id);
}

public sealed record HotkeyRebindResult(
    bool Success,
    HotkeyGesture? ActiveGesture,
    bool PreviousBindingRestored);

public sealed class HotkeyBindingManager : IDisposable
{
    private readonly object _sync = new();
    private readonly IGlobalHotkeyRegistrar _registrar;
    private readonly Dictionary<int, HotkeyGesture> _active = new();
    private bool _disposed;

    public HotkeyBindingManager(IGlobalHotkeyRegistrar registrar)
    {
        _registrar = registrar;
    }

    public HotkeyGesture? GetActiveGesture(int id)
    {
        lock (_sync)
        {
            return _active.TryGetValue(id, out var gesture) ? gesture : null;
        }
    }

    public HotkeyRebindResult Rebind(int id, HotkeyGesture gesture)
    {
        if (!gesture.IsValid)
        {
            return new HotkeyRebindResult(false, GetActiveGesture(id), false);
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active.TryGetValue(id, out var previous) && previous == gesture)
            {
                return new HotkeyRebindResult(true, previous, false);
            }

            var hadPrevious = _active.Remove(id, out previous);
            if (hadPrevious && !SafeUnregister(id))
            {
                _active[id] = previous;
                return new HotkeyRebindResult(false, previous, true);
            }

            if (SafeRegister(id, gesture))
            {
                _active[id] = gesture;
                return new HotkeyRebindResult(true, gesture, false);
            }

            if (hadPrevious && SafeRegister(id, previous))
            {
                _active[id] = previous;
                return new HotkeyRebindResult(false, previous, true);
            }

            return new HotkeyRebindResult(false, null, false);
        }
    }

    public bool Unbind(int id)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_active.TryGetValue(id, out var previous))
            {
                return true;
            }

            if (!SafeUnregister(id))
            {
                return false;
            }

            _active.Remove(id);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var id in _active.Keys.ToArray())
            {
                SafeUnregister(id);
            }

            _active.Clear();
        }
    }

    private bool SafeRegister(int id, HotkeyGesture gesture)
    {
        try
        {
            return _registrar.TryRegister(id, gesture);
        }
        catch
        {
            return false;
        }
    }

    private bool SafeUnregister(int id)
    {
        try
        {
            return _registrar.TryUnregister(id);
        }
        catch
        {
            return false;
        }
    }
}

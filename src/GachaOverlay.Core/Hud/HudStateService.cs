namespace GachaOverlay.Core.Hud;

public sealed class HudStateService
{
    private readonly object _sync = new();
    private HudSessionState _current;

    public HudStateService(HudVisibilityMode visibilityMode = HudVisibilityMode.Always)
    {
        _current = HudSessionState.CreateDefault(NormalizeVisibilityMode(visibilityMode));
    }

    public event Action<HudSessionState>? StateChanged;

    public HudSessionState Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void ToggleLock() => Update(state => state with { IsLocked = !state.IsLocked });

    public void SetLocked(bool isLocked) => Update(state => state with { IsLocked = isLocked });

    public void ToggleUserVisibility() =>
        Update(state => state with { UserHudEnabled = !state.UserHudEnabled });

    public void SetUserHudEnabled(bool enabled) =>
        Update(state => state with { UserHudEnabled = enabled });

    public void SetVisibilityMode(HudVisibilityMode mode) =>
        Update(state => state with { VisibilityMode = NormalizeVisibilityMode(mode) });

    public void SetTargetGameForeground(bool isForeground) =>
        Update(state => state with { IsTargetGameForeground = isForeground });

    public void SetRpcConnected(bool isConnected) =>
        Update(state => state with
        {
            IsRpcConnected = isConnected,
            HasInitialConnectionReady = state.HasInitialConnectionReady || isConnected,
        });

    private void Update(Func<HudSessionState, HudSessionState> update)
    {
        HudSessionState next;
        lock (_sync)
        {
            next = update(_current);
            if (next == _current)
            {
                return;
            }

            _current = next;
        }

        StateChanged?.Invoke(next);
    }

    private static HudVisibilityMode NormalizeVisibilityMode(HudVisibilityMode mode) =>
        Enum.IsDefined(mode) ? mode : HudVisibilityMode.Always;
}

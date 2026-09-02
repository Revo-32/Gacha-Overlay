namespace LSOverlay.Backend.Runtime;

internal enum BackendConnectionHealthState
{
    Stopped,
    Starting,
    Connecting,
    Ready,
    TargetGuildUnavailable,
    Disconnected,
    Faulted,
}

internal enum BackendConnectionHealthReason
{
    None,
    Startup,
    GatewayConnecting,
    GatewayReady,
    TargetGuildMissing,
    GatewayDisconnected,
    PrivilegedIntentsRejected,
    AuthenticationFailed,
    UnexpectedFailure,
    GracefulShutdown,
}

internal sealed record BackendConnectionHealthSnapshot(
    BackendConnectionHealthState State,
    BackendConnectionHealthReason Reason,
    DateTimeOffset ChangedAt);

internal sealed class BackendConnectionHealth
{
    private readonly object _sync = new();
    private bool _hasFaulted;
    private BackendConnectionHealthSnapshot _current = new(
        BackendConnectionHealthState.Stopped,
        BackendConnectionHealthReason.None,
        DateTimeOffset.UtcNow);

    public event Action<BackendConnectionHealthSnapshot>? Changed;

    public BackendConnectionHealthSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public bool HasFaulted
    {
        get
        {
            lock (_sync)
            {
                return _hasFaulted;
            }
        }
    }

    public bool Transition(
        BackendConnectionHealthState state,
        BackendConnectionHealthReason reason)
    {
        BackendConnectionHealthSnapshot snapshot;
        lock (_sync)
        {
            if (_current.State == state && _current.Reason == reason)
            {
                return false;
            }

            snapshot = new BackendConnectionHealthSnapshot(
                state,
                reason,
                DateTimeOffset.UtcNow);
            _current = snapshot;
            _hasFaulted |= state == BackendConnectionHealthState.Faulted;
        }

        Changed?.Invoke(snapshot);
        return true;
    }
}

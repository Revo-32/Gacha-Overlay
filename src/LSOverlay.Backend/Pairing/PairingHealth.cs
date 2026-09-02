namespace LSOverlay.Backend.Pairing;

internal enum PairingHealthState
{
    Starting,
    Available,
    Degraded,
}

internal sealed class PairingHealth
{
    private int _state = (int)PairingHealthState.Starting;

    public PairingHealthState State => (PairingHealthState)Volatile.Read(ref _state);

    public void Set(PairingHealthState state) => Volatile.Write(ref _state, (int)state);
}

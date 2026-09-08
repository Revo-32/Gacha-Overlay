namespace GachaOverlay.Core.Hud.Game;

public sealed record GtaProcessIdentity(int Pid, long StartTicks, string Name);
public sealed record GtaProcessObservation(bool Succeeded, IReadOnlyList<GtaProcessIdentity> Processes, int ForegroundPid);
public sealed record GtaClientState(bool ProcessRunning, bool Foreground, bool ValidTrackedProcess,
    GtaProcessIdentity? Process, long Generation)
{
    public static GtaClientState Empty { get; } = new(false, false, false, null, 0);
}
public interface IGtaProcessProbe { GtaProcessObservation Observe(); }

/// <summary>One-second reconciliation. Two absent observations confirm loss; a failed observation
/// does not flap, but cannot keep an unconfirmed timer running indefinitely.</summary>
public sealed class GtaProcessTracker
{
    private int _missing;
    public GtaClientState Current { get; private set; } = GtaClientState.Empty;
    public event Action<GtaClientState>? Changed;

    public void Reconcile(GtaProcessObservation observation)
    {
        var process = observation.Succeeded
            ? observation.Processes.FirstOrDefault(x => x == Current.Process) ?? observation.Processes.FirstOrDefault()
            : null;
        if (process is null && ++_missing < 2) return;
        if (process is not null) _missing = 0;
        var generation = Current.Generation + (Current.Process != process ? 1 : 0);
        var next = new GtaClientState(process is not null, process is not null && process.Pid == observation.ForegroundPid,
            process is not null, process, generation);
        if (next == Current) return;
        Current = next;
        Changed?.Invoke(next);
    }

    public void Exited(GtaProcessIdentity process, long generation)
    {
        if (Current.Process != process || Current.Generation != generation) return;
        _missing = 2;
        Reconcile(new(true, [], 0));
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using GachaOverlay.Core.Hud.Game;

namespace GachaOverlay.App.Services;

internal sealed class GtaProcessMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly IGtaProcessProbe _probe;
    private readonly GtaProcessTracker _tracker = new();
    private System.Threading.Timer? _timer;
    private Process? _process;
    private GtaProcessIdentity? _trackedIdentity;
    private EventHandler? _exitHandler;
    private bool _disposed;
    public GtaProcessMonitor(IGtaProcessProbe? probe = null)
    {
        _probe = probe ?? new WindowsGtaProcessProbe();
        _tracker.Changed += state => { TrackExit(state); Changed?.Invoke(state); };
    }
    public event Action<GtaClientState>? Changed;
    public void Start() => _timer = new System.Threading.Timer(_ => Reconcile(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    private void Reconcile()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _tracker.Reconcile(_probe.Observe());
        }
    }
    private void TrackExit(GtaClientState state)
    {
        if (_process is not null && _trackedIdentity == state.Process) return;
        ClearProcess();
        if (state.Process is not { } identity) return;
        try
        {
            _process = Process.GetProcessById(identity.Pid);
            _trackedIdentity = identity;
            if (_process.StartTime.ToUniversalTime().Ticks != identity.StartTicks) { ClearProcess(); return; }
            _exitHandler = (_, _) =>
            {
                lock (_sync)
                    if (!_disposed) _tracker.Exited(identity, state.Generation);
            };
            _process.Exited += _exitHandler;
            _process.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { ClearProcess(); } // Reconciliation remains authoritative when exit subscription is unavailable.
    }
    private void ClearProcess()
    {
        if (_process is null) return;
        if (_exitHandler is not null) _process.Exited -= _exitHandler;
        _process.Dispose();
        _process = null;
        _trackedIdentity = null;
        _exitHandler = null;
    }
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer?.Dispose();
            ClearProcess();
        }
    }
}

internal sealed class WindowsGtaProcessProbe : IGtaProcessProbe
{
    public GtaProcessObservation Observe()
    {
        var found = new List<GtaProcessIdentity>();
        var succeeded = true;
        foreach (var name in TargetGameMatcher.DefaultProcessNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch (System.ComponentModel.Win32Exception) { succeeded = false; continue; }
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited) found.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, name));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    { succeeded = false; }
                }
            }
        }
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        return new(succeeded, found, unchecked((int)foreground));
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

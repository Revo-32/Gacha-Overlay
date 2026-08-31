using System.Diagnostics;
using System.Runtime.InteropServices;
using GachaOverlay.Core.Hud.Game;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.App.Services;

internal sealed record GameForegroundSnapshot(
    string? ProcessName,
    bool IsTargetGame,
    DateTimeOffset ChangedAt);

internal sealed class GameForegroundMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0;

    private readonly object _sync = new();
    private readonly TargetGameMatcher _matcher;
    private readonly IAppLogger _logger;
    private readonly WinEventDelegate _winEventCallback;
    private IntPtr _hook;
    private System.Threading.Timer? _fallbackTimer;
    private GameForegroundSnapshot? _last;
    private bool _enabled;
    private bool _disposed;

    public GameForegroundMonitor(TargetGameMatcher matcher, IAppLogger logger)
    {
        _matcher = matcher;
        _logger = logger;
        _winEventCallback = OnWinEvent;
    }

    public event Action<GameForegroundSnapshot>? ForegroundChanged;

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            if (enabled)
            {
                _logger.Information("GAME", "Foreground monitoring resumed for GameForegroundOnly mode.");
                _hook = SetWinEventHook(
                    EventSystemForeground,
                    EventSystemForeground,
                    IntPtr.Zero,
                    _winEventCallback,
                    0,
                    0,
                    WineventOutOfContext);
                if (_hook == IntPtr.Zero)
                {
                    _logger.Warning(
                        "GAME",
                        $"Foreground event hook failed error={Marshal.GetLastPInvokeError()}; using 1-second fallback polling.");
                    _fallbackTimer = new System.Threading.Timer(
                        _ => EvaluateCurrentForeground(),
                        null,
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(1));
                }
            }
            else
            {
                _logger.Information("GAME", "Foreground monitoring suspended for Always mode.");
                StopMonitoringCore();
            }
        }

        if (enabled)
        {
            EvaluateCurrentForeground();
        }
        else
        {
            Publish(new GameForegroundSnapshot(null, false, DateTimeOffset.UtcNow));
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
            _enabled = false;
            StopMonitoringCore();
        }
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime) => EvaluateForeground(hwnd);

    private void EvaluateCurrentForeground() => EvaluateForeground(GetForegroundWindow());

    private void EvaluateForeground(IntPtr hwnd)
    {
        lock (_sync)
        {
            if (!_enabled || _disposed)
            {
                return;
            }
        }

        var processName = TryGetProcessName(hwnd);
        Publish(new GameForegroundSnapshot(
            processName,
            _matcher.IsTarget(processName),
            DateTimeOffset.UtcNow));
    }

    private void Publish(GameForegroundSnapshot snapshot)
    {
        Action<GameForegroundSnapshot>? handlers;
        lock (_sync)
        {
            if (_disposed ||
                (_last is not null &&
                 string.Equals(_last.ProcessName, snapshot.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                 _last.IsTargetGame == snapshot.IsTargetGame))
            {
                return;
            }

            _last = snapshot;
            handlers = ForegroundChanged;
        }

        _logger.Information(
            "GAME",
            $"Foreground process={snapshot.ProcessName ?? "unknown"} target={snapshot.IsTargetGame.ToString().ToLowerInvariant()}.");
        handlers?.Invoke(snapshot);
    }

    private static string? TryGetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void StopMonitoringCore()
    {
        _fallbackTimer?.Dispose();
        _fallbackTimer = null;
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}

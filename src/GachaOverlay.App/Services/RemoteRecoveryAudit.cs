using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.App.Services;

internal enum RemoteRecoverySignal
{
    ChatSnapshot,
    ChatStream,
    PresenceSnapshot,
    PresenceStream,
    SalesComplete,
    SalesStream,
}

internal sealed record RemoteRecoverySnapshot
{
    public string Schema { get; init; } = "LSOverlay.WpfRecovery.v1";
    public string RunId { get; init; } = string.Empty;
    public int ProcessId { get; init; } = Environment.ProcessId;
    public DateTimeOffset ObservedAtUtc { get; init; }
    public long Attempt { get; init; }
    public string? BackendEpoch { get; init; }
    public bool SalesTrackingEnabled { get; init; }
    public bool ChatSnapshotApplied { get; init; }
    public bool ChatStreamReady { get; init; }
    public bool PresenceSnapshotApplied { get; init; }
    public bool PresenceStreamLive { get; init; }
    public bool SalesSnapshotComplete { get; init; }
    public bool SalesStreamReady { get; init; }
    public bool AuthenticationRequired { get; init; }
    public bool TerminalFailure { get; init; }
    public bool AttemptEnded { get; init; }

    public bool Ready => Attempt > 0 && !AttemptEnded && !AuthenticationRequired &&
        !TerminalFailure && SalesTrackingEnabled && BackendEpoch is not null &&
        ChatSnapshotApplied && ChatStreamReady && PresenceSnapshotApplied &&
        PresenceStreamLive && SalesSnapshotComplete && SalesStreamReady;
}

/// <summary>
/// Opt-in, helper-owned readiness evidence. Contains no credentials, Discord IDs,
/// message content, or host data and never changes production connection behavior.
/// </summary>
internal sealed class RemoteRecoveryAudit : IDisposable
{
    internal const string DirectoryVariable = "LSO_DEV_RECOVERY_AUDIT_DIRECTORY";
    internal const string RunIdVariable = "LSO_DEV_RECOVERY_AUDIT_RUN_ID";
    private readonly object _sync = new();
    private readonly string? _path;
    private readonly IAppLogger _logger;
    private readonly System.Threading.Timer? _timer;
    private RemoteRecoverySnapshot _snapshot;
    private bool _disposed;
    private bool _writeWarningLogged;

    internal RemoteRecoveryAudit(string runId, string? path = null, IAppLogger? logger = null)
    {
        _snapshot = new RemoteRecoverySnapshot { RunId = runId };
        _path = path;
        _logger = logger ?? NullAppLogger.Instance;
        if (path is not null)
        {
            _timer = new System.Threading.Timer(_ => Publish(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    public static RemoteRecoveryAudit? FromEnvironment(IAppLogger logger)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        var runId = Environment.GetEnvironmentVariable(RunIdVariable);
        Environment.SetEnvironmentVariable(DirectoryVariable, null);
        Environment.SetEnvironmentVariable(RunIdVariable, null);
        if (directory is null && runId is null)
        {
            return null;
        }

        if (!TryResolvePath(directory, runId, out var path))
        {
            logger.Warning("AUDIT", "Invalid helper readiness destination; evidence export disabled.");
            return null;
        }

        return new RemoteRecoveryAudit(runId!, path, logger);
    }

    internal static bool TryResolvePath(string? directory, string? runId, out string? path)
    {
        path = null;
        if (!Guid.TryParseExact(runId, "N", out _) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            var info = new DirectoryInfo(Path.GetFullPath(directory));
            if (!info.Exists || info.Parent is null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !string.Equals(info.Parent.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(info.Name, "^LSOverlay-[A-Za-z0-9-]+-Audit-[a-f0-9]{32}$") ||
                !info.Name.EndsWith(runId!, StringComparison.Ordinal))
            {
                return false;
            }

            path = Path.Combine(info.FullName, "wpf-recovery.json");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public RemoteRecoverySnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _snapshot with { ObservedAtUtc = DateTimeOffset.UtcNow };
            }
        }
    }

    public long BeginAttempt(bool salesTrackingEnabled)
    {
        lock (_sync)
        {
            _snapshot = new RemoteRecoverySnapshot
            {
                RunId = _snapshot.RunId,
                Attempt = checked(_snapshot.Attempt + 1),
                SalesTrackingEnabled = salesTrackingEnabled,
            };
            return _snapshot.Attempt;
        }
    }

    public void Mark(long attempt, RemoteRecoverySignal signal, string? backendGeneration = null)
    {
        lock (_sync)
        {
            if (_disposed || _snapshot.Attempt != attempt || _snapshot.AttemptEnded)
            {
                return;
            }

            _snapshot = signal switch
            {
                RemoteRecoverySignal.ChatSnapshot => _snapshot with { ChatSnapshotApplied = true },
                RemoteRecoverySignal.ChatStream => _snapshot with { ChatStreamReady = true },
                RemoteRecoverySignal.PresenceSnapshot when !string.IsNullOrWhiteSpace(backendGeneration) =>
                    _snapshot with
                    {
                        PresenceSnapshotApplied = true,
                        BackendEpoch = string.Join("-", Convert.ToHexString(
                            SHA256.HashData(Encoding.UTF8.GetBytes(backendGeneration)))
                            .Chunk(8).Select(chunk => new string(chunk))),
                    },
                RemoteRecoverySignal.PresenceStream => _snapshot with { PresenceStreamLive = true },
                RemoteRecoverySignal.SalesComplete => _snapshot with { SalesSnapshotComplete = true },
                RemoteRecoverySignal.SalesStream => _snapshot with { SalesStreamReady = true },
                _ => _snapshot,
            };
        }
    }

    public void InvalidateChat()
    {
        lock (_sync)
        {
            _snapshot = _snapshot with { ChatSnapshotApplied = false, ChatStreamReady = false };
        }
    }

    public void InvalidateSales()
    {
        lock (_sync)
        {
            _snapshot = _snapshot with { SalesSnapshotComplete = false, SalesStreamReady = false };
        }
    }

    public void InvalidateConnection(bool authenticationRequired = false, bool terminalFailure = false)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                ChatStreamReady = false,
                PresenceStreamLive = false,
                SalesStreamReady = false,
                AuthenticationRequired = _snapshot.AuthenticationRequired || authenticationRequired,
                TerminalFailure = _snapshot.TerminalFailure || terminalFailure,
            };
        }
    }

    public void EndAttempt(long attempt)
    {
        lock (_sync)
        {
            if (_snapshot.Attempt == attempt)
            {
                _snapshot = _snapshot with { AttemptEnded = true };
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _snapshot = _snapshot with { AttemptEnded = true };
        }
        _timer?.Dispose();
    }

    private void Publish()
    {
        lock (_sync)
        {
            if (_disposed || _path is null)
            {
                return;
            }

            try
            {
                File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(Current), new UTF8Encoding(false));
                File.Move(_path + ".tmp", _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!_writeWarningLogged)
                {
                    _writeWarningLogged = true;
                    _logger.Warning("AUDIT", "Readiness evidence unavailable; the helper must not assume recovery.");
                }
            }
        }
    }
}

using System.Diagnostics;
using System.Text.Json;
using GachaOverlay.App.Services;

namespace GachaOverlay.Tests.Backend;

public sealed class M911RecoveryAuditTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Ready_RequiresEveryIndependentRecoverySignal(int missing)
    {
        using var audit = new RemoteRecoveryAudit("test");
        var attempt = audit.BeginAttempt(salesTrackingEnabled: true);
        foreach (var signal in Enum.GetValues<RemoteRecoverySignal>())
        {
            if ((int)signal != missing)
            {
                audit.Mark(attempt, signal, "backend-generation");
            }
        }

        Assert.False(audit.Current.Ready);
        audit.Mark(attempt, (RemoteRecoverySignal)missing, "backend-generation");
        Assert.True(audit.Current.Ready);
    }

    [Fact]
    public void DisabledSales_AndUnstartedAttemptsCannotPass()
    {
        using var audit = new RemoteRecoveryAudit("test");
        Assert.False(audit.Current.Ready);
        MarkAll(audit, audit.BeginAttempt(salesTrackingEnabled: false));
        Assert.False(audit.Current.Ready);
    }

    [Fact]
    public void NewAttempt_ResetsAllEvidenceAndRejectsOldCallbacks()
    {
        using var audit = new RemoteRecoveryAudit("test");
        var oldAttempt = audit.BeginAttempt(true);
        MarkAll(audit, oldAttempt);
        var nextAttempt = audit.BeginAttempt(true);
        MarkAll(audit, oldAttempt);
        audit.EndAttempt(oldAttempt);
        Assert.False(audit.Current.Ready);
        Assert.False(audit.Current.ChatSnapshotApplied);
        Assert.False(audit.Current.AttemptEnded);
        Assert.Null(audit.Current.BackendEpoch);
        MarkAll(audit, nextAttempt);
        Assert.True(audit.Current.Ready);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AuthenticationAndTerminalFailure_CannotBeClearedByLateReady(bool authentication, bool terminal)
    {
        using var audit = new RemoteRecoveryAudit("test");
        var attempt = audit.BeginAttempt(true);
        MarkAll(audit, attempt);
        audit.InvalidateConnection(authentication, terminal);
        MarkAll(audit, attempt);
        Assert.False(audit.Current.Ready);
        Assert.Equal(authentication, audit.Current.AuthenticationRequired);
        Assert.Equal(terminal, audit.Current.TerminalFailure);
        MarkAll(audit, audit.BeginAttempt(true));
        Assert.True(audit.Current.Ready);
    }

    [Fact]
    public void TransientLoss_RequiresAllStreamsAgain()
    {
        using var audit = new RemoteRecoveryAudit("test");
        var attempt = audit.BeginAttempt(true);
        MarkAll(audit, attempt);
        audit.InvalidateConnection();
        Assert.True(audit.Current.ChatSnapshotApplied);
        Assert.False(audit.Current.Ready);
        audit.Mark(attempt, RemoteRecoverySignal.ChatStream);
        audit.Mark(attempt, RemoteRecoverySignal.PresenceStream);
        Assert.False(audit.Current.Ready);
        audit.Mark(attempt, RemoteRecoverySignal.SalesStream);
        Assert.True(audit.Current.Ready);
    }

    [Fact]
    public void PartialSales_AndPendingChatCannotReusePreviousReady()
    {
        using var audit = new RemoteRecoveryAudit("test");
        var attempt = audit.BeginAttempt(true);
        MarkAll(audit, attempt);
        audit.InvalidateSales();
        audit.Mark(attempt, RemoteRecoverySignal.SalesStream);
        Assert.False(audit.Current.Ready);
        audit.Mark(attempt, RemoteRecoverySignal.SalesComplete);
        Assert.True(audit.Current.Ready);
        audit.InvalidateChat();
        Assert.False(audit.Current.ChatSnapshotApplied);
        Assert.False(audit.Current.ChatStreamReady);
        Assert.False(audit.Current.Ready);
    }

    [Fact]
    public void EndedAndDisposedAttempts_IgnoreLateReady()
    {
        using var audit = new RemoteRecoveryAudit("test");
        var attempt = audit.BeginAttempt(true);
        audit.EndAttempt(attempt);
        MarkAll(audit, attempt);
        Assert.False(audit.Current.Ready);
        Assert.False(audit.Current.ChatSnapshotApplied);
        var next = audit.BeginAttempt(true);
        MarkAll(audit, next);
        audit.Dispose();
        MarkAll(audit, next);
        Assert.False(audit.Current.Ready);
    }

    [Fact]
    public void Epoch_IsOpaqueAndChangesWithBackendProcessGeneration()
    {
        using var audit = new RemoteRecoveryAudit("test");
        var attempt = audit.BeginAttempt(true);
        audit.Mark(attempt, RemoteRecoverySignal.PresenceSnapshot, "private-generation-123456789012345678");
        var epoch = audit.Current.BackendEpoch;
        Assert.Matches("^[A-F0-9]{8}(-[A-F0-9]{8}){7}$", epoch!);
        var json = JsonSerializer.Serialize(audit.Current);
        Assert.DoesNotContain("private-generation", json, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789012345678", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        audit.Mark(attempt, RemoteRecoverySignal.PresenceSnapshot, "next-generation");
        Assert.NotEqual(epoch, audit.Current.BackendEpoch);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "invalid")]
    [InlineData("C:\\", "11111111111111111111111111111111")]
    public void Destination_RejectsMissingOrBroadPaths(string? directory, string? runId)
    {
        Assert.False(RemoteRecoveryAudit.TryResolvePath(directory, runId, out var path));
        Assert.Null(path);
    }

    [Fact]
    public async Task Destination_RequiresOwnedTempRunAndWritesFreshJson()
    {
        var runId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), $"LSOverlay-M911-Test-Audit-{runId}");
        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(RemoteRecoveryAudit.TryResolvePath(directory, runId, out var path));
            Assert.False(RemoteRecoveryAudit.TryResolvePath(directory, Guid.NewGuid().ToString("N"), out _));
            Assert.False(RemoteRecoveryAudit.TryResolvePath(Path.GetTempPath(), runId, out _));
            using var audit = new RemoteRecoveryAudit(runId, path);
            MarkAll(audit, audit.BeginAttempt(true));
            RemoteRecoverySnapshot? report = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (File.Exists(path))
                {
                    report = JsonSerializer.Deserialize<RemoteRecoverySnapshot>(await File.ReadAllTextAsync(path));
                    if (report?.Ready == true) { break; }
                }
                await Task.Delay(50);
            }
            Assert.NotNull(report);
            Assert.True(report.Ready);
            Assert.Equal(Environment.ProcessId, report.ProcessId);
            Assert.Equal(runId, report.RunId);
            Assert.InRange(DateTimeOffset.UtcNow - report.ObservedAtUtc, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Helper_OfflinePowerShellRecoveryRegressionsPass()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "tools", "dev", "test-ls-m911-recovery.ps1") })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        Assert.True(process.ExitCode == 0, $"{await output}\n{await errors}");
        Assert.Contains("M9.11 offline recovery checks passed", await output, StringComparison.Ordinal);
    }

    private static void MarkAll(RemoteRecoveryAudit audit, long attempt)
    {
        foreach (var signal in Enum.GetValues<RemoteRecoverySignal>())
        {
            audit.Mark(attempt, signal, "backend-generation");
        }
    }
}

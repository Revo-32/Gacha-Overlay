using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Diagnostics;
using GachaOverlay.Infrastructure.Logging;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Diagnostics;

public sealed class M82DiagnosticBundleTests
{
    private static readonly string[] RequiredJsonEntries =
    {
        "catalog-summary.json",
        "diagnostic-summary.json",
        "environment-summary.json",
        "health-snapshot.json",
        "runtime-metrics.json",
        "sanitized-settings.json",
    };

    [Fact]
    public async Task Bundle_contains_only_allowlisted_generated_artifacts_and_bounded_logs()
    {
        using var temporary = new TemporaryDirectory();
        var logs = temporary.File("logs");
        Directory.CreateDirectory(logs);
        for (var index = 0; index < 3; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(logs, $"gacha-overlay.log.{index}"),
                new string((char)('a' + index), 1_200_000));
        }

        await File.WriteAllTextAsync(
            Path.Combine(logs, "discord-client-secret.dat"),
            "must never be copied");
        var destination = temporary.File("diagnostics.zip");
        var exporter = new DiagnosticBundleExporter(NullAppLogger.Instance);

        var result = await exporter.ExportAsync(CreateRequest(destination, logs));

        Assert.Equal(DiagnosticBundleExportStatus.Succeeded, result.Status);
        using var archive = ZipFile.OpenRead(destination);
        var names = archive.Entries.Select(entry => entry.FullName).Order().ToArray();
        Assert.All(RequiredJsonEntries, name => Assert.Contains(name, names));
        Assert.All(names, name => Assert.True(
            RequiredJsonEntries.Contains(name) ||
            name == "crash-summary.json" ||
            name is "logs/log-1.txt" or "logs/log-2.txt"));
        Assert.DoesNotContain(names, name => name.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(
            archive.Entries.Where(entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal))
                .Sum(entry => entry.Length),
            1,
            DiagnosticBundleExporter.MaximumUncompressedLogBytes);
        using var settings = JsonDocument.Parse(ReadEntry(archive, "sanitized-settings.json"));
        Assert.Equal("ko", settings.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public async Task Serialization_boundary_redacts_every_prohibited_secret_and_message_body()
    {
        using var temporary = new TemporaryDirectory();
        var destination = temporary.File("diagnostics.zip");
        var artifacts = CreateArtifacts();
        artifacts["diagnostic-summary.json"] = new
        {
            ClientSecret = "client-secret-value",
            AccessToken = "access-token-value",
            RefreshToken = "refresh-token-value",
            Authorization = "Bearer authorization-token-value",
            Credential = "AQAAANCMnd8BFdERjHoAwE/Cl+sBA-sensitive-blob",
            Content = "private discord message body with spaces",
        };
        var exporter = new DiagnosticBundleExporter(NullAppLogger.Instance);

        var result = await exporter.ExportAsync(new DiagnosticBundleRequest(destination, artifacts));

        Assert.True(result.IsSuccess);
        using var archive = ZipFile.OpenRead(destination);
        var allContent = string.Join('\n', archive.Entries.Select(entry => ReadEntry(archive, entry.FullName)));
        Assert.DoesNotContain("client-secret-value", allContent, StringComparison.Ordinal);
        Assert.DoesNotContain("access-token-value", allContent, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-token-value", allContent, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization-token-value", allContent, StringComparison.Ordinal);
        Assert.DoesNotContain("AQAAANCMnd8BFdERjHoAwE/Cl+sBA", allContent, StringComparison.Ordinal);
        Assert.DoesNotContain("private discord message body", allContent, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", allContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Crash_summary_is_rebuilt_from_allowlisted_fields_only()
    {
        using var temporary = new TemporaryDirectory();
        var crash = temporary.File("crash-summary.json");
        await File.WriteAllTextAsync(crash, """
            {
              "timestamp": "2026-09-01T00:00:00Z",
              "appVersion": "1.0.0",
              "exceptionType": "System.InvalidOperationException",
              "sanitizedStack": "at Safe.Method()",
              "subsystemContext": "WPF Dispatcher",
              "rawDiscordPayload": "private message that must not be copied",
              "clientSecret": "must-not-leak"
            }
            """);
        var destination = temporary.File("diagnostics.zip");
        var exporter = new DiagnosticBundleExporter(NullAppLogger.Instance);

        var result = await exporter.ExportAsync(new DiagnosticBundleRequest(
            destination,
            CreateArtifacts(),
            CrashSummaryPath: crash));

        Assert.True(result.IsSuccess);
        using var archive = ZipFile.OpenRead(destination);
        var content = ReadEntry(archive, "crash-summary.json");
        Assert.Contains("System.InvalidOperationException", content, StringComparison.Ordinal);
        Assert.DoesNotContain("rawDiscordPayload", content, StringComparison.Ordinal);
        Assert.DoesNotContain("private message", content, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Destination_failure_is_recoverable_and_partial_archive_is_cleaned()
    {
        using var temporary = new TemporaryDirectory();
        var blockedParent = temporary.File("not-a-directory");
        await File.WriteAllTextAsync(blockedParent, "block");
        var destination = Path.Combine(blockedParent, "diagnostics.zip");
        var exporter = new DiagnosticBundleExporter(NullAppLogger.Instance);

        var result = await exporter.ExportAsync(CreateRequest(destination));

        Assert.Equal(DiagnosticBundleExportStatus.Failed, result.Status);
        Assert.False(File.Exists(destination));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(temporary.Path),
            path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Repeated_export_is_single_flight_without_blocking_other_services()
    {
        using var temporary = new TemporaryDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporter = new DiagnosticBundleExporter(
            NullAppLogger.Instance,
            beforeExport: async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            });
        var serviceTicks = 0;
        using var serviceCancellation = new CancellationTokenSource();
        var service = Task.Run(async () =>
        {
            while (!serviceCancellation.IsCancellationRequested)
            {
                Interlocked.Increment(ref serviceTicks);
                await Task.Delay(5, serviceCancellation.Token);
            }
        });
        var first = exporter.ExportAsync(CreateRequest(temporary.File("first.zip")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = await exporter.ExportAsync(CreateRequest(temporary.File("second.zip")));
        await Task.Delay(30);
        release.TrySetResult();
        var completed = await first;
        serviceCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service);

        Assert.Equal(DiagnosticBundleExportStatus.Busy, second.Status);
        Assert.Equal(DiagnosticBundleExportStatus.Succeeded, completed.Status);
        Assert.True(serviceTicks > 1);
    }

    [Fact]
    public async Task Cancellation_is_safe_and_leaves_no_partial_archive()
    {
        using var temporary = new TemporaryDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporter = new DiagnosticBundleExporter(
            NullAppLogger.Instance,
            beforeExport: async cancellationToken =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        using var cancellation = new CancellationTokenSource();
        var destination = temporary.File("cancelled.zip");
        var export = exporter.ExportAsync(CreateRequest(destination), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        var result = await export;

        Assert.Equal(DiagnosticBundleExportStatus.Cancelled, result.Status);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp"));
    }

    [Fact]
    public async Task Already_redacted_previous_log_and_active_current_log_export_successfully()
    {
        using var temporary = new TemporaryDirectory();
        var logs = temporary.File("logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "gacha-overlay.log.1"),
            "2026-09-01 [INF] [RPC] content=[REDACTED]");
        using var activeLogger = new RollingFileLogger(logs);
        activeLogger.Information("RPC", "content=private-current-message");
        var destination = temporary.File("diagnostics.zip");
        var exporter = new DiagnosticBundleExporter(NullAppLogger.Instance);

        var result = await exporter.ExportAsync(CreateRequest(destination, logs));

        Assert.True(result.IsSuccess);
        using var archive = ZipFile.OpenRead(destination);
        var logContent = string.Join('\n', archive.Entries
            .Where(entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal))
            .Select(entry => ReadEntry(archive, entry.FullName)));
        Assert.Contains("content=[REDACTED]", logContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-current-message", logContent, StringComparison.Ordinal);
        Assert.DoesNotContain("[REDACTED]REDACTED]", logContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_current_log_is_reported_and_does_not_fail_bundle()
    {
        using var temporary = new TemporaryDirectory();
        var logs = temporary.File("logs");
        Directory.CreateDirectory(logs);
        var logPath = Path.Combine(logs, "gacha-overlay.log");
        await using var locked = new FileStream(
            logPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        await locked.WriteAsync(Encoding.UTF8.GetBytes("active log"));
        await locked.FlushAsync();
        var destination = temporary.File("diagnostics.zip");
        var exporter = new DiagnosticBundleExporter(NullAppLogger.Instance);

        var result = await exporter.ExportAsync(CreateRequest(destination, logs));

        Assert.True(result.IsSuccess);
        using var archive = ZipFile.OpenRead(destination);
        using var summary = JsonDocument.Parse(ReadEntry(archive, "diagnostic-summary.json"));
        var logsSummary = summary.RootElement
            .GetProperty("optionalData")
            .GetProperty("logs");
        Assert.Equal("skipped", logsSummary.GetProperty("status").GetString());
        Assert.Equal(0, logsSummary.GetProperty("includedCount").GetInt32());
        Assert.Equal(1, logsSummary.GetProperty("skippedCount").GetInt32());
    }

    [Fact]
    public async Task Malformed_optional_crash_summary_is_skipped_without_failing_bundle()
    {
        using var temporary = new TemporaryDirectory();
        var crash = temporary.File("crash-summary.json");
        await File.WriteAllTextAsync(crash, "{ incomplete");
        var destination = temporary.File("diagnostics.zip");
        var exporter = new DiagnosticBundleExporter(NullAppLogger.Instance);

        var result = await exporter.ExportAsync(new DiagnosticBundleRequest(
            destination,
            CreateArtifacts(),
            CrashSummaryPath: crash));

        Assert.True(result.IsSuccess);
        using var archive = ZipFile.OpenRead(destination);
        Assert.Null(archive.GetEntry("crash-summary.json"));
        using var summary = JsonDocument.Parse(ReadEntry(archive, "diagnostic-summary.json"));
        var crashSummary = summary.RootElement
            .GetProperty("optionalData")
            .GetProperty("crashSummary");
        Assert.Equal("skipped", crashSummary.GetProperty("status").GetString());
        Assert.Equal("invalidJson", crashSummary.GetProperty("skipReason").GetString());
    }

    [Fact]
    public async Task Non_finite_metric_values_are_serialized_as_json_null()
    {
        using var temporary = new TemporaryDirectory();
        var artifacts = CreateArtifacts();
        artifacts["runtime-metrics.json"] = new
        {
            First = double.NaN,
            Second = double.PositiveInfinity,
            Third = float.NegativeInfinity,
            Finite = 12.5,
        };
        var destination = temporary.File("diagnostics.zip");
        var exporter = new DiagnosticBundleExporter(NullAppLogger.Instance);

        var result = await exporter.ExportAsync(new DiagnosticBundleRequest(destination, artifacts));

        Assert.True(result.IsSuccess);
        using var archive = ZipFile.OpenRead(destination);
        using var json = JsonDocument.Parse(ReadEntry(archive, "runtime-metrics.json"));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("first").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("second").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("third").ValueKind);
        Assert.Equal(12.5, json.RootElement.GetProperty("finite").GetDouble());
    }

    [Theory]
    [InlineData("ImmediatelyConstructed", "Disconnected", false, 0)]
    [InlineData("RpcDisconnected", "Disconnected", false, 0)]
    [InlineData("RpcConnecting", "Connecting", false, 0)]
    [InlineData("AuthenticatedBootstrapIncomplete", "Authenticating", false, 0)]
    [InlineData("LiveAndBootstrappedSalesOff", "Connected", false, 0)]
    [InlineData("SalesConnecting", "Connected", true, 0)]
    [InlineData("SalesLive", "Connected", true, 1)]
    [InlineData("BootstrapInProgress", "Connected", false, 0)]
    public async Task Startup_state_matrix_exports_without_optional_samples(
        string phase,
        string rpcState,
        bool salesEnabled,
        int remoteSalesSamples)
    {
        using var temporary = new TemporaryDirectory();
        var artifacts = CreateArtifacts();
        var runtime = new RuntimeMetricsCollector().Snapshot();
        artifacts["diagnostic-summary.json"] = new
        {
            Phase = phase,
            Rpc = new { State = rpcState },
            Sales = new { Enabled = salesEnabled },
            RemoteSales = new { SampleCount = remoteSalesSamples },
            LastSync = (DateTimeOffset?)null,
        };
        artifacts["runtime-metrics.json"] = new
        {
            Runtime = runtime,
            Process = new
            {
                CpuPercent = (double?)null,
                RemoteSalesDuration = new
                {
                    SampleCount = remoteSalesSamples,
                    Average = (double?)null,
                    P95 = (double?)null,
                    P99 = (double?)null,
                    Maximum = (double?)null,
                    Status = remoteSalesSamples == 0 ? "notSampled" : "sampled",
                },
            },
        };
        var destination = temporary.File($"{phase}.zip");
        var exporter = new DiagnosticBundleExporter(NullAppLogger.Instance);

        var result = await exporter.ExportAsync(new DiagnosticBundleRequest(destination, artifacts));

        Assert.True(result.IsSuccess);
        using var archive = ZipFile.OpenRead(destination);
        Assert.All(RequiredJsonEntries, name =>
        {
            using var _ = JsonDocument.Parse(ReadEntry(archive, name));
        });
    }

    [Fact]
    public async Task Export_uses_copied_artifact_snapshot_when_caller_mutates_dictionary()
    {
        using var temporary = new TemporaryDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var artifacts = CreateArtifacts();
        var exporter = new DiagnosticBundleExporter(
            NullAppLogger.Instance,
            beforeExport: async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            });
        var destination = temporary.File("snapshot.zip");

        var export = exporter.ExportAsync(new DiagnosticBundleRequest(destination, artifacts));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        artifacts.Clear();
        release.TrySetResult();
        var result = await export;

        Assert.True(result.IsSuccess);
        using var archive = ZipFile.OpenRead(destination);
        Assert.All(RequiredJsonEntries, name => Assert.NotNull(archive.GetEntry(name)));
    }

    [Fact]
    public async Task Failure_log_contains_sanitized_stage_entry_message_and_stack()
    {
        using var temporary = new TemporaryDirectory();
        var artifacts = CreateArtifacts();
        artifacts["client_secret=do-not-log-this"] = new { Value = 1 };
        var logger = new RecordingLogger();
        var exporter = new DiagnosticBundleExporter(logger);

        var result = await exporter.ExportAsync(new DiagnosticBundleRequest(
            temporary.File("failed.zip"),
            artifacts));

        Assert.Equal(DiagnosticBundleExportStatus.Failed, result.Status);
        Assert.Equal(DiagnosticExportStage.ValidateEntries, result.FailureStage);
        Assert.Equal("client_secret=[REDACTED]", result.FailureEntry);
        var error = Assert.Single(logger.Errors);
        Assert.Contains("exportStage=ValidateEntries", error, StringComparison.Ordinal);
        Assert.Contains("entry=client_secret=[REDACTED]", error, StringComparison.Ordinal);
        Assert.Contains("exception=InvalidDataException", error, StringComparison.Ordinal);
        Assert.Contains("message=", error, StringComparison.Ordinal);
        Assert.Contains("stack=", error, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this", error, StringComparison.Ordinal);
    }

    private static DiagnosticBundleRequest CreateRequest(
        string destination,
        string? logDirectory = null) =>
        new(destination, CreateArtifacts(), logDirectory);

    private static Dictionary<string, object> CreateArtifacts() => new(StringComparer.Ordinal)
    {
        ["diagnostic-summary.json"] = new { Version = "test" },
        ["sanitized-settings.json"] = new { Language = "ko" },
        ["runtime-metrics.json"] = new { UptimeSeconds = 1 },
        ["health-snapshot.json"] = new { Rpc = "Connected" },
        ["environment-summary.json"] = new { Os = "Windows" },
        ["catalog-summary.json"] = new { Count = 1 },
    };

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException(name);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Errors { get; } = [];

        public void Information(string category, string message)
        {
        }

        public void Warning(string category, string message)
        {
        }

        public void Error(string category, string message, Exception? exception = null) =>
            Errors.Add(message);
    }
}

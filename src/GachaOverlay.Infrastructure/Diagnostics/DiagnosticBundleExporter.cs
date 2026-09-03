using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Logging;

namespace GachaOverlay.Infrastructure.Diagnostics;

public enum DiagnosticBundleExportStatus
{
    Succeeded,
    Busy,
    Cancelled,
    Failed,
}

public enum DiagnosticExportStage
{
    None,
    SelectDestination,
    CreateSnapshot,
    BuildSummary,
    BuildSanitizedSettings,
    BuildRuntimeMetrics,
    BuildHealthSnapshot,
    BuildEnvironmentSummary,
    BuildCatalogSummary,
    CollectLogs,
    BuildCrashSummary,
    ValidateEntries,
    WriteArchive,
    FinalizeArchive,
}

public sealed record DiagnosticBundleExportResult(
    DiagnosticBundleExportStatus Status,
    string? DestinationPath = null,
    string? FailureType = null,
    DiagnosticExportStage FailureStage = DiagnosticExportStage.None,
    string? FailureEntry = null)
{
    public bool IsSuccess => Status == DiagnosticBundleExportStatus.Succeeded;
}

public sealed record DiagnosticBundleRequest(
    string DestinationPath,
    IReadOnlyDictionary<string, object> JsonArtifacts,
    string? LogDirectory = null,
    string? CrashSummaryPath = null);

public sealed partial class DiagnosticBundleExporter
{
    public const int MaximumLogFiles = 2;
    public const int MaximumUncompressedLogBytes = 2 * 1024 * 1024;

    public static IReadOnlySet<string> AllowedJsonEntryNames { get; } =
        new[]
        {
            "diagnostic-summary.json",
            "sanitized-settings.json",
            "runtime-metrics.json",
            "health-snapshot.json",
            "environment-summary.json",
            "catalog-summary.json",
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new FiniteDoubleJsonConverter(),
            new FiniteSingleJsonConverter(),
        },
    };

    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly IAppLogger _logger;
    private readonly IRuntimeMetrics? _metrics;
    private readonly Func<CancellationToken, Task>? _beforeExport;

    public DiagnosticBundleExporter(
        IAppLogger logger,
        IRuntimeMetrics? metrics = null,
        Func<CancellationToken, Task>? beforeExport = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;
        _beforeExport = beforeExport;
    }

    public async Task<DiagnosticBundleExportResult> ExportAsync(
        DiagnosticBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return new DiagnosticBundleExportResult(DiagnosticBundleExportStatus.Cancelled);
        }

        if (!await _singleFlight.WaitAsync(0).ConfigureAwait(false))
        {
            return new DiagnosticBundleExportResult(DiagnosticBundleExportStatus.Busy);
        }

        var started = Stopwatch.GetTimestamp();
        var progress = new DiagnosticExportProgress();
        try
        {
            progress.Update(DiagnosticExportStage.CreateSnapshot);
            var snapshot = SnapshotRequest(request);
            if (_beforeExport is not null)
            {
                await _beforeExport(cancellationToken).ConfigureAwait(false);
            }

            var result = await Task.Run(
                    () => ExportCore(snapshot, progress, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _metrics?.Increment(RuntimeMetricNames.DiagnosticExports);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return new DiagnosticBundleExportResult(DiagnosticBundleExportStatus.Cancelled);
        }
        catch (Exception exception)
        {
            _metrics?.Increment(RuntimeMetricNames.DiagnosticExportFailures);
            // Exception messages/paths can contain arbitrary data supplied by files or serializers.
            const string safeMessage = "Diagnostic operation failed.";
            const string safeStack = "omitted";
            var safeEntry = progress.Entry is null
                ? "none"
                : SanitizeFailureText(progress.Entry, 256);
            _logger.Error(
                "DIAGNOSTICS",
                $"exportStage={progress.Stage} " +
                $"entry={safeEntry} result=Failed " +
                $"exception={exception.GetType().Name} " +
                $"message={safeMessage} stack={safeStack}");
            return new DiagnosticBundleExportResult(
                DiagnosticBundleExportStatus.Failed,
                FailureType: exception.GetType().Name,
                FailureStage: progress.Stage,
                FailureEntry: safeEntry == "none" ? null : safeEntry);
        }
        finally
        {
            _metrics?.RecordDuration(
                RuntimeMetricNames.DiagnosticExportDuration,
                Stopwatch.GetElapsedTime(started));
            _singleFlight.Release();
        }
    }

    private DiagnosticBundleExportResult ExportCore(
        DiagnosticBundleRequest request,
        DiagnosticExportProgress progress,
        CancellationToken cancellationToken)
    {
        progress.Update(DiagnosticExportStage.WriteArchive);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        if (!Path.IsPathFullyQualified(request.DestinationPath))
            throw new ArgumentException("The diagnostic destination must be absolute.");
        var destination = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The diagnostic destination is invalid.");
        Directory.CreateDirectory(directory);
        var temporary = $"{destination}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var entries = BuildEntries(request, progress, cancellationToken);
            progress.Update(DiagnosticExportStage.ValidateEntries);
            AuditEntrySet(entries, progress);
            progress.Update(DiagnosticExportStage.WriteArchive);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var artifact in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress.Update(DiagnosticExportStage.WriteArchive, artifact.Name);
                        var entry = archive.CreateEntry(
                            artifact.Name,
                            CompressionLevel.Optimal);
                        using var writer = new StreamWriter(
                            entry.Open(),
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        writer.Write(artifact.Content);
                    }
                }
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Update(DiagnosticExportStage.FinalizeArchive);
            File.Move(temporary, destination, overwrite: true);
            _logger.Information(
                "DIAGNOSTICS",
                $"Diagnostic bundle created entries={entries.Count}.");
            return new DiagnosticBundleExportResult(
                DiagnosticBundleExportStatus.Succeeded,
                destination);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static DiagnosticBundleRequest SnapshotRequest(DiagnosticBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.JsonArtifacts);
        return request with
        {
            JsonArtifacts = request.JsonArtifacts.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
        };
    }

    private static IReadOnlyList<DiagnosticArtifact> BuildEntries(
        DiagnosticBundleRequest request,
        DiagnosticExportProgress progress,
        CancellationToken cancellationToken)
    {
        var entries = new List<DiagnosticArtifact>();
        foreach (var pair in request.JsonArtifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Update(StageForJsonEntry(pair.Key), pair.Key);
            if (!AllowedJsonEntryNames.Contains(pair.Key))
            {
                throw new InvalidDataException(
                    $"Diagnostic artifact '{pair.Key}' is not allowlisted.");
            }

            var json = JsonSerializer.Serialize(pair.Value, JsonOptions);
            entries.Add(new DiagnosticArtifact(pair.Key, SanitizeContent(json, isJson: true)));
        }

        var crashSummaryStatus = "notConfigured";
        string? crashSummarySkipReason = null;
        if (!string.IsNullOrWhiteSpace(request.CrashSummaryPath))
        {
            if (!File.Exists(request.CrashSummaryPath))
            {
                crashSummaryStatus = "notFound";
            }
            else
            {
                progress.Update(
                    DiagnosticExportStage.BuildCrashSummary,
                    "crash-summary.json");
                try
                {
                    entries.Add(new DiagnosticArtifact(
                        "crash-summary.json",
                        BuildCrashSummary(request.CrashSummaryPath)));
                    crashSummaryStatus = "included";
                }
                catch (Exception exception) when (IsOptionalFileFailure(exception))
                {
                    crashSummaryStatus = "skipped";
                    crashSummarySkipReason = GetOptionalFileFailureReason(exception);
                }
            }
        }

        progress.Update(DiagnosticExportStage.CollectLogs);
        var logs = ReadBoundedLogs(request.LogDirectory, cancellationToken);
        entries.AddRange(logs.Artifacts);
        progress.Update(
            DiagnosticExportStage.BuildSummary,
            "diagnostic-summary.json");
        AttachOptionalDataSummary(
            entries,
            crashSummaryStatus,
            crashSummarySkipReason,
            logs);
        return entries;
    }

    private static DiagnosticExportStage StageForJsonEntry(string entryName) => entryName switch
    {
        "diagnostic-summary.json" => DiagnosticExportStage.BuildSummary,
        "sanitized-settings.json" => DiagnosticExportStage.BuildSanitizedSettings,
        "runtime-metrics.json" => DiagnosticExportStage.BuildRuntimeMetrics,
        "health-snapshot.json" => DiagnosticExportStage.BuildHealthSnapshot,
        "environment-summary.json" => DiagnosticExportStage.BuildEnvironmentSummary,
        "catalog-summary.json" => DiagnosticExportStage.BuildCatalogSummary,
        _ => DiagnosticExportStage.ValidateEntries,
    };

    private static void AttachOptionalDataSummary(
        List<DiagnosticArtifact> entries,
        string crashSummaryStatus,
        string? crashSummarySkipReason,
        LogCollectionResult logs)
    {
        var index = entries.FindIndex(artifact =>
            artifact.Name == "diagnostic-summary.json");
        if (index < 0)
        {
            return;
        }

        var root = JsonNode.Parse(entries[index].Content) as JsonObject;
        if (root is null)
        {
            return;
        }

        root["optionalData"] = JsonSerializer.SerializeToNode(new
        {
            CrashSummary = new
            {
                Status = crashSummaryStatus,
                SkipReason = crashSummarySkipReason,
            },
            Logs = new
            {
                logs.Status,
                IncludedCount = logs.Artifacts.Count,
                SkippedCount = logs.Skipped.Count,
                logs.Skipped,
            },
        }, JsonOptions);
        entries[index] = entries[index] with
        {
            Content = SanitizeContent(root.ToJsonString(JsonOptions), isJson: true),
        };
    }

    private static string BuildCrashSummary(string path)
    {
        using var document = JsonDocument.Parse(ReadBoundedText(path, 256 * 1024));
        var root = document.RootElement;
        var summary = new
        {
            Timestamp = ReadJsonString(root, "timestamp", 80),
            AppVersion = ReadJsonString(root, "appVersion", 40),
            ExceptionType = ReadJsonString(root, "exceptionType", 240),
            SanitizedStack = ReadJsonString(root, "sanitizedStack", 32 * 1024),
            SubsystemContext = ReadJsonString(root, "subsystemContext", 240),
        };
        return SanitizeContent(JsonSerializer.Serialize(summary, JsonOptions), isJson: true);
    }

    private static string? ReadJsonString(
        JsonElement root,
        string propertyName,
        int maximumCharacters)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return value is null || value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters];
    }

    private static LogCollectionResult ReadBoundedLogs(
        string? logDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
        {
            return new LogCollectionResult("unavailable", [], []);
        }

        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(logDirectory, "gacha-overlay.log*")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(TryGetLastWriteTimeUtc)
                .Take(MaximumLogFiles)
                .ToArray();
        }
        catch (Exception exception) when (IsOptionalFileFailure(exception))
        {
            return new LogCollectionResult(
                "skipped",
                [],
                [new LogSkipSummary("directory", GetOptionalFileFailureReason(exception))]);
        }

        var artifacts = new List<DiagnosticArtifact>();
        var skipped = new List<LogSkipSummary>();
        var remaining = MaximumUncompressedLogBytes;
        for (var index = 0; index < paths.Length && remaining > 0; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = $"candidate-{index + 1}";
            try
            {
                var content = TrimUtf8ToMaximumBytes(
                    SanitizeContent(ReadBoundedText(paths[index], remaining)),
                    remaining);
                remaining -= Encoding.UTF8.GetByteCount(content);
                artifacts.Add(new DiagnosticArtifact(
                    $"logs/log-{artifacts.Count + 1}.txt",
                    content));
            }
            catch (Exception exception) when (IsOptionalFileFailure(exception))
            {
                skipped.Add(new LogSkipSummary(
                    candidate,
                    GetOptionalFileFailureReason(exception)));
            }
        }

        var status = skipped.Count > 0
            ? artifacts.Count > 0 ? "partial" : "skipped"
            : artifacts.Count > 0 ? "included" : "empty";
        return new LogCollectionResult(status, artifacts, skipped);
    }

    private static DateTime TryGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static bool IsOptionalFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or System.Security.SecurityException or ArgumentException or InvalidDataException;

    private static string GetOptionalFileFailureReason(Exception exception) => exception switch
    {
        InvalidDataException => "privacyBoundary",
        JsonException => "invalidJson",
        UnauthorizedAccessException or System.Security.SecurityException => "accessDenied",
        NotSupportedException or ArgumentException => "invalidPathOrFormat",
        _ => "unreadable",
    };

    private static string TrimUtf8ToMaximumBytes(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var midpoint = low + ((high - low + 1) / 2);
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, midpoint)) <= maximumBytes)
            {
                low = midpoint;
            }
            else
            {
                high = midpoint - 1;
            }
        }

        if (low > 0 && char.IsHighSurrogate(value[low - 1]))
        {
            low--;
        }

        return value[..low];
    }

    private static string ReadBoundedText(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[4];
        var headerLength = stream.ReadAtLeast(header, 4, throwOnEndOfStream: false);
        Encoding encoding = Encoding.UTF8;
        var preamble = 0;
        var unit = 1;
        if (headerLength >= 4 && header.SequenceEqual(new byte[] { 0xff, 0xfe, 0, 0 }))
            (encoding, preamble, unit) = (Encoding.UTF32, 4, 4);
        else if (headerLength >= 4 && header.SequenceEqual(new byte[] { 0, 0, 0xfe, 0xff }))
            (encoding, preamble, unit) = (new UTF32Encoding(bigEndian: true, byteOrderMark: false), 4, 4);
        else if (headerLength >= 2 && header[0] == 0xff && header[1] == 0xfe)
            (encoding, preamble, unit) = (Encoding.Unicode, 2, 2);
        else if (headerLength >= 2 && header[0] == 0xfe && header[1] == 0xff)
            (encoding, preamble, unit) = (Encoding.BigEndianUnicode, 2, 2);
        else if (headerLength >= 3 && header[0] == 0xef && header[1] == 0xbb && header[2] == 0xbf)
            preamble = 3;
        var snapshotLength = stream.Length;
        var start = Math.Max(preamble, snapshotLength - maximumBytes);
        start += (unit - ((start - preamble) % unit)) % unit;
        stream.Position = Math.Min(start, snapshotLength);

        // Capture a finite tail, even if another process keeps appending while we read.
        var buffer = new byte[(int)Math.Min(snapshotLength - stream.Position, maximumBytes)];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0) break;
            read += count;
        }
        using var snapshot = new MemoryStream(buffer, 0, read, writable: false);
        using var reader = new StreamReader(
            snapshot,
            encoding,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 81920,
            leaveOpen: false);
        var text = reader.ReadToEnd();
        if (text.Contains('\0')) throw new InvalidDataException("Unsupported binary diagnostic source.");
        // A tail can begin inside a body or secret value, without its identifying
        // field name. Omit that partial line instead of exporting unlabelled data.
        if (start > preamble)
        {
            var newline = text.IndexOf('\n');
            return newline < 0 ? string.Empty : text[(newline + 1)..];
        }
        return text.Length > 0 && text[0] == '�' ? text[1..] : text;
    }

    private static string SanitizeContent(string content, bool isJson = false)
    {
        var sanitized = isJson ? DiagnosticContentSanitizer.Json(content) : DiagnosticContentSanitizer.Text(content);
        // A log can mention retiring a credential file without containing that file.
        // Mask the name; never add credential files to the source allowlist.
        sanitized = ProhibitedCredentialArtifactPattern().Replace(sanitized, "[REDACTED-FILE]");
        if (ProhibitedCredentialArtifactPattern().IsMatch(sanitized) ||
            DpapiBlobPattern().IsMatch(sanitized))
        {
            throw new InvalidDataException(
                "A prohibited credential artifact was detected at the diagnostic boundary.");
        }

        return sanitized;
    }

    private static string SanitizeFailureText(string value, int maximumCharacters)
    {
        var sanitized = DiagnosticContentSanitizer.Text(value);
        return sanitized.Length <= maximumCharacters
            ? sanitized
            : sanitized[..maximumCharacters];
    }

    private static void AuditEntrySet(
        IReadOnlyList<DiagnosticArtifact> entries,
        DiagnosticExportProgress progress)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in entries)
        {
            progress.Update(DiagnosticExportStage.ValidateEntries, artifact.Name);
            var allowed = AllowedJsonEntryNames.Contains(artifact.Name) ||
                artifact.Name == "crash-summary.json" ||
                LogEntryPattern().IsMatch(artifact.Name);
            if (!allowed || artifact.Name.Contains("..", StringComparison.Ordinal) ||
                !names.Add(artifact.Name))
            {
                throw new InvalidDataException(
                    $"Diagnostic entry '{artifact.Name}' failed the final allowlist audit.");
            }

            if (!string.Equals(
                    artifact.Content,
                    SanitizeContent(artifact.Content, artifact.Name.EndsWith(".json", StringComparison.Ordinal)),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Diagnostic entry '{artifact.Name}' failed the final redaction audit.");
            }
        }

        if (!AllowedJsonEntryNames.IsSubsetOf(names))
            throw new InvalidDataException("A required diagnostic artifact is missing.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    [GeneratedRegex("^(?:logs/log-[1-9][0-9]*\\.txt)$", RegexOptions.CultureInvariant)]
    private static partial Regex LogEntryPattern();

    [GeneratedRegex(
        "(?:discord-(?:client-secret|oauth-token)|remote-access-token)\\.dat|(?:raw[-_ ]?)?credential(?:s)?\\.(?:dat|bin)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProhibitedCredentialArtifactPattern();

    [GeneratedRegex(
        "AQAAANCMnd8BFdERjHoAwE/Cl\\+sBA",
        RegexOptions.CultureInvariant)]
    private static partial Regex DpapiBlobPattern();

    private sealed record DiagnosticArtifact(string Name, string Content);

    private sealed record LogSkipSummary(string Candidate, string Reason);

    private sealed record LogCollectionResult(
        string Status,
        IReadOnlyList<DiagnosticArtifact> Artifacts,
        IReadOnlyList<LogSkipSummary> Skipped);

    private sealed class DiagnosticExportProgress
    {
        private readonly object _sync = new();
        private DiagnosticExportStage _stage;
        private string? _entry;

        public DiagnosticExportStage Stage
        {
            get
            {
                lock (_sync)
                {
                    return _stage;
                }
            }
        }

        public string? Entry
        {
            get
            {
                lock (_sync)
                {
                    return _entry;
                }
            }
        }

        public void Update(DiagnosticExportStage stage, string? entry = null)
        {
            lock (_sync)
            {
                _stage = stage;
                _entry = entry;
            }
        }
    }

    private sealed class FiniteDoubleJsonConverter : JsonConverter<double>
    {
        public override double Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => reader.GetDouble();

        public override void Write(
            Utf8JsonWriter writer,
            double value,
            JsonSerializerOptions options)
        {
            if (double.IsFinite(value))
            {
                writer.WriteNumberValue(value);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }

    private sealed class FiniteSingleJsonConverter : JsonConverter<float>
    {
        public override float Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => reader.GetSingle();

        public override void Write(
            Utf8JsonWriter writer,
            float value,
            JsonSerializerOptions options)
        {
            if (float.IsFinite(value))
            {
                writer.WriteNumberValue(value);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}

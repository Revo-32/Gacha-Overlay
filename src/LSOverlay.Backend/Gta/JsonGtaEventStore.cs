using System.Text;
using System.Text.Json;
using GachaOverlay.Core.Gta;
using Microsoft.Extensions.Logging;

namespace LSOverlay.Backend.Gta;

internal interface IGtaEventStore
{
    string Path { get; }

    GtaTrustedEventState Load();

    bool Save(GtaTrustedEventState state);
}

internal sealed class JsonGtaEventStore : IGtaEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<JsonGtaEventStore> _logger;

    public JsonGtaEventStore(Configuration.BackendConfiguration configuration, ILogger<JsonGtaEventStore> logger)
        : this(System.IO.Path.Combine(configuration.StateDirectory, "gta-companion-events.json"), logger)
    {
    }

    internal JsonGtaEventStore(string path, ILogger<JsonGtaEventStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Path { get; }

    public GtaTrustedEventState Load()
    {
        lock (_sync)
        {
            if (TryLoad(Path, out var state))
            {
                return state!;
            }

            if (TryLoad(BackupPath, out state))
            {
                _logger.LogWarning("GTA event primary store was invalid; Last-Good backup restored.");
                return state!;
            }

            return GtaTrustedEventState.Empty;
        }
    }

    public bool Save(GtaTrustedEventState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var temporary = $"{Path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        lock (_sync)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path)
                    ?? throw new InvalidOperationException("GTA event store directory is invalid.");
                Directory.CreateDirectory(directory);
                var normalized = state with
                {
                    SchemaVersion = GtaTrustedEventState.CurrentSchemaVersion,
                    RelevantCampaigns = state.RelevantCampaigns.Take(GtaEventResolver.MaximumCampaigns).ToArray(),
                };
                var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(normalized, JsonOptions));
                using (var stream = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(Path))
                {
                    File.Replace(temporary, Path, BackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, Path);
                }

                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                    NotSupportedException or System.Security.SecurityException)
            {
                TryDelete(temporary);
                _logger.LogWarning(
                    "GTA event store save failed; previous Last-Good data was preserved category={Category}.",
                    exception.GetType().Name);
                return false;
            }
        }
    }

    private string BackupPath => Path + ".bak";

    private bool TryLoad(string path, out GtaTrustedEventState? state)
    {
        state = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var candidate = JsonSerializer.Deserialize<GtaTrustedEventState>(stream, JsonOptions);
            if (candidate is null || candidate.SchemaVersion != GtaTrustedEventState.CurrentSchemaVersion)
            {
                throw new InvalidDataException("Unsupported GTA event store schema.");
            }

            state = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or NotSupportedException or System.Security.SecurityException)
        {
            _logger.LogWarning(
                "GTA event store load failed category={Category}; Discord hydration will recover it.",
                exception.GetType().Name);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}

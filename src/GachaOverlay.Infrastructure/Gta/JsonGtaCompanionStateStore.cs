using System.Text;
using System.Text.Json;
using GachaOverlay.Core.Gta;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.Infrastructure.Gta;

public sealed class JsonGtaCompanionStateStore : IGtaCompanionStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly IAppLogger _logger;

    public JsonGtaCompanionStateStore(string path, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _logger = logger ?? NullAppLogger.Instance;
    }

    public GtaCompanionLocalState? Load()
    {
        lock (_sync)
        {
            if (TryLoad(_path, out var state)) return state;
            if (TryLoad(_path + ".bak", out state))
            {
                _logger.Warning("GTA-COMPANION", "Primary local state was invalid; backup state recovered.");
                return state;
            }

            return null;
        }
    }

    public bool Save(GtaCompanionLocalState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var temporary = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        lock (_sync)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path)
                    ?? throw new InvalidOperationException("GTA Companion state directory is invalid.");
                Directory.CreateDirectory(directory);
                var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(state, JsonOptions));
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

                if (File.Exists(_path))
                {
                    File.Replace(temporary, _path, _path + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, _path);
                }

                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                    NotSupportedException or System.Security.SecurityException)
            {
                TryDelete(temporary);
                _logger.Error(
                    "GTA-COMPANION",
                    "Local challenge state save failed; previous state was preserved.",
                    exception);
                return false;
            }
        }
    }

    private bool TryLoad(string path, out GtaCompanionLocalState? state)
    {
        state = null;
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var loaded = JsonSerializer.Deserialize<GtaCompanionLocalState>(stream, JsonOptions);
            if (loaded is null || loaded.SchemaVersion != GtaCompanionLocalState.CurrentSchemaVersion)
            {
                throw new InvalidDataException("Unsupported GTA Companion local state schema.");
            }

            state = loaded;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or NotSupportedException or System.Security.SecurityException)
        {
            _logger.Warning(
                "GTA-COMPANION",
                $"Local challenge state could not be loaded; safe defaults will be used ({exception.GetType().Name}).");
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

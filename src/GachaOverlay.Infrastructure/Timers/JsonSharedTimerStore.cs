using System.Text;
using System.Text.Json;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Timers;

namespace GachaOverlay.Infrastructure.Timers;

public sealed class JsonSharedTimerStore : ISharedTimerStore
{
    private const int Version = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private readonly string _path;
    private readonly IAppLogger _logger;

    public JsonSharedTimerStore(string path, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = logger ?? NullAppLogger.Instance;
    }

    public IReadOnlyList<SharedTimerPersistedEntry> Load()
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<SharedTimerPersistedEntry>();
        }

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<SharedTimerDocument>(stream, JsonOptions);
            return document?.Version == Version
                ? document.Entries ?? Array.Empty<SharedTimerPersistedEntry>()
                : Array.Empty<SharedTimerPersistedEntry>();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException or System.Security.SecurityException)
        {
            _logger.Warning(
                "SHARED-TIMER",
                $"Shared timer state could not be loaded; empty state is being used ({exception.GetType().Name}).");
            return Array.Empty<SharedTimerPersistedEntry>();
        }
    }

    public bool Save(IReadOnlyCollection<SharedTimerPersistedEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(
                new SharedTimerDocument(Version, entries.Take(SharedTimerRegistry.DefaultCapacity).ToArray()),
                JsonOptions));
            using (var stream = new FileStream(
                       temporaryPath,
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
                File.Replace(temporaryPath, _path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                System.Security.SecurityException)
        {
            TryDelete(temporaryPath);
            _logger.Error("SHARED-TIMER", "Shared timer state save failed.", exception);
            return false;
        }
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

    private sealed record SharedTimerDocument(
        int Version,
        IReadOnlyList<SharedTimerPersistedEntry> Entries);
}

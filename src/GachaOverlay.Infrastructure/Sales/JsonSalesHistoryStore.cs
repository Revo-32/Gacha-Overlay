using System.Text;
using System.Text.Json;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Infrastructure.Sales;

public sealed class JsonSalesHistoryStore : ISalesHistoryStore
{
    private const int DocumentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly HashSet<string> _canonicalProductIds;
    private readonly IAppLogger _logger;
    private Dictionary<string, DateTimeOffset> _entries = new(StringComparer.Ordinal);

    public JsonSalesHistoryStore(
        string path,
        IEnumerable<string> canonicalProductIds,
        IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(canonicalProductIds);
        _path = path;
        _canonicalProductIds = canonicalProductIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal);
        _logger = logger ?? NullAppLogger.Instance;
        Load();
    }

    public event Action? Changed;

    public IReadOnlyList<SalesHistoryEntry> Snapshot()
    {
        lock (_sync)
        {
            return _entries
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new SalesHistoryEntry(pair.Key, pair.Value))
                .ToArray();
        }
    }

    public bool RecordSold(IReadOnlyCollection<string> productIds, DateTimeOffset soldAt)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        if (soldAt == default)
        {
            return false;
        }

        var normalizedIds = productIds
            .Where(_canonicalProductIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedIds.Length == 0)
        {
            return false;
        }

        bool saved;
        lock (_sync)
        {
            var next = new Dictionary<string, DateTimeOffset>(_entries, StringComparer.Ordinal);
            var utc = soldAt.ToUniversalTime();
            foreach (var productId in normalizedIds)
            {
                next[productId] = utc;
            }

            saved = SaveCore(next);
            if (saved)
            {
                _entries = next;
            }
        }

        if (saved)
        {
            Changed?.Invoke();
        }

        return saved;
    }

    public bool Clear()
    {
        bool saved;
        lock (_sync)
        {
            saved = SaveCore(new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
            if (saved)
            {
                _entries.Clear();
            }
        }

        if (saved)
        {
            Changed?.Invoke();
        }

        return saved;
    }

    private void Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
            {
                return;
            }

            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var document = JsonSerializer.Deserialize<SalesHistoryDocument>(stream, JsonOptions);
                if (document?.Version != DocumentVersion)
                {
                    throw new InvalidDataException("Unsupported sales history version.");
                }

                _entries = (document.Entries ?? Array.Empty<SalesHistoryEntry>())
                    .Where(entry =>
                        _canonicalProductIds.Contains(entry.ProductId) &&
                        entry.LastSoldAt != default)
                    .GroupBy(entry => entry.ProductId, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Max(entry => entry.LastSoldAt).ToUniversalTime(),
                        StringComparer.Ordinal);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or
                    InvalidDataException or NotSupportedException or
                    System.Security.SecurityException)
            {
                _entries.Clear();
                _logger.Warning(
                    "SALES-HISTORY",
                    $"Sales history could not be loaded; an empty history is being used ({exception.GetType().Name}).");
            }
        }
    }

    private bool SaveCore(IReadOnlyDictionary<string, DateTimeOffset> entries)
    {
        var temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("The sales history directory is invalid.");
            }

            Directory.CreateDirectory(directory);
            var document = new SalesHistoryDocument(
                DocumentVersion,
                entries
                    .Where(pair => _canonicalProductIds.Contains(pair.Key))
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new SalesHistoryEntry(pair.Key, pair.Value.ToUniversalTime()))
                    .ToArray());
            var bytes = new UTF8Encoding(false).GetBytes(
                JsonSerializer.Serialize(document, JsonOptions));
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
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                NotSupportedException or System.Security.SecurityException)
        {
            TryDelete(temporaryPath);
            _logger.Error("SALES-HISTORY", "Sales history save failed; the previous file was preserved.", exception);
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

    private sealed record SalesHistoryDocument(
        int Version,
        IReadOnlyList<SalesHistoryEntry> Entries);
}

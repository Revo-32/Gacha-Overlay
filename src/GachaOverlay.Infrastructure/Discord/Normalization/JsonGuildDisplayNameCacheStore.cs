using System.Text.Json;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.Infrastructure.Discord.Normalization;

public sealed class JsonGuildDisplayNameCacheStore : IGuildDisplayNameCacheStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly IAppLogger _logger;

    public JsonGuildDisplayNameCacheStore(string filePath, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger ?? NullAppLogger.Instance;
    }

    public GuildDisplayNameCacheDocument Load(string accountUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUserId);
        var empty = Empty(accountUserId);
        if (!File.Exists(_filePath))
        {
            return empty;
        }

        try
        {
            using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var document = JsonSerializer.Deserialize<GuildDisplayNameCacheDocument>(
                stream,
                SerializerOptions);
            if (document is null ||
                document.Version != GuildDisplayNameCacheDocument.CurrentVersion ||
                !string.Equals(document.AccountUserId, accountUserId, StringComparison.Ordinal))
            {
                _logger.Warning(
                    "NAME-CACHE",
                    "Guild display-name cache was ignored because its version or account scope did not match.");
                return empty;
            }

            return document with
            {
                Entries = document.Entries?.ToArray() ??
                    Array.Empty<GuildDisplayNameCacheEntry>(),
            };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warning(
                "NAME-CACHE",
                $"Guild display-name cache could not be loaded: {exception.GetType().Name}.");
            return empty;
        }
    }

    public void Save(GuildDisplayNameCacheDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The cache path has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warning(
                "NAME-CACHE",
                $"Guild display-name cache could not be saved: {exception.GetType().Name}.");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // A stale temporary file is harmless and will never be loaded as cache state.
            }
        }
    }

    private static GuildDisplayNameCacheDocument Empty(string accountUserId) => new(
        GuildDisplayNameCacheDocument.CurrentVersion,
        accountUserId,
        Array.Empty<GuildDisplayNameCacheEntry>());
}

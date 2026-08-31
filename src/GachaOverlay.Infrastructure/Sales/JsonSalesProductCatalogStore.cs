using System.Text.Json;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Infrastructure.Sales;

public sealed class JsonSalesProductCatalogStore : ISalesProductCatalogWorkspace
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly IAppLogger _logger;

    public JsonSalesProductCatalogStore(string filePath, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger ?? NullAppLogger.Instance;
    }

    public SalesProductCatalog Load()
    {
        if (!File.Exists(_filePath))
        {
            return SalesProductCatalog.Empty;
        }

        try
        {
            using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var document = JsonSerializer.Deserialize<SalesProductCatalogDocument>(
                stream,
                SerializerOptions);
            return document is null
                ? SalesProductCatalog.Empty
                : SalesProductCatalog.CreateValidated(document);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException)
        {
            _logger.Warning(
                "PRODUCT",
                $"Product catalog could not be loaded and an empty catalog is active: {exception.GetType().Name}.");
            return SalesProductCatalog.Empty;
        }
    }

    public SalesProductCatalog BuiltInCatalog => SalesProductCatalog.Empty;

    public SalesProductCatalog EffectiveCatalog => Load();

    public int OverrideCount => Load().Products.Count;

    public bool BuiltInLoaded => false;

    public bool SaveEffective(SalesProductCatalogDocument document) => Save(document);

    public bool RestoreDefault(string? guildId, string emojiId) => false;

    public bool ResetOverrides() => Save(new SalesProductCatalogDocument(
        SalesProductCatalogDocument.CurrentVersion,
        Array.Empty<SalesProductDefinition>()));

    public SalesProductDefinitionSource GetSource(string? guildId, string emojiId)
    {
        var product = Load().Products.FirstOrDefault(candidate =>
            string.Equals(candidate.GuildId ?? string.Empty, guildId ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(candidate.EmojiId, emojiId, StringComparison.Ordinal));
        return product is { Enabled: false }
            ? SalesProductDefinitionSource.Disabled
            : SalesProductDefinitionSource.Custom;
    }

    public bool Save(SalesProductCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        SalesProductCatalog catalog;
        try
        {
            catalog = SalesProductCatalog.CreateValidated(document);
        }
        catch (InvalidDataException exception)
        {
            _logger.Warning("PRODUCT", $"Product catalog was not saved: {exception.Message}");
            return false;
        }

        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The catalog path has no parent directory.");
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
                JsonSerializer.Serialize(
                    stream,
                    new SalesProductCatalogDocument(
                        SalesProductCatalogDocument.CurrentVersion,
                        catalog.Products),
                    SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                var backupPath = _filePath + ".bak";
                File.Delete(backupPath);
                File.Replace(temporaryPath, _filePath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warning(
                "PRODUCT",
                $"Product catalog could not be saved: {exception.GetType().Name}.");
            return false;
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
                // Temporary catalog files are never read as production state.
            }
        }
    }

    public bool Export(string exportPath, SalesProductCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportPath);
        ArgumentNullException.ThrowIfNull(catalog);
        var fullPath = Path.GetFullPath(exportPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The export path has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            var document = new SalesProductCatalogExportDocument(
                SalesProductCatalogDocument.CurrentVersion,
                DateTimeOffset.UtcNow,
                catalog.Products);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warning("PRODUCT", $"Product catalog export failed: {exception.GetType().Name}.");
            return false;
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
            }
        }
    }
}

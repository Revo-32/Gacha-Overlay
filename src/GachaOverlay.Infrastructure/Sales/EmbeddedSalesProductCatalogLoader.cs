using System.Reflection;
using System.Text.Json;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Infrastructure.Sales;

public static class EmbeddedSalesProductCatalogLoader
{
    public const string ResourceName =
        "GachaOverlay.Infrastructure.Sales.DefaultSalesProductCatalog.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static SalesProductCatalog Load(IAppLogger? logger = null)
    {
        var activeLogger = logger ?? NullAppLogger.Instance;
        try
        {
            using var stream = typeof(EmbeddedSalesProductCatalogLoader)
                .Assembly
                .GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                activeLogger.Warning(
                    "PRODUCT",
                    $"Embedded product catalog resource is missing: {ResourceName}.");
                return SalesProductCatalog.Empty;
            }

            var document = JsonSerializer.Deserialize<SalesProductCatalogDocument>(
                stream,
                SerializerOptions);
            if (document is null)
            {
                activeLogger.Warning("PRODUCT", "Embedded product catalog is empty.");
                return SalesProductCatalog.Empty;
            }

            var catalog = SalesProductCatalog.CreateValidated(document);
            activeLogger.Information(
                "PRODUCT",
                $"Embedded product catalog loaded mappings={catalog.Products.Count} groups={catalog.Products.Select(product => product.ProductId).Distinct(StringComparer.Ordinal).Count()}.");
            return catalog;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException)
        {
            activeLogger.Warning(
                "PRODUCT",
                $"Embedded product catalog could not be loaded; Sales continues without defaults: {exception.GetType().Name}.");
            return SalesProductCatalog.Empty;
        }
    }

    public static bool ResourceExists() => typeof(EmbeddedSalesProductCatalogLoader)
        .Assembly
        .GetManifestResourceNames()
        .Contains(ResourceName, StringComparer.Ordinal);
}

using System.Text.Json;
using System.Xml.Linq;
using GachaOverlay.Core.Sales;

namespace LSOverlay.Backend.CoreClient;

// Embed the exact shared catalog/strings, without loading Full's settings,
// token storage, diagnostics or other client infrastructure into the server.
internal static class CoreSalesResources
{
    public static SalesProductCatalog Catalog { get; } = LoadCatalog();
    public static SalesQueuePresentationStrings Strings { get; } = LoadStrings();
    private static Stream Resource(string name) => typeof(CoreSalesResources).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("Core Sales resource missing.");
    private static SalesProductCatalog LoadCatalog()
    {
        using var source = Resource("LSOverlay.CoreSales.Catalog.json");
        var document = JsonSerializer.Deserialize<SalesProductCatalogDocument>(source, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException();
        return SalesProductCatalog.CreateValidated(document);
    }
    private static SalesQueuePresentationStrings LoadStrings()
    {
        using var source = Resource("LSOverlay.CoreSales.Strings.ko.xml");
        var values = XDocument.Load(source).Root!.Elements("data").ToDictionary(item => (string)item.Attribute("name")!, item => (string)item.Element("value")!, StringComparer.Ordinal);
        string Get(string key) => values.TryGetValue(key, out var value) ? value : throw new InvalidDataException("Core Sales string missing.");
        return new(Get("SalesHealthLiveAccessible"), Get("SalesHealthConnecting"), Get("SalesHealthResyncing"),
            Get("SalesHealthRemoteConnecting"), Get("SalesHealthRemoteSynchronizing"), Get("SalesHealthRemoteResyncing"),
            Get("SalesHealthRemoteReconnecting"), Get("SalesHealthPaused"), Get("SalesHealthDegraded"),
            Get("SalesHealthDisconnected"), Get("SalesHealthRemoteError"), Get("SalesCurrentSellerFormat"),
            Get("SalesWaitingCountFormat"), Get("SalesProductFormat"), Get("SalesNextSellerFormat"), Get("SalesQueueEmpty"),
            Get("SalesNoDisplayFields"), Get("SalesNextTurnSelf"), Get("SalesCurrentTurnSelf"), Get("SalesHealthRemoteUnavailable"),
            Get("SalesHealthRemoteAccessRevoked"), Get("SalesDetailRequired"));
    }
}

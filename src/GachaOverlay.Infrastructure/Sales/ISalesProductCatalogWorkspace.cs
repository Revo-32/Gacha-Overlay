using GachaOverlay.Core.Sales;

namespace GachaOverlay.Infrastructure.Sales;

public enum SalesProductDefinitionSource
{
    BuiltIn,
    Modified,
    Custom,
    Disabled,
}

public interface ISalesProductCatalogWorkspace
{
    SalesProductCatalog BuiltInCatalog { get; }

    SalesProductCatalog EffectiveCatalog { get; }

    int OverrideCount { get; }

    bool BuiltInLoaded { get; }

    bool SaveEffective(SalesProductCatalogDocument document);

    bool RestoreDefault(string? guildId, string emojiId);

    bool ResetOverrides();

    SalesProductDefinitionSource GetSource(string? guildId, string emojiId);

    bool Export(string exportPath, SalesProductCatalog catalog);
}

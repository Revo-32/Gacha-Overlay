namespace GachaOverlay.Core.Product;

/// <summary>
/// Stable product identity values used while the application transitions to LS Overlay.
/// Technical and storage names remain unchanged until a dedicated migration milestone.
/// </summary>
public static class ProductIdentity
{
    public const string CurrentDisplayName = "Gacha Overlay";

    public const string FutureDisplayName = "LS Overlay";

    public const string LegacyTechnicalName = "GachaOverlay";

    public const string LocalDataDirectoryName = LegacyTechnicalName;
}

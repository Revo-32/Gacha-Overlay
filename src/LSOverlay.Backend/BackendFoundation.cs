using GachaOverlay.Core.Providers;

namespace LSOverlay.Backend;

/// <summary>
/// Identifies the sole production data provider used by the Windows client.
/// </summary>
public static class BackendFoundation
{
    public static OverlayDataProviderDescriptor ProviderDescriptor =>
        OverlayProviderCatalog.LsOverlayRemote;

    public static int ProtocolVersion => OverlayProtocolVersion.Current;
}

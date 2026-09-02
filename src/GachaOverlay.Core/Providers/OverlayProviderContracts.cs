namespace GachaOverlay.Core.Providers;

[Flags]
public enum OverlayDataCapabilities
{
    None = 0,
    Chat = 1 << 0,
    SalesMessages = 1 << 1,
    SalesCompletionEvidence = 1 << 2,
    HostPresence = 1 << 3,
    SalesReactionWriteBack = 1 << 4,
}

public enum OverlayProviderTransport
{
    LsOverlayProtocol,
    Test,
    Unavailable,
}

public enum OverlayProviderActivation
{
    Production,
    PreparedOnly,
    TestOnly,
    Unavailable,
}

public sealed record OverlayDataProviderDescriptor(
    string ProviderId,
    OverlayProviderTransport Transport,
    OverlayDataCapabilities Capabilities,
    OverlayProviderActivation Activation)
{
    public bool Supports(OverlayDataCapabilities capabilities) =>
        (Capabilities & capabilities) == capabilities;
}

/// <summary>
/// Identifies a normalized data provider without coupling Core to its transport or SDK.
/// Provider-specific source roles supply data through separate Core ingress contracts.
/// </summary>
public interface IOverlayDataProvider
{
    OverlayDataProviderDescriptor ProviderDescriptor { get; }
}

public static class OverlayProviderCatalog
{
    public static OverlayDataProviderDescriptor LsOverlayRemote { get; } = new(
        "ls-overlay-remote",
        OverlayProviderTransport.LsOverlayProtocol,
        OverlayDataCapabilities.Chat |
        OverlayDataCapabilities.SalesMessages |
        OverlayDataCapabilities.SalesCompletionEvidence |
        OverlayDataCapabilities.SalesReactionWriteBack |
        OverlayDataCapabilities.HostPresence,
        OverlayProviderActivation.Production);

    public static OverlayDataProviderDescriptor TestSalesCompletion { get; } = new(
        "test-sales-completion",
        OverlayProviderTransport.Test,
        OverlayDataCapabilities.SalesCompletionEvidence,
        OverlayProviderActivation.TestOnly);

    public static OverlayDataProviderDescriptor UnavailableSalesCompletion { get; } = new(
        "unavailable-sales-completion",
        OverlayProviderTransport.Unavailable,
        OverlayDataCapabilities.SalesCompletionEvidence,
        OverlayProviderActivation.Unavailable);
}

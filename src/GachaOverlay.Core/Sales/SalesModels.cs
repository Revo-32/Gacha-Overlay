using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Core.Sales;

public enum SaleDomainState
{
    Pending,
    Sold,
    Deleted,
}

public enum SaleObservationTrust
{
    NeverObserved,
    Trusted,
    TemporarilyUntrusted,
}

public enum SalesObservationStatus
{
    Disabled,
    Unavailable,
    AccessibilityUnavailable,
    Paused,
    Resyncing,
    Live,
    Partial,
    Error,
}

public sealed record SaleProduct(
    string ProductId,
    string DisplayName,
    string EmojiId,
    string EmojiName,
    int Quantity = 1)
{
    public string QuantityDisplayName => Quantity > 1
        ? $"{DisplayName} x{Quantity}"
        : DisplayName;
}

public static class SalesProductSummaryFormatter
{
    public static string Format(IEnumerable<SaleProduct>? products)
    {
        if (products is null)
        {
            return string.Empty;
        }

        return string.Join(
            " · ",
            products
                .Where(product => product.Quantity > 0 &&
                    !string.IsNullOrWhiteSpace(product.DisplayName))
                .Select(product => product.QuantityDisplayName));
    }
}

public sealed record SaleRecord(
    string MessageId,
    string GuildId,
    string ChannelId,
    string AuthorId,
    DateTimeOffset? CreatedAt,
    long SourceRevision,
    string SourceFingerprint,
    string AuthorUsername,
    string? AuthorGlobalDisplayName,
    string? AuthorGuildNickname,
    DiscordDisplayNameSource AuthorGuildNicknameObservationSource,
    GuildDisplayNameResolution DisplayName,
    SaleProduct? Product,
    SaleDomainState DomainState,
    SaleObservationTrust ObservationTrust,
    DateTimeOffset? LastTrustedObservationAt,
    long LastObservationGeneration,
    DateTimeOffset? DeletedAt,
    IReadOnlyList<SaleProduct>? Products = null)
{
    public bool IsProvisional => ObservationTrust == SaleObservationTrust.NeverObserved;

    public bool ParticipatesInQueue => DomainState == SaleDomainState.Pending;

    public IReadOnlyList<SaleProduct> AllProducts => Products is { Count: > 0 }
        ? Products
        : Product is null
            ? Array.Empty<SaleProduct>()
            : new[] { Product };
}

public sealed record SalesQueueEntry(
    string MessageId,
    string GuildId,
    string AuthorId,
    DateTimeOffset? CreatedAt,
    string DisplayName,
    DiscordDisplayNameSource DisplayNameSource,
    bool IsExactGuildNickname,
    SaleProduct? Product,
    SaleObservationTrust ObservationTrust,
    IReadOnlyList<SaleProduct>? Products = null)
{
    public bool IsProvisional => ObservationTrust == SaleObservationTrust.NeverObserved;

    public IReadOnlyList<SaleProduct> AllProducts => Products is { Count: > 0 }
        ? Products
        : Product is null
            ? Array.Empty<SaleProduct>()
            : new[] { Product };

    public string ProductSummary => SalesProductSummaryFormatter.Format(AllProducts);
}

public sealed record SalesQueueSnapshot(
    long Revision,
    bool IsTrackingEnabled,
    IReadOnlyList<SalesQueueEntry> ActiveItems,
    SalesQueueEntry? CurrentSeller,
    int ActiveCount,
    int WaitingCount,
    SalesQueueEntry? NextWaitingEntry,
    bool CurrentSellerIsSelf,
    bool NextSellerIsSelf,
    bool ContainsUnverifiedActiveItems,
    bool IsObservationSourceAvailable,
    SalesObservationStatus ObservationStatus,
    DateTimeOffset UpdatedAt,
    string? AuthenticatedUserId = null)
{
    public static SalesQueueSnapshot Empty { get; } = new(
        0,
        true,
        Array.Empty<SalesQueueEntry>(),
        null,
        0,
        0,
        null,
        false,
        false,
        false,
        false,
        SalesObservationStatus.Unavailable,
        DateTimeOffset.MinValue);
}

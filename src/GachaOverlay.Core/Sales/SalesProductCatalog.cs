using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Localization;

namespace GachaOverlay.Core.Sales;

public sealed record SalesProductDefinition(
    string ProductId,
    string EmojiId,
    string? EmojiName,
    string? GuildId,
    IReadOnlyDictionary<string, string> DisplayNames,
    bool Enabled = true,
    string? GroupName = null);

public sealed record SalesEmojiInventoryItem(
    string EmojiId,
    string EmojiName,
    string GuildId,
    bool Animated,
    int UsageCount,
    bool IsMapped,
    string? SourceText = null)
{
    public string PreviewUrl =>
        $"https://cdn.discordapp.com/emojis/{EmojiId}.{(Animated ? "gif" : "png")}?size=64&quality=lossless";
}

public sealed record SalesProductCatalogDocument(
    int Version,
    IReadOnlyList<SalesProductDefinition> Products)
{
    public const int LegacyVersion = 1;
    public const int CurrentVersion = 2;
}

public sealed class SalesProductCatalog
{
    private readonly IReadOnlyList<SalesProductDefinition> _products;

    private SalesProductCatalog(IReadOnlyList<SalesProductDefinition> products)
    {
        _products = products;
    }

    public static SalesProductCatalog Empty { get; } = new(
        Array.Empty<SalesProductDefinition>());

    public IReadOnlyList<SalesProductDefinition> Products => _products;

    public static SalesProductCatalog CreateValidated(SalesProductCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version is not (
            SalesProductCatalogDocument.LegacyVersion or
            SalesProductCatalogDocument.CurrentVersion))
        {
            throw new InvalidDataException(
                $"Unsupported product catalog version '{document.Version}'.");
        }

        var valid = new List<SalesProductDefinition>();
        var keys = new HashSet<(string GuildId, string EmojiId)>();
        foreach (var product in document.Products ?? Array.Empty<SalesProductDefinition>())
        {
            if (string.IsNullOrWhiteSpace(product.ProductId) ||
                string.IsNullOrWhiteSpace(product.EmojiId))
            {
                throw new InvalidDataException("ProductId and EmojiId are required.");
            }

            var key = (product.GuildId ?? string.Empty, product.EmojiId);
            if (!keys.Add(key))
            {
                throw new InvalidDataException(
                    $"Duplicate EmojiId '{product.EmojiId}' in the same Guild scope.");
            }

            var names = product.DisplayNames is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(
                    product.DisplayNames,
                    StringComparer.OrdinalIgnoreCase);
            valid.Add(product with
            {
                ProductId = product.ProductId.Trim(),
                EmojiId = product.EmojiId.Trim(),
                EmojiName = NullIfBlank(product.EmojiName),
                GuildId = NullIfBlank(product.GuildId),
                DisplayNames = names,
                GroupName = ResolveGroupName(product, names),
            });
        }

        if (document.Version == SalesProductCatalogDocument.LegacyVersion)
        {
            var groupIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < valid.Count; index++)
            {
                var product = valid[index];
                if (string.IsNullOrWhiteSpace(product.GroupName))
                {
                    continue;
                }

                var key = $"{product.GuildId ?? string.Empty}\u001f{product.GroupName.Trim()}";
                if (!groupIds.TryGetValue(key, out var productId))
                {
                    groupIds[key] = product.ProductId;
                    continue;
                }

                valid[index] = product with { ProductId = productId };
            }
        }

        return new SalesProductCatalog(valid.ToArray());
    }

    public SaleProduct? MapFirst(
        string guildId,
        IReadOnlyList<DiscordCustomEmoji> emojis,
        string locale) => MapAll(guildId, emojis, locale).FirstOrDefault();

    public IReadOnlyList<SaleProduct> MapAll(
        string guildId,
        IReadOnlyList<DiscordCustomEmoji> emojis,
        string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentNullException.ThrowIfNull(emojis);

        var mapped = new List<SaleProduct>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var emoji in emojis)
        {
            var definition = FindByEmojiId(guildId, emoji.EmojiId) ??
                FindByEmojiName(guildId, emoji.Name);
            if (definition is null)
            {
                continue;
            }

            var displayName = ResolveDisplayName(definition, locale);
            if (displayName is null)
            {
                continue;
            }

            if (!indexes.TryGetValue(definition.ProductId, out var index))
            {
                indexes[definition.ProductId] = mapped.Count;
                mapped.Add(new SaleProduct(
                    definition.ProductId,
                    displayName,
                    emoji.EmojiId,
                    emoji.Name));
            }
            else
            {
                mapped[index] = mapped[index] with
                {
                    Quantity = mapped[index].Quantity + 1,
                };
            }
        }

        return mapped;
    }

    public SaleProduct Relocalize(string guildId, SaleProduct product, string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentNullException.ThrowIfNull(product);
        var definition = _products.FirstOrDefault(candidate =>
                candidate.Enabled &&
                string.Equals(candidate.ProductId, product.ProductId, StringComparison.Ordinal) &&
                string.Equals(candidate.GuildId, guildId, StringComparison.Ordinal)) ??
            _products.FirstOrDefault(candidate =>
                candidate.Enabled &&
                string.Equals(candidate.ProductId, product.ProductId, StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(candidate.GuildId));
        var displayName = definition is null ? null : ResolveDisplayName(definition, locale);
        return displayName is null
            ? product
            : product with { DisplayName = displayName };
    }

    private SalesProductDefinition? FindByEmojiId(string guildId, string emojiId) =>
        _products.FirstOrDefault(product =>
            product.Enabled &&
            string.Equals(product.EmojiId, emojiId, StringComparison.Ordinal) &&
            string.Equals(product.GuildId, guildId, StringComparison.Ordinal)) ??
        _products.FirstOrDefault(product =>
            product.Enabled &&
            string.Equals(product.EmojiId, emojiId, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(product.GuildId));

    private SalesProductDefinition? FindByEmojiName(string guildId, string emojiName) =>
        _products.FirstOrDefault(product =>
            product.Enabled &&
            !string.IsNullOrWhiteSpace(product.EmojiName) &&
            string.Equals(product.EmojiName, emojiName, StringComparison.Ordinal) &&
            string.Equals(product.GuildId, guildId, StringComparison.Ordinal)) ??
        _products.FirstOrDefault(product =>
            product.Enabled &&
            !string.IsNullOrWhiteSpace(product.EmojiName) &&
            string.Equals(product.EmojiName, emojiName, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(product.GuildId));

    private static string? ResolveDisplayName(
        SalesProductDefinition product,
        string locale)
    {
        var normalized = SupportedLocales.NormalizeOrEnglish(locale);
        if (product.DisplayNames.TryGetValue(normalized, out var localized) &&
            !string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        if (product.DisplayNames.TryGetValue(SupportedLocales.English, out var english) &&
            !string.IsNullOrWhiteSpace(english))
        {
            return english;
        }

        if (!string.IsNullOrWhiteSpace(product.GroupName))
        {
            return product.GroupName;
        }

        return string.IsNullOrWhiteSpace(product.EmojiName) ? null : product.EmojiName;
    }

    private static string? ResolveGroupName(
        SalesProductDefinition product,
        IReadOnlyDictionary<string, string> displayNames)
    {
        if (!string.IsNullOrWhiteSpace(product.GroupName))
        {
            return product.GroupName.Trim();
        }

        foreach (var locale in new[]
                 {
                     SupportedLocales.English,
                     SupportedLocales.Korean,
                     SupportedLocales.Japanese,
                 })
        {
            if (displayNames.TryGetValue(locale, out var localized) &&
                !string.IsNullOrWhiteSpace(localized))
            {
                return localized.Trim();
            }
        }

        return displayNames.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
            ?? NullIfBlank(product.EmojiName);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record SalesProductCatalogExportDocument(
    int Version,
    DateTimeOffset ExportedAt,
    IReadOnlyList<SalesProductDefinition> Products);

using System.Text.RegularExpressions;
using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Core.Sales;

public enum SaleParseStatus { Parsed, PartiallyParsed, Ambiguous, Unknown }

public sealed record NormalizedSalePost(IReadOnlyList<SaleProduct> Products, SaleParseStatus Status, [property: System.Text.Json.Serialization.JsonIgnore] string? DetailSource);

/// <summary>One transport-independent parser. No network, cache, timers or raw-text logging.</summary>
public static partial class SalesPostNormalizer
{
    public const int MaximumSourceLength = 4096;
    private static readonly IReadOnlyDictionary<string, int> QuantityIds = new Dictionary<string, int>
    {
        ["1440756004404072479"] = 2,
        ["1440755981960216596"] = 3,
        ["1440755960451698741"] = 4,
        ["1444224678817169519"] = 5,
    };

    [GeneratedRegex(@"<a?:(?<name>[A-Za-z0-9_]+):(?<id>[0-9]+)>|(?<keycap>[0-9])\uFE0F?\u20E3|(?<number>[0-9]+)대창|(?<multiplier>x[0-9]+)|벙나항|벙나|나클만|항스패|격납고|붕키|봉카|나끌|낙글|낙굴|벙커|나클|대창|스패|붕|나|낙|엘", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Tokens();

    public static NormalizedSalePost Parse(string guildId, string? content,
        IReadOnlyList<DiscordCustomEmoji> emojis, SalesProductCatalog catalog, string locale)
    {
        var source = content ?? "";
        var truncated = source.Length > MaximumSourceLength;
        if (truncated) source = source[..MaximumSourceLength];
        var covered = new bool[source.Length];
        var products = new List<SaleProduct>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var explicitQuantities = new HashSet<int>();
        var representedEmojiIds = new HashSet<string>(StringComparer.Ordinal);
        var special = catalog.ResolveCanonical(guildId, "스패", locale);
        var specialEmojiCount = 0;
        var ambiguous = truncated;
        var invalidSpecialQuantity = false;

        void Add(SaleProduct? product, bool emoji = false)
        {
            if (product is null) return;
            if (emoji && product.ProductId == special?.ProductId) specialEmojiCount++;
            if (positions.ContainsKey(product.ProductId)) return;
            positions[product.ProductId] = products.Count;
            products.Add(product with { Quantity = 1 });
        }
        bool Quantity(string id, string name)
        {
            int quantity;
            if (!string.IsNullOrWhiteSpace(id))
            {
                if (!QuantityIds.TryGetValue(id, out quantity)) return false;
            }
            else
            {
                if (name.Length != 2 || name[0] != 'x' || name[1] is < '2' or > '5') return false;
                quantity = name[1] - '0';
            }
            explicitQuantities.Add(quantity);
            return true;
        }
        bool AddEmoji(DiscordCustomEmoji emoji)
        {
            if (Quantity(emoji.EmojiId, emoji.Name)) return true;
            var mapped = catalog.MapAll(guildId, new[] { emoji }, locale).FirstOrDefault();
            Add(mapped, true);
            return mapped is not null;
        }

        foreach (Match match in Tokens().Matches(source))
        {
            var value = match.Value;
            var isCustom = match.Groups["id"].Success;
            var isKeycap = match.Groups["keycap"].Success;
            // Explicit full 벙커 can occur in decorative prose. All short aliases,
            // quantity patterns and other tokens require reliable word boundaries.
            if (!isCustom && !isKeycap && value != "벙커" && !IsBounded(source, match.Index, match.Length)) continue;
            var recognized = true;
            if (isCustom)
            {
                var id = match.Groups["id"].Value;
                representedEmojiIds.Add(id);
                recognized = AddEmoji(new DiscordCustomEmoji(id, match.Groups["name"].Value, value.StartsWith("<a:", StringComparison.Ordinal)));
            }
            else if (isKeycap || match.Groups["multiplier"].Success)
            {
                var numeric = isKeycap ? match.Groups["keycap"].Value : value[1..];
                if (int.TryParse(numeric, out var count) && count is >= 1 and <= 5) explicitQuantities.Add(count);
                else { invalidSpecialQuantity = true; ambiguous = true; }
            }
            else if (match.Groups["number"].Success)
            {
                if (int.TryParse(match.Groups["number"].Value, out var count) && count is >= 1 and <= 5)
                { Add(special); explicitQuantities.Add(count); }
                else { invalidSpecialQuantity = true; ambiguous = true; }
            }
            else
            {
                var names = value switch
                {
                    "벙나항" => new[] { "벙커", "나클", "항스패" },
                    "벙나" => new[] { "벙커", "나클" },
                    "벙커" or "붕키" or "봉카" or "붕" => new[] { "벙커" },
                    "나클" or "나클만" or "나끌" or "낙글" or "낙굴" or "나" or "낙" => new[] { "나클" },
                    "대창" or "스패" => new[] { "스패" },
                    "격납고" or "항스패" => new[] { "항스패" },
                    "엘" => new[] { "엘" },
                    _ => Array.Empty<string>(),
                };
                foreach (var name in names)
                {
                    var product = catalog.ResolveCanonical(guildId, name, locale);
                    if (product is null) recognized = false;
                    Add(product);
                }
            }
            if (recognized) Array.Fill(covered, true, match.Index, match.Length);
        }
        // Some transports provide structured emoji without the raw <:...> syntax.
        foreach (var emoji in emojis.Take(128))
            if (!representedEmojiIds.Contains(emoji.EmojiId)) AddEmoji(emoji);

        if (special is not null && positions.TryGetValue(special.ProductId, out var specialIndex))
        {
            if (explicitQuantities.Count > 1 || invalidSpecialQuantity || (explicitQuantities.Count == 0 && specialEmojiCount > 5))
            {
                ambiguous = true;
                // Quantity is part of this canonical item: omit it rather than guess x1.
                products.RemoveAt(specialIndex);
            }
            else products[specialIndex] = products[specialIndex] with
            { Quantity = explicitQuantities.Count == 1 ? explicitQuantities.Single() : Math.Max(1, specialEmojiCount) };
        }
        var unknown = source.Where((c, index) => !covered[index] && char.IsLetterOrDigit(c)).Any();
        var status = ambiguous ? SaleParseStatus.Ambiguous
            : products.Count == 0 ? SaleParseStatus.Unknown
            : unknown ? SaleParseStatus.PartiallyParsed : SaleParseStatus.Parsed;
        return new(products.ToArray(), status, status == SaleParseStatus.Parsed ? null : source);
    }

    private static bool IsBounded(string source, int index, int length) =>
        (index == 0 || !IsWord(source[index - 1])) &&
        (index + length == source.Length || !IsWord(source[index + length]));
    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
}

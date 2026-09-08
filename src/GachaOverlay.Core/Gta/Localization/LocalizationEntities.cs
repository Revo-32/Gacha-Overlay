using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta.Localization;

public enum LocalizationEntityKind { Vehicle, CreatorJobTitle, Property, PreservedActivity }
internal sealed record LocalizationEntitySpan(int Start, int Length);

// Evidence is an explicit semantic type, or a canonical manufacturer followed by a model.
// This is a manufacturer vocabulary, not a model dictionary or an unknown-English fallback.
internal static class LocalizationEntities
{
    private static readonly Regex Vehicle = new(@"\b(?:Grotti|Bravado|Coil|Karin|Pfister|Imponte|Penaud|Mammoth|Ocelot|Pegassi|Dinka|Annis|Benefactor|Obey|Ubermacht|Übermacht|Dewbauchee|Vapid|Declasse|Enus|Progen|Truffade|Buckingham|HVY|Lampadati|Överflöd|Overflod|Western Company|Canis)\s+[A-Z0-9][A-Za-z0-9'’-]*(?:[ \t]+(?:[A-Z0-9][A-Za-z0-9'’-]*|de|la)){0,4}\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Creator = new("\\bCommunity(?: Mission| Race)? Series\\s+(?:job|race|mission)\\s+(?:named|titled)\\s+(?<title>\"[^\"\\r\\n]{1,160}\"|“[^”\\r\\n]{1,160}”)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    // Explicit reward-item and depot-owner constructions identify an entity role;
    // they do not turn the surrounding campaign goals or arbitrary title case into names.
    private static readonly Regex RewardItem = new(@"\b(?:get|receive)\s+GTA\$\d[\d,]*\s+\+\s+the\s+(?<title>[^.!?;\r\n]{1,120}\s+(?:Tracksuit|Sweatsuit))(?=[.!?;]|$)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex DirectRewardItem = new(@"\b(?:get|receive)\s+the\s+(?<title>[^.!?;\r\n]{1,120}?\s+(?:Tracksuit|Sweatsuit))(?=\s+and\b|[.!?;]|$)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex NamedWeeklyList = new(@"(?:clothing rewards|Primary Targets):\s*(?<title>[^\r\n.!?]{1,240})",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex DepotOwners = new(@"\((?<title>[A-Z][A-Za-z'’-]*(?:\s+[A-Z][A-Za-z'’-]*){0,4}(?:\s+&\s+[A-Z][A-Za-z'’-]*(?:\s+[A-Z][A-Za-z'’-]*){0,4})*)\s+depots\)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    // The named delivery activity immediately qualified by explicit depot owners is
    // preserve-until-verified too; generic title-cased prose has no such evidence.
    private static readonly Regex DepotActivityPrefix = new(@"\b(?<title>[A-Z][A-Za-z'’-]*(?:\s+[A-Z][A-Za-z'’-]*){0,3}\s+Deliveries)\s+\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<LocalizationEntitySpan> Extract(string text, LocalizationEntityKind? kind)
    {
        if (kind is not null) return [new(0, text.Length)];
        return Vehicle.Matches(text).Select(m => new LocalizationEntitySpan(m.Index, m.Length))
            .Concat(new[] { Creator, RewardItem, DirectRewardItem, DepotOwners }.SelectMany(pattern => pattern.Matches(text))
                .Select(m => m.Groups["title"]).Select(g => new LocalizationEntitySpan(g.Index, g.Length)))
            .Concat(NamedWeeklyList.Matches(text).SelectMany(m =>
                Regex.Matches(m.Groups["title"].Value, @"(?:^|,\s*(?:or\s+)?|\s+&\s+)(?<entity>.*?)(?=,|\s+&\s+|$)",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
                    .Select(part => part.Groups["entity"])
                    .Where(part => part.Length > 0)
                    .Select(part => new LocalizationEntitySpan(m.Groups["title"].Index + part.Index, part.Length))))
            .Concat(DepotOwners.Matches(text).Select(m => DepotActivityPrefix.Match(text[..m.Index]))
                .Where(m => m.Success).Select(m => m.Groups["title"]).Select(g => new LocalizationEntitySpan(g.Index, g.Length)))
            .OrderBy(e => e.Start).ToArray();
    }
}

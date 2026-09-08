using System.Collections.ObjectModel;

namespace GachaOverlay.Core.Gta;

public enum GtaTranslationSource
{
    RockstarOfficial,
    Curated,
    OriginalFallback,
}

public sealed record GtaGlossaryEntry(
    string CanonicalId,
    string EnglishName,
    IReadOnlyList<string> EnglishAliases,
    string KoreanDisplayName,
    string Category,
    GtaTranslationSource TranslationSource);

public sealed class GtaEventVocabulary
{
    private static readonly IReadOnlyDictionary<string, string[]> FamilyPatterns =
        new ReadOnlyDictionary<string, string[]>(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["gun_van"] = ["GUN VAN"],
            ["salvage_yard"] = ["SALVAGE YARD"],
            ["test_rides"] = ["TEST RIDES", "FREE VEHICLES"],
            ["premium_race"] = ["PREMIUM RACE"],
            ["time_trial"] = ["TIME TRIAL", "TRIALS"],
            ["discounts"] = ["DISCOUNTS", "OFFERS"],
            ["prize_ride"] = ["PRIZE RIDE"],
            ["podium"] = ["PODIUM VEHICLE", "LUCKY WHEEL"],
            ["weekly_challenge"] = ["WEEKLY CHALLENGE", "WEEKLY CHALLENGES"],
            ["bonuses"] = ["BONUSES", "BONUS GTA$", "BONUS REWARDS"],
            ["free_items"] = ["FREE ITEMS", "FREE VEHICLE", "FREE REWARDS"],
            ["login_rewards"] = ["LOGIN REWARD", "LOG IN TO RECEIVE"],
            ["rotating_content"] = ["ROTATING CONTENT", "SHOWROOM", "COMMUNITY SERIES", "FEATURED SERIES"],
        });

    private static readonly string[] WeeklyAnchorIds =
        FamilyPatterns.Keys.Where(key => key != "weekly_challenge").ToArray();

    public static IReadOnlyList<string> ChallengeActions { get; } = Array.AsReadOnly(new[]
    {
        "EARN", "COMPLETE", "WIN", "PARTICIPATE", "SELL", "SOURCE", "DELIVER",
        "PURCHASE", "CLAIM", "PLAY", "FINISH", "PLACE", "SURVIVE", "DESTROY",
        "STEAL", "COLLECT",
    });

    public static IReadOnlyList<string> RewardModifierTerms { get; } = Array.AsReadOnly(new[]
    {
        "GTA$", "RP", "CASINO CHIPS", "RESEARCH PROGRESS", "SPEED", "FREE", "OFF",
        "FIRST TIME COMPLETION", "LOGIN REWARD", "BONUS REWARD", "2X", "3X", "4X",
        "5X", "6X",
    });

    public static IReadOnlyList<GtaGlossaryEntry> Glossary { get; } = GtaTranslationGlossary.Default.Entries;

    public static int HeadingFamilyCount => FamilyPatterns.Count;

    public static int KnownActivityAliasCount => Glossary.Sum(entry => 1 + entry.EnglishAliases.Count);

    public string? MatchHeadingFamily(string? line)
    {
        var identity = GtaEventTextNormalizer.NormalizeIdentity(line);
        if (identity.Length == 0)
        {
            return null;
        }

        foreach (var family in FamilyPatterns)
        {
            if (family.Value.Any(pattern => identity.StartsWith(pattern, StringComparison.Ordinal) ||
                    identity.Contains(pattern, StringComparison.Ordinal)))
            {
                return family.Key;
            }
        }

        return null;
    }

    public bool IsExactHeading(string line) => FamilyPatterns.Values.Any(patterns =>
        patterns.Contains(GtaEventTextNormalizer.NormalizeIdentity(line), StringComparer.Ordinal));

    public IReadOnlySet<string> FindWeeklyAnchorFamilies(IEnumerable<string> lines) =>
        lines.Select(MatchHeadingFamily)
            .Where(family => family is not null && WeeklyAnchorIds.Contains(family, StringComparer.Ordinal))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

    public bool IsKnownHeading(string line) => MatchHeadingFamily(line) is not null ||
        IsCampaignHeading(line);

    public static bool IsCampaignHeading(string line)
    {
        var identity = GtaEventTextNormalizer.NormalizeIdentity(line);
        return identity.Contains("EVENT BREAKDOWN", StringComparison.Ordinal) ||
            identity.Contains("FULL SCHEDULE", StringComparison.Ordinal) ||
            identity.Contains("BY WEEK", StringComparison.Ordinal) ||
            identity.Contains("MONTH LONG WEEKLY CHALLENGES", StringComparison.Ordinal) ||
            identity.Contains("GOALS", StringComparison.Ordinal) ||
            identity.Contains("REWARDS", StringComparison.Ordinal) ||
            identity.Contains("UPCOMING", StringComparison.Ordinal);
    }

    public bool TryTranslate(string? value, out string translated)
    {
        translated = string.Empty;
        var identity = GtaEventTextNormalizer.NormalizeIdentity(value);
        if (identity.Length == 0)
        {
            return false;
        }

        var entry = Glossary
            .OrderByDescending(candidate => candidate.EnglishName.Length)
            .FirstOrDefault(candidate =>
                identity.Contains(GtaEventTextNormalizer.NormalizeIdentity(candidate.EnglishName), StringComparison.Ordinal) ||
                candidate.EnglishAliases.Any(alias =>
                    identity.Contains(GtaEventTextNormalizer.NormalizeIdentity(alias), StringComparison.Ordinal)));
        if (entry is null || entry.TranslationSource == GtaTranslationSource.OriginalFallback)
        {
            return false;
        }

        translated = entry.KoreanDisplayName;
        return true;
    }

}

public sealed record GtaUnknownVocabularyEntry(string Kind, string Value, int Count);

public sealed class GtaUnknownVocabularyReport
{
    public const int MaximumEntries = 64;
    private readonly object _sync = new();
    private readonly Dictionary<(string Kind, string Value), int> _counts = new();
    private readonly Queue<(string Kind, string Value)> _order = new();

    public void Observe(string kind, string? value)
    {
        var normalizedKind = Bound(kind, 32);
        var normalizedValue = Bound(GtaEventTextNormalizer.Normalize(value), 160);
        if (normalizedKind.Length == 0 || normalizedValue.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            var key = (normalizedKind, normalizedValue);
            if (_counts.TryGetValue(key, out var count))
            {
                _counts[key] = checked(count + 1);
                return;
            }

            while (_counts.Count >= MaximumEntries && _order.TryDequeue(out var oldest))
            {
                _counts.Remove(oldest);
            }

            _counts[key] = 1;
            _order.Enqueue(key);
        }
    }

    public IReadOnlyList<GtaUnknownVocabularyEntry> Snapshot()
    {
        lock (_sync)
        {
            return _counts.Select(pair => new GtaUnknownVocabularyEntry(
                    pair.Key.Kind,
                    pair.Key.Value,
                    pair.Value))
                .OrderByDescending(entry => entry.Count)
                .ThenBy(entry => entry.Kind, StringComparer.Ordinal)
                .ThenBy(entry => entry.Value, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static string Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Length <= maximum ? value.Trim() : value.Trim()[..maximum];
}

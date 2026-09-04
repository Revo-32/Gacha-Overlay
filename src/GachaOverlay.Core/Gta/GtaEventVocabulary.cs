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
            ["rotating_content"] = ["SHOWROOM", "COMMUNITY SERIES", "FEATURED SERIES"],
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

    public static IReadOnlyList<GtaGlossaryEntry> Glossary { get; } = Array.AsReadOnly(new[]
    {
        Curated("special_cargo", "Special Cargo", "특수 화물", "Business", "Special Cargo Sales"),
        Curated("air_freight_cargo", "Air Freight Cargo", "항공 화물", "Business", "Air Freight"),
        Curated("bunker", "Bunker", "벙커", "Business", "Gunrunning"),
        Curated("nightclub", "Nightclub", "나이트클럽", "Business"),
        Curated("acid_lab", "Acid Lab", "산성 연구소", "Business"),
        Curated("salvage_yard", "Salvage Yard", "폐차장", "Business", "Salvage Yard Robbery"),
        Curated("gun_van", "Gun Van", "건 밴", "System"),
        Curated("time_trial", "Time Trial", "타임 트라이얼", "Activity"),
        Curated("hsw_time_trial", "HSW Time Trial", "HSW 타임 트라이얼", "Activity"),
        Curated("rc_time_trial", "RC Time Trial", "RC 타임 트라이얼", "Activity"),
        Curated("community_series", "Community Series", "커뮤니티 시리즈", "Activity"),
        Curated("adversary_mode", "Adversary Mode", "대적 모드", "Activity"),
        Curated("survival", "Survival", "서바이벌", "Activity"),
        Curated("contact_mission", "Contact Mission", "연락책 임무", "Activity"),
        Curated("special_vehicle_work", "Special Vehicle Work", "특수 차량 임무", "Activity"),
        Curated("auto_shop_contract", "Auto Shop Contract", "튜닝 샵 계약", "Activity"),
        Curated("security_contract", "Security Contract", "보안 계약", "Activity"),
        Curated("payphone_hit", "Payphone Hit", "공중전화 암살", "Activity"),
        Curated("casino_chips", "Casino Chips", "카지노 칩", "Reward"),
        Curated("research_progress", "Research Progress", "연구 진행도", "Reward"),
        Fallback("unknown_entity", "Unknown Entity", "Entity"),
        Fallback("future_activity", "Future Activity", "Activity"),
    });

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

    private static GtaGlossaryEntry Curated(
        string id,
        string english,
        string korean,
        string category,
        params string[] aliases) =>
        new(id, english, aliases, korean, category, GtaTranslationSource.Curated);

    private static GtaGlossaryEntry Fallback(string id, string english, string category) =>
        new(id, english, Array.Empty<string>(), english, category, GtaTranslationSource.OriginalFallback);
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

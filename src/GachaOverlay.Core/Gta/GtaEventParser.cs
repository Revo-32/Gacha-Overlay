using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta;

public sealed partial class GtaEventParser
{
    private readonly GtaEventVocabulary _vocabulary;
    private readonly GtaUnknownVocabularyReport _unknown;
    private readonly KstResetSchedule _schedule;

    public GtaEventParser(
        GtaEventVocabulary? vocabulary = null,
        GtaUnknownVocabularyReport? unknown = null,
        KstResetSchedule? schedule = null)
    {
        _vocabulary = vocabulary ?? new GtaEventVocabulary();
        _unknown = unknown ?? new GtaUnknownVocabularyReport();
        _schedule = schedule ?? new KstResetSchedule();
    }

    public GtaParsedEvent Parse(
        CanonicalEventDocument document,
        GtaEventClassification classification)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(classification);
        return classification.Kind switch
        {
            GtaEventClassificationKind.WeeklyBulletin =>
                new GtaParsedEvent(classification, ParseWeek(document), null),
            GtaEventClassificationKind.MultiWeekCampaign =>
                new GtaParsedEvent(classification, null, ParseCampaign(document)),
            _ => new GtaParsedEvent(classification, null, null),
        };
    }

    private GtaEventWeek ParseWeek(CanonicalEventDocument document)
    {
        var lines = MeaningfulLines(document.CanonicalText);
        var ranges = GtaEventDateParser.FindRanges(document.CanonicalText, document.ReceivedAt, _schedule);
        var dateRange = ranges.FirstOrDefault();
        var effectiveFrom = dateRange?.StartAt ?? InferWeeklyStart(document.ReceivedAt);
        var weekKey = effectiveFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var challenge = ParseWeeklyChallenge(lines, document.ReceivedAt);
        var bonuses = new List<GtaSemanticEventItem>();
        var discounts = new List<GtaSemanticEventItem>();
        var freeItems = new List<GtaSemanticEventItem>();
        var other = new List<GtaSemanticEventItem>();
        string? section = null;

        foreach (var line in lines)
        {
            var family = _vocabulary.MatchHeadingFamily(line);
            if (family is not null)
            {
                section = family;
                continue;
            }

            if (IsOpening(line) || challenge?.OriginalText == line || LooksLikeDateOnly(line, document.ReceivedAt))
            {
                continue;
            }

            if (TryParseItem(line, section, document.ReceivedAt, out var item))
            {
                AddItem(item!, bonuses, discounts, freeItems, other);
                continue;
            }

            if (section is "bonuses" or "gun_van" or "salvage_yard" or "premium_race" or
                "time_trial" or "prize_ride" or "podium" or "rotating_content" or "login_rewards")
            {
                var kind = section == "login_rewards"
                    ? GtaEventItemKind.LoginReward
                    : section == "rotating_content" ? GtaEventItemKind.RotatingContent : GtaEventItemKind.Note;
                other.Add(CreateItem(kind, line, line, null, null, Array.Empty<GtaRewardType>(), null, null));
                if (!_vocabulary.IsKnownHeading(line) && LooksLikeHeading(line))
                {
                    _unknown.Observe("heading", line);
                }
            }
        }

        return new GtaEventWeek(
            weekKey,
            effectiveFrom,
            dateRange?.EndAt,
            lines.FirstOrDefault(line =>
                !IsOpening(line) && !_vocabulary.IsKnownHeading(line) && !LooksLikeDateOnly(line, document.ReceivedAt)),
            challenge,
            BoundDistinct(bonuses, 64),
            BoundDistinct(discounts, 128),
            BoundDistinct(freeItems, 32),
            BoundDistinct(other, 64),
            document.SourceMessageId,
            document.EditedAt ?? document.ReceivedAt);
    }

    private GtaEventCampaign ParseCampaign(CanonicalEventDocument document)
    {
        var lines = MeaningfulLines(document.CanonicalText);
        var ranges = GtaEventDateParser.FindRanges(document.CanonicalText, document.ReceivedAt, _schedule);
        var title = lines.FirstOrDefault(line =>
            !LooksLikeDateOnly(line, document.ReceivedAt)) ?? "GTA Online Event";
        var goals = new List<string>();
        var rewards = new List<string>();
        var planned = new List<GtaCampaignWeek>();
        string? section = null;
        foreach (var line in lines)
        {
            var identity = GtaEventTextNormalizer.NormalizeIdentity(line);
            if (identity.Contains("GOAL", StringComparison.Ordinal) ||
                identity.Contains("CHALLENGE", StringComparison.Ordinal))
            {
                section = "goals";
                continue;
            }

            if (identity.Contains("REWARD", StringComparison.Ordinal))
            {
                section = "rewards";
                continue;
            }

            if (GtaEventDateParser.TryFindFirstRange(line, document.ReceivedAt, out var range))
            {
                planned.Add(new GtaCampaignWeek(
                    range!.StartAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    Bound(line, 256),
                    range.StartAt,
                    range.EndAt));
            }

            if (line == title || GtaEventVocabulary.IsCampaignHeading(line))
            {
                continue;
            }

            if (section == "goals")
            {
                goals.Add(Bound(line, 256));
            }
            else if (section == "rewards")
            {
                rewards.Add(Bound(line, 256));
            }
        }

        var keyMaterial = string.Join('|', new[] { title }
            .Concat(ranges.Select(range => $"{range.StartAt:yyyyMMdd}-{range.EndAt:yyyyMMdd}")));
        return new GtaEventCampaign(
            StableKey("campaign", keyMaterial),
            Bound(title, 256),
            ranges.Count == 0 ? null : ranges.Min(range => range.StartAt),
            ranges.Count == 0 ? null : ranges.Max(range => range.EndAt),
            goals.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray(),
            rewards.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray(),
            planned.DistinctBy(week => week.WeekKey).Take(8).ToArray(),
            document.SourceMessageId,
            document.EditedAt ?? document.ReceivedAt);
    }

    private GtaSemanticChallenge? ParseWeeklyChallenge(
        IReadOnlyList<string> lines,
        DateTimeOffset reference)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (_vocabulary.MatchHeadingFamily(lines[index]) != "weekly_challenge")
            {
                continue;
            }

            var text = lines.Skip(index + 1).FirstOrDefault(line =>
                _vocabulary.MatchHeadingFamily(line) is null && !LooksLikeDateOnly(line, reference));
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var identity = GtaEventTextNormalizer.NormalizeIdentity(text);
            var action = GtaEventVocabulary.ChallengeActions.FirstOrDefault(candidate =>
                identity.StartsWith(candidate + " ", StringComparison.Ordinal) || identity == candidate);
            var target = action is null
                ? text
                : text[Math.Min(text.Length, action.Length)..].TrimStart(' ', ':', '-');
            int? count = FirstIntegerRegex().Match(text) is { Success: true } countMatch &&
                int.TryParse(countMatch.Value, out var parsedCount)
                    ? parsedCount
                    : null;
            var reward = MoneyRegex().Match(text) is { Success: true } rewardMatch
                ? rewardMatch.Value
                : null;
            var semantic = $"{action}|{GtaEventTextNormalizer.NormalizeIdentity(target)}|{count}|{reward}";
            return new GtaSemanticChallenge(
                StableKey("challenge", semantic),
                Bound(text, 512),
                action,
                Bound(target, 384),
                count,
                null,
                reward,
                Array.Empty<string>());
        }

        return null;
    }

    private bool TryParseItem(
        string line,
        string? section,
        DateTimeOffset reference,
        out GtaSemanticEventItem? item)
    {
        item = null;
        var cleaned = TrimBullet(line);
        var discount = DiscountRegex().Match(cleaned);
        if (!discount.Success)
        {
            discount = TrailingDiscountRegex().Match(cleaned);
        }
        if (discount.Success && int.TryParse(discount.Groups["percent"].Value, out var percent))
        {
            var entity = discount.Groups["entity"].Value.Trim();
            ObserveUnknown(entity);
            item = CreateItem(
                GtaEventItemKind.Discount,
                cleaned,
                entity,
                null,
                percent,
                Array.Empty<GtaRewardType>(),
                null,
                FindLineRange(cleaned, reference));
            return true;
        }

        var free = FreeRegex().Match(cleaned);
        if (!free.Success)
        {
            free = TrailingFreeRegex().Match(cleaned);
        }
        if (free.Success)
        {
            var entity = free.Groups["entity"].Value.Trim();
            ObserveUnknown(entity);
            item = CreateItem(
                GtaEventItemKind.FreeItem,
                cleaned,
                entity,
                null,
                null,
                Array.Empty<GtaRewardType>(),
                null,
                FindLineRange(cleaned, reference));
            return true;
        }

        var multiplier = MultiplierRegex().Match(cleaned);
        if (!multiplier.Success || !int.TryParse(multiplier.Groups["multiplier"].Value, out var value))
        {
            return false;
        }

        var rest = multiplier.Groups["body"].Value.Trim();
        var rewardTypes = ParseRewardTypes(rest);
        var activity = ExtractActivity(rest);
        if (!string.IsNullOrWhiteSpace(activity))
        {
            ObserveUnknown(activity);
        }

        var qualifier = rest.Contains("FIRST TIME COMPLETION", StringComparison.OrdinalIgnoreCase)
            ? "First Time Completion"
            : rest.Contains("RESEARCH PROGRESS", StringComparison.OrdinalIgnoreCase)
                ? "Research Progress"
                : rest.Contains("CASINO CHIPS", StringComparison.OrdinalIgnoreCase)
                    ? "Casino Chips"
                    : null;
        item = CreateItem(
            section == "login_rewards" ? GtaEventItemKind.LoginReward : GtaEventItemKind.Bonus,
            cleaned,
            activity,
            value,
            null,
            rewardTypes,
            qualifier,
            FindLineRange(cleaned, reference));
        return true;
    }

    private void ObserveUnknown(string value)
    {
        if (!_vocabulary.TryTranslate(value, out _))
        {
            _unknown.Observe("entity", value);
        }
    }

    private static GtaSemanticEventItem CreateItem(
        GtaEventItemKind kind,
        string original,
        string? activity,
        int? multiplier,
        int? discount,
        IReadOnlyList<GtaRewardType> rewards,
        string? qualifier,
        GtaEventDateRange? range)
    {
        var semantic = $"{kind}|{GtaEventTextNormalizer.NormalizeIdentity(activity ?? original)}|" +
            $"{multiplier}|{discount}|{string.Join(',', rewards)}|{qualifier}|{range?.StartAt:O}|{range?.EndAt:O}";
        return new GtaSemanticEventItem(
            StableKey("item", semantic),
            kind,
            Bound(original, 512),
            string.IsNullOrWhiteSpace(activity) ? null : Bound(activity, 256),
            multiplier,
            discount,
            rewards,
            qualifier,
            range);
    }

    private static void AddItem(
        GtaSemanticEventItem item,
        ICollection<GtaSemanticEventItem> bonuses,
        ICollection<GtaSemanticEventItem> discounts,
        ICollection<GtaSemanticEventItem> freeItems,
        ICollection<GtaSemanticEventItem> other)
    {
        switch (item.Kind)
        {
            case GtaEventItemKind.Bonus: bonuses.Add(item); break;
            case GtaEventItemKind.Discount: discounts.Add(item); break;
            case GtaEventItemKind.FreeItem: freeItems.Add(item); break;
            default: other.Add(item); break;
        }
    }

    private static IReadOnlyList<GtaRewardType> ParseRewardTypes(string text)
    {
        var rewards = new List<GtaRewardType>();
        if (text.Contains("GTA$", StringComparison.OrdinalIgnoreCase)) rewards.Add(GtaRewardType.GtaCash);
        if (WordRpRegex().IsMatch(text)) rewards.Add(GtaRewardType.Rp);
        if (text.Contains("CASINO CHIPS", StringComparison.OrdinalIgnoreCase)) rewards.Add(GtaRewardType.CasinoChips);
        if (text.Contains("RESEARCH PROGRESS", StringComparison.OrdinalIgnoreCase)) rewards.Add(GtaRewardType.ResearchProgress);
        if (text.Contains("SPEED", StringComparison.OrdinalIgnoreCase)) rewards.Add(GtaRewardType.Speed);
        if (text.Contains("FIRST TIME COMPLETION", StringComparison.OrdinalIgnoreCase)) rewards.Add(GtaRewardType.FirstTimeCompletion);
        return rewards.Count == 0 ? new[] { GtaRewardType.Other } : rewards.Distinct().ToArray();
    }

    private static string? ExtractActivity(string rest)
    {
        var on = ActivitySeparatorRegex().Match(rest);
        if (on.Success)
        {
            return TrimDateSuffix(on.Groups["activity"].Value);
        }

        var stripped = RewardTokenRegex().Replace(rest, " ");
        stripped = ConnectorRegex().Replace(stripped, " ");
        stripped = TrimDateSuffix(stripped).Trim(' ', '-', ':', ',', '&');
        return stripped.Length == 0 ? null : stripped;
    }

    private static GtaEventDateRange? FindLineRange(string line, DateTimeOffset reference) =>
        GtaEventDateParser.FindRanges(line, reference).FirstOrDefault();

    private DateTimeOffset InferWeeklyStart(DateTimeOffset receivedAt)
    {
        var local = _schedule.ToKst(receivedAt);
        return local.DayOfWeek == DayOfWeek.Thursday && local.TimeOfDay < KstResetSchedule.WeeklyResetTime
            ? _schedule.GetNextWeeklyReset(receivedAt)
            : _schedule.GetWeeklyCycleStart(receivedAt);
    }

    private static IReadOnlyList<string> MeaningfulLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TrimBullet)
            .Where(line => line.Length > 0)
            .ToArray();

    private static bool IsOpening(string line)
    {
        var identity = GtaEventTextNormalizer.NormalizeIdentity(line);
        return identity.Contains("A NEW GTA ONLINE EVENT STARTS ON", StringComparison.Ordinal) ||
            identity.Contains("THE LATEST GTA ONLINE EVENT IS STILL LIVE", StringComparison.Ordinal);
    }

    private static bool LooksLikeDateOnly(string line, DateTimeOffset reference) =>
        GtaEventDateParser.FindRanges(line, reference).Count > 0 && line.Length < 40;

    private static bool LooksLikeHeading(string line)
    {
        var letters = line.Where(char.IsLetter).ToArray();
        return line.Length <= 96 && letters.Length >= 3 && letters.All(char.IsUpper);
    }

    private static string TrimBullet(string value) => value.Trim().TrimStart('-', '•', '·', '*', ' ');

    private static string TrimDateSuffix(string value) => ParenthesizedDateRegex().Replace(value, string.Empty).Trim();

    private static IReadOnlyList<GtaSemanticEventItem> BoundDistinct(
        IEnumerable<GtaSemanticEventItem> items,
        int maximum) => items.DistinctBy(item => item.ItemKey).Take(maximum).ToArray();

    private static string StableKey(string prefix, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{prefix}_{Convert.ToHexString(bytes.AsSpan(0, 10)).ToLowerInvariant()}";
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum].TrimEnd();

    [GeneratedRegex(@"(?<multiplier>\d{1,2})\s*[X×]\s*(?<body>.+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultiplierRegex();

    [GeneratedRegex(@"(?<percent>\d{1,3})\s*%\s*OFF\s+(?<entity>.+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscountRegex();

    [GeneratedRegex(@"^(?<entity>.+?)(?:\s*[-:]\s*|\s*\()(?<percent>\d{1,3})\s*%\s*OFF\)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDiscountRegex();

    [GeneratedRegex(@"^FREE\s+(?<entity>.+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FreeRegex();

    [GeneratedRegex(@"^(?<entity>.+?)(?:\s*[-:]\s*|\s*\()FREE\)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingFreeRegex();

    [GeneratedRegex(@"\b(?:ON|IN|FOR)\s+(?<activity>.+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActivitySeparatorRegex();

    [GeneratedRegex(@"GTA\$|CASINO CHIPS|RESEARCH PROGRESS|FIRST TIME COMPLETION|\bRP\b|\bSPEED\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RewardTokenRegex();

    [GeneratedRegex(@"\s*(?:&|,|\+|AND)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectorRegex();

    [GeneratedRegex(@"\([^)]*(?:JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedDateRegex();

    [GeneratedRegex(@"\bRP\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordRpRegex();

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex FirstIntegerRegex();

    [GeneratedRegex(@"GTA\$\s?[\d,]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MoneyRegex();
}

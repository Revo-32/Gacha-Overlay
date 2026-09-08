using System.Text.RegularExpressions;
using LSOverlay.Protocol;

namespace GachaOverlay.App.Presentation;

internal sealed record GtaWeeklyEventRow(string ItemKey, string DisplayText);
internal sealed record GtaWeeklyEventGroup(string Heading, IReadOnlyList<GtaWeeklyEventRow> Items);

// A client-only projection: never rewrite the snapshot or use translated prose as the group key.
internal static class GtaWeeklyEventPresentation
{
    private sealed record GroupKey(string Heading, DateTimeOffset? From, DateTimeOffset? To, int Order, int Value);

    public static IReadOnlyList<GtaWeeklyEventGroup> Build(GtaCompanionWeek? week)
    {
        if (week is null) return Array.Empty<GtaWeeklyEventGroup>();
        return week.Bonuses.Concat(week.Discounts).Concat(week.FreeItems).Concat(week.OtherEvents)
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayTextKo))
            .Take(48)
            .Select(item => (Item: item, Group: Describe(item)))
            .GroupBy(entry => entry.Group.Key)
            .OrderBy(group => group.Key.Order)
            .ThenByDescending(group => group.Key.Value)
            .Select(group => new GtaWeeklyEventGroup(group.Key.Heading,
                group.Select(entry => new GtaWeeklyEventRow(entry.Item.ItemKey,
                    RemoveRepeatedHeading(entry.Item.DisplayTextKo, entry.Group.Pattern))).ToArray()))
            .ToArray();
    }

    private static (GroupKey Key, string? Pattern) Describe(GtaCompanionItem item)
    {
        var sourceHeader = item.OriginalLabel.Split('\n')[0].Trim();
        var fallback = item.Kind switch
        {
            GtaCompanionItemKind.Bonus => "기타 보너스",
            GtaCompanionItemKind.Discount => "기타 할인",
            GtaCompanionItemKind.FreeItem => "무료 혜택",
            _ => "기타 이벤트",
        };
        (GroupKey, string?) Group(string heading, int order, int value, string? pattern) =>
            (new(heading, item.EffectiveFrom, item.EffectiveTo, order, value), pattern);

        if (item.Kind == GtaCompanionItemKind.Bonus && item.Multiplier is int multiplier)
        {
            var rewards = item.RewardTypes.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string? label = null;
            string? rewardPattern = null;
            string? sourcePattern = null;
            if (rewards.SequenceEqual(new[] { "GtaCash", "Rp" }))
            {
                label = "GTA$ · RP";
                rewardPattern = @"GTA(?:\$|\s*달러)\s*(?:\+|&|와|및|and)\s*RP";
                sourcePattern = @"GTA\$\s*(?:&|AND|\+)\s*RP";
            }
            else if (rewards.SequenceEqual(new[] { "Speed" }))
            {
                label = "속도";
                rewardPattern = "속도";
                sourcePattern = "SPEED";
            }
            // Only unqualified source headings are compacted. Unknown/member/platform conditions stay intact.
            if (label is not null && Match(sourceHeader, $@"^{multiplier}\s*[X×]\s+{sourcePattern}$"))
            {
                var pattern = $@"(?:{multiplier}\s*배\s*{rewardPattern}|{rewardPattern}\s*{multiplier}\s*배)";
                return Group($"{multiplier}배 {label}", 0, multiplier, pattern);
            }
        }
        if (item.Kind == GtaCompanionItemKind.Discount && item.DiscountPercent is int percent)
        {
            if (Match(sourceHeader, $@"^DISCOUNTS?\s*\(\s*{percent}%\s*OFF\s*\)$") ||
                Match(sourceHeader, $@"^{percent}%\s*OFF\s*:"))
            {
                return Group($"{percent}% 할인", 1, percent,
                    $@"(?:할인\s*\(\s*{percent}%\s*할인\s*\)|{percent}%\s*할인)");
            }
            if (Match(sourceHeader, $@"^{percent}%\s*OFF\s+GTA\+\s+Members\s*:"))
            {
                return Group($"{percent}% 할인 · GTA+ 회원", 1, percent,
                    $@"GTA\+\s*회원\s*{percent}%\s*할인");
            }
        }
        if (item.Kind == GtaCompanionItemKind.FreeItem &&
            Match(sourceHeader, @"^DISCOUNTS?\s*\(\s*FREE\s*\|\s*100%\s*OFF\s*\)$"))
        {
            return Group("무료 · 100% 할인", 2, 100, @"할인\s*\(\s*무료\s*\|\s*100%\s*할인\s*\)");
        }
        return Group(fallback, 3, 0, null);
    }

    private static string RemoveRepeatedHeading(string text, string? pattern)
    {
        if (pattern is null) return text;
        // Remove only an entire, known heading at a boundary, not numbers/words inside an activity or condition.
        var separator = @"(?:\s*[:：·]\s*|\s+)";
        var trimmed = text.Trim();
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var timeout = TimeSpan.FromMilliseconds(100);
        var prefix = Regex.Match(trimmed, $@"^(?:{pattern}){separator}(?<body>.+)$", options | RegexOptions.Singleline, timeout);
        if (prefix.Success) return prefix.Groups["body"].Value.Trim();
        var suffix = Regex.Match(trimmed, $@"^(?<body>.+?){separator}(?:{pattern})$", options | RegexOptions.Singleline, timeout);
        return suffix.Success ? suffix.Groups["body"].Value.Trim() : text;
    }

    private static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}

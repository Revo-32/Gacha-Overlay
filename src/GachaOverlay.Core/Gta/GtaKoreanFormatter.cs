using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta;

public sealed partial class GtaKoreanFormatter
{
    private readonly GtaEventVocabulary _vocabulary;

    public GtaKoreanFormatter(GtaEventVocabulary? vocabulary = null)
    {
        _vocabulary = vocabulary ?? new GtaEventVocabulary();
    }

    public string FormatChallenge(GtaSemanticChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var target = TranslateOrOriginal(challenge.Target ?? challenge.OriginalText);
        var amount = challenge.Reward is null ? string.Empty : $" {challenge.Reward}";
        return challenge.Action?.ToUpperInvariant() switch
        {
            "EARN" => $"{target}에서{amount} 획득".Replace("에서  획득", "에서 보상 획득", StringComparison.Ordinal),
            "COMPLETE" => $"{target} 완료",
            "WIN" => $"{target} 승리",
            "PARTICIPATE" => $"{target} 참가",
            "SELL" => $"{target} 판매",
            "SOURCE" => $"{target} 확보",
            "DELIVER" => $"{target} 배달",
            "PURCHASE" => $"{target} 구매",
            "CLAIM" => $"{target} 획득",
            "PLAY" => $"{target} 플레이",
            "FINISH" => $"{target} 완료",
            "PLACE" => $"{target} 순위 달성",
            "SURVIVE" => $"{target} 생존",
            "DESTROY" => $"{target} 파괴",
            "STEAL" => $"{target} 훔치기",
            "COLLECT" => $"{target} 수집",
            _ => TranslateKnownTerms(challenge.OriginalText),
        };
    }

    public string? FormatReward(GtaSemanticChallenge challenge) =>
        string.IsNullOrWhiteSpace(challenge.Reward) ? null : $"보상 {challenge.Reward}";

    public string FormatItem(GtaSemanticEventItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var subject = TranslateOrOriginal(item.Activity ?? item.OriginalLabel);
        return item.Kind switch
        {
            GtaEventItemKind.Discount => $"{subject} · {item.DiscountPercent}% 할인",
            GtaEventItemKind.FreeItem => $"{subject} · 무료",
            _ when item.Multiplier is not null =>
                $"{subject} · {item.Multiplier}배{FormatRewards(item.RewardTypes)}",
            _ => TranslateKnownTerms(item.OriginalLabel),
        };
    }

    public string FormatCampaignText(string value) => TranslateKnownTerms(value);

    public string TranslateKnownTerms(string value)
    {
        var result = value;
        foreach (var entry in GtaEventVocabulary.Glossary
                     .Where(entry => entry.TranslationSource != GtaTranslationSource.OriginalFallback)
                     .OrderByDescending(entry => entry.EnglishName.Length))
        {
            result = ReplaceInvariant(result, entry.EnglishName, entry.KoreanDisplayName);
            foreach (var alias in entry.EnglishAliases.OrderByDescending(alias => alias.Length))
            {
                result = ReplaceInvariant(result, alias, entry.KoreanDisplayName);
            }
        }

        result = DiscountRegex().Replace(result, match => $"{match.Groups["percent"].Value}% 할인");
        result = MultiplierRegex().Replace(result, match => $"{match.Groups["value"].Value}배");
        return result.Replace("FREE", "무료", StringComparison.OrdinalIgnoreCase);
    }

    private string TranslateOrOriginal(string value)
    {
        // Replace known terms in place so counts, qualifiers and unknown proper
        // names surrounding a known entity are never discarded.
        var translated = TranslateKnownTerms(value);
        return translated.Length == 0 && _vocabulary.TryTranslate(value, out var exact)
            ? exact
            : translated;
    }

    private static string FormatRewards(IReadOnlyList<GtaRewardType> rewards)
    {
        var names = rewards.Where(reward => reward != GtaRewardType.Other).Select(reward => reward switch
        {
            GtaRewardType.GtaCash => "GTA$",
            GtaRewardType.Rp => "RP",
            GtaRewardType.CasinoChips => "카지노 칩",
            GtaRewardType.ResearchProgress => "연구 진행도",
            GtaRewardType.Speed => "속도",
            GtaRewardType.FirstTimeCompletion => "첫 완료",
            _ => string.Empty,
        }).Where(value => value.Length > 0).ToArray();
        return names.Length == 0 ? string.Empty : " " + string.Join(" + ", names);
    }

    private static string ReplaceInvariant(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            value = value.Remove(index, oldValue.Length).Insert(index, newValue);
            index = value.IndexOf(oldValue, index + newValue.Length, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    [GeneratedRegex(@"(?<percent>\d{1,3})\s*%\s*OFF", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscountRegex();

    [GeneratedRegex(@"(?<value>\d{1,2})\s*[X×]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultiplierRegex();
}

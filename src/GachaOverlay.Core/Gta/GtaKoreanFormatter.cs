using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta;

public sealed partial class GtaKoreanFormatter
{
    private readonly GtaEventVocabulary _vocabulary;
    private readonly GtaTranslationGlossary _glossary;
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    public const string RuleVersion = "ko-2.3-1";
    public string CacheVersion => RuleVersion + ":" + _glossary.Version;
    public int CachedCount { get { lock (_sync) return _memory.Count; } }

    public GtaKoreanFormatter(GtaEventVocabulary? vocabulary = null, GtaTranslationGlossary? glossary = null)
    {
        _vocabulary = vocabulary ?? new GtaEventVocabulary();
        _glossary = glossary ?? GtaTranslationGlossary.Default;
    }

    public string FormatChallenge(GtaSemanticChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var target = TranslateOrOriginal(challenge.Target ?? challenge.OriginalText);
        var amount = challenge.Reward is null ? string.Empty : $" {challenge.Reward}";
        var candidate = challenge.Action?.ToUpperInvariant() switch
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
        return GtaTranslationIntegrity.Select(challenge.OriginalText, candidate, TranslateKnownTerms(challenge.OriginalText));
    }

    public string? FormatReward(GtaSemanticChallenge challenge) =>
        string.IsNullOrWhiteSpace(challenge.Reward) ? null : $"보상 {challenge.Reward}";

    public string FormatItem(GtaSemanticEventItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var subject = TranslateOrOriginal(item.Activity ?? item.OriginalLabel);
        var candidate = item.Kind switch
        {
            GtaEventItemKind.Discount => $"{subject} · {item.DiscountPercent}% 할인",
            GtaEventItemKind.FreeItem => $"{subject} · 무료",
            _ when item.Multiplier is not null =>
                $"{subject} · {item.Multiplier}배{FormatRewards(item.RewardTypes)}",
            _ => TranslateKnownTerms(item.OriginalLabel),
        };
        return GtaTranslationIntegrity.Select(item.OriginalLabel, candidate, TranslateKnownTerms(item.OriginalLabel));
    }

    public string FormatCampaignText(string value) => TranslateKnownTerms(value);

    public string TranslateKnownTerms(string value)
    {
        if (value.Length > 16384) return value;
        lock (_sync)
        {
            var key = CacheVersion + ":" + value;
            if (_memory.TryGetValue(key, out var cached)) return cached;
            string result;
            try
            {
                result = _glossary.Translate(value);
                result = DiscountRegex().Replace(result, match => $"{match.Groups["percent"].Value}% 할인");
                result = MultiplierRegex().Replace(result, match => $"{match.Groups["value"].Value}배");
                result = FreeRegex().Replace(result, "무료");
                result = WeeklyChallengeRegex().Replace(result, "주간 도전");
                result = CompletionRewardRegex().Replace(result, "완료 보상");
                result = CashRpRegex().Replace(result, "GTA$ + RP");
                result = MissionCountRegex().Replace(result, match => $"{match.Groups["target"].Value} 임무 {match.Groups["count"].Value}회 완료");
                result = RaceCountRegex().Replace(result, match => $"{match.Groups["target"].Value} 레이스 {match.Groups["count"].Value}회 승리");
            }
            catch (RegexMatchTimeoutException) { return value; }
            result = GtaTranslationIntegrity.Select(value, result);
            if (_memory.Count >= 256) _memory.Remove(_memory.Keys.First());
            _memory[key] = result;
            return result;
        }
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

    [GeneratedRegex(@"(?<percent>\d{1,3})\s*%\s*OFF", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscountRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<value>\d{1,2})\s*[X×](?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultiplierRegex();

    [GeneratedRegex(@"\bFREE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FreeRegex();
    [GeneratedRegex(@"\bWEEKLY CHALLENGES?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeeklyChallengeRegex();
    [GeneratedRegex(@"\bCOMPLETION REWARDS?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompletionRewardRegex();
    [GeneratedRegex(@"GTA\$\s+(?:and|&)\s+RP\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CashRpRegex();
    [GeneratedRegex(@"^Complete\s+(?<count>\d+)\s+(?<target>.+?)\s+missions?\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex MissionCountRegex();
    [GeneratedRegex(@"^Win\s+(?<count>\d+)\s+(?<target>.+?)\s+races?\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex RaceCountRegex();
}

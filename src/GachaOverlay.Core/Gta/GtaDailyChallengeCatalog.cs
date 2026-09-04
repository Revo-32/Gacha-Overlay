namespace GachaOverlay.Core.Gta;

public enum GtaDailyChallengeStatus
{
    Active,
    Legacy,
    Unverified,
}

public sealed record GtaDailyChallengeDefinition(
    string ChallengeId,
    string EnglishCanonicalName,
    IReadOnlyList<string> EnglishAliases,
    string KoreanDisplayName,
    string KoreanShortDescription,
    IReadOnlyList<string> Requirements,
    GtaDailyChallengeStatus Status,
    GtaTranslationSource TranslationSource);

public static class GtaDailyChallengeCatalog
{
    public const string CustomChallengeId = "custom";

    // This is deliberately a small curated foundation, not a claim that every
    // historical GTA Online Daily Objective is present. 직접 입력 remains available.
    public static IReadOnlyList<GtaDailyChallengeDefinition> Entries { get; } = Array.AsReadOnly(new[]
    {
        Active("participate_deathmatch", "Participate in a Deathmatch", "데스매치 참가", "데스매치를 한 번 플레이하면 완료됩니다."),
        Active("complete_contact_mission", "Complete a Contact Mission", "연락책 임무 완료", "연락책 임무를 한 번 완료하면 됩니다."),
        Active("participate_race", "Participate in a Race", "레이스 참가", "레이스를 한 번 플레이하면 완료됩니다."),
        Active("win_race", "Win a Race", "레이스 승리", "레이스에서 한 번 승리하면 됩니다."),
        Active("complete_survival", "Complete a Survival", "서바이벌 완료", "서바이벌을 한 번 완료하면 됩니다."),
        Active("complete_gang_attack", "Complete a Gang Attack", "갱 어택 완료", "갱 어택을 한 번 완료하면 됩니다."),
        Active("rob_store", "Rob a Convenience Store", "편의점 털기", "편의점을 한 번 털면 완료됩니다."),
        Active("respray_vehicle", "Respray a Vehicle", "이동 수단 재도색", "개조 샵에서 이동 수단을 재도색하면 됩니다."),
        Active("visit_casino", "Visit The Diamond Casino & Resort", "다이아몬드 카지노 방문", "다이아몬드 카지노에 방문하면 완료됩니다."),
        Active("play_golf", "Play a Round of Golf", "골프 플레이", "골프 라운드를 한 번 플레이하면 됩니다."),
        Active("play_tennis", "Play a Game of Tennis", "테니스 플레이", "테니스 경기를 한 번 플레이하면 됩니다."),
        Active("collect_bounty", "Collect a Bounty", "현상금 수령", "현상금이 걸린 플레이어를 처치해 현상금을 받으면 됩니다."),
        Legacy("participate_parachuting", "Participate in Parachuting", "낙하산 강하 참가"),
        Legacy("play_arm_wrestling", "Play Arm Wrestling", "팔씨름 플레이"),
        Legacy("play_darts", "Play Darts", "다트 플레이"),
        Unverified("future_business_sale", "Complete a Business Sale", "사업장 판매 완료"),
        Unverified("future_series", "Participate in a Featured Series", "추천 시리즈 참가"),
        Unverified("future_collectible", "Collect a Daily Collectible", "오늘의 수집품 획득"),
        Unverified("future_freemode", "Complete a Freemode Event", "자유 모드 이벤트 완료"),
    });

    public static IReadOnlyList<GtaDailyChallengeDefinition> SearchableEntries =>
        Entries.Where(entry => entry.Status == GtaDailyChallengeStatus.Active).ToArray();

    private static GtaDailyChallengeDefinition Active(
        string id,
        string english,
        string korean,
        string description) => new(
            id,
            english,
            Array.Empty<string>(),
            korean,
            description,
            Array.Empty<string>(),
            GtaDailyChallengeStatus.Active,
            GtaTranslationSource.Curated);

    private static GtaDailyChallengeDefinition Legacy(string id, string english, string korean) => new(
        id, english, Array.Empty<string>(), korean, string.Empty, Array.Empty<string>(),
        GtaDailyChallengeStatus.Legacy, GtaTranslationSource.Curated);

    private static GtaDailyChallengeDefinition Unverified(string id, string english, string korean) => new(
        id, english, Array.Empty<string>(), korean, string.Empty, Array.Empty<string>(),
        GtaDailyChallengeStatus.Unverified, GtaTranslationSource.Curated);
}

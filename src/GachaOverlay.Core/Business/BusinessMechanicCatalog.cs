namespace GachaOverlay.Core.Business;

public enum MechanicEvidenceConfidence
{
    VerifiedOfficial,
    VerifiedCommunity,
    Provisional,
}

public sealed record MechanicEvidence(
    string Mechanic,
    MechanicEvidenceConfidence Confidence,
    string Basis,
    string? SourceUrl = null);

public enum BusinessHeistKind
{
    Original,
    Doomsday,
    Casino,
    CayoGroup,
    CayoSolo,
    Kortz,
}

public static class BusinessMechanicCatalog
{
    public const int CatalogVersion = 1;
    public static readonly TimeSpan BunkerNormalSupply = TimeSpan.FromMinutes(140);
    public static readonly TimeSpan AcidProductUnitDuration = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan AcidNormalSupply = TimeSpan.FromMinutes(150);
    public const int AcidBoostProductUnitAllowance = 80;
    public static readonly TimeSpan AcidBoostAllowanceWork = TimeSpan.FromTicks(
        AcidProductUnitDuration.Ticks * AcidBoostProductUnitAllowance);
    public static readonly TimeSpan AcidBoostExpiration = TimeSpan.FromHours(24);
    public static readonly TimeSpan MansionBoostWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan StaffDispatch = TimeSpan.FromMinutes(48);
    public static readonly TimeSpan NightclubCycle = TimeSpan.FromMinutes(48);
    public static readonly TimeSpan CarWashCycle = TimeSpan.FromMinutes(48);
    public static readonly TimeSpan WarehouseStaffDispatch = TimeSpan.FromMinutes(48);
    public static readonly TimeSpan AirFreightStaffDispatch = TimeSpan.FromMinutes(48);
    public static readonly TimeSpan CayoHardModeWindow = TimeSpan.FromMinutes(48);
    public static readonly TimeSpan KortzHardModeWindow = TimeSpan.FromMinutes(48);

    public const double MansionMultiplier = 3d;
    public const double AcidOwnBoostMultiplier = 2d;

    private static readonly int[] NightclubIncomeByFivePercentPopularityStep =
    [
        50_000, 50_000, 45_000, 25_000, 24_000, 23_000, 22_000,
        21_000, 20_000, 10_000, 9_500, 9_000, 8_500, 8_000,
        2_500, 2_200, 2_000, 1_800, 1_600, 1_500, 1_500,
    ];

    public static IReadOnlyList<int> NightclubTargets { get; } =
        [50_000, 45_000, 25_000, 24_000, 20_000, 10_000];

    public static IReadOnlyList<MechanicEvidence> Evidence { get; } =
    [
        new("Bunker upgraded manufacturing supply", MechanicEvidenceConfidence.VerifiedCommunity,
            "A full supply bar represents 140 minutes of GTA Online playtime."),
        new("Acid Lab fully upgraded supply and own boost", MechanicEvidenceConfidence.VerifiedCommunity,
            "Upgraded production uses 90 seconds per product unit. The x2 boost applies to at most 80 produced units and expires after 24 real-time hours.",
            "https://www.pcgamer.com/how-to-make-money-in-gta-online/"),
        new("Mansion business boost duration", MechanicEvidenceConfidence.VerifiedOfficial,
            "The production boost lasts 24 real-time hours.",
            "https://www.rockstargames.com/newswire/article/51358a55o2o11o/gta-online-a-safehouse-in-the-hills-coming-december-10"),
        new("Mansion business boost multiplier", MechanicEvidenceConfidence.VerifiedCommunity,
            "The active business receives an x3 production multiplier."),
        new("Nightclub popularity", MechanicEvidenceConfidence.VerifiedCommunity,
            "48-minute online cycles and staff-upgrade decay table.",
            "https://www.gtaboom.com/gta-online-nightclub-guide-after-hours-ac95"),
        new("Money Fronts heat", MechanicEvidenceConfidence.VerifiedCommunity,
            "48-minute online cycles with business-count dependent progression.",
            "https://www.gtaboom.com/money-fronts-guide-eb4f/"),
        new("Warehouse and Air Freight staff", MechanicEvidenceConfidence.VerifiedCommunity,
            "48-minute wall-clock staff dispatch reference.",
            "https://thegtawiki.com/guides/online-timers"),
        new("Original Heists group cooldown", MechanicEvidenceConfidence.VerifiedOfficial,
            "The group cooldown is one in-game day (48 real minutes)."),
        new("Doomsday group cooldown", MechanicEvidenceConfidence.VerifiedOfficial,
            "Rockstar documents the group cooldown as one in-game day (48 real minutes).",
            "https://www.rockstargames.com/newswire/article/3974k2848172a2/upcoming-improvements-to-the-gta-online-experience"),
        new("Casino group cooldown", MechanicEvidenceConfidence.VerifiedOfficial,
            "Rockstar documents the group cooldown as one in-game day (48 real minutes).",
            "https://www.rockstargames.com/newswire/article/3974k2848172a2/upcoming-improvements-to-the-gta-online-experience"),
        new("Cayo Perico group cooldown", MechanicEvidenceConfidence.VerifiedOfficial,
            "Rockstar documents the group cooldown as one in-game day (48 real minutes).",
            "https://www.rockstargames.com/newswire/article/3974k2848172a2/upcoming-improvements-to-the-gta-online-experience"),
        new("Cayo Perico solo cooldown", MechanicEvidenceConfidence.VerifiedOfficial,
            "Rockstar documents the solo cooldown as three in-game days (144 real minutes).",
            "https://www.rockstargames.com/newswire/article/3974k2848172a2/upcoming-improvements-to-the-gta-online-experience"),
        new("Cayo Perico hard-mode window", MechanicEvidenceConfidence.VerifiedCommunity,
            "A 48-minute setup-start window is retained as a community-backed value."),
        new("Kortz contact delay", MechanicEvidenceConfidence.VerifiedCommunity,
            "The contact delay is 10 real minutes.",
            "https://www.rockstargames.com/newswire/article/2525o93834o413/the-kortz-center-heist-now-available-in-gta-online"),
        new("Kortz hard-mode window", MechanicEvidenceConfidence.VerifiedCommunity,
            "The hard-mode setup window is 48 real minutes.",
            "https://www.rockstargames.com/newswire/article/2525o93834o413/the-kortz-center-heist-now-available-in-gta-online"),
    ];

    public static TimeSpan NightclubTimeUntilBelowTarget(int targetIncome, bool staffUpgrade)
    {
        if (!NightclubTargets.Contains(targetIncome))
        {
            targetIncome = 50_000;
        }

        var popularityStep = staffUpgrade ? 1 : 2;
        var cycles = 0;
        var tableIndex = 0;
        while (tableIndex < NightclubIncomeByFivePercentPopularityStep.Length &&
               NightclubIncomeByFivePercentPopularityStep[tableIndex] >= targetIncome)
        {
            cycles++;
            tableIndex += popularityStep;
        }

        return TimeSpan.FromTicks(NightclubCycle.Ticks * cycles);
    }

    public static TimeSpan CarWashTimeUntilMinimum(int ownedBusinesses)
    {
        var count = Math.Clamp(ownedBusinesses, 1, 3);
        var cycles = count switch { 1 => 10, 2 => 12, _ => 13 };
        return TimeSpan.FromTicks(CarWashCycle.Ticks * cycles);
    }

    public static TimeSpan HeistCooldown(BusinessHeistKind kind) => kind switch
    {
        BusinessHeistKind.CayoSolo => TimeSpan.FromMinutes(144),
        BusinessHeistKind.Kortz => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(48),
    };
}

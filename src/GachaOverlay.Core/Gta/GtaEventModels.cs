namespace GachaOverlay.Core.Gta;

public sealed record GtaEventEmbedFieldInput(string Name, string Value);

public sealed record GtaEventEmbedInput(
    string? Title,
    string? Description,
    IReadOnlyList<GtaEventEmbedFieldInput> Fields,
    string? ProviderName = null,
    string? AuthorName = null);

public sealed record GtaEventForwardInput(
    string? Content,
    IReadOnlyList<GtaEventEmbedInput> Embeds);

public sealed record GtaEventSourceInput(
    ulong SourceMessageId,
    ulong ChannelId,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? EditedAt,
    string? Content,
    IReadOnlyList<GtaEventEmbedInput> Embeds,
    IReadOnlyList<GtaEventForwardInput> ForwardedSnapshots,
    string? SourcePublisher = null,
    string? SourceChannelName = null);

public sealed record CanonicalEventBlock(string Kind, string Text);

public sealed record CanonicalEventDocument(
    ulong SourceMessageId,
    ulong ChannelId,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? EditedAt,
    string? SourcePublisher,
    string? SourceChannelName,
    bool IsForwarded,
    IReadOnlyList<CanonicalEventBlock> CanonicalBlocks,
    string CanonicalText);

public enum GtaEventClassificationKind
{
    WeeklyBulletin,
    MultiWeekCampaign,
    Candidate,
    Ignore,
    Unknown,
}

public sealed record GtaEventClassification(
    GtaEventClassificationKind Kind,
    int WeeklyAnchorFamilyCount,
    bool HasWeeklyHeading,
    bool HasTrustedSource,
    string Reason);

public enum GtaRewardType
{
    GtaCash,
    Rp,
    CasinoChips,
    ResearchProgress,
    Speed,
    FirstTimeCompletion,
    Other,
}

public enum GtaEventItemKind
{
    Bonus,
    Discount,
    FreeItem,
    LoginReward,
    RotatingContent,
    Note,
}

public sealed record GtaEventDateRange(
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string OriginalText);

public sealed record GtaSemanticChallenge(
    string ChallengeKey,
    string OriginalText,
    string? Action,
    string? Target,
    int? Count,
    string? Qualifier,
    string? Reward,
    IReadOnlyList<string> Requirements);

public sealed record GtaSemanticEventItem(
    string ItemKey,
    GtaEventItemKind Kind,
    string OriginalLabel,
    string? Activity,
    int? Multiplier,
    int? DiscountPercent,
    IReadOnlyList<GtaRewardType> RewardTypes,
    string? Qualifier,
    GtaEventDateRange? DateScope);

public sealed record GtaEventWeek(
    string WeekKey,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo,
    string? Theme,
    GtaSemanticChallenge? WeeklyChallenge,
    IReadOnlyList<GtaSemanticEventItem> Bonuses,
    IReadOnlyList<GtaSemanticEventItem> Discounts,
    IReadOnlyList<GtaSemanticEventItem> FreeItems,
    IReadOnlyList<GtaSemanticEventItem> OtherEvents,
    ulong SourceMessageId,
    DateTimeOffset ParsedAt);

public sealed record GtaCampaignWeek(
    string WeekKey,
    string Label,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? EffectiveTo);

public sealed record GtaEventCampaign(
    string CampaignKey,
    string Title,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> Rewards,
    IReadOnlyList<GtaCampaignWeek> PlannedWeeks,
    ulong SourceMessageId,
    DateTimeOffset ParsedAt);

public sealed record GtaParsedEvent(
    GtaEventClassification Classification,
    GtaEventWeek? Week,
    GtaEventCampaign? Campaign);

public sealed record GtaTrustedEventState(
    int SchemaVersion,
    GtaEventWeek? ActiveWeek,
    GtaEventWeek? StagedWeek,
    IReadOnlyList<GtaEventCampaign> RelevantCampaigns,
    DateTimeOffset LastUpdatedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static GtaTrustedEventState Empty { get; } = new(
        CurrentSchemaVersion,
        null,
        null,
        Array.Empty<GtaEventCampaign>(),
        DateTimeOffset.MinValue);
}

public enum GtaResolvedAvailability
{
    Available,
    Preparing,
    Unavailable,
}

public sealed record GtaResolvedEventState(
    GtaResolvedAvailability Availability,
    GtaEventWeek? CurrentWeek,
    GtaEventCampaign? Campaign,
    DateTimeOffset EvaluatedAt);

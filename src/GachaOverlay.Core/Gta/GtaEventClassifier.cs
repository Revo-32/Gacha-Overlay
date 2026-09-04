namespace GachaOverlay.Core.Gta;

public sealed class GtaEventClassifier
{
    private static readonly string[] WeeklyOpenings =
    {
        "A NEW GTA ONLINE EVENT STARTS ON",
        "THE LATEST GTA ONLINE EVENT IS STILL LIVE",
    };

    private static readonly string[] CampaignSignatures =
    {
        "EVENT BREAKDOWN",
        "FULL SCHEDULE BELOW",
        "MONTH LONG WEEKLY CHALLENGES",
        "BONUSES & DISCOUNTS",
    };

    private readonly GtaEventVocabulary _vocabulary;

    public GtaEventClassifier(GtaEventVocabulary? vocabulary = null)
    {
        _vocabulary = vocabulary ?? new GtaEventVocabulary();
    }

    public GtaEventClassification Classify(CanonicalEventDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = document.CanonicalText.ToUpperInvariant();
        var source = $"{document.SourcePublisher} {document.SourceChannelName}".ToUpperInvariant();
        if (source.Contains("GTA-PLUS-BENEFITS", StringComparison.Ordinal) ||
            source.Contains("GTA PLUS BENEFITS", StringComparison.Ordinal) ||
            text.Contains("GTA+ MONTHLY", StringComparison.Ordinal) ||
            text.Contains("GTA+ BENEFITS", StringComparison.Ordinal))
        {
            return Result(GtaEventClassificationKind.Ignore, 0, false, false, "GtaPlus");
        }

        var lines = document.CanonicalText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var weeklyHeading = lines.Any(IsWeeklyHeading);
        var anchorCount = _vocabulary.FindWeeklyAnchorFamilies(lines).Count;
        var weeklyOpening = WeeklyOpenings.Any(opening => text.Contains(opening, StringComparison.Ordinal));
        var trustedSource = source.Contains("GTA SERIES VIDEOS", StringComparison.Ordinal) ||
            source.Contains("GTAO-WEEKLY-BONUSES", StringComparison.Ordinal) ||
            source.Contains("GTAO WEEKLY BONUSES", StringComparison.Ordinal);

        if (weeklyHeading && anchorCount >= 3 && (weeklyOpening || trustedSource))
        {
            return Result(
                GtaEventClassificationKind.WeeklyBulletin,
                anchorCount,
                weeklyHeading,
                trustedSource,
                weeklyOpening ? "StrongContentSignature" : "TrustedSourceAndStructure");
        }

        var campaignSignatureCount = CampaignSignatures.Count(signature =>
            text.Contains(signature, StringComparison.Ordinal));
        var dateRangeCount = GtaEventDateParser.FindRanges(
            document.CanonicalText,
            document.ReceivedAt).Count;
        var additionalCampaignSignal = text.Contains("BY WEEK", StringComparison.Ordinal) ||
            text.Contains("WEEK 1", StringComparison.Ordinal) ||
            text.Contains("WEEK 2", StringComparison.Ordinal) ||
            text.Contains("CAMPAIGN", StringComparison.Ordinal);
        if (campaignSignatureCount > 0 && dateRangeCount >= 2 && additionalCampaignSignal)
        {
            return Result(
                GtaEventClassificationKind.MultiWeekCampaign,
                anchorCount,
                weeklyHeading,
                trustedSource,
                "CampaignStructure");
        }

        if ((weeklyHeading && anchorCount >= 2) ||
            (weeklyOpening && anchorCount >= 3) ||
            (trustedSource && (weeklyHeading || anchorCount >= 2)))
        {
            return Result(
                GtaEventClassificationKind.Candidate,
                anchorCount,
                weeklyHeading,
                trustedSource,
                "WeeklyLikeButUntrusted");
        }

        if (text.Contains("GTA ONLINE", StringComparison.Ordinal) || trustedSource)
        {
            return Result(
                GtaEventClassificationKind.Unknown,
                anchorCount,
                weeklyHeading,
                trustedSource,
                "GtaRelatedUnknownStructure");
        }

        return Result(GtaEventClassificationKind.Ignore, anchorCount, weeklyHeading, trustedSource, "Unrelated");
    }

    private static bool IsWeeklyHeading(string line)
    {
        var identity = GtaEventTextNormalizer.NormalizeIdentity(line);
        return identity is "WEEKLY CHALLENGE" or "WEEKLY CHALLENGES";
    }

    private static GtaEventClassification Result(
        GtaEventClassificationKind kind,
        int anchorCount,
        bool heading,
        bool source,
        string reason) => new(kind, anchorCount, heading, source, reason);
}

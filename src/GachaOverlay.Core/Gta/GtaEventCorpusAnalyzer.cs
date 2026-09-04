namespace GachaOverlay.Core.Gta;

public sealed record GtaEventCorpusAnalysis(
    int MessageCount,
    int WeeklyCount,
    int CampaignCount,
    IReadOnlyDictionary<string, int> HeadingCounts,
    IReadOnlyList<GtaUnknownVocabularyEntry> UnknownVocabulary);

public sealed class GtaEventCorpusAnalyzer
{
    private readonly CanonicalEventDocumentBuilder _builder = new();
    private readonly GtaEventVocabulary _vocabulary = new();
    private readonly GtaEventClassifier _classifier;
    private readonly GtaUnknownVocabularyReport _unknown = new();
    private readonly GtaEventParser _parser;

    public GtaEventCorpusAnalyzer()
    {
        _classifier = new GtaEventClassifier(_vocabulary);
        _parser = new GtaEventParser(_vocabulary, _unknown);
    }

    public GtaEventCorpusAnalysis Analyze(IEnumerable<GtaEventSourceInput> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var headings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        var weekly = 0;
        var campaigns = 0;
        foreach (var source in sources)
        {
            var document = _builder.Build(source);
            var classification = _classifier.Classify(document);
            _ = _parser.Parse(document, classification);
            count++;
            if (classification.Kind == GtaEventClassificationKind.WeeklyBulletin) weekly++;
            if (classification.Kind == GtaEventClassificationKind.MultiWeekCampaign) campaigns++;
            foreach (var line in document.CanonicalText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var family = _vocabulary.MatchHeadingFamily(line);
                if (family is not null || GtaEventVocabulary.IsCampaignHeading(line))
                {
                    var key = family ?? GtaEventTextNormalizer.NormalizeIdentity(line);
                    headings[key] = headings.GetValueOrDefault(key) + 1;
                }
            }
        }

        return new GtaEventCorpusAnalysis(
            count,
            weekly,
            campaigns,
            headings.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
            _unknown.Snapshot());
    }
}

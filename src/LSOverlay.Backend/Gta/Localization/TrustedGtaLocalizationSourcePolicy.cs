using System.Text.Json;
using GachaOverlay.Core.Gta;
using GachaOverlay.Core.Gta.Localization;

namespace LSOverlay.Backend.Gta.Localization;

internal sealed record PublicGtaText(string Id, string Type, string Text,
    IReadOnlyDictionary<string, string>? ProtectedTerms = null);
internal sealed record PublicGtaLocalizationInput(string SourceRevision, IReadOnlyList<PublicGtaText> Items, bool IsComplete = true);

internal static class TrustedGtaLocalizationSourcePolicy
{
    public const ulong ChannelId = 1417898156187713577;
    public const ulong AuthorId = 1417898538385539085;
    public const string Version = "trusted-relay-2-weekly-fields";
    public static bool Allows(ulong channelId, ulong authorId) => channelId == ChannelId && authorId == AuthorId;

    public static PublicGtaLocalizationInput? Extract(CanonicalEventDocument document)
    {
        if (!document.OwnInputIntegrity.IsComplete || !Allows(document.ChannelId, document.AuthorId) || string.IsNullOrWhiteSpace(document.OwnCanonicalText) ||
            document.OwnCanonicalText.Length > 16 * 1024) return null;
        var text = document.OwnCanonicalText;
        var own = document with { CanonicalText = text, CanonicalBlocks = [], IsForwarded = false };
        var classifier = new GtaEventClassifier();
        var parsed = new GtaEventParser().Parse(own, classifier.Classify(own));
        // Weekly bulletins commonly contain linked races and a ping in the opening.
        // Keep whole-field exclusion (never send stripped or guessed fragments),
        // while allowing unrelated safe weekly fields through the existing pipeline.
        // Campaign policy remains unchanged.
        if (parsed.Week is null && ContainsPrivateReference(text)) return null;
        var fields = new List<PublicGtaText>();
        void Add(string kind, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !ContainsPrivateReference(value) && !fields.Any(f => f.Text == value))
                fields.Add(new($"field.{fields.Count:D3}", kind, value));
        }
        if (parsed.Week is { } week)
        {
            if (week.WeeklyChallenge is { } challenge)
            {
                Add("challenge", challenge.OriginalText); Add("reward", challenge.Reward);
                foreach (var requirement in challenge.Requirements) Add("requirement", requirement);
            }
            Add("title", week.Theme);
            foreach (var item in week.Bonuses.Concat(week.Discounts).Concat(week.FreeItems).Concat(week.OtherEvents))
                Add(item.Kind.ToString(), item.OriginalLabel);
        }
        if (parsed.Campaign is { } campaign)
        {
            Add("title", campaign.Title);
            foreach (var goal in campaign.Goals) Add("goal", goal);
            foreach (var reward in campaign.Rewards) Add("reward", reward);
            foreach (var plannedWeek in campaign.PlannedWeeks) Add("schedule", plannedWeek.Label);
        }
        if (fields.Count is 0 or > 64 || fields.Sum(f => f.Text.Length) > 12000) return null;
        // Includes all normalized own content, including headings not rendered as fields.
        // No Discord IDs, author/profile data or transport state enters this DTO/hash.
        return new(GtaLocalizationGlossary.Digest(text + "\n" + JsonSerializer.Serialize(fields)), fields.AsReadOnly());
    }

    private static bool ContainsPrivateReference(string text)
    {
        if (new[] { "@", "<#", "<:", "<a:", "http://", "https://" }.Any(marker =>
            text.Contains(marker, StringComparison.OrdinalIgnoreCase))) return true;
        var digits = 0;
        foreach (var character in text)
        {
            digits = char.IsDigit(character) ? digits + 1 : 0;
            if (digits >= 17) return true;
        }
        return false;
    }
}

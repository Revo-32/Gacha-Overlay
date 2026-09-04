using System.Text;
using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta;

public sealed partial class CanonicalEventDocumentBuilder
{
    public const int MaximumBlocks = 64;
    public const int MaximumBlockLength = 2048;
    public const int MaximumCanonicalLength = 16 * 1024;

    public CanonicalEventDocument Build(GtaEventSourceInput source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SourceMessageId == 0 || source.ChannelId == 0)
        {
            throw new ArgumentException("Discord source identity is required.", nameof(source));
        }

        var blocks = new List<CanonicalEventBlock>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string kind, string? value)
        {
            if (blocks.Count >= MaximumBlocks || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = GtaEventTextNormalizer.Normalize(value);
            if (normalized.Length == 0)
            {
                return;
            }

            normalized = normalized.Length <= MaximumBlockLength
                ? normalized
                : normalized[..MaximumBlockLength].TrimEnd();
            if (seen.Add(normalized))
            {
                blocks.Add(new CanonicalEventBlock(kind, normalized));
            }
        }

        foreach (var forwarded in source.ForwardedSnapshots ?? Array.Empty<GtaEventForwardInput>())
        {
            Add("ForwardContent", forwarded.Content);
            AddEmbeds("ForwardEmbed", forwarded.Embeds, Add);
        }

        Add("Content", source.Content);
        AddEmbeds("Embed", source.Embeds, Add);

        var canonical = string.Join('\n', blocks.Select(block => block.Text));
        if (canonical.Length > MaximumCanonicalLength)
        {
            canonical = canonical[..MaximumCanonicalLength].TrimEnd();
        }

        var publisher = FirstMeaningful(
            source.SourcePublisher,
            source.ForwardedSnapshots?.SelectMany(item => item.Embeds)
                .SelectMany(embed => new[] { embed.ProviderName, embed.AuthorName }),
            source.Embeds?.SelectMany(embed => new[] { embed.ProviderName, embed.AuthorName }));
        return new CanonicalEventDocument(
            source.SourceMessageId,
            source.ChannelId,
            source.ReceivedAt,
            source.EditedAt,
            publisher,
            NormalizeMetadata(source.SourceChannelName),
            source.ForwardedSnapshots?.Count > 0,
            blocks,
            canonical);
    }

    private static void AddEmbeds(
        string prefix,
        IReadOnlyList<GtaEventEmbedInput>? embeds,
        Action<string, string?> add)
    {
        foreach (var embed in embeds ?? Array.Empty<GtaEventEmbedInput>())
        {
            add(prefix + "Title", embed.Title);
            add(prefix + "Description", embed.Description);
            foreach (var field in embed.Fields ?? Array.Empty<GtaEventEmbedFieldInput>())
            {
                add(prefix + "Field", $"{field.Name}\n{field.Value}");
            }
        }
    }

    private static string? FirstMeaningful(
        string? explicitValue,
        IEnumerable<string?>? first,
        IEnumerable<string?>? second) =>
        new[] { explicitValue }
            .Concat(first ?? Array.Empty<string?>())
            .Concat(second ?? Array.Empty<string?>())
            .Select(NormalizeMetadata)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? NormalizeMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = RepeatedWhitespaceRegex().Replace(value.Trim(), " ");
        return normalized.Length <= 128 ? normalized : normalized[..128];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex RepeatedWhitespaceRegex();
}

public static partial class GtaEventTextNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('–', '-')
            .Replace('—', '-')
            .Replace('−', '-')
            .Normalize(NormalizationForm.FormKC);
        var lines = new List<string>();
        var lastBlank = true;
        foreach (var raw in text.Split('\n'))
        {
            var line = HeadingPrefixRegex().Replace(raw.Trim(), string.Empty);
            line = FormattingMarkerRegex().Replace(line, string.Empty);
            line = RepeatedHorizontalWhitespaceRegex().Replace(line, " ").Trim();
            if (line.Length == 0)
            {
                if (!lastBlank)
                {
                    lines.Add(string.Empty);
                }

                lastBlank = true;
                continue;
            }

            lines.Add(line);
            lastBlank = false;
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    public static string NormalizeIdentity(string? value)
    {
        var normalized = Normalize(value).ToUpperInvariant();
        return IdentityPunctuationRegex().Replace(normalized, " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Aggregate(string.Empty, (current, part) =>
                current.Length == 0 ? part : current + " " + part);
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*")]
    private static partial Regex HeadingPrefixRegex();

    [GeneratedRegex(@"\*\*|__|~~|`+")]
    private static partial Regex FormattingMarkerRegex();

    [GeneratedRegex(@"[\t\u00A0 ]+")]
    private static partial Regex RepeatedHorizontalWhitespaceRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}$%+]+")]
    private static partial Regex IdentityPunctuationRegex();
}

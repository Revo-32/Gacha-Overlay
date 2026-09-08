using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta.Localization;

public sealed record LocalizationTerm(string Id, string Source, string Ko, string Category,
    string Policy, string[] Aliases, bool ProtectOutput, bool CaseSensitive);

public sealed class GtaLocalizationGlossary
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static GtaLocalizationGlossary Default { get; } = Parse(ReadResource("gta-online-ko-glossary.json"));
    public IReadOnlyList<LocalizationTerm> Entries { get; }
    public string Version { get; }
    public string Hash { get; }
    private readonly Lazy<(Regex Pattern, LocalizationTerm Term)[]> _patterns;

    private GtaLocalizationGlossary(Document document)
    {
        Version = document.GlossaryVersion;
        Entries = Array.AsReadOnly(document.Entries);
        Hash = Digest(JsonSerializer.Serialize(document, Json));
        _patterns = new(() => Entries.Where(t => t.ProtectOutput).SelectMany(t => t.Aliases.Prepend(t.Source)
            .Select(alias => (Alias: alias, Term: t)))
            .OrderByDescending(t => t.Alias.Length).ThenBy(t => t.Alias, StringComparer.Ordinal)
            .Select(t => (new Regex(@"\G" + Regex.Escape(t.Alias) + @"(?![\p{L}\p{N}_])",
                RegexOptions.CultureInvariant | (t.Term.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                TimeSpan.FromMilliseconds(100)), t.Term)).ToArray());
    }

    public (int Length, string Output)? Match(string text, int offset)
    {
        if (offset > 0 && (char.IsLetterOrDigit(text[offset - 1]) || text[offset - 1] == '_')) return null;
        foreach (var (pattern, term) in _patterns.Value)
        {
            var match = pattern.Match(text, offset);
            if (match.Success) return (match.Length, term.Ko);
        }
        return null;
    }

    public static GtaLocalizationGlossary Parse(string json)
    {
        if (json.Length > 512 * 1024) throw new InvalidDataException("GlossarySize");
        var doc = JsonSerializer.Deserialize<Document>(json, Json) ?? throw new InvalidDataException("GlossarySchema");
        if (doc.SchemaVersion != 1 || doc.Locale != "ko-KR" || string.IsNullOrWhiteSpace(doc.GlossaryVersion) ||
            doc.Entries is null || doc.Entries.Length is < 1 or > 600) throw new InvalidDataException("GlossarySchema");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] categories = ["weapon", "property", "mc_business", "upgrade", "economy", "activity", "heist",
            "dlc", "side_content", "recent", "adversary", "weekly", "vehicle_class", "context", "token"];
        foreach (var term in doc.Entries)
        {
            if (term is null || string.IsNullOrWhiteSpace(term.Id) || !ids.Add(term.Id) ||
                !categories.Contains(term.Category) || term.Aliases is null || term.Aliases.Length > 16 ||
                term.Policy is not ("official" or "preferred" or "preserve" or "preserve_until_verified" or "contextual") ||
                term.Ko is null || term.Ko.Length > 200 || term.Ko.Contains("[[", StringComparison.Ordinal) ||
                (term.Policy != "contextual" && (string.IsNullOrWhiteSpace(term.Ko) || !term.ProtectOutput)) ||
                (term.Policy == "contextual" && term.ProtectOutput) ||
                (term.Policy is "preserve" or "preserve_until_verified" && term.Ko != term.Source))
                throw new InvalidDataException("GlossaryEntry");
            foreach (var alias in term.Aliases.Prepend(term.Source))
                if (string.IsNullOrWhiteSpace(alias) || alias.Length > 200 || alias.Contains("[[", StringComparison.Ordinal) || !aliases.Add(alias))
                    throw new InvalidDataException("GlossaryAlias");
        }
        return new(doc);
    }

    public static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string ReadResource(string name)
    {
        using var stream = typeof(GtaLocalizationGlossary).Assembly.GetManifestResourceStream("GachaOverlay.Core.Gta.Localization." + name)
            ?? throw new InvalidDataException("LocalizationResourceMissing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private sealed record Document(int SchemaVersion, string GlossaryVersion, string Locale, LocalizationTerm[] Entries);
}

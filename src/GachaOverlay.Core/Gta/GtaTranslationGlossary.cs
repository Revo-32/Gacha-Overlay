using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta;

public sealed class GtaTranslationGlossary
{
    private readonly IReadOnlyDictionary<string, string> _terms;
    private readonly Regex? _pattern;
    public string Version { get; }
    public IReadOnlyList<GtaGlossaryEntry> Entries { get; }
    public static GtaTranslationGlossary Default { get; } = LoadDefault();
    public GtaTranslationGlossary(string version, IReadOnlyList<GtaGlossaryEntry> entries)
    {
        Entries = entries.Take(512).ToArray();
        Version = version + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Entries))));
        var terms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries.OrderBy(x => x.TranslationSource == GtaTranslationSource.Curated ? 0 : 1))
        {
            if (entry.TranslationSource == GtaTranslationSource.OriginalFallback) continue;
            foreach (var alias in entry.EnglishAliases.Prepend(entry.EnglishName)) terms.TryAdd(alias, entry.KoreanDisplayName);
        }
        _terms = terms;
        if (terms.Count > 0)
            _pattern = new Regex(@"(?<![\p{L}\p{N}_])(?:" + string.Join('|', terms.Keys.OrderByDescending(x => x.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
    public string Translate(string source) => _pattern?.Replace(source, match => _terms[match.Value]) ?? source;
    public static GtaTranslationGlossary Parse(string json)
    {
        try
        {
            if (json.Length > 256 * 1024) return new("invalid", []);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter());
            var document = JsonSerializer.Deserialize<Document>(json, options);
            if (document is null || string.IsNullOrWhiteSpace(document.Version) || document.Version.Length > 64 ||
                document.Entries is null || document.Entries.Count > 512 || document.Entries.Any(x =>
                    x is null || string.IsNullOrWhiteSpace(x.EnglishName) || x.EnglishName.Length > 160 ||
                    string.IsNullOrWhiteSpace(x.KoreanDisplayName) || x.KoreanDisplayName.Length > 160 ||
                    x.EnglishAliases is null || x.EnglishAliases.Count > 16 || x.EnglishAliases.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 160) ||
                    !Enum.IsDefined(x.TranslationSource))) return new("invalid", []);
            return new(document.Version, document.Entries);
        }
        catch (JsonException) { return new("invalid", []); }
    }
    private static GtaTranslationGlossary LoadDefault()
    {
        using var stream = typeof(GtaTranslationGlossary).Assembly.GetManifestResourceStream("GachaOverlay.Core.Gta.TranslationGlossary.ko.json");
        if (stream is null) return new("missing", []);
        using var reader = new StreamReader(stream);
        var legacy = Parse(reader.ReadToEnd());
        var approved = Localization.GtaLocalizationGlossary.Default;
        var entries = approved.Entries.Where(term => term.ProtectOutput).Select(term =>
        {
            var previous = legacy.Entries.FirstOrDefault(old => old.EnglishName.Equals(term.Source, StringComparison.OrdinalIgnoreCase));
            return new GtaGlossaryEntry(previous?.CanonicalId ?? term.Id, term.Source, term.Aliases,
                term.Ko, term.Category, previous?.TranslationSource == GtaTranslationSource.RockstarOfficial
                    ? GtaTranslationSource.RockstarOfficial : GtaTranslationSource.Curated);
        }).ToList();
        // Keep legacy-only aliases for compatibility, but never let them override
        // a newly approved source or alias (Gunrunning is no longer Bunker).
        var recognized = entries.SelectMany(e => e.EnglishAliases.Prepend(e.EnglishName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in legacy.Entries)
        {
            var index = entries.FindIndex(e => e.EnglishName.Equals(old.EnglishName, StringComparison.OrdinalIgnoreCase));
            var aliases = old.EnglishAliases.Where(alias => !recognized.Contains(alias)).ToArray();
            if (index >= 0) entries[index] = entries[index] with { EnglishAliases = entries[index].EnglishAliases.Concat(aliases).ToArray() };
            else if (!recognized.Contains(old.EnglishName)) entries.Add(old with { EnglishAliases = aliases });
        }
        return new(approved.Version + ":" + approved.Hash, entries);
    }
    private sealed record Document(string Version, IReadOnlyList<GtaGlossaryEntry> Entries);
}

public static partial class GtaTranslationIntegrity
{
    public static bool IsValid(string source, string? translation) =>
        !string.IsNullOrWhiteSpace(translation) && !translation.Contains('\uFFFD') &&
        Numbers(source).SequenceEqual(Numbers(translation)) &&
        Protected(source).SequenceEqual(Protected(translation));
    public static string Select(string source, string? current, string? lastGood = null) =>
        IsValid(source, current) ? current! : IsValid(source, lastGood) ? lastGood! : source;
    private static IEnumerable<string> Numbers(string value) => NumberPattern().Matches(value)
        // Conservative fallback also rejects date/amount permutations with the same digits.
        .Select(x => x.Value.Replace(",", "", StringComparison.Ordinal));
    private static IEnumerable<string> Protected(string value) => ProtectedPattern().Matches(value)
        .Select(x => x.Value.ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal)
            .Replace("×", "X", StringComparison.Ordinal).Replace("배", "X", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal);
    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
    [GeneratedRegex(@"\d+(?:\.\d+)?\s*(?:%|[X×]|배)(?![\p{L}\p{N}])|GTA\$|(?<![\p{L}\p{N}])(?:RP|HSW)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedPattern();
}

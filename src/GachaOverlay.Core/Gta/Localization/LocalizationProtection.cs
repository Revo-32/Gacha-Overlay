using System.Text;
using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta.Localization;

public sealed record ProtectedLocalizationText(string Source, string Text, IReadOnlyDictionary<string, string> Tokens)
{
    public IReadOnlyDictionary<string, LocalizationQuantity> Quantities { get; init; } = new Dictionary<string, LocalizationQuantity>();
}

public sealed class LocalizationProtection(GtaLocalizationGlossary glossary)
{
    // Unknown words are preserved, not guessed. This deliberately favors safe English
    // fragments over inventing a vehicle, creator-job or new activity name.
    private static readonly HashSet<string> Grammar = new(("a an the and or on in at to for from with of by " +
        "earn get off complete receive claim available through log login this week weekly all your " +
        "you can will be is are as during including plus only each every win play sell purchase " +
        "rewards reward bonus bonuses discounts discount missions jobs races vehicles times days hours minutes " +
        "seconds weeks months collect deliver participate finish survive destroy steal source first " +
        "time completion new double triple now until enjoy take part unlock free more than least " +
        "requirements requirement inventory select selected receive completing winning selling once twice")
        .Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Fact = new(@"\G(?:\b(?:January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+\d{1,2}(?:,?\s+\d{4})?|(?:GTA\$|\$)?\d[\d,]*(?:[.:/-]\d+)*(?:\s*(?:%|[X×]|RP|seconds?|minutes?|hours?|days?|weeks?|months?))?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Word = new(@"\G[\p{L}\p{N}]+(?:['’_-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Token = new(@"\[\[L\d{3}_\d{4}\]\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public ProtectedLocalizationText Protect(string source, int fieldIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 2048 || source.Contains("[[", StringComparison.Ordinal) ||
            source.Contains("]]", StringComparison.Ordinal) || source.Contains('\uFFFD')) throw new InvalidDataException("InputText");
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var quantities = new Dictionary<string, LocalizationQuantity>(StringComparer.Ordinal);
        var output = new StringBuilder();
        void Append(string value, LocalizationQuantity? quantity = null)
        {
            var key = $"[[L{fieldIndex:D3}_{tokens.Count:D4}]]";
            tokens.Add(key, value);
            if (quantity is not null) quantities.Add(key, quantity);
            output.Append(key);
        }
        for (var i = 0; i < source.Length;)
        {
            if (source[i] is '"' or '“')
            {
                var end = source.IndexOf(source[i] == '“' ? '”' : '"', i + 1);
                if (end > i + 1) { Append(source[i..(end + 1)]); i = end + 1; continue; }
            }
            if (glossary.Match(source, i) is { } term)
            {
                Append(term.Output); i += term.Length; continue;
            }
            var fact = Fact.Match(source, i);
            if (fact.Success && (i + fact.Length == source.Length || !char.IsLetterOrDigit(source[i + fact.Length])))
            {
                var quantity = KoreanLocalizationSurface.ParseQuantity(fact.Value);
                Append(quantity?.Display ?? fact.Value, quantity); i += fact.Length; continue;
            }
            var word = Word.Match(source, i);
            if (word.Success && !Grammar.Contains(word.Value) && word.Value.Any(c => c <= 127 && char.IsLetterOrDigit(c)))
            {
                var end = i + word.Length;
                // Preserve a whole unknown proper-name span, including class-like words
                // inside it (e.g. an unknown model ending in "Sport" or "Van").
                while (end < source.Length && source[end] == ' ')
                {
                    var next = Word.Match(source, end + 1);
                    if (!next.Success || Grammar.Contains(next.Value)) break;
                    end += 1 + next.Length;
                }
                Append(source[i..end]); i = end; continue;
            }
            if (word.Success) { output.Append(word.Value); i += word.Length; }
            else { output.Append(source[i]); i++; }
        }
        return new(source, output.ToString(), tokens) { Quantities = quantities };
    }

    public static bool TryRestore(ProtectedLocalizationText input, string? text, out string restored, out string reason)
    {
        restored = input.Source;
        reason = "Content";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096 || text.Contains('`') || text.Contains('\uFFFD')) return false;
        var found = Token.Matches(text).Select(m => m.Value).ToArray();
        if (found.Length != input.Tokens.Count || found.Distinct(StringComparer.Ordinal).Count() != found.Length ||
            found.Any(t => !input.Tokens.ContainsKey(t))) { reason = "Placeholder"; return false; }
        var remainder = Token.Replace(text, "");
        if (Regex.IsMatch(Token.Replace(input.Text, ""), @"\b(?:earn|get|complete|receive|claim|available|win|sell|collect|play)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) &&
            !remainder.Any(c => c is >= '\uAC00' and <= '\uD7A3')) return false;
        if (remainder.Contains('[') || remainder.Contains(']') || remainder.Any(char.IsDigit)) { reason = "NumericOrPlaceholder"; return false; }
        // The model may localize grammar, never introduce new Latin proper names/tokens.
        if (remainder.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')) { reason = "UnprotectedToken"; return false; }
        if (remainder.Any(char.IsLetter) && !remainder.Any(c => c is >= '\uAC00' and <= '\uD7A3')) return false;
        if (remainder.Length > Math.Max(80, input.Source.Length * 3)) return false;
        // Normalize only known reward coordination between separate protected atoms.
        // Never rewrite a quoted creator name or a monetary amount containing these characters.
        var display = new Dictionary<string, string>(input.Tokens, StringComparer.Ordinal);
        foreach (var fact in input.Quantities)
        {
            // Units belong to the protected semantic atom, never to model-generated grammar.
            if (Regex.IsMatch(text, Regex.Escape(fact.Key) + @"\s*(?:초|분|시간|일|주일?|개월|배|퍼센트|%)",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) { reason = "QuantityUnit"; return false; }
        }
        foreach (var currency in input.Tokens.Where(t => t.Value == "GTA$"))
            foreach (var rp in input.Tokens.Where(t => t.Value == "RP"))
            {
                var coordination = new Regex(Regex.Escape(currency.Key) + @"\s*(?:및|와|과)\s*" + Regex.Escape(rp.Key),
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (!coordination.IsMatch(text)) continue;
                display[currency.Key] = "GTA 달러";
                text = coordination.Replace(text, currency.Key + "와 " + rp.Key);
            }
        var validSurface = true;
        var surfacePattern = new Regex(@"(?<token>\[\[L\d{3}_\d{4}\]\])(?<particle>" + KoreanLocalizationSurface.ParticlePattern + @")?",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var remainingGrammar = surfacePattern.Replace(text, match =>
        {
            var value = display[match.Groups["token"].Value];
            var particle = match.Groups["particle"].Value;
            if (!KoreanLocalizationSurface.TryParticle(value, particle, out _)) validSurface = false;
            return "";
        });
        if (!validSurface || KoreanLocalizationSurface.HasMechanicalParticle(remainingGrammar)) { reason = "KoreanSurface"; return false; }
        restored = surfacePattern.Replace(text, match =>
        {
            var value = display[match.Groups["token"].Value];
            KoreanLocalizationSurface.TryParticle(value, match.Groups["particle"].Value, out var particle);
            return value + particle;
        });
        reason = "Valid";
        return true;
    }
}

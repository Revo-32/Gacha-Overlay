using System.Text;
using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta.Localization;

public sealed record ProtectedLocalizationText(string Source, string Text, IReadOnlyDictionary<string, string> Tokens)
{
    public IReadOnlyDictionary<string, LocalizationQuantity> Quantities { get; init; } = new Dictionary<string, LocalizationQuantity>();
}

public sealed class LocalizationProtection(GtaLocalizationGlossary glossary)
{
    public const string Version = "gta-protect-2";
    private static readonly string[] NumberWords = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];
    private static readonly Regex NumberWord = new(@"\G(?:one|two|three|four|five|six|seven|eight|nine|ten)\b(?:\s+(?:seconds?|minutes?|hours?|days?|weeks?|months?))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Fact = new(@"\G(?:\b(?:January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sept?|Oct|Nov|Dec)\s+\d{1,2}(?:-\d{1,2})?(?:,?\s+\d{4})?|(?:GTA\$|\$)?\d[\d,]*(?:[.:/-]\d+)*(?:\s*(?:%|[X×]|RP|seconds?|minutes?|hours?|days?|weeks?|months?))?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Word = new(@"\G[\p{L}\p{N}]+(?:['’_-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Token = new(@"\[\[L\d{3}_\d{4}\]\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Platform = new(@"\G(?:PS[345]|PlayStation [345]|Xbox Series X\|S|Xbox One|PC Enhanced|PC Legacy)(?![\p{L}\p{N}_])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public ProtectedLocalizationText Protect(string source, int fieldIndex = 0, LocalizationEntityKind? entityKind = null)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 2048 || source.Contains("[[", StringComparison.Ordinal) ||
            source.Contains("]]", StringComparison.Ordinal) || source.Contains('\uFFFD')) throw new InvalidDataException("InputText");
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var quantities = new Dictionary<string, LocalizationQuantity>(StringComparer.Ordinal);
        var output = new StringBuilder();
        var entities = LocalizationEntities.Extract(source, entityKind);
        void Append(string value, LocalizationQuantity? quantity = null)
        {
            var key = $"[[L{fieldIndex:D3}_{tokens.Count:D4}]]";
            tokens.Add(key, value);
            if (quantity is not null) quantities.Add(key, quantity);
            output.Append(key);
        }
        for (var i = 0; i < source.Length;)
        {
            var platform = Platform.Match(source, i);
            if (platform.Success) { Append(platform.Value); i += platform.Length; continue; }
            var fact = Fact.Match(source, i);
            if (fact.Success && (i + fact.Length == source.Length || !char.IsLetterOrDigit(source[i + fact.Length])))
            {
                var quantity = KoreanLocalizationSurface.ParseQuantity(fact.Value);
                Append(quantity?.Display ?? fact.Value, quantity); i += fact.Length; continue;
            }
            // Longest approved compound wins before any inferred entity/prose handling.
            if (glossary.Match(source, i) is { } term)
            {
                Append(term.Output); i += term.Length; continue;
            }
            if (entities.FirstOrDefault(e => e.Start == i) is { } entity)
            {
                Append(source.Substring(i, entity.Length)); i += entity.Length; continue;
            }
            var numericWord = NumberWord.Match(source, i);
            if (numericWord.Success)
            {
                var parts = numericWord.Value.Split(' ', 2);
                var number = (Array.FindIndex(NumberWords, word => word.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) + 1)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                var quantity = parts.Length == 2 ? KoreanLocalizationSurface.ParseQuantity(number + " " + parts[1]) : null;
                Append(quantity?.Display ?? number, quantity); i += numericWord.Length; continue;
            }
            // Unknown prose is translatable. Neither capitalization nor dictionary absence is evidence.
            var word = Word.Match(source, i);
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
        foreach (var term in input.Tokens)
        {
            var finalWord = Regex.Match(term.Value, @"(?:^|\s)([가-힣]{2,})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (finalWord.Success && Regex.IsMatch(text, Regex.Escape(term.Key) + @"\s+" + finalWord.Groups[1].Value + @"(?=\s|$|[.,:;])",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) { reason = "RepeatedTerm"; return false; }
        }
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

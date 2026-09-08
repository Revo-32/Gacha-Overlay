using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta.Localization;

public sealed record LocalizationQuantity(string Value, string Unit, string Display);

// Deliberately scoped to protected atoms, not an arbitrary Korean prose rewriter.
public static class KoreanLocalizationSurface
{
    public const string Version = "ko-surface-1";
    private enum Ending { Vowel, Consonant, Rieul }
    private static readonly IReadOnlyDictionary<string, Ending> Pronunciations = new Dictionary<string, Ending>(StringComparer.OrdinalIgnoreCase)
    {
        ["RP"] = Ending.Vowel,
        ["GTA$"] = Ending.Vowel,
        ["HSW"] = Ending.Vowel,
        ["CEO"] = Ending.Vowel,
        ["MC"] = Ending.Vowel,
        ["GTA"] = Ending.Vowel,
        ["GTA Online"] = Ending.Consonant,
        ["GTA+"] = Ending.Vowel,
        ["VIP"] = Ending.Vowel,
        ["PC"] = Ending.Vowel,
        ["LS"] = Ending.Vowel,
        ["LSIA"] = Ending.Vowel,
        ["MOC"] = Ending.Vowel,
        ["FIB"] = Ending.Vowel,
        ["IAA"] = Ending.Vowel,
        ["LSPD"] = Ending.Vowel,
        ["Mk II"] = Ending.Vowel,
        ["Grotti Turismo Omaggio"] = Ending.Vowel,
        ["Mansion Raid"] = Ending.Vowel,
        ["KnoWay Out"] = Ending.Consonant,
        ["Bravado Banshee GTS"] = Ending.Vowel
    };
    private static readonly Regex Quantity = new(@"\A(?<value>\d+(?:\.\d+)?)\s*(?<unit>seconds?|minutes?|hours?|days?|weeks?|months?|[x×])\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    internal const string ParticlePattern = @"(?:으로\(로\)|로\(으로\)|을\(를\)|를\(을\)|은\(는\)|는\(은\)|이\(가\)|가\(이\)|과\(와\)|와\(과\)|으로|은|는|이|가|을|를|과|와|로)(?=\s|$|[.,!?;:])";
    private static readonly Regex MechanicalParticle = new(@"(?:을\(를\)|를\(을\)|은\(는\)|는\(은\)|이\(가\)|가\(이\)|과\(와\)|와\(과\)|으로\(로\)|로\(으로\))",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static LocalizationQuantity? ParseQuantity(string source)
    {
        var match = Quantity.Match(source);
        if (!match.Success) return null;
        var value = match.Groups["value"].Value;
        var unit = match.Groups["unit"].Value.ToLowerInvariant().TrimEnd('s');
        if (unit is "x" or "×") unit = "multiplier";
        var displayUnit = unit switch
        {
            "second" => "초",
            "minute" => "분",
            "hour" => "시간",
            "day" => "일",
            "week" => "주",
            "month" => "개월",
            "multiplier" => "배",
            _ => throw new InvalidDataException("QuantityUnit")
        };
        return new(value, unit, value + displayUnit);
    }

    public static bool TryParticle(string surface, string particle, out string result)
    {
        result = particle;
        if (!TryEnding(surface, out var ending)) return !MechanicalParticle.IsMatch(particle);
        result = particle switch
        {
            "은" or "는" or "은(는)" or "는(은)" => ending == Ending.Vowel ? "는" : "은",
            "이" or "가" or "이(가)" or "가(이)" => ending == Ending.Vowel ? "가" : "이",
            "을" or "를" or "을(를)" or "를(을)" => ending == Ending.Vowel ? "를" : "을",
            "과" or "와" or "과(와)" or "와(과)" => ending == Ending.Vowel ? "와" : "과",
            "으로" or "로" or "으로(로)" or "로(으로)" => ending is Ending.Vowel or Ending.Rieul ? "로" : "으로",
            _ => particle
        };
        return true;
    }

    internal static bool HasMechanicalParticle(string grammar) => MechanicalParticle.IsMatch(grammar);

    private static bool TryEnding(string surface, out Ending ending)
    {
        var text = surface.TrimEnd(' ', '"', '”', '\'');
        if (Pronunciations.TryGetValue(text, out ending)) return true;
        if (text.Length == 0) return false;
        var last = text[^1];
        if (last is >= '\uAC00' and <= '\uD7A3')
        {
            var jongseong = (last - '\uAC00') % 28;
            ending = jongseong == 0 ? Ending.Vowel : jongseong == 8 ? Ending.Rieul : Ending.Consonant;
            return true;
        }
        // Numeric atoms use Korean number readings; never infer Latin pronunciation from one ASCII letter.
        if (last is >= '0' and <= '9')
        {
            ending = last is '2' or '4' or '5' or '9' ? Ending.Vowel : last is '1' or '7' or '8' ? Ending.Rieul : Ending.Consonant;
            return true;
        }
        return false;
    }
}

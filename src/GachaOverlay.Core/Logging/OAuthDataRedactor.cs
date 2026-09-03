using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Logging;

public static partial class OAuthDataRedactor
{
    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length > 4 * 1024 * 1024) return "[REDACTED]";
        try
        {
            var result = CallbackQuery().Replace(value, "/auth/discord/callback");
            result = ClaimHeader().Replace(result, "LSOAuthClaim [REDACTED]");
            result = QuotedSensitiveField().Replace(result, match => match.Groups["prefix"].Value + "[REDACTED]");
            result = SingleQuotedSensitiveField().Replace(result, match => match.Groups["prefix"].Value + "[REDACTED]");
            return SensitiveField().Replace(result, match => match.Groups["prefix"].Value + "[REDACTED]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED]";
        }
    }

    private const string Fields = @"(?:code|state|(?:oauth[_-]?)?(?:access[_-]?token|refresh[_-]?token|client[_-]?secret)|(?:login[_-]?)?claim[_-]?secret|code[_-]?verifier)";

    [GeneratedRegex(@"(?<prefix>(?<![\w])" + Fields + @"[""']?\s*[:=]\s*"")(?:\\.|[^""\\\r\n])*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex QuotedSensitiveField();
    [GeneratedRegex(@"(?<prefix>(?<![\w])" + Fields + @"[""']?\s*[:=]\s*')(?:\\.|[^'\\\r\n])*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex SingleQuotedSensitiveField();
    [GeneratedRegex(@"/auth/discord/callback\?[^\s""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex CallbackQuery();
    [GeneratedRegex(@"LSOAuthClaim\s+(?!\[REDACTED\])[^\s""',;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex ClaimHeader();
    [GeneratedRegex(@"(?<prefix>(?<![\w])" + Fields + @"[""']?\s*[:=]\s*[""']?)(?!\[REDACTED\])[^\s""',;&}\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex SensitiveField();
}

using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Logging;

public static partial class OAuthDataRedactor
{
    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var result = CallbackQuery().Replace(value, "/auth/discord/callback");
        result = ClaimHeader().Replace(result, "LSOAuthClaim [REDACTED]");
        return SensitiveField().Replace(result, match => match.Groups["prefix"].Value + "[REDACTED]");
    }

    [GeneratedRegex(@"/auth/discord/callback\?[^\s""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CallbackQuery();
    [GeneratedRegex(@"LSOAuthClaim\s+(?!\[REDACTED\])[^\s""',;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaimHeader();
    [GeneratedRegex(@"(?<prefix>(?<![\w])(?:code|state|(?:oauth[_-]?)?(?:access[_-]?token|refresh[_-]?token|client[_-]?secret)|(?:login[_-]?)?claim[_-]?secret|code[_-]?verifier)[""']?\s*[:=]\s*[""']?)(?!\[REDACTED\])[^\s""',;&}\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveField();
}

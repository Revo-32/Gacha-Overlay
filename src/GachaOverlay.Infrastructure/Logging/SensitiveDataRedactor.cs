using System.Text.RegularExpressions;

namespace GachaOverlay.Infrastructure.Logging;

internal static partial class SensitiveDataRedactor
{
    internal const string Replacement = "[REDACTED]";

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length > 4 * 1024 * 1024) return Replacement;
        try
        {
            var sanitized = JsonSensitiveFieldPattern().Replace(
                GachaOverlay.Core.Logging.OAuthDataRedactor.Sanitize(value),
                match => match.Groups["prefix"].Value + Replacement);
            sanitized = SingleQuotedSensitiveFieldPattern().Replace(
                sanitized, match => match.Groups["prefix"].Value + Replacement);
            sanitized = AuthorizationCredentialPattern().Replace(
                sanitized,
                match => match.Groups["prefix"].Value + Replacement);
            sanitized = StandaloneBearerPattern().Replace(
                sanitized,
                match => match.Groups["prefix"].Value + Replacement);
            sanitized = StandalonePairingPattern().Replace(
                sanitized,
                match => match.Groups["prefix"].Value + Replacement);
            return SensitiveFieldPattern().Replace(
                sanitized,
                match => match.Groups["prefix"].Value + Replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            return Replacement;
        }
    }

    [GeneratedRegex(
        "(?<prefix>\"(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|pairing[_-]?(?:claim[_-]?)?secret|authorization|[A-Za-z0-9_-]*credential[A-Za-z0-9_-]*|secret|content)\"\\s*:\\s*\")(?<secret>(?:\\\\.|[^\"\\\\])*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex JsonSensitiveFieldPattern();

    [GeneratedRegex(
        @"(?<prefix>(?<![\w])(?:[\w-]*credential[\w-]*|secret|content|authorization)[""']?\s*[:=]\s*')(?:\\.|[^'\\\r\n])*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex SingleQuotedSensitiveFieldPattern();

    [GeneratedRegex(
        "(?<prefix>(?<![A-Za-z0-9_])(?:authorization)[\\\"']?\\s*[:=]\\s*[\\\"']?\\s*(?:bearer|lsopairing)\\s+)(?<secret>[^\\\"'\\s,;&}\\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex AuthorizationCredentialPattern();

    [GeneratedRegex(
        "(?<prefix>(?<![A-Za-z0-9_])bearer\\s+)(?<secret>[A-Za-z0-9._~+/\\-]+=*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex StandaloneBearerPattern();

    [GeneratedRegex(
        "(?<prefix>(?<![A-Za-z0-9_])lsopairing\\s+)(?<secret>[A-Za-z0-9._~+/\\-]+=*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex StandalonePairingPattern();

    [GeneratedRegex(
        "(?<prefix>(?<![A-Za-z0-9_])[\\\"']?(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|pairing[_-]?(?:claim[_-]?)?secret|authorization|[A-Za-z0-9_-]*credential[A-Za-z0-9_-]*|secret|content)[\\\"']?\\s*[:=]\\s*[\\\"']?)(?!\\[REDACTED\\])(?<secret>[^\\\"'\\s,;&}\\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex SensitiveFieldPattern();
}

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GachaOverlay.Infrastructure.Logging;

namespace GachaOverlay.Infrastructure.Diagnostics;

// JSON is a data structure, not a log line. Never run token regexes over its syntax.
internal static partial class DiagnosticContentSanitizer
{
    public static string Json(string content)
    {
        var root = JsonNode.Parse(content)
            ?? throw new System.Text.Json.JsonException("Diagnostic JSON must contain an object.");
        if (root is not JsonObject) throw new System.Text.Json.JsonException("Diagnostic JSON must contain an object.");
        Visit(root);
        return root.ToJsonString(new() { WriteIndented = true });
    }

    private static void Visit(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                var value = obj[key];
                var field = key.Replace("_", "", StringComparison.Ordinal)
                    .Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
                // These are explicit presence flags, never credentials.
                var safeFlag = field is "hasprotectedcredential" or "hascredential" &&
                    value is JsonValue flag && flag.TryGetValue<bool>(out _);
                // SalesFeatureHealthSnapshot.State is an enum serialized as a number.
                var numericState = field == "state" && value is JsonValue state && state.TryGetValue<int>(out _);
                if (!safeFlag && !numericState && IsPrivateField(field))
                    obj[key] = SensitiveDataRedactor.Replacement;
                else if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    obj[key] = Text(text);
                else if (value is not null) Visit(value);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) array[i] = Text(text);
                else if (array[i] is { } child) Visit(child);
            }
        }
    }

    private static bool IsPrivateField(string field) =>
        field.Contains("credential", StringComparison.Ordinal) ||
        field.Contains("token", StringComparison.Ordinal) ||
        field.Contains("secret", StringComparison.Ordinal) ||
        field is "authorization" or "code" or "state" or "codeverifier" or "content" or
            "body" or "rawpayload" or "rawhttppayload" or "rawdiscordpayload" or
            "messagecontent" or "chatcontent" or "salescontent" or
            "hostid" or "host1userid" or "host2userid" or "userid" or "guildid" or "channelid";

    public static string Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length > 4 * 1024 * 1024) return SensitiveDataRedactor.Replacement;
        try
        {
            // Body fields in line-oriented logs may contain spaces. Drop the complete remainder.
            var sanitized = BodyLine().Replace(text, "content=[REDACTED]");
            sanitized = SensitiveDataRedactor.Sanitize(sanitized);
            return DiscordIdentifier().Replace(sanitized, SensitiveDataRedactor.Replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            return SensitiveDataRedactor.Replacement;
        }
    }

    [GeneratedRegex(@"(?<![\w])(?:content|body|rawPayload|rawHttpPayload|rawDiscordPayload)\s*[:=][^\r\n]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex BodyLine();

    [GeneratedRegex(@"(?<!\d)\d{17,20}(?!\d)", RegexOptions.CultureInvariant, 250)]
    private static partial Regex DiscordIdentifier();
}

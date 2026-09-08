using System.Text.Json;
using GachaOverlay.Core.Gta.Localization;

namespace LSOverlay.Backend.Gta.Localization;

internal sealed record PreparedLocalization(PublicGtaLocalizationInput Input,
    PublicGtaLocalizationInput ProtectedInput, IReadOnlyList<ProtectedLocalizationText> Fields);

internal static class LocalizationValidation
{
    public const string Version = "fields-validation-2";
    public static PreparedLocalization Prepare(PublicGtaLocalizationInput input, GtaLocalizationGlossary glossary)
    {
        if (!input.IsComplete || input.Items.Count is 0 or > 64 || input.Items.Sum(i => i.Text.Length) > 12000 ||
            input.Items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != input.Items.Count)
            throw new InvalidDataException("InputSizeOrIdentity");
        var protector = new LocalizationProtection(glossary);
        var fields = input.Items.Select((i, n) => protector.Protect(i.Text, n)).ToArray();
        return new(input, input with
        {
            Items = input.Items.Select((i, n) => i with
            {
                Text = fields[n].Text,
                ProtectedTerms = fields[n].Tokens
            }).ToArray()
        }, fields);
    }

    public static bool TryValidate(PreparedLocalization prepared, string? json,
        out IReadOnlyDictionary<string, string> translations, out string reason)
        => TryValidate(prepared, json, out translations, out reason, out _);

    public static bool TryValidate(PreparedLocalization prepared, string? json,
        out IReadOnlyDictionary<string, string> translations, out string reason,
        out IReadOnlyList<PlaceholderFieldDiagnostic> diagnostics)
    {
        translations = new Dictionary<string, string>();
        reason = "Schema";
        diagnostics = [PlaceholderDiagnostics.Failure(-1, PlaceholderFailure.Structural)];
        if (!prepared.Input.IsComplete || string.IsNullOrWhiteSpace(json) || json.Length > 128 * 1024) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() != prepared.Fields.Count) return false;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var failures = new List<PlaceholderFieldDiagnostic>();
            var owners = prepared.Fields.SelectMany((field, index) => field.Tokens.Keys.Select(key => (key, index)))
                .ToDictionary(p => p.key, p => p.index, StringComparer.Ordinal);
            string? firstReason = null;
            for (var i = 0; i < items.GetArrayLength(); i++)
            {
                var row = items[i];
                if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 2 ||
                    !row.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    id.GetString() != prepared.Input.Items[i].Id || !row.TryGetProperty("textKo", out var text) ||
                    text.ValueKind != JsonValueKind.String)
                {
                    failures.Add(PlaceholderDiagnostics.Failure(i, PlaceholderFailure.Structural));
                    firstReason ??= "Schema";
                    continue;
                }
                var body = text.GetString()!;
                if (body.Length <= 4096 && PlaceholderDiagnostics.Inspect(i, prepared.Fields[i], body, owners) is { } failure)
                {
                    failures.Add(failure);
                    firstReason ??= "Placeholder";
                    continue;
                }
                if (!LocalizationProtection.TryRestore(prepared.Fields[i], body, out var restored, out var fieldReason))
                {
                    failures.Add(PlaceholderDiagnostics.Failure(i, PlaceholderFailure.ValueValidation) with
                    {
                        ValueFailure = Enum.TryParse<LocalizationValueFailure>(fieldReason, out var rule) ? rule : LocalizationValueFailure.Content
                    });
                    firstReason ??= fieldReason;
                    continue;
                }
                // Never truncate validated output in the existing wire field.
                var maximum = prepared.Input.Items[i].Type is "Bonus" or "Discount" or "FreeItem" or
                    "LoginReward" or "RotatingContent" or "Note" or "challenge" ? 512 : 256;
                if (restored.Length > maximum)
                {
                    failures.Add(PlaceholderDiagnostics.Failure(i, PlaceholderFailure.ValueValidation) with { ValueFailure = LocalizationValueFailure.OutputSize });
                    firstReason ??= "OutputSize";
                    continue;
                }
                result[prepared.Fields[i].Source] = restored;
            }
            diagnostics = failures.AsReadOnly();
            if (failures.Count > 0) { reason = firstReason!; return false; }
            translations = result;
            reason = "Valid";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { return false; }
    }
}

internal sealed class LocalizationOverrides
{
    internal sealed record Entry(string SourceHash, string Field, string ProtectedTextKo, string Reason);
    private sealed record Document(string Version, Entry[] Entries);
    public string Hash { get; }
    private readonly Entry[] _entries;
    public static LocalizationOverrides Default { get; } = Load();
    public LocalizationOverrides(string json)
    {
        if (json.Length > 256 * 1024) throw new InvalidDataException("OverrideSize");
        var doc = JsonSerializer.Deserialize<Document>(json, GeminiGtaLocalizationProvider.JsonOptions);
        if (doc is null || string.IsNullOrWhiteSpace(doc.Version) || doc.Entries is null || doc.Entries.Length > 256 ||
            doc.Entries.Any(e => e is null || e.SourceHash is null || e.SourceHash.Length != 64 ||
                string.IsNullOrWhiteSpace(e.Field) || string.IsNullOrWhiteSpace(e.ProtectedTextKo) || e.ProtectedTextKo.Length > 2048) ||
            doc.Entries.DistinctBy(e => (e.SourceHash, e.Field)).Count() != doc.Entries.Length)
            throw new InvalidDataException("OverrideSchema");
        _entries = doc.Entries;
        Hash = GtaLocalizationGlossary.Digest(JsonSerializer.Serialize(doc));
    }
    public IReadOnlyDictionary<string, string> Resolve(PreparedLocalization prepared)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < prepared.Fields.Count; i++)
        {
            var field = prepared.Input.Items[i];
            var entry = _entries.FirstOrDefault(e => e.SourceHash == GtaLocalizationGlossary.Digest(field.Text) && e.Field == field.Type);
            if (entry is not null && LocalizationProtection.TryRestore(prepared.Fields[i], entry.ProtectedTextKo, out var value, out _) &&
                value.Length <= (field.Type == "challenge" || char.IsUpper(field.Type[0]) ? 512 : 256)) result[field.Text] = value;
        }
        return result;
    }
    private static LocalizationOverrides Load()
    {
        using var stream = typeof(LocalizationOverrides).Assembly.GetManifestResourceStream("LSOverlay.GtaOverrides.json")!;
        using var reader = new StreamReader(stream);
        return new(reader.ReadToEnd());
    }
}

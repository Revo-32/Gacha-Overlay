using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta.Localization;

public enum PlaceholderFailure { Missing, Duplicate, Unknown, Mutated, CrossField, CountMismatch, Structural, ValueValidation }
public enum LocalizationValueFailure { Content, NumericOrPlaceholder, UnprotectedToken, RepeatedTerm, QuantityUnit, KoreanSurface, OutputSize }

// Structural data only. Never retain source/output text, external IDs, or malformed token bodies.
public sealed record PlaceholderFieldDiagnostic(string FieldId, IReadOnlyList<PlaceholderFailure> Failures,
    int ExpectedCount, int ObservedCount, IReadOnlyList<string> ExpectedTokenIds, IReadOnlyList<string> ObservedTokenIds,
    IReadOnlyList<string> MissingTokenIds, IReadOnlyList<string> DuplicateTokenIds,
    IReadOnlyList<string> UnknownTokenIds, IReadOnlyList<string> MovedTokenIds, bool TokenListsTruncated)
{
    public LocalizationValueFailure? ValueFailure { get; init; }
}

public static class PlaceholderDiagnostics
{
    public const int MaximumReportedTokens = 16;
    private static readonly Regex Token = new(@"\[\[L\d{3}_\d{4}\]\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static PlaceholderFieldDiagnostic Failure(int field, PlaceholderFailure failure) =>
        new(field < 0 ? "response" : $"field.{field:D3}", [failure], 0, 0, [], [], [], [], [], [], false);

    public static PlaceholderFieldDiagnostic? Inspect(int field, ProtectedLocalizationText input, string output,
        IReadOnlyDictionary<string, int> owners)
    {
        var expected = input.Tokens.Keys.ToArray();
        var observed = Token.Matches(output).Select(m => m.Value).ToArray();
        var missing = expected.Except(observed, StringComparer.Ordinal).ToArray();
        var duplicate = observed.GroupBy(t => t, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        var unknown = observed.Where(t => !owners.ContainsKey(t)).Distinct(StringComparer.Ordinal).ToArray();
        var moved = observed.Where(t => owners.TryGetValue(t, out var owner) && owner != field).Distinct(StringComparer.Ordinal).ToArray();
        // Malformed syntax stays out of diagnostics. Bracket residue is reported only as a category.
        var residue = Token.Replace(output, "");
        var failures = new List<PlaceholderFailure>();
        if (missing.Length > 0) failures.Add(PlaceholderFailure.Missing);
        if (duplicate.Length > 0) failures.Add(PlaceholderFailure.Duplicate);
        if (unknown.Length > 0) failures.Add(PlaceholderFailure.Unknown);
        if (moved.Length > 0) failures.Add(PlaceholderFailure.CrossField);
        if (residue.Contains('[') || residue.Contains(']')) failures.Add(PlaceholderFailure.Mutated);
        if (missing.Length > 0 || duplicate.Length > 0 || unknown.Length > 0 || moved.Length > 0 || observed.Length != expected.Length)
            failures.Add(PlaceholderFailure.CountMismatch);
        if (failures.Count == 0) return null;
        string[] Bound(IEnumerable<string> tokens) => tokens.Take(MaximumReportedTokens).ToArray();
        return new($"field.{field:D3}", failures, expected.Length, observed.Length,
            Bound(expected), Bound(observed), Bound(missing), Bound(duplicate), Bound(unknown), Bound(moved),
            new[] { expected.Length, observed.Length, missing.Length, duplicate.Length, unknown.Length, moved.Length }.Any(n => n > MaximumReportedTokens));
    }
}

namespace GachaOverlay.Core.Localization;

public static class SupportedLocales
{
    public const string English = "en";
    public const string Korean = "ko";
    public const string Japanese = "ja";

    public static IReadOnlyList<string> All { get; } =
        new[] { English, Korean, Japanese };

    public static bool IsSupported(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return false;
        }

        var language = GetLanguagePart(locale);
        return All.Contains(language, StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizeOrEnglish(string? locale)
    {
        if (!IsSupported(locale))
        {
            return English;
        }

        return GetLanguagePart(locale!).ToLowerInvariant();
    }

    private static string GetLanguagePart(string locale)
    {
        var normalized = locale.Trim().Replace('_', '-');
        var separatorIndex = normalized.IndexOf('-');
        return separatorIndex < 0 ? normalized : normalized[..separatorIndex];
    }
}

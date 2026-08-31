using System.ComponentModel;
using System.Globalization;
using System.Resources;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.Infrastructure.Localization;

public sealed class ResourceLocalizationService : ILocalizationService
{
    private const string ResourceBaseName =
        "GachaOverlay.Infrastructure.Localization.Resources.Strings";

    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en");

    private readonly IAppLogger _logger;
    private readonly ResourceManager _resourceManager;
    private CultureInfo _currentCulture = EnglishCulture;

    public ResourceLocalizationService(
        string? initialLocale = null,
        IAppLogger? logger = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
        _resourceManager = new ResourceManager(
            ResourceBaseName,
            typeof(ResourceLocalizationService).Assembly);

        SetLanguage(initialLocale ?? SupportedLocales.English);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string CurrentLocale { get; private set; } = SupportedLocales.English;

    public string this[string key] => GetString(key);

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var localized = _resourceManager.GetString(key, _currentCulture);
            if (!string.IsNullOrEmpty(localized))
            {
                return localized;
            }

            var english = _resourceManager.GetString(key, EnglishCulture);
            return string.IsNullOrEmpty(english) ? key : english;
        }
        catch (MissingManifestResourceException exception)
        {
            _logger.Error("LOCALIZATION", "Localization resources are unavailable.", exception);
            return key;
        }
    }

    public void SetLanguage(string? locale)
    {
        var isSupported = SupportedLocales.IsSupported(locale);
        var normalized = SupportedLocales.NormalizeOrEnglish(locale);

        if (!isSupported)
        {
            _logger.Warning(
                "LOCALIZATION",
                $"Unsupported locale '{locale ?? "<null>"}' was replaced with English.");
        }

        if (string.Equals(CurrentLocale, normalized, StringComparison.Ordinal))
        {
            return;
        }

        CurrentLocale = normalized;
        _currentCulture = CultureInfo.GetCultureInfo(normalized);
        _logger.Information("LOCALIZATION", $"Language = {normalized}");

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLocale)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}

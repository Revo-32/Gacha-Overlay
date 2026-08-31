using System.ComponentModel;

namespace GachaOverlay.Core.Localization;

public interface ILocalizationService : INotifyPropertyChanged
{
    event EventHandler? LanguageChanged;

    string CurrentLocale { get; }

    string this[string key] { get; }

    string GetString(string key);

    void SetLanguage(string? locale);
}

namespace GachaOverlay.Core.Settings;

public interface ISettingsStore
{
    AppSettings Current { get; }

    AppSettings Load();

    bool Save(AppSettings settings);

    bool Update(Func<AppSettings, AppSettings> update);
}

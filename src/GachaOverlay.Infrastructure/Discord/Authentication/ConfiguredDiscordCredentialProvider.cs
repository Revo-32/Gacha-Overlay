using GachaOverlay.Core.Settings;

namespace GachaOverlay.Infrastructure.Discord.Authentication;

public sealed class ConfiguredDiscordCredentialProvider : IDiscordCredentialProvider
{
    private readonly ISettingsStore _settingsStore;
    private readonly IDiscordProtectedCredentialStore _protectedStore;
    private readonly IDiscordCredentialProvider _environmentProvider;

    public ConfiguredDiscordCredentialProvider(
        ISettingsStore settingsStore,
        IDiscordProtectedCredentialStore protectedStore,
        IDiscordCredentialProvider? environmentProvider = null)
    {
        _settingsStore = settingsStore;
        _protectedStore = protectedStore;
        _environmentProvider = environmentProvider ?? new EnvironmentDiscordCredentialProvider();
    }

    public bool TryGetCredentials(out DiscordCredentials? credentials)
    {
        if (_environmentProvider.TryGetCredentials(out credentials) && credentials is not null)
        {
            return true;
        }

        var settings = _settingsStore.Current;
        if (string.IsNullOrWhiteSpace(settings.DiscordClientId) ||
            string.IsNullOrWhiteSpace(settings.DiscordRedirectUri) ||
            !_protectedStore.TryLoadClientSecret(out var clientSecret) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            credentials = null;
            return false;
        }

        credentials = new DiscordCredentials(
            settings.DiscordClientId,
            clientSecret,
            settings.DiscordRedirectUri);
        return true;
    }
}

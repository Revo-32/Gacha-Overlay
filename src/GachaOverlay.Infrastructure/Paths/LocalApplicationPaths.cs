using GachaOverlay.Core.Product;

namespace GachaOverlay.Infrastructure.Paths;

public sealed class LocalApplicationPaths
{
    public LocalApplicationPaths(string? localApplicationData = null)
    {
        var localRoot = localApplicationData;
        if (string.IsNullOrWhiteSpace(localRoot))
        {
            localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (string.IsNullOrWhiteSpace(localRoot))
        {
            throw new InvalidOperationException("A local application data directory is not available.");
        }

        DataDirectory = Path.Combine(localRoot, ProductIdentity.LocalDataDirectoryName);
        SettingsFilePath = Path.Combine(DataDirectory, "settings.json");
        LegacyDiscordClientSecretFilePath = Path.Combine(DataDirectory, "discord-client-secret.dat");
        LegacyDiscordOAuthTokenFilePath = Path.Combine(DataDirectory, "discord-oauth-token.dat");
        RemoteAccessTokenFilePath = Path.Combine(DataDirectory, "remote-access-token.dat");
        RemoteInstallationIdFilePath = Path.Combine(DataDirectory, "remote-installation-id.txt");
        GuildDisplayNameCacheFilePath = Path.Combine(DataDirectory, "guild-display-names.json");
        SalesProductCatalogFilePath = Path.Combine(DataDirectory, "sales-products.json");
        SalesProductOverrideFilePath = Path.Combine(
            DataDirectory,
            "sales-products.override.json");
        LogDirectory = Path.Combine(DataDirectory, "Logs");
        CrashSummaryFilePath = Path.Combine(DataDirectory, "crash-summary.json");
        NotificationToneDirectory = Path.Combine(DataDirectory, "NotificationTones");
    }

    public string DataDirectory { get; }

    public string SettingsFilePath { get; }

    public string LegacyDiscordClientSecretFilePath { get; }

    public string LegacyDiscordOAuthTokenFilePath { get; }

    public string RemoteAccessTokenFilePath { get; }

    public string RemoteInstallationIdFilePath { get; }

    public string GuildDisplayNameCacheFilePath { get; }

    public string SalesProductCatalogFilePath { get; }

    public string SalesProductOverrideFilePath { get; }

    public string LogDirectory { get; }

    public string CrashSummaryFilePath { get; }

    public string NotificationToneDirectory { get; }
}

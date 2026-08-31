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

        DataDirectory = Path.Combine(localRoot, "GachaOverlay");
        SettingsFilePath = Path.Combine(DataDirectory, "settings.json");
        DiscordClientSecretFilePath = Path.Combine(DataDirectory, "discord-client-secret.dat");
        DiscordOAuthTokenFilePath = Path.Combine(DataDirectory, "discord-oauth-token.dat");
        GuildDisplayNameCacheFilePath = Path.Combine(DataDirectory, "guild-display-names.json");
        SalesProductCatalogFilePath = Path.Combine(DataDirectory, "sales-products.json");
        SalesProductOverrideFilePath = Path.Combine(
            DataDirectory,
            "sales-products.override.json");
        LogDirectory = Path.Combine(DataDirectory, "Logs");
        CrashSummaryFilePath = Path.Combine(DataDirectory, "crash-summary.json");
    }

    public string DataDirectory { get; }

    public string SettingsFilePath { get; }

    public string DiscordClientSecretFilePath { get; }

    public string DiscordOAuthTokenFilePath { get; }

    public string GuildDisplayNameCacheFilePath { get; }

    public string SalesProductCatalogFilePath { get; }

    public string SalesProductOverrideFilePath { get; }

    public string LogDirectory { get; }

    public string CrashSummaryFilePath { get; }
}

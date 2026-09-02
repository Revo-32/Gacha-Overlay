namespace LSOverlay.Backend.Configuration;

internal sealed class DiscordWebAuthOptions
{
    public const string CallbackPath = "/auth/discord/callback";
    private readonly string _secret;

    private DiscordWebAuthOptions(string clientId, string secret, Uri origin)
    {
        ClientId = clientId;
        _secret = secret;
        RedirectUri = new Uri(origin, CallbackPath);
    }

    public string ClientId { get; }
    public Uri RedirectUri { get; }
    internal string RevealForTokenExchange() => _secret;
    public override string ToString() => "Discord Web Auth configured [REDACTED]";

    public static DiscordWebAuthOptions? Resolve(Func<string, string?> provider)
    {
        string? Read(string key)
        {
            try { return provider(key); }
            catch (KeyNotFoundException) { return null; }
        }

        var enabled = Read("LSO_DISCORD_WEB_AUTH_ENABLED");
        if (string.IsNullOrWhiteSpace(enabled) || string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            throw new BackendDeploymentException("LSO_DISCORD_WEB_AUTH_ENABLED must be true or false.");

        var clientId = Read("LSO_DISCORD_OAUTH_CLIENT_ID");
        var secret = Read("LSO_DISCORD_OAUTH_CLIENT_SECRET");
        if (clientId is null || !ulong.TryParse(clientId, out var id) || id == 0 ||
            clientId.Any(character => !char.IsAsciiDigit(character)))
            throw new BackendDeploymentException("LSO_DISCORD_OAUTH_CLIENT_ID is required and must be valid.");
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 256 || secret.Any(char.IsWhiteSpace))
            throw new BackendDeploymentException("LSO_DISCORD_OAUTH_CLIENT_SECRET is required and must be valid.");

        var value = Read("LSO_PUBLIC_BASE_URL");
        var development = string.Equals(Read("DOTNET_ENVIRONMENT") ?? Read("ASPNETCORE_ENVIRONMENT"),
            "Development", StringComparison.OrdinalIgnoreCase);
        var railway = !string.IsNullOrEmpty(Read("RAILWAY_SERVICE_ID")) ||
            !string.IsNullOrEmpty(Read("RAILWAY_PROJECT_ID")) || !string.IsNullOrEmpty(Read("RAILWAY_ENVIRONMENT_ID")) ||
            !string.IsNullOrEmpty(Read("RAILWAY_VOLUME_MOUNT_PATH"));
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            origin.AbsolutePath != "/" || value!.Contains('\\') ||
            (value != origin.GetLeftPart(UriPartial.Authority) && value != origin.GetLeftPart(UriPartial.Authority) + "/") ||
            (origin.Scheme != "https" && !(development && !railway && origin.Scheme == "http" && origin.IsLoopback)))
            throw new BackendDeploymentException("LSO_PUBLIC_BASE_URL must be a canonical HTTPS origin (Development loopback excepted).");
        return new DiscordWebAuthOptions(clientId, secret!, origin);
    }
}

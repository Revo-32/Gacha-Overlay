namespace GachaOverlay.Infrastructure.Discord.Authentication;

public sealed class EnvironmentDiscordCredentialProvider : IDiscordCredentialProvider
{
    public const string ClientIdVariable = "DISCORD_CLIENT_ID";
    public const string ClientSecretVariable = "DISCORD_CLIENT_SECRET";
    public const string RedirectUriVariable = "DISCORD_REDIRECT_URI";

    public bool TryGetCredentials(out DiscordCredentials? credentials)
    {
        var clientId = Environment.GetEnvironmentVariable(ClientIdVariable);
        var clientSecret = Environment.GetEnvironmentVariable(ClientSecretVariable);
        var redirectUri = Environment.GetEnvironmentVariable(RedirectUriVariable)
            ?? "https://127.0.0.1";

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(redirectUri))
        {
            credentials = null;
            return false;
        }

        credentials = new DiscordCredentials(
            clientId.Trim(),
            clientSecret,
            redirectUri.Trim());
        return true;
    }
}

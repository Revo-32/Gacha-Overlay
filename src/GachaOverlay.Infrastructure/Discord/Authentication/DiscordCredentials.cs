namespace GachaOverlay.Infrastructure.Discord.Authentication;

public sealed record DiscordCredentials(
    string ClientId,
    string ClientSecret,
    string RedirectUri);

public interface IDiscordCredentialProvider
{
    bool TryGetCredentials(out DiscordCredentials? credentials);
}

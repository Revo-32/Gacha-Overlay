namespace GachaOverlay.Infrastructure.Discord.Authentication;

public enum ProtectedCredentialStatus
{
    Missing,
    Available,
    Unreadable,
}

public sealed record DiscordOAuthToken(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt);

public interface IDiscordProtectedCredentialStore
{
    ProtectedCredentialStatus ClientSecretStatus { get; }

    ProtectedCredentialStatus OAuthTokenStatus { get; }

    bool TryLoadClientSecret(out string? clientSecret);

    bool SaveClientSecret(string clientSecret);

    bool TryLoadOAuthToken(out DiscordOAuthToken? token);

    bool SaveOAuthToken(DiscordOAuthToken token);

    void ClearOAuthToken();
}

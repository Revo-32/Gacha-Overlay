using GachaOverlay.Infrastructure.Discord.Authentication;

namespace GachaOverlay.App.Presentation;

internal sealed record DiscordConnectionSetupRequest(
    string ClientId,
    string ClientSecret,
    string RedirectUri,
    string GuildId,
    string MainChannelId,
    string SalesChannelId);

internal sealed record DiscordConnectionSetupSnapshot(
    bool ClientIdConfigured,
    ProtectedCredentialStatus ClientSecretStatus,
    ProtectedCredentialStatus OAuthTokenStatus,
    bool GuildConfigured,
    bool MainChannelConfigured,
    bool SalesChannelConfigured)
{
    public bool CanConnect =>
        ClientIdConfigured && ClientSecretStatus == ProtectedCredentialStatus.Available;
}

internal sealed record DiscordConnectionSetupResult(bool Success, string MessageKey)
{
    public static DiscordConnectionSetupResult Succeeded { get; } =
        new(true, "SettingsDiscordSavedAndConnecting");
}

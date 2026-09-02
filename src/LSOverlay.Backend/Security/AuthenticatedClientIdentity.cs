namespace LSOverlay.Backend.Security;

internal sealed record AuthenticatedClientIdentity(
    Guid ClientInstallationId,
    ulong DiscordUserId,
    ulong GuildId);

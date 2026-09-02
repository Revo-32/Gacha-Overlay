using LSOverlay.Protocol;

namespace LSOverlay.RemoteClient;

public interface ILSOverlayDiscordWebAuthClient
{
    Task<DiscordWebAuthStartResponse?> StartDiscordWebAuthAsync(Guid installationId, CancellationToken cancellationToken = default);
    Task<DiscordWebAuthClaimResult> GetDiscordWebAuthStatusAsync(Guid sessionId, string claimSecret, CancellationToken cancellationToken = default);
    Task CancelDiscordWebAuthAsync(Guid sessionId, string claimSecret, CancellationToken cancellationToken = default);
}

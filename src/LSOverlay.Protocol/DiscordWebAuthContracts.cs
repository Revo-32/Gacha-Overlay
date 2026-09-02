using System.Text.Json.Serialization;

namespace LSOverlay.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DiscordWebAuthStartRequest(int ProtocolVersion, Guid ClientInstallationId);

public sealed record DiscordWebAuthStartResponse(
    int ProtocolVersion, Guid SessionId, string ClaimSecret, string AuthorizationUrl, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "Discord web authentication session [REDACTED]";
}

public enum DiscordWebAuthStatus { Pending, Approved, Denied, Expired, Claimed }

public enum DiscordWebAuthFailure
{
    None, Cancelled, InvalidRequest, SessionExpired, NotMember, VerificationUnavailable, TemporaryFailure,
}

public sealed record DiscordWebAuthClaimResult(
    int ProtocolVersion, DiscordWebAuthStatus Status, DiscordWebAuthFailure Failure = DiscordWebAuthFailure.None,
    string? AccessToken = null, DateTimeOffset? CredentialExpiresAt = null)
{
    public override string ToString() => $"Discord web authentication: {Status}/{Failure} [REDACTED]";
}

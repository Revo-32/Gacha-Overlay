using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LSOverlay.Protocol;

namespace LSOverlay.RemoteClient;

public sealed partial class LSOverlayRemoteClient
{
    public async Task<DiscordWebAuthStartResponse?> StartDiscordWebAuthAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var content = JsonContent.Create(new DiscordWebAuthStartRequest(OverlayTransportProtocol.Version, installationId), options: OverlayProtocolJson.Options);
        using var response = await _http.PostAsync(Endpoint("api/v1/auth/discord/sessions"), content, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var session = await DeserializeAsync<DiscordWebAuthStartResponse>(response, timeout.Token).ConfigureAwait(false);
        OverlayProtocolJson.EnsureVersion(session.ProtocolVersion);
        ValidateWebAuthSession(session, _baseUri);
        return session;
    }

    public async Task<DiscordWebAuthClaimResult> GetDiscordWebAuthStatusAsync(Guid sessionId, string claimSecret, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = WebAuthRequest(HttpMethod.Get, sessionId, claimSecret);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new(OverlayTransportProtocol.Version, DiscordWebAuthStatus.Expired, DiscordWebAuthFailure.SessionExpired);
        response.EnsureSuccessStatusCode();
        var result = await DeserializeAsync<DiscordWebAuthClaimResult>(response, timeout.Token).ConfigureAwait(false);
        OverlayProtocolJson.EnsureVersion(result.ProtocolVersion);
        return result;
    }

    public async Task CancelDiscordWebAuthAsync(Guid sessionId, string claimSecret, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var request = WebAuthRequest(HttpMethod.Delete, sessionId, claimSecret);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
    }

    private HttpRequestMessage WebAuthRequest(HttpMethod method, Guid session, string secret)
    {
        var request = new HttpRequestMessage(method, Endpoint($"api/v1/auth/discord/sessions/{session:D}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("LSOAuthClaim", secret);
        return request;
    }

    internal static void ValidateWebAuthSession(DiscordWebAuthStartResponse session, Uri backend)
    {
        if (session.SessionId == Guid.Empty || session.ClaimSecret.Length != 43 ||
            session.ExpiresAt <= DateTimeOffset.UtcNow || session.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(6) ||
            session.AuthorizationUrl.Length > 4096 ||
            !Uri.TryCreate(session.AuthorizationUrl, UriKind.Absolute, out var url) ||
            url.Scheme != "https" || url.Host != "discord.com" || !url.IsDefaultPort ||
            url.UserInfo.Length != 0 || url.Fragment.Length != 0 || url.AbsolutePath != "/oauth2/authorize")
            throw new InvalidDataException("Invalid browser authentication response.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in url.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !fields.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1])))
                throw new InvalidDataException("Invalid browser authentication response.");
        }
        var callback = new Uri(backend, "/auth/discord/callback").AbsoluteUri;
        if (fields.Count != 7 || fields.GetValueOrDefault("scope") != "identify" ||
            fields.GetValueOrDefault("response_type") != "code" || fields.GetValueOrDefault("redirect_uri") != callback ||
            !ulong.TryParse(fields.GetValueOrDefault("client_id"), out var clientId) || clientId == 0 ||
            fields.GetValueOrDefault("state") is not { Length: 43 } state || state == session.ClaimSecret ||
            fields.GetValueOrDefault("code_challenge_method") != "S256" || fields.GetValueOrDefault("code_challenge")?.Length != 43)
            throw new InvalidDataException("Invalid browser authentication response.");
    }
}

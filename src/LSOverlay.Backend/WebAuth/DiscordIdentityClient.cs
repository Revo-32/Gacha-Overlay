using System.Net.Http.Headers;
using System.Text.Json;
using LSOverlay.Backend.Configuration;

namespace LSOverlay.Backend.WebAuth;

internal interface IDiscordIdentityClient
{
    Task<ulong> IdentifyAsync(string code, string verifier, CancellationToken cancellationToken);
}

// One shared, owned HttpClient. No HttpClientFactory request logging or redirects.
internal sealed class DiscordIdentityClient : IDiscordIdentityClient, IDisposable
{
    private readonly DiscordWebAuthOptions _options;
    private readonly HttpClient _http;
    public DiscordIdentityClient(DiscordWebAuthOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 16 * 1024 };
    }

    public async Task<ulong> IdentifyAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://discord.com/api/oauth2/token");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri.AbsoluteUri,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.RevealForTokenExchange(),
            ["code_verifier"] = verifier,
        });
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = token.RootElement;
        if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(access.GetString()) ||
            !root.TryGetProperty("token_type", out var type) || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("scope", out var scope) || scope.GetString() != "identify")
            throw new InvalidDataException("Discord identity response was invalid.");

        using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
        userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.GetString());
        using var userResponse = await _http.SendAsync(userRequest, cancellationToken).ConfigureAwait(false);
        userResponse.EnsureSuccessStatusCode();
        using var user = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!user.RootElement.TryGetProperty("id", out var userId) || userId.ValueKind != JsonValueKind.String ||
            !ulong.TryParse(userId.GetString(), out var id) || id == 0 ||
            (user.RootElement.TryGetProperty("bot", out var bot) && bot.GetBoolean()) ||
            (user.RootElement.TryGetProperty("system", out var system) && system.GetBoolean()))
            throw new InvalidDataException("Discord identity response was invalid.");
        // No profile, access token or refresh token escapes this method or is persisted.
        return id;
    }

    public void Dispose() => _http.Dispose();
}

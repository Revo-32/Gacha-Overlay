using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Infrastructure.Discord.Authentication;

public sealed class DiscordAuthenticationService : IDiscordAuthenticationService, IDisposable
{
    private static readonly string[] RequiredScopes = { "rpc", "identify", "messages.read" };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IDiscordProtectedCredentialStore _protectedStore;
    private readonly SemaphoreSlim _authenticationLock = new(1, 1);
    private DiscordOAuthToken? _token;
    private bool _protectedTokenLoaded;
    private int _disposed;

    public DiscordAuthenticationService(
        HttpClient? httpClient = null,
        IDiscordProtectedCredentialStore? protectedStore = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _protectedStore = protectedStore ?? NullDiscordProtectedCredentialStore.Instance;
    }

    public void ClearSavedAuthentication()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ClearToken();
        _protectedTokenLoaded = true;
    }

    public async Task<DiscordAuthenticationResult> AuthenticateAsync(
        IDiscordRpcClient rpcClient,
        DiscordCredentials credentials,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _authenticationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LoadProtectedTokenOnce();

            if (_token is not null && !IsExpired(_token))
            {
                try
                {
                    return await AuthenticateWithTokenAsync(
                            rpcClient,
                            _token.AccessToken,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (DiscordRpcException)
                {
                    // The stored token is no longer accepted. Refresh or reauthorize below.
                }
            }

            if (!string.IsNullOrWhiteSpace(_token?.RefreshToken))
            {
                var refreshed = await TryRefreshTokenAsync(
                        credentials,
                        _token.RefreshToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (refreshed is not null)
                {
                    SetToken(refreshed);
                    try
                    {
                        return await AuthenticateWithTokenAsync(
                                rpcClient,
                                refreshed.AccessToken,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (DiscordRpcException)
                    {
                        // One explicit authorization is attempted below.
                    }
                }
            }

            ClearToken();
            JsonElement authorize;
            try
            {
                authorize = await rpcClient.CommandAsync(
                        "AUTHORIZE",
                        new
                        {
                            client_id = credentials.ClientId,
                            scopes = RequiredScopes,
                        },
                        timeout: TimeSpan.FromMinutes(2),
                        cancellationToken)
                    .ConfigureAwait(false);
                DiscordRpcProtocol.EnsureSuccess(authorize);
            }
            catch (DiscordRpcException exception)
            {
                throw new DiscordAuthenticationRequiredException(
                    "Discord authorization was not completed.",
                    exception);
            }

            var authorizationCode = TryGetNestedString(authorize, "data", "code");
            if (string.IsNullOrWhiteSpace(authorizationCode))
            {
                throw new DiscordAuthenticationRequiredException(
                    "Discord AUTHORIZE returned no authorization code.");
            }

            var token = await ExchangeAuthorizationCodeAsync(
                    credentials,
                    authorizationCode,
                    cancellationToken)
                .ConfigureAwait(false);
            SetToken(token);

            try
            {
                return await AuthenticateWithTokenAsync(
                        rpcClient,
                        token.AccessToken,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DiscordRpcException exception)
            {
                ClearToken();
                throw new DiscordAuthenticationRequiredException(
                    "Discord rejected the newly issued OAuth token.",
                    exception);
            }
        }
        finally
        {
            _authenticationLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _token = null;
        _authenticationLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private void LoadProtectedTokenOnce()
    {
        if (_protectedTokenLoaded)
        {
            return;
        }

        _protectedTokenLoaded = true;
        if (_protectedStore.TryLoadOAuthToken(out var token) &&
            token is not null &&
            !string.IsNullOrWhiteSpace(token.AccessToken))
        {
            _token = token;
        }
    }

    private void SetToken(DiscordOAuthToken token)
    {
        _token = token;
        _protectedStore.SaveOAuthToken(token);
    }

    private void ClearToken()
    {
        _token = null;
        _protectedStore.ClearOAuthToken();
    }

    private static bool IsExpired(DiscordOAuthToken token) =>
        token.ExpiresAt is not null && token.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30);

    private static async Task<DiscordAuthenticationResult> AuthenticateWithTokenAsync(
        IDiscordRpcClient rpcClient,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var response = await rpcClient.CommandAsync(
                "AUTHENTICATE",
                new { access_token = accessToken },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        DiscordRpcProtocol.EnsureSuccess(response);

        if (!response.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("user", out var user))
        {
            throw new InvalidDataException("Discord AUTHENTICATE returned no user identity.");
        }

        var userId = DiscordJson.GetString(user, "id");
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidDataException("Discord AUTHENTICATE returned no user ID.");
        }

        var username = DiscordJson.GetString(user, "global_name")
            ?? DiscordJson.GetString(user, "username")
            ?? string.Empty;
        return new DiscordAuthenticationResult(userId, username);
    }

    private Task<DiscordOAuthToken> ExchangeAuthorizationCodeAsync(
        DiscordCredentials credentials,
        string authorizationCode,
        CancellationToken cancellationToken) =>
        ExchangeTokenAsync(
            credentials,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = authorizationCode,
                ["redirect_uri"] = credentials.RedirectUri,
            },
            cancellationToken);

    private async Task<DiscordOAuthToken?> TryRefreshTokenAsync(
        DiscordCredentials credentials,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExchangeTokenAsync(
                    credentials,
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refreshToken,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DiscordAuthenticationRequiredException)
        {
            return null;
        }
    }

    private async Task<DiscordOAuthToken> ExchangeTokenAsync(
        DiscordCredentials credentials,
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken cancellationToken)
    {
        var basicCredential = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{credentials.ClientId}:{credentials.ClientSecret}"));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://discord.com/api/v10/oauth2/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredential);
        request.Content = new FormUrlEncodedContent(formValues);

        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DiscordAuthenticationRequiredException(
                $"Discord OAuth token exchange failed with HTTP {(int)response.StatusCode}.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var accessToken = DiscordJson.GetString(document.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new DiscordAuthenticationRequiredException(
                "Discord OAuth token response contained no access token.");
        }

        var refreshToken = DiscordJson.GetString(document.RootElement, "refresh_token");
        var expiresIn = DiscordJson.GetInt32(document.RootElement, "expires_in");
        DateTimeOffset? expiresAt = expiresIn is > 0
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value)
            : null;
        return new DiscordOAuthToken(accessToken, refreshToken, expiresAt);
    }

    private static string? TryGetNestedString(
        JsonElement root,
        string objectProperty,
        string valueProperty) =>
        root.TryGetProperty(objectProperty, out var nested)
            ? DiscordJson.GetString(nested, valueProperty)
            : null;

    private sealed class NullDiscordProtectedCredentialStore : IDiscordProtectedCredentialStore
    {
        public static NullDiscordProtectedCredentialStore Instance { get; } = new();

        public ProtectedCredentialStatus ClientSecretStatus => ProtectedCredentialStatus.Missing;

        public ProtectedCredentialStatus OAuthTokenStatus => ProtectedCredentialStatus.Missing;

        public bool TryLoadClientSecret(out string? clientSecret)
        {
            clientSecret = null;
            return false;
        }

        public bool SaveClientSecret(string clientSecret) => true;

        public bool TryLoadOAuthToken(out DiscordOAuthToken? token)
        {
            token = null;
            return false;
        }

        public bool SaveOAuthToken(DiscordOAuthToken token) => true;

        public void ClearOAuthToken()
        {
        }
    }
}

public sealed class DiscordAuthenticationRequiredException : Exception
{
    public DiscordAuthenticationRequiredException(string message)
        : base(message)
    {
    }

    public DiscordAuthenticationRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

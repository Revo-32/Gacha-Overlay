using System.Net;
using System.Text;
using System.Text.Json;
using GachaOverlay.Infrastructure.Discord.Authentication;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Tests.Discord.Connection;

public sealed class DiscordAuthenticationServiceTests
{
    [Fact]
    public async Task Authenticate_UsesProvenFlowAndReusesInMemoryTokenOnReconnect()
    {
        var rpcClient = new AuthenticationRpcClient();
        var httpHandler = new TokenExchangeHandler();
        using var httpClient = new HttpClient(httpHandler);
        using var authentication = new DiscordAuthenticationService(httpClient);
        var credentials = new DiscordCredentials(
            "client-id",
            "client-secret",
            "https://127.0.0.1");

        var first = await authentication.AuthenticateAsync(
            rpcClient,
            credentials,
            CancellationToken.None);
        var second = await authentication.AuthenticateAsync(
            rpcClient,
            credentials,
            CancellationToken.None);

        Assert.Equal("user-id", first.UserId);
        Assert.Equal(first, second);
        Assert.Equal(1, rpcClient.AuthorizeCount);
        Assert.Equal(2, rpcClient.AuthenticateCount);
        Assert.Equal(1, httpHandler.RequestCount);
    }

    [Fact]
    public async Task ReturningLaunch_ReusesProtectedAccessTokenWithoutNewAuthorization()
    {
        var protectedStore = new FakeProtectedCredentialStore();
        var httpHandler = new TokenExchangeHandler();
        using var httpClient = new HttpClient(httpHandler);
        var credentials = new DiscordCredentials(
            "client-id",
            "client-secret",
            "https://127.0.0.1");
        var firstRpc = new AuthenticationRpcClient();
        using (var first = new DiscordAuthenticationService(httpClient, protectedStore))
        {
            await first.AuthenticateAsync(firstRpc, credentials, CancellationToken.None);
        }

        var returningRpc = new AuthenticationRpcClient();
        using (var returning = new DiscordAuthenticationService(httpClient, protectedStore))
        {
            await returning.AuthenticateAsync(
                returningRpc,
                credentials,
                CancellationToken.None);
        }

        Assert.Equal(1, firstRpc.AuthorizeCount);
        Assert.Equal(0, returningRpc.AuthorizeCount);
        Assert.Equal(1, returningRpc.AuthenticateCount);
        Assert.Equal(1, httpHandler.RequestCount);
        Assert.Equal(ProtectedCredentialStatus.Available, protectedStore.OAuthTokenStatus);
    }

    [Fact]
    public async Task ExpiredProtectedToken_UsesRefreshTokenBeforeAuthorization()
    {
        var protectedStore = new FakeProtectedCredentialStore
        {
            Token = new DiscordOAuthToken(
                "expired-access",
                "stored-refresh",
                DateTimeOffset.UtcNow.AddMinutes(-1)),
        };
        var rpc = new AuthenticationRpcClient();
        var httpHandler = new TokenExchangeHandler();
        using var httpClient = new HttpClient(httpHandler);
        using var authentication = new DiscordAuthenticationService(httpClient, protectedStore);

        await authentication.AuthenticateAsync(
            rpc,
            new DiscordCredentials("client-id", "client-secret", "https://127.0.0.1"),
            CancellationToken.None);

        Assert.Equal(0, rpc.AuthorizeCount);
        Assert.Equal(1, rpc.AuthenticateCount);
        Assert.Single(httpHandler.RequestBodies);
        Assert.Contains("grant_type=refresh_token", httpHandler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("refresh_token=stored-refresh", httpHandler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableProtectedToken_FallsBackToOneSafeAuthorization()
    {
        var protectedStore = new FakeProtectedCredentialStore
        {
            Token = new DiscordOAuthToken("unreadable", null, null),
            FailTokenRead = true,
        };
        var rpc = new AuthenticationRpcClient();
        using var httpClient = new HttpClient(new TokenExchangeHandler());
        using var authentication = new DiscordAuthenticationService(httpClient, protectedStore);

        var result = await authentication.AuthenticateAsync(
            rpc,
            new DiscordCredentials("client-id", "client-secret", "https://127.0.0.1"),
            CancellationToken.None);

        Assert.Equal("user-id", result.UserId);
        Assert.Equal(1, rpc.AuthorizeCount);
        Assert.Equal(1, rpc.AuthenticateCount);
        Assert.Equal(ProtectedCredentialStatus.Available, protectedStore.OAuthTokenStatus);
    }

    private sealed class TokenExchangeHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"access_token\":\"access-token-value\",\"refresh_token\":\"refresh-token-value\",\"expires_in\":3600}",
                    Encoding.UTF8,
                    "application/json"),
            };
            return response;
        }
    }

    private sealed class AuthenticationRpcClient : IDiscordRpcClient
    {
        public int AuthorizeCount { get; private set; }

        public int AuthenticateCount { get; private set; }

        public event Action<JsonElement>? DispatchReceived
        {
            add { }
            remove { }
        }

        public Task<string> ConnectAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JsonElement> HandshakeAsync(
            string clientId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JsonElement> CommandAsync(
            string command,
            object arguments,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (command == "AUTHORIZE")
            {
                AuthorizeCount++;
                return Task.FromResult(Parse("{\"data\":{\"code\":\"authorization-code\"}}"));
            }

            if (command == "AUTHENTICATE")
            {
                AuthenticateCount++;
                return Task.FromResult(Parse(
                    "{\"data\":{\"user\":{\"id\":\"user-id\",\"username\":\"User\"}}}"));
            }

            throw new NotSupportedException(command);
        }

        public Task<JsonElement> SubscribeAsync(
            string eventName,
            object arguments,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Exception?> WaitForDisconnectAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}

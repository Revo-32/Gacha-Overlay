using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Backend.WebAuth;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GachaOverlay.Tests.Backend;

public sealed class M914WebAuthHttpTests
{
    [Fact]
    public void DeploymentLoggingOverrideCannotEnableCallbackQueryOrBodyLogging()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Trace)
            .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Trace)
            .AddFilter("Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware", LogLevel.Trace));
        WebAuthLogPolicy.Apply(services);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();
        foreach (var category in new[] { "Microsoft.AspNetCore.Hosting.Diagnostics", "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware" })
            foreach (var level in Enum.GetValues<LogLevel>()) Assert.False(factory.CreateLogger(category).IsEnabled(level));
    }

    [Fact]
    public async Task RealKestrelStartCallbackClaimNeverSendsTokensToBrowser()
    {
        await using var fixture = await Fixture.Start();
        var session = await fixture.Create();
        var state = M914WebAuthTests.State(session);
        using var callback = await fixture.Http.GetAsync("/auth/discord/callback?code=private-code&state=" + state);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        var html = await callback.Content.ReadAsStringAsync();
        foreach (var value in new[] { state, session.ClaimSecret, "private-code", "synthetic-secret", "access_token", "lso_" })
            Assert.DoesNotContain(value, html);
        Assert.Contains("LS Overlay", html); Assert.Contains("완료", html);
        Assert.True(callback.Headers.CacheControl!.NoStore);
        Assert.Equal("no-referrer", callback.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", callback.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(0, fixture.Registry.Count);
        await using var client = new LSOverlayRemoteClient(fixture.Http.BaseAddress!, fixture.Http);
        var claim = await client.GetDiscordWebAuthStatusAsync(session.SessionId, session.ClaimSecret);
        Assert.Equal(DiscordWebAuthStatus.Approved, claim.Status);
        Assert.NotNull(fixture.Registry.Authenticate(claim.AccessToken!));
        Assert.Equal(DiscordWebAuthStatus.Claimed, (await client.GetDiscordWebAuthStatusAsync(session.SessionId, session.ClaimSecret)).Status);
        using var replay = await fixture.Http.GetAsync("/auth/discord/callback?code=private-code&state=" + state);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(1, fixture.Registry.Count);
    }

    [Theory]
    [InlineData("?code=private-code")]
    [InlineData("?code=one&code=two&state=bad")]
    [InlineData("?code=one&error=access_denied&state=bad")]
    [InlineData("?state=bad&installationId=arbitrary")]
    public async Task BadCallbacksAreSafeAndNeverExchange(string query)
    {
        await using var fixture = await Fixture.Start();
        using var response = await fixture.Http.GetAsync("/auth/discord/callback" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(query, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, fixture.Identity.Calls);
    }

    [Fact]
    public async Task DisabledRoutesReturn404WithoutOAuthServices()
    {
        await using var fixture = await Fixture.Start(enabled: false);
        using var response = await fixture.Http.PostAsJsonAsync(DiscordWebAuthEndpoints.SessionsPath,
            new DiscordWebAuthStartRequest(1, Guid.NewGuid()), OverlayProtocolJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var callback = await fixture.Http.GetAsync("/auth/discord/callback?code=x&state=y");
        Assert.Equal(HttpStatusCode.NotFound, callback.StatusCode);
    }

    [Theory]
    [InlineData("discordUserId", "456")]
    [InlineData("guildId", "123")]
    [InlineData("redirectUri", "https://evil.test")]
    [InlineData("clientSecret", "synthetic")]
    public async Task StartRejectsClientOwnedIdentityAndRedirectFields(string field, string value)
    {
        await using var fixture = await Fixture.Start();
        using var response = await fixture.Http.PostAsJsonAsync(DiscordWebAuthEndpoints.SessionsPath,
            new Dictionary<string, object> { ["protocolVersion"] = 1, ["clientInstallationId"] = Guid.NewGuid(), [field] = value });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RateAndBodyLimitsApplyAndClaimCannotUseQuery()
    {
        await using var fixture = await Fixture.Start();
        var session = await fixture.Create();
        using var leakedClaim = await fixture.Http.GetAsync(DiscordWebAuthEndpoints.SessionsPath + "/" + session.SessionId + "?claim=" + session.ClaimSecret);
        Assert.Equal(HttpStatusCode.BadRequest, leakedClaim.StatusCode);
        for (var i = 1; i < 10; i++) await fixture.Create();
        using var over = await fixture.Http.PostAsJsonAsync(DiscordWebAuthEndpoints.SessionsPath, new DiscordWebAuthStartRequest(1, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        await using var fresh = await Fixture.Start();
        using var big = await fresh.Http.PostAsync(DiscordWebAuthEndpoints.SessionsPath,
            new StringContent("{\"padding\":\"" + new string('x', 4096) + "\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Contains(big.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge });
    }

    [Theory]
    [InlineData("success")]
    [InlineData("token400")]
    [InlineData("token500")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("malformed-token")]
    [InlineData("missing-token")]
    [InlineData("wrong-scope")]
    [InlineData("malformed-user")]
    [InlineData("missing-id")]
    [InlineData("bot")]
    [InlineData("system")]
    public async Task DiscordHttpIsFormEncodedIdentityOnlyBoundedAndDisposed(string scenario)
    {
        var options = DiscordWebAuthOptions.Resolve(M914WebAuthTests.Environment().GetValueOrDefault)!;
        var contents = new List<TrackingContent>();
        var calls = 0;
        using var handler = new Handler(async (request, cancellation) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("https://discord.com/api/oauth2/token", request.RequestUri!.AbsoluteUri);
                Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
                var fields = QueryHelpers.ParseQuery("?" + await request.Content.ReadAsStringAsync(cancellation));
                Assert.Equal("authorization_code", fields["grant_type"]); Assert.Equal("private-code", fields["code"]);
                Assert.Equal(options.RedirectUri.AbsoluteUri, fields["redirect_uri"]);
                Assert.Equal("synthetic-secret-not-production", fields["client_secret"]);
                Assert.Equal("verifier", fields["code_verifier"]);
                if (scenario == "network") throw new HttpRequestException("synthetic");
                if (scenario == "timeout") throw new TaskCanceledException("synthetic");
            }
            else
            {
                Assert.Equal("https://discord.com/api/v10/users/@me", request.RequestUri!.AbsoluteUri);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("private-access", request.Headers.Authorization.Parameter);
            }
            var body = calls == 1 ? scenario switch
            {
                "malformed-token" => "not-json",
                "missing-token" => "{}",
                "wrong-scope" => "{\"access_token\":\"private-access\",\"token_type\":\"Bearer\",\"scope\":\"identify email\"}",
                _ => "{\"access_token\":\"private-access\",\"refresh_token\":\"private-refresh\",\"token_type\":\"Bearer\",\"scope\":\"identify\"}",
            } : scenario switch
            {
                "malformed-user" => "{",
                "missing-id" => "{}",
                "bot" => "{\"id\":\"456\",\"bot\":true}",
                "system" => "{\"id\":\"456\",\"system\":true}",
                _ => "{\"id\":\"456\"}",
            };
            var content = new TrackingContent(body); contents.Add(content);
            return new HttpResponseMessage(scenario == "token400" ? HttpStatusCode.BadRequest : scenario == "token500" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK) { Content = content };
        });
        using var client = new DiscordIdentityClient(options, handler);
        if (scenario == "success") Assert.Equal((ulong)456, await client.IdentifyAsync("private-code", "verifier", default));
        else await Assert.ThrowsAnyAsync<Exception>(() => client.IdentifyAsync("private-code", "verifier", default));
        Assert.All(contents, content => Assert.True(content.Disposed));
        Assert.InRange(calls, 1, 2);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-host")]
    [InlineData("wrong-scope")]
    [InlineData("wrong-redirect")]
    [InlineData("claim-in-state")]
    public async Task RemoteClientValidatesOfficialBrowserUrlAndPrivateClaimHeader(string scenario)
    {
        var state = new string('s', 43); var claim = new string('c', 43);
        var url = "https://discord.com/oauth2/authorize?response_type=code&scope=identify&client_id=12345&redirect_uri=" +
            Uri.EscapeDataString("https://overlay.example/auth/discord/callback") + "&state=" + state + "&code_challenge_method=S256&code_challenge=" + new string('p', 43);
        url = scenario switch
        {
            "wrong-host" => url.Replace("discord.com", "evil.test"),
            "wrong-scope" => url.Replace("scope=identify", "scope=email"),
            "wrong-redirect" => url.Replace("overlay.example", "evil.test"),
            "claim-in-state" => url.Replace(state, claim),
            _ => url,
        };
        var session = new DiscordWebAuthStartResponse(1, Guid.NewGuid(), claim, url, DateTimeOffset.UtcNow.AddMinutes(5));
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(session, options: OverlayProtocolJson.Options) });
            Assert.Equal("LSOAuthClaim", request.Headers.Authorization!.Scheme); Assert.Equal(claim, request.Headers.Authorization.Parameter);
            Assert.DoesNotContain(claim, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new DiscordWebAuthClaimResult(1, DiscordWebAuthStatus.Pending), options: OverlayProtocolJson.Options) });
        }));
        await using var client = new LSOverlayRemoteClient(new Uri("https://overlay.example"), http);
        if (scenario != "valid") await Assert.ThrowsAsync<InvalidDataException>(() => client.StartDiscordWebAuthAsync(Guid.NewGuid()));
        else
        {
            Assert.NotNull(await client.StartDiscordWebAuthAsync(Guid.NewGuid()));
            Assert.Equal(DiscordWebAuthStatus.Pending, (await client.GetDiscordWebAuthStatusAsync(session.SessionId, claim)).Status);
            await client.CancelDiscordWebAuthAsync(session.SessionId, claim);
        }
    }

    private sealed class TrackingContent(string value) : StringContent(value)
    {
        public bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "LSOverlay-M914-Http-" + Guid.NewGuid().ToString("N"));
        private WebApplication _app = null!;
        public HttpClient Http = null!;
        public ClientCredentialRegistry Registry = null!;
        public M914WebAuthTests.IdentityFake Identity = new();
        public static async Task<Fixture> Start(bool enabled = true)
        {
            var fixture = new Fixture();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            var config = new BackendConfiguration(new BackendBotCredential("synthetic-bot"), 123, Array.Empty<ulong>(), fixture._directory,
                webAuth: enabled ? DiscordWebAuthOptions.Resolve(M914WebAuthTests.Environment().GetValueOrDefault) : null);
            fixture.Registry = new ClientCredentialRegistry(config);
            builder.Services.AddSingleton(config);
            if (enabled)
            {
                builder.Services.AddSingleton(new DiscordWebAuthService(config, fixture.Identity, new M914WebAuthTests.MemberFake(), fixture.Registry, new TransportMetrics()));
                builder.Services.AddSingleton(new WebAuthRateLimiter());
            }
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            });
            fixture._app = builder.Build(); fixture._app.UseBackendTransportSecurity(); fixture._app.MapDiscordWebAuth();
            await fixture._app.StartAsync();
            fixture.Http = new HttpClient { BaseAddress = new Uri(fixture._app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            return fixture;
        }
        public async Task<DiscordWebAuthStartResponse> Create()
        {
            using var response = await Http.PostAsJsonAsync(DiscordWebAuthEndpoints.SessionsPath, new DiscordWebAuthStartRequest(1, Guid.NewGuid()), OverlayProtocolJson.Options);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<DiscordWebAuthStartResponse>(OverlayProtocolJson.Options))!;
        }
        public async ValueTask DisposeAsync()
        {
            Http.Dispose(); await _app.StopAsync(); await _app.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}

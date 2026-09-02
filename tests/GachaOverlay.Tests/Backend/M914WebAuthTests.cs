using System.Net;
using System.Text.Json;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Logging;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Backend.WebAuth;
using LSOverlay.Protocol;
using Microsoft.AspNetCore.WebUtilities;

namespace GachaOverlay.Tests.Backend;

public sealed class M914WebAuthTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LSOverlay-M914-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    internal static Dictionary<string, string?> Environment() => new()
    {
        ["LSO_DISCORD_WEB_AUTH_ENABLED"] = "true",
        ["LSO_DISCORD_OAUTH_CLIENT_ID"] = "12345",
        ["LSO_DISCORD_OAUTH_CLIENT_SECRET"] = "synthetic-secret-not-production",
        ["LSO_PUBLIC_BASE_URL"] = "https://overlay.revo32.cloud",
    };

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void DisabledNeedsNoOAuthConfiguration(string? enabled)
    {
        Assert.Null(DiscordWebAuthOptions.Resolve(key => key == "LSO_DISCORD_WEB_AUTH_ENABLED" ? enabled : null));
    }

    [Theory]
    [InlineData("LSO_DISCORD_WEB_AUTH_ENABLED", "yes")]
    [InlineData("LSO_DISCORD_OAUTH_CLIENT_ID", null)]
    [InlineData("LSO_DISCORD_OAUTH_CLIENT_ID", "0")]
    [InlineData("LSO_DISCORD_OAUTH_CLIENT_ID", "+123")]
    [InlineData("LSO_DISCORD_OAUTH_CLIENT_SECRET", null)]
    [InlineData("LSO_DISCORD_OAUTH_CLIENT_SECRET", "secret\nvalue")]
    [InlineData("LSO_PUBLIC_BASE_URL", null)]
    [InlineData("LSO_PUBLIC_BASE_URL", "http://overlay.revo32.cloud")]
    [InlineData("LSO_PUBLIC_BASE_URL", "http://127.0.0.1:5188")]
    [InlineData("LSO_PUBLIC_BASE_URL", "https://example.test/path")]
    [InlineData("LSO_PUBLIC_BASE_URL", "https://example.test/?secret=value")]
    [InlineData("LSO_PUBLIC_BASE_URL", "https://example.test/#fragment")]
    [InlineData("LSO_PUBLIC_BASE_URL", "https://user:secret@example.test")]
    [InlineData("LSO_PUBLIC_BASE_URL", "https://example.test/a/..")]
    public void EnabledInvalidConfigurationFailsSanitized(string key, string? value)
    {
        var env = Environment(); env[key] = value;
        var error = Assert.Throws<BackendDeploymentException>(() => DiscordWebAuthOptions.Resolve(env.GetValueOrDefault));
        Assert.DoesNotContain("synthetic-secret-not-production", error.Message);
    }

    [Fact]
    public void DevelopmentAllowsOnlyExplicitLoopbackHttpAndRailwayNeverDoes()
    {
        var env = Environment(); env["ASPNETCORE_ENVIRONMENT"] = "Development";
        env["LSO_PUBLIC_BASE_URL"] = "http://127.0.0.1:5188";
        Assert.Equal("http://127.0.0.1:5188/auth/discord/callback", DiscordWebAuthOptions.Resolve(env.GetValueOrDefault)!.RedirectUri.AbsoluteUri);
        env["RAILWAY_SERVICE_ID"] = "synthetic";
        Assert.Throws<BackendDeploymentException>(() => DiscordWebAuthOptions.Resolve(env.GetValueOrDefault));
    }

    [Fact]
    public void SessionSecretsAreSeparateAndUrlHasOnlyIdentifyAndPkce()
    {
        var (service, _, _, _) = Create();
        var sessions = Enumerable.Range(0, 32).Select(_ => service.Start(Guid.NewGuid())).ToArray();
        Assert.Equal(32, sessions.Select(value => value.ClaimSecret).Distinct().Count());
        foreach (var session in sessions)
        {
            var url = new Uri(session.AuthorizationUrl);
            var query = QueryHelpers.ParseQuery(url.Query);
            Assert.Equal("discord.com", url.Host); Assert.Equal("https", url.Scheme);
            Assert.Equal("identify", query["scope"]); Assert.Equal("code", query["response_type"]);
            Assert.Equal("https://overlay.revo32.cloud/auth/discord/callback", query["redirect_uri"]);
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal(43, session.ClaimSecret.Length); Assert.Equal(43, query["state"].ToString().Length);
            Assert.NotEqual(session.ClaimSecret, query["state"].ToString());
            Assert.DoesNotContain(session.ClaimSecret, session.AuthorizationUrl);
            Assert.DoesNotContain("synthetic-secret", session.AuthorizationUrl);
            Assert.Equal(_now.AddMinutes(5), session.ExpiresAt);
            Assert.DoesNotContain(session.ClaimSecret, session.ToString());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InvalidStateNeverExchangesCode(string? state)
    {
        var (service, discord, _, registry) = Create(); service.Start(Guid.NewGuid());
        Assert.Equal(DiscordWebAuthFailure.InvalidRequest, await service.CompleteAsync(state, "code", null, default));
        Assert.Equal(0, discord.Calls); Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ApprovalRequiresPrivateOneTimeClaimAndPersistsOnlyRemoteHash()
    {
        var (service, discord, member, registry) = Create();
        var installation = Guid.NewGuid(); var session = service.Start(installation);
        var state = State(session);
        Assert.Equal(DiscordWebAuthStatus.Pending, service.Claim(session.SessionId, session.ClaimSecret).Status);
        Assert.Equal(DiscordWebAuthFailure.None, await service.CompleteAsync(state, "test-code", null, default));
        Assert.Equal(0, registry.Count);
        Assert.Equal(installation, member.Identity!.ClientInstallationId);
        Assert.Equal((ulong)456, member.Identity.DiscordUserId); Assert.Equal((ulong)123, member.Identity.GuildId);
        Assert.Throws<UnauthorizedAccessException>(() => service.Claim(session.SessionId, new string('x', 43)));
        var claim = service.Claim(session.SessionId, session.ClaimSecret);
        Assert.Equal(DiscordWebAuthStatus.Approved, claim.Status);
        Assert.NotNull(registry.Authenticate(claim.AccessToken!));
        Assert.Equal(DiscordWebAuthStatus.Claimed, service.Claim(session.SessionId, session.ClaimSecret).Status);
        Assert.Equal(DiscordWebAuthFailure.InvalidRequest, await service.CompleteAsync(state, "code", null, default));
        Assert.Equal(1, discord.Calls); Assert.Equal(1, registry.Count);
        var disk = string.Join("", Directory.GetFiles(_directory).Select(File.ReadAllText));
        foreach (var secret in new[] { state, session.ClaimSecret, claim.AccessToken!, "test-code", "synthetic-secret" }) Assert.DoesNotContain(secret, disk);
        Assert.NotNull(new ClientCredentialRegistry(_directory).Authenticate(claim.AccessToken!));
    }

    [Theory]
    [InlineData(1, DiscordWebAuthFailure.NotMember)]
    [InlineData(2, DiscordWebAuthFailure.VerificationUnavailable)]
    public async Task MembershipDenyAndUnavailableFailClosed(int membership, DiscordWebAuthFailure expected)
    {
        var (service, _, member, registry) = Create(); member.Result = (GuildMembershipStatus)membership;
        var session = service.Start(Guid.NewGuid());
        Assert.Equal(expected, await service.CompleteAsync(State(session), "code", null, default));
        var claim = service.Claim(session.SessionId, session.ClaimSecret);
        Assert.Equal(DiscordWebAuthStatus.Denied, claim.Status); Assert.Equal(expected, claim.Failure);
        Assert.Null(claim.AccessToken); Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ParallelCallbacksAreOneTimeAndCancellationCannotApprove()
    {
        var (service, discord, _, registry) = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        discord.Handler = async (_, _, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); return 456; };
        var session = service.Start(Guid.NewGuid());
        var first = service.CompleteAsync(State(session), "code", null, default);
        await entered.Task;
        Assert.Equal(DiscordWebAuthFailure.InvalidRequest, await service.CompleteAsync(State(session), "code", null, default));
        service.Claim(session.SessionId, session.ClaimSecret, cancel: true);
        release.SetResult();
        Assert.Equal(DiscordWebAuthFailure.InvalidRequest, await first);
        Assert.Equal(DiscordWebAuthStatus.Denied, service.Claim(session.SessionId, session.ClaimSecret).Status);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task DifferentSessionCodeCannotPassPkceAndSessionsStayBounded()
    {
        var (service, discord, _, registry) = Create();
        var a = service.Start(Guid.NewGuid()); var b = service.Start(Guid.NewGuid());
        var challenge = QueryHelpers.ParseQuery(new Uri(a.AuthorizationUrl).Query)["code_challenge"];
        discord.Handler = (_, verifier, _) =>
        {
            var actual = Convert.ToBase64String(CryptographicSecrets.Hash(verifier)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (actual != challenge) throw new HttpRequestException("invalid_grant");
            return Task.FromResult<ulong>(456);
        };
        Assert.Equal(DiscordWebAuthFailure.TemporaryFailure, await service.CompleteAsync(State(b), "code-for-a", null, default));
        Assert.Equal(0, registry.Count);
        for (var i = 2; i < 128; i++) service.Start(Guid.NewGuid());
        Assert.Equal(128, service.Count); Assert.Throws<InvalidOperationException>(() => service.Start(Guid.NewGuid()));
        _now = _now.AddMinutes(6); service.Sweep(); Assert.Equal(0, service.Count);
        Assert.Equal(DiscordWebAuthFailure.InvalidRequest, await service.CompleteAsync(State(a), "code", null, default));
        Assert.Throws<UnauthorizedAccessException>(() => service.Claim(a.SessionId, a.ClaimSecret));
        Assert.NotNull(service.Start(Guid.NewGuid()));
    }

    [Fact]
    public async Task ConsentDenialAndRequestCancellationDoNotIssue()
    {
        var (service, discord, _, registry) = Create();
        var session = service.Start(Guid.NewGuid());
        Assert.Equal(DiscordWebAuthFailure.Cancelled, await service.CompleteAsync(State(session), null, "access_denied", default));
        Assert.Equal(0, discord.Calls);
        var another = service.Start(Guid.NewGuid());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(DiscordWebAuthFailure.TemporaryFailure, await service.CompleteAsync(State(another), "code", null, cancelled.Token));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void RateLimitIsBoundedAndDoesNotStarveFiftyPollingUsersBehindOneNat()
    {
        var limiter = new WebAuthRateLimiter(() => _now);
        for (var i = 0; i < 10; i++) Assert.True(limiter.Allow("one-source", 0));
        Assert.False(limiter.Allow("one-source", 0));
        for (var i = 0; i < 1500; i++) Assert.True(limiter.Allow("one-source", 1));
        _now = _now.AddMinutes(1); Assert.True(limiter.Allow("one-source", 0));
        for (var i = 1; i < WebAuthRateLimiter.MaximumSources; i++) Assert.True(limiter.Allow("source" + i, 1));
        Assert.False(limiter.Allow("over-capacity", 1));
    }

    [Theory]
    [InlineData("https://test/auth/discord/callback?code=private-value&state=secret")]
    [InlineData("code=private-value state=secret")]
    [InlineData("{\"access_token\":\"private-value\",\"refresh_token\":\"secret\"}")]
    [InlineData("client_secret=private-value claimSecret=secret")]
    [InlineData("LSOAuthClaim private-value")]
    [InlineData("code_verifier=private-value")]
    public void CentralRedactionIsIdempotent(string input)
    {
        var result = SensitiveDataRedactor.Sanitize(input);
        Assert.DoesNotContain("private-value", result); Assert.DoesNotContain("=secret", result);
        Assert.Equal(result, SensitiveDataRedactor.Sanitize(result));
        Assert.Equal(OAuthDataRedactor.Sanitize(input), OAuthDataRedactor.Sanitize(OAuthDataRedactor.Sanitize(input)));
    }

    private (DiscordWebAuthService, IdentityFake, MemberFake, ClientCredentialRegistry) Create()
    {
        var options = DiscordWebAuthOptions.Resolve(Environment().GetValueOrDefault)!;
        var config = new BackendConfiguration(new BackendBotCredential("synthetic-bot"), 123, Array.Empty<ulong>(), _directory, webAuth: options);
        var registry = new ClientCredentialRegistry(config, () => _now);
        var discord = new IdentityFake(); var member = new MemberFake();
        return (new DiscordWebAuthService(config, discord, member, registry, new TransportMetrics(), () => _now), discord, member, registry);
    }

    internal static string State(DiscordWebAuthStartResponse response) => QueryHelpers.ParseQuery(new Uri(response.AuthorizationUrl).Query)["state"].ToString();
    internal sealed class IdentityFake : IDiscordIdentityClient
    {
        public int Calls;
        public Func<string, string, CancellationToken, Task<ulong>> Handler = (_, _, _) => Task.FromResult<ulong>(456);
        public Task<ulong> IdentifyAsync(string code, string verifier, CancellationToken token) { Calls++; return Handler(code, verifier, token); }
    }
    internal sealed class MemberFake : IGuildMembershipVerifier
    {
        public GuildMembershipStatus Result = GuildMembershipStatus.Member;
        public AuthenticatedClientIdentity? Identity;
        public Task<GuildMembershipStatus> VerifyAsync(AuthenticatedClientIdentity identity, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Identity = identity; return Task.FromResult(Result); }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

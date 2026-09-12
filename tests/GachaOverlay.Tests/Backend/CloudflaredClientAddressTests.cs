using System.Net;
using System.Net.Http.Json;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Backend.WebAuth;
using LSOverlay.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GachaOverlay.Tests.Backend;

public sealed class CloudflaredClientAddressTests
{
    [Theory]
    [InlineData("198.51.100.10", "198.51.100.10")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("2001:0db8:0:0:0:0:0:1", "2001:db8::1")]
    [InlineData("::ffff:198.51.100.10", "198.51.100.10")]
    public void TrustedPeerCanonicalizesClientAndScheme(string input, string expected)
    {
        var context = Context("127.0.0.1", input);
        Assert.True(CloudflaredClientAddress.Apply(context, IPAddress.Loopback));
        Assert.Equal(expected, context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("https", context.Request.Scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("198.51.100.1,198.51.100.2")]
    [InlineData("198.51.100.1:80")]
    [InlineData(" 198.51.100.1")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("[2001:db8::1]")]
    [InlineData("fe80::1%1")]
    public void MalformedFailsWithoutRewriting(string value)
    {
        var context = Context("127.0.0.1", value);
        Assert.False(CloudflaredClientAddress.Apply(context, IPAddress.Loopback));
        Assert.Equal(IPAddress.Loopback, context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
    }

    [Fact]
    public void DuplicateMissingAndInsecureHeadersFail()
    {
        foreach (var header in new[] { "CF-Connecting-IP", "X-Forwarded-Proto" })
        {
            var context = Context("127.0.0.1", "198.51.100.1");
            context.Request.Headers.Append(header, context.Request.Headers[header]);
            Assert.False(CloudflaredClientAddress.Apply(context, IPAddress.Loopback));
            context.Request.Headers.Remove(header);
            Assert.False(CloudflaredClientAddress.Apply(context, IPAddress.Loopback));
        }
        var insecure = Context("127.0.0.1", "127.0.0.1");
        insecure.Request.Headers["X-Forwarded-Proto"] = "http";
        Assert.False(CloudflaredClientAddress.Apply(insecure, IPAddress.Loopback));
    }

    [Fact]
    public void UntrustedPeerCannotSpoofIdentityOrHttps()
    {
        var context = Context("198.51.100.50", "198.51.100.1");
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        Assert.True(CloudflaredClientAddress.Apply(context, IPAddress.Loopback));
        Assert.Equal("198.51.100.50", context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("http", context.Request.Scheme);
        Assert.False(BackendTransportHosting.IsSecure(context, false));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.0/8")]
    [InlineData("127.1")]
    [InlineData("*")]
    public void ConfigurationRejectsBroadTrust(string value)
    {
        var env = new Dictionary<string, string?> { ["LSO_TRUSTED_CLOUDFLARED_PEER"] = value };
        Assert.Throws<BackendDeploymentException>(() => BackendDeploymentOptions.Resolve(env.GetValueOrDefault));
    }

    [Fact]
    public async Task ActualLoginEndpointSeparatesBucketsAndKeepsOAuthContract()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LSOverlay-CF-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var deployment = new BackendDeploymentOptions(false, new Uri("http://127.0.0.1:0"), directory, null)
        { TrustedCloudflaredPeer = IPAddress.Loopback };
        var config = new BackendConfiguration(new BackendBotCredential("synthetic"), 123, [], deployment: deployment,
            webAuth: DiscordWebAuthOptions.Resolve(M914WebAuthTests.Environment().GetValueOrDefault));
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(new DiscordWebAuthService(config, new M914WebAuthTests.IdentityFake(),
            new M914WebAuthTests.MemberFake(), new ClientCredentialRegistry(config), new TransportMetrics()));
        builder.Services.AddSingleton<WebAuthRateLimiter>();
        await using var app = builder.Build();
        app.UseBackendTransportSecurity();
        app.MapDiscordWebAuth();
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            async Task<HttpResponseMessage> Start(string ip)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, DiscordWebAuthEndpoints.SessionsPath);
                request.Headers.Add("CF-Connecting-IP", ip);
                request.Headers.Add("X-Forwarded-Proto", "https");
                request.Content = JsonContent.Create(new DiscordWebAuthStartRequest(1, Guid.NewGuid()), options: OverlayProtocolJson.Options);
                return await http.SendAsync(request);
            }
            foreach (var ip in new[] { "198.51.100.1", "2001:db8::2" })
            {
                for (var i = 0; i < 10; i++)
                {
                    using var response = await Start(ip);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.False(response.Headers.Contains("Set-Cookie"));
                    var session = await response.Content.ReadFromJsonAsync<DiscordWebAuthStartResponse>(OverlayProtocolJson.Options);
                    Assert.Contains(Uri.EscapeDataString(config.WebAuth!.RedirectUri.AbsoluteUri), session!.AuthorizationUrl);
                }
                using var blocked = await Start(ip);
                Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            }
            using var mapped = await Start("::ffff:198.51.100.1");
            Assert.Equal(HttpStatusCode.TooManyRequests, mapped.StatusCode);
            using var malformed = await Start("bad");
            Assert.Equal(HttpStatusCode.Forbidden, malformed.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static DefaultHttpContext Context(string peer, string ip)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Headers["CF-Connecting-IP"] = ip;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        return context;
    }
}

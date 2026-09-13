using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.CoreClient;
using LSOverlay.Backend.Security;
using Microsoft.Extensions.DependencyInjection;

namespace GachaOverlay.Tests.Backend;

public sealed partial class M93KestrelChatIntegrationTests
{
    [Fact]
    public async Task CoreProduction_MediaPermissionBurstReusesLeaseAndInvalidationDenies()
    {
        await using var fixture = await ChatFixture.StartAsync(core: true);
        var source = (LoopbackChatSource)fixture.Services.GetRequiredService<IChatDiscordSource>();
        var chat = fixture.Services.GetRequiredService<RemoteChatService>();
        var identity = new AuthenticatedClientIdentity(Guid.NewGuid(), 456, 123);
        var channel = ChannelSelection.Ids[0];
        for (var i = 0; i < 20; i++)
            Assert.Equal(ChatAuthorizationStatus.Authorized, (await chat.AuthorizeMediaAsync(identity, channel, CancellationToken.None)).Status);
        Assert.Equal(1, source.PermissionRequests); // Former per-image forced path: 20.
        source.RejectAccess = true;
        fixture.Services.GetRequiredService<IChatAuthorizationService>().InvalidateGuild(123);
        Assert.NotEqual(ChatAuthorizationStatus.Authorized, (await chat.AuthorizeMediaAsync(identity, channel, CancellationToken.None)).Status);
        Assert.Equal(2, source.PermissionRequests);
    }

    [Fact]
    public async Task CoreProduction_CoreOnlyRetiresFullReadRoutesButKeepsCoreAndSalesAuthentication()
    {
        await using var fixture = await ChatFixture.StartAsync(core: true, coreOnly: true);
        using var http = new HttpClient { BaseAddress = fixture.BaseUri };
        foreach (var path in new[] { "api/v1/stream", "api/v1/stream/", "api/v1/stream/nested", "API/V1/STREAM", "api/v1/bootstrap/", "api/v1/chat/channels", "api/v1/sales/bootstrap/" })
            Assert.Equal(HttpStatusCode.Gone, (await http.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/v1/core/manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("api/v1/core/credential/renew", null)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Gone, (await http.PostAsync("api/v1/sales/status", null)).StatusCode);
    }

    [Fact]
    public async Task CoreProduction_LoginRenewsWithoutReplacingTokenAndCannotResurrectExpiredToken()
    {
        var now = DateTimeOffset.UtcNow;
        await using var fixture = await ChatFixture.StartAsync(core: true, credentialClock: () => now);
        var installation = Guid.NewGuid();
        var issued = fixture.Credentials.Issue(installation, 456, 123);
        using var http = new HttpClient { BaseAddress = fixture.BaseUri };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.AccessToken);
        now = now.AddDays(160);
        var response = await http.PostAsync("api/v1/core/credential/renew", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(now.AddDays(180), json.RootElement.GetProperty("credentialExpiresAt").GetDateTimeOffset());
        Assert.NotNull(fixture.Credentials.Authenticate(issued.AccessToken));
        Assert.Equal(1, fixture.Credentials.Count);
        Assert.Equal(now.AddDays(180), fixture.Credentials.Renew(issued.AccessToken));
        now = now.AddDays(181);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("api/v1/core/credential/renew", null)).StatusCode);
        Assert.Null(fixture.Credentials.Renew(issued.AccessToken));
    }

    [Fact]
    public async Task CoreProduction_ReplacedLoginCannotRenew()
    {
        await using var fixture = await ChatFixture.StartAsync(core: true);
        var installation = Guid.NewGuid();
        var old = fixture.Credentials.Issue(installation, 456, 123);
        fixture.Credentials.Issue(installation, 456, 123);
        Assert.Null(fixture.Credentials.Renew(old.AccessToken));
    }
}

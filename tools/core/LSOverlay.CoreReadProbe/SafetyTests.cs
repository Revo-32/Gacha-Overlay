using System.Net;
using Discord;
using Discord.Rest;

namespace LSOverlay.CoreReadProbe;

internal static class SafetyTests
{
    public static async Task RunAsync()
    {
        var assertions = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Read-only safety assertion failed."); assertions++; }
        var handler = new FakeHandler();
        using var rest = new ReadOnlyRest(123, handler);
        foreach (var method in new[] { "POST", "PUT", "PATCH", "DELETE", "HEAD", "get" }) Check(!rest.Allows(method, "users/@me"));
        foreach (var path in new[] { "https://example.com", "//example.com", "../users/@me", "users/@me?token=x", "guilds/456", "guilds/123/members", "guilds/123/members/1?x=1", "channels/789/messages?limit=20" }) Check(!rest.Allows("GET", path));
        foreach (var path in new[] { "users/@me", "oauth2/applications/@me", "guilds/123?with_counts=false", "guilds/123/channels", "guilds/123/members/1" }) Check(rest.Allows("GET", path));
        rest.SelectChannel(789);
        Check(rest.Allows("GET", "channels/789/messages?limit=20"));
        Check(!rest.Allows("GET", "channels/789/messages?limit=100"));
        Check(!rest.Allows("GET", "channels/790/messages?limit=20"));
        try { rest.SelectChannel(790); throw new Exception("Expected rejection"); } catch (InvalidOperationException) { assertions++; }
        try { await rest.SendAsync("POST", "users/@me", CancellationToken.None, false, null!); throw new Exception("Expected rejection"); } catch (InvalidOperationException) { assertions++; }
        try { await rest.SendAsync("GET", "users/@me", "{}", CancellationToken.None, false, null!); throw new Exception("Expected rejection"); } catch (InvalidOperationException) { assertions++; }
        Check(handler.Calls == 0);
        var response = await rest.SendAsync("GET", "users/@me", CancellationToken.None, false, null!);
        response.Stream.Dispose();
        Check(handler.Calls == 1 && rest.Requests == 1);
        Check(handler.LastUri == "https://discord.com/api/v10/users/@me");
        handler.Status = HttpStatusCode.TooManyRequests;
        try { await rest.SendAsync("GET", "users/@me", CancellationToken.None, false, null!); throw new Exception("Expected rejection"); } catch (InvalidOperationException) { assertions++; }
        Check(handler.Calls == 2);
        handler.Status = HttpStatusCode.Redirect;
        try { await rest.SendAsync("GET", "users/@me", CancellationToken.None, false, null!); throw new Exception("Expected rejection"); } catch (InvalidOperationException) { assertions++; }
        Check(handler.Calls == 3);
        handler.Status = HttpStatusCode.OK;
        (await rest.SendAsync("GET", "channels/789/messages?limit=20", CancellationToken.None, false, null!)).Stream.Dispose();
        try { await rest.SendAsync("GET", "channels/789/messages?limit=20", CancellationToken.None, false, null!); throw new Exception("Expected rejection"); } catch (ReadOnlyFailure error) { Check(error.Code == "history-already-read"); }
        Check(handler.Calls == 4);
        try { await rest.SendAsync("GET", "users/@me", CancellationToken.None, false, null!, new[] { new KeyValuePair<string, IEnumerable<string>>("Authorization", new[] { "test" }) }); throw new Exception("Expected rejection"); } catch (ReadOnlyFailure) { assertions++; }
        Check(handler.Calls == 4);
        // Exercise the real SDK with an entirely offline transport. SDK supplies
        // an empty request-header collection; that is not an override header.
        var sdkHandler = new FakeHandler { Body = "{\"id\":\"123456789012345678\",\"username\":\"offline-probe\",\"discriminator\":\"0000\",\"avatar\":null,\"bot\":true}" };
        using var sdkRest = new ReadOnlyRest(123, sdkHandler);
        using var sdk = new DiscordRestClient(new DiscordRestConfig { RestClientProvider = _ => sdkRest, DefaultRetryMode = RetryMode.AlwaysFail });
        await sdk.LoginAsync(TokenType.Bot, "synthetic-only-not-a-real-token", validateToken: false);
        Console.WriteLine($"Offline SDK login: requests={sdkHandler.Calls}, endpoint={sdkHandler.LastUri}, user={sdk.CurrentUser.Id}.");
        Check(sdk.CurrentUser.Id == 123456789012345678 && sdkHandler.Calls is >= 1 and <= 2);
        Console.WriteLine($"PASS {assertions} read-only safety assertions; no network used.");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUri { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }
}

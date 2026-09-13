using System.Net;
using LSOverlay.CoreMedia;

namespace LSOverlay.CoreMediaProbe;
internal static class SecurityTests
{
    public static async Task Run(string root, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        foreach (var value in new[] {
            "http://cdn.discordapp.com/emojis/1.png", "https://cdn.discordapp.com:444/emojis/1.png",
            "https://cdn.discordapp.com.evil.test/emojis/1.png", "https://evil@cdn.discordapp.com/emojis/1.png",
            "https://cdn.discordapp.com/emojis/1.png#fragment", "https://127.0.0.1/emojis/1.png",
            "file:///etc/passwd", "https://cdn.discordapp.com/../secret", "https://cdn.discordapp.com/attachments/1/2/a%2fb.png",
            "https://cdn.discordapp.com/attachments/1/2/a%252fb.png", "https://cdn.discordapp.com/attachments/1/2/a%0ab.png" })
            await reject(() => { MediaSourcePolicy.Validate(value); return Task.CompletedTask; }, "untrusted URL rejected");
        check(MediaSourcePolicy.Validate("https://cdn.discordapp.com/attachments/1/2/%ED%95%9C%EA%B8%80.png?ex=1&is=2&hm=test").Scheme == "https", "signed Korean attachment path accepted");
        foreach(var value in new[]{"https://static.klipy.com/ii/abc/de/f.gif","https://static1.klipy.com/ii/abc/a.webp","https://static2.klipy.com/ii/abc/a.gif","https://media1.giphy.com/media/cZ7rmKfFYOvYI/200.gif","https://media.tenor.com/abc/def.gif","https://images-ext-1.discordapp.net/external/abc/https/static.klipy.com/ii/a.gif"})
            check(MediaSourcePolicy.Validate(value).AbsoluteUri==value,"known provider image URL preserved");
        foreach(var value in new[]{"https://klipy.com/gifs/a","https://api.klipy.com/v2/search","https://static.klipy.com.evil.test/a.gif","https://evil.klipy.com/a.gif","https://media1.giphy.com/media/a/200.mp4","https://media1.giphy.com/a.gif#x","https://images-ext-1.discordapp.net/external/a/https/127.0.0.1/a.gif","https://static.klipy.com/a%2fb.gif"})
            await reject(()=>{MediaSourcePolicy.Validate(value);return Task.CompletedTask;},"provider boundary rejected");
        foreach (var ip in new[] { "127.0.0.1", "10.0.0.1", "169.254.169.254", "172.16.1.1", "192.168.0.10", "0.0.0.0", "100.64.1.1", "224.0.0.1", "255.255.255.255", "192.0.2.1", "198.18.0.1", "203.0.113.1", "::1", "::", "fc00::1", "fe80::1", "ff02::1", "2001:db8::1", "2002:7f00:1::", "::ffff:127.0.0.1" })
            check(!MediaSourcePolicy.IsPublic(IPAddress.Parse(ip)), "non-public/transition IP rejected");
        check(MediaSourcePolicy.IsPublic(IPAddress.Parse("1.1.1.1")) && MediaSourcePolicy.IsPublic(IPAddress.Parse("2606:4700::1111")), "global IPv4 and IPv6 accepted");
        var clock = new FakeClock(); var catalog = new MediaCatalog(clock); const string url = "https://cdn.discordapp.com/emojis/123.png";
        var id = catalog.RegisterCanonical("viewer", "channel", "message", url);
        check(id.Length == 48 && id != url && id == catalog.RegisterCanonical("viewer", "channel", "message", url), "opaque bounded references stable within scope");
        async Task Denied(Func<Task> body, string name) { try { await body(); } catch (UnauthorizedAccessException) { check(true, name); return; } check(false, name); }
        var permissionCalls = 0;
        Task<bool> Permission(string channel, string message, CancellationToken _) { permissionCalls++; return Task.FromResult(channel == "channel" && message == "message"); }
        check(await catalog.ResolveAuthorizedAsync(id, "viewer", Permission, default) == url, "authorized media resolves canonical source");
        await catalog.ResolveAuthorizedAsync(id, "viewer", Permission, default);
        check(permissionCalls == 2, "cache/reference hits still require current permission");
        await Denied(() => catalog.ResolveAuthorizedAsync(id, "other", Permission, default), "cross-viewer media denied");
        await Denied(() => catalog.ResolveAuthorizedAsync(id, "viewer", (_, _, _) => Task.FromResult(false), default), "revoked channel access denied");
        catalog.RevokeMessage("channel", "message");
        await Denied(() => catalog.ResolveAuthorizedAsync(id, "viewer", Permission, default), "deleted message reference denied");
        id = catalog.RegisterCanonical("viewer", "channel", "message", url); clock.Now += TimeSpan.FromMinutes(11);
        await Denied(() => catalog.ResolveAuthorizedAsync(id, "viewer", Permission, default), "expired reference denied");
        using var cache = new DerivativeCache(Path.Combine(root, "fetch"));
        using var handler = new FakeHandler(); using var client = new HttpClient(handler);
        handler.Reply = () => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("http://127.0.0.1/private"); return response; };
        await reject(() => MediaSourcePolicy.FetchAndConvertAsync(client, cache, url, new(16, 16), default), "redirect rejected without follow-up");
        check(handler.Calls == 1, "redirect makes one request only");
        handler.Reply = () => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixtures.Png()) }; response.Content.Headers.ContentType = new("text/html"); return response; };
        await reject(() => MediaSourcePolicy.FetchAndConvertAsync(client, cache, url, new(16, 16), default), "non-image CDN response rejected");
        handler.Reply = () => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixtures.Png()) }; response.Content.Headers.ContentType = new("image/png"); response.Content.Headers.ContentLength = MediaConverter.MaximumSourceBytes + 1; return response; };
        await reject(() => MediaSourcePolicy.FetchAndConvertAsync(client, cache, url, new(16, 16), default), "oversized declared CDN response rejected");
        handler.Reply = () => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixtures.Png()) }; response.Content.Headers.ContentType = new("image/png"); return response; };
        var key = await MediaSourcePolicy.FetchAndConvertAsync(client, cache, url, new(16, 16), default);
        using (var lease = cache.Open(key)) check(MediaPackage.Read(lease).Frames.Count == 1, "bounded fake CDN conversion succeeds");
        check(handler.SafeRequests, "fetch sends no credentials or request body");
    }
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Reply { get; set; } = () => new(HttpStatusCode.NotFound);
        public int Calls { get; private set; }
        public bool SafeRequests { get; private set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; SafeRequests &= request.Method == HttpMethod.Get && request.Content is null && !request.Headers.Any();
            cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Reply());
        }
    }
}

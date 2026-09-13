using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LSOverlay.Backend.CoreClient;
using LSOverlay.Protocol;
using Microsoft.AspNetCore.WebUtilities;
using System.Threading.Channels;

namespace LSOverlay.CoreFixtureHost;

// Compiled only into developer tooling. This is NOT the real Discord auth service.
// All identities/data are synthetic; there is no external route or Production store.
internal sealed class CoreContractFixture(bool chatFixture = false)
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new();
    private readonly object _gate = new();
    private CoreSnapshot _snapshot = chatFixture ? ChatFixtureData.Create(1, 20, 1) : CoreFixtureData.Create(Guid.NewGuid().ToString("N"), 1);
    private readonly ConcurrentDictionary<int, Channel<bool>> _updates = new();
    private readonly SemaphoreSlim _streamSlots = new(8, 8);
    private int _connections, _authStarts, _snapshots, _resumes;
    private readonly string _origin = Environment.GetEnvironmentVariable("CORE_FIXTURE_ORIGIN") ?? "http://127.0.0.1:15188";

    public void Map(WebApplication app)
    {
        if (!Uri.TryCreate(_origin, UriKind.Absolute, out var origin) || origin.Scheme != "http" || !origin.IsLoopback ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/")
            throw new InvalidOperationException("Synthetic fixture origin must be an explicit HTTP loopback origin.");
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });
        app.MapGet("/fixture/manifest", () => Results.Json(new
        {
            product = "LS Overlay Core",
            stage = chatFixture ? "M3 synthetic chat" : "M2 synthetic contracts",
            syntheticAuthentication = true,
            liveUpdates = true,
            liveDiscord = false,
            productionDataMounted = false,
            mediaFetchEnabled = false,
            capabilities = new[] { CoreClientProtocol.Capability },
            connections = Volatile.Read(ref _connections),
            authStarts = Volatile.Read(ref _authStarts),
            snapshots = Volatile.Read(ref _snapshots),
            resumes = Volatile.Read(ref _resumes)
        }));
        app.MapPost("/api/v1/auth/discord/sessions", (Delegate)StartAsync);
        app.MapMethods("/api/v1/auth/discord/sessions/{sessionId:guid}", new[] { "GET", "DELETE" }, Claim);
        app.Map("/api/v1/core/stream", StreamAsync);
        app.MapPost("/fixture/revision", (HttpContext context) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            lock (_gate) _snapshot = CreateSnapshot(_snapshot.Generation, _snapshot.Revision + 1);
            SignalUpdate();
            return Results.Ok();
        });
        app.MapPost("/fixture/generation", (HttpContext context) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            lock (_gate) _snapshot = CreateSnapshot(Guid.NewGuid().ToString("N"), 1);
            SignalUpdate();
            return Results.Ok();
        });
        app.MapGet("/fixture/redirect", () => Results.Redirect("/healthz"));
        app.MapGet("/fixture/oversized", () => Results.Text(new string('x', 65537)));
        app.MapGet("/fixture/delayed", async (HttpContext context) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
            return Results.Ok();
        });
    }

    private CoreSnapshot CreateSnapshot(string generation, long revision) => chatFixture
        ? ChatFixtureData.Create(checked((int)revision), 20, revision) with { Generation = generation }
        : CoreFixtureData.Create(generation, revision);
    private void SignalUpdate()
    {
        foreach (var channel in _updates.Values) channel.Writer.TryWrite(true);
    }

    private async Task<IResult> StartAsync(HttpContext context)
    {
        if (context.Request.QueryString.HasValue || context.Request.ContentLength > 1024) return Results.BadRequest();
        foreach (var pair in _sessions.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).ToArray()) _sessions.TryRemove(pair.Key, out _);
        foreach (var pair in _tokens.Where(pair => pair.Value <= DateTimeOffset.UtcNow).ToArray()) _tokens.TryRemove(pair.Key, out _);
        if (_sessions.Count >= 32 || _tokens.Count >= 64) return Results.StatusCode(429);
        DiscordWebAuthStartRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<DiscordWebAuthStartRequest>(OverlayProtocolJson.Options, context.RequestAborted); }
        catch (JsonException) { return Results.BadRequest(); }
        if (request is null || request.ProtocolVersion != 1 || request.ClientInstallationId == Guid.Empty) return Results.BadRequest();
        var id = Guid.NewGuid(); var secret = RandomSecret(); var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var authorizationUrl = QueryHelpers.AddQueryString("https://discord.com/oauth2/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "123",
            ["response_type"] = "code",
            ["scope"] = "identify",
            ["redirect_uri"] = _origin.TrimEnd('/') + "/auth/discord/callback",
            ["state"] = RandomSecret(),
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = RandomSecret()
        });
        _sessions[id] = new Session(Hash(secret), expires);
        Interlocked.Increment(ref _authStarts);
        return Results.Json(new DiscordWebAuthStartResponse(1, id, secret, authorizationUrl, expires), OverlayProtocolJson.Options);
    }

    private IResult Claim(HttpContext context, Guid sessionId)
    {
        const string scheme = "LSOAuthClaim ";
        var header = context.Request.Headers.Authorization;
        if (context.Request.QueryString.HasValue || header.Count != 1 || header[0] is not string auth ||
            !auth.StartsWith(scheme, StringComparison.Ordinal) || auth.Length != scheme.Length + 43 ||
            !_sessions.TryGetValue(sessionId, out var session) || !CryptographicOperations.FixedTimeEquals(Hash(auth[scheme.Length..]), session.ClaimHash))
            return Results.Unauthorized();
        lock (session)
        {
            DiscordWebAuthClaimResult result;
            if (session.Claimed) result = new(1, DiscordWebAuthStatus.Claimed);
            else if (session.Expires <= DateTimeOffset.UtcNow) result = new(1, DiscordWebAuthStatus.Expired, DiscordWebAuthFailure.SessionExpired);
            else if (HttpMethods.IsDelete(context.Request.Method)) { session.Claimed = true; result = new(1, DiscordWebAuthStatus.Denied, DiscordWebAuthFailure.Cancelled); }
            else if (session.Polls++ == 0) result = new(1, DiscordWebAuthStatus.Pending);
            else
            {
                session.Claimed = true;
                var token = "lso_" + RandomSecret(); var expires = DateTimeOffset.UtcNow.AddMinutes(10);
                _tokens[Convert.ToHexString(Hash(token))] = expires;
                result = new(1, DiscordWebAuthStatus.Approved, AccessToken: token, CredentialExpiresAt: expires);
            }
            return Results.Json(result, OverlayProtocolJson.Options);
        }
    }

    private bool Authorized(HttpContext context)
    {
        var header = context.Request.Headers.Authorization;
        return header.Count == 1 && header[0] is string auth && auth.StartsWith("Bearer ", StringComparison.Ordinal) &&
            _tokens.TryGetValue(Convert.ToHexString(Hash(auth[7..])), out var expires) && expires > DateTimeOffset.UtcNow;
    }

    private async Task StreamAsync(HttpContext context)
    {
        if (!Authorized(context)) { context.Response.StatusCode = 401; return; }
        if (context.Request.QueryString.HasValue || !context.WebSockets.IsWebSocketRequest ||
            !context.WebSockets.WebSocketRequestedProtocols.Contains(OverlayTransportProtocol.WebSocketSubprotocol, StringComparer.Ordinal))
        { context.Response.StatusCode = 400; return; }
        if (!_streamSlots.Wait(0)) { context.Response.StatusCode = 429; return; }
        var connectionId = Interlocked.Increment(ref _connections);
        var updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _updates[connectionId] = updates;
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(OverlayTransportProtocol.WebSocketSubprotocol);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            lifetime.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                using var helloDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                helloDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                var helloBytes = await ReceiveAsync(socket, helloDeadline.Token);
                var hello = JsonSerializer.Deserialize<CoreSessionStart>(helloBytes, OverlayProtocolJson.Options) ?? throw new InvalidDataException();
                CoreSnapshotWire.ValidateHello(hello);
                CoreSnapshot snapshot; lock (_gate) snapshot = _snapshot;
                var resumed = CoreSnapshotWire.CanResume(hello, snapshot);
                if (!resumed)
                {
                    foreach (var frame in CoreSnapshotWire.Encode(snapshot)) await socket.SendAsync(frame, WebSocketMessageType.Text, true, lifetime.Token);
                    Interlocked.Increment(ref _snapshots);
                }
                else Interlocked.Increment(ref _resumes);
                var ready = JsonSerializer.SerializeToUtf8Bytes(new CoreReady(1, CoreClientProtocol.Ready, snapshot.Generation, snapshot.Revision, resumed), OverlayProtocolJson.Options);
                await socket.SendAsync(ready, WebSocketMessageType.Text, true, lifetime.Token);
                while (!lifetime.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var nextEvent = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    nextEvent.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        await updates.Reader.ReadAsync(nextEvent.Token);
                        CoreSnapshot latest; lock (_gate) latest = _snapshot;
                        if (latest.Generation == snapshot.Generation && latest.Revision == snapshot.Revision) continue;
                        foreach (var frame in CoreSnapshotWire.Encode(latest)) await socket.SendAsync(frame, WebSocketMessageType.Text, true, lifetime.Token);
                        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new CoreReady(1, CoreClientProtocol.Ready, latest.Generation, latest.Revision, false), OverlayProtocolJson.Options), WebSocketMessageType.Text, true, lifetime.Token);
                        snapshot = latest; Interlocked.Increment(ref _snapshots);
                        continue;
                    }
                    catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                    await socket.SendAsync("{\"protocolVersion\":1,\"type\":\"heartbeat\"}"u8.ToArray(), WebSocketMessageType.Text, true, lifetime.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    using var ack = JsonDocument.Parse(await ReceiveAsync(socket, deadline.Token));
                    if (ack.RootElement.GetProperty("type").GetString() != "heartbeat_ack") throw new InvalidDataException();
                }
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or InvalidDataException or JsonException or NotSupportedException or InvalidOperationException)
            {
                socket.Abort(); // Never log claim/token/control bodies, even on malformed input.
            }
        }
        finally { _updates.TryRemove(connectionId, out _); _streamSlots.Release(); }
    }

    private static async Task<byte[]> ReceiveAsync(WebSocket socket, CancellationToken cancellation)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        for (; ; )
        {
            var result = await socket.ReceiveAsync(buffer, cancellation);
            if (result.MessageType != WebSocketMessageType.Text || bytes.Length + result.Count > 16 * 1024) throw new InvalidDataException();
            bytes.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return bytes.ToArray();
        }
    }
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static string RandomSecret() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private sealed class Session(byte[] claimHash, DateTimeOffset expires)
    {
        public byte[] ClaimHash { get; } = claimHash;
        public DateTimeOffset Expires { get; } = expires;
        public bool Claimed { get; set; }
        public int Polls { get; set; }
    }
}

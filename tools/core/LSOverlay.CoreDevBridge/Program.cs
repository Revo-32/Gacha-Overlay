using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using LSOverlay.Backend.CoreClient;
using LSOverlay.CoreDevBridge;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

if (args is ["--self-test"]) { BridgeTests.Run(); return; }
if (args.Length != 0) throw new InvalidOperationException("Use the isolated deployment configuration.");
if (!ulong.TryParse(Environment.GetEnvironmentVariable("CORE_DEV_CHAT_CHANNEL"),out var channelId) || channelId == 0)
    throw new InvalidOperationException("An explicit existing authorized development chat channel is required.");
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // No headers, claim bodies, content, exception URLs or tokens in logs.
builder.WebHost.ConfigureKestrel(options => { options.Limits.MaxRequestBodySize = 4096; options.Limits.MaxConcurrentConnections = 4; });
var app = builder.Build();
using var streamSlot = new SemaphoreSlim(1,1);
app.UseWebSockets();
app.Use(async (context,next) => {
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Headers.ContainsKey("Origin") || context.Request.QueryString.HasValue ||
        context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 400; return; }
    await next();
});
app.MapGet("/healthz",() => Results.Json(new { status="ok",environment="isolated-core-dev",productionConfigurationChanged=false }));
app.MapGet("/core-dev/manifest",() => Results.Json(new { readOnly=true,authOrigin=ReadOnlyPolicy.Origin,liveDiscord=true,
    mediaDelivery=false,maximumConnections=1,maximumSessionMinutes=30 }));
app.MapGet("/api/v1/core/stream",async (HttpContext context) => {
    var header = context.Request.Headers.Authorization;
    if (header.Count != 1 || header[0] is not string auth || !auth.StartsWith("Bearer ",StringComparison.Ordinal) || !ReadOnlyPolicy.IsToken(auth[7..]))
    { context.Response.StatusCode=401; return; }
    if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains(OverlayTransportProtocol.WebSocketSubprotocol))
    { context.Response.StatusCode=400; return; }
    if (!streamSlot.Wait(0)) { context.Response.StatusCode=429; return; }
    try
    {
        var token = auth[7..]; // Per-session RAM only; never copied to disk/config/environment.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        lifetime.CancelAfter(TimeSpan.FromMinutes(30));
        using var http = new HttpClient(new ReadOnlyHandler(new SocketsHttpHandler { AllowAutoRedirect=false,UseCookies=false,UseProxy=false })) { Timeout=TimeSpan.FromSeconds(15) };
        await using var remote = new LSOverlayRemoteClient(new Uri(ReadOnlyPolicy.Origin),http);
        BootstrapResponse identity;
        try { identity = await remote.GetBootstrapAsync(token,lifetime.Token); }
        catch (RemoteAuthenticationRequiredException) { context.Response.StatusCode=401; return; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { context.Response.StatusCode=503; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync(OverlayTransportProtocol.WebSocketSubprotocol);
        using var abort = lifetime.Token.Register(socket.Abort);
        var state = new RelayState(identity); var gate = new object();
        var updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true });
        var chatRequests=Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true });
        var salesRequests=Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true });
        void Change(Action action)
        {
            try { lock (gate) action(); updates.Writer.TryWrite(true); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or InvalidOperationException) { lifetime.Cancel(); }
        }
        remote.ChatChannelReady += value => Change(() => state.Chat(value));
        remote.ChatMutationReceived += value => Change(() => state.Chat(value));
        remote.ChatStreamStatusChanged += (channel,status) => {
            Change(() => state.ChatState(channel,status));
            if (channel==channelId && status==OverlayTransportProtocol.ChatResyncRequired) chatRequests.Writer.TryWrite(true);
        };
        remote.SalesReady += value => Change(() => state.Sales(value));
        remote.SalesMutationReceived += value => Change(() => state.Sales(value));
        remote.SalesStreamStatusChanged += value => {
            Change(() => state.SalesState(value));
            if (value==OverlayTransportProtocol.SalesResyncRequired) salesRequests.Writer.TryWrite(true);
        };
        remote.HostPresenceChanged += value => Change(() => state.Presence(value));
        Task? producer=null;
        try
        {
            using var helloDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); helloDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            var hello = JsonSerializer.Deserialize<CoreSessionStart>(await Receive(socket,helloDeadline.Token),OverlayProtocolJson.Options) ?? throw new InvalidDataException();
            CoreSnapshotWire.ValidateHello(hello);
            // Reconnect starts a fresh viewer-scoped projection, never false-resumes
            // a stale previous process or upstream permission generation.
            producer = Task.Run(async () => {
                try {
                    var catalog = await remote.GetChatChannelsAsync(token,lifetime.Token);
                    if (!catalog.Channels.Any(channel => channel.ChannelId == channelId)) throw new InvalidDataException("Channel access unavailable.");
                    var chat = await remote.GetChatBootstrapAsync(token,channelId,lifetime.Token);
                    Change(() => state.Chat(chat));
                    var channelSwitches = Channel.CreateBounded<ChatBootstrapResponse>(1);
                    var salesResyncs = Channel.CreateBounded<SalesBootstrapResponse>(1);
                    // Independent bounded workers: slow Sales HTTP never blocks
                    // Chat delivery/heartbeat. No periodic history polling.
                    async Task Pump(bool sales) {
                        var requests=sales ? salesRequests : chatRequests;
                        while (await requests.Reader.WaitToReadAsync(lifetime.Token)) {
                            requests.Reader.TryRead(out _);
                            try {
                                if (sales) await salesResyncs.Writer.WriteAsync(await remote.GetSalesBootstrapAsync(token,lifetime.Token),lifetime.Token);
                                else await channelSwitches.Writer.WriteAsync(await remote.GetChatBootstrapAsync(token,channelId,lifetime.Token),lifetime.Token);
                            } catch (HttpRequestException) { Change(() => { if (sales) state.SalesState(OverlayTransportProtocol.SalesFailed); else state.ChatState(channelId,OverlayTransportProtocol.ChatFailed); }); }
                            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { Change(() => { if (sales) state.SalesState(OverlayTransportProtocol.SalesFailed); else state.ChatState(channelId,OverlayTransportProtocol.ChatFailed); }); }
                            catch (RemoteAuthenticationRequiredException) { lifetime.Cancel(); }
                            await Task.Delay(1000,lifetime.Token);
                        }
                    }
                    var chatPump=Pump(false); var salesPump=Pump(true); salesRequests.Writer.TryWrite(true);
                    try { await remote.StreamIndependentAsync(token,chat,channelSwitches.Reader,salesResyncs.Reader,
                        presence => Change(() => state.Presence(presence)),() => { },lifetime.Token); }
                    finally { lifetime.Cancel(); try { await Task.WhenAll(chatPump,salesPump); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { } }
                } finally { lifetime.Cancel(); }
            },lifetime.Token);
            var receiver = Task.Run(async () => {
                try {
                    while (!lifetime.IsCancellationRequested) {
                        using var ackDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); ackDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                        using var message = JsonDocument.Parse(await Receive(socket,ackDeadline.Token));
                        if (message.RootElement.GetProperty("type").GetString() != OverlayTransportProtocol.HeartbeatAck) throw new InvalidDataException();
                    }
                } finally { lifetime.Cancel(); }
            },lifetime.Token);
            updates.Writer.TryWrite(true);
            var heartbeatAt = DateTimeOffset.UtcNow;
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    try {
                        await updates.Reader.ReadAsync(deadline.Token);
                        CoreSnapshot snapshot; lock (gate) snapshot=state.Capture();
                        foreach (var frame in CoreSnapshotWire.Encode(snapshot)) await socket.SendAsync(frame,WebSocketMessageType.Text,true,lifetime.Token);
                        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new CoreReady(1,CoreClientProtocol.Ready,snapshot.Generation,snapshot.Revision,false),OverlayProtocolJson.Options),WebSocketMessageType.Text,true,lifetime.Token);
                    } catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                    if (DateTimeOffset.UtcNow-heartbeatAt >= TimeSpan.FromSeconds(5)) {
                        await socket.SendAsync("{\"protocolVersion\":1,\"type\":\"heartbeat\"}"u8.ToArray(),WebSocketMessageType.Text,true,lifetime.Token);
                        heartbeatAt=DateTimeOffset.UtcNow;
                    }
                }
            }
            finally { lifetime.Cancel(); try { await receiver; } catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidDataException or JsonException or KeyNotFoundException) { } }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidDataException or JsonException or NotSupportedException or InvalidOperationException) { }
        finally {
            lifetime.Cancel();
            if (producer is not null) try { await producer; } catch (Exception e) when (e is HttpRequestException or WebSocketException or OperationCanceledException or InvalidDataException or RemoteAuthenticationRequiredException or RemoteResyncRequiredException) { }
            socket.Abort();
        }
    }
    finally { streamSlot.Release(); }
});
await app.RunAsync();

static async Task<byte[]> Receive(WebSocket socket,CancellationToken cancellation)
{
    using var bytes = new MemoryStream(); var buffer = new byte[4096];
    do {
        var frame = await socket.ReceiveAsync(buffer.AsMemory(),cancellation);
        if (frame.MessageType != WebSocketMessageType.Text || bytes.Length+frame.Count > buffer.Length) throw new InvalidDataException("Invalid Core control frame.");
        bytes.Write(buffer,0,frame.Count);
        if (frame.EndOfMessage) return bytes.ToArray();
    } while (true);
}

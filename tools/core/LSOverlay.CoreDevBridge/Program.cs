using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using LSOverlay.Backend.CoreClient;
using LSOverlay.CoreDevBridge;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

if (args is ["--self-test"]) { await BridgeTests.Run(); return; }
if (args.Length != 0) throw new InvalidOperationException("Use the isolated deployment configuration.");
if (!ulong.TryParse(Environment.GetEnvironmentVariable("CORE_DEV_CHAT_CHANNEL"),out var channelId) || channelId == 0)
    throw new InvalidOperationException("An explicit existing authorized development chat channel is required.");
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // No headers, claim bodies, content, exception URLs or tokens in logs.
builder.WebHost.ConfigureKestrel(options => { options.Limits.MaxRequestBodySize = 4096; options.Limits.MaxConcurrentConnections = 8; });
var app = builder.Build();
var telemetry = new BridgeTelemetry();
var mediaEnabled=Environment.GetEnvironmentVariable("CORE_DEV_MEDIA")=="1";
using var mediaProxy=mediaEnabled ? new PrivateMediaProxy(telemetry) : null;
AuthorizedMedia? activeMedia=null;
Func<string,int,bool>? selectChannel=null;
using var streamSlot = new SemaphoreSlim(1,1);
app.UseWebSockets();
app.Use(async (context,next) => {
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Headers.ContainsKey("Origin") || context.Request.QueryString.HasValue ||
        context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 400; return; }
    await next();
});
app.MapGet("/healthz",() => Results.Json(new { status="ok",environment="isolated-core-dev",productionConfigurationChanged=false }));
app.MapGet("/core-dev/diagnostics",()=>Results.Json(telemetry.Snapshot()));
app.MapGet("/core-dev/manifest",() => Results.Json(new { readOnly=true,authOrigin=ReadOnlyPolicy.Origin,liveDiscord=true,
    mediaDelivery=mediaEnabled,channelSelection=true,maximumConnections=1,maximumSessionMinutes=30 }));
app.MapPost("/core-dev/channel/{slot:int}",(HttpContext context,int slot)=>{
    var headers=context.Request.Headers.Authorization;
    if(headers.Count!=1 || headers[0] is not string value || !value.StartsWith("Bearer ",StringComparison.Ordinal) || !ReadOnlyPolicy.IsToken(value[7..]))return Results.StatusCode(401);
    return Volatile.Read(ref selectChannel)?.Invoke(value[7..],slot)==true?Results.Json(new {accepted=true}):Results.StatusCode(403);
});
if (mediaProxy is not null) app.MapGet("/api/v1/core/media/{id}/{width:int}/{height:int}",(HttpContext context,string id,int width,int height)=>mediaProxy.Serve(context,Volatile.Read(ref activeMedia),id,width,height));
app.MapGet("/api/v1/core/stream",(Delegate)(async Task (HttpContext context) => {
    var header = context.Request.Headers.Authorization;
    if (header.Count != 1 || header[0] is not string auth || !auth.StartsWith("Bearer ",StringComparison.Ordinal) || !ReadOnlyPolicy.IsToken(auth[7..]))
    { context.Response.StatusCode=401; return; }
    if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains(OverlayTransportProtocol.WebSocketSubprotocol))
    { context.Response.StatusCode=400; return; }
    if (!streamSlot.Wait(0)) { context.Response.StatusCode=429; return; }
    telemetry.Count(BridgeSignal.Connections); telemetry.Count(BridgeSignal.Active);
    try
    {
        var token = auth[7..]; // Per-session RAM only; never copied to disk/config/environment.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        lifetime.CancelAfter(TimeSpan.FromMinutes(30));
        using var http = new HttpClient(new ReadOnlyHandler(new SocketsHttpHandler { AllowAutoRedirect=false,UseCookies=false,UseProxy=false },telemetry)) { Timeout=TimeSpan.FromSeconds(15) };
        await using var remote = new LSOverlayRemoteClient(new Uri(ReadOnlyPolicy.Origin),http);
        BootstrapResponse identity;
        try { identity = await remote.GetBootstrapAsync(token,lifetime.Token); }
        catch (RemoteAuthenticationRequiredException) { context.Response.StatusCode=401; return; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { context.Response.StatusCode=503; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync(OverlayTransportProtocol.WebSocketSubprotocol);
        using var abort = lifetime.Token.Register(socket.Abort);
        var gate = new object(); RelayState? state=null;
        var channels=new ChannelSelection();
        var catalogLookup=new InFlightLookup<ChatChannelCatalogResponse>(cancel=>remote.GetChatChannelsAsync(token,cancel),lifetime.Token);
        var salesLookup=new InFlightLookup<SalesBootstrapResponse>(cancel=>remote.GetSalesBootstrapAsync(token,cancel),lifetime.Token);
        ulong selectedChannel=channelId;
        var media=mediaEnabled ? new AuthorizedMedia(identity.SelfDiscordUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),token,
            message=> {lock(gate) return state!.ReadableChannel(message);},
            async (channel,message,cancel)=> {
                if (!ulong.TryParse(channel,out var id) || !ulong.TryParse(message,out var messageId)) return false;
                // Authorization follows the currently committed/readable chat,
                // not a requested destination whose bootstrap is still pending.
                bool committedChat;lock(gate)committedChat=id==state!.ChatChannel;
                if (committedChat) return (await catalogLookup.Get(cancel)).Channels.Any(item=>item.ChannelId==id);
                var sales=await salesLookup.Get(cancel);
                return sales.Channel.ChannelId==id && sales.RecentMessages.Any(item=>item.MessageId==messageId);
            },lifetime.Token) : null;
        // Hints only: this bridge still cannot write. An explicitly enabled
        // native client sends commands directly to the existing HTTPS issuer.
        state=new RelayState(identity,media) {Channels=channels,IncludeSalesActionHints=true}; Volatile.Write(ref activeMedia,media);
        var updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true });
        var chatRequests=Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true });
        var salesRequests=Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true });
        var requestedSlot=-1;
        var tokenHash=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));
        Volatile.Write(ref selectChannel,(candidate,slot)=>{
            if(lifetime.IsCancellationRequested || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(tokenHash,System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(candidate))))return false;
            try {channels.Resolve(slot);}catch(UnauthorizedAccessException){return false;}
            Interlocked.Exchange(ref requestedSlot,slot);return chatRequests.Writer.TryWrite(true);
        });
        void Change(Action action,string stage="state")
        {
            try { lock (gate) action(); updates.Writer.TryWrite(true); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or InvalidOperationException) { telemetry.Failure(stage,e); lifetime.Cancel(); }
        }
        remote.ChatChannelReady += value => { telemetry.Count(BridgeSignal.ChatBootstrap); Change(() => {media?.RevokeChannel(state.ChatChannel.ToString(System.Globalization.CultureInfo.InvariantCulture));media?.RevokeChannel(value.Channel.ChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture));state.Chat(value);},"chat-bootstrap"); };
        remote.ChatMutationReceived += value => { telemetry.Count(BridgeSignal.ChatMutation); Change(() => {media?.Revoke(value.MessageId.ToString());state.Chat(value);},"chat-mutation"); };
        remote.ChatStreamStatusChanged += (channel,status) => {
            Change(() => state.ChatState(channel,status));
            if (channel==Volatile.Read(ref selectedChannel) && status==OverlayTransportProtocol.ChatResyncRequired) chatRequests.Writer.TryWrite(true);
        };
        remote.SalesReady += value => { telemetry.Count(BridgeSignal.SalesBootstrap); telemetry.Sales(value); Change(() => {media?.RevokeChannel(value.Channel.ChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture));state.Sales(value);},"sales-bootstrap"); };
        remote.SalesMutationReceived += value => { telemetry.Count(BridgeSignal.SalesMutation); Change(() => {media?.Revoke(value.MessageId.ToString());state.Sales(value);},"sales-mutation"); };
        remote.SalesStreamStatusChanged += value => {
            Change(() => state.SalesState(value));
            if (value==OverlayTransportProtocol.SalesResyncRequired) salesRequests.Writer.TryWrite(true);
        };
        remote.HostPresenceChanged += value => { telemetry.Count(BridgeSignal.Presence); Change(() => state.Presence(value)); };
        Task? producer=null;
        try
        {
            using var helloDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); helloDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            var hello = JsonSerializer.Deserialize<CoreSessionStart>(await Receive(socket,helloDeadline.Token),OverlayProtocolJson.Options) ?? throw new InvalidDataException();
            CoreSnapshotWire.ValidateHello(hello);
            telemetry.Count(BridgeSignal.Hello);
            // Reconnect starts a fresh viewer-scoped projection, never false-resumes
            // a stale previous process or upstream permission generation.
            producer = Task.Run(async () => {
                try {
                    var catalog = await catalogLookup.Get(lifetime.Token);channels.Update(catalog);
                    if(channels.Describe(selectedChannel).Slot<0)selectedChannel=channels.Resolve(channels.Describe(0).AvailableSlots.FirstOrDefault(-1));
                    var chat = await remote.GetChatBootstrapAsync(token,selectedChannel,lifetime.Token);
                    Change(() => state.Chat(chat),"initial-chat");
                    var channelSwitches = Channel.CreateBounded<ChatBootstrapResponse>(1);
                    var salesResyncs = Channel.CreateBounded<SalesBootstrapResponse>(1);
                    // Independent bounded workers: slow Sales HTTP never blocks
                    // Chat delivery/heartbeat. No periodic history polling.
                    async Task Pump(bool sales) {
                        var requests=sales ? salesRequests : chatRequests;
                        while (await requests.Reader.WaitToReadAsync(lifetime.Token)) {
                            requests.Reader.TryRead(out _);
                            var userRequest=false;var failed=false;var switchTimer=System.Diagnostics.Stopwatch.StartNew();
                            try {
                                if (sales) await salesResyncs.Writer.WriteAsync(await salesLookup.Get(lifetime.Token),lifetime.Token);
                                else {
                                    var slot=Interlocked.Exchange(ref requestedSlot,-1);
                                    userRequest=slot>=0;
                                    if(slot>=0) {
                                        var next=channels.Resolve(slot);
                                        bool needed;lock(gate)needed=ChannelSelection.NeedsBootstrap(next,Volatile.Read(ref selectedChannel),state.ChatChannel,state.ChatStatus);
                                        if(!needed)continue;
                                        channels.Update(await catalogLookup.Get(lifetime.Token));next=channels.Resolve(slot);
                                        // Keep old stream cursor valid until the new bootstrap is handed to the SDK.
                                        Volatile.Write(ref selectedChannel,next);
                                    }
                                    await channelSwitches.Writer.WriteAsync(await remote.GetChatBootstrapAsync(token,Volatile.Read(ref selectedChannel),lifetime.Token),lifetime.Token);
                                    if(userRequest)telemetry.Timing("channelBootstrap",switchTimer.Elapsed.TotalMilliseconds);
                                }
                            } catch (HttpRequestException e) { failed=true;telemetry.Failure(sales ? "sales-http" : "chat-http",e); Change(() => { if (sales) state.SalesState(OverlayTransportProtocol.SalesFailed); else state.ChatState(state.ChatChannel,OverlayTransportProtocol.ChatFailed); }); }
                            catch (UnauthorizedAccessException) {failed=true;Change(()=>state.ChatState(state.ChatChannel,OverlayTransportProtocol.ChatAuthorizationUnavailable));}
                            catch (OperationCanceledException e) when (!lifetime.IsCancellationRequested) { failed=true;telemetry.Failure(sales ? "sales-timeout" : "chat-timeout",e); Change(() => { if (sales) state.SalesState(OverlayTransportProtocol.SalesFailed); else state.ChatState(state.ChatChannel,OverlayTransportProtocol.ChatFailed); }); }
                            catch (RemoteAuthenticationRequiredException) { lifetime.Cancel(); }
                            if(ChannelSelection.NeedsCooldown(sales,userRequest,failed))await Task.Delay(1000,lifetime.Token);
                        }
                    }
                    var chatPump=Pump(false); var salesPump=Pump(true); salesRequests.Writer.TryWrite(true);
                    try { await remote.StreamIndependentAsync(token,chat,channelSwitches.Reader,salesResyncs.Reader,
                        presence => Change(() => state.Presence(presence)),() => telemetry.Count(BridgeSignal.UpstreamConnected),lifetime.Token); }
                    catch (Exception e) when (e is not OperationCanceledException) { telemetry.Failure("upstream-stream",e); throw; }
                    finally { lifetime.Cancel(); try { await Task.WhenAll(chatPump,salesPump); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { } }
                } finally { lifetime.Cancel(); }
            },lifetime.Token);
            var receiver = Task.Run(async () => {
                try {
                    while (!lifetime.IsCancellationRequested) {
                        using var ackDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); ackDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                        using var message = JsonDocument.Parse(await Receive(socket,ackDeadline.Token));
                        if (message.RootElement.GetProperty("type").GetString() != OverlayTransportProtocol.HeartbeatAck) throw new InvalidDataException();
                        telemetry.Count(BridgeSignal.HeartbeatAck);
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
                        telemetry.Count(BridgeSignal.Snapshots);
                    } catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                    if (DateTimeOffset.UtcNow-heartbeatAt >= TimeSpan.FromSeconds(5)) {
                        await socket.SendAsync("{\"protocolVersion\":1,\"type\":\"heartbeat\"}"u8.ToArray(),WebSocketMessageType.Text,true,lifetime.Token);
                        heartbeatAt=DateTimeOffset.UtcNow;
                    }
                }
            }
            finally { lifetime.Cancel(); try { await receiver; } catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidDataException or JsonException or KeyNotFoundException) { } }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidDataException or JsonException or NotSupportedException or InvalidOperationException) { if (!lifetime.IsCancellationRequested) telemetry.Failure("downstream-stream",e); }
        finally {
            lifetime.Cancel();
            if (producer is not null) try { await producer; } catch (Exception e) when (e is HttpRequestException or WebSocketException or OperationCanceledException or InvalidDataException or RemoteAuthenticationRequiredException or RemoteResyncRequiredException) { }
            socket.Abort();
        }
    }
    finally { Volatile.Write(ref selectChannel,null);Volatile.Write(ref activeMedia,null); telemetry.Count(BridgeSignal.Active,-1); streamSlot.Release(); }
}));
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

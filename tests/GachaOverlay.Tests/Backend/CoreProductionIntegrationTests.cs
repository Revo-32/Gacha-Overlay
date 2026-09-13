using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LSOverlay.Backend.CoreClient;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Sales;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace GachaOverlay.Tests.Backend;

public sealed partial class M93KestrelChatIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreProduction_ExpiredOrReplacedCredentialClosesExistingSocket(bool expired)
    {
        var now=DateTimeOffset.UtcNow;
        await using var fixture=await ChatFixture.StartAsync(core:true,credentialClock:()=>now);
        var installation=Guid.NewGuid();var token=fixture.Credentials.Issue(installation,456,123).AccessToken;
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(22));
        using var socket=await ConnectCore(fixture.BaseUri,token,cancel.Token);
        await ReadCore(socket,s=>s.ChatConnectionState=="live",cancel.Token);
        if(expired)now=now.AddDays(181);else fixture.Credentials.Issue(installation,456,123);
        using var http=new HttpClient{BaseAddress=fixture.BaseUri};http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        Assert.Equal(HttpStatusCode.Unauthorized,(await http.PostAsync("api/v1/core/channel/0",null,cancel.Token)).StatusCode);
        await Assert.ThrowsAsync<WebSocketException>(()=>ReadCore(socket,_=>false,cancel.Token));
        await Wait211Async(()=>fixture.Services.GetRequiredService<RemoteConnectionLimiter>().Active==0);
    }

    [Fact]
    public async Task CoreProduction_SlowSalesDoesNotBlockChatAndRevocationClearsContent()
    {
        await using var fixture=await ChatFixture.StartAsync(core:true);
        var source=(LoopbackChatSource)fixture.Services.GetRequiredService<IChatDiscordSource>();
        source.SalesBarrier=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token=fixture.Credentials.Issue(Guid.NewGuid(),456,123).AccessToken;
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket=await ConnectCore(fixture.BaseUri,token,cancel.Token);
        var before=await ReadCore(socket,s=>s.ChatConnectionState=="live",cancel.Token);
        Assert.Null(before.Sales.Actions);
        fixture.Streams.PublishUpsert(OverlayTransportProtocol.ChatMessageCreate,Message(105,ChannelSelection.Ids[0]));
        var live=await ReadCore(socket,s=>s.Chat.Any(m=>m.Id=="105"),cancel.Token);
        Assert.Null(live.Sales.Actions);
        source.SalesBarrier.TrySetResult();
        await ReadCore(socket,s=>s.Sales.Actions is not null,cancel.Token);
        source.RejectAccess=true;
        fixture.Services.GetRequiredService<IChatAuthorizationService>().InvalidateGuild(123);
        fixture.Streams.PublishResyncRequired(ChannelSelection.Ids[0]);
        fixture.Services.GetRequiredService<ActiveSalesStreamRegistry>().PublishResyncRequired();
        var revoked=await ReadCore(socket,s=>s.ChatConnectionState=="unavailable" && s.Sales.Actions is null,cancel.Token);
        Assert.Empty(revoked.Chat);Assert.Empty(revoked.Sales.Queue);
        socket.Abort();
    }

    [Fact]
    public async Task CoreProduction_TwoUsersIndependentChannels_ReplayAndReconnectCleanly()
    {
        await using var fixture=await ChatFixture.StartAsync(core:true);
        var first=fixture.Credentials.Issue(Guid.NewGuid(),456,123).AccessToken;
        var second=fixture.Credentials.Issue(Guid.NewGuid(),457,123).AccessToken;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var a=await ConnectCore(fixture.BaseUri,first,timeout.Token);
        using var b=await ConnectCore(fixture.BaseUri,second,timeout.Token);
        var one=await ReadCore(a,s=>s.ChatConnectionState=="live" && s.Sales.Actions is not null,timeout.Token);
        var two=await ReadCore(b,s=>s.ChatConnectionState=="live" && s.Sales.Actions is not null,timeout.Token);
        Assert.Equal("456",one.SelfUserId);Assert.Equal("457",two.SelfUserId);
        Assert.NotEqual(one.Generation,two.Generation);Assert.Equal(0,one.ChatSelection!.Slot);Assert.Equal(0,two.ChatSelection!.Slot);
        using var http=new HttpClient {BaseAddress=fixture.BaseUri};http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",first);
        Assert.Equal(HttpStatusCode.OK,(await http.PostAsync("api/v1/core/channel/1",null,timeout.Token)).StatusCode);
        var changed=await ReadCore(a,s=>s.ChatSelection?.Slot==1 && s.ChatConnectionState=="live",timeout.Token);
        fixture.Streams.PublishUpsert(OverlayTransportProtocol.ChatMessageCreate,Message(101,ChannelSelection.Ids[0]));
        var other=await ReadCore(b,s=>s.Chat.Any(m=>m.Id=="101"),timeout.Token);
        Assert.Equal(0,other.ChatSelection!.Slot);
        fixture.Streams.PublishUpsert(OverlayTransportProtocol.ChatMessageCreate,Message(102,ChannelSelection.Ids[1]));
        changed=await ReadCore(a,s=>s.Chat.Any(m=>m.Id=="102"),timeout.Token);
        Assert.DoesNotContain(changed.Chat,m=>m.Id=="101");
        Assert.Equal(HttpStatusCode.Forbidden,(await http.PostAsync("api/v1/core/channel/8",null,timeout.Token)).StatusCode);
        a.Abort();b.Abort();
        await Wait211Async(()=>fixture.Services.GetRequiredService<RemoteConnectionLimiter>().Active==0);
        using var reconnected=await ConnectCore(fixture.BaseUri,first,timeout.Token);
        var again=await ReadCore(reconnected,s=>s.ChatConnectionState=="live" && s.Sales.Actions is not null,timeout.Token);
        Assert.NotEqual(one.Generation,again.Generation);Assert.Equal("456",again.SelfUserId);
        reconnected.Abort();
        await Wait211Async(()=>fixture.Services.GetRequiredService<RemoteConnectionLimiter>().Active==0 && fixture.Services.GetRequiredService<RemotePublicationHub>().ActiveSubscriptions==0);
    }

    [Fact]
    public async Task CoreProduction_UnauthenticatedBrowserAndUnknownMediaFailClosed()
    {
        await using var fixture=await ChatFixture.StartAsync(core:true);
        using var http=new HttpClient {BaseAddress=fixture.BaseUri};
        Assert.Equal(HttpStatusCode.Unauthorized,(await http.PostAsync("api/v1/core/channel/0",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync("api/v1/core/media/"+new string('a',48)+"/64/64")).StatusCode);
        http.DefaultRequestHeaders.Add("Origin","https://example.com");
        Assert.Equal(HttpStatusCode.BadRequest,(await http.GetAsync("api/v1/core/manifest")).StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.BadRequest,(await http.GetAsync("api/v1/core/manifest?token=synthetic")).StatusCode);
        var token=fixture.Credentials.Issue(Guid.NewGuid(),456,123).AccessToken;
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket=await ConnectCore(fixture.BaseUri,token,cancel.Token);
        await ReadCore(socket,s=>s.ChatConnectionState=="live",cancel.Token);
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync("api/v1/core/media/"+new string('a',48)+"/64/64",cancel.Token)).StatusCode);
        using var duplicate=new ClientWebSocket();duplicate.Options.SetRequestHeader("Authorization","Bearer "+token);duplicate.Options.AddSubProtocol(OverlayTransportProtocol.WebSocketSubprotocol);
        await Assert.ThrowsAsync<WebSocketException>(()=>duplicate.ConnectAsync(new UriBuilder(fixture.BaseUri){Scheme="ws",Path="api/v1/core/stream"}.Uri,cancel.Token));
        socket.Abort();
    }

    private static async Task<ClientWebSocket> ConnectCore(Uri origin,string token,CancellationToken cancel)
    {
        var socket=new ClientWebSocket();socket.Options.SetRequestHeader("Authorization","Bearer "+token);socket.Options.AddSubProtocol(OverlayTransportProtocol.WebSocketSubprotocol);
        await socket.ConnectAsync(new UriBuilder(origin){Scheme="ws",Path="api/v1/core/stream"}.Uri,cancel);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"type\":\"core_session_start_v1\",\"capabilities\":[\"core_render_v1\"]}"),WebSocketMessageType.Text,true,cancel);
        return socket;
    }
    private static async Task<CoreSnapshot> ReadCore(ClientWebSocket socket,Func<CoreSnapshot,bool> predicate,CancellationToken cancel)
    {
        using var payload=new MemoryStream();var frame=new byte[64*1024];string? generation=null;long revision=0;int next=0,total=0;
        while(true)
        {
            var used=0;WebSocketReceiveResult received;
            do {received=await socket.ReceiveAsync(new ArraySegment<byte>(frame,used,frame.Length-used),cancel);Assert.Equal(WebSocketMessageType.Text,received.MessageType);used+=received.Count;}while(!received.EndOfMessage);
            using var json=JsonDocument.Parse(frame.AsMemory(0,used));var type=json.RootElement.GetProperty("type").GetString();
            if(type=="heartbeat") {await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"type\":\"heartbeat_ack\"}"),WebSocketMessageType.Text,true,cancel);continue;}
            if(type==CoreClientProtocol.SnapshotChunk)
            {
                var chunk=JsonSerializer.Deserialize<CoreSnapshotChunk>(frame.AsSpan(0,used),OverlayProtocolJson.Options)!;
                Assert.Equal(next++,chunk.Index);generation=chunk.Generation;revision=chunk.Revision;total=chunk.Count;
                await payload.WriteAsync(Convert.FromBase64String(chunk.PayloadBase64),cancel);continue;
            }
            Assert.Equal(CoreClientProtocol.Ready,type);Assert.Equal(total,next);Assert.True(next>0);
            var ready=JsonSerializer.Deserialize<CoreReady>(frame.AsSpan(0,used),OverlayProtocolJson.Options)!;
            Assert.Equal(generation,ready.Generation);Assert.Equal(revision,ready.Revision);Assert.False(ready.Resumed);
            var snapshot=JsonSerializer.Deserialize<CoreSnapshot>(payload.ToArray(),OverlayProtocolJson.Options)!;
            if(predicate(snapshot))return snapshot;
            payload.SetLength(0);next=0;
        }
    }
}

using LSOverlay.Protocol;

namespace LSOverlay.CoreDevBridge;
internal static class BridgeTests
{
    public static void Run()
    {
        var assertions=0;
        void Check(bool condition) { ++assertions; if (!condition) throw new InvalidOperationException("Bridge assertion failed: "+assertions); }
        void Reject(Action action) { try { action(); } catch (InvalidDataException) { ++assertions; return; } throw new InvalidOperationException("Unsafe state accepted."); }
        foreach (var path in new[] {"/api/v1/bootstrap","/api/v1/chat/channels"}) Check(ReadOnlyPolicy.Allows(HttpMethod.Get,new Uri(ReadOnlyPolicy.Origin+path)));
        Check(ReadOnlyPolicy.Allows(HttpMethod.Post,new Uri(ReadOnlyPolicy.Origin+"/api/v1/chat/bootstrap")));
        Check(ReadOnlyPolicy.Allows(HttpMethod.Post,new Uri(ReadOnlyPolicy.Origin+"/api/v1/sales/bootstrap")));
        foreach (var method in new[] {HttpMethod.Post,HttpMethod.Put,HttpMethod.Patch,HttpMethod.Delete})
            Check(!ReadOnlyPolicy.Allows(method,new Uri(ReadOnlyPolicy.Origin+"/api/v1/sales/status")));
        foreach (var url in new[] {"https://discord.com/api/v10/channels/1/messages","http://127.0.0.1:5188/api/v1/bootstrap","https://overlay.revo32.cloud.evil.test/api/v1/bootstrap",
            ReadOnlyPolicy.Origin+"/api/v1/bootstrap?token=x",ReadOnlyPolicy.Origin+"/api/v1/bootstrap#fragment",ReadOnlyPolicy.Origin+"/api/v1/auth/discord/sessions"})
            Check(!ReadOnlyPolicy.Allows(HttpMethod.Get,new Uri(url)));
        Check(ReadOnlyPolicy.IsToken("lso_"+new string('a',43)));
        foreach (var value in new[] {"",new string('a',47),"lso_"+new string('a',42),"lso_"+new string('a',42)+"\n"}) Check(!ReadOnlyPolicy.IsToken(value));
        var now=DateTimeOffset.UtcNow;
        var identity=new BootstrapResponse(1,"presence",0,77,new[] {new HostPresenceSnapshot(1,HostPresenceState.GtaOnline,12,30,now)});
        var state=new RelayState(identity);
        ChatMessage Message(ulong id,ulong channel=10,ulong author=77) => new(id,1,channel,"default",0,new(author,"fixture","검증",null,false,false),"합성 메시지",now.AddMilliseconds(id),null,false,false,false,0,
            Array.Empty<ChatEmoji>(),Array.Empty<ChatAttachment>(),Array.Empty<ChatEmbed>(),Array.Empty<ChatMention>(),Array.Empty<ChatSticker>(),Array.Empty<ChatForwardSnapshot>(),null,Array.Empty<ChatComponent>(),null);
        var descriptor=new ChatChannelDescriptor(1,10,"synthetic",0,false);
        var chat=new ChatBootstrapResponse(1,descriptor,"chat",0,Enumerable.Range(1,25).Select(i=>Message((ulong)i)).ToArray());
        state.Chat(chat); var snapshot=state.Capture();
        Check(snapshot.Chat.Count==20 && snapshot.Chat[0].Id=="6"); Check(snapshot.ChatConnectionState=="live");
        state.Chat(new ChatMutationEnvelope(1,"chat",1,OverlayTransportProtocol.ChatMessageCreate,10,26,Message(26))); Check(state.Capture().Chat.Last().Id=="26");
        state.Chat(new ChatMutationEnvelope(1,"chat",2,OverlayTransportProtocol.ChatMessageUpdate,10,26,Message(26) with {Content="수정"})); Check(state.Capture().Chat.Last().Runs[0].Text=="수정");
        state.Chat(new ChatMutationEnvelope(1,"chat",3,OverlayTransportProtocol.ChatMessageDelete,10,26,null)); Check(state.Capture().Chat.Count==19);
        Reject(()=>state.Chat(new ChatMutationEnvelope(1,"chat",5,OverlayTransportProtocol.ChatMessageDelete,10,25,null)));
        Reject(()=>state.Chat(new ChatMutationEnvelope(1,"old",4,OverlayTransportProtocol.ChatMessageDelete,10,25,null)));
        Reject(()=>state.Chat(new ChatMutationEnvelope(1,"chat",4,OverlayTransportProtocol.ChatMessageCreate,10,27,Message(27,11))));
        state.ChatState(10,OverlayTransportProtocol.ChatResyncRequired); Check(state.Capture().Chat.Count==19 && state.Capture().ChatConnectionState=="recovering");
        state.ChatState(10,OverlayTransportProtocol.ChatAccessRevoked); Check(state.Capture().Chat.Count==0);
        Reject(()=>state.Presence(identity with {SelfDiscordUserId=88}));
        var salesDescriptor=descriptor with {ChannelId=20};
        SalesCompletionObservation Evidence(ulong id,bool sold=false) => new(id,sold,false,SalesEvidenceCoverage.Complete,now);
        var sales=new SalesBootstrapResponse(1,salesDescriptor,"sales",0,new[] {Message(100,20),Message(101,20,88)},new[] {Evidence(100),Evidence(101)},SalesBootstrapCoverage.Complete);
        state.Sales(sales); snapshot=state.Capture(); Check(snapshot.Sales.Queue.Count==2); Check(snapshot.Sales.CurrentIsSelf); Check(snapshot.Sales.Presentation!.CompletionEnabledMessageIds.Count==0);
        state.Sales(new SalesMutationEnvelope(1,"sales",1,OverlayTransportProtocol.SalesCompletionEvidence,20,100,null,Evidence(100,true))); Check(state.Capture().Sales.CurrentMessageId=="101");
        state.Sales(new SalesMutationEnvelope(1,"sales",2,OverlayTransportProtocol.SalesCompletionEvidence,20,100,null,Evidence(100,false))); Check(state.Capture().Sales.CurrentMessageId=="100");
        state.SalesState(OverlayTransportProtocol.SalesResyncRequired); Check(state.Capture().Sales.Queue.Count==2 && !state.Capture().Sales.Presentation!.IsTrustedForNewPersonalAlert);
        Reject(()=>state.Sales(sales with {CompletionObservations=new[] {Evidence(100),Evidence(100)}}));
        Reject(()=>state.Sales(sales with {Coverage=SalesBootstrapCoverage.Truncated}));
        state.SalesState(OverlayTransportProtocol.SalesAccessRevoked); Check(state.Capture().Sales.Queue.Count==0);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {status="PASS",synthetic=true,assertions,discordWrites=0,upstreamCalls=0}));
    }
}

using LSOverlay.Protocol;
using LSOverlay.Backend.CoreClient;

namespace LSOverlay.CoreDevBridge;
internal static class BridgeTests
{
    public static async Task Run()
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
        var allowed=new ChannelSelection();
        Check(!ChannelSelection.NeedsBootstrap(10,10,10,"live"));Check(ChannelSelection.NeedsBootstrap(10,10,10,"recovering"));Check(ChannelSelection.NeedsBootstrap(20,10,10,"live"));
        Check(ChannelSelection.NeedsBootstrap(10,20,10,"live")); // Returning before the previous switch commits must not be dropped.
        Check(!ChannelSelection.NeedsCooldown(false,true,false));Check(ChannelSelection.NeedsCooldown(false,true,true));Check(ChannelSelection.NeedsCooldown(false,false,false));Check(ChannelSelection.NeedsCooldown(true,true,false));
        allowed.Update(new(1,ChannelSelection.Ids.Select((id,i)=>descriptor with {ChannelId=id,Name="decorated "+i}).Append(descriptor with {ChannelId=1417858541439680582,Name="메인"}).ToArray()));
        Check(allowed.Describe(0).AvailableSlots.SequenceEqual(Enumerable.Range(0,8)));
        for(var slot=0;slot<8;slot++){Check(allowed.Resolve(slot)==ChannelSelection.Ids[slot]);Check(allowed.Describe(ChannelSelection.Ids[slot]).Name==ChannelSelection.Names[slot]);}
        try {allowed.Resolve(8);throw new InvalidOperationException("Unlisted channel accepted");}catch(UnauthorizedAccessException){++assertions;}
        allowed.Update(new(1,new[]{descriptor with {ChannelId=ChannelSelection.Ids[2]}}));Check(allowed.Describe(0).AvailableSlots.SequenceEqual(new[]{2}));
        try {allowed.Resolve(0);throw new InvalidOperationException("Inaccessible channel accepted");}catch(UnauthorizedAccessException){++assertions;}
        var readGate=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);var reads=0;
        var lookup=new InFlightLookup<int>(_=>{++reads;return readGate.Task;},default);
        var waiting=Enumerable.Range(0,8).Select(_=>lookup.Get(default)).ToArray();Check(reads==1);readGate.SetResult(42);Check((await Task.WhenAll(waiting)).All(value=>value==42));
        await lookup.Get(default);Check(reads==2); // Completed authorization is never reused as a TTL cache.
        var cancelGate=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);using var callerCancellation=new CancellationTokenSource();
        var independent=new InFlightLookup<int>(_=>cancelGate.Task,default);var cancelled=independent.Get(callerCancellation.Token);var survivor=independent.Get(default);callerCancellation.Cancel();
        try {await cancelled;throw new InvalidOperationException("Caller cancellation ignored");}catch(OperationCanceledException){++assertions;}cancelGate.SetResult(1);Check(await survivor==1);
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
        var interactive=new RelayState(identity) {IncludeSalesActionHints=true};interactive.Sales(sales);
        var hints=interactive.Capture().Sales.Actions!;
        Check(hints.Generation=="sales" && hints.Sequence==0 && hints.Targets.Count==1);
        Check(hints.Targets[0] is {MessageId:"100",CanComplete:true,CanUndo:false,BotCompleted:false});
        interactive.Sales(new SalesMutationEnvelope(1,"sales",1,OverlayTransportProtocol.SalesCompletionEvidence,20,100,null,Evidence(100,true) with {BotCompletedMarkerPresent=true}));
        Check(interactive.Capture().Sales.Actions!.Targets[0] is {CanComplete:false,CanUndo:true,BotCompleted:true});
        interactive.Sales(new SalesMutationEnvelope(1,"sales",2,OverlayTransportProtocol.SalesCompletionEvidence,20,100,null,Evidence(100)));
        Check(interactive.Capture().Sales.Actions!.Targets[0] is {CanComplete:true,CanUndo:false,BotCompleted:false});
        interactive.Sales(new SalesMutationEnvelope(1,"sales",3,OverlayTransportProtocol.SalesCompletionEvidence,20,100,null,Evidence(100,true)));
        Check(interactive.Capture().Sales.Actions!.Targets[0] is {CanComplete:false,CanUndo:false,BotCompleted:false}); // Another user's marker cannot be undone.
        interactive.SalesState(OverlayTransportProtocol.SalesResyncRequired);Check(interactive.Capture().Sales.Actions is null);
        interactive.Sales(sales);interactive.Sales(new SalesMutationEnvelope(1,"sales",1,OverlayTransportProtocol.SalesMessageDelete,20,100,null,null));
        Check(interactive.Capture().Sales.Actions!.Targets.Count==0);
        interactive.SalesState(OverlayTransportProtocol.SalesAccessRevoked);Check(interactive.Capture().Sales.Actions is null);
        using var scope=new CancellationTokenSource(); var permitted=true;string? readable="10";var lookups=0;
        var media=new AuthorizedMedia("77","lso_"+new string('a',43),_=>readable,(_,_,_)=>{++lookups;return Task.FromResult(permitted);},scope.Token);
        async Task Denied(string id) {try {await media.Resolve(id,default);}catch(UnauthorizedAccessException) {++assertions;return;}throw new InvalidOperationException("Unauthorized media resolved.");}
        Check(media.Accepts("lso_"+new string('a',43)));Check(!media.Accepts("lso_"+new string('b',43)));
        var emoji=media.RegisterEmoji("1","123",false);var animated=media.RegisterEmoji("1","123",true);
        Check(emoji.Length==48 && animated.Length==48 && emoji!=animated);
        Check(await media.Resolve(emoji,default)=="https://cdn.discordapp.com/emojis/123.png");
        Check(await media.Resolve(animated,default)=="https://cdn.discordapp.com/emojis/123.gif");Check(lookups==2);
        Check(media.RegisterEmoji("1","123",false)==emoji);
        media.RevokeChannel("20");Check(await media.Resolve(emoji,default)=="https://cdn.discordapp.com/emojis/123.png");
        foreach (var source in new[] {"http://127.0.0.1/secret","https://evil.test/x.png","https://cdn.discordapp.com/emojis/123.png#bad","https://cdn.discordapp.com/attachments/1/2/%2fprivate.png"})
            Check(media.RegisterCanonical("1","attachment","x",source).StartsWith("unavailable-",StringComparison.Ordinal));
        permitted=false;await Denied(emoji);permitted=true;readable=null;await Denied(emoji);readable="10";
        media.RevokeChannel("10");await Denied(emoji);await Denied(animated);
        var replacement=media.RegisterEmoji("1","123",false);Check(replacement!=emoji);
        media.Revoke();await Denied(replacement);
        replacement=media.RegisterEmoji("1","123",false);scope.Cancel();await Denied(replacement);Check(!media.Accepts("lso_"+new string('a',43)));
        var permissionGate=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var racing=new AuthorizedMedia("77","lso_"+new string('a',43),_=>"10",(_,_,_)=>permissionGate.Task,default);
        var racingId=racing.RegisterEmoji("1","123",false);var resolving=racing.Resolve(racingId,default);racing.Revoke();permissionGate.SetResult(true);
        try {await resolving;throw new InvalidOperationException("Revocation race accepted.");}catch(UnauthorizedAccessException) {++assertions;}
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {status="PASS",synthetic=true,assertions,discordWrites=0,upstreamCalls=0}));
    }
}

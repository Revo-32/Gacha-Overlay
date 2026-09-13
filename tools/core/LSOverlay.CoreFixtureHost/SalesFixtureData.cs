using System.Text.Json;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Localization;
using LSOverlay.Backend.CoreClient;
using LSOverlay.Protocol;

namespace LSOverlay.CoreFixtureHost;
internal static class SalesFixtureData
{
    public static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        var created=DateTimeOffset.Parse("2026-09-13T12:00:00Z");
        var own=new SalesQueueEntry("100","fixture","77",created,"검증용 나",DiscordDisplayNameSource.GuildNickname,true,
            new("bunker","벙커","12345","bunker"),SaleObservationTrust.Trusted,DetailSource:"함께 판매합니다 <:cargo:12345> · 준비 완료");
        var other=own with {MessageId="101",AuthorId="88",DisplayName="앞 판매자",DetailSource="차례를 확인해 주세요.",Product=new("nightclub","나이트클럽","12345","nightclub")};
        var third=own with {MessageId="102",AuthorId="99",DisplayName="다음 판매자",DetailSource="세 번째 대기열입니다."};
        var queue=SalesQueueSnapshot.Empty with {Revision=1,ActiveItems=new[] {other,own,third},CurrentSeller=other,NextWaitingEntry=own,ActiveCount=3,WaitingCount=2,
            ObservationStatus=SalesObservationStatus.Live,IsObservationSourceAvailable=true,AuthenticatedUserId="77",UpdatedAt=created};
        var presenter=new CoreSalesPresentationProjection(CoreSalesPresentationProjection.Strings(new ResourceLocalizationService("ko")));
        var projection=new CoreSemanticProjection(new FixtureMedia());
        var chat=Enumerable.Range(0,20).Select(i => new NormalizedDiscordMessage((200+i).ToString(),"fixture-chat","88","synthetic","검증용 작성자",
            "채팅·판매·세션 통합 합성 화면입니다. "+i,created.AddSeconds(i),null,Array.Empty<DiscordCustomEmoji>(),Array.Empty<DiscordAttachmentMetadata>(),Array.Empty<DiscordEmbedMetadata>(),Array.Empty<DiscordMention>())).ToArray();
        var live=SalesFeatureHealthEvaluator.Evaluate(new(true,RemoteSalesPresentationPhase.Live,true,SalesCoverageState.Complete,created,3,3));
        var recovery=SalesFeatureHealthEvaluator.Evaluate(new(true,RemoteSalesPresentationPhase.Reconnecting,true,SalesCoverageState.Complete,created,3,3));
        void Write(string name,SalesQueueSnapshot state,SalesFeatureHealthSnapshot health,HostPresenceState presence=HostPresenceState.GtaOnline,int? players=12)
        {
            var snapshot=projection.Capture("synthetic-sales-v1",state.Revision,"77",chat,state,new[] {new HostPresenceSnapshot(1,presence,players,players is null ? null : 30,created),new HostPresenceSnapshot(2,HostPresenceState.Offline,null,null,created)});
            snapshot=presenter.Apply(snapshot,state,health); // read-only: no authorized action IDs
            File.WriteAllBytes(Path.Combine(directory,name+".json"),JsonSerializer.SerializeToUtf8Bytes(snapshot,OverlayProtocolJson.Options));
        }
        Write("next",queue,live);
        queue=queue with {Revision=2,ActiveItems=new[] {own,other,third},CurrentSeller=own,NextWaitingEntry=other}; Write("current",queue,live);
        Write("recovering",queue with {Revision=3,ObservationStatus=SalesObservationStatus.Resyncing},recovery,HostPresenceState.AwaitingPresence,null);
        queue=queue with {Revision=4,ActiveItems=new[] {other,third},CurrentSeller=other,NextWaitingEntry=third,ActiveCount=2,WaitingCount=1}; Write("completed",queue,live);
        Write("empty",queue with {Revision=5,ActiveItems=Array.Empty<SalesQueueEntry>(),CurrentSeller=null,NextWaitingEntry=null,ActiveCount=0,WaitingCount=0},live);
    }
    private sealed class FixtureMedia : ICoreMediaReferences { public string RegisterCanonical(string messageId,string kind,string identity,string? assetUrl) => "fixture-emoji"; }
}

using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Localization;
using LSOverlay.Backend.CoreClient;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests.Backend;
public sealed class CoreSalesM5Tests
{
    private static readonly SalesQueuePresentationStrings Text = CoreSalesPresentationProjection.Strings(new ResourceLocalizationService("ko"));
    private static readonly SalesFeatureHealthSnapshot Live = SalesFeatureHealthEvaluator.Evaluate(new(true,RemoteSalesPresentationPhase.Live,true,SalesCoverageState.Complete,DateTimeOffset.UtcNow,2,2));
    private static readonly SalesFeatureHealthSnapshot Recovering = SalesFeatureHealthEvaluator.Evaluate(new(true,RemoteSalesPresentationPhase.Reconnecting,true,SalesCoverageState.Complete,null,2,2));
    private static SalesQueueSnapshot Queue() {
        var a = new SalesQueueEntry("1","guild","77",DateTimeOffset.UtcNow,"나",DiscordDisplayNameSource.GuildNickname,true,new("bunker","벙커","1","bunker"),SaleObservationTrust.Trusted);
        var b = a with { MessageId="2",AuthorId="88",DisplayName="다음 사람" };
        return SalesQueueSnapshot.Empty with { Revision=3,CurrentSeller=a,NextWaitingEntry=b,ActiveItems=new[] {a,b},ActiveCount=2,WaitingCount=1,ObservationStatus=SalesObservationStatus.Live,IsObservationSourceAvailable=true };
    }
    private static CoreSnapshot Snapshot(SalesQueueSnapshot queue, string viewer="77", string generation="g") =>
        new CoreSemanticProjection(new NoMedia()).Capture(generation,1,viewer,Array.Empty<NormalizedDiscordMessage>(),queue,Array.Empty<HostPresenceSnapshot>());
    [Fact]
    public void CoreUsesFullCurrentAndNextPresentationAndReadOnlyByDefault()
    {
        var queue=Queue(); var projector=new CoreSalesPresentationProjection(Text);
        var own=projector.Apply(Snapshot(queue),queue,Live).Sales.Presentation!;
        Assert.Equal("CurrentTurnSelf",own.ContentMode); Assert.Equal(Text.CurrentTurnSelf,own.PrimaryText);
        Assert.DoesNotContain("벙커",own.PrimaryText); Assert.Empty(own.CompletionEnabledMessageIds);
        var next=projector.Apply(Snapshot(queue,"88"),queue,Live).Sales.Presentation!;
        Assert.Equal("NextTurnSelf",next.ContentMode); Assert.Equal(Text.NextTurnSelf,next.SecondaryText);
    }
    [Fact]
    public void RecoveryRetainsExistingAlertButDoesNotCreateNewAlert()
    {
        var queue=Queue(); var projector=new CoreSalesPresentationProjection(Text);
        projector.Apply(Snapshot(queue),queue,Live);
        var stale=queue with { ObservationStatus=SalesObservationStatus.Resyncing };
        var old=projector.Apply(Snapshot(stale),stale,Recovering).Sales.Presentation!;
        Assert.Equal("CurrentTurnSelf",old.ContentMode); Assert.False(old.IsTrustedForNewPersonalAlert); Assert.NotEmpty(old.StatusText);
        var fresh=new CoreSalesPresentationProjection(Text).Apply(Snapshot(stale),stale,Recovering).Sales.Presentation!;
        Assert.Equal("Normal",fresh.ContentMode);
        Assert.Equal("Normal",projector.Apply(Snapshot(stale,generation:"other"),stale,Recovering).Sales.Presentation!.ContentMode);
    }
    [Fact]
    public void CompletionRequiresOwnMessageAndLiveExplicitAuthorization()
    {
        var queue=Queue(); var ids=new HashSet<string> {"1","2"}; var projector=new CoreSalesPresentationProjection(Text);
        Assert.Equal(new[] {"1"},projector.Apply(Snapshot(queue),queue,Live,ids).Sales.Presentation!.CompletionEnabledMessageIds);
        Assert.Empty(projector.Apply(Snapshot(queue),queue,Recovering,ids).Sales.Presentation!.CompletionEnabledMessageIds);
        Assert.Equal(new[] {"2"},projector.Apply(Snapshot(queue,"88"),queue,Live,ids).Sales.Presentation!.CompletionEnabledMessageIds);
    }
    [Fact]
    public void CanonicalCompletionPromotesNextAndEmptyLiveQueueHides()
    {
        var queue=Queue(); var projector=new CoreSalesPresentationProjection(Text); projector.Apply(Snapshot(queue),queue,Live);
        var next=queue with { ActiveItems=new[] {queue.NextWaitingEntry!},CurrentSeller=queue.NextWaitingEntry,NextWaitingEntry=null,ActiveCount=1,WaitingCount=0 };
        Assert.Equal("CurrentTurnSelf",projector.Apply(Snapshot(next,"88"),next,Live).Sales.Presentation!.ContentMode);
        var empty=SalesQueueSnapshot.Empty with {ObservationStatus=SalesObservationStatus.Live};
        Assert.False(projector.Apply(Snapshot(empty),empty,Live).Sales.Presentation!.IsVisible);
    }
    private sealed class NoMedia : ICoreMediaReferences { public string RegisterCanonical(string messageId,string kind,string identity,string? assetUrl) => "unused"; }
}

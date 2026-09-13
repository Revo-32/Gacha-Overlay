using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Sales;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.CoreClient;

// One presenter per authenticated Core connection. Reuses Full's current/next,
// recovery/Last-Good and alert semantics; the native client never parses Sales.
public sealed class CoreSalesPresentationProjection(SalesQueuePresentationStrings strings)
{
    private SalesQueuePresentationState? _previous;
    private string? _viewer, _generation;
    public CoreSnapshot Apply(CoreSnapshot snapshot, SalesQueueSnapshot canonical, SalesFeatureHealthSnapshot health,
        IReadOnlySet<string>? authorizedCompletionIds = null)
    {
        if (_viewer != snapshot.SelfUserId || _generation != snapshot.Generation) _previous = null;
        _viewer = snapshot.SelfUserId; _generation = snapshot.Generation;
        // Never reuse a different viewer's flags from a shared canonical queue.
        var mine = canonical with
        {
            AuthenticatedUserId = snapshot.SelfUserId,
            CurrentSellerIsSelf = canonical.CurrentSeller?.AuthorId == snapshot.SelfUserId,
            NextSellerIsSelf = canonical.NextWaitingEntry?.AuthorId == snapshot.SelfUserId
        };
        var state = SalesQueuePresentationFactory.Create(new(mine,health,new(true,true,true,true),strings,"판매",
            double.MaxValue,SalesQueueFieldMeasurements.Empty,_previous,SalesQueueChangeContext.None,false,true,false));
        _previous = state;
        // Zero measurements/infinite budget request semantic field order only.
        // No real line breaks, widths, glyph coordinates or DPI are decided here.
        // Completion requires an existing authorization/evidence path to opt in;
        // read-only fixtures/bridges omit it and cannot dispatch Discord writes.
        var completion = authorizedCompletionIds is null || !mine.IsTrackingEnabled || health.State != SalesFeatureHealthState.Live || mine.ObservationStatus != SalesObservationStatus.Live
            ? Array.Empty<string>()
            : mine.ActiveItems.Where(entry => entry.AuthorId == snapshot.SelfUserId && authorizedCompletionIds.Contains(entry.MessageId)).Select(entry => entry.MessageId).ToArray();
        return snapshot with { Sales = snapshot.Sales with { Presentation = new(state.ContentMode.ToString(),state.HealthMode.ToString(),state.AccentKind.ToString(),
            state.IconKind.ToString(),state.PrimaryText,state.SecondaryText,state.StatusText,state.IsVisible,state.IsTrustedForNewPersonalAlert,completion) } };
    }
    public static SalesQueuePresentationStrings Strings(ILocalizationService localization) => new(
        localization["SalesHealthLiveAccessible"],localization["SalesHealthConnecting"],localization["SalesHealthResyncing"],
        localization["SalesHealthRemoteConnecting"],localization["SalesHealthRemoteSynchronizing"],localization["SalesHealthRemoteResyncing"],
        localization["SalesHealthRemoteReconnecting"],localization["SalesHealthPaused"],localization["SalesHealthDegraded"],
        localization["SalesHealthDisconnected"],localization["SalesHealthRemoteError"],localization["SalesCurrentSellerFormat"],
        localization["SalesWaitingCountFormat"],localization["SalesProductFormat"],localization["SalesNextSellerFormat"],localization["SalesQueueEmpty"],
        localization["SalesNoDisplayFields"],localization["SalesNextTurnSelf"],localization["SalesCurrentTurnSelf"],localization["SalesHealthRemoteUnavailable"],
        localization["SalesHealthRemoteAccessRevoked"],localization["SalesDetailRequired"]);
}

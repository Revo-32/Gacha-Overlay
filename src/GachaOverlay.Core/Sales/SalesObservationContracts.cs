namespace GachaOverlay.Core.Sales;

public enum SaleReactionOutcome
{
    NotObserved,
    Sold,
    NotSold,
}

public enum SalesObservationCompleteness
{
    Partial,
    Full,
}

public enum SalesCoverageState
{
    None,
    Partial,
    Complete,
}

public enum SalesObservationReason
{
    None,
    SalesTrackingDisabled,
    SourceNotReady,
    CoverageIncomplete,
}

public enum ManualSalesResyncResult
{
    Requested,
    Coalesced,
    TrackingDisabled,
    RemoteUnavailable,
}

public sealed record SaleReactionObservation(
    string MessageId,
    SaleReactionOutcome Outcome,
    bool HasTrustedEvidence,
    DateTimeOffset ObservedAt,
    long Generation,
    long? SourceRevision = null);

public sealed record SalesObservationBatch(
    long Generation,
    DateTimeOffset ObservedAt,
    SalesObservationStatus SensorStatus,
    bool IsTrusted,
    SalesObservationCompleteness Completeness,
    IReadOnlyCollection<SaleReactionObservation> Observations,
    SalesCoverageState Coverage = SalesCoverageState.None,
    SalesObservationReason StatusReason = SalesObservationReason.None,
    int TargetMessageCount = 0,
    int ObservedMessageCount = 0,
    int SoldCount = 0,
    int NotSoldCount = 0,
    int NotObservedCount = 0,
    long TargetSetRevision = 0);

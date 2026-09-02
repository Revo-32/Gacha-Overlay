namespace GachaOverlay.Core.Sales;

public enum SalesFeatureHealthState
{
    Disabled,
    Connecting,
    Resyncing,
    Live,
    Paused,
    Degraded,
    Disconnected,
    Error,
}

public enum SalesFeatureHealthReason
{
    None,
    SalesTrackingDisabled,
    CoveragePartial,
    InitialResyncRequired,
    TargetChannelNotSelected,
    ResyncInProgress,
    SensorFailure,
    DiscordDisconnected,
    RemoteSalesConnecting,
    RemoteSalesSynchronizing,
    RemoteSalesResyncing,
    RemoteSalesReconnecting,
    RemoteSalesAuthorizationUnavailable,
    RemoteSalesAccessRevoked,
    RemoteSalesUnavailable,
}

public enum EffectiveSalesSource
{
    RemoteStarting,
    RemotePrimary,
    RemoteRecovering,
    RemoteUnavailable,
    AccessRevoked,
}

public enum RemoteSalesPresentationPhase
{
    Disabled,
    Connecting,
    Bootstrapping,
    Live,
    Resyncing,
    Reconnecting,
    AuthorizationUnavailable,
    CredentialUnavailable,
    AccessRevoked,
    ChannelUnavailable,
    Failed,
}

public sealed record SalesFeatureHealthInput(
    bool SalesTrackingEnabled,
    RemoteSalesPresentationPhase RemotePhase,
    bool RemoteCanonicalReady,
    SalesCoverageState Coverage,
    DateTimeOffset? LastCompleteResyncAt,
    int TargetMessageCount,
    int ObservedMessageCount);

public sealed record SalesFeatureHealthSnapshot(
    SalesFeatureHealthState State,
    SalesFeatureHealthReason Reason,
    SalesObservationReason SensorReason,
    SalesObservationStatus SensorStatus,
    SalesCoverageState Coverage,
    bool IsFullyTrustworthy,
    DateTimeOffset? LastCompleteResyncAt,
    int TargetMessageCount,
    int ObservedMessageCount,
    EffectiveSalesSource EffectiveSource = EffectiveSalesSource.RemoteStarting,
    RemoteSalesPresentationPhase RemotePhase = RemoteSalesPresentationPhase.Disabled)
{
    public static SalesFeatureHealthSnapshot Disabled { get; } = new(
        SalesFeatureHealthState.Disabled,
        SalesFeatureHealthReason.SalesTrackingDisabled,
        SalesObservationReason.SalesTrackingDisabled,
        SalesObservationStatus.Disabled,
        SalesCoverageState.None,
        false,
        null,
        0,
        0,
        EffectiveSalesSource.RemoteUnavailable);
}

public static class SalesFeatureHealthEvaluator
{
    public static SalesFeatureHealthSnapshot Evaluate(SalesFeatureHealthInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var decision = SalesAcquisitionPolicy.Evaluate(new SalesAcquisitionPolicyInput(
            input.SalesTrackingEnabled,
            input.RemotePhase,
            input.RemoteCanonicalReady));

        if (!input.SalesTrackingEnabled)
        {
            return Create(
                SalesFeatureHealthState.Disabled,
                SalesFeatureHealthReason.SalesTrackingDisabled,
                SalesObservationStatus.Disabled);
        }

        if (decision.EffectiveSource == EffectiveSalesSource.AccessRevoked)
        {
            return Create(
                SalesFeatureHealthState.Error,
                SalesFeatureHealthReason.RemoteSalesAccessRevoked,
                SalesObservationStatus.Unavailable);
        }

        if (decision.EffectiveSource == EffectiveSalesSource.RemoteUnavailable)
        {
            return Create(
                SalesFeatureHealthState.Error,
                input.RemotePhase == RemoteSalesPresentationPhase.AuthorizationUnavailable
                    ? SalesFeatureHealthReason.RemoteSalesAuthorizationUnavailable
                    : SalesFeatureHealthReason.RemoteSalesUnavailable,
                SalesObservationStatus.Unavailable);
        }

        if (decision.EffectiveSource == EffectiveSalesSource.RemoteStarting)
        {
            return Create(
                SalesFeatureHealthState.Connecting,
                input.RemotePhase == RemoteSalesPresentationPhase.Bootstrapping
                    ? SalesFeatureHealthReason.RemoteSalesSynchronizing
                    : SalesFeatureHealthReason.RemoteSalesConnecting,
                SalesObservationStatus.Resyncing);
        }

        if (decision.EffectiveSource == EffectiveSalesSource.RemoteRecovering)
        {
            var reason = input.RemotePhase switch
            {
                RemoteSalesPresentationPhase.Reconnecting =>
                    SalesFeatureHealthReason.RemoteSalesReconnecting,
                RemoteSalesPresentationPhase.AuthorizationUnavailable =>
                    SalesFeatureHealthReason.RemoteSalesAuthorizationUnavailable,
                _ => SalesFeatureHealthReason.RemoteSalesResyncing,
            };
            return Create(
                SalesFeatureHealthState.Resyncing,
                reason,
                SalesObservationStatus.Resyncing);
        }

        if (input.Coverage != SalesCoverageState.Complete ||
            input.ObservedMessageCount != input.TargetMessageCount)
        {
            return Create(
                SalesFeatureHealthState.Degraded,
                SalesFeatureHealthReason.CoveragePartial,
                SalesObservationStatus.Partial);
        }

        if (!input.LastCompleteResyncAt.HasValue)
        {
            return Create(
                SalesFeatureHealthState.Resyncing,
                SalesFeatureHealthReason.InitialResyncRequired,
                SalesObservationStatus.Resyncing);
        }

        return Create(
            SalesFeatureHealthState.Live,
            SalesFeatureHealthReason.None,
            SalesObservationStatus.Live,
            isFullyTrustworthy: true);

        SalesFeatureHealthSnapshot Create(
            SalesFeatureHealthState state,
            SalesFeatureHealthReason reason,
            SalesObservationStatus observationStatus,
            bool isFullyTrustworthy = false) => new(
                state,
                reason,
                input.Coverage == SalesCoverageState.Partial
                    ? SalesObservationReason.CoverageIncomplete
                    : SalesObservationReason.None,
                observationStatus,
                input.Coverage,
                isFullyTrustworthy,
                input.LastCompleteResyncAt,
                input.TargetMessageCount,
                input.ObservedMessageCount,
                decision.EffectiveSource,
                input.RemotePhase);
    }
}

public sealed class SalesFeatureHealthMonitor
{
    private SalesFeatureHealthSnapshot? _current;

    public event Action<SalesFeatureHealthSnapshot>? Changed;

    public SalesFeatureHealthSnapshot? Current => _current;

    public bool Update(SalesFeatureHealthInput input)
    {
        var next = SalesFeatureHealthEvaluator.Evaluate(input);
        if (next == _current)
        {
            return false;
        }

        _current = next;
        Changed?.Invoke(next);
        return true;
    }
}

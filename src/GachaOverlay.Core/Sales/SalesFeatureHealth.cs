using GachaOverlay.Core.Discord.Connection;

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
    DiscordDisconnected,
    DiscordConfigurationRequired,
    DiscordFaulted,
    DiscordConnecting,
    DiscordAuthenticating,
    DiscordReconnecting,
    SalesSourceNotReady,
    DiscordWindowUnavailable,
    AccessibilityUnavailable,
    TargetChannelNotSelected,
    TargetChannelUnknown,
    CoveragePartial,
    ResyncInProgress,
    InitialResyncRequired,
    SensorFailure,
}

public sealed record SalesFeatureHealthInput(
    bool SalesTrackingEnabled,
    DiscordConnectionState RpcConnectionState,
    bool SalesSourceReady,
    SalesSensorHealth SensorHealth,
    bool CurrentGenerationResyncComplete);

public sealed record SalesFeatureHealthSnapshot(
    SalesFeatureHealthState State,
    SalesFeatureHealthReason Reason,
    SalesObservationReason SensorReason,
    SalesObservationStatus SensorStatus,
    SalesCoverageState Coverage,
    bool IsFullyTrustworthy,
    DateTimeOffset? LastCompleteResyncAt,
    int TargetMessageCount,
    int ObservedMessageCount)
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
        0);
}

public static class SalesFeatureHealthEvaluator
{
    public static SalesFeatureHealthSnapshot Evaluate(SalesFeatureHealthInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.SensorHealth);

        var sensor = input.SensorHealth;
        if (!input.SalesTrackingEnabled)
        {
            return Create(
                SalesFeatureHealthState.Disabled,
                SalesFeatureHealthReason.SalesTrackingDisabled);
        }

        if (input.RpcConnectionState == DiscordConnectionState.Faulted)
        {
            return Create(
                SalesFeatureHealthState.Error,
                SalesFeatureHealthReason.DiscordFaulted);
        }

        if (input.RpcConnectionState is
            DiscordConnectionState.Disconnected or
            DiscordConnectionState.ConfigurationRequired)
        {
            return Create(
                SalesFeatureHealthState.Disconnected,
                input.RpcConnectionState == DiscordConnectionState.ConfigurationRequired
                    ? SalesFeatureHealthReason.DiscordConfigurationRequired
                    : SalesFeatureHealthReason.DiscordDisconnected);
        }

        if (input.RpcConnectionState is
            DiscordConnectionState.Connecting or
            DiscordConnectionState.Authenticating or
            DiscordConnectionState.Reconnecting)
        {
            return Create(
                SalesFeatureHealthState.Connecting,
                input.RpcConnectionState switch
                {
                    DiscordConnectionState.Authenticating =>
                        SalesFeatureHealthReason.DiscordAuthenticating,
                    DiscordConnectionState.Reconnecting =>
                        SalesFeatureHealthReason.DiscordReconnecting,
                    _ => SalesFeatureHealthReason.DiscordConnecting,
                });
        }

        if (!input.SalesSourceReady)
        {
            return Create(
                SalesFeatureHealthState.Connecting,
                SalesFeatureHealthReason.SalesSourceNotReady);
        }

        if (sensor.Status == SalesObservationStatus.AccessibilityUnavailable ||
            sensor.Reason == SalesObservationReason.AccessibilityTreeUnavailable)
        {
            return Create(
                SalesFeatureHealthState.Error,
                SalesFeatureHealthReason.AccessibilityUnavailable);
        }

        if (sensor.Status is SalesObservationStatus.Error or SalesObservationStatus.Unavailable)
        {
            return Create(
                SalesFeatureHealthState.Error,
                sensor.Reason is SalesObservationReason.DiscordNotRunning or
                    SalesObservationReason.DiscordWindowNotFound
                    ? SalesFeatureHealthReason.DiscordWindowUnavailable
                    : SalesFeatureHealthReason.SensorFailure);
        }

        if (sensor.Reason == SalesObservationReason.TargetChannelUnknown)
        {
            return Create(
                SalesFeatureHealthState.Paused,
                SalesFeatureHealthReason.TargetChannelUnknown);
        }

        if (sensor.Status == SalesObservationStatus.Paused ||
            sensor.Reason == SalesObservationReason.TargetChannelNotSelected)
        {
            return Create(
                SalesFeatureHealthState.Paused,
                SalesFeatureHealthReason.TargetChannelNotSelected);
        }

        if (sensor.Status == SalesObservationStatus.Resyncing)
        {
            return Create(
                SalesFeatureHealthState.Resyncing,
                SalesFeatureHealthReason.ResyncInProgress);
        }

        if (sensor.Status == SalesObservationStatus.Partial ||
            sensor.Coverage == SalesCoverageState.Partial ||
            sensor.Reason == SalesObservationReason.CoverageIncomplete)
        {
            return Create(
                SalesFeatureHealthState.Degraded,
                SalesFeatureHealthReason.CoveragePartial);
        }

        if (sensor.Status == SalesObservationStatus.Live &&
            sensor.Coverage == SalesCoverageState.Complete &&
            sensor.IsComplete &&
            input.CurrentGenerationResyncComplete &&
            sensor.LastCompleteResyncAt.HasValue)
        {
            return Create(
                SalesFeatureHealthState.Live,
                SalesFeatureHealthReason.None,
                isFullyTrustworthy: true);
        }

        return Create(
            SalesFeatureHealthState.Resyncing,
            SalesFeatureHealthReason.InitialResyncRequired);

        SalesFeatureHealthSnapshot Create(
            SalesFeatureHealthState state,
            SalesFeatureHealthReason reason,
            bool isFullyTrustworthy = false) => new(
                state,
                reason,
                sensor.Reason,
                sensor.Status,
                sensor.Coverage,
                isFullyTrustworthy,
                sensor.LastCompleteResyncAt,
                sensor.TargetMessageCount,
                sensor.ObservedMessageCount);
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

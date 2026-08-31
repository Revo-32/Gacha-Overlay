using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales.M7;

public sealed class SalesFeatureHealthTests
{
    [Fact]
    public void SalesOff_HasHighestDisabledPrecedence()
    {
        var result = Evaluate(
            enabled: false,
            rpc: DiscordConnectionState.Faulted,
            ready: false,
            sensor: Sensor(SalesObservationStatus.Error));
        Assert.Equal(SalesFeatureHealthState.Disabled, result.State);
        Assert.Equal(SalesFeatureHealthReason.SalesTrackingDisabled, result.Reason);
    }

    [Theory]
    [InlineData(DiscordConnectionState.Disconnected, SalesFeatureHealthState.Disconnected, SalesFeatureHealthReason.DiscordDisconnected)]
    [InlineData(DiscordConnectionState.ConfigurationRequired, SalesFeatureHealthState.Disconnected, SalesFeatureHealthReason.DiscordConfigurationRequired)]
    [InlineData(DiscordConnectionState.Faulted, SalesFeatureHealthState.Error, SalesFeatureHealthReason.DiscordFaulted)]
    [InlineData(DiscordConnectionState.Connecting, SalesFeatureHealthState.Connecting, SalesFeatureHealthReason.DiscordConnecting)]
    [InlineData(DiscordConnectionState.Authenticating, SalesFeatureHealthState.Connecting, SalesFeatureHealthReason.DiscordAuthenticating)]
    [InlineData(DiscordConnectionState.Reconnecting, SalesFeatureHealthState.Connecting, SalesFeatureHealthReason.DiscordReconnecting)]
    public void RpcState_HasDeterministicPrecedence(
        DiscordConnectionState rpc,
        SalesFeatureHealthState expectedState,
        SalesFeatureHealthReason expectedReason)
    {
        var result = Evaluate(rpc: rpc, sensor: LiveSensor());
        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedReason, result.Reason);
        Assert.False(result.IsFullyTrustworthy);
    }

    [Fact]
    public void SalesSourceNotReady_IsConnecting()
    {
        var result = Evaluate(ready: false, sensor: LiveSensor());
        Assert.Equal(SalesFeatureHealthState.Connecting, result.State);
        Assert.Equal(SalesFeatureHealthReason.SalesSourceNotReady, result.Reason);
    }

    [Theory]
    [InlineData(SalesObservationStatus.AccessibilityUnavailable, SalesObservationReason.None, SalesFeatureHealthReason.AccessibilityUnavailable)]
    [InlineData(SalesObservationStatus.Error, SalesObservationReason.ScanFailed, SalesFeatureHealthReason.SensorFailure)]
    [InlineData(SalesObservationStatus.Unavailable, SalesObservationReason.DiscordWindowNotFound, SalesFeatureHealthReason.DiscordWindowUnavailable)]
    public void InfrastructureFailure_IsErrorAndPreservesSensorReason(
        SalesObservationStatus status,
        SalesObservationReason sensorReason,
        SalesFeatureHealthReason expectedReason)
    {
        var result = Evaluate(sensor: Sensor(status, sensorReason));
        Assert.Equal(SalesFeatureHealthState.Error, result.State);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(sensorReason, result.SensorReason);
    }

    [Theory]
    [InlineData(SalesObservationReason.TargetChannelNotSelected, SalesFeatureHealthReason.TargetChannelNotSelected)]
    [InlineData(SalesObservationReason.TargetChannelUnknown, SalesFeatureHealthReason.TargetChannelUnknown)]
    public void TargetChannelProblem_IsPausedNotError(
        SalesObservationReason reason,
        SalesFeatureHealthReason expected)
    {
        var result = Evaluate(sensor: Sensor(SalesObservationStatus.Paused, reason));
        Assert.Equal(SalesFeatureHealthState.Paused, result.State);
        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void Resyncing_IsExplicit()
    {
        var result = Evaluate(sensor: Sensor(SalesObservationStatus.Resyncing));
        Assert.Equal(SalesFeatureHealthState.Resyncing, result.State);
        Assert.Equal(SalesFeatureHealthReason.ResyncInProgress, result.Reason);
    }

    [Theory]
    [InlineData(SalesObservationStatus.Partial, SalesCoverageState.Complete)]
    [InlineData(SalesObservationStatus.Live, SalesCoverageState.Partial)]
    public void PartialStatusOrCoverage_IsNeverLive(
        SalesObservationStatus status,
        SalesCoverageState coverage)
    {
        var result = Evaluate(sensor: Sensor(status, coverage: coverage));
        Assert.Equal(SalesFeatureHealthState.Degraded, result.State);
        Assert.False(result.IsFullyTrustworthy);
    }

    [Fact]
    public void LiveRequiresCompleteCurrentGenerationResync()
    {
        var result = Evaluate(sensor: LiveSensor(), resyncComplete: false);
        Assert.Equal(SalesFeatureHealthState.Resyncing, result.State);
        Assert.Equal(SalesFeatureHealthReason.InitialResyncRequired, result.Reason);
    }

    [Fact]
    public void LiveComplete_IsFullyTrustworthy()
    {
        var result = Evaluate(sensor: LiveSensor());
        Assert.Equal(SalesFeatureHealthState.Live, result.State);
        Assert.Equal(SalesFeatureHealthReason.None, result.Reason);
        Assert.True(result.IsFullyTrustworthy);
        Assert.Equal(SalesCoverageState.Complete, result.Coverage);
    }

    [Fact]
    public void RpcDisconnected_OverridesLiveSensor()
    {
        var result = Evaluate(
            rpc: DiscordConnectionState.Disconnected,
            sensor: LiveSensor());
        Assert.Equal(SalesFeatureHealthState.Disconnected, result.State);
    }

    [Fact]
    public void IdenticalHealthInput_DoesNotRaiseDuplicateTransition()
    {
        var monitor = new SalesFeatureHealthMonitor();
        var changes = 0;
        monitor.Changed += _ => changes++;
        var input = Input(sensor: LiveSensor());
        Assert.True(monitor.Update(input));
        Assert.False(monitor.Update(input));
        Assert.Equal(1, changes);
    }

    private static SalesFeatureHealthSnapshot Evaluate(
        bool enabled = true,
        DiscordConnectionState rpc = DiscordConnectionState.Connected,
        bool ready = true,
        SalesSensorHealth? sensor = null,
        bool resyncComplete = true) =>
        SalesFeatureHealthEvaluator.Evaluate(
            Input(enabled, rpc, ready, sensor, resyncComplete));

    private static SalesFeatureHealthInput Input(
        bool enabled = true,
        DiscordConnectionState rpc = DiscordConnectionState.Connected,
        bool ready = true,
        SalesSensorHealth? sensor = null,
        bool resyncComplete = true) => new(
            enabled,
            rpc,
            ready,
            sensor ?? LiveSensor(),
            resyncComplete);

    internal static SalesSensorHealth LiveSensor() =>
        Sensor(SalesObservationStatus.Live, coverage: SalesCoverageState.Complete) with
        {
            IsComplete = true,
            LastSuccessfulScanAt = SalesTestFactory.Epoch,
            LastCompleteResyncAt = SalesTestFactory.Epoch,
            TargetMessageCount = 3,
            ObservedMessageCount = 3,
        };

    internal static SalesSensorHealth Sensor(
        SalesObservationStatus status,
        SalesObservationReason reason = SalesObservationReason.None,
        SalesCoverageState coverage = SalesCoverageState.None) =>
        SalesSensorHealth.Disabled with
        {
            Status = status,
            Reason = reason,
            Coverage = coverage,
        };
}

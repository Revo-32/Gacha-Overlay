namespace GachaOverlay.Core.Sales;

public sealed record SalesAcquisitionPolicyInput(
    bool SalesTrackingEnabled,
    RemoteSalesPresentationPhase RemotePhase,
    bool RemoteCanonicalReady);

public sealed record SalesAcquisitionDecision(
    EffectiveSalesSource EffectiveSource,
    bool AllowRemoteProductionEvidence)
{
    public bool AllowsAnyProductionEvidence => AllowRemoteProductionEvidence;
}

/// <summary>
/// Selects the sole production Sales authority from canonical Remote evidence.
/// </summary>
public static class SalesAcquisitionPolicy
{
    public static SalesAcquisitionDecision Evaluate(SalesAcquisitionPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.SalesTrackingEnabled)
        {
            return None(EffectiveSalesSource.RemoteUnavailable);
        }

        if (input.RemotePhase == RemoteSalesPresentationPhase.AccessRevoked)
        {
            return None(EffectiveSalesSource.AccessRevoked);
        }

        if (input.RemotePhase == RemoteSalesPresentationPhase.Live &&
            input.RemoteCanonicalReady)
        {
            return new SalesAcquisitionDecision(
                EffectiveSalesSource.RemotePrimary,
                AllowRemoteProductionEvidence: true);
        }

        return input.RemotePhase switch
        {
            RemoteSalesPresentationPhase.Resyncing or
            RemoteSalesPresentationPhase.Reconnecting or
            RemoteSalesPresentationPhase.AuthorizationUnavailable or
            RemoteSalesPresentationPhase.Live =>
                None(EffectiveSalesSource.RemoteRecovering),

            RemoteSalesPresentationPhase.CredentialUnavailable or
            RemoteSalesPresentationPhase.ChannelUnavailable or
            RemoteSalesPresentationPhase.Failed or
            RemoteSalesPresentationPhase.Disabled =>
                None(EffectiveSalesSource.RemoteUnavailable),

            _ => None(EffectiveSalesSource.RemoteStarting),
        };
    }

    private static SalesAcquisitionDecision None(EffectiveSalesSource source) =>
        new(source, AllowRemoteProductionEvidence: false);
}

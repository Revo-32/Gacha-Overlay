using GachaOverlay.Core.Sales;
using LSOverlay.Protocol;
using SalesTests = GachaOverlay.Tests.Sales.SalesTestFactory;

namespace GachaOverlay.Tests.Backend;

public sealed class M910RemoteSalesAuthorityTests
{
    [Theory]
    [InlineData(RemoteSalesPresentationPhase.Connecting, false, EffectiveSalesSource.RemoteStarting)]
    [InlineData(RemoteSalesPresentationPhase.Bootstrapping, false, EffectiveSalesSource.RemoteStarting)]
    [InlineData(RemoteSalesPresentationPhase.Live, true, EffectiveSalesSource.RemotePrimary)]
    [InlineData(RemoteSalesPresentationPhase.Live, false, EffectiveSalesSource.RemoteRecovering)]
    [InlineData(RemoteSalesPresentationPhase.Resyncing, false, EffectiveSalesSource.RemoteRecovering)]
    [InlineData(RemoteSalesPresentationPhase.Reconnecting, false, EffectiveSalesSource.RemoteRecovering)]
    [InlineData(RemoteSalesPresentationPhase.AuthorizationUnavailable, false, EffectiveSalesSource.RemoteRecovering)]
    [InlineData(RemoteSalesPresentationPhase.CredentialUnavailable, false, EffectiveSalesSource.RemoteUnavailable)]
    [InlineData(RemoteSalesPresentationPhase.ChannelUnavailable, false, EffectiveSalesSource.RemoteUnavailable)]
    [InlineData(RemoteSalesPresentationPhase.Failed, false, EffectiveSalesSource.RemoteUnavailable)]
    [InlineData(RemoteSalesPresentationPhase.AccessRevoked, false, EffectiveSalesSource.AccessRevoked)]
    public void Policy_ExposesOnlyRealRemoteAuthorityStates(
        RemoteSalesPresentationPhase phase,
        bool canonicalReady,
        EffectiveSalesSource expected)
    {
        var decision = SalesAcquisitionPolicy.Evaluate(new SalesAcquisitionPolicyInput(
            true,
            phase,
            canonicalReady));

        Assert.Equal(expected, decision.EffectiveSource);
        Assert.Equal(expected == EffectiveSalesSource.RemotePrimary,
            decision.AllowRemoteProductionEvidence);
    }

    [Fact]
    public void SalesOff_HasNoProductionEvidence()
    {
        var decision = SalesAcquisitionPolicy.Evaluate(new SalesAcquisitionPolicyInput(
            false,
            RemoteSalesPresentationPhase.Live,
            true));
        Assert.False(decision.AllowsAnyProductionEvidence);
        Assert.Equal(EffectiveSalesSource.RemoteUnavailable, decision.EffectiveSource);
    }

    [Theory]
    [InlineData(RemoteSalesPresentationPhase.Connecting, SalesFeatureHealthState.Connecting)]
    [InlineData(RemoteSalesPresentationPhase.Bootstrapping, SalesFeatureHealthState.Connecting)]
    [InlineData(RemoteSalesPresentationPhase.Resyncing, SalesFeatureHealthState.Resyncing)]
    [InlineData(RemoteSalesPresentationPhase.Reconnecting, SalesFeatureHealthState.Resyncing)]
    [InlineData(RemoteSalesPresentationPhase.Failed, SalesFeatureHealthState.Error)]
    [InlineData(RemoteSalesPresentationPhase.AccessRevoked, SalesFeatureHealthState.Error)]
    public void Health_IsRemoteSpecific(
        RemoteSalesPresentationPhase phase,
        SalesFeatureHealthState expected)
    {
        var health = SalesFeatureHealthEvaluator.Evaluate(new SalesFeatureHealthInput(
            true,
            phase,
            false,
            SalesCoverageState.None,
            null,
            0,
            0));
        Assert.Equal(expected, health.State);
    }

    [Fact]
    public void CompletionMarkers_PreserveSoldOrClosedSemantics()
    {
        Assert.True(Observation(sold: true, closed: false).IsSold);
        Assert.True(Observation(sold: false, closed: true).IsSold);
        Assert.True(Observation(sold: true, closed: true).IsSold);
        Assert.False(Observation(sold: false, closed: false).IsSold);
        Assert.Equal(1451583544295034940UL, RemoteSalesPolicy.SoldEmojiId);
        Assert.Equal("SOLD", RemoteSalesPolicy.SoldEmojiName);
        Assert.Equal(1418284521337651321UL, RemoteSalesPolicy.ClosedEmojiId);
        Assert.Equal("closed", RemoteSalesPolicy.ClosedEmojiName);
    }

    [Fact]
    public void TransientRecovery_DoesNotFabricateSoldToPending()
    {
        var engine = SalesTests.Engine();
        engine.ApplyAuthoritativeWindowSnapshot(new[] { SalesTests.Message("1") });
        Apply(engine, 1, SaleReactionOutcome.Sold, trusted: true);

        Apply(engine, 2, SaleReactionOutcome.NotObserved, trusted: false);

        Assert.Equal(SaleDomainState.Sold, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void CanonicalCompleteAbsence_AllowsSoldToPending()
    {
        var engine = SalesTests.Engine();
        engine.ApplyAuthoritativeWindowSnapshot(new[] { SalesTests.Message("1") });
        Apply(engine, 1, SaleReactionOutcome.Sold, trusted: true);
        Apply(engine, 2, SaleReactionOutcome.NotSold, trusted: true);

        Assert.Equal(SaleDomainState.Pending, Assert.Single(engine.Records).DomainState);
    }

    [Fact]
    public void RecoveryReplayWithSameState_DoesNotDuplicateTransition()
    {
        var engine = SalesTests.Engine();
        engine.ApplyAuthoritativeWindowSnapshot(new[] { SalesTests.Message("1") });
        Apply(engine, 1, SaleReactionOutcome.Sold, trusted: true);
        var revision = engine.Current.Revision;

        Apply(engine, 2, SaleReactionOutcome.Sold, trusted: true);

        Assert.Equal(revision, engine.Current.Revision);
    }

    [Fact]
    public void AuthoritativeWindow_RejectsMoreThanNewestThirty()
    {
        var engine = SalesTests.Engine();
        var messages = Enumerable.Range(1, AuthoritativeSalesWindow.Size + 1)
            .Select(index => SalesTests.Message(index.ToString()))
            .ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            engine.ApplyAuthoritativeWindowSnapshot(messages));
    }

    [Fact]
    public void ExactDelete_RemainsIdempotent()
    {
        var engine = SalesTests.Engine();
        engine.ApplyAuthoritativeWindowSnapshot(new[] { SalesTests.Message("1") });
        Assert.True(engine.ApplySourceDelete("1"));
        Assert.False(engine.ApplySourceDelete("1"));
        Assert.Empty(engine.Current.ActiveItems);
    }

    private static void Apply(
        SalesStateEngine engine,
        long generation,
        SaleReactionOutcome outcome,
        bool trusted)
    {
        engine.ApplyObservationBatch(new SalesObservationBatch(
            generation,
            SalesTests.Epoch.AddMinutes(generation),
            trusted ? SalesObservationStatus.Live : SalesObservationStatus.Partial,
            trusted,
            trusted ? SalesObservationCompleteness.Full : SalesObservationCompleteness.Partial,
            new[]
            {
                SalesTests.Observation(
                    "1",
                    outcome,
                    generation,
                    trustedEvidence: trusted),
            },
            trusted ? SalesCoverageState.Complete : SalesCoverageState.Partial));
    }

    private static SalesCompletionObservation Observation(bool sold, bool closed) => new(
        1,
        sold,
        closed,
        SalesEvidenceCoverage.Complete,
        DateTimeOffset.UnixEpoch);
}

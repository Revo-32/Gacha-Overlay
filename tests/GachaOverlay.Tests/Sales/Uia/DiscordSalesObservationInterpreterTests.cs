using GachaOverlay.App.Services.Sales;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales.Uia;

public sealed class DiscordSalesObservationInterpreterTests
{
    [Fact]
    public void ReactionGroupWithSold_ProducesTrustedSold()
    {
        var batch = Interpret(
            UiaSalesTestFactory.Targets(1, "1"),
            UiaSalesTestFactory.Context(
                "1",
                groups: UiaSalesTestFactory.Group("1", hasCompletionReaction: true)));
        var observation = Assert.Single(batch.Observations);
        Assert.Equal(SaleReactionOutcome.Sold, observation.Outcome);
        Assert.True(observation.HasTrustedEvidence);
    }

    [Fact]
    public void SoldPositiveEvidence_RemainsTrustedWhenDuplicateContextIsIncomplete()
    {
        var snapshot = UiaSalesTestFactory.Selected(new[]
        {
            UiaSalesTestFactory.Context("1", complete: false),
            UiaSalesTestFactory.Context(
                "1",
                groups: UiaSalesTestFactory.Group("1", hasCompletionReaction: true)),
        });
        var batch = DiscordSalesObservationInterpreter.Interpret(
            snapshot,
            UiaSalesTestFactory.Targets(1, "1"),
            1,
            SalesTestFactory.Epoch);
        Assert.Equal(SaleReactionOutcome.Sold, Assert.Single(batch.Observations).Outcome);
    }

    [Fact]
    public void CompleteReactionGroupWithoutSold_ProducesTrustedNotSold()
    {
        var batch = Interpret(
            UiaSalesTestFactory.Targets(1, "1"),
            UiaSalesTestFactory.Context(
                "1",
                groups: UiaSalesTestFactory.Group("1")));
        var observation = Assert.Single(batch.Observations);
        Assert.Equal(SaleReactionOutcome.NotSold, observation.Outcome);
        Assert.True(observation.HasTrustedEvidence);
    }

    [Fact]
    public void CompleteMessageContextWithoutReactionGroup_ProducesTrustedNotSold()
    {
        var batch = Interpret(
            UiaSalesTestFactory.Targets(1, "1"),
            UiaSalesTestFactory.Context("1"));
        Assert.Equal(SaleReactionOutcome.NotSold, Assert.Single(batch.Observations).Outcome);
    }

    [Fact]
    public void MessageBodyContainingSold_IsNotReactionEvidence()
    {
        var batch = Interpret(
            UiaSalesTestFactory.Targets(1, "1"),
            UiaSalesTestFactory.Context(
                "1",
                kind: DiscordMessageContextKind.MessageContent));
        Assert.Equal(SaleReactionOutcome.NotSold, Assert.Single(batch.Observations).Outcome);
    }

    [Fact]
    public void MissingUiaContext_ProducesNotObservedNotNotSold()
    {
        var batch = DiscordSalesObservationInterpreter.Interpret(
            UiaSalesTestFactory.Selected(),
            UiaSalesTestFactory.Targets(1, "1"),
            1,
            SalesTestFactory.Epoch);
        var observation = Assert.Single(batch.Observations);
        Assert.Equal(SaleReactionOutcome.NotObserved, observation.Outcome);
        Assert.False(observation.HasTrustedEvidence);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void IncompleteContextOrReactionGroup_ProducesNotObserved(
        bool contextComplete,
        bool groupComplete)
    {
        var batch = Interpret(
            UiaSalesTestFactory.Targets(1, "1"),
            UiaSalesTestFactory.Context(
                "1",
                contextComplete,
                groups: UiaSalesTestFactory.Group(
                    "1",
                    groupComplete,
                    hasCompletionReaction: false)));
        Assert.Equal(SaleReactionOutcome.NotObserved, Assert.Single(batch.Observations).Outcome);
    }

    [Fact]
    public void ConflictingDuplicateContextsWithoutSold_AreConservative()
    {
        var snapshot = UiaSalesTestFactory.Selected(new[]
        {
            UiaSalesTestFactory.Context("1", complete: true),
            UiaSalesTestFactory.Context("1", complete: false),
        });
        var batch = DiscordSalesObservationInterpreter.Interpret(
            snapshot,
            UiaSalesTestFactory.Targets(1, "1"),
            1,
            SalesTestFactory.Epoch);
        Assert.Equal(SaleReactionOutcome.NotObserved, Assert.Single(batch.Observations).Outcome);
    }

    [Fact]
    public void AllTargetsObserved_ProducesCompleteLiveBatch()
    {
        var targets = UiaSalesTestFactory.Targets(9, "1", "2");
        var batch = Interpret(
            targets,
            UiaSalesTestFactory.Context("1"),
            UiaSalesTestFactory.Context(
                "2",
                groups: UiaSalesTestFactory.Group("2", hasCompletionReaction: true)));
        Assert.Equal(SalesCoverageState.Complete, batch.Coverage);
        Assert.Equal(SalesObservationCompleteness.Full, batch.Completeness);
        Assert.Equal(SalesObservationStatus.Live, batch.SensorStatus);
        Assert.Equal(2, batch.ObservedMessageCount);
        Assert.Equal(1, batch.SoldCount);
        Assert.Equal(1, batch.NotSoldCount);
        Assert.Equal(0, batch.NotObservedCount);
        Assert.Equal(9, batch.TargetSetRevision);
    }

    [Fact]
    public void SomeTargetsObserved_ProducesPartialBatch()
    {
        var batch = Interpret(
            UiaSalesTestFactory.Targets(1, "1", "2"),
            UiaSalesTestFactory.Context("1"));
        Assert.Equal(SalesCoverageState.Partial, batch.Coverage);
        Assert.Equal(SalesObservationStatus.Partial, batch.SensorStatus);
        Assert.Equal(1, batch.ObservedMessageCount);
        Assert.Equal(1, batch.NotObservedCount);
    }

    [Fact]
    public void NoTargetContexts_ProducesNoneCoverage()
    {
        var batch = Interpret(UiaSalesTestFactory.Targets(1, "1", "2"));
        Assert.Equal(SalesCoverageState.None, batch.Coverage);
        Assert.Equal(SalesObservationStatus.Partial, batch.SensorStatus);
        Assert.Equal(2, batch.NotObservedCount);
    }

    [Fact]
    public void EmptyTargetSetWithSuccessfulTraversal_IsComplete()
    {
        var batch = Interpret(UiaSalesTestFactory.Targets(1));
        Assert.Equal(SalesCoverageState.Complete, batch.Coverage);
        Assert.Equal(SalesObservationStatus.Live, batch.SensorStatus);
        Assert.Empty(batch.Observations);
    }

    [Fact]
    public void GloballyIncompleteTraversal_CannotClaimCompleteCoverage()
    {
        var snapshot = UiaSalesTestFactory.Selected(
            new[] { UiaSalesTestFactory.Context("1") },
            traversalComplete: false);
        var batch = DiscordSalesObservationInterpreter.Interpret(
            snapshot,
            UiaSalesTestFactory.Targets(1, "1"),
            1,
            SalesTestFactory.Epoch);
        Assert.Equal(SalesCoverageState.Partial, batch.Coverage);
        Assert.Equal(SalesObservationStatus.Partial, batch.SensorStatus);
    }

    [Theory]
    [InlineData(SalesObservationReason.DiscordNotRunning, SalesObservationStatus.Unavailable)]
    [InlineData(SalesObservationReason.DiscordWindowNotFound, SalesObservationStatus.Unavailable)]
    public void WindowUnavailable_ProducesUntrustedUnavailableBatch(
        SalesObservationReason reason,
        SalesObservationStatus expected)
    {
        var batch = DiscordSalesObservationInterpreter.Interpret(
            UiaSalesTestFactory.Unavailable(reason),
            UiaSalesTestFactory.Targets(1, "1"),
            1,
            SalesTestFactory.Epoch);
        Assert.Equal(expected, batch.SensorStatus);
        Assert.False(batch.IsTrusted);
        Assert.Empty(batch.Observations);
    }

    [Fact]
    public void AccessibilityUnavailable_ProducesNoTrustedObservations()
    {
        var snapshot = UiaSalesTestFactory.Selected() with
        {
            AccessibilityReady = false,
            FailureReason = SalesObservationReason.AccessibilityTreeUnavailable,
        };
        var batch = DiscordSalesObservationInterpreter.Interpret(
            snapshot,
            UiaSalesTestFactory.Targets(1, "1"),
            1,
            SalesTestFactory.Epoch);
        Assert.Equal(SalesObservationStatus.AccessibilityUnavailable, batch.SensorStatus);
        Assert.False(batch.IsTrusted);
    }

    [Theory]
    [InlineData(SalesTargetChannelStatus.NotSelected, SalesObservationReason.TargetChannelNotSelected)]
    [InlineData(SalesTargetChannelStatus.Unknown, SalesObservationReason.TargetChannelUnknown)]
    public void NonTargetOrUnknownChannel_ProducesPausedWithoutObservations(
        SalesTargetChannelStatus channelStatus,
        SalesObservationReason reason)
    {
        var snapshot = UiaSalesTestFactory.Selected() with
        {
            TargetChannelStatus = channelStatus,
            FailureReason = reason,
        };
        var batch = DiscordSalesObservationInterpreter.Interpret(
            snapshot,
            UiaSalesTestFactory.Targets(1, "1"),
            1,
            SalesTestFactory.Epoch);
        Assert.Equal(SalesObservationStatus.Paused, batch.SensorStatus);
        Assert.False(batch.IsTrusted);
        Assert.Empty(batch.Observations);
    }

    [Fact]
    public void PartialBatch_AppliesOnlyExplicitTrustedOutcomesToM5()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("1", seconds: 1),
            SalesTestFactory.Message("2", seconds: 2),
        });
        var records = engine.Records.ToDictionary(record => record.MessageId);
        var targets = new SalesObservationTargetSet(
            1,
            1,
            true,
            "channel",
            "sales",
            records.Values.Select(record =>
                new SalesObservationTarget(record.MessageId, record.SourceRevision)).ToArray());
        var batch = Interpret(targets, UiaSalesTestFactory.Context(
            "1",
            groups: UiaSalesTestFactory.Group("1", hasCompletionReaction: true)));
        engine.ApplyObservationBatch(batch);
        Assert.Equal(SaleDomainState.Sold, engine.Records.Single(x => x.MessageId == "1").DomainState);
        Assert.Equal(
            SaleObservationTrust.NeverObserved,
            engine.Records.Single(x => x.MessageId == "2").ObservationTrust);
    }

    private static SalesObservationBatch Interpret(
        SalesObservationTargetSet targets,
        params DiscordMessageAccessibilityContext[] contexts) =>
        DiscordSalesObservationInterpreter.Interpret(
            UiaSalesTestFactory.Selected(contexts),
            targets,
            1,
            SalesTestFactory.Epoch);
}

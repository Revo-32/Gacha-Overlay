using GachaOverlay.App.Services.Sales;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales.Uia;

public sealed class M821SalesCompletionReactionMatcherTests
{
    [Theory]
    [InlineData("1451583544295034940", "renamed", "Sold")]
    [InlineData("1418284521337651321", "renamed", "Closed")]
    [InlineData("1451583544295034940", null, "Sold")]
    [InlineData("1418284521337651321", null, "Closed")]
    public void ProductionEmojiId_IsPrimaryAndNameIndependent(
        string emojiId,
        string? emojiName,
        string expected)
    {
        var match = DiscordSalesCompletionReactionMatcher.Match(
            new DiscordReactionIdentity(emojiId, emojiName));

        Assert.True(match.IsCompletion);
        Assert.Equal(expected, match.Marker.ToString());
        Assert.Equal(SalesCompletionReactionMatchSource.EmojiId, match.Source);
    }

    [Theory]
    [InlineData("999999999999999999", "SOLD")]
    [InlineData("999999999999999998", "closed")]
    [InlineData("", "SOLD")]
    [InlineData(" ", "closed")]
    public void ExplicitWrongEmojiId_NeverFallsBackToName(string emojiId, string emojiName)
    {
        var match = DiscordSalesCompletionReactionMatcher.Match(
            new DiscordReactionIdentity(emojiId, emojiName));

        Assert.False(match.IsCompletion);
        Assert.Equal(SalesCompletionReactionMatchSource.None, match.Source);
    }

    [Theory]
    [InlineData("SOLD", "Sold")]
    [InlineData("closed", "Closed")]
    public void MissingEmojiId_UsesExactCanonicalNameFallback(
        string emojiName,
        string expected)
    {
        var match = DiscordSalesCompletionReactionMatcher.Match(
            new DiscordReactionIdentity(null, emojiName));

        Assert.Equal(expected, match.Marker.ToString());
        Assert.Equal(SalesCompletionReactionMatchSource.NameFallback, match.Source);
    }

    [Theory]
    [InlineData("SOLD2")]
    [InlineData("MYSOLD")]
    [InlineData("PRESOLD")]
    [InlineData("SOLD_TEST")]
    [InlineData("sold")]
    [InlineData("closedown")]
    [InlineData("preclosed")]
    [InlineData("myclosed")]
    [InlineData("closed_test")]
    [InlineData("closed2")]
    [InlineData("CLOSED")]
    [InlineData("마감")]
    [InlineData("")]
    [InlineData(null)]
    public void MissingEmojiId_RejectsNonCanonicalName(string? emojiName) =>
        Assert.False(DiscordSalesCompletionReactionMatcher.Match(
            new DiscordReactionIdentity(null, emojiName)).IsCompletion);

    [Theory]
    [InlineData("SOLD")]
    [InlineData(":SOLD:")]
    [InlineData("SOLD반응1개, 눌러서 반응하기")]
    [InlineData("SOLD reaction, 1")]
    [InlineData("😀 SOLD, 24 reactions")]
    [InlineData("closed")]
    [InlineData(":closed:")]
    [InlineData("closed반응1개, 눌러서 반응하기")]
    [InlineData("closed reaction, 1")]
    [InlineData("🔒 closed, 24 reactions")]
    public void AccessibilityNameFallback_AcceptsOnlyCanonicalIdentifierToken(string name)
    {
        var match = DiscordSalesCompletionReactionMatcher.MatchAccessibleNameFallback(name);

        Assert.True(match.IsCompletion);
        Assert.Equal(SalesCompletionReactionMatchSource.NameFallback, match.Source);
    }

    [Theory]
    [InlineData("SOLD2")]
    [InlineData("MYSOLD")]
    [InlineData("PRESOLD")]
    [InlineData("SOLD_TEST")]
    [InlineData("sold reaction, 1")]
    [InlineData("closedown")]
    [InlineData("preclosed")]
    [InlineData("myclosed")]
    [InlineData("closed_test")]
    [InlineData("closed2")]
    [InlineData("CLOSED reaction, 1")]
    [InlineData("오늘 판매 마감")]
    [InlineData("")]
    [InlineData(null)]
    public void AccessibilityNameFallback_RejectsSubstringAndUnrelatedText(string? name) =>
        Assert.False(DiscordSalesCompletionReactionMatcher
            .MatchAccessibleNameFallback(name)
            .IsCompletion);
}

public sealed class M821SalesCompletionObservationTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SoldOrClosedOrBoth_ProducesOneTrustedSold(bool sold, bool closed)
    {
        var groups = new List<DiscordReactionGroupSnapshot>();
        if (sold)
        {
            groups.Add(UiaSalesTestFactory.Group("1", hasCompletionReaction: true));
        }

        if (closed)
        {
            groups.Add(UiaSalesTestFactory.Group("1", hasCompletionReaction: true));
        }

        var batch = Interpret("1", UiaSalesTestFactory.Context("1", groups: groups.ToArray()));

        var observation = Assert.Single(batch.Observations);
        Assert.Equal(SaleReactionOutcome.Sold, observation.Outcome);
        Assert.True(observation.HasTrustedEvidence);
        Assert.Equal(1, batch.SoldCount);
    }

    [Fact]
    public void NeitherMarkerInCompleteReactionGroup_ProducesTrustedNotSold()
    {
        var batch = Interpret(
            "1",
            UiaSalesTestFactory.Context("1", groups: UiaSalesTestFactory.Group("1")));

        Assert.Equal(SaleReactionOutcome.NotSold, Assert.Single(batch.Observations).Outcome);
    }

    [Theory]
    [InlineData("MessageContent")]
    [InlineData("MessageAccessories")]
    [InlineData("ChatMessageContainer")]
    public void NonReactionContextCannotCreateCompletionEvidence(string kindName)
    {
        var kind = Enum.Parse<DiscordMessageContextKind>(kindName);
        var batch = Interpret("1", UiaSalesTestFactory.Context("1", kind: kind));

        Assert.Equal(SaleReactionOutcome.NotSold, Assert.Single(batch.Observations).Outcome);
    }

    [Fact]
    public void OtherMessageReactionGroup_DoesNotAffectCurrentMessage()
    {
        var snapshot = UiaSalesTestFactory.Selected(new[]
        {
            UiaSalesTestFactory.Context("1"),
            UiaSalesTestFactory.Context(
                "2",
                groups: UiaSalesTestFactory.Group("2", hasCompletionReaction: true)),
        });

        var batch = DiscordSalesObservationInterpreter.Interpret(
            snapshot,
            UiaSalesTestFactory.Targets(1, "1"),
            1,
            SalesTestFactory.Epoch);

        Assert.Equal(SaleReactionOutcome.NotSold, Assert.Single(batch.Observations).Outcome);
    }

    [Fact]
    public void PositiveCompletionEvidence_RemainsTrustedDuringPartialTraversal()
    {
        var batch = Interpret(
            "1",
            UiaSalesTestFactory.Context(
                "1",
                complete: false,
                groups: UiaSalesTestFactory.Group(
                    "1",
                    complete: false,
                    hasCompletionReaction: true)));

        Assert.Equal(SaleReactionOutcome.Sold, Assert.Single(batch.Observations).Outcome);
    }

    [Fact]
    public void PartialTraversalWithNeitherMarker_CannotCreateNotSoldEvidence()
    {
        var batch = Interpret(
            "1",
            UiaSalesTestFactory.Context(
                "1",
                complete: false,
                groups: UiaSalesTestFactory.Group("1", complete: false)));

        var observation = Assert.Single(batch.Observations);
        Assert.Equal(SaleReactionOutcome.NotObserved, observation.Outcome);
        Assert.False(observation.HasTrustedEvidence);
    }

    private static SalesObservationBatch Interpret(
        string targetMessageId,
        params DiscordMessageAccessibilityContext[] contexts) =>
        DiscordSalesObservationInterpreter.Interpret(
            UiaSalesTestFactory.Selected(contexts),
            UiaSalesTestFactory.Targets(1, targetMessageId),
            1,
            SalesTestFactory.Epoch);
}

public sealed class M821SalesCompletionQueueTests
{
    [Theory]
    [InlineData("SOLD")]
    [InlineData("closed")]
    [InlineData("both")]
    public void CompletionMarker_ExcludesSoldRecordFromEveryActiveProjection(string marker)
    {
        var engine = ThreeEntries();
        Apply(engine, 1, ("2", SaleReactionOutcome.Sold));

        Assert.Equal(SaleDomainState.Sold, Record(engine, "2").DomainState);
        Assert.Equal(new[] { "1", "3" }, ActiveIds(engine));
        Assert.DoesNotContain(engine.Current.ActiveItems, item => item.MessageId == "2");
        Assert.Equal(2, engine.Current.ActiveCount);
        Assert.Equal(1, engine.Current.WaitingCount);
        Assert.Equal("1", engine.Current.CurrentSeller!.MessageId);
        Assert.Equal("3", engine.Current.NextWaitingEntry!.MessageId);
        Assert.NotEmpty(marker);
    }

    [Fact]
    public void CurrentSellerCompletion_RecalculatesCurrentAndWaiting()
    {
        var engine = ThreeEntries();

        Apply(engine, 1, ("1", SaleReactionOutcome.Sold));

        Assert.Equal("2", engine.Current.CurrentSeller!.MessageId);
        Assert.Equal("3", engine.Current.NextWaitingEntry!.MessageId);
        Assert.Equal(2, engine.Current.ActiveCount);
        Assert.Equal(1, engine.Current.WaitingCount);
    }

    [Fact]
    public void SoldRecord_IsRetainedInternally()
    {
        var engine = ThreeEntries();

        Apply(engine, 1, ("2", SaleReactionOutcome.Sold));

        Assert.Contains(engine.Records, record =>
            record.MessageId == "2" && record.DomainState == SaleDomainState.Sold);
    }

    [Theory]
    [InlineData("remove-SOLD-closed-remains")]
    [InlineData("remove-closed-SOLD-remains")]
    public void RemovingOnlyOneOfTwoMarkers_KeepsOneLogicalSoldState(string scenario)
    {
        var engine = ThreeEntries();
        Apply(engine, 1, ("2", SaleReactionOutcome.Sold));
        var revisionAfterFirstCompletion = engine.Current.Revision;

        var changed = Apply(engine, 2, ("2", SaleReactionOutcome.Sold));

        Assert.False(changed);
        Assert.Equal(revisionAfterFirstCompletion, engine.Current.Revision);
        Assert.Equal(SaleDomainState.Sold, Record(engine, "2").DomainState);
        Assert.DoesNotContain("2", ActiveIds(engine));
        Assert.NotEmpty(scenario);
    }

    [Theory]
    [InlineData("SOLD-only-removed")]
    [InlineData("closed-only-removed")]
    [InlineData("both-removed")]
    public void CompleteAbsenceOfBothMarkers_ReturnsPendingInOriginalOrder(string scenario)
    {
        var engine = ThreeEntries();
        Apply(engine, 1, ("2", SaleReactionOutcome.Sold));

        Apply(engine, 2, ("2", SaleReactionOutcome.NotSold));

        Assert.Equal(SaleDomainState.Pending, Record(engine, "2").DomainState);
        Assert.Equal(new[] { "1", "2", "3" }, ActiveIds(engine));
        Assert.Equal(3, engine.Current.ActiveCount);
        Assert.Equal(2, engine.Current.WaitingCount);
        Assert.NotEmpty(scenario);
    }

    [Fact]
    public void SnowflakeTieBreaker_IsRestoredAfterPendingReturn()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("10"),
            SalesTestFactory.Message("2"),
            SalesTestFactory.Message("9"),
        });
        Apply(engine, 1, ("9", SaleReactionOutcome.Sold));

        Apply(engine, 2, ("9", SaleReactionOutcome.NotSold));

        Assert.Equal(new[] { "2", "9", "10" }, ActiveIds(engine));
    }

    [Theory]
    [InlineData(SaleReactionOutcome.NotObserved, false)]
    [InlineData(SaleReactionOutcome.NotSold, false)]
    public void UntrustedOrNotObservedEvidence_CannotClearSold(
        SaleReactionOutcome outcome,
        bool trustedEvidence)
    {
        var engine = ThreeEntries();
        Apply(engine, 1, ("2", SaleReactionOutcome.Sold));

        engine.ApplyObservationBatch(SalesTestFactory.Batch(
            2,
            true,
            SalesObservationStatus.Partial,
            SalesTestFactory.Observation(
                "2",
                outcome,
                2,
                trustedEvidence: trustedEvidence)));

        Assert.Equal(SaleDomainState.Sold, Record(engine, "2").DomainState);
        Assert.DoesNotContain("2", ActiveIds(engine));
    }

    [Theory]
    [InlineData(SalesObservationStatus.Unavailable)]
    [InlineData(SalesObservationStatus.AccessibilityUnavailable)]
    [InlineData(SalesObservationStatus.Paused)]
    [InlineData(SalesObservationStatus.Error)]
    public void UiaOrChannelUnavailable_PreservesSoldRecordAndTrustedQueue(
        SalesObservationStatus status)
    {
        var engine = ThreeEntries();
        Apply(engine, 1, ("2", SaleReactionOutcome.Sold));

        engine.ApplyObservationBatch(SalesTestFactory.Batch(2, false, status));

        Assert.Equal(SaleDomainState.Sold, Record(engine, "2").DomainState);
        Assert.Equal(new[] { "1", "3" }, ActiveIds(engine));
    }

    [Fact]
    public void SecondCompletionMarker_DoesNotDuplicateStateTransition()
    {
        var engine = ThreeEntries();
        Assert.True(Apply(engine, 1, ("2", SaleReactionOutcome.Sold)));
        var revision = engine.Current.Revision;

        Assert.False(Apply(engine, 2, ("2", SaleReactionOutcome.Sold)));
        Assert.Equal(revision, engine.Current.Revision);
    }

    [Fact]
    public void CompletionTransition_DoesNotChangeProductAggregation()
    {
        var catalog = SalesTestFactory.Catalog(
            SalesTestFactory.Product("product", "100", "item", "Item"));
        var engine = SalesTestFactory.Engine(catalog);
        engine.ApplySourceCreate(SalesTestFactory.Message(
            "1",
            emojis: new[]
            {
                new DiscordCustomEmoji("100", "item", false),
                new DiscordCustomEmoji("100", "item", false),
            }));
        var before = Record(engine, "1").AllProducts.Single();

        Apply(engine, 1, ("1", SaleReactionOutcome.Sold));
        var sold = Record(engine, "1").AllProducts.Single();
        Apply(engine, 2, ("1", SaleReactionOutcome.NotSold));
        var returned = Record(engine, "1").AllProducts.Single();

        Assert.Equal(2, before.Quantity);
        Assert.Equal(before, sold);
        Assert.Equal(before, returned);
    }

    [Fact]
    public void SalesTrackingOff_IgnoresCompletionObservation()
    {
        var engine = ThreeEntries();
        engine.SetTrackingEnabled(false);

        var changed = Apply(engine, 1, ("2", SaleReactionOutcome.Sold));

        Assert.False(changed);
        Assert.Empty(engine.Current.ActiveItems);
        Assert.Equal(SaleDomainState.Pending, Record(engine, "2").DomainState);
    }

    private static SalesStateEngine ThreeEntries()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplySourceSnapshot(new[]
        {
            SalesTestFactory.Message("1", authorId: "author-1", seconds: 1),
            SalesTestFactory.Message("2", authorId: "author-2", seconds: 2),
            SalesTestFactory.Message("3", authorId: "author-3", seconds: 3),
        });
        return engine;
    }

    private static bool Apply(
        SalesStateEngine engine,
        long generation,
        params (string MessageId, SaleReactionOutcome Outcome)[] outcomes) =>
        engine.ApplyObservationBatch(SalesTestFactory.Batch(
            generation,
            true,
            SalesObservationStatus.Live,
            outcomes.Select(outcome => SalesTestFactory.Observation(
                outcome.MessageId,
                outcome.Outcome,
                generation)).ToArray()));

    private static SaleRecord Record(SalesStateEngine engine, string messageId) =>
        engine.Records.Single(record => record.MessageId == messageId);

    private static string[] ActiveIds(SalesStateEngine engine) =>
        engine.Current.ActiveItems.Select(item => item.MessageId).ToArray();
}

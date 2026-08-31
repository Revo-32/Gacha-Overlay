using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales.M7;

public sealed class SalesAnimationRequestTests
{
    [Fact]
    public void TrustedSoldCurrentChange_RequestsSoldTransition()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentId: "2", nextId: "3"),
            change: M7PresentationTestFactory.SoldChange());
        Assert.Equal("2", result.CurrentMessageId);
        Assert.Equal(SalesQueueAnimationRequest.SoldTransition, result.AnimationRequest);
    }

    [Fact]
    public void SameCurrentPolling_DoesNotAnimate()
    {
        var first = M7PresentationTestFactory.Create();
        var repeated = M7PresentationTestFactory.Create(previous: first);
        Assert.Equal(SalesQueueAnimationRequest.None, repeated.AnimationRequest);
    }

    [Theory]
    [InlineData(SalesQueueChangeReason.DisplayNameChanged)]
    [InlineData(SalesQueueChangeReason.SettingsChanged)]
    [InlineData(SalesQueueChangeReason.Resync)]
    [InlineData(SalesQueueChangeReason.TrustedNotSold)]
    [InlineData(SalesQueueChangeReason.SourceDeleted)]
    [InlineData(SalesQueueChangeReason.SourceCreated)]
    public void NonSoldReason_DoesNotMasqueradeAsSoldTransition(
        SalesQueueChangeReason reason)
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentId: "2", nextId: "3"),
            change: new SalesQueueChangeContext(true, "1", "2", reason, 11));
        Assert.Equal(SalesQueueAnimationRequest.None, result.AnimationRequest);
    }

    [Fact]
    public void WaitingCountOnlyChange_DoesNotAnimate()
    {
        var first = M7PresentationTestFactory.Create();
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(extraWaiting: 2, revision: 11),
            previous: first);
        Assert.Equal(SalesQueueAnimationRequest.None, result.AnimationRequest);
    }

    [Fact]
    public void DisplayNameOnlyChange_DoesNotAnimate()
    {
        var first = M7PresentationTestFactory.Create();
        var changedQueue = M7PresentationTestFactory.Queue() with
        {
            CurrentSeller = M7PresentationTestFactory.Queue().CurrentSeller! with
            {
                DisplayName = "Renamed",
            },
            Revision = 11,
        };
        var result = M7PresentationTestFactory.Create(
            queue: changedQueue,
            previous: first);
        Assert.Equal(SalesQueueAnimationRequest.None, result.AnimationRequest);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AnimationFallback_IsImmediateWhenDisabledOrHudHidden(
        bool animationsEnabled,
        bool hudVisible)
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentId: "2", nextId: "3"),
            change: M7PresentationTestFactory.SoldChange(),
            animationsEnabled: animationsEnabled,
            hudVisible: hudVisible);
        Assert.Equal(SalesQueueAnimationRequest.None, result.AnimationRequest);
    }

    [Fact]
    public void CurrentTurnEntry_HasPriorityOverSoldFade()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true, currentId: "2"),
            change: M7PresentationTestFactory.SoldChange());
        Assert.Equal(SalesQueueAnimationRequest.CurrentTurnEnter, result.AnimationRequest);
    }

    [Fact]
    public void CurrentTurnAnimation_DoesNotStackOnRepeatedSnapshot()
    {
        var first = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true));
        var repeated = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true, revision: 11),
            previous: first);
        Assert.Equal(SalesQueueAnimationRequest.CurrentTurnEnter, first.AnimationRequest);
        Assert.Equal(SalesQueueAnimationRequest.None, repeated.AnimationRequest);
    }

    [Fact]
    public void NextTurnAnimation_DoesNotStackOnRepeatedSnapshot()
    {
        var first = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(nextSelf: true));
        var repeated = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(nextSelf: true, revision: 11),
            previous: first);
        Assert.Equal(SalesQueueAnimationRequest.NextTurnEnter, first.AnimationRequest);
        Assert.Equal(SalesQueueAnimationRequest.None, repeated.AnimationRequest);
    }

    [Fact]
    public void RapidUpdates_CoalesceToLatestNonAnimatedPresentation()
    {
        var sold = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentId: "2", nextId: "3"),
            change: M7PresentationTestFactory.SoldChange());
        var latest = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(
                currentId: "2",
                nextId: "3",
                extraWaiting: 2,
                revision: 12),
            previous: sold);
        Assert.Equal(SalesQueueAnimationRequest.None, latest.AnimationRequest);
        Assert.Contains("Waiting 3", latest.PrimaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void SpinnerStopsOutsideConnectingOrResyncing()
    {
        var connecting = M7PresentationTestFactory.Create(
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Connecting));
        var live = M7PresentationTestFactory.Create(previous: connecting);
        Assert.True(connecting.IsSpinnerActive);
        Assert.False(live.IsSpinnerActive);
    }

    [Fact]
    public void HudHidden_StopsSpinnerWork()
    {
        var hidden = M7PresentationTestFactory.Create(
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Resyncing),
            hudVisible: false);
        Assert.Equal(SalesStatusIconKind.Spinner, hidden.IconKind);
        Assert.False(hidden.IsSpinnerActive);
    }

    [Fact]
    public void AnimationDurations_AreWithinUxBounds()
    {
        Assert.InRange(SalesAnimationDurations.SoldTransition.TotalMilliseconds, 150, 250);
        Assert.InRange(SalesAnimationDurations.CurrentTurnEnter.TotalMilliseconds, 300, 500);
        Assert.InRange(SalesAnimationDurations.NextTurnEnter.TotalMilliseconds, 100, 250);
    }
}

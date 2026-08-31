using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales.M7;

public sealed class SalesPersonalAlertTests
{
    [Fact]
    public void TrustedLiveCurrentSelf_OverridesNormalFields()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true));
        Assert.Equal(SalesQueueContentMode.CurrentTurnSelf, result.ContentMode);
        Assert.Equal(SalesQueueAccentKind.CurrentTurn, result.AccentKind);
        Assert.Equal("Sell now", result.PrimaryText);
        Assert.Equal(SalesQueueVisibleFields.None, result.VisibleFields);
        Assert.DoesNotContain("Waiting", result.PrimaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverObservedCurrentSelf_DoesNotRaiseStrongAlert()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(
                currentSelf: true,
                currentTrust: SaleObservationTrust.NeverObserved));
        Assert.Equal(SalesQueueContentMode.Normal, result.ContentMode);
        Assert.Equal(SalesQueueAnimationRequest.None, result.AnimationRequest);
    }

    [Theory]
    [InlineData(SalesFeatureHealthState.Paused)]
    [InlineData(SalesFeatureHealthState.Resyncing)]
    [InlineData(SalesFeatureHealthState.Degraded)]
    [InlineData(SalesFeatureHealthState.Disconnected)]
    [InlineData(SalesFeatureHealthState.Error)]
    public void NonLiveHealth_SuppressesNewCurrentAlert(SalesFeatureHealthState health)
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true),
            health: M7PresentationTestFactory.Health(health));
        Assert.NotEqual(SalesQueueContentMode.CurrentTurnSelf, result.ContentMode);
        Assert.Equal(SalesQueueAnimationRequest.None, result.AnimationRequest);
    }

    [Fact]
    public void LiveArrivalForTrustedCurrentSelf_EntersOnce()
    {
        var resyncing = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true),
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Resyncing));
        var live = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true),
            previous: resyncing);
        var repeated = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true),
            previous: live);
        Assert.Equal(SalesQueueAnimationRequest.CurrentTurnEnter, live.AnimationRequest);
        Assert.Equal(SalesQueueAnimationRequest.None, repeated.AnimationRequest);
    }

    [Fact]
    public void CurrentAlertPausedAfterActivation_IsRetainedWithGuidance()
    {
        var live = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true));
        var paused = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(
                currentSelf: true,
                currentTrust: SaleObservationTrust.TemporarilyUntrusted),
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Paused),
            previous: live);
        Assert.Equal(SalesQueueContentMode.CurrentTurnSelf, paused.ContentMode);
        Assert.Equal("Sell now", paused.PrimaryText);
        Assert.Contains("open", paused.SecondaryText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SalesQueueAnimationRequest.None, paused.AnimationRequest);
    }

    [Theory]
    [InlineData(SalesFeatureHealthState.Paused)]
    [InlineData(SalesFeatureHealthState.Disconnected)]
    [InlineData(SalesFeatureHealthState.Error)]
    public void ActiveCurrentAlert_NeverHidesHealthWarning(SalesFeatureHealthState health)
    {
        var live = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true));
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(
                currentSelf: true,
                currentTrust: SaleObservationTrust.TemporarilyUntrusted),
            health: M7PresentationTestFactory.Health(health),
            previous: live);
        Assert.Equal(SalesQueueContentMode.CurrentTurnSelf, result.ContentMode);
        Assert.True(result.IsTwoLine);
        Assert.Contains(result.StatusText, result.SecondaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void PausedToLiveSameCurrent_DoesNotRepeatPulse()
    {
        var live = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true));
        var paused = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(
                currentSelf: true,
                currentTrust: SaleObservationTrust.TemporarilyUntrusted),
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Paused),
            previous: live);
        var restored = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true),
            previous: paused);
        Assert.Equal(SalesQueueContentMode.CurrentTurnSelf, restored.ContentMode);
        Assert.Equal(SalesQueueAnimationRequest.None, restored.AnimationRequest);
    }

    [Fact]
    public void CurrentChangesAwayFromSelf_EndsAlert()
    {
        var previous = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true));
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentId: "other"),
            previous: previous);
        Assert.Equal(SalesQueueContentMode.Normal, result.ContentMode);
    }

    [Fact]
    public void SameSaleReentryAfterLeaving_AllowsNewAlert()
    {
        var first = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true));
        var away = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentId: "other"),
            previous: first);
        var reentry = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true),
            previous: away);
        Assert.Equal(SalesQueueContentMode.CurrentTurnSelf, reentry.ContentMode);
        Assert.Equal(SalesQueueAnimationRequest.CurrentTurnEnter, reentry.AnimationRequest);
    }

    [Fact]
    public void SalesOff_ClearsCurrentAlertState()
    {
        var previous = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true));
        var off = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(tracking: false, currentSelf: true),
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Disabled),
            previous: previous);
        Assert.Equal(SalesQueueContentMode.Hidden, off.ContentMode);
        Assert.Equal(SalesQueueAnimationRequest.None, off.AnimationRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrustedLiveNextSelf_OverridesNextDisplayOption(bool showNext)
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(nextSelf: true),
            options: new SalesQueueDisplayOptions(true, true, false, showNext));
        Assert.Equal(SalesQueueContentMode.NextTurnSelf, result.ContentMode);
        Assert.Contains("I'm next", result.SecondaryText, StringComparison.Ordinal);
        Assert.Equal(SalesQueueAccentKind.NextTurn, result.AccentKind);
    }

    [Fact]
    public void NextAlert_IsWeakerAndKeepsNormalQueueInformation()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(nextSelf: true));
        Assert.Equal(SalesQueueAccentKind.NextTurn, result.AccentKind);
        Assert.Contains("Current Seller", result.PrimaryText, StringComparison.Ordinal);
        Assert.Contains("I'm next", result.SecondaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverObservedNextSelf_IsSuppressed()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(
                nextSelf: true,
                nextTrust: SaleObservationTrust.NeverObserved));
        Assert.Equal(SalesQueueContentMode.Normal, result.ContentMode);
    }

    [Theory]
    [InlineData(SalesFeatureHealthState.Paused)]
    [InlineData(SalesFeatureHealthState.Resyncing)]
    [InlineData(SalesFeatureHealthState.Degraded)]
    public void NonLiveHealth_SuppressesNewNextAlert(SalesFeatureHealthState health)
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(nextSelf: true),
            health: M7PresentationTestFactory.Health(health));
        Assert.Equal(SalesQueueContentMode.Normal, result.ContentMode);
    }

    [Fact]
    public void ActiveNextAlert_Paused_IsRetainedWithGuidance()
    {
        var live = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(nextSelf: true));
        var paused = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(
                nextSelf: true,
                nextTrust: SaleObservationTrust.TemporarilyUntrusted),
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Paused),
            previous: live);
        Assert.Equal(SalesQueueContentMode.NextTurnSelf, paused.ContentMode);
        Assert.Contains("I'm next", paused.SecondaryText, StringComparison.Ordinal);
        Assert.Contains("open", paused.SecondaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NextBecomesCurrentSelf_CurrentAlertWins()
    {
        var next = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(nextSelf: true));
        var current = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true, currentId: "2"),
            previous: next);
        Assert.Equal(SalesQueueContentMode.CurrentTurnSelf, current.ContentMode);
        Assert.Equal(SalesQueueAnimationRequest.CurrentTurnEnter, current.AnimationRequest);
    }

    [Fact]
    public void NextChangesAwayFromSelf_RemovesAlert()
    {
        var previous = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(nextSelf: true));
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(nextId: "3"),
            previous: previous);
        Assert.Equal(SalesQueueContentMode.Normal, result.ContentMode);
    }
}

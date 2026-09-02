using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales.M7;

public sealed class SalesStatusAndQueuePresentationTests
{
    [Theory]
    [InlineData(SalesFeatureHealthState.Live, SalesHealthVisualMode.Live, SalesStatusIconKind.LiveDot, false)]
    [InlineData(SalesFeatureHealthState.Connecting, SalesHealthVisualMode.Connecting, SalesStatusIconKind.Spinner, true)]
    [InlineData(SalesFeatureHealthState.Resyncing, SalesHealthVisualMode.Resyncing, SalesStatusIconKind.Spinner, true)]
    [InlineData(SalesFeatureHealthState.Paused, SalesHealthVisualMode.Paused, SalesStatusIconKind.Warning, false)]
    [InlineData(SalesFeatureHealthState.Degraded, SalesHealthVisualMode.Degraded, SalesStatusIconKind.Warning, false)]
    [InlineData(SalesFeatureHealthState.Disconnected, SalesHealthVisualMode.Disconnected, SalesStatusIconKind.Error, false)]
    [InlineData(SalesFeatureHealthState.Error, SalesHealthVisualMode.Error, SalesStatusIconKind.Error, false)]
    public void HealthState_MapsToSemanticVisual(
        SalesFeatureHealthState state,
        SalesHealthVisualMode expectedMode,
        SalesStatusIconKind expectedIcon,
        bool spinner)
    {
        var result = M7PresentationTestFactory.Create(
            health: M7PresentationTestFactory.Health(state));
        Assert.Equal(expectedMode, result.HealthMode);
        Assert.Equal(expectedIcon, result.IconKind);
        Assert.Equal(spinner, result.IsSpinnerActive);
    }

    [Fact]
    public void Disabled_HidesQueueAndStatus()
    {
        var result = M7PresentationTestFactory.Create(
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Disabled));
        Assert.False(result.IsVisible);
        Assert.Equal(SalesQueueContentMode.Hidden, result.ContentMode);
        Assert.Equal(SalesHealthVisualMode.Hidden, result.HealthMode);
    }

    [Fact]
    public void QueueTrackingOff_HidesEvenIfHealthClaimsLive()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(tracking: false));
        Assert.False(result.IsVisible);
    }

    [Fact]
    public void Live_HasAccessibleMeaningWithoutLongStatusText()
    {
        var result = M7PresentationTestFactory.Create();
        Assert.Equal(string.Empty, result.StatusText);
        Assert.Equal("Sales live", result.AccessibleStatus);
    }

    [Fact]
    public void Paused_UsesGenericRemoteStatusWithoutChannelInstruction()
    {
        var result = M7PresentationTestFactory.Create(
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Paused),
            channel: "#🚒판매모집");
        Assert.Equal("Remote Sales paused", result.StatusText);
        Assert.DoesNotContain("#🚒판매모집", result.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SalesFeatureHealthState.Connecting, "Connecting to Discord")]
    [InlineData(SalesFeatureHealthState.Resyncing, "Resyncing sales status")]
    [InlineData(SalesFeatureHealthState.Degraded, "Partial sales status")]
    [InlineData(SalesFeatureHealthState.Disconnected, "Discord disconnected")]
    [InlineData(SalesFeatureHealthState.Error, "Sales sensor unavailable")]
    public void HealthText_IsUserFacingNotEnum(
        SalesFeatureHealthState state,
        string expected)
    {
        var result = M7PresentationTestFactory.Create(
            health: M7PresentationTestFactory.Health(state));
        Assert.Equal(expected, result.StatusText);
        Assert.DoesNotContain("SalesFeatureHealthState", result.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void NonLiveHealth_PreservesTrustedQueueContent()
    {
        var result = M7PresentationTestFactory.Create(
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Paused));
        Assert.Contains("Current Seller", result.PrimaryText, StringComparison.Ordinal);
        Assert.Contains("Remote Sales paused", result.SecondaryText, StringComparison.Ordinal);
        Assert.True(result.IsTwoLine);
    }

    [Fact]
    public void UncertainEmptyQueue_DoesNotClaimNoOneWaiting()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(empty: true),
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Resyncing));
        Assert.Equal("Resyncing sales status", result.PrimaryText);
        Assert.DoesNotContain("No one", result.PrimaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveEmptyQueue_ShowsLocalizedEmptyTextAndLiveDot()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(empty: true));
        Assert.Equal(SalesQueueContentMode.Empty, result.ContentMode);
        Assert.Equal("No one waiting", result.PrimaryText);
        Assert.Equal(SalesStatusIconKind.LiveDot, result.IconKind);
    }

    [Fact]
    public void DefaultFields_AreCurrentSellerAndWaitingCount()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(extraWaiting: 2));
        Assert.Contains("Current Seller", result.PrimaryText, StringComparison.Ordinal);
        Assert.Contains("Waiting 3", result.PrimaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("Product", result.PrimaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("Next", result.PrimaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitingCount_ExcludesCurrentSeller()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(extraWaiting: 3));
        Assert.Contains("Waiting 4", result.PrimaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductOnWithMapping_ShowsProduct()
    {
        var product = new SaleProduct("p", "Bunker", "1", "emoji");
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(product: product),
            options: new SalesQueueDisplayOptions(true, true, true, false));
        Assert.Contains("Bunker", result.PrimaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("Product Bunker", result.PrimaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductOnWithoutMapping_HasNoPlaceholder()
    {
        var result = M7PresentationTestFactory.Create(
            options: new SalesQueueDisplayOptions(true, true, true, false));
        Assert.DoesNotContain("Product", result.PrimaryText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void NormalNextUser_RespectsDisplayOption(bool showNext, bool expected)
    {
        var result = M7PresentationTestFactory.Create(
            options: new SalesQueueDisplayOptions(true, true, false, showNext));
        Assert.Equal(
            expected,
            result.PrimaryText.Contains("Next NextUser", StringComparison.Ordinal) ||
            result.SecondaryText.Contains("Next NextUser", StringComparison.Ordinal));
    }

    [Fact]
    public void AllDisplayFieldsOff_UsesLocalizedNeutralText()
    {
        var result = M7PresentationTestFactory.Create(
            options: new SalesQueueDisplayOptions(false, false, false, false));
        Assert.Equal("No fields", result.PrimaryText);
    }
}

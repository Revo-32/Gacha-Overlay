using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales.M7;

public sealed class SalesLayoutAndResponsiveTests
{
    private static readonly SalesQueueDisplayOptions AllFields = new(true, true, true, true);
    private static readonly SaleProduct Product = new("p", "Bunker", "1", "emoji");
    private static readonly SalesQueueFieldMeasurements Metrics = new(100, 70, 90, 90);

    [Fact]
    public void SmallContentAndEnoughWidth_UsesOneLine()
    {
        var result = M7PresentationTestFactory.Create(width: 500);
        Assert.False(result.IsTwoLine);
    }

    [Fact]
    public void ManyFieldsAtModerateWidth_UsesTwoLines()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(product: Product),
            options: AllFields,
            width: 220,
            measurements: Metrics);
        Assert.True(result.IsTwoLine);
        Assert.NotEmpty(result.SecondaryText);
    }

    [Fact]
    public void NarrowWidth_DropsNextBeforeProduct()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(product: Product),
            options: AllFields,
            width: 180,
            measurements: new SalesQueueFieldMeasurements(100, 100, 50, 100));
        Assert.False(result.VisibleFields.HasFlag(SalesQueueVisibleFields.NextWaitingUser));
        Assert.True(result.VisibleFields.HasFlag(SalesQueueVisibleFields.Product));
    }

    [Fact]
    public void NarrowerWidth_DropsProductBeforeWaitingCount()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(product: Product),
            options: AllFields,
            width: 160,
            measurements: new SalesQueueFieldMeasurements(100, 50, 100, 0));
        Assert.False(result.VisibleFields.HasFlag(SalesQueueVisibleFields.Product));
        Assert.True(result.VisibleFields.HasFlag(SalesQueueVisibleFields.WaitingCount));
    }

    [Fact]
    public void ExtremeWidth_RetainsCurrentSellerLongest()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(product: Product),
            options: AllFields,
            width: 1,
            measurements: Metrics);
        Assert.Equal(SalesQueueVisibleFields.CurrentSeller, result.VisibleFields);
    }

    [Theory]
    [InlineData(SalesFeatureHealthState.Paused)]
    [InlineData(SalesFeatureHealthState.Degraded)]
    [InlineData(SalesFeatureHealthState.Disconnected)]
    [InlineData(SalesFeatureHealthState.Error)]
    public void ActionHealthText_RemainsVisibleAtNarrowWidth(SalesFeatureHealthState health)
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(product: Product),
            health: M7PresentationTestFactory.Health(health),
            options: AllFields,
            width: 90,
            measurements: Metrics);
        Assert.True(result.IsTwoLine);
        Assert.False(string.IsNullOrWhiteSpace(result.StatusText));
        Assert.Contains(result.StatusText, result.SecondaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthMessage_ForcesSecondLineAndOptionalFieldReduction()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(product: Product),
            health: M7PresentationTestFactory.Health(SalesFeatureHealthState.Paused),
            options: AllFields,
            width: 185,
            measurements: Metrics);
        Assert.True(result.IsTwoLine);
        Assert.False(result.VisibleFields.HasFlag(SalesQueueVisibleFields.NextWaitingUser));
        Assert.Contains("paused", result.SecondaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SalesFeatureHealthState.Live, false)]
    [InlineData(SalesFeatureHealthState.Connecting, false)]
    [InlineData(SalesFeatureHealthState.Resyncing, false)]
    [InlineData(SalesFeatureHealthState.Paused, true)]
    [InlineData(SalesFeatureHealthState.Degraded, true)]
    [InlineData(SalesFeatureHealthState.Disconnected, true)]
    [InlineData(SalesFeatureHealthState.Error, true)]
    public void UltraCompact_HidesNormalDetailButKeepsActionableHealth(
        SalesFeatureHealthState health,
        bool expectedVisible)
    {
        var result = M7PresentationTestFactory.Create(
            health: M7PresentationTestFactory.Health(health),
            ultraCompact: true);
        Assert.Equal(expectedVisible, result.IsVisible);
        if (expectedVisible)
        {
            Assert.Equal(SalesQueueVisibleFields.None, result.VisibleFields);
            Assert.Equal(result.StatusText, result.PrimaryText);
        }
    }

    [Fact]
    public void IdenticalMetrics_DoNotOscillateLayout()
    {
        var first = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(product: Product),
            options: AllFields,
            width: 220,
            measurements: Metrics);
        var second = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(product: Product),
            options: AllFields,
            previous: first,
            width: 220,
            measurements: Metrics);
        Assert.Equal(first.IsTwoLine, second.IsTwoLine);
        Assert.Equal(first.VisibleFields, second.VisibleFields);
        Assert.Equal(first.PrimaryText, second.PrimaryText);
        Assert.Equal(first.SecondaryText, second.SecondaryText);
    }

    [Fact]
    public void CurrentTurn_RemainsVisibleInUltraCompact()
    {
        var result = M7PresentationTestFactory.Create(
            queue: M7PresentationTestFactory.Queue(currentSelf: true),
            ultraCompact: true);
        Assert.True(result.IsVisible);
        Assert.Equal(SalesQueueContentMode.CurrentTurnSelf, result.ContentMode);
        Assert.Equal("Sell now", result.PrimaryText);
    }
}

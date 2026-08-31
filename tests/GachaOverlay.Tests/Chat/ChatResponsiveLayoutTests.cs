using GachaOverlay.Core.Chat;

namespace GachaOverlay.Tests.Chat;

public sealed class ChatResponsiveLayoutTests
{
    private static readonly ChatResponsiveInput Typical = new(
        500,
        300,
        20,
        100,
        40,
        132,
        5,
        true,
        true);

    [Fact]
    public void GenerousMeasuredSpace_UsesFullLayout()
    {
        Assert.Equal(
            ChatResponsiveLevel.Full,
            ChatResponsiveLayout.Evaluate(Typical, ChatResponsiveLevel.Reduced));
    }

    [Fact]
    public void IntermediateMeasuredSpace_DegradesToReduced()
    {
        Assert.Equal(
            ChatResponsiveLevel.Reduced,
            ChatResponsiveLayout.Evaluate(
                Typical with { AvailableWidth = 250, AvailableHeight = 150 },
                ChatResponsiveLevel.Full));
    }

    [Fact]
    public void ConstrainedMeasuredSpace_UsesUltraCompact()
    {
        Assert.Equal(
            ChatResponsiveLevel.UltraCompact,
            ChatResponsiveLayout.Evaluate(
                Typical with { AvailableWidth = 150, AvailableHeight = 90 },
                ChatResponsiveLevel.Reduced));
    }

    [Fact]
    public void Hysteresis_PreventsBoundaryOscillation()
    {
        var nearBoundary = Typical with { AvailableWidth = 400, AvailableHeight = 230 };

        Assert.Equal(
            ChatResponsiveLevel.Full,
            ChatResponsiveLayout.Evaluate(nearBoundary, ChatResponsiveLevel.Full));
    }
}

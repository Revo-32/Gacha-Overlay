using GachaOverlay.Core.Hud.Geometry;

namespace GachaOverlay.Tests.Hud;

public sealed class M75ResizeHitTestTests
{
    [Theory]
    [InlineData(1, 1, HudResizeRegion.TopLeft)]
    [InlineData(499, 1, HudResizeRegion.TopRight)]
    [InlineData(1, 299, HudResizeRegion.BottomLeft)]
    [InlineData(499, 299, HudResizeRegion.BottomRight)]
    [InlineData(1, 150, HudResizeRegion.Left)]
    [InlineData(499, 150, HudResizeRegion.Right)]
    [InlineData(250, 1, HudResizeRegion.Top)]
    [InlineData(250, 299, HudResizeRegion.Bottom)]
    [InlineData(250, 150, HudResizeRegion.None)]
    public void Resolve_ReturnsExpectedCornerEdgeAndInterior(
        double x,
        double y,
        HudResizeRegion expected) =>
        Assert.Equal(expected, HudResizeHitTestPolicy.Resolve(x, y, 500, 300, 96));

    [Fact]
    public void CornerZone_IsLargerThanEdgeZone()
    {
        Assert.True(HudResizeHitTestPolicy.CornerDip > HudResizeHitTestPolicy.EdgeDip);
        Assert.Equal(
            HudResizeRegion.TopLeft,
            HudResizeHitTestPolicy.Resolve(18, 18, 500, 300, 96));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LockedOrInteractiveControl_TakesPrecedence(bool locked, bool interactive) =>
        Assert.Equal(
            HudResizeRegion.None,
            HudResizeHitTestPolicy.Resolve(
                1,
                1,
                500,
                300,
                144,
                locked,
                interactive));

    [Fact]
    public void DpiScale_PreservesDipSizedCornerTarget()
    {
        Assert.Equal(
            HudResizeRegion.TopLeft,
            HudResizeHitTestPolicy.Resolve(36, 36, 750, 450, 144));
        Assert.Equal(
            HudResizeRegion.None,
            HudResizeHitTestPolicy.Resolve(39, 39, 750, 450, 144));
    }
}

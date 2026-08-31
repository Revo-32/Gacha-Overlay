using GachaOverlay.Core.Hud.Geometry;

namespace GachaOverlay.Tests.Hud;

public sealed class WindowPlacementEngineTests
{
    private static readonly DisplayWorkingArea Primary = new(
        "PRIMARY",
        new HudRectangle(0, 0, 1920, 1040),
        96,
        true);

    private readonly WindowPlacementEngine _engine = new();

    [Fact]
    public void ValidSavedGeometry_IsPreserved()
    {
        var saved = new HudWindowGeometry(1200, 80, 520, 320, "PRIMARY", 96);

        var result = _engine.Resolve(saved, new[] { Primary });

        Assert.Equal(saved, result.Geometry);
        Assert.False(result.WasCorrected);
    }

    [Fact]
    public void SecondaryMonitorGeometry_IsPreserved()
    {
        var secondary = new DisplayWorkingArea(
            "SECONDARY",
            new HudRectangle(1920, 0, 2560, 1400),
            96);
        var saved = new HudWindowGeometry(2100, 100, 600, 400, "SECONDARY", 96);

        var result = _engine.Resolve(saved, new[] { Primary, secondary });

        Assert.Equal(saved, result.Geometry);
    }

    [Fact]
    public void NegativeCoordinateMonitor_IsSupported()
    {
        var left = new DisplayWorkingArea(
            "LEFT",
            new HudRectangle(-1600, 0, 1600, 900),
            96);
        var saved = new HudWindowGeometry(-1400, 100, 500, 300, "LEFT", 96);

        var result = _engine.Resolve(saved, new[] { Primary, left });

        Assert.Equal(-1400, result.Geometry.X);
        Assert.Equal("LEFT", result.Geometry.DisplayId);
        Assert.False(result.WasCorrected);
    }

    [Fact]
    public void MonitorAbovePrimary_IsSupported()
    {
        var above = new DisplayWorkingArea(
            "ABOVE",
            new HudRectangle(0, -1200, 1920, 1200),
            96);
        var saved = new HudWindowGeometry(400, -900, 600, 350, "ABOVE", 96);

        var result = _engine.Resolve(saved, new[] { Primary, above });

        Assert.Equal(saved, result.Geometry);
    }

    [Fact]
    public void RemovedMonitorGeometry_IsMovedOnlyEnoughToRecover()
    {
        var saved = new HudWindowGeometry(2300, 100, 520, 320, "REMOVED", 96);

        var result = _engine.Resolve(saved, new[] { Primary });

        Assert.True(result.WasCorrected);
        Assert.Equal(Primary.Bounds.Right - WindowPlacementEngine.MinimumVisibleWidthDip, result.Geometry.X);
        Assert.Equal("PRIMARY", result.Geometry.DisplayId);
    }

    [Fact]
    public void ResolutionShrink_CorrectsOffScreenPosition()
    {
        var smaller = Primary with { Bounds = new HudRectangle(0, 0, 1280, 720) };
        var saved = new HudWindowGeometry(1600, 800, 500, 300, "PRIMARY", 96);

        var result = _engine.Resolve(saved, new[] { smaller });

        Assert.True(result.WasCorrected);
        Assert.True(result.Geometry.X < 1280);
        Assert.True(result.Geometry.Y < 720);
    }

    [Fact]
    public void FullyOffScreenGeometry_IsRecovered()
    {
        var saved = new HudWindowGeometry(-4000, -3000, 520, 320, null, 96);

        var result = _engine.Resolve(saved, new[] { Primary });

        var intersection = result.Geometry.Rectangle.Intersection(Primary.Bounds);
        Assert.True(intersection.Width >= WindowPlacementEngine.MinimumVisibleWidthDip);
        Assert.True(intersection.Height >= WindowPlacementEngine.MinimumVisibleHeightDip);
    }

    [Fact]
    public void PartiallyVisibleGeometry_IsNotOverCorrected()
    {
        var saved = new HudWindowGeometry(-400, 100, 520, 320, "PRIMARY", 96);

        var result = _engine.Resolve(saved, new[] { Primary });

        Assert.Equal(saved, result.Geometry);
        Assert.False(result.WasCorrected);
    }

    [Fact]
    public void OversizedGeometry_IsLimitedToWorkingArea()
    {
        var saved = new HudWindowGeometry(0, 0, 5000, 4000, "PRIMARY", 96);

        var result = _engine.Resolve(saved, new[] { Primary });

        Assert.Equal(Primary.Bounds.Width, result.Geometry.Width);
        Assert.Equal(Primary.Bounds.Height, result.Geometry.Height);
        Assert.True(result.WasCorrected);
    }

    [Fact]
    public void UndersizedGeometry_EnforcesTechnicalMinimum()
    {
        var saved = new HudWindowGeometry(100, 100, 20, 30, "PRIMARY", 96);

        var result = _engine.Resolve(saved, new[] { Primary });

        Assert.Equal(WindowPlacementEngine.MinimumWidthDip, result.Geometry.Width);
        Assert.Equal(WindowPlacementEngine.MinimumHeightDip, result.Geometry.Height);
    }

    [Fact]
    public void DpiChange_ScalesSavedWindowSize()
    {
        var highDpi = Primary with { Dpi = 144 };
        var saved = new HudWindowGeometry(200, 100, 520, 320, "PRIMARY", 96);

        var result = _engine.Resolve(saved, new[] { highDpi });

        Assert.Equal(780, result.Geometry.Width);
        Assert.Equal(480, result.Geometry.Height);
        Assert.Equal(144, result.Geometry.Dpi);
    }
}

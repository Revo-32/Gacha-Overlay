namespace GachaOverlay.Core.Hud.Geometry;

public enum HudResizeRegion
{
    None,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public static class HudResizeHitTestPolicy
{
    public const double EdgeDip = 9;
    public const double CornerDip = 25;

    public static HudResizeRegion Resolve(
        double x,
        double y,
        double width,
        double height,
        double dpi,
        bool isLocked = false,
        bool isInteractive = false)
    {
        if (isLocked || isInteractive ||
            !double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            width <= 0 || height <= 0)
        {
            return HudResizeRegion.None;
        }

        var scale = double.IsFinite(dpi) && dpi > 0 ? dpi / 96d : 1;
        var edge = Math.Max(5, Math.Round(EdgeDip * scale, MidpointRounding.AwayFromZero));
        var corner = Math.Max(edge, Math.Round(CornerDip * scale, MidpointRounding.AwayFromZero));
        var leftEdge = x >= 0 && x < edge;
        var rightEdge = x <= width && x > width - edge;
        var topEdge = y >= 0 && y < edge;
        var bottomEdge = y <= height && y > height - edge;
        var leftCorner = x >= 0 && x < corner;
        var rightCorner = x <= width && x > width - corner;
        var topCorner = y >= 0 && y < corner;
        var bottomCorner = y <= height && y > height - corner;

        return (leftCorner, rightCorner, topCorner, bottomCorner) switch
        {
            (true, _, true, _) => HudResizeRegion.TopLeft,
            (_, true, true, _) => HudResizeRegion.TopRight,
            (true, _, _, true) => HudResizeRegion.BottomLeft,
            (_, true, _, true) => HudResizeRegion.BottomRight,
            _ => (leftEdge, rightEdge, topEdge, bottomEdge) switch
            {
                (true, _, _, _) => HudResizeRegion.Left,
                (_, true, _, _) => HudResizeRegion.Right,
                (_, _, true, _) => HudResizeRegion.Top,
                (_, _, _, true) => HudResizeRegion.Bottom,
                _ => HudResizeRegion.None,
            },
        };
    }
}

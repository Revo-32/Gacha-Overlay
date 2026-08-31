namespace GachaOverlay.Core.Hud.Geometry;

public sealed record HudWindowGeometry(
    double X,
    double Y,
    double Width,
    double Height,
    string? DisplayId = null,
    double Dpi = 96)
{
    public HudRectangle Rectangle => new(X, Y, Width, Height);
}

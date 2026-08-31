namespace GachaOverlay.Core.Hud.Geometry;

public readonly record struct HudRectangle(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    public bool IsFiniteAndPositive =>
        double.IsFinite(X) &&
        double.IsFinite(Y) &&
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width > 0 &&
        Height > 0;

    public HudRectangle Intersection(HudRectangle other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top
            ? new HudRectangle(0, 0, 0, 0)
            : new HudRectangle(left, top, right - left, bottom - top);
    }
}

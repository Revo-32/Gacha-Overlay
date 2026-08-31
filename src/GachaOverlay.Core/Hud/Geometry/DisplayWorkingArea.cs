namespace GachaOverlay.Core.Hud.Geometry;

public sealed record DisplayWorkingArea(
    string Id,
    HudRectangle Bounds,
    double Dpi,
    bool IsPrimary = false)
{
    public double Scale => NormalizeDpi(Dpi) / 96d;

    public bool IsValid => !string.IsNullOrWhiteSpace(Id) && Bounds.IsFiniteAndPositive;

    public static double NormalizeDpi(double dpi) =>
        double.IsFinite(dpi) && dpi >= 48 && dpi <= 768 ? dpi : 96d;
}

namespace GachaOverlay.Core.Hud;

public static class HudSurfaceOpacityPolicy
{
    public static double CalculateEffectiveOpacity(double globalOpacity, double localOpacity)
    {
        var global = HudSettingsDefaults.NormalizeSurfaceOpacity(globalOpacity);
        var local = double.IsFinite(localOpacity) ? Math.Clamp(localOpacity, 0, 1) : 1;
        return global * local;
    }

    public static byte CalculateAlpha(double globalOpacity, double localOpacity) =>
        (byte)Math.Round(
            CalculateEffectiveOpacity(globalOpacity, localOpacity) * byte.MaxValue,
            MidpointRounding.AwayFromZero);
}

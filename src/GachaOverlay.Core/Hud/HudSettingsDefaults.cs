namespace GachaOverlay.Core.Hud;

public static class HudSettingsDefaults
{
    public const double SurfaceOpacity = 0.82;
    public const string ModifierDragModifier = "Alt";

    public static double NormalizeSurfaceOpacity(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, 0, 1)
            : SurfaceOpacity;

    public static string NormalizeModifierDragModifier(string? value) =>
        string.Equals(value?.Trim(), "Alt", StringComparison.OrdinalIgnoreCase)
            ? ModifierDragModifier
            : ModifierDragModifier;
}

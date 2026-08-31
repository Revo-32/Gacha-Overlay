namespace GachaOverlay.Core.Hud.Hotkeys;

public sealed record HotkeySetting
{
    public static HotkeySetting DefaultLockToggle { get; } = new()
    {
        Modifiers = string.Empty,
        Key = "F10",
    };

    public static HotkeySetting DefaultVisibilityToggle { get; } = new()
    {
        Modifiers = string.Empty,
        Key = "F9",
    };

    public string Modifiers { get; init; } = string.Empty;

    public string Key { get; init; } = "F10";
}

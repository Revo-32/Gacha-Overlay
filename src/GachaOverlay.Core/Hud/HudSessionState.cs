namespace GachaOverlay.Core.Hud;

public sealed record HudSessionState(
    bool IsLocked,
    bool UserHudEnabled,
    HudVisibilityMode VisibilityMode,
    bool IsTargetGameForeground,
    bool HasInitialConnectionReady)
{
    public bool IsClickThrough => IsLocked;

    public bool EffectiveVisible =>
        HasInitialConnectionReady &&
        UserHudEnabled &&
        (VisibilityMode == HudVisibilityMode.Always ||
         (VisibilityMode == HudVisibilityMode.GameForegroundOnly &&
          IsTargetGameForeground));

    public static HudSessionState CreateDefault(
        HudVisibilityMode visibilityMode = HudVisibilityMode.Always) =>
        new(
            IsLocked: true,
            UserHudEnabled: true,
            VisibilityMode: visibilityMode,
            IsTargetGameForeground: false,
            HasInitialConnectionReady: false);
}

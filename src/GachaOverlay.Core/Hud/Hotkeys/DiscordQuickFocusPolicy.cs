namespace GachaOverlay.Core.Hud.Hotkeys;

public readonly record struct QuickFocusDecision(bool Consume, bool RequestFocus);

public sealed class DiscordQuickFocusPolicy
{
    private bool _down;
    private bool _consumed;
    public QuickFocusDecision HandleT(bool down, bool gtaForeground, bool modifiers, bool injected, bool enabled)
    {
        if (injected) return default;
        if (!down)
        {
            var consumed = _consumed;
            _down = false;
            _consumed = false;
            return new(consumed, false);
        }
        if (_down) return new(_consumed, false);
        _down = true;
        _consumed = enabled && gtaForeground && !modifiers;
        return new(_consumed, _consumed);
    }
    public void Reset(bool alreadyDown = false) { _down = alreadyDown; _consumed = false; }
}

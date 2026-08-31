using GachaOverlay.Core.Settings;

namespace GachaOverlay.Core.Chat;

public static class ChatStylePresets
{
    public static AppSettings Apply(AppSettings current, ChatStylePreset preset)
    {
        ArgumentNullException.ThrowIfNull(current);
        return preset switch
        {
            ChatStylePreset.Clean => current with
            {
                HudSurfaceOpacity = 0.62,
                ChatLayoutMode = ChatLayoutMode.Balanced,
                ChatShowTime = true,
                ChatFontPreset = ChatFontPreset.Pretendard,
                ChatFontSizePoints = 12.5,
                ChatNicknameOutlineEnabled = false,
                ChatMessageOutlineEnabled = false,
                ChatNicknameOutlineThickness = 0.5,
                ChatMessageOutlineThickness = 0.5,
                ChatLineHeightMultiplier = 1.42,
                ChatMessageSpacing = 1.5,
                ChatMaxLines = 2,
                ChatShowImages = true,
            },
            ChatStylePreset.HighReadability => current with
            {
                HudSurfaceOpacity = 0.9,
                ChatLayoutMode = ChatLayoutMode.Balanced,
                ChatShowTime = true,
                ChatFontPreset = ChatFontPreset.WantedSans,
                ChatFontSizePoints = 14,
                ChatNicknameOutlineEnabled = true,
                ChatMessageOutlineEnabled = true,
                ChatOutlineThickness = 0.65,
                ChatNicknameOutlineThickness = 0.75,
                ChatMessageOutlineThickness = 0.75,
                ChatLineHeightMultiplier = 1.5,
                ChatMessageSpacing = 2.5,
                ChatMaxLines = 2,
                ChatShowImages = true,
            },
            ChatStylePreset.GtaLegacy => current with
            {
                HudSurfaceOpacity = 0.42,
                ChatLayoutMode = ChatLayoutMode.Compact,
                ChatShowTime = false,
                ChatFontPreset = ChatFontPreset.Cafe24ProSlim,
                ChatFontSizePoints = 12.25,
                ChatNicknameOutlineEnabled = false,
                ChatMessageOutlineEnabled = false,
                ChatOutlineThickness = 0.35,
                ChatNicknameOutlineThickness = 0.25,
                ChatMessageOutlineThickness = 0.25,
                ChatLineHeightMultiplier = 1.32,
                ChatMessageSpacing = 0.5,
                ChatMaxLines = 2,
                ChatShowImages = true,
            },
            _ => current with
            {
                HudSurfaceOpacity = 0.68,
                ChatLayoutMode = ChatLayoutMode.Compact,
                ChatShowTime = false,
                ChatFontPreset = ChatFontPreset.Kimm,
                ChatFontSizePoints = 12.5,
                ChatNicknameOutlineEnabled = false,
                ChatMessageOutlineEnabled = false,
                ChatOutlineThickness = 0.4,
                ChatNicknameOutlineThickness = 0.5,
                ChatMessageOutlineThickness = 0.5,
                ChatLineHeightMultiplier = 1.4,
                ChatMessageSpacing = 1.25,
                ChatMaxLines = 2,
                ChatShowImages = true,
            },
        };
    }

    public static ChatStylePreset? Match(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        foreach (var preset in Enum.GetValues<ChatStylePreset>())
        {
            if (CreateSignature(current) == CreateSignature(Apply(current, preset)))
            {
                return preset;
            }
        }

        return null;
    }

    private static PresetSignature CreateSignature(AppSettings settings) => new(
        settings.HudSurfaceOpacity,
        settings.ChatLayoutMode,
        settings.ChatShowTime,
        settings.ChatFontPreset,
        settings.ChatFontSizePoints,
        settings.ChatNicknameOutlineEnabled,
        settings.ChatMessageOutlineEnabled,
        settings.ChatOutlineThickness,
        settings.ChatNicknameOutlineThickness,
        settings.ChatMessageOutlineThickness,
        settings.ChatLineHeightMultiplier,
        settings.ChatMessageSpacing,
        settings.ChatMaxLines,
        settings.ChatShowImages);

    private sealed record PresetSignature(
        double HudSurfaceOpacity,
        ChatLayoutMode LayoutMode,
        bool ShowTime,
        ChatFontPreset FontPreset,
        double FontSizePoints,
        bool NicknameOutline,
        bool MessageOutline,
        double OutlineThickness,
        double NicknameOutlineThickness,
        double MessageOutlineThickness,
        double LineHeightMultiplier,
        double MessageSpacing,
        int MaxLines,
        bool ShowImages);
}

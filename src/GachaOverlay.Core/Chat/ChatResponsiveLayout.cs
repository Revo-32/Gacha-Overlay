namespace GachaOverlay.Core.Chat;

public enum ChatResponsiveLevel
{
    Full,
    Reduced,
    UltraCompact,
}

public readonly record struct ChatResponsiveInput(
    double AvailableWidth,
    double AvailableHeight,
    double LineHeight,
    double MaximumNicknameWidth,
    double TimeWidth,
    double ThumbnailExtent,
    int VisibleMessageCount,
    bool ImagesEnabled,
    bool TimeEnabled);

public static class ChatResponsiveLayout
{
    public static ChatResponsiveLevel Evaluate(
        ChatResponsiveInput input,
        ChatResponsiveLevel previous)
    {
        if (!double.IsFinite(input.AvailableWidth) ||
            !double.IsFinite(input.AvailableHeight) ||
            input.AvailableWidth <= 0 ||
            input.AvailableHeight <= 0)
        {
            return previous;
        }

        var lineHeight = Math.Max(1, input.LineHeight);
        var fullWidth = input.MaximumNicknameWidth +
            (input.TimeEnabled ? input.TimeWidth : 0) +
            (input.ImagesEnabled ? Math.Min(input.ThumbnailExtent, input.AvailableWidth * 0.35) : 0) +
            lineHeight * 7;
        var fullHeight = Math.Min(Math.Max(1, input.VisibleMessageCount), 3) * lineHeight * 2.1;
        var reducedWidth = input.MaximumNicknameWidth + lineHeight * 5;
        var reducedHeight = Math.Min(Math.Max(1, input.VisibleMessageCount), 4) * lineHeight * 1.55;
        var hysteresis = lineHeight * 1.25;

        var fullFits = input.AvailableWidth >= fullWidth && input.AvailableHeight >= fullHeight;
        var reducedFits = input.AvailableWidth >= reducedWidth &&
            input.AvailableHeight >= reducedHeight;

        return previous switch
        {
            ChatResponsiveLevel.Full when input.AvailableWidth >= fullWidth - hysteresis &&
                input.AvailableHeight >= fullHeight - hysteresis => ChatResponsiveLevel.Full,
            ChatResponsiveLevel.UltraCompact when input.AvailableWidth < reducedWidth + hysteresis ||
                input.AvailableHeight < reducedHeight + hysteresis => ChatResponsiveLevel.UltraCompact,
            _ when fullFits => ChatResponsiveLevel.Full,
            _ when reducedFits => ChatResponsiveLevel.Reduced,
            _ => ChatResponsiveLevel.UltraCompact,
        };
    }
}

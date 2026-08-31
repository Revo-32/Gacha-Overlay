using GachaOverlay.Core.Settings;

namespace GachaOverlay.Core.Chat;

public readonly record struct ChatLayoutPresentation(
    bool IsCompact,
    bool IsBalanced,
    bool IsUltraCompact,
    bool ShowTime,
    bool ShowImages,
    bool CanEnlarge)
{
    public static ChatLayoutPresentation Resolve(
        AppSettings settings,
        ChatResponsiveLevel responsiveLevel)
    {
        var ultra = responsiveLevel == ChatResponsiveLevel.UltraCompact;
        var compact = settings.ChatLayoutMode == ChatLayoutMode.Compact && !ultra;
        var full = responsiveLevel == ChatResponsiveLevel.Full;
        var showImages = settings.ChatShowImages && full;
        return new ChatLayoutPresentation(
            compact,
            !compact && !ultra,
            ultra,
            settings.ChatShowTime && full && settings.ChatLayoutMode == ChatLayoutMode.Balanced,
            showImages,
            showImages && settings.ChatImageMode == ChatImageMode.ThumbnailAndEnlarge);
    }
}

public readonly record struct ChatEnrichmentIdentity(
    string MessageId,
    long Generation,
    int Revision);

public static class ChatEnrichmentGuard
{
    public static bool IsCurrent(
        ChatEnrichmentIdentity expected,
        ChatEnrichmentIdentity actual,
        bool itemStillExists) =>
        itemStillExists && expected == actual;
}

public static class ChatAutoScrollPolicy
{
    public static bool ShouldScrollToLatest(ChatPresentationChangeKind changeKind) =>
        changeKind is ChatPresentationChangeKind.SnapshotAdd or ChatPresentationChangeKind.Add;
}

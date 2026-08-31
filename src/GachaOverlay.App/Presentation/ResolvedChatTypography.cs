using System.Windows;
using System.Windows.Media;
using GachaOverlay.Core.Chat;
using FontFamily = System.Windows.Media.FontFamily;

namespace GachaOverlay.App.Presentation;

internal enum ChatFontResolutionSource
{
    Bundled,
    System,
    Fallback,
}

internal enum ChatFontFallbackReason
{
    BundledDirectoryMissing,
    BundledFamilyNotFound,
    FamilyMetadataMismatch,
    TypefaceUnavailable,
    SystemFontNotInstalled,
    SystemFallbackUnavailable,
}

internal sealed record ResolvedChatFontRole(
    FontFamily FontFamily,
    FontWeight FontWeight,
    string ResolvedDisplayName,
    ChatFontResolutionSource Source,
    bool IsFallback,
    ChatFontFallbackReason? FallbackReason);

internal sealed record ResolvedChatTypography(
    ChatFontPreset RequestedFont,
    string RequestedDisplayName,
    ResolvedChatFontRole Nickname,
    ResolvedChatFontRole Message)
{
    public bool IsFallback => Nickname.IsFallback || Message.IsFallback;

    public ChatFontFallbackReason? FallbackReason =>
        Nickname.FallbackReason ?? Message.FallbackReason;

    public string ResolvedSummary =>
        $"{Nickname.ResolvedDisplayName} {Nickname.FontWeight} / " +
        $"{Message.ResolvedDisplayName} {Message.FontWeight}";
}

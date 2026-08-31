using System.Collections.ObjectModel;

namespace GachaOverlay.Core.Themes;

public enum ColorThemeId
{
    GitHubDark,
    OneDarkPro,
    Nord,
    TokyoNight,
    Monokai,
}

public enum SemanticColorToken
{
    AppBackground,
    SurfaceBase,
    SurfaceRaised,
    SurfaceElevated,
    SurfaceHover,
    SurfacePressed,
    SurfaceSelected,
    BorderSubtle,
    BorderStrong,
    Divider,
    TextPrimary,
    TextSecondary,
    TextMuted,
    TextDisabled,
    AccentPrimary,
    AccentHover,
    AccentPressed,
    AccentSubtle,
    ChatMessage,
    ChatNickname,
    ChatMention,
    ChatSelfMention,
    ChatOutline,
    ChatMentionBackground,
    ChatSelfMentionBackground,
    StatusLive,
    StatusInfo,
    StatusWarning,
    StatusError,
    FocusRing,
    Selection,
    MediaOverlay,
    ScrollTrack,
    ScrollThumb,
    ScrollThumbHover,
    ScrollThumbDragging,
}

public sealed record ColorThemeDefinition(
    ColorThemeId Id,
    string DisplayName,
    string DescriptionResourceKey,
    IReadOnlyDictionary<SemanticColorToken, string> Colors,
    IReadOnlyList<SemanticColorToken> Swatches);

public static class ColorThemeCatalog
{
    private static readonly IReadOnlyList<SemanticColorToken> DefaultSwatches =
        Array.AsReadOnly<SemanticColorToken>(
        [
        SemanticColorToken.SurfaceBase,
        SemanticColorToken.ChatMessage,
        SemanticColorToken.ChatNickname,
        SemanticColorToken.ChatMention,
        SemanticColorToken.StatusLive,
        ]);

    private static readonly IReadOnlyList<ColorThemeDefinition> Definitions =
        Array.AsReadOnly<ColorThemeDefinition>(
        [
        Create(
            ColorThemeId.GitHubDark,
            "GitHub Dark",
            "ColorThemeGitHubDarkDescription",
            appBackground: "#0D1117",
            surfaceBase: "#161B22",
            surfaceRaised: "#21262D",
            surfaceElevated: "#272D35",
            borderStrong: "#30363D",
            textPrimary: "#F0F6FC",
            textSecondary: "#C9D1D9",
            textMuted: "#8B949E",
            chatNickname: "#58A6FF",
            chatMention: "#A371F7",
            chatSelfMention: "#3FB950",
            accentPrimary: "#58A6FF",
            statusLive: "#3FB950",
            statusInfo: "#58A6FF",
            statusWarning: "#D29922",
            statusError: "#F85149"),
        Create(
            ColorThemeId.OneDarkPro,
            "One Dark Pro",
            "ColorThemeOneDarkProDescription",
            appBackground: "#1E222A",
            surfaceBase: "#282C34",
            surfaceRaised: "#303540",
            surfaceElevated: "#353B46",
            borderStrong: "#3E4451",
            textPrimary: "#E8EBF0",
            textSecondary: "#C8CDD5",
            textMuted: "#8D949F",
            chatNickname: "#61AFEF",
            chatMention: "#C678DD",
            chatSelfMention: "#98C379",
            accentPrimary: "#56B6C2",
            statusLive: "#98C379",
            statusInfo: "#61AFEF",
            statusWarning: "#E5C07B",
            statusError: "#E06C75"),
        Create(
            ColorThemeId.Nord,
            "Nord",
            "ColorThemeNordDescription",
            appBackground: "#2E3440",
            surfaceBase: "#3B4252",
            surfaceRaised: "#434C5E",
            surfaceElevated: "#4A5364",
            borderStrong: "#4C566A",
            textPrimary: "#ECEFF4",
            textSecondary: "#D8DEE9",
            textMuted: "#A7B0C0",
            chatNickname: "#88C0D0",
            chatMention: "#B48EAD",
            chatSelfMention: "#A3BE8C",
            accentPrimary: "#81A1C1",
            statusLive: "#A3BE8C",
            statusInfo: "#88C0D0",
            statusWarning: "#EBCB8B",
            statusError: "#BF616A"),
        Create(
            ColorThemeId.TokyoNight,
            "Tokyo Night",
            "ColorThemeTokyoNightDescription",
            appBackground: "#1A1B26",
            surfaceBase: "#24283B",
            surfaceRaised: "#2B3046",
            surfaceElevated: "#32384F",
            borderStrong: "#3B4261",
            textPrimary: "#D5DAFF",
            textSecondary: "#A9B1D6",
            textMuted: "#8C94B8",
            chatNickname: "#7AA2F7",
            chatMention: "#BB9AF7",
            chatSelfMention: "#9ECE6A",
            accentPrimary: "#7DCFFF",
            statusLive: "#9ECE6A",
            statusInfo: "#7DCFFF",
            statusWarning: "#E0AF68",
            statusError: "#F7768E"),
        Create(
            ColorThemeId.Monokai,
            "Monokai",
            "ColorThemeMonokaiDescription",
            appBackground: "#1D1E1A",
            surfaceBase: "#272822",
            surfaceRaised: "#303129",
            surfaceElevated: "#36372F",
            borderStrong: "#49483E",
            textPrimary: "#F8F8F2",
            textSecondary: "#D7D7CC",
            textMuted: "#A0A097",
            chatNickname: "#66D9EF",
            chatMention: "#AE81FF",
            chatSelfMention: "#A6E22E",
            accentPrimary: "#66D9EF",
            statusLive: "#A6E22E",
            statusInfo: "#66D9EF",
            statusWarning: "#E6DB74",
            statusError: "#F92672"),
        ]);

    public static ColorThemeId DefaultTheme => ColorThemeId.GitHubDark;

    public static IReadOnlyList<ColorThemeDefinition> All => Definitions;

    public static ColorThemeDefinition Get(ColorThemeId id) =>
        Definitions.FirstOrDefault(theme => theme.Id == id) ??
        Definitions[0];

    private static ColorThemeDefinition Create(
        ColorThemeId id,
        string displayName,
        string descriptionResourceKey,
        string appBackground,
        string surfaceBase,
        string surfaceRaised,
        string surfaceElevated,
        string borderStrong,
        string textPrimary,
        string textSecondary,
        string textMuted,
        string chatNickname,
        string chatMention,
        string chatSelfMention,
        string accentPrimary,
        string statusLive,
        string statusInfo,
        string statusWarning,
        string statusError)
    {
        var colors = new Dictionary<SemanticColorToken, string>
        {
            [SemanticColorToken.AppBackground] = appBackground,
            [SemanticColorToken.SurfaceBase] = surfaceBase,
            [SemanticColorToken.SurfaceRaised] = surfaceRaised,
            [SemanticColorToken.SurfaceElevated] = surfaceElevated,
            [SemanticColorToken.SurfaceHover] = surfaceElevated,
            [SemanticColorToken.SurfacePressed] = borderStrong,
            [SemanticColorToken.SurfaceSelected] = Blend(surfaceBase, accentPrimary, 0.22),
            [SemanticColorToken.BorderSubtle] = Blend(surfaceRaised, borderStrong, 0.58),
            [SemanticColorToken.BorderStrong] = borderStrong,
            [SemanticColorToken.Divider] = Blend(surfaceRaised, borderStrong, 0.58),
            [SemanticColorToken.TextPrimary] = textPrimary,
            [SemanticColorToken.TextSecondary] = textSecondary,
            [SemanticColorToken.TextMuted] = textMuted,
            [SemanticColorToken.TextDisabled] = WithAlpha(textMuted, 0.62),
            [SemanticColorToken.AccentPrimary] = accentPrimary,
            [SemanticColorToken.AccentHover] = Blend(accentPrimary, textPrimary, 0.16),
            [SemanticColorToken.AccentPressed] = Blend(accentPrimary, appBackground, 0.24),
            [SemanticColorToken.AccentSubtle] = Blend(surfaceBase, accentPrimary, 0.18),
            [SemanticColorToken.ChatMessage] = textPrimary,
            [SemanticColorToken.ChatNickname] = chatNickname,
            [SemanticColorToken.ChatMention] = chatMention,
            [SemanticColorToken.ChatSelfMention] = chatSelfMention,
            [SemanticColorToken.ChatOutline] = "#FF000000",
            [SemanticColorToken.ChatMentionBackground] = WithAlpha(chatMention, 0.66),
            [SemanticColorToken.ChatSelfMentionBackground] = WithAlpha(chatSelfMention, 0.78),
            [SemanticColorToken.StatusLive] = statusLive,
            [SemanticColorToken.StatusInfo] = statusInfo,
            [SemanticColorToken.StatusWarning] = statusWarning,
            [SemanticColorToken.StatusError] = statusError,
            [SemanticColorToken.FocusRing] = accentPrimary,
            [SemanticColorToken.Selection] = Blend(surfaceBase, accentPrimary, 0.28),
            [SemanticColorToken.MediaOverlay] = "#C0000000",
            [SemanticColorToken.ScrollTrack] = WithAlpha(borderStrong, 0.28),
            [SemanticColorToken.ScrollThumb] = Blend(surfaceRaised, textMuted, 0.48),
            [SemanticColorToken.ScrollThumbHover] = Blend(textMuted, accentPrimary, 0.42),
            [SemanticColorToken.ScrollThumbDragging] = accentPrimary,
        };
        return new ColorThemeDefinition(
            id,
            displayName,
            descriptionResourceKey,
            new ReadOnlyDictionary<SemanticColorToken, string>(colors),
            DefaultSwatches);
    }

    private static string Blend(string background, string foreground, double amount)
    {
        var back = ParseRgb(background);
        var front = ParseRgb(foreground);
        return $"#{Blend(back.Red, front.Red, amount):X2}" +
            $"{Blend(back.Green, front.Green, amount):X2}" +
            $"{Blend(back.Blue, front.Blue, amount):X2}";
    }

    private static string WithAlpha(string value, double opacity)
    {
        var rgb = ParseRgb(value);
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        return $"#{alpha:X2}{rgb.Red:X2}{rgb.Green:X2}{rgb.Blue:X2}";
    }

    private static byte Blend(byte background, byte foreground, double amount) =>
        (byte)Math.Round(background + ((foreground - background) * Math.Clamp(amount, 0, 1)));

    private static (byte Red, byte Green, byte Blue) ParseRgb(string value) =>
        (
            Convert.ToByte(value.Substring(1, 2), 16),
            Convert.ToByte(value.Substring(3, 2), 16),
            Convert.ToByte(value.Substring(5, 2), 16)
        );
}

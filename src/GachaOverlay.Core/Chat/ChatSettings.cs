namespace GachaOverlay.Core.Chat;

public enum ChatLayoutMode
{
    Compact,
    Balanced,
}

public enum ChatImageMode
{
    ThumbnailOnly,
    ThumbnailAndEnlarge,
}

public enum ChatImageSizeMode
{
    Compact,
    Large,
}

public enum ChatFontPreset
{
    Kimm = 0,
    KoPubWorldDotum = 1,
    Pretendard = 2,
    Cafe24ProSlim = 3,
    WantedSans = 4,
    ChosunGulim = 5,
}

public enum ChatFontRoleWeight
{
    Light,
    Normal,
    Medium,
    SemiBold,
    Bold,
}

public enum ChatStylePreset
{
    Clean,
    Modern,
    HighReadability,
    GtaLegacy,
}

public enum RoleIconPosition
{
    Left = 0,
    AdjacentRight = 1,
    FarRight = 2,
}

public static partial class ChatSettings
{
    public const double DefaultFontSizePoints = 12;
    public const double DefaultLineHeightMultiplier = 1.42;
    public const double DefaultMessageSpacing = 1;
    public const double MinimumReactionSize = 14;
    public const double DefaultReactionSize = 18;
    public const double MaximumReactionSize = 42;

    public static double NormalizeFontSize(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 8, 32) : DefaultFontSizePoints;

    public static int NormalizeMaxLines(int value) => Math.Clamp(value, 1, 3);

    public static double NormalizeOutlineThickness(double value) =>
        double.IsFinite(value)
            ? Math.Round(Math.Clamp(value, 0, 10) * 4, MidpointRounding.AwayFromZero) / 4
            : 1.5;

    public static double NormalizeSurfaceOpacity(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;

    public static double NormalizeQueueDetailMaxHeight(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 120, 640) : 280;

    public static double NormalizeLineHeightMultiplier(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 1.0, 1.65) : DefaultLineHeightMultiplier;

    public static double NormalizeMessageSpacing(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, -2, 48) : DefaultMessageSpacing;

    public static double NormalizeReactionSize(double value) =>
        double.IsFinite(value)
            ? Math.Round(
                Math.Clamp(value, MinimumReactionSize, MaximumReactionSize),
                MidpointRounding.AwayFromZero)
            : DefaultReactionSize;

    public static string ResolveFontFamily(ChatFontPreset preset) =>
        ResolveTypography(preset).DisplayName;

    public static ChatTypographyDefinition ResolveTypography(ChatFontPreset preset) => preset switch
    {
        ChatFontPreset.Pretendard => new ChatTypographyDefinition(
            "Pretendard",
            "Pretendard Variable",
            ChatFontRoleWeight.SemiBold,
            "Pretendard Variable",
            ChatFontRoleWeight.Normal,
            IsBundled: true),
        ChatFontPreset.WantedSans => new ChatTypographyDefinition(
            "Wanted Sans",
            "Wanted Sans Variable",
            ChatFontRoleWeight.Bold,
            "Wanted Sans Variable",
            ChatFontRoleWeight.Medium,
            IsBundled: true),
        ChatFontPreset.Cafe24ProSlim => new ChatTypographyDefinition(
            "Cafe24 PRO Slim",
            "Cafe24 PRO Slim",
            ChatFontRoleWeight.Bold,
            "Cafe24 PRO Slim",
            ChatFontRoleWeight.Normal,
            IsBundled: true),
        ChatFontPreset.ChosunGulim => new ChatTypographyDefinition(
            "조선굴림체",
            "조선굴림체",
            ChatFontRoleWeight.Normal,
            "조선굴림체",
            ChatFontRoleWeight.Normal,
            IsBundled: true),
        _ => new ChatTypographyDefinition(
            "한국기계연구원",
            "KIMM_Bold",
            ChatFontRoleWeight.Bold,
            "KIMM_Light",
            ChatFontRoleWeight.Light,
            IsBundled: true),
    };

}

public sealed record ChatReactionMetrics(
    double ImageExtent,
    double UnicodeFontSize,
    double CountFontSize)
{
    public static ChatReactionMetrics FromMasterSize(double value)
    {
        var size = ChatSettings.NormalizeReactionSize(value);
        return new ChatReactionMetrics(
            size,
            Math.Round(size * (16d / 18d), 2),
            Math.Min(21, Math.Round(12 + ((size - 18) * 0.4), 2)));
    }
}

public sealed record ChatTypographyDefinition(
    string DisplayName,
    string NicknameFamilyName,
    ChatFontRoleWeight NicknameWeight,
    string MessageFamilyName,
    ChatFontRoleWeight MessageWeight,
    bool IsBundled);

public static class ChatVisualMetrics
{
    public static double CalculateLineHeight(
        double fontSizeDip,
        double multiplier) =>
        Math.Ceiling(
            Math.Max(1, fontSizeDip) * ChatSettings.NormalizeLineHeightMultiplier(multiplier));

    public static double CalculateEmojiExtent(double fontSizeDip, double lineHeight) =>
        Math.Clamp(
            Math.Max(fontSizeDip * 1.2, lineHeight * 1.22),
            18,
            48);

    public static double CalculateStickerExtent(
        ChatResponsiveLevel level,
        bool largeMedia = false) =>
        level switch
        {
            ChatResponsiveLevel.Full => largeMedia ? 180 : 96,
            ChatResponsiveLevel.Reduced => largeMedia ? 132 : 72,
            _ => 0,
        };
}

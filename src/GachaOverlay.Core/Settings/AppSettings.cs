using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Hud.Geometry;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Themes;
using GachaOverlay.Core.Discord.Connection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GachaOverlay.Core.Settings;

public sealed record AppSettings
{
    private static readonly Dictionary<string, double> EmptyScrollPositions =
        new(StringComparer.OrdinalIgnoreCase);
    public const int CurrentSchemaVersion = 11;

    public const int CurrentOnboardingVersion = 1;

    public const int CurrentHotkeySettingsVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Language { get; init; } = SupportedLocales.English;

    public SettingsCategory LastSettingsCategory { get; init; } = SettingsCategory.General;

    public ColorThemeId ColorTheme { get; init; } = ColorThemeCatalog.DefaultTheme;

    public Dictionary<string, double> SettingsCategoryScrollPositions { get; init; } =
        EmptyScrollPositions;

    public string? DiscordClientId { get; init; }

    public string DiscordRedirectUri { get; init; } = "https://127.0.0.1";

    public string? DiscordGuildId { get; init; } = ProductionServerProfile.GuildId;

    public string? DiscordMainChannelId { get; init; }

    public string? DiscordSalesChannelId { get; init; } = ProductionServerProfile.SalesChannelId;

    public int OnboardingVersion { get; init; }

    public bool WindowsAutoStart { get; init; }

    public bool DiscordAutoLaunch { get; init; }

    public double HudSurfaceOpacity { get; init; } = HudSettingsDefaults.SurfaceOpacity;

    public double HudChromeOpacity { get; init; } = 1;

    public double ChatSurfaceOpacity { get; init; } = 1;

    public double SalesSurfaceOpacity { get; init; } = 1;

    public double QueueDetailSurfaceOpacity { get; init; } = 1;

    public bool MinimalHudMode { get; init; }

    public bool HudModifierDragEnabled { get; init; }

    public string HudModifierDragModifier { get; init; } = HudSettingsDefaults.ModifierDragModifier;

    public HotkeySetting HudLockHotkey { get; init; } = HotkeySetting.DefaultLockToggle;

    public HotkeySetting HudVisibilityHotkey { get; init; } = HotkeySetting.DefaultVisibilityToggle;

    public int HotkeySettingsVersion { get; init; } = CurrentHotkeySettingsVersion;

    public bool HotkeysCustomized { get; init; }

    public HudVisibilityMode HudVisibilityMode { get; init; } = HudVisibilityMode.Always;

    public HudWindowGeometry? HudWindowGeometry { get; init; }

    public ChatLayoutMode ChatLayoutMode { get; init; } = ChatLayoutMode.Balanced;

    public bool ChatShowTime { get; init; } = true;

    public ChatFontPreset ChatFontPreset { get; init; } = ChatFontPreset.Kimm;

    public double ChatFontSizePoints { get; init; } = ChatSettings.DefaultFontSizePoints;

    public bool ChatNicknameOutlineEnabled { get; init; } = true;

    public bool ChatMessageOutlineEnabled { get; init; } = true;

    public double ChatOutlineThickness { get; init; } = 1.5;

    public double ChatNicknameOutlineThickness { get; init; } = 1.5;

    public double ChatMessageOutlineThickness { get; init; } = 1.5;

    public double ChatLineHeightMultiplier { get; init; } =
        ChatSettings.DefaultLineHeightMultiplier;

    public double ChatMessageSpacing { get; init; } = ChatSettings.DefaultMessageSpacing;

    public int ChatMaxLines { get; init; } = 2;

    public bool ChatShowImages { get; init; } = true;

    public ChatImageMode ChatImageMode { get; init; } = ChatImageMode.ThumbnailOnly;

    public ChatImageSizeMode ChatImageSizeMode { get; init; } = ChatImageSizeMode.Compact;

    public bool ChatCustomEmojiEnabled { get; init; } = true;

    public bool ChatStickerEnabled { get; init; } = true;

    public bool HidePreviewSourceUrl { get; init; } = true;

    public bool SalesTrackingEnabled { get; init; } = true;

    public bool SalesShowCurrentSeller { get; init; } = true;

    public bool SalesShowWaitingCount { get; init; } = true;

    public bool SalesShowProduct { get; init; }

    public bool SalesShowNextWaitingUser { get; init; }

    public double SalesQueueDetailMaxHeight { get; init; } = 280;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public static AppSettings CreateDefault() => new();
}

public enum SettingsCategory
{
    General,
    Discord,
    Server,
    Hud,
    Chat,
    Media,
    Sales,
    Hotkeys,
    Diagnostics,
    Developer,
}

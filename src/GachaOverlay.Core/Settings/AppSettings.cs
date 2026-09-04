using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Hud.Geometry;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Themes;
using GachaOverlay.Core.Timers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GachaOverlay.Core.Settings;

public sealed record AppSettings
{
    private static readonly Dictionary<string, double> EmptyScrollPositions =
        new(StringComparer.OrdinalIgnoreCase);
    public const int CurrentSchemaVersion = 19;

    public const int CurrentOnboardingVersion = 2;

    public const int CurrentHotkeySettingsVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Language { get; init; } = SupportedLocales.English;

    public SettingsCategory LastSettingsCategory { get; init; } = SettingsCategory.General;

    public ColorThemeId ColorTheme { get; init; } = ColorThemeCatalog.DefaultTheme;

    public Dictionary<string, double> SettingsCategoryScrollPositions { get; init; } =
        EmptyScrollPositions;

    public const string ProductionRemoteBackendBaseUrl = "https://overlay.revo32.cloud";
    public string RemoteBackendBaseUrl { get; init; } = ProductionRemoteBackendBaseUrl;

    public string? RemoteSelectedChannelId { get; init; }

    public int OnboardingVersion { get; init; }

    public bool WindowsAutoStart { get; init; }

    public double HudSurfaceOpacity { get; init; } = HudSettingsDefaults.SurfaceOpacity;

    public double HudChromeOpacity { get; init; } = 1;

    public double ChatSurfaceOpacity { get; init; } = 1;

    public double SalesSurfaceOpacity { get; init; } = 1;

    public double QueueDetailSurfaceOpacity { get; init; } = 1;

    public bool MinimalHudMode { get; init; } = true;

    public bool ShowGtaSession { get; init; } = true;

    public SessionHostSelection SelectedSessionHost { get; init; } =
        SessionHostSelection.Host1;

    public bool HudModifierDragEnabled { get; init; }

    public string HudModifierDragModifier { get; init; } = HudSettingsDefaults.ModifierDragModifier;

    public HotkeySetting HudLockHotkey { get; init; } = HotkeySetting.DefaultLockToggle;

    public HotkeySetting HudVisibilityHotkey { get; init; } = HotkeySetting.DefaultVisibilityToggle;

    public HotkeySetting PreviousMainChannelHotkey { get; init; } = new() { Key = "" };
    public HotkeySetting NextMainChannelHotkey { get; init; } = new() { Key = "" };

    public HotkeySetting GeneralTimerHotkey { get; init; } = new() { Key = "" };

    public HotkeySetting BunkerTimerHotkey { get; init; } = new() { Key = "" };

    public HotkeySetting LsdTimerHotkey { get; init; } = new() { Key = "" };

    public int GeneralTimerMinutes { get; init; } = GtaoTimerPresets.General[0];

    public int BunkerTimerMinutes { get; init; } = GtaoTimerPresets.Bunker[0];

    public int LsdTimerMinutes { get; init; } = GtaoTimerPresets.Lsd[0];

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

    public bool SalesTurnSoundEnabled { get; init; } = true;

    public double SalesTurnSoundVolume { get; init; } = 50;

    public bool NotifySalesNext { get; init; } = true;

    public bool NotifySalesCurrent { get; init; } = true;

    public double SalesQueueDetailMaxHeight { get; init; } = 280;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public static AppSettings CreateDefault() => new();
}

[JsonConverter(typeof(SessionHostSelectionJsonConverter))]
public enum SessionHostSelection
{
    Host1 = 1,
    Host2 = 2,
}

public sealed class SessionHostSelectionJsonConverter : JsonConverter<SessionHostSelection>
{
    public override SessionHostSelection Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            string.Equals(reader.GetString(), "Host2", StringComparison.OrdinalIgnoreCase))
        {
            return SessionHostSelection.Host2;
        }

        if (reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt32(out var numeric) &&
            numeric == (int)SessionHostSelection.Host2)
        {
            return SessionHostSelection.Host2;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }

        return SessionHostSelection.Host1;
    }

    public override void Write(
        Utf8JsonWriter writer,
        SessionHostSelection value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value == SessionHostSelection.Host2 ? "Host2" : "Host1");
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

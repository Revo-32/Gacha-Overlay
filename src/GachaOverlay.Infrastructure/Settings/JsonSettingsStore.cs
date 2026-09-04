using System.Text;
using System.Text.Json;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Timers;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Themes;

namespace GachaOverlay.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _settingsFilePath;
    private readonly IAppLogger _logger;
    private AppSettings _current = AppSettings.CreateDefault();

    public JsonSettingsStore(string settingsFilePath, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);
        _settingsFilePath = settingsFilePath;
        _logger = logger ?? NullAppLogger.Instance;
    }

    public AppSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public AppSettings Load()
    {
        lock (_sync)
        {
            var backupPath = GetBackupPath();
            if (TryReadSettings(_settingsFilePath, out var settings, out var primaryError))
            {
                _current = Normalize(settings!);
                _logger.Information(
                    "SETTINGS",
                    $"Loaded schema {_current.SchemaVersion} with language '{_current.Language}'.");
                if (RequiresMigrationPersistence(settings!, _current))
                {
                    if (SaveCore(_current))
                    {
                        _logger.Information("SETTINGS", "Settings migration was persisted.");
                    }
                }

                return _current;
            }

            if (TryReadSettings(backupPath, out var backup, out var backupError))
            {
                _current = Normalize(backup!);
                _logger.Warning(
                    "SETTINGS",
                    $"Primary settings were unavailable ({FormatReadFailure(primaryError)}); " +
                    $"backup schema {_current.SchemaVersion} was recovered.");
                _ = SaveCore(_current, createBackup: false);

                return _current;
            }

            _current = AppSettings.CreateDefault();
            if (primaryError is null && backupError is null)
            {
                _logger.Information("SETTINGS", "Settings file not found; defaults loaded.");
            }
            else
            {
                _logger.Warning(
                    "SETTINGS",
                    $"Settings could not be loaded (primary={FormatReadFailure(primaryError)}, " +
                    $"backup={FormatReadFailure(backupError)}); safe defaults were restored.");
            }

            return _current;
        }
    }

    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            return SaveCore(settings);
        }
    }

    public bool Update(Func<AppSettings, AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_sync)
        {
            return SaveCore(update(_current));
        }
    }

    private bool SaveCore(AppSettings settings, bool createBackup = true)
    {
        var normalized = Normalize(settings);
        var temporaryPath = $"{_settingsFilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("The settings directory is invalid.");
            }

            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(normalized, SerializerOptions);
            var bytes = new UTF8Encoding(false).GetBytes(json);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            ValidateSerializedSettings(temporaryPath);
            if (File.Exists(_settingsFilePath))
            {
                string? backupPath = null;
                if (createBackup)
                {
                    backupPath = GetBackupPath();
                    File.Delete(backupPath);
                }

                File.Replace(
                    temporaryPath,
                    _settingsFilePath,
                    backupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _settingsFilePath);
            }

            _current = normalized;
            _logger.Information(
                "SETTINGS",
                $"Saved schema {_current.SchemaVersion} with language '{_current.Language}'.");
            return true;
        }
        catch (Exception exception)
        {
            TryDeleteTemporaryFile(temporaryPath);
            _logger.Error("SETTINGS", "Settings save failed; the previous file was preserved.", exception);
            return false;
        }
    }

    private static bool TryReadSettings(
        string path,
        out AppSettings? settings,
        out Exception? error)
    {
        settings = null;
        error = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            settings = JsonSerializer.Deserialize<AppSettings>(stream, SerializerOptions);
            if (settings is null)
            {
                throw new JsonException("The settings document contained no settings object.");
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException)
        {
            error = exception;
            return false;
        }
    }

    private static void ValidateSerializedSettings(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        if (JsonSerializer.Deserialize<AppSettings>(stream, SerializerOptions) is null)
        {
            throw new JsonException("The serialized settings document failed validation.");
        }
    }

    private string GetBackupPath() => _settingsFilePath + ".bak";

    private static string FormatReadFailure(Exception? exception) =>
        exception?.GetType().Name ?? "Missing";

    private AppSettings Normalize(AppSettings settings)
    {
        var language = SupportedLocales.Korean;
        if (!string.Equals(language, settings.Language, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Information(
                "SETTINGS",
                $"Stored locale '{settings.Language}' was migrated to the Korean-only runtime.");
        }

        var sourceSchemaVersion = settings.SchemaVersion;
        var schemaVersion = sourceSchemaVersion < AppSettings.CurrentSchemaVersion
            ? AppSettings.CurrentSchemaVersion
            : settings.SchemaVersion;

        if (schemaVersion > AppSettings.CurrentSchemaVersion)
        {
            _logger.Warning(
                "SETTINGS",
                $"Settings schema {schemaVersion} is newer than supported schema {AppSettings.CurrentSchemaVersion}; known fields were loaded.");
        }

        var visibilityMode = Enum.IsDefined(settings.HudVisibilityMode)
            ? settings.HudVisibilityMode
            : HudVisibilityMode.Always;
        if (visibilityMode != settings.HudVisibilityMode)
        {
            _logger.Warning(
                "SETTINGS",
                $"Unsupported HUD visibility mode '{settings.HudVisibilityMode}' was replaced with Always.");
        }

        var lockHotkey = NormalizeHotkey(
            settings.HudLockHotkey,
            HotkeySetting.DefaultLockToggle,
            "LockToggle");
        var visibilityHotkey = NormalizeHotkey(
            settings.HudVisibilityHotkey,
            HotkeySetting.DefaultVisibilityToggle,
            "VisibilityToggle");
        if (HotkeyGesture.TryParse(lockHotkey, out var lockGesture) &&
            HotkeyGesture.TryParse(visibilityHotkey, out var visibilityGesture) &&
            lockGesture == visibilityGesture)
        {
            visibilityHotkey = HotkeySetting.DefaultVisibilityToggle;
            _logger.Warning(
                "SETTINGS",
                "Duplicate HUD hotkeys were replaced with the safe visibility default.");
        }

        var hotkeysCustomized = settings.HotkeysCustomized;
        if (!hotkeysCustomized &&
            (sourceSchemaVersion < AppSettings.CurrentSchemaVersion ||
             settings.HotkeySettingsVersion < AppSettings.CurrentHotkeySettingsVersion) &&
            IsKnownLegacyDefaultPair(lockHotkey, visibilityHotkey))
        {
            lockHotkey = HotkeySetting.DefaultLockToggle;
            visibilityHotkey = HotkeySetting.DefaultVisibilityToggle;
            _logger.Information(
                "SETTINGS",
                "Known legacy default HUD hotkeys were migrated to F9/F10 without modifiers.");
        }

        // Optional navigation bindings must never prevent F9/F10 from registering.
        var usedGestures = new HashSet<HotkeyGesture>();
        HotkeyGesture.TryParse(lockHotkey, out var normalizedLock);
        HotkeyGesture.TryParse(visibilityHotkey, out var normalizedVisibility);
        usedGestures.Add(normalizedLock);
        usedGestures.Add(normalizedVisibility);
        HotkeySetting Optional(HotkeySetting? candidate)
        {
            if (candidate is null || !HotkeyGesture.TryParse(candidate, out var value) ||
                !usedGestures.Add(value)) return new HotkeySetting { Key = "" };
            return value.ToSetting();
        }
        var previousChannelHotkey = Optional(settings.PreviousMainChannelHotkey);
        var nextChannelHotkey = Optional(settings.NextMainChannelHotkey);
        var generalTimerHotkey = Optional(settings.GeneralTimerHotkey);
        var bunkerTimerHotkey = Optional(settings.BunkerTimerHotkey);
        var lsdTimerHotkey = Optional(settings.LsdTimerHotkey);

        var geometry = settings.HudWindowGeometry;
        if (geometry is not null && !geometry.Rectangle.IsFiniteAndPositive)
        {
            geometry = null;
            _logger.Warning("SETTINGS", "Invalid HUD geometry was discarded.");
        }

        var fontPreset = settings.ChatFontPreset == ChatFontPreset.KoPubWorldDotum
            ? ChatFontPreset.WantedSans
            : Enum.IsDefined(settings.ChatFontPreset)
                ? settings.ChatFontPreset
                : ChatFontPreset.Kimm;
        if (settings.ChatFontPreset == ChatFontPreset.KoPubWorldDotum)
        {
            _logger.Information(
                "SETTINGS",
                "Legacy KoPub World typography was migrated to bundled Wanted Sans.");
        }

        var scrollPositions = (settings.SettingsCategoryScrollPositions ??
                new Dictionary<string, double>())
            .Where(pair =>
                Enum.TryParse<SettingsCategory>(pair.Key, ignoreCase: true, out var category) &&
                category != SettingsCategory.Server &&
                double.IsFinite(pair.Value))
            .ToDictionary(
                pair => pair.Key,
                pair => Math.Max(0, pair.Value),
                StringComparer.OrdinalIgnoreCase);

        var colorTheme = sourceSchemaVersion < 10
            ? ColorThemeCatalog.DefaultTheme
            : Enum.IsDefined(settings.ColorTheme)
                ? settings.ColorTheme
                : ColorThemeCatalog.DefaultTheme;
        if (sourceSchemaVersion < 10)
        {
            _logger.Information(
                "SETTINGS",
                "Legacy color settings were migrated to the GitHub Dark theme.");
        }
        else if (!Enum.IsDefined(settings.ColorTheme))
        {
            _logger.Warning(
                "SETTINGS",
                $"Unsupported color theme '{settings.ColorTheme}' was replaced with GitHub Dark.");
        }

        var nicknameOutline = sourceSchemaVersion < 9
            ? settings.ChatOutlineThickness
            : settings.ChatNicknameOutlineThickness;
        var messageOutline = sourceSchemaVersion < 9
            ? settings.ChatOutlineThickness
            : settings.ChatMessageOutlineThickness;

        return settings with
        {
            SchemaVersion = schemaVersion,
            MinimalHudMode = sourceSchemaVersion < 18 || settings.MinimalHudMode,
            Language = language,
            ColorTheme = colorTheme,
            LastSettingsCategory = settings.LastSettingsCategory == SettingsCategory.Server
                ? SettingsCategory.Discord
                : Enum.IsDefined(settings.LastSettingsCategory)
                    ? settings.LastSettingsCategory
                    : SettingsCategory.General,
            SettingsCategoryScrollPositions = scrollPositions,
            RemoteBackendBaseUrl = NormalizeRemoteBackendBaseUrl(settings.RemoteBackendBaseUrl),
            RemoteSelectedChannelId = NormalizeNumericText(settings.RemoteSelectedChannelId),
            OnboardingVersion = Math.Clamp(
                settings.OnboardingVersion,
                0,
                AppSettings.CurrentOnboardingVersion),
            HudSurfaceOpacity = HudSettingsDefaults.NormalizeSurfaceOpacity(
                settings.HudSurfaceOpacity),
            HudChromeOpacity = ChatSettings.NormalizeSurfaceOpacity(settings.HudChromeOpacity),
            ChatSurfaceOpacity = ChatSettings.NormalizeSurfaceOpacity(settings.ChatSurfaceOpacity),
            SalesSurfaceOpacity = ChatSettings.NormalizeSurfaceOpacity(settings.SalesSurfaceOpacity),
            QueueDetailSurfaceOpacity = ChatSettings.NormalizeSurfaceOpacity(
                settings.QueueDetailSurfaceOpacity),
            HudModifierDragModifier = HudSettingsDefaults.NormalizeModifierDragModifier(
                settings.HudModifierDragModifier),
            HudLockHotkey = lockHotkey,
            HudVisibilityHotkey = visibilityHotkey,
            PreviousMainChannelHotkey = previousChannelHotkey,
            NextMainChannelHotkey = nextChannelHotkey,
            GeneralTimerHotkey = generalTimerHotkey,
            BunkerTimerHotkey = bunkerTimerHotkey,
            LsdTimerHotkey = lsdTimerHotkey,
            GeneralTimerMinutes = GtaoTimerPresets.Normalize(
                GtaoTimerSlot.General, settings.GeneralTimerMinutes),
            BunkerTimerMinutes = GtaoTimerPresets.Normalize(
                GtaoTimerSlot.Bunker, settings.BunkerTimerMinutes),
            LsdTimerMinutes = GtaoTimerPresets.Normalize(
                GtaoTimerSlot.Lsd, settings.LsdTimerMinutes),
            HotkeySettingsVersion = AppSettings.CurrentHotkeySettingsVersion,
            HotkeysCustomized = hotkeysCustomized,
            HudVisibilityMode = visibilityMode,
            HudWindowGeometry = geometry,
            SelectedSessionHost = Enum.IsDefined(settings.SelectedSessionHost)
                ? settings.SelectedSessionHost
                : SessionHostSelection.Host1,
            ChatLayoutMode = Enum.IsDefined(settings.ChatLayoutMode)
                ? settings.ChatLayoutMode
                : ChatLayoutMode.Balanced,
            ChatShowTime = settings.ChatShowTime,
            ChatFontPreset = fontPreset,
            ChatFontSizePoints = ChatSettings.NormalizeFontSize(settings.ChatFontSizePoints),
            ChatOutlineThickness = ChatSettings.NormalizeOutlineThickness(
                settings.ChatOutlineThickness),
            ChatNicknameOutlineThickness = ChatSettings.NormalizeOutlineThickness(
                nicknameOutline),
            ChatMessageOutlineThickness = ChatSettings.NormalizeOutlineThickness(
                messageOutline),
            ChatLineHeightMultiplier = ChatSettings.NormalizeLineHeightMultiplier(
                settings.ChatLineHeightMultiplier),
            ChatMessageSpacing = ChatSettings.NormalizeMessageSpacing(
                settings.ChatMessageSpacing),
            ChatRoleIconPosition = Enum.IsDefined(settings.ChatRoleIconPosition)
                ? settings.ChatRoleIconPosition
                : RoleIconPosition.Left,
            ChatReactionSize = ChatSettings.NormalizeReactionSize(
                sourceSchemaVersion < 20
                    ? ChatSettings.DefaultReactionSize
                    : settings.ChatReactionSize),
            ChatMaxLines = ChatSettings.NormalizeMaxLines(settings.ChatMaxLines),
            ChatImageMode = Enum.IsDefined(settings.ChatImageMode)
                ? settings.ChatImageMode
                : ChatImageMode.ThumbnailOnly,
            ChatImageSizeMode = Enum.IsDefined(settings.ChatImageSizeMode)
                ? settings.ChatImageSizeMode
                : ChatImageSizeMode.Compact,
            SalesQueueDetailMaxHeight = ChatSettings.NormalizeQueueDetailMaxHeight(
                settings.SalesQueueDetailMaxHeight),
            SalesTurnSoundVolume = double.IsFinite(settings.SalesTurnSoundVolume)
                ? Math.Clamp(settings.SalesTurnSoundVolume, 0, 100)
                : 50,
            ExtensionData = RemoveDeprecatedFields(settings.ExtensionData),
        };
    }

    private string NormalizeRemoteBackendBaseUrl(string? value)
    {
        const string fallback = AppSettings.ProductionRemoteBackendBaseUrl;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _logger.Warning(
                    "SETTINGS",
                    "Invalid remote backend endpoint was replaced with the safe loopback default.");
            }

            return fallback;
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static string? NormalizeNumericText(string? value)
    {
        var normalized = NormalizeText(value);
        return normalized is not null && normalized.All(char.IsAsciiDigit)
            ? normalized
            : null;
    }

    private HotkeySetting NormalizeHotkey(
        HotkeySetting? setting,
        HotkeySetting fallback,
        string name)
    {
        if (HotkeyGesture.TryParse(setting, out var gesture))
        {
            return gesture.ToSetting();
        }

        _logger.Warning("SETTINGS", $"Invalid HUD hotkey '{name}' was replaced with a safe default.");
        return fallback;
    }

    private static bool RequiresMigrationPersistence(
        AppSettings source,
        AppSettings normalized) =>
        source.SchemaVersion < AppSettings.CurrentSchemaVersion ||
        !string.Equals(source.Language, normalized.Language, StringComparison.OrdinalIgnoreCase) ||
        source.HotkeySettingsVersion < AppSettings.CurrentHotkeySettingsVersion ||
        source.ChatFontPreset == ChatFontPreset.KoPubWorldDotum ||
        !Enum.IsDefined(source.ColorTheme) ||
        ContainsDeprecatedFields(source.ExtensionData) ||
        source.LastSettingsCategory != normalized.LastSettingsCategory ||
        source.OnboardingVersion != normalized.OnboardingVersion ||
        source.HudLockHotkey != normalized.HudLockHotkey ||
        source.HudVisibilityHotkey != normalized.HudVisibilityHotkey ||
        source.GeneralTimerHotkey != normalized.GeneralTimerHotkey ||
        source.BunkerTimerHotkey != normalized.BunkerTimerHotkey ||
        source.LsdTimerHotkey != normalized.LsdTimerHotkey ||
        source.GeneralTimerMinutes != normalized.GeneralTimerMinutes ||
        source.BunkerTimerMinutes != normalized.BunkerTimerMinutes ||
        source.LsdTimerMinutes != normalized.LsdTimerMinutes ||
        source.ChatRoleIconPosition != normalized.ChatRoleIconPosition ||
        Math.Abs(source.ChatReactionSize - normalized.ChatReactionSize) > 0.001 ||
        source.SelectedSessionHost != normalized.SelectedSessionHost;

    private static bool IsKnownLegacyDefaultPair(
        HotkeySetting lockSetting,
        HotkeySetting visibilitySetting)
    {
        if (!HotkeyGesture.TryParse(lockSetting, out var lockGesture) ||
            !HotkeyGesture.TryParse(visibilitySetting, out var visibilityGesture))
        {
            return false;
        }

        return IsPair("Control+F9", "Control+F10") ||
               IsPair("Control+Shift+Alt+L", "Control+Shift+Alt+H") ||
               IsPair("Control+Shift+L", "Control+Shift+H");

        bool IsPair(string legacyLock, string legacyVisibility) =>
            HotkeyGesture.TryParseDisplayText(legacyLock, out var expectedLock) &&
            HotkeyGesture.TryParseDisplayText(legacyVisibility, out var expectedVisibility) &&
            lockGesture == expectedLock &&
            visibilityGesture == expectedVisibility;
    }

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, JsonElement>? RemoveDeprecatedFields(
        Dictionary<string, JsonElement>? extensionData)
    {
        if (extensionData is null || extensionData.Count == 0)
        {
            return null;
        }

        var filtered = extensionData
            .Where(pair => !IsDeprecatedField(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return filtered.Count == 0 ? null : filtered;
    }

    private static bool ContainsDeprecatedFields(
        Dictionary<string, JsonElement>? extensionData) =>
        extensionData?.Keys.Any(IsDeprecatedField) == true;

    private static bool IsDeprecatedField(string name) =>
        name.Equals("quickDiscordFocusEnabled", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("salesAcquisitionPreference", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("discordClientId", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("discordClientSecret", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("discordRedirectUri", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("discordOAuthScopes", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("discordOAuthToken", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("discordAccessToken", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("discordRefreshToken", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("discordGuildId", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("discordMainChannelId", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("discordSalesChannelId", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatSource", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("mainChatSource", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("discordAutoLaunch", StringComparison.OrdinalIgnoreCase) ||
        IsLegacyColorField(name) ||
        IsLegacyChatShadowField(name);

    private static bool IsLegacyColorField(string name) => name.Equals(
            "chatNicknameColor",
            StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatMessageColor", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatMentionColor", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatSelfMentionColor", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatOutlineColor", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatShadowColor", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyChatShadowField(string name) => name.Equals(
            "chatNicknameShadowEnabled",
            StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatMessageShadowEnabled", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatShadowEnabled", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatShadowOpacity", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatShadowStrength", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatShadowDepth", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("chatShadowOffset", StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch
        {
            // A stale temporary file is harmless and may be cleaned on a later save.
        }
    }
}

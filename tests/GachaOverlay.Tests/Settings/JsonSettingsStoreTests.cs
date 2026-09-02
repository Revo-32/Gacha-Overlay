using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Hud.Geometry;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Tests.TestSupport;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Themes;

namespace GachaOverlay.Tests.Settings;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsEnglishDefaults()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));

        var settings = store.Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(SupportedLocales.English, settings.Language);
    }

    [Theory]
    [InlineData(SupportedLocales.Korean)]
    [InlineData(SupportedLocales.Japanese)]
    public void SaveAndLoad_RoundTripsSupportedLanguage(string language)
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = directory.File("settings.json");
        var writer = new JsonSettingsStore(settingsPath);

        var saved = writer.Save(AppSettings.CreateDefault() with { Language = language });
        var loaded = new JsonSettingsStore(settingsPath).Load();

        Assert.True(saved);
        Assert.Equal(language, loaded.Language);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void Load_WhenJsonIsMalformed_FallsBackToSafeDefaults()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = directory.File("settings.json");
        System.IO.File.WriteAllText(settingsPath, "{ this is not valid json");
        var store = new JsonSettingsStore(settingsPath);

        var settings = store.Load();

        Assert.Equal(AppSettings.CreateDefault(), settings);
    }

    [Fact]
    public void AtomicSave_CreatesOnePreviousVersionBackupAndFlushesValidJson()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = directory.File("settings.json");
        var store = new JsonSettingsStore(settingsPath);
        Assert.True(store.Save(AppSettings.CreateDefault() with
        {
            Language = SupportedLocales.English,
        }));

        Assert.True(store.Save(store.Current with
        {
            Language = SupportedLocales.Korean,
        }));

        var backup = new JsonSettingsStore(settingsPath + ".bak").Load();
        var primary = new JsonSettingsStore(settingsPath).Load();
        Assert.Equal(SupportedLocales.English, backup.Language);
        Assert.Equal(SupportedLocales.Korean, primary.Language);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void CorruptPrimary_RecoversValidBackupWithoutReplacingItWithCorruptData()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = directory.File("settings.json");
        var store = new JsonSettingsStore(settingsPath);
        Assert.True(store.Save(AppSettings.CreateDefault() with
        {
            Language = SupportedLocales.Korean,
        }));
        Assert.True(store.Save(store.Current with
        {
            Language = SupportedLocales.Japanese,
        }));
        File.WriteAllText(settingsPath, "{ invalid primary");

        var recovered = new JsonSettingsStore(settingsPath).Load();

        Assert.Equal(SupportedLocales.Korean, recovered.Language);
        Assert.Equal(
            SupportedLocales.Korean,
            new JsonSettingsStore(settingsPath + ".bak").Load().Language);
        Assert.Equal(
            SupportedLocales.Korean,
            new JsonSettingsStore(settingsPath).Load().Language);
    }

    [Fact]
    public void ReplacementFailure_PreservesPreviousValidPrimary()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = directory.File("settings.json");
        var store = new JsonSettingsStore(settingsPath);
        Assert.True(store.Save(AppSettings.CreateDefault()));
        var original = File.ReadAllText(settingsPath);
        Directory.CreateDirectory(settingsPath + ".bak");

        var saved = store.Save(store.Current with
        {
            Language = SupportedLocales.Korean,
        });

        Assert.False(saved);
        Assert.Equal(original, File.ReadAllText(settingsPath));
        Assert.Equal(SupportedLocales.English, store.Current.Language);
    }

    [Fact]
    public void UnknownFields_ArePreservedAcrossNormalizeAndAtomicSave()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = directory.File("settings.json");
        File.WriteAllText(settingsPath, """
            {
              "schemaVersion": 10,
              "language": "ko",
              "futureReleaseField": { "enabled": true, "revision": 12 }
            }
            """);
        var store = new JsonSettingsStore(settingsPath);
        store.Load();

        Assert.True(store.Update(current => current with
        {
            Language = SupportedLocales.Japanese,
        }));

        var json = File.ReadAllText(settingsPath);
        Assert.Contains("\"futureReleaseField\"", json, StringComparison.Ordinal);
        Assert.Contains("\"revision\": 12", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_IsIdempotentAfterFirstPersistence()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = directory.File("settings.json");
        File.WriteAllText(settingsPath, """
            { "schemaVersion": 8, "chatOutlineThickness": 1.25 }
            """);
        _ = new JsonSettingsStore(settingsPath).Load();
        var afterFirstLoad = File.ReadAllText(settingsPath);

        _ = new JsonSettingsStore(settingsPath).Load();

        Assert.Equal(afterFirstLoad, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void Save_WhenDirectoryCannotBeCreated_ReturnsFalseWithoutChangingCurrent()
    {
        using var directory = new TemporaryDirectory();
        var blockingFile = directory.File("not-a-directory");
        System.IO.File.WriteAllText(blockingFile, "block");
        var store = new JsonSettingsStore(System.IO.Path.Combine(blockingFile, "settings.json"));

        var saved = store.Save(AppSettings.CreateDefault() with
        {
            Language = SupportedLocales.Korean,
        });

        Assert.False(saved);
        Assert.Equal(SupportedLocales.English, store.Current.Language);
    }

    [Fact]
    public void AtomicUpdates_PreserveLanguageAndRemoteConfiguration()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));
        store.Load();

        store.Update(current => current with { Language = SupportedLocales.Korean });
        store.Update(current => current with
        {
            RemoteBackendBaseUrl = "https://overlay.example/path",
            RemoteSelectedChannelId = "1234",
        });

        var loaded = new JsonSettingsStore(directory.File("settings.json")).Load();
        Assert.Equal(SupportedLocales.Korean, loaded.Language);
        Assert.Equal("https://overlay.example", loaded.RemoteBackendBaseUrl);
        Assert.Equal("1234", loaded.RemoteSelectedChannelId);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void HudSettings_SaveAndLoad_RoundTrip()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var expectedGeometry = new HudWindowGeometry(-1200, 80, 640, 360, "LEFT", 144);
        var store = new JsonSettingsStore(path);

        Assert.True(store.Save(AppSettings.CreateDefault() with
        {
            HudSurfaceOpacity = 0.45,
            HudModifierDragEnabled = true,
            HudVisibilityMode = HudVisibilityMode.GameForegroundOnly,
            HudLockHotkey = new HotkeySetting { Modifiers = "Control+Alt", Key = "F10" },
            HudVisibilityHotkey = new HotkeySetting { Modifiers = "Control+Alt", Key = "F11" },
            HudWindowGeometry = expectedGeometry,
        }));
        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal(0.45, loaded.HudSurfaceOpacity);
        Assert.True(loaded.HudModifierDragEnabled);
        Assert.Equal(HudVisibilityMode.GameForegroundOnly, loaded.HudVisibilityMode);
        Assert.Equal("Control+Alt+F10", Format(loaded.HudLockHotkey));
        Assert.Equal("Control+Alt+F11", Format(loaded.HudVisibilityHotkey));
        Assert.Equal(expectedGeometry, loaded.HudWindowGeometry);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    [InlineData(double.NaN, 0.82)]
    public void InvalidOpacity_IsClampedOrFallsBack(double input, double expected)
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));

        Assert.True(store.Save(AppSettings.CreateDefault() with { HudSurfaceOpacity = input }));

        Assert.Equal(expected, store.Current.HudSurfaceOpacity, 3);
    }

    [Fact]
    public void InvalidHotkeys_FallBackWithoutBreakingOtherSettings()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));

        Assert.True(store.Save(AppSettings.CreateDefault() with
        {
            Language = SupportedLocales.Korean,
            HudLockHotkey = new HotkeySetting { Modifiers = string.Empty, Key = "Mouse1" },
            HudVisibilityHotkey = new HotkeySetting { Modifiers = "Control", Key = "?" },
        }));

        Assert.Equal(SupportedLocales.Korean, store.Current.Language);
        Assert.Equal(
            HotkeySetting.DefaultLockToggle,
            store.Current.HudLockHotkey);
        Assert.Equal(
            HotkeySetting.DefaultVisibilityToggle,
            store.Current.HudVisibilityHotkey);
    }

    [Fact]
    public void DuplicateHotkeys_UseSafeVisibilityFallback()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));
        var duplicate = new HotkeySetting { Modifiers = "Control+Shift", Key = "K" };

        Assert.True(store.Save(AppSettings.CreateDefault() with
        {
            HudLockHotkey = duplicate,
            HudVisibilityHotkey = duplicate,
        }));

        Assert.Equal("Control+Shift+K", Format(store.Current.HudLockHotkey));
        Assert.Equal(
            HotkeySetting.DefaultVisibilityToggle,
            store.Current.HudVisibilityHotkey);
    }

    [Fact]
    public void InvalidVisibilityMode_FallsBackToAlways()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonSettingsStore(directory.File("settings.json"));

        Assert.True(store.Save(AppSettings.CreateDefault() with
        {
            HudVisibilityMode = (HudVisibilityMode)999,
        }));

        Assert.Equal(HudVisibilityMode.Always, store.Current.HudVisibilityMode);
    }

    [Fact]
    public void ModifierDrag_DefaultsOff()
    {
        Assert.False(AppSettings.CreateDefault().HudModifierDragEnabled);
    }

    [Fact]
    public void ChatSettings_SaveLoadAndNormalizeWithoutLegacyHexColors()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var store = new JsonSettingsStore(path);

        Assert.True(store.Save(AppSettings.CreateDefault() with
        {
            ChatLayoutMode = ChatLayoutMode.Compact,
            ChatShowTime = false,
            ChatFontPreset = ChatFontPreset.KoPubWorldDotum,
            ChatFontSizePoints = 100,
            ColorTheme = ColorThemeId.Nord,
            ChatMaxLines = 9,
            ChatLineHeightMultiplier = 99,
            ChatMessageSpacing = -4,
            ChatShowImages = false,
            ChatImageMode = ChatImageMode.ThumbnailAndEnlarge,
        }));

        var loaded = new JsonSettingsStore(path).Load();
        Assert.Equal(ChatLayoutMode.Compact, loaded.ChatLayoutMode);
        Assert.False(loaded.ChatShowTime);
        Assert.Equal(ChatFontPreset.WantedSans, loaded.ChatFontPreset);
        Assert.Equal(32, loaded.ChatFontSizePoints);
        Assert.Equal(ColorThemeId.Nord, loaded.ColorTheme);
        Assert.Equal(3, loaded.ChatMaxLines);
        Assert.Equal(1.65, loaded.ChatLineHeightMultiplier);
        Assert.Equal(-2, loaded.ChatMessageSpacing);
        Assert.False(loaded.ChatShowImages);
        Assert.Equal(ChatImageMode.ThumbnailAndEnlarge, loaded.ChatImageMode);
    }

    [Fact]
    public void DefaultHotkeys_AreF9AndF10WithoutModifiers()
    {
        var defaults = AppSettings.CreateDefault();

        Assert.Equal("F10", Format(defaults.HudLockHotkey));
        Assert.Equal("F9", Format(defaults.HudVisibilityHotkey));
    }

    [Fact]
    public void LegacyControlFunctionDefaults_MigrateToBareF9AndF10()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        System.IO.File.WriteAllText(path, """
            {
              "schemaVersion": 6,
              "hudLockHotkey": { "modifiers": "Control", "key": "F9" },
              "hudVisibilityHotkey": { "modifiers": "Control", "key": "F10" }
            }
            """);

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal("F9", Format(loaded.HudVisibilityHotkey));
        Assert.Equal("F10", Format(loaded.HudLockHotkey));
        Assert.Equal(AppSettings.CurrentHotkeySettingsVersion, loaded.HotkeySettingsVersion);
        Assert.False(loaded.HotkeysCustomized);
        var persisted = System.IO.File.ReadAllText(path);
        Assert.Contains(
            $"\"schemaVersion\": {AppSettings.CurrentSchemaVersion}",
            persisted,
            StringComparison.Ordinal);
        Assert.Contains("\"key\": \"F9\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"key\": \"F10\"", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyTripleModifierDefaults_MigrateToBareF9AndF10()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        System.IO.File.WriteAllText(path, """
            {
              "schemaVersion": 6,
              "hudLockHotkey": { "modifiers": "Control+Shift+Alt", "key": "L" },
              "hudVisibilityHotkey": { "modifiers": "Control+Shift+Alt", "key": "H" }
            }
            """);

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal("F9", Format(loaded.HudVisibilityHotkey));
        Assert.Equal("F10", Format(loaded.HudLockHotkey));
    }

    [Fact]
    public void ExplicitCustomLegacyHotkeys_ArePreserved()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        System.IO.File.WriteAllText(path, """
            {
              "schemaVersion": 6,
              "hotkeysCustomized": true,
              "hudLockHotkey": { "modifiers": "Control", "key": "F9" },
              "hudVisibilityHotkey": { "modifiers": "Control", "key": "F10" }
            }
            """);

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal("Control+F9", Format(loaded.HudLockHotkey));
        Assert.Equal("Control+F10", Format(loaded.HudVisibilityHotkey));
        Assert.True(loaded.HotkeysCustomized);
    }

    [Fact]
    public void DefaultSerialization_PreservesNoneModifierAndRemoteConfiguration()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var store = new JsonSettingsStore(path);

        Assert.True(store.Save(AppSettings.CreateDefault() with
        {
            RemoteBackendBaseUrl = "https://overlay.example/path",
            RemoteSelectedChannelId = "222",
        }));

        var json = System.IO.File.ReadAllText(path);
        var loaded = new JsonSettingsStore(path).Load();
        Assert.Contains("\"modifiers\": \"\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("clientSecret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://overlay.example", loaded.RemoteBackendBaseUrl);
        Assert.Equal("222", loaded.RemoteSelectedChannelId);
    }

    [Fact]
    public void Deserialization_DoesNotConvertNoneModifierToControl()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        System.IO.File.WriteAllText(path, """
            {
              "schemaVersion": 7,
              "hotkeySettingsVersion": 1,
              "hudLockHotkey": { "modifiers": "", "key": "F10" },
              "hudVisibilityHotkey": { "modifiers": "", "key": "F9" }
            }
            """);

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal("F9", Format(loaded.HudVisibilityHotkey));
        Assert.Equal("F10", Format(loaded.HudLockHotkey));
    }

    [Fact]
    public void CorruptDeserializedHotkeys_FallBackToNewBareDefaults()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        System.IO.File.WriteAllText(path, """
            {
              "schemaVersion": 7,
              "hotkeySettingsVersion": 1,
              "hudLockHotkey": { "modifiers": "Control", "key": "Mouse1" },
              "hudVisibilityHotkey": { "modifiers": "Control", "key": "?" }
            }
            """);

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal("F9", Format(loaded.HudVisibilityHotkey));
        Assert.Equal("F10", Format(loaded.HudLockHotkey));
    }

    private static string Format(HotkeySetting setting)
    {
        Assert.True(HotkeyGesture.TryParse(setting, out var gesture));
        return gesture.ToString();
    }
}

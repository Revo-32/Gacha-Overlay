using System.Text.Json;
using System.Xml.Linq;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Settings;

namespace GachaOverlay.Tests.Presentation;

public sealed class DiscordQuickFocusRemovalTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData(17, true)]
    [InlineData(17, false)]
    [InlineData(18, true)]
    [InlineData(18, false)]
    public void OldSettingIsIgnoredAndDroppedOnSaveWithoutResettingOtherChoices(int schema, bool oldEnabled)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LSOverlay-FocusRemoval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = schema,
                quickDiscordFocusEnabled = oldEnabled,
                language = "ko",
                remoteBackendBaseUrl = "https://example.test",
                showGtaSession = false,
                hudSurfaceOpacity = 0.4,
            }));
            var store = new JsonSettingsStore(path);
            var settings = store.Load();
            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal("ko", settings.Language);
            Assert.Equal("https://example.test", settings.RemoteBackendBaseUrl);
            Assert.False(settings.ShowGtaSession);
            Assert.Equal(0.4, settings.HudSurfaceOpacity);
            Assert.Equal("F9", settings.HudVisibilityHotkey.Key);
            Assert.Equal("F10", settings.HudLockHotkey.Key);
            Assert.Equal("", settings.PreviousMainChannelHotkey.Key);
            Assert.Equal("", settings.NextMainChannelHotkey.Key);
            Assert.True(store.Save(settings));
            Assert.DoesNotContain("quickDiscordFocusEnabled", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(new JsonSettingsStore(path).Load()));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CompiledAppContainsNoQuickFocusServiceOrSetting()
    {
        Assert.Null(typeof(AppSettings).GetProperty("QuickDiscordFocusEnabled"));
        Assert.Null(typeof(FoundationViewModel).GetProperty("QuickDiscordFocusEnabled"));
        foreach (var name in new[] { "DiscordForegroundService", "IDiscordForegroundService", "DiscordQuickFocusHook", "DiscordFocusResult" })
            Assert.Null(typeof(GlobalHotkeyService).Assembly.GetType("GachaOverlay.App.Services." + name));
        foreach (var name in new[] { "DiscordQuickFocusPolicy", "DiscordQuickFocusRouting", "DiscordQuickFocusRoute", "QuickFocusDecision" })
            Assert.Null(typeof(AppSettings).Assembly.GetType("GachaOverlay.Core.Hud.Hotkeys." + name));
    }

    [Fact]
    public void DefaultHudHotkeysRemainF9F10AndHaveNoImplicitTRegistration()
    {
        var settings = AppSettings.CreateDefault();
        var plan = GlobalHotkeyService.CreateRegistrationPlan(settings.HudLockHotkey, settings.HudVisibilityHotkey);
        Assert.Equal("F9", plan.VisibilityToggle.ToSetting().Key);
        Assert.Equal("F10", plan.LockToggle.ToSetting().Key);
        Assert.Equal("", settings.PreviousMainChannelHotkey.Key);
        Assert.Equal("", settings.NextMainChannelHotkey.Key);
        var service = File.ReadAllText(Path.Combine(Root, "src", "GachaOverlay.App", "Services", "GlobalHotkeyService.cs"));
        Assert.DoesNotContain("QuickFocus", service);
        Assert.DoesNotContain("0x5A05", service);
        Assert.DoesNotContain("0x54", service);
        Assert.DoesNotContain("SetWindowsHookEx", service);
        Assert.Contains("_bindings.Dispose()", service);
    }

    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.ko.resx")]
    [InlineData("Strings.ja.resx")]
    public void SettingsUiAndTranslationsDoNotAdvertiseRemovedFeature(string resourceName)
    {
        var ui = File.ReadAllText(Path.Combine(Root, "src", "GachaOverlay.App", "Presentation", "FoundationWindow.xaml"));
        Assert.DoesNotContain("QuickDiscordFocus", ui);
        var resources = XDocument.Load(Path.Combine(Root, "src", "GachaOverlay.Infrastructure", "Localization", "Resources", resourceName));
        Assert.DoesNotContain(resources.Descendants("data"), item =>
            ((string?)item.Attribute("name"))?.StartsWith("SettingsQuickDiscordFocus", StringComparison.Ordinal) == true);
        var hint = resources.Descendants("data").Single(item => (string?)item.Attribute("name") == "SettingsChannelHotkeyHint").Value;
        Assert.DoesNotContain("Discord", hint);
    }
}

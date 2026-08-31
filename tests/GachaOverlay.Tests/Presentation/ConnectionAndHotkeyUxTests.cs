using System.Windows;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Authentication;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Presentation;

public sealed class ConnectionAndHotkeyUxTests
{
    [Fact]
    public void ResetHotkeys_RestoresF9F10AndClearsCustomizedMarker()
    {
        using var fixture = new ViewModelFixture();
        fixture.Store.Update(settings => settings with
        {
            HudLockHotkey = Parse("Control+F11").ToSetting(),
            HudVisibilityHotkey = Parse("Control+F12").ToSetting(),
            HotkeysCustomized = true,
        });
        fixture.RecreateViewModel((_, _) => true);

        fixture.ViewModel.ResetHotkeysCommand.Execute(null);

        Assert.Equal("F9", fixture.ViewModel.VisibilityHotkeyText);
        Assert.Equal("F10", fixture.ViewModel.LockHotkeyText);
        Assert.Equal(HotkeySetting.DefaultVisibilityToggle, fixture.Store.Current.HudVisibilityHotkey);
        Assert.Equal(HotkeySetting.DefaultLockToggle, fixture.Store.Current.HudLockHotkey);
        Assert.False(fixture.Store.Current.HotkeysCustomized);
        Assert.Contains("F9", fixture.ViewModel.HotkeyValidationMessage, StringComparison.Ordinal);
        Assert.Contains("F10", fixture.ViewModel.HotkeyValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedResetRegistration_PreservesPreviousCustomSettingsAndDisplay()
    {
        using var fixture = new ViewModelFixture();
        var oldLock = Parse("Control+F11").ToSetting();
        var oldVisibility = Parse("Control+F12").ToSetting();
        fixture.Store.Update(settings => settings with
        {
            HudLockHotkey = oldLock,
            HudVisibilityHotkey = oldVisibility,
            HotkeysCustomized = true,
        });
        fixture.RecreateViewModel((_, _) => false);

        fixture.ViewModel.ResetHotkeysCommand.Execute(null);

        Assert.Equal(oldLock, fixture.Store.Current.HudLockHotkey);
        Assert.Equal(oldVisibility, fixture.Store.Current.HudVisibilityHotkey);
        Assert.Equal("Control+F11", fixture.ViewModel.LockHotkeyText);
        Assert.Equal("Control+F12", fixture.ViewModel.VisibilityHotkeyText);
        Assert.True(fixture.Store.Current.HotkeysCustomized);
    }

    [Fact]
    public void ExplicitCustomHotkeys_AreAppliedWithCorrectVisibilityLockMapping()
    {
        using var fixture = new ViewModelFixture();
        HotkeySetting? observedLock = null;
        HotkeySetting? observedVisibility = null;
        fixture.RecreateViewModel((lockSetting, visibilitySetting) =>
        {
            observedLock = lockSetting;
            observedVisibility = visibilitySetting;
            return true;
        });
        fixture.ViewModel.VisibilityHotkeyText = "F7";
        fixture.ViewModel.LockHotkeyText = "F8";

        fixture.ViewModel.ApplyHotkeysCommand.Execute(null);

        Assert.Equal("F7", Format(observedVisibility!));
        Assert.Equal("F8", Format(observedLock!));
        Assert.True(fixture.Store.Current.HotkeysCustomized);
    }

    [Fact]
    public void MissingCredentials_StatusIsActionableAndSetupCommandIsAvailable()
    {
        using var fixture = new ViewModelFixture();
        DiscordConnectionSetupRequest? observed = null;
        fixture.RecreateViewModel(
            (_, _) => true,
            request =>
            {
                observed = request;
                return DiscordConnectionSetupResult.Succeeded;
            });
        fixture.ViewModel.DiscordClientIdText = "123456789";
        fixture.ViewModel.DiscordRedirectUriText = "https://127.0.0.1";
        fixture.ViewModel.SetDiscordClientSecret("protected-secret");
        fixture.ViewModel.UpdateDiscordStatus(new DiscordConnectionStatus(
            DiscordConnectionState.ConfigurationRequired,
            0,
            "CredentialsMissing",
            DateTimeOffset.UtcNow));

        Assert.Equal("Discord configuration required", fixture.ViewModel.DiscordConnectionStatusText);
        Assert.True(fixture.ViewModel.SaveAndConnectDiscordCommand.CanExecute(null));
        fixture.ViewModel.SaveAndConnectDiscordCommand.Execute(null);
        Assert.Equal("123456789", observed?.ClientId);
        Assert.Equal("protected-secret", observed?.ClientSecret);
    }

    [Theory]
    [InlineData(SupportedLocales.English, "Discord configuration required")]
    [InlineData(SupportedLocales.Korean, "Discord 설정 필요")]
    [InlineData(SupportedLocales.Japanese, "Discord 設定が必要")]
    public void CredentialsMissingText_IsLocalizedWithoutVerificationLauncherInstruction(
        string locale,
        string expected)
    {
        using var fixture = new ViewModelFixture(locale);
        fixture.ViewModel.UpdateDiscordStatus(new DiscordConnectionStatus(
            DiscordConnectionState.ConfigurationRequired,
            0,
            "CredentialsMissing",
            DateTimeOffset.UtcNow));

        Assert.Equal(expected, fixture.ViewModel.DiscordConnectionStatusText);
        Assert.DoesNotContain(
            "M2",
            fixture.ViewModel.DiscordConnectionStatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("TrayDiscordConnectionSetup", fixture.Localization["TrayDiscordConnectionSetup"]);
        Assert.NotEqual("TrayDiscordReconnect", fixture.Localization["TrayDiscordReconnect"]);
    }

    [Fact]
    public void UserFacingConnectionResources_DoNotReferenceDevelopmentLauncher()
    {
        foreach (var locale in new[]
                 {
                     SupportedLocales.English,
                     SupportedLocales.Korean,
                     SupportedLocales.Japanese,
                 })
        {
            var localization = new ResourceLocalizationService(locale);
            foreach (var key in new[]
                     {
                         "DiscordStatusConfigurationRequired",
                         "DiscordStatusAuthenticationRequired",
                         "SettingsDiscordSecretRequired",
                         "SettingsDiscordSavedAndConnecting",
                         "TrayDiscordConnectionSetup",
                     })
            {
                Assert.DoesNotContain("M2", localization[key], StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("launcher", localization[key], StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static HotkeyGesture Parse(string text)
    {
        Assert.True(HotkeyGesture.TryParseDisplayText(text, out var gesture));
        return gesture;
    }

    private static string Format(HotkeySetting setting)
    {
        Assert.True(HotkeyGesture.TryParse(setting, out var gesture));
        return gesture.ToString();
    }

    private sealed class ViewModelFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public ViewModelFixture(string locale = SupportedLocales.English)
        {
            Store = new JsonSettingsStore(_directory.File("settings.json"));
            Store.Load();
            Localization = new ResourceLocalizationService(locale);
            RecreateViewModel((_, _) => true);
        }

        public JsonSettingsStore Store { get; }

        public ResourceLocalizationService Localization { get; }

        public FoundationViewModel ViewModel { get; private set; } = null!;

        public void RecreateViewModel(
            Func<HotkeySetting, HotkeySetting, bool> applyHotkeys,
            Func<DiscordConnectionSetupRequest, DiscordConnectionSetupResult>? configure = null)
        {
            ViewModel?.Dispose();
            ViewModel = new FoundationViewModel(
                Store,
                Localization,
                NullAppLogger.Instance,
                new ChatTypographyResolver(NullAppLogger.Instance, new SystemFontCatalog()),
                () => { },
                _ => { },
                () => { },
                applyHotkeys,
                configure,
                () => { },
                () => new DiscordConnectionSetupSnapshot(
                    !string.IsNullOrWhiteSpace(Store.Current.DiscordClientId),
                    ProtectedCredentialStatus.Missing,
                    ProtectedCredentialStatus.Missing,
                    !string.IsNullOrWhiteSpace(Store.Current.DiscordGuildId),
                    !string.IsNullOrWhiteSpace(Store.Current.DiscordMainChannelId),
                    !string.IsNullOrWhiteSpace(Store.Current.DiscordSalesChannelId)));
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class SystemFontCatalog : IChatFontCatalog
    {
        public bool TryResolveBundled(
            string wpfFamilyName,
            string metadataFamilyName,
            FontWeight requestedWeight,
            string resolvedDisplayName,
            out ResolvedChatFontRole? role,
            out ChatFontFallbackReason failureReason)
        {
            role = Create(requestedWeight, resolvedDisplayName);
            failureReason = default;
            return true;
        }

        public bool TryResolveSystem(
            string familyName,
            FontWeight requestedWeight,
            out ResolvedChatFontRole? role)
        {
            role = Create(requestedWeight, familyName);
            return true;
        }

        public ResolvedChatFontRole ResolveFallback(
            FontWeight requestedWeight,
            ChatFontFallbackReason reason) =>
            Create(requestedWeight, "Segoe UI") with
            {
                IsFallback = true,
                FallbackReason = reason,
            };

        private static ResolvedChatFontRole Create(FontWeight weight, string name) => new(
            new System.Windows.Media.FontFamily("Segoe UI"),
            weight,
            name,
            ChatFontResolutionSource.System,
            false,
            null);
    }
}

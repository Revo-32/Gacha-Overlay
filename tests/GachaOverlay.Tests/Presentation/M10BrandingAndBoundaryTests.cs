using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Threading;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Settings;

namespace GachaOverlay.Tests.Presentation;

public sealed class M10BrandingAndBoundaryTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData("LS_Overlay_icon.png", "AD47711F668097536B24DC71706ADF612E3A420900860A086B2EA61F6CF6D295")]
    [InlineData("LS_Overlay_logo.png", "3317B093CAFD8F97D797CF159C795589E0E2A8C959571A9C049367DEB30F42CB")]
    [InlineData("LS_Overlay_Banner.png", "ACFDE7902FD7CA5911BA9FB98D44B54C94CC8B5C8150A1D1BE7CB0F33BE3AE3C")]
    public void ApprovedArtworkBytesRemainUnchanged(string name, string expected)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root, "assets", "branding", name));
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    [Fact]
    public void DerivedIcoContainsAllSevenBoundedPngFrames()
    {
        var data = File.ReadAllBytes(Path.Combine(Root, "src", "GachaOverlay.App", "Assets", "Branding", "LSOverlay-AppIcon.ico"));
        Assert.Equal(1, BitConverter.ToUInt16(data, 2));
        Assert.Equal(7, BitConverter.ToUInt16(data, 4));
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        for (var i = 0; i < sizes.Length; i++)
        {
            var entry = 6 + i * 16;
            Assert.Equal(sizes[i], data[entry] == 0 ? 256 : data[entry]);
            Assert.Equal(data[entry], data[entry + 1]);
            var length = BitConverter.ToInt32(data, entry + 8);
            var offset = BitConverter.ToInt32(data, entry + 12);
            Assert.InRange(offset + length, 1, data.Length);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, data.Skip(offset).Take(4));
        }
    }

    [Fact]
    public void ProductWiringAndDefaultHelperCannotStartLegacyOrOperatorFlows()
    {
        var host = File.ReadAllText(Path.Combine(Root, "src", "GachaOverlay.App", "Lifecycle", "ApplicationHost.cs"));
        Assert.Contains("channelPolicy: MainChannelPolicy.Apply", host);
        var helper = File.ReadAllText(Path.Combine(Root, "tools", "dev", "run-ls-m10-local.ps1"));
        Assert.DoesNotContain("Read-Host", helper);
        Assert.DoesNotContain("LSOverlay.Backend.exe", helper);
        Assert.DoesNotContain("Stop-Process", helper);
        var onboarding = File.ReadAllText(Path.Combine(Root, "src", "GachaOverlay.App", "Presentation", "OnboardingWindow.xaml"));
        Assert.DoesNotContain("BackendBaseUrl", onboarding);
        var settings = File.ReadAllText(Path.Combine(Root, "src", "GachaOverlay.App", "Presentation", "FoundationWindow.xaml"));
        Assert.DoesNotContain("BackendBaseUrl", settings.Split("x:Key=\"DiscordTemplate\"")[1].Split("</DataTemplate>")[0]);
        Assert.Contains("BackendBaseUrl", settings.Split("x:Key=\"DeveloperTemplate\"")[1]);
        Assert.DoesNotContain("Feature Freeze", settings);
    }

    [Fact]
    public void InvalidOptionalBindingsDoNotDisablePrimaryHudHotkeys()
    {
        var path = Path.Combine(Path.GetTempPath(), "LSOverlay-M10-" + Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var store = new JsonSettingsStore(path);
            store.Update(settings => settings with
            {
                PreviousMainChannelHotkey = new() { Key = "F9" },
                NextMainChannelHotkey = new() { Key = "invalid-key" },
            });
            Assert.Equal("F9", store.Current.HudVisibilityHotkey.Key);
            Assert.Equal("F10", store.Current.HudLockHotkey.Key);
            Assert.Equal("", store.Current.PreviousMainChannelHotkey.Key);
            Assert.Equal("", store.Current.NextMainChannelHotkey.Key);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void QuickFocusHookDisposalStopsOnlyItsOwnedThreadAndCannotActivateDiscord()
    {
        var foreground = new NeverGameForeground();
        var hook = new DiscordQuickFocusHook(Dispatcher.CurrentDispatcher, NullAppLogger.Instance, foreground);
        hook.SetEnabled(true);
        var thread = (Thread)typeof(DiscordQuickFocusHook).GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hook)!;
        hook.Dispose();
        hook.Dispose();
        Assert.False(thread.IsAlive);
        Assert.Equal(0, foreground.Activations);
        Assert.False(Dispatcher.CurrentDispatcher.HasShutdownStarted);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("ja")]
    public void NewUserFacingStringsExistAndDoNotExposeTransportEnums(string locale)
    {
        var localization = new ResourceLocalizationService(locale);
        foreach (var key in new[] { "HudHealthReconnecting", "HudHealthError", "HudHealthLoginRequired",
            "SettingsQuickDiscordFocus", "SalesDetailRequired", "SalesAgeMinutes", "SessionFull" })
        {
            Assert.NotEqual(key, localization[key]);
            Assert.DoesNotContain("RemotePrimary", localization[key]);
            Assert.DoesNotContain("Bootstrapping", localization[key]);
        }
    }

    private sealed class NeverGameForeground : IDiscordForegroundService
    {
        public int Activations { get; private set; }
        public bool IsGtaEnhancedForeground() => false;
        public bool TryActivateDiscord() { Activations++; return false; }
    }
}

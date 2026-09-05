using System.Reflection;
using System.Text.Json;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Paths;

namespace GachaOverlay.Tests.Release;

public sealed class Stable210MetadataTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void ReleaseMetadataMatchesManifestWithoutRenamingManagedAssembly()
    {
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Root, "tools/release/ls-2.1.1.json")));
        var version = manifest.RootElement.GetProperty("version").GetString();
        var app = typeof(GachaOverlay.App.App).Assembly;

        Assert.Equal("2.1.1", version);
        Assert.Equal(version,
            app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        Assert.Equal("GachaOverlay.App", app.GetName().Name);
        Assert.Equal("2.1.1.0",
            app.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.Equal("LS Overlay", app.GetCustomAttribute<AssemblyProductAttribute>()!.Product);
        Assert.Equal("LS Overlay", app.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description);
        Assert.Equal("LSOverlay.exe",
            manifest.RootElement.GetProperty("executableName").GetString());
        Assert.Equal("LS-Overlay-2.1.1-win-x64.zip",
            manifest.RootElement.GetProperty("zipName").GetString());
        Assert.Equal("LS-Overlay-2.1-Quick-Start-ko.pdf",
            manifest.RootElement.GetProperty("quickStartName").GetString());
        Assert.Equal("LS-Overlay-2.1-User-Guide-ko.pdf",
            manifest.RootElement.GetProperty("guideName").GetString());
        Assert.Equal("LS-Overlay-2.1.1-SHA256.txt",
            manifest.RootElement.GetProperty("checksumName").GetString());
        Assert.Equal("v2.1.1", manifest.RootElement.GetProperty("tag").GetString());
        Assert.Equal("LS Overlay 2.1.1",
            manifest.RootElement.GetProperty("title").GetString());
        Assert.False(manifest.RootElement.GetProperty("prerelease").GetBoolean());
    }

    [Fact]
    public void StablePackagingPreservesExistingProfileAndCredentialIdentity()
    {
        Assert.Equal(Path.Combine("X:/Users/Test", "GachaOverlay"),
            new LocalApplicationPaths("X:/Users/Test").DataDirectory);
        Assert.Equal(22, AppSettings.CurrentSchemaVersion);
        var app = File.ReadAllText(Path.Combine(Root, "src/GachaOverlay.App/App.xaml.cs"));
        Assert.Contains(
            @"Local\GachaOverlay.Foundation.74B75E39-1972-4FA1-B718-5546F7D85E30", app);
        var credential = File.ReadAllText(Path.Combine(
            Root, "src/GachaOverlay.App/Services/DpapiRemoteAccessCredentialStore.cs"));
        Assert.Contains("LSOverlay.M9.4.RemoteAccessCredential", credential);
        Assert.Contains("DataProtectionScope.CurrentUser", credential);
    }

    [Fact]
    public void PublicDocumentsCarryCurrentVersionContactAndLinksWithoutLegacySetup()
    {
        foreach (var relative in new[]
                 {
                     "README.md",
                     "docs/2.1/quick-start/LS-Overlay-2.1-Quick-Start-ko.md",
                     "docs/2.1/user-guide/LS-Overlay-2.1-User-Guide-ko.md",
                     "docs/releases/LS-Overlay-2.1.1-release-notes.md",
                 })
        {
            var text = File.ReadAllText(Path.Combine(Root, relative));
            Assert.Contains(relative.StartsWith("docs/2.1/", StringComparison.Ordinal) ? "2.1.0" : "2.1.1", text);
            Assert.Contains("LSOverlay.exe", text);
            Assert.Contains("mailto:revo.32.39.41@gmail.com", text);
            Assert.Contains("https://overlay.revo32.cloud/privacy", text);
            Assert.Contains("https://overlay.revo32.cloud/terms", text);
            Assert.Contains("https://status.revo32.cloud", text);
            Assert.DoesNotContain("Client Secret 값을 입력", text);
            Assert.DoesNotContain("Bot Token을 입력", text);
            Assert.DoesNotContain("Guild ID", text);
            Assert.DoesNotContain("E:\\Codex", text);
            Assert.DoesNotContain("RemotePrimary", text);
        }
    }

    [Fact]
    public void TwoGuideSourcesUseCurrentSharedFactsAndExplicitScreenshotGate()
    {
        var quick = File.ReadAllText(Path.Combine(
            Root, "docs/2.1/quick-start/LS-Overlay-2.1-Quick-Start-ko.md"));
        var full = File.ReadAllText(Path.Combine(
            Root, "docs/2.1/user-guide/LS-Overlay-2.1-User-Guide-ko.md"));

        foreach (var text in new[] { quick, full })
        {
            Assert.Contains("매일 15:00 KST", text);
            Assert.Contains("목요일 18:00 KST", text);
            Assert.Contains("F9", text);
            Assert.Contains("F10", text);
            Assert.Contains("ESC", text);
            Assert.Contains("%LOCALAPPDATA%\\GachaOverlay", text);
            Assert.Contains("SCREENSHOT REQUIRED", text);
            Assert.DoesNotContain("Daily 18:00", text);
        }

        var buildScript = File.ReadAllText(Path.Combine(
            Root, "tools/manual/build_21_guides.py"));
        Assert.Contains("assert_screenshots", buildScript);
        Assert.Contains("WantedSansVariable.ttf", buildScript);
        Assert.Contains("addOutlineEntry", buildScript);
    }

    [Fact]
    public void StablePackagerRequiresBothGuidesAndAllLicenseGroups()
    {
        var script = File.ReadAllText(Path.Combine(
            Root, "tools/release/package-ls-stable.ps1"));
        Assert.Contains("QuickStartPath", script);
        Assert.Contains("GuidePath", script);
        Assert.Contains("ExpectedExeSha256", script);
        Assert.Contains("archiveContents", script);

        using var licenseManifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(Root, "tools/release/license-manifest.json")));
        var directories = licenseManifest.RootElement.GetProperty("entries")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("stagingDirectory").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(
            new HashSet<string?> { "Runtime", "Fonts", "Themes", "Media" }
                .SetEquals(directories));
    }
}

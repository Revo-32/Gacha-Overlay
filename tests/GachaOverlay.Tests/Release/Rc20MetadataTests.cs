using System.Reflection;
using System.Text.Json;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Paths;

namespace GachaOverlay.Tests.Release;

public sealed class Rc20MetadataTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void ReleaseMetadataMatchesManifestWithoutRenamingManagedAssembly()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tools/release/ls-2.0.0.json")));
        var version = manifest.RootElement.GetProperty("version").GetString();
        var app = typeof(GachaOverlay.App.App).Assembly;
        Assert.Equal("2.0.0", version);
        Assert.Equal(version, app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        Assert.Equal("GachaOverlay.App", app.GetName().Name);
        Assert.Equal("2.0.0.0", app.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.Equal("LS Overlay", app.GetCustomAttribute<AssemblyProductAttribute>()!.Product);
        Assert.Equal("LS Overlay", app.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description);
        Assert.Equal("LSOverlay.exe", manifest.RootElement.GetProperty("executableName").GetString());
        Assert.Equal("LS-Overlay-2.0.0-win-x64.zip", manifest.RootElement.GetProperty("zipName").GetString());
        Assert.Equal("LS-Overlay-2.0-User-Guide-ko.pdf", manifest.RootElement.GetProperty("guideName").GetString());
        Assert.Equal("v2.0.0", manifest.RootElement.GetProperty("tag").GetString());
        Assert.Equal("LS Overlay 2.0.0", manifest.RootElement.GetProperty("title").GetString());
        Assert.False(manifest.RootElement.GetProperty("prerelease").GetBoolean());
    }

    [Fact]
    public void RcPackagingPreservesExistingProfileAndCredentialIdentity()
    {
        Assert.Equal(Path.Combine("X:/Users/Test", "GachaOverlay"), new LocalApplicationPaths("X:/Users/Test").DataDirectory);
        Assert.Equal(22, AppSettings.CurrentSchemaVersion);
        var app = File.ReadAllText(Path.Combine(Root, "src/GachaOverlay.App/App.xaml.cs"));
        Assert.Contains(@"Local\GachaOverlay.Foundation.74B75E39-1972-4FA1-B718-5546F7D85E30", app);
        var credential = File.ReadAllText(Path.Combine(Root, "src/GachaOverlay.App/Services/DpapiRemoteAccessCredentialStore.cs"));
        Assert.Contains("LSOverlay.M9.4.RemoteAccessCredential", credential);
        Assert.Contains("DataProtectionScope.CurrentUser", credential);
    }

    [Fact]
    public void NewUserDocumentsCarryCurrentVersionContactAndLinksWithoutLegacySetup()
    {
        foreach (var relative in new[]
                 {
                     "README.md", "docs/user/QUICK-START-ko.md", "docs/user/LS-Overlay-2.0-RC-User-Guide-ko.md",
                     "docs/releases/LS-Overlay-2.0.0-github-release.md",
                 })
        {
            var text = File.ReadAllText(Path.Combine(Root, relative));
            Assert.Contains("2.0.0", text);
            Assert.Contains("LSOverlay.exe", text);
            Assert.Contains("mailto:revo.32.39.41@gmail.com", text);
            Assert.Contains("https://overlay.revo32.cloud/privacy", text);
            Assert.Contains("https://overlay.revo32.cloud/terms", text);
            Assert.Contains("https://status.revo32.cloud", text);
            Assert.DoesNotContain("Client Secret", text);
            Assert.DoesNotContain("Bot Token", text);
            Assert.DoesNotContain("Guild ID", text);
            Assert.DoesNotContain("E:\\Codex", text);
        }
    }
}

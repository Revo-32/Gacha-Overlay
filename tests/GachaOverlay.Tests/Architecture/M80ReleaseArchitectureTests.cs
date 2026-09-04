using System.Text.RegularExpressions;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Paths;

namespace GachaOverlay.Tests.Architecture;

public sealed class M80ReleaseArchitectureTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Fact]
    public void Core_DoesNotReferenceAppInfrastructureWpfOrUiaAssemblies()
    {
        var references = typeof(AppSettings).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("GachaOverlay.App", references);
        Assert.DoesNotContain("GachaOverlay.Infrastructure", references);
        Assert.DoesNotContain("PresentationCore", references);
        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("UIAutomationClient", references);
        Assert.DoesNotContain("UIAutomationTypes", references);
    }

    [Fact]
    public void CoreSource_DoesNotContainRawUiaImplementationConcepts()
    {
        var source = ReadProductionSources("GachaOverlay.Core");

        Assert.DoesNotContain("AutomationElement", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ControlType", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows.Automation", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionLogs_DoNotSerializeWholeDiscordPayloads()
    {
        var source = ReadProductionSources("GachaOverlay.Infrastructure") +
            ReadProductionSources("GachaOverlay.App");
        var rawPayloadLog = new Regex(
            "(?:_logger|Logger)\\s*\\.\\s*(?:Information|Warning|Error)\\s*\\([^;]*GetRawText\\s*\\(",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        Assert.DoesNotMatch(rawPayloadLog, source);
    }

    [Fact]
    public void Source_HasNoKnownSingleFileDangerousLocationApisOrWorkingDirectoryAssumptions()
    {
        var source = ReadProductionSources("GachaOverlay.Core") +
            ReadProductionSources("GachaOverlay.Infrastructure") +
            ReadProductionSources("GachaOverlay.App");

        foreach (var forbidden in new[]
                 {
                     "Assembly.Location",
                     "Assembly.CodeBase",
                     "Assembly.GetFile(",
                     "Assembly.GetFiles(",
                     "Environment.CurrentDirectory",
                     "Directory.GetCurrentDirectory(",
                 })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("E:\\Codex\\", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeUserData_IsRootedUnderConfiguredLocalApplicationData()
    {
        var paths = new LocalApplicationPaths("X:\\UserLocalData");

        Assert.StartsWith(
            Path.Combine("X:\\UserLocalData", "GachaOverlay"),
            paths.SettingsFilePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(paths.DataDirectory, paths.LogDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            paths.DataDirectory,
            paths.SalesProductOverrideFilePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseSchemas_MatchCurrentAuditedVersions()
    {
        Assert.Equal(19, AppSettings.CurrentSchemaVersion);
        Assert.Equal(2, GachaOverlay.Core.Sales.SalesProductCatalogDocument.CurrentVersion);
    }

    [Fact]
    public void DeveloperSettingsTemplate_DoesNotExposeCredentialOrRawRpcControls()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Presentation",
            "FoundationWindow.xaml"));
        var start = xaml.IndexOf("<DataTemplate x:Key=\"DeveloperTemplate\">", StringComparison.Ordinal);
        var end = xaml.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var developerTemplate = xaml[start..end];

        Assert.DoesNotContain("ClientSecret", developerTemplate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AccessToken", developerTemplate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RefreshToken", developerTemplate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RawRpc", developerTemplate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ResetProductOverridesCommand", developerTemplate, StringComparison.Ordinal);

        var host = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Lifecycle",
            "ApplicationHost.cs"));
        Assert.Contains("SettingsDeveloperResetOverridesConfirm", host, StringComparison.Ordinal);
        Assert.Contains("MessageBoxButton.YesNo", host, StringComparison.Ordinal);
    }

    private static string ReadProductionSources(string project) => string.Join(
        Environment.NewLine,
        Directory.GetFiles(
                Path.Combine(RepositoryRoot, "src", project),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
}

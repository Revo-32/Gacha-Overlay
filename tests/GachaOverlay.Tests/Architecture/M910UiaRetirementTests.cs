using System.Text.Json;
using GachaOverlay.Core.Providers;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Settings;

namespace GachaOverlay.Tests.Architecture;

public sealed class M910UiaRetirementTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    public static TheoryData<string> RetiredProductionPatternData => new()
    {
        "System.Windows.Automation",
        "AutomationElement",
        "DiscordUiaSalesReactionObservationSource",
        "WindowsDiscordAccessibilityAdapter",
        "WindowsDiscordOpaqueMessageResolver",
        "UiaFallback",
        "AccessibilityUnavailable",
        "force-renderer-accessibility",
    };

    [Theory]
    [MemberData(nameof(RetiredProductionPatternData))]
    public void ProductionSource_HasNoRetiredImplementationReference(string pattern)
    {
        var matches = Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "src"),
                "*",
                SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".xaml" or ".resx" or ".csproj")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(matches);
    }

    [Fact]
    public void RetiredImplementationFiles_AreDeleted()
    {
        var retired = new[]
        {
            "src/GachaOverlay.App/Services/Sales/WindowsDiscordAccessibilityAdapter.cs",
            "src/GachaOverlay.App/Services/Sales/DiscordUiaSalesReactionObservationSource.cs",
            "src/GachaOverlay.App/Services/Sales/DiscordAccessibilityModels.cs",
            "src/GachaOverlay.App/Services/Sales/DiscordUiaRawModels.cs",
            "src/GachaOverlay.App/Services/Sales/DiscordSalesCompletionReactionMatcher.cs",
            "src/GachaOverlay.App/Services/Sales/Win32DiscordWindowLocator.cs",
            "src/GachaOverlay.App/Services/WindowsDiscordOpaqueMessageResolver.cs",
            "tools/Start-DiscordAccessibilityVerification.ps1",
        };

        Assert.All(retired, path => Assert.False(File.Exists(Path.Combine(
            RepositoryRoot,
            path.Replace('/', Path.DirectorySeparatorChar)))));
    }

    [Fact]
    public void ProviderCatalog_ExposesRemoteProductionAuthority()
    {
        Assert.Equal(
            OverlayProviderActivation.Production,
            OverlayProviderCatalog.LsOverlayRemote.Activation);
        Assert.True(OverlayProviderCatalog.LsOverlayRemote.Supports(
            OverlayDataCapabilities.SalesMessages |
            OverlayDataCapabilities.SalesCompletionEvidence |
            OverlayDataCapabilities.SalesReactionWriteBack));
    }

    [Fact]
    public void RuntimeComposition_DoesNotFeedLocalRpcIntoSalesCoordinator()
    {
        var host = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Lifecycle",
            "ApplicationHost.cs"));
        var coordinator = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Services",
            "SalesPresentationCoordinator.cs"));

        Assert.DoesNotContain("ApplySourceState", host, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyRpcStatus", host, StringComparison.Ordinal);
        Assert.DoesNotContain("ISalesReactionObservationSource", coordinator, StringComparison.Ordinal);
        Assert.Contains("ApplyRemoteSalesBootstrap", coordinator, StringComparison.Ordinal);
        Assert.Contains("ApplyRemoteSalesMutation", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteCoordinator_IsTheUnconditionalProductionSession()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Services",
            "RemoteChatProductionCoordinator.cs"));
        Assert.Contains("while (!cancellationToken.IsCancellationRequested)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MainChatSource", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema16_MigratesRemovedPreferenceAndPreservesUnknownFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gacha-m910-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "SchemaVersion": 15,
                  "Language": "ko",
                  "SalesAcquisitionPreference": "ForceLegacy",
                  "FutureSetting": { "enabled": true }
                }
                """);

            var loaded = new JsonSettingsStore(path).Load();
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.NotNull(loaded.ExtensionData);
            Assert.False(loaded.ExtensionData!.ContainsKey("SalesAcquisitionPreference"));
            Assert.True(loaded.ExtensionData.ContainsKey("FutureSetting"));

            using var persisted = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(persisted.RootElement.TryGetProperty(
                "SalesAcquisitionPreference",
                out _));
            Assert.True(persisted.RootElement.TryGetProperty("FutureSetting", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void M910Helper_ReusesHardenedAuditAndOffersRequiredModes()
    {
        var helper = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tools",
            "dev",
            "run-ls-m910-local.ps1"));
        Assert.Contains("SecureString", File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tools",
            "dev",
            "run-ls-m99-audit.ps1")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Quick", helper, StringComparison.Ordinal);
        Assert.Contains("ReconnectCycles = 5", helper, StringComparison.Ordinal);
        Assert.Contains("run-ls-m99-audit.ps1", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Soak", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteOnly", helper, StringComparison.Ordinal);
    }
}

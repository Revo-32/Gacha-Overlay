using System.Text.Json;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Providers;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Architecture;

public sealed class M911RemoteOnlyRetirementTests
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
        "DiscordRpcClient",
        "DiscordRpcTransport",
        "IDiscordRpcClient",
        "discord-ipc-",
        "DiscordConnectionCoordinator",
        "DiscordAuthenticationService",
        "IDiscordAuthenticationService",
        "oauth2/token",
        "HttpListenerContext",
        "DiscordOAuthSession",
        "DiscordProcessService",
        "MainChatSource.",
        "DiscordLocalRpc",
        "LSO_REMOTE_ONLY_VALIDATION",
    };

    [Theory]
    [MemberData(nameof(RetiredProductionPatternData))]
    public void ProductionSource_HasNoRetiredLocalRpcOrOAuthImplementation(string pattern)
    {
        var matches = ProductionFiles()
            .Where(path => File.ReadAllText(path).Contains(
                pattern,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(matches);
    }

    [Fact]
    public void RetiredImplementationFiles_AreDeleted()
    {
        var retired = new[]
        {
            "src/GachaOverlay.Infrastructure/Discord/Rpc/DiscordRpcClient.cs",
            "src/GachaOverlay.Infrastructure/Discord/Rpc/DiscordRpcTransport.cs",
            "src/GachaOverlay.Infrastructure/Discord/Connection/DiscordConnectionCoordinator.cs",
            "src/GachaOverlay.Infrastructure/Discord/Authentication/DiscordAuthenticationService.cs",
            "src/GachaOverlay.Infrastructure/Discord/Authentication/IDiscordProtectedCredentialStore.cs",
            "src/GachaOverlay.Infrastructure/Discord/Process/DiscordProcessService.cs",
            "src/GachaOverlay.App/Services/DpapiDiscordProtectedCredentialStore.cs",
            "src/GachaOverlay.App/Presentation/DiscordConnectionSetup.cs",
            "src/GachaOverlay.Core/Providers/MainChatSource.cs",
            "tools/Run-M2-Verification.ps1",
        };

        Assert.All(retired, relative => Assert.False(File.Exists(Path.Combine(
            RepositoryRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)))));
    }

    [Fact]
    public void ProviderCatalog_HasExactlyOneProductionRemoteProvider()
    {
        var provider = OverlayProviderCatalog.LsOverlayRemote;

        Assert.Equal(OverlayProviderActivation.Production, provider.Activation);
        Assert.True(provider.Supports(
            OverlayDataCapabilities.Chat |
            OverlayDataCapabilities.SalesMessages |
            OverlayDataCapabilities.HostPresence |
            OverlayDataCapabilities.SalesReactionWriteBack));
        Assert.DoesNotContain(
            typeof(OverlayProviderCatalog).GetProperties(),
            property => property.Name.Contains("Local", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Schema16LocalSelection_MigratesToSchema17RemoteOnlyAndPreservesRemoteSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 16,
              "language": "ko",
              "lastSettingsCategory": 2,
              "settingsCategoryScrollPositions": {
                "Server": 91,
                "Hud": 37
              },
              "remoteBackendBaseUrl": "https://overlay.example/path",
              "remoteSelectedChannelId": "1428747924229193828",
              "mainChatSource": "Local",
              "chatSource": "Legacy",
              "discordClientId": "123456789012345678",
              "discordClientSecret": "legacy-secret-fixture",
              "discordRedirectUri": "http://127.0.0.1:6463/callback",
              "discordOAuthScopes": "rpc identify messages.read",
              "discordOAuthToken": "legacy-token-fixture",
              "discordAccessToken": "legacy-access-fixture",
              "discordRefreshToken": "legacy-refresh-fixture",
              "discordGuildId": "111",
              "discordMainChannelId": "222",
              "discordSalesChannelId": "333",
              "discordAutoLaunch": true,
              "futureSetting": { "enabled": true }
            }
            """);

        var loaded = new JsonSettingsStore(path).Load();

        Assert.Equal(17, loaded.SchemaVersion);
        Assert.Equal(SettingsCategory.Discord, loaded.LastSettingsCategory);
        Assert.Equal("https://overlay.example", loaded.RemoteBackendBaseUrl);
        Assert.Equal("1428747924229193828", loaded.RemoteSelectedChannelId);
        Assert.False(loaded.SettingsCategoryScrollPositions.ContainsKey("Server"));
        Assert.Equal(37, loaded.SettingsCategoryScrollPositions["Hud"]);
        Assert.NotNull(loaded.ExtensionData);
        Assert.True(loaded.ExtensionData!.ContainsKey("futureSetting"));

        using var persisted = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var removed in new[]
                 {
                     "mainChatSource", "chatSource", "discordClientId",
                     "discordClientSecret", "discordRedirectUri", "discordOAuthScopes",
                     "discordOAuthToken", "discordAccessToken", "discordRefreshToken",
                     "discordGuildId", "discordMainChannelId",
                     "discordSalesChannelId", "discordAutoLaunch",
                 })
        {
            Assert.False(persisted.RootElement.TryGetProperty(removed, out _));
        }
        Assert.True(persisted.RootElement.TryGetProperty("futureSetting", out _));
    }

    [Fact]
    public void ExactLegacyCredentialRetirement_PreservesRemoteCredentialAndUnrelatedFiles()
    {
        using var directory = new TemporaryDirectory();
        var legacySecret = directory.File("discord-client-secret.dat");
        var legacyToken = directory.File("discord-oauth-token.dat");
        var remoteToken = directory.File("remote-access-token.dat");
        var unrelated = directory.File("remote-installation-id.txt");
        File.WriteAllBytes(legacySecret, new byte[] { 1 });
        File.WriteAllBytes(legacyToken, new byte[] { 2 });
        var remoteStore = new DpapiRemoteAccessCredentialStore(
            remoteToken,
            NullAppLogger.Instance);
        Assert.True(remoteStore.Save("m911-remote-access-token-fixture"));
        var remoteBytes = File.ReadAllBytes(remoteToken);
        File.WriteAllText(unrelated, "installation-fixture");
        var logger = new RecordingLogger();
        var service = new LegacyCredentialRetirementService(
            legacySecret,
            legacyToken,
            logger);

        Assert.True(service.Retire());
        Assert.True(service.Retire());

        Assert.False(File.Exists(legacySecret));
        Assert.False(File.Exists(legacyToken));
        Assert.Equal(remoteBytes, File.ReadAllBytes(remoteToken));
        Assert.True(remoteStore.TryLoad(out var restoredRemoteToken));
        Assert.Equal("m911-remote-access-token-fixture", restoredRemoteToken);
        Assert.Equal("installation-fixture", File.ReadAllText(unrelated));
        Assert.Equal(2, logger.InformationMessages.Count);
        Assert.All(logger.InformationMessages, message =>
            Assert.DoesNotContain("installation-fixture", message, StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsOnboardingAndDiagnostics_ExposeOnlyRemoteConnectionUx()
    {
        var foundation = Read("src/GachaOverlay.App/Presentation/FoundationWindow.xaml");
        var onboarding = Read("src/GachaOverlay.App/Presentation/OnboardingWindow.xaml");
        var onboardingViewModel = Read(
            "src/GachaOverlay.App/Presentation/OnboardingViewModel.cs");
        var diagnostics = Read("src/GachaOverlay.App/Presentation/FoundationViewModel.cs");

        var combinedUx = foundation + onboarding;
        Assert.Contains("RemoteChatSettings", combinedUx, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSecret", combinedUx, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RedirectUri", combinedUx, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SourceOptions", combinedUx, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerTemplate", foundation, StringComparison.Ordinal);
        Assert.Contains("StepCount = 3", onboardingViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Rpc", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DiscordProcess", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void M911Helper_UsesHardenedRemoteFlowWithoutLegacyPromptsOrSoak()
    {
        var helper = Read("tools/dev/run-ls-m911-local.ps1");
        var hardened = Read("tools/dev/run-ls-m99-audit.ps1");

        Assert.Contains("SecureString", hardened, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Discord Desktop Fully Closed", helper, StringComparison.Ordinal);
        Assert.Contains("ReconnectCycles = 5", helper, StringComparison.Ordinal);
        Assert.Contains("run-ls-m99-audit.ps1", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Soak", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OAuth", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LSO_REMOTE_ONLY_VALIDATION", hardened, StringComparison.Ordinal);
        Assert.Contains("$maximumAttempts = 480", hardened, StringComparison.Ordinal);
        Assert.Contains("Discord Gateway is still reconnecting", hardened, StringComparison.Ordinal);
        Assert.Contains("Discord Gateway WebSocket connection remained unavailable", hardened, StringComparison.Ordinal);
        Assert.Contains("Sanitized log:", hardened, StringComparison.Ordinal);
    }

    private static IEnumerable<string> ProductionFiles() => Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, "src"),
            "*",
            SearchOption.AllDirectories)
        .Where(path => Path.GetExtension(path) is ".cs" or ".xaml" or ".resx" or ".csproj")
        .Where(path => !path.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .Where(path => !path.Contains(
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string Read(string relative) => File.ReadAllText(Path.Combine(
        RepositoryRoot,
        relative.Replace('/', Path.DirectorySeparatorChar)));

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> InformationMessages { get; } = [];

        public void Information(string category, string message) =>
            InformationMessages.Add($"{category}:{message}");

        public void Warning(string category, string message)
        {
        }

        public void Error(string category, string message, Exception? exception = null)
        {
        }
    }
}

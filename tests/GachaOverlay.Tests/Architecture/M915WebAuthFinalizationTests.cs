using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Infrastructure.Localization;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace GachaOverlay.Tests.Architecture;

public sealed class M915WebAuthFinalizationTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

    [Theory]
    [InlineData("LoginRequired", false, true, true)]
    [InlineData("AccessRevoked", true, true, true)]
    [InlineData("Reconnecting", true, false, false)]
    [InlineData("AuthorizationUnavailable", true, false, false)]
    [InlineData("Live", true, false, false)]
    [InlineData("LoginInProgress", false, true, false)]
    public void LoginActionTracksCredentialValidityNotTransientNetworkState(string health, bool credential, bool visible, bool enabled)
    {
        var snapshot = new RemoteChatSnapshot("https://overlay.revo32.cloud", Enum.Parse<RemoteChatHealthState>(health),
            health, credential, null, Array.Empty<RemoteChannelOption>(), null);
        using var viewModel = new RemoteChatSettingsViewModel(new ResourceLocalizationService("ko"), snapshot,
            _ => Task.FromResult(true), () => Task.CompletedTask, () => { }, () => Task.FromResult(true),
            () => Task.CompletedTask, _ => Task.FromResult(true));
        Assert.Equal(visible, viewModel.NeedsLogin);
        Assert.Equal(enabled, viewModel.BeginLoginCommand.CanExecute(null));
    }

    [Fact]
    public void NoLegacyContractsClientApisOrPairCodeUiRemain()
    {
        Assert.DoesNotContain(typeof(OverlayTransportProtocol).Assembly.GetTypes(), type => type.Name.Contains("Pairing", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(ILSOverlayRemoteClient).GetMethods(), method => method.Name.Contains("Pairing", StringComparison.Ordinal));
        Assert.Null(typeof(RemoteChatSnapshot).GetProperty("PairingCode"));
        Assert.Null(typeof(RemoteChatSettingsViewModel).GetProperty("HasPairingCode"));
        foreach (var file in new[] { "FoundationWindow.xaml", "OnboardingWindow.xaml" })
        {
            var xaml = Read("src/GachaOverlay.App/Presentation/" + file);
            Assert.DoesNotContain("Pairing", xaml);
            Assert.DoesNotContain("/lsoverlay", xaml);
            Assert.Contains("BeginLoginCommand", xaml);
        }
    }

    [Fact]
    public void BackendHasNoInteractionHandlerRegistrationOrPublicPairRoutes()
    {
        var backendRoot = Path.Combine(Root, "src/LSOverlay.Backend");
        var sources = string.Join('\n', Directory.EnumerateFiles(backendRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "Migrations"))
            .Select(File.ReadAllText));
        foreach (var forbidden in new[] { "SlashCommandExecuted", "DiscordPairingCommand", "PairingService", "CreateApplicationCommand", "BulkOverwriteApplicationCommands", "TryReadPairingClaim", "CreateUserCode", "NormalizeUserCode", "/api/v1/pairings" })
            Assert.DoesNotContain(forbidden, sources);
        Assert.Contains("AddHostedService<SlashPairingRetirementWorker>", Read("src/LSOverlay.Backend/Program.cs"));
        Assert.DoesNotContain("Migration", Read("src/LSOverlay.Backend/Transport/BackendTransportHosting.cs"));
    }

    [Fact]
    public void WebAuthNeverChecksApplicationCommandPermissionsOrUsesLocalFallback()
    {
        var sources = Read("src/GachaOverlay.App/Services/RemoteChatProductionCoordinator.WebAuth.cs") +
            Read("src/LSOverlay.Backend/WebAuth/DiscordWebAuthService.cs");
        foreach (var forbidden in new[] { "UseApplicationCommands", "GuildPermission", "ManageGuild", "Administrator", "IsLoopback", "Pairing", "lsoverlay" })
            Assert.DoesNotContain(forbidden, sources);
        Assert.Contains("scope=identify", sources);
        Assert.Contains("code_challenge_method=S256", sources);
        Assert.Contains("WebAuthUnavailable", sources);
    }

    [Fact]
    public void MigrationOnlyDeletesExactCommandAndHasBoundedIndependentLifetime()
    {
        var migration = Read("src/LSOverlay.Backend/Migrations/SlashPairingRetirementMigration.cs");
        var worker = Read("src/LSOverlay.Backend/Migrations/SlashPairingRetirementWorker.cs");
        Assert.DoesNotContain("PostAsync", migration);
        Assert.DoesNotContain("PutAsync", migration);
        Assert.DoesNotContain("ClientCredentialRegistry", migration);
        Assert.Contains("{route}/{commandId}", migration);
        Assert.Contains("IsExactLegacyShape", migration);
        Assert.Contains("attempt <= 3", worker);
        Assert.Contains("CancelAfter(TimeSpan.FromSeconds(20))", worker);
        Assert.Contains("BackgroundService", worker);
        Assert.DoesNotContain("SlashPairing", Read("src/LSOverlay.Backend/Discord/DiscordGatewayAdapter.cs"));
    }
}

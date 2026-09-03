using Discord;
using LSOverlay.Backend.Discord;
using LSOverlay.Protocol;
using System.Text.RegularExpressions;

namespace GachaOverlay.Tests.Backend;

public sealed class M92BoundaryTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void ProtocolAndRemoteClient_ArePlatformNeutralAndDiscordFree()
    {
        var protocol = Project("src", "LSOverlay.Protocol", "LSOverlay.Protocol.csproj");
        var client = Project("src", "LSOverlay.RemoteClient", "LSOverlay.RemoteClient.csproj");

        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", protocol, StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", client, StringComparison.Ordinal);
        Assert.DoesNotContain("Discord.Net", protocol, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Discord.Net", client, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseWPF", protocol, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseWPF", client, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsApp_ComposesRemoteClientWithoutLeakingTransportIntoCore()
    {
        var appProject = Project("src", "GachaOverlay.App", "GachaOverlay.App.csproj");
        var host = File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", "GachaOverlay.App", "Lifecycle", "ApplicationHost.cs"));
        var coreProject = Project("src", "GachaOverlay.Core", "GachaOverlay.Core.csproj");

        Assert.Contains("LSOverlay.RemoteClient", appProject, StringComparison.Ordinal);
        Assert.Contains("RemoteChatProductionCoordinator", host, StringComparison.Ordinal);
        Assert.DoesNotContain("LSOverlay.RemoteClient", coreProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Discord.Net", appProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api/v1", host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscordNet_RemainsBackendOnly()
    {
        var consumers = Directory.GetFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains("Discord.Net.WebSocket", StringComparison.Ordinal))
            .ToArray();

        Assert.Single(consumers);
        Assert.EndsWith(Path.Combine("src", "LSOverlay.Backend", "LSOverlay.Backend.csproj"),
            consumers[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GatewayIntents_RemainExactAndExcludeGuildMembers()
    {
        var expected = GatewayIntents.Guilds |
            GatewayIntents.GuildMessages |
            GatewayIntents.GuildMessageReactions |
            GatewayIntents.GuildMessagePolls |
            GatewayIntents.MessageContent |
            GatewayIntents.GuildPresences;
        Assert.Equal(expected, DiscordGatewayPolicy.RequiredIntents);
        Assert.Equal(GatewayIntents.None,
            DiscordGatewayPolicy.RequiredIntents & GatewayIntents.GuildMembers);
    }

    [Fact]
    public void BackendContainsTargetedMemberLookupButNoFullMemberDownload()
    {
        var sources = SourceTree("src", "LSOverlay.Backend");
        Assert.Contains("GetGuildUserAsync", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadUsersAsync", sources, StringComparison.Ordinal);
    }

    [Fact]
    public void M93ExposesTransportNeutralChannelCatalogAndMessageContract()
    {
        var protocol = SourceTree("src", "LSOverlay.Protocol");
        Assert.Contains("ChatChannelCatalog", protocol, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ChatMessageCreate", protocol, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ChatMessageUpdate", protocol, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ChatMessageDelete", protocol, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SocketMessage", protocol, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HelperPreventsProbeBotTokenInheritanceAndCleansEnvironment()
    {
        var helper = File.ReadAllText(Path.Combine(
            RepositoryRoot, "tools", "dev", "run-ls-m92-local.ps1"));
        var clearIndex = helper.IndexOf(
            "SetEnvironmentVariable($tokenName, $null, 'Process')",
            StringComparison.Ordinal);
        var probeIndex = helper.IndexOf("& dotnet run --project $probeProject", StringComparison.Ordinal);

        Assert.Contains("-AsSecureString", helper, StringComparison.Ordinal);
        Assert.Contains("finally", helper, StringComparison.Ordinal);
        Assert.True(clearIndex >= 0 && clearIndex < probeIndex);
        Assert.DoesNotMatch(new Regex(@"(?<!\d)\d{17,20}(?!\d)"), helper);
        var probeSource = SourceTree("src", "LSOverlay.TransportProbe");
        Assert.DoesNotContain("LSO_DISCORD_BOT_TOKEN", probeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCertificateValidationBypassExists()
    {
        var sources = SourceTree("src", "LSOverlay.Backend") +
            SourceTree("src", "LSOverlay.RemoteClient");
        Assert.DoesNotContain("RemoteCertificateValidationCallback", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("DangerousAcceptAnyServerCertificateValidator", sources, StringComparison.Ordinal);
    }

    private static string Project(params string[] path) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot }.Concat(path).ToArray()));

    private static string SourceTree(params string[] path)
    {
        var root = Path.Combine(new[] { RepositoryRoot }.Concat(path).ToArray());
        return string.Join(Environment.NewLine,
            Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
    }
}

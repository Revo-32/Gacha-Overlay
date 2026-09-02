using LSOverlay.Backend.Events;
using System.Text.RegularExpressions;

namespace GachaOverlay.Tests.Backend;

public sealed class BackendSecurityBoundaryTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Fact]
    public void NormalizedSignals_DoNotExposeContentRawPayloadOrSecrets()
    {
        var signalTypes = new[]
        {
            typeof(BackendMessageSignal),
            typeof(BackendReactionSignal),
            typeof(TrackedHostPresenceSnapshot),
        };

        var names = signalTypes
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(names, name => name.Contains("Content", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReactionIdentity_PreservesCustomIdAndUnicodeName()
    {
        var signal = ReactionIdentityNormalizer.Create(
            BackendReactionOperation.Add,
            1,
            2,
            3,
            4,
            5,
            "판매완료",
            DateTimeOffset.UnixEpoch);

        Assert.Equal((ulong)5, signal.EmojiId);
        Assert.Equal("판매완료", signal.EmojiName);
        Assert.Equal(BackendReactionOperation.Add, signal.Operation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReactionIdentity_PreservesEveryObservedOperation(int operationValue)
    {
        var operation = (BackendReactionOperation)operationValue;
        var signal = ReactionIdentityNormalizer.Create(
            operation,
            1,
            2,
            3,
            null,
            null,
            null,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(operation, signal.Operation);
    }

    [Fact]
    public void BackendPresenceCode_HasNoFreeFormParserOrSecretsAccess()
    {
        var sourceDirectory = Path.Combine(RepositoryRoot, "src", "LSOverlay.Backend");
        var sources = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        var combined = string.Join(Environment.NewLine, sources);

        Assert.DoesNotContain("Regex", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(".Secrets", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SalesStateEngine", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsAppComposition_HasNoBackendReferenceOrLaunchCode()
    {
        var project = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "GachaOverlay.App.csproj"));
        var applicationHost = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Lifecycle",
            "ApplicationHost.cs"));

        Assert.DoesNotContain("LSOverlay.Backend", project, StringComparison.Ordinal);
        Assert.DoesNotContain("LSOverlay.Backend", applicationHost, StringComparison.Ordinal);
        Assert.DoesNotContain("LSO_DISCORD_BOT_TOKEN", applicationHost, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendProject_HasNoWindowsUiOrAppDependency()
    {
        var project = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "LSOverlay.Backend",
            "LSOverlay.Backend.csproj"));

        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("net8.0-windows", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseWPF", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GachaOverlay.App", project, StringComparison.Ordinal);
        Assert.DoesNotContain("GachaOverlay.Infrastructure", project, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscordNetPackage_IsReferencedOnlyByBackendProject()
    {
        var projectFiles = Directory.GetFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var consumers = projectFiles
            .Where(path => File.ReadAllText(path).Contains(
                "Discord.Net.WebSocket",
                StringComparison.Ordinal))
            .ToArray();

        var consumer = Assert.Single(consumers);
        Assert.EndsWith(
            Path.Combine("src", "LSOverlay.Backend", "LSOverlay.Backend.csproj"),
            consumer,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalHelper_UsesSecurePromptAndAlwaysClearsProcessToken()
    {
        var helper = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tools",
            "dev",
            "run-ls-backend-local.ps1"));

        Assert.Contains("-AsSecureString", helper, StringComparison.Ordinal);
        Assert.Contains("LSO_DISCORD_BOT_TOKEN", helper, StringComparison.Ordinal);
        Assert.Contains("$PSBoundParameters.ContainsKey('TrackedHostIds')", helper, StringComparison.Ordinal);
        Assert.Contains(
            "$TrackedHostIds = Read-Host 'Tracked Host Discord User ID(s) [optional]'",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetEnvironmentVariable($hostsName, $TrackedHostIds, 'Process')",
            helper,
            StringComparison.Ordinal);
        Assert.Contains("finally", helper, StringComparison.Ordinal);
        Assert.Contains("SetEnvironmentVariable($tokenName, $null, 'Process')", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host $plainToken", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content", helper, StringComparison.OrdinalIgnoreCase);
        var dotnetRunLine = helper
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("& dotnet run", StringComparison.Ordinal));
        Assert.DoesNotContain("Token", dotnetRunLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"(?<!\d)\d{17,20}(?!\d)"), helper);
    }
}

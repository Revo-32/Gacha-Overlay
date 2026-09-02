using System.Text.RegularExpressions;

namespace GachaOverlay.Tests.Backend;

public sealed class M941ValidationHelperTests
{
    private const string RequiredInvocation =
        "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \".\\tools\\dev\\run-ls-m94-local.ps1\"";

    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string HelperPath = Path.Combine(
        RepositoryRoot,
        "tools",
        "dev",
        "run-ls-m94-local.ps1");

    private static readonly string DocumentationPath = Path.Combine(
        RepositoryRoot,
        "docs",
        "architecture",
        "M9.4.1-user-actual-validation-helper.md");

    [Fact]
    public void HelperAndDocumentation_ContainOneShotExecutionPolicyBypassInvocation()
    {
        var helper = File.ReadAllText(HelperPath);
        var documentation = File.ReadAllText(DocumentationPath);

        Assert.Contains(RequiredInvocation, helper, StringComparison.Ordinal);
        Assert.Contains(RequiredInvocation, documentation, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-ExecutionPolicy", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Set-ExecutionPolicy",
            documentation,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Helper_LaunchesReleaseBackendAndRealProductionStyleWpfOnly()
    {
        var helper = File.ReadAllText(HelperPath);

        Assert.Contains("LSOverlay.Backend.csproj", helper, StringComparison.Ordinal);
        Assert.Contains("GachaOverlay.App.csproj", helper, StringComparison.Ordinal);
        Assert.Contains("win-x64-singlefile.pubxml", helper, StringComparison.Ordinal);
        Assert.Contains("'publish'", helper, StringComparison.Ordinal);
        Assert.Contains("'/healthz'", helper, StringComparison.Ordinal);
        Assert.Contains("Start-WpfApplication", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("LSOverlay.TransportProbe", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("TransportProbe", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Helper_BotTokenIsSecurePromptedAndNeverPlacedInChildArguments()
    {
        var helper = File.ReadAllText(HelperPath);
        var backendStart = Slice(
            helper,
            "$backendProcess = Start-Process",
            "# Security boundary");

        Assert.Contains("Read-Host 'Discord Bot Token' -AsSecureString", helper, StringComparison.Ordinal);
        Assert.Contains("LSO_DISCORD_BOT_TOKEN", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("$plainToken", backendStart, StringComparison.Ordinal);
        Assert.DoesNotContain("$tokenName", backendStart, StringComparison.Ordinal);
        Assert.DoesNotContain("LSO_DISCORD_BOT_TOKEN", backendStart, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host $plainToken", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"(?<!\d)\d{17,20}(?!\d)"), helper);
    }

    [Fact]
    public void Helper_ClearsBackendEnvironmentBeforeEveryWpfLaunch()
    {
        var helper = File.ReadAllText(HelperPath);
        var clearBoundary = helper.IndexOf(
            "# Security boundary",
            StringComparison.Ordinal);
        var clearEnvironment = helper.IndexOf(
            "Clear-BackendEnvironment",
            clearBoundary,
            StringComparison.Ordinal);
        var clearToken = helper.IndexOf(
            "Clear-TokenMaterial",
            clearBoundary,
            StringComparison.Ordinal);
        var wpfLaunch = helper.IndexOf(
            "$activeWpfProcess = Start-WpfApplication",
            clearBoundary,
            StringComparison.Ordinal);

        Assert.True(clearBoundary >= 0);
        Assert.InRange(clearEnvironment, clearBoundary + 1, wpfLaunch - 1);
        Assert.InRange(clearToken, clearEnvironment + 1, wpfLaunch - 1);
        Assert.Contains(
            "GetEnvironmentVariable($tokenName, 'Process')",
            helper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Helper_PreservesTemporaryBackendStateAcrossWpfRelaunch()
    {
        var helper = File.ReadAllText(HelperPath);
        var validationLoop = Slice(
            helper,
            "$keepRunning = $true",
            "finally {");

        Assert.Contains("R = Relaunch WPF", validationLoop, StringComparison.Ordinal);
        Assert.Contains("$activeWpfProcess = Start-WpfApplication", validationLoop, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-IsolatedStateDirectory", validationLoop, StringComparison.Ordinal);
        Assert.Contains(
            "Remove-IsolatedStateDirectory -Path $stateDirectory",
            helper[helper.LastIndexOf("finally {", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void Helper_CleanupTargetsOnlyOwnedProcessesAndBoundedTemporaryState()
    {
        var helper = File.ReadAllText(HelperPath);

        Assert.Contains("Stop-HelperWpfProcess -Process $activeWpfProcess", helper, StringComparison.Ordinal);
        Assert.Contains("Stop-HelperBackendProcess -Process $backendProcess", helper, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::WriteAllText($shutdownFile, 'stop')", helper, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id $Process.Id -Force", helper, StringComparison.Ordinal);
        Assert.Contains("$temporaryRoot", helper, StringComparison.Ordinal);
        Assert.Contains("$leaf.StartsWith($statePrefix", helper, StringComparison.Ordinal);
        Assert.Contains("'LSOverlay-M94-'", helper, StringComparison.Ordinal);
        Assert.Contains("'LSOverlay-M95-'", helper, StringComparison.Ordinal);
        Assert.Contains("Clear-BackendEnvironment", helper, StringComparison.Ordinal);
        Assert.Contains("Clear-TokenMaterial", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Process | Stop-Process", helper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Helper_DetectsOccupiedEndpointBeforePromptAndPreservesFailureLogs()
    {
        var helper = File.ReadAllText(HelperPath);
        var endpointCheck = helper.IndexOf(
            "Assert-BackendEndpointAvailable -Url $BackendUrl",
            StringComparison.Ordinal);
        var tokenPrompt = helper.IndexOf(
            "Read-Host 'Discord Bot Token' -AsSecureString",
            StringComparison.Ordinal);
        var backendStart = helper.IndexOf(
            "$backendProcess = Start-Process",
            StringComparison.Ordinal);

        Assert.True(endpointCheck >= 0);
        Assert.InRange(endpointCheck, 0, tokenPrompt - 1);
        Assert.InRange(tokenPrompt, endpointCheck + 1, backendStart - 1);
        Assert.Contains("LSOverlay-M94-LastFailure", helper, StringComparison.Ordinal);
        Assert.Contains("Preserve-BackendFailureLogs", helper, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $source", helper, StringComparison.Ordinal);
        Assert.Contains("Backend logs preserved at $preservedLogs", helper, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}

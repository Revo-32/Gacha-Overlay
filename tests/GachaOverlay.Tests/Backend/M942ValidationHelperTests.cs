using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GachaOverlay.Tests.Backend;

public sealed class M942ValidationHelperTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string HelperPath = Path.Combine(
        RepositoryRoot,
        "tools",
        "dev",
        "run-ls-m94-local.ps1");

    [Fact]
    public void Helper_WpfPublishArguments_PreserveSingleProjectAndProfileArguments_WithSpaces()
    {
        var helper = File.ReadAllText(HelperPath);
        var argumentConstruction = Slice(
            helper,
            "$wpfPublishArguments = @(",
            "Invoke-CheckedDotNet -Arguments $wpfPublishArguments");

        Assert.Single(
            Regex.Matches(argumentConstruction, @"(?<!\w)\$wpfProject(?!\w)")
                .Cast<Match>());
        Assert.Contains(
            "('-p:PublishProfile=' + $publishProfile)",
            argumentConstruction,
            StringComparison.Ordinal);

        const string project = @"E:\Repository With Spaces\src\GachaOverlay.App\GachaOverlay.App.csproj";
        const string profile = @"E:\Repository With Spaces\src\GachaOverlay.App\Properties\PublishProfiles\win-x64-singlefile.pubxml";
        const string output = @"C:\Temporary Output\wpf-release";
        var command = string.Join(
            Environment.NewLine,
            "$wpfProject = '" + project + "'",
            "$publishProfile = '" + profile + "'",
            "$wpfOutput = '" + output + "'",
            argumentConstruction,
            "[Console]::Out.Write(($wpfPublishArguments | ConvertTo-Json -Compress))");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"PowerShell argument evaluation failed: {standardError}");
        var arguments = JsonSerializer.Deserialize<string[]>(standardOutput);
        Assert.NotNull(arguments);
        Assert.Equal(
            new[]
            {
                "publish",
                project,
                "-c",
                "Release",
                "--no-restore",
                "-p:PublishProfile=" + profile,
                "-o",
                output,
            },
            arguments);
        Assert.DoesNotContain("-p:PublishProfile=", arguments);
        Assert.DoesNotContain(profile, arguments);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}

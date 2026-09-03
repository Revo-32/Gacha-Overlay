using System.Diagnostics;

namespace GachaOverlay.Tests.Backend;

public sealed class M9131DockerBuildGraphTests : IDisposable
{
    private static readonly string Repository = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string Verifier = Path.Combine(Repository, "tools", "dev", "verify-backend-docker-context.ps1");
    private readonly string _fixture = Path.Combine(Path.GetTempPath(), $"LSOverlay-M9131-Test-{Guid.NewGuid():N}");

    [Fact]
    public async Task CurrentBackendGraphIncludesEveryEvaluatedGitEligibleSource()
    {
        var result = await VerifyAsync(Repository);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("\"SourceClosure\": \"PASS\"", result.Output);
    }

    [Fact]
    public async Task DeletedTrackedSourceIsExcludedFromPendingBuildContext()
    {
        await CreateFixtureAsync();
        Write("src/SyntheticLeaf/Retired.cs", "public class Retired { }");
        var add = await RunAsync("git", _fixture, new[] { "add", "src/SyntheticLeaf/Retired.cs" });
        Assert.Equal(0, add.ExitCode);
        File.Delete(Path.Combine(_fixture, "src/SyntheticLeaf/Retired.cs"));
        var result = await VerifyAsync(_fixture);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task TransitiveProjectAndNewSourceSubdirectoryAreDiscoveredWithoutAFileAllowlist()
    {
        await CreateFixtureAsync();
        Write("src/SyntheticLeaf/Future/Subdirectory/NewSource.cs", "public class FutureSource { }");
        var result = await VerifyAsync(_fixture);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("SyntheticLeaf.csproj", result.Output);
    }

    [Theory]
    [InlineData("git", "absent from Git candidates")]
    [InlineData("dockerignore", "excluded by .dockerignore")]
    [InlineData("copy", "Complete project-level COPY missing")]
    [InlineData("broad-parent", "outside Backend closure")]
    [InlineData("windows", "Windows/WPF project")]
    public async Task MissingOrUnsafeSourceClosureFailsBeforePublish(string defect, string expected)
    {
        await CreateFixtureAsync();
        Write("src/SyntheticLeaf/Ignored-Code/NewSource.cs", "public class AdditionalSource { }");
        switch (defect)
        {
            case "git":
                File.AppendAllText(Path.Combine(_fixture, ".gitignore"), "ignored-code/\n");
                break;
            case "dockerignore":
                File.AppendAllText(Path.Combine(_fixture, ".dockerignore"), "**/Ignored-Code/**\n");
                break;
            case "copy":
                var docker = File.ReadAllText(Path.Combine(_fixture, "Dockerfile"));
                Write("Dockerfile", docker.Replace("COPY src/SyntheticLeaf/ src/SyntheticLeaf/\n", "", StringComparison.Ordinal));
                break;
            case "broad-parent":
                File.AppendAllText(Path.Combine(_fixture, ".dockerignore"), "!src/\n");
                Write("src/UnrelatedDesktop/Program.cs", "public class MustStayOutsideContainer { }");
                break;
            case "windows":
                var project = File.ReadAllText(Path.Combine(_fixture, "src/SyntheticLeaf/SyntheticLeaf.csproj"));
                Write("src/SyntheticLeaf/SyntheticLeaf.csproj", project.Replace("net8.0", "net8.0-windows", StringComparison.Ordinal));
                break;
        }
        var result = await VerifyAsync(_fixture);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expected, result.Output);
    }

    [Fact]
    public async Task GitRuntimeOutputExclusionDoesNotHideProjectSourceTrees()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Repository, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Repository, "tests"), "*.cs", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(Repository, path).Replace('\\', '/'))
            .Where(path => !path.Split('/').Any(part => part is "bin" or "obj"))
            .ToArray();
        var result = await RunAsync("git", Repository,
            new[] { "check-ignore", "--no-index", "--stdin" }, string.Join('\n', files) + "\n");
        Assert.True(result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Output), result.Output);

        var runtime = await RunAsync("git", Repository,
            new[] { "check-ignore", "--no-index", "diagnostics/synthetic.json" });
        Assert.Equal(0, runtime.ExitCode);
    }

    private async Task CreateFixtureAsync()
    {
        Directory.CreateDirectory(_fixture);
        Assert.Equal(0, (await RunAsync("git", _fixture, new[] { "init", "--quiet" })).ExitCode);
        Assert.Equal(0, (await RunAsync("git", _fixture, new[] { "config", "core.ignorecase", "true" })).ExitCode);
        Write(".gitignore", "bin/\nobj/\n");
        Write("Directory.Build.props", "<Project />");
        var projects = new[] { "LSOverlay.Backend", "SyntheticMiddle", "SyntheticLeaf" };
        Write(".dockerignore", "**\n!Dockerfile\n!.dockerignore\n!Directory.Build.props\n" +
            string.Join('\n', projects.Select(project => $"!src/{project}/**")) + "\n**/bin/**\n**/obj/**\n");
        var recipe = "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\nWORKDIR /src\nCOPY Directory.Build.props ./\n";
        for (var index = 0; index < projects.Length; index++)
        {
            var project = projects[index];
            var reference = index + 1 < projects.Length
                ? $"<ItemGroup><ProjectReference Include=\"../{projects[index + 1]}/{projects[index + 1]}.csproj\" /></ItemGroup>"
                : "";
            Write($"src/{project}/{project}.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>" + reference + "</Project>");
            Write($"src/{project}/Source.cs", $"namespace {project.Replace('.', '_')}; public class Source {{ }}");
            recipe += $"COPY src/{project}/{project}.csproj src/{project}/\n";
        }
        recipe += "RUN dotnet restore src/LSOverlay.Backend/LSOverlay.Backend.csproj\n";
        foreach (var project in projects) recipe += $"COPY src/{project}/ src/{project}/\n";
        recipe += "RUN dotnet publish src/LSOverlay.Backend/LSOverlay.Backend.csproj -c Release --no-restore --self-contained false -p:UseAppHost=false -o /app/publish\n";
        Write("Dockerfile", recipe);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_fixture, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static Task<(int ExitCode, string Output)> VerifyAsync(string root) => RunAsync("pwsh", root,
        new[] { "-NoProfile", "-File", Verifier, "-RepositoryRoot", root, "-CheckOnly" });

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string executable, string directory, IEnumerable<string> arguments, string? input = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input is not null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }

    public void Dispose()
    {
        if (Directory.Exists(_fixture))
        {
            // git add creates read-only object files on Windows. This GUID
            // directory contains only this test's synthetic repository.
            foreach (var file in Directory.EnumerateFiles(_fixture, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(_fixture, recursive: true);
        }
    }
}

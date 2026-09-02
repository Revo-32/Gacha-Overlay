using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Infrastructure.Lifecycle;
using GachaOverlay.Infrastructure.Sales;

namespace GachaOverlay.Tests.Release;

public sealed class M911RuntimeUxTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Fact]
    public void BuiltInProductCatalogGuildScope_MatchesFixedProductionGuild()
    {
        var catalog = EmbeddedSalesProductCatalogLoader.Load();

        Assert.NotEmpty(catalog.Products);
        Assert.All(
            catalog.Products,
            product => Assert.Equal(ProductionServerProfile.GuildId, product.GuildId));
    }

    [Fact]
    public void WindowsAutoStart_EnablesDisablesAndSelfHealsCurrentProcessPath()
    {
        var store = new FakeAutoStartStore();
        var path = @"C:\Portable\Gacha Overlay.exe";
        var service = new WindowsAutoStartService(store, () => path);

        Assert.True(service.Apply(enabled: true));
        Assert.Equal($"\"{path}\"", store.Value);
        Assert.True(service.IsCurrentRegistration());
        store.Value = "\"C:\\Old\\GachaOverlay.exe\"";
        Assert.False(service.IsCurrentRegistration());
        Assert.True(service.Apply(enabled: true));
        Assert.True(service.IsCurrentRegistration());
        Assert.True(service.Apply(enabled: false));
        Assert.Null(store.Value);
    }

    [Fact]
    public void WindowsAutoStartFailure_IsRecoverable()
    {
        var service = new WindowsAutoStartService(
            new FakeAutoStartStore { ThrowOnWrite = true },
            () => @"C:\Portable\GachaOverlay.exe");

        Assert.False(service.Apply(enabled: true));
    }

    [Fact]
    public void ProductionSource_HasNoSilentDiscordKillOrAccessibilityLaunch()
    {
        var source = string.Join('\n', Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        Assert.DoesNotContain(".Kill(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Kill", source, StringComparison.Ordinal);
        Assert.DoesNotContain("force-renderer-accessibility", source, StringComparison.Ordinal);
    }

    private sealed class FakeAutoStartStore : IWindowsAutoStartStore
    {
        public string? Value { get; set; }

        public bool ThrowOnWrite { get; init; }

        public string? Read(string valueName) => Value;

        public void Write(string valueName, string command)
        {
            if (ThrowOnWrite)
            {
                throw new UnauthorizedAccessException();
            }

            Value = command;
        }

        public void Delete(string valueName) => Value = null;
    }
}

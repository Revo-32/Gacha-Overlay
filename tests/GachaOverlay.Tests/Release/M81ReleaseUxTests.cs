using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Discord.Connection;
using GachaOverlay.Infrastructure.Discord.Normalization;
using GachaOverlay.Infrastructure.Discord.Process;
using GachaOverlay.Infrastructure.Lifecycle;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Sales;
using GachaOverlay.Infrastructure.Settings;

namespace GachaOverlay.Tests.Release;

public sealed class M81ReleaseUxTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Fact]
    public void ProductionSettingsCategories_AreExactlyTenInRequiredOrder()
    {
        Assert.Equal(
            [
                SettingsCategory.General,
                SettingsCategory.Discord,
                SettingsCategory.Server,
                SettingsCategory.Hud,
                SettingsCategory.Chat,
                SettingsCategory.Media,
                SettingsCategory.Sales,
                SettingsCategory.Hotkeys,
                SettingsCategory.Diagnostics,
                SettingsCategory.Developer,
            ],
            Enum.GetValues<SettingsCategory>());
    }

    [Theory]
    [InlineData("en", "Server", "Server Settings")]
    [InlineData("ko", "서버", "서버 설정")]
    [InlineData("ja", "サーバー", "サーバー設定")]
    public void ServerAndOnboardingStrings_AreLocalized(
        string locale,
        string category,
        string title)
    {
        var localization = new ResourceLocalizationService(locale);
        Assert.Equal(category, localization["SettingsCategoryServer"]);
        Assert.Equal(title, localization["SettingsServerTitle"]);
        Assert.NotEqual("OnboardingTitle", localization["OnboardingTitle"]);
        Assert.NotEqual("SettingsWindowsAutoStart", localization["SettingsWindowsAutoStart"]);
        Assert.NotEqual("SettingsDiscordAutoLaunch", localization["SettingsDiscordAutoLaunch"]);
    }

    [Fact]
    public void NormalSettingsUi_HasMainSelectorButNoEditableGuildOrSalesIds()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Presentation",
            "FoundationWindow.xaml"));
        Assert.Contains("ServerSettings.MainChannels", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding DiscordGuildIdText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding DiscordSalesChannelIdText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding DiscordMainChannelIdText", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_HasSixGuidedStepsAndNoSeparateCredentialStore()
    {
        Assert.Equal(6, OnboardingViewModel.StepCount);
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Presentation",
            "OnboardingViewModel.cs"));
        Assert.Contains("FoundationViewModel Settings", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CredentialStore", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_AreEnglishIncompleteOnboardingAndStartupOptionsOff()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Equal("en", settings.Language);
        Assert.Equal(0, settings.OnboardingVersion);
        Assert.False(settings.WindowsAutoStart);
        Assert.False(settings.DiscordAutoLaunch);
        Assert.Equal(ProductionServerProfile.GuildId, settings.DiscordGuildId);
        Assert.Equal(ProductionServerProfile.SalesChannelId, settings.DiscordSalesChannelId);
        Assert.Null(settings.DiscordMainChannelId);
    }

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
    public void SchemaTenMigration_PreservesMainCredentialsAndOtherSettingsButFixesGuildRoles()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 10,
              "language": "ko",
              "discordClientId": "123456",
              "discordGuildId": "legacy-guild",
              "discordMainChannelId": "main-kept",
              "discordSalesChannelId": "legacy-sales",
              "chatShowTime": false,
              "futureField": "kept"
            }
            """);

        var first = new JsonSettingsStore(path).Load();
        var second = new JsonSettingsStore(path).Load();
        Assert.Equal(11, first.SchemaVersion);
        Assert.Equal(ProductionServerProfile.GuildId, first.DiscordGuildId);
        Assert.Equal("main-kept", first.DiscordMainChannelId);
        Assert.Equal(ProductionServerProfile.SalesChannelId, first.DiscordSalesChannelId);
        Assert.Equal("123456", first.DiscordClientId);
        Assert.False(first.ChatShowTime);
        Assert.True(first.ExtensionData!.ContainsKey("futureField"));
        Assert.Equal(first.SchemaVersion, second.SchemaVersion);
        Assert.Equal(first.DiscordMainChannelId, second.DiscordMainChannelId);
        Assert.Equal(first.DiscordClientId, second.DiscordClientId);
        Assert.Equal(first.OnboardingVersion, second.OnboardingVersion);
        Assert.True(second.ExtensionData!.ContainsKey("futureField"));
    }

    [Fact]
    public void CurrentSchema_SelfHealsFixedGuildAndSalesValuesOnDisk()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 11,
              "discordGuildId": "manually-overridden-guild",
              "discordMainChannelId": "main-kept",
              "discordSalesChannelId": "manually-overridden-sales"
            }
            """);

        var loaded = new JsonSettingsStore(path).Load();
        var persisted = File.ReadAllText(path);

        Assert.Equal(ProductionServerProfile.GuildId, loaded.DiscordGuildId);
        Assert.Equal("main-kept", loaded.DiscordMainChannelId);
        Assert.Equal(ProductionServerProfile.SalesChannelId, loaded.DiscordSalesChannelId);
        Assert.Contains(ProductionServerProfile.GuildId, persisted, StringComparison.Ordinal);
        Assert.Contains(ProductionServerProfile.SalesChannelId, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("manually-overridden-guild", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("manually-overridden-sales", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerCategoryAndScrollPosition_RoundTrip()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var store = new JsonSettingsStore(path);
        store.Load();

        Assert.True(store.Save(store.Current with
        {
            LastSettingsCategory = SettingsCategory.Server,
            SettingsCategoryScrollPositions = new Dictionary<string, double>
            {
                [SettingsCategory.Server.ToString()] = 164.5,
            },
        }));

        var loaded = new JsonSettingsStore(path).Load();
        Assert.Equal(SettingsCategory.Server, loaded.LastSettingsCategory);
        Assert.Equal(164.5, loaded.SettingsCategoryScrollPositions["Server"]);
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
    public async Task DiscordAutoLaunch_IsOffByDefaultAndAttemptsAtMostOnceWhenEnabled()
    {
        var offProcess = new LaunchCountingProcessService();
        var off = CreateWaitingCoordinator(offProcess, autoLaunch: false);
        off.Start(CancellationToken.None);
        await WaitUntilAsync(() => off.Status.Detail == "DiscordNotRunning");
        Assert.Equal(0, offProcess.LaunchCount);
        await off.DisposeAsync();

        var onProcess = new LaunchCountingProcessService();
        var on = CreateWaitingCoordinator(onProcess, autoLaunch: true);
        on.Start(CancellationToken.None);
        await WaitUntilAsync(() => on.Status.Detail == "DiscordNotRunning");
        Assert.Equal(1, onProcess.LaunchCount);
        await on.DisposeAsync();
    }

    [Fact]
    public async Task DiscordAutoLaunch_RunningDiscordIsNotDuplicatedAndFailureIsRecoverable()
    {
        var runningProcess = new LaunchCountingProcessService { IsRunning = true };
        var running = CreateWaitingCoordinator(
            runningProcess,
            autoLaunch: true,
            new GachaOverlay.Tests.Discord.Connection.FakeRpcClient());
        running.Start(CancellationToken.None);
        await WaitUntilAsync(() => running.Status.State == DiscordConnectionState.Connected);
        Assert.Equal(0, runningProcess.LaunchCount);
        await running.DisposeAsync();

        var failingProcess = new LaunchCountingProcessService { LaunchResult = false };
        var failing = CreateWaitingCoordinator(failingProcess, autoLaunch: true);
        failing.Start(CancellationToken.None);
        await WaitUntilAsync(() => failing.Status.Detail == "DiscordAutoLaunchFailed");
        Assert.Equal(1, failingProcess.LaunchCount);
        Assert.Equal(DiscordConnectionState.Disconnected, failing.Status.State);
        await failing.DisposeAsync();
    }

    [Fact]
    public void ProductionSource_HasNoSilentDiscordKill()
    {
        var source = string.Join('\n', Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain(".Kill(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Kill", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessibilityGuidance_UsesOnboardingOwnerAndHasOwnerlessFallback()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GachaOverlay.App",
            "Lifecycle",
            "ApplicationHost.cs"));

        Assert.Contains(
            "_onboardingWindow is { IsVisible: true }",
            source,
            StringComparison.Ordinal);
        Assert.Contains("if (owner is null)", source, StringComparison.Ordinal);
        Assert.Contains("ShowSetupMessage(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MessageBox.Show(\r\n                SettingsOwnerWindow,\r\n                _localization[\"SettingsDiscordAccessibility",
            source,
            StringComparison.Ordinal);
    }

    private static DiscordConnectionCoordinator CreateWaitingCoordinator(
        IDiscordProcessService process,
        bool autoLaunch,
        GachaOverlay.Tests.Discord.Connection.FakeRpcClient? client = null) =>
        new(
            process,
            new GachaOverlay.Tests.Discord.Connection.FakeCredentialProvider(),
            client is null
                ? new GachaOverlay.Tests.Discord.Connection.FakeRpcClientFactory()
                : new GachaOverlay.Tests.Discord.Connection.FakeRpcClientFactory(client),
            new GachaOverlay.Tests.Discord.Connection.FakeAuthenticationService(),
            new GachaOverlay.Tests.Discord.Connection.FakeChannelResolver(),
            new DiscordMessageNormalizer(NullAppLogger.Instance),
            new GachaOverlay.Core.Discord.Messages.DiscordMessagePipeline(),
            new DiscordTargetOptions(),
            new ImmediateReconnectDelayStrategy(),
            NullAppLogger.Instance,
            discordAutoLaunchEnabledProvider: () => autoLaunch);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException();
            }

            await Task.Delay(10);
        }
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

    private sealed class LaunchCountingProcessService : IDiscordProcessService
    {
        public int LaunchCount { get; private set; }

        public bool IsRunning { get; init; }

        public bool LaunchResult { get; init; } = true;

        public bool IsDiscordRunning() => IsRunning;

        public Task WaitUntilDiscordIsRunningAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public bool TryLaunchDiscord(bool accessibilityMode = false)
        {
            LaunchCount++;
            return LaunchResult;
        }
    }

    private sealed class ImmediateReconnectDelayStrategy : IReconnectDelayStrategy
    {
        public Task DelayAsync(int consecutiveFailureCount, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"gacha-m81-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_path);

        public string File(string name) => Path.Combine(_path, name);

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}

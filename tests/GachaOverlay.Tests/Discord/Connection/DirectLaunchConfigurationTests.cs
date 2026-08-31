using System.Text;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Hud;
using GachaOverlay.Infrastructure.Discord.Authentication;
using GachaOverlay.Infrastructure.Discord.Channels;
using GachaOverlay.Infrastructure.Discord.Connection;
using GachaOverlay.Infrastructure.Discord.Normalization;
using GachaOverlay.Infrastructure.Discord.Rpc;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Discord.Connection;

public sealed class DirectLaunchConfigurationTests
{
    [Fact]
    public void CredentialsAbsent_ReturnsConfigurationRequiredWithoutThrowing()
    {
        using var directory = new TemporaryDirectory();
        var settings = new JsonSettingsStore(directory.File("settings.json"));
        settings.Load();
        var protectedStore = new FakeProtectedCredentialStore();
        var provider = new ConfiguredDiscordCredentialProvider(
            settings,
            protectedStore,
            new EmptyCredentialProvider());

        var found = provider.TryGetCredentials(out var credentials);

        Assert.False(found);
        Assert.Null(credentials);
        Assert.Equal(ProtectedCredentialStatus.Missing, protectedStore.ClientSecretStatus);
    }

    [Fact]
    public async Task MissingConfiguration_CanBeCompletedAndReconnectedWithoutRestartingApp()
    {
        using var directory = new TemporaryDirectory();
        var settings = new JsonSettingsStore(directory.File("settings.json"));
        settings.Load();
        var protectedStore = new FakeProtectedCredentialStore();
        var provider = new ConfiguredDiscordCredentialProvider(
            settings,
            protectedStore,
            new EmptyCredentialProvider());
        var coordinator = CreateCoordinator(provider, new FakeRpcClient(), () => new DiscordTargetOptions());
        coordinator.Start(CancellationToken.None);

        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.ConfigurationRequired);
        Assert.Equal("CredentialsMissing", coordinator.Status.Detail);
        Assert.Equal(0, coordinator.Status.Generation);

        settings.Update(current => current with
        {
            DiscordClientId = "123456789",
            DiscordRedirectUri = "https://127.0.0.1",
        });
        protectedStore.SaveClientSecret("protected-secret");
        coordinator.RequestReconnect();

        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);
        Assert.Equal("LiveAndBootstrapped", coordinator.Status.Detail);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ReturningLaunch_ReusesSavedNonSecretTargetIds()
    {
        using var directory = new TemporaryDirectory();
        var settings = new JsonSettingsStore(directory.File("settings.json"));
        settings.Load();
        settings.Update(current => current with
        {
            DiscordClientId = "123456789",
            DiscordRedirectUri = "https://127.0.0.1",
            DiscordGuildId = "111",
            DiscordMainChannelId = "222",
            DiscordSalesChannelId = "333",
        });
        var protectedStore = new FakeProtectedCredentialStore { ClientSecret = "secret" };
        var provider = new ConfiguredDiscordCredentialProvider(
            settings,
            protectedStore,
            new EmptyCredentialProvider());
        var resolver = new CapturingChannelResolver();
        var coordinator = CreateCoordinator(
            provider,
            new FakeRpcClient(),
            () => new DiscordTargetOptions
            {
                GuildId = settings.Current.DiscordGuildId,
                MainChannelId = settings.Current.DiscordMainChannelId,
                SalesChannelId = settings.Current.DiscordSalesChannelId,
            },
            resolver);
        coordinator.Start(CancellationToken.None);

        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        Assert.Equal(ProductionServerProfile.GuildId, resolver.Observed?.GuildId);
        Assert.Equal("222", resolver.Observed?.MainChannelId);
        Assert.Equal(ProductionServerProfile.SalesChannelId, resolver.Observed?.SalesChannelId);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public void EnvironmentCredentials_RemainSupportedForVerificationLauncher()
    {
        using var directory = new TemporaryDirectory();
        var settings = new JsonSettingsStore(directory.File("settings.json"));
        settings.Load();
        var environment = new InlineCredentialProvider(
            new DiscordCredentials("launcher-client", "launcher-secret", "https://launcher"));
        var provider = new ConfiguredDiscordCredentialProvider(
            settings,
            new FakeProtectedCredentialStore(),
            environment);

        Assert.True(provider.TryGetCredentials(out var credentials));
        Assert.Equal("launcher-client", credentials?.ClientId);
    }

    [Fact]
    public void DpapiStore_RoundTripsCurrentUserSecretsWithoutPlainTextFiles()
    {
        using var directory = new TemporaryDirectory();
        var secretPath = directory.File("client-secret.dat");
        var tokenPath = directory.File("oauth-token.dat");
        var writer = new DpapiDiscordProtectedCredentialStore(
            secretPath,
            tokenPath,
            NullAppLogger.Instance);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        Assert.True(writer.SaveClientSecret("super-secret-value"));
        Assert.True(writer.SaveOAuthToken(new DiscordOAuthToken(
            "access-token-value",
            "refresh-token-value",
            expiresAt)));

        var secretBytes = Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(secretPath));
        var tokenBytes = Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(tokenPath));
        Assert.DoesNotContain("super-secret-value", secretBytes, StringComparison.Ordinal);
        Assert.DoesNotContain("access-token-value", tokenBytes, StringComparison.Ordinal);

        var reader = new DpapiDiscordProtectedCredentialStore(
            secretPath,
            tokenPath,
            NullAppLogger.Instance);
        Assert.True(reader.TryLoadClientSecret(out var secret));
        Assert.True(reader.TryLoadOAuthToken(out var token));
        Assert.Equal("super-secret-value", secret);
        Assert.Equal("access-token-value", token?.AccessToken);
        Assert.Equal("refresh-token-value", token?.RefreshToken);
        Assert.Equal(ProtectedCredentialStatus.Available, reader.ClientSecretStatus);
        Assert.Equal(ProtectedCredentialStatus.Available, reader.OAuthTokenStatus);
    }

    [Fact]
    public void ProtectedCredentialLoadFailure_RequestsSafeSetupInsteadOfCrashing()
    {
        using var directory = new TemporaryDirectory();
        var settings = new JsonSettingsStore(directory.File("settings.json"));
        settings.Load();
        settings.Update(current => current with { DiscordClientId = "123456789" });
        var protectedStore = new FakeProtectedCredentialStore
        {
            ClientSecret = "unreadable",
            FailSecretRead = true,
            FailTokenRead = true,
        };
        var provider = new ConfiguredDiscordCredentialProvider(
            settings,
            protectedStore,
            new EmptyCredentialProvider());

        var exception = Record.Exception(() => provider.TryGetCredentials(out _));

        Assert.Null(exception);
        Assert.False(provider.TryGetCredentials(out _));
        Assert.Equal(ProtectedCredentialStatus.Unreadable, protectedStore.ClientSecretStatus);
        Assert.Equal(ProtectedCredentialStatus.Unreadable, protectedStore.OAuthTokenStatus);
    }

    [Fact]
    public void MissingCredentials_VisibilityIntentCannotBypassInitialConnectionGate()
    {
        var hud = new HudStateService(HudVisibilityMode.Always);

        hud.SetUserHudEnabled(false);
        hud.SetUserHudEnabled(true);

        Assert.True(hud.Current.UserHudEnabled);
        Assert.False(hud.Current.HasInitialConnectionReady);
        Assert.False(hud.Current.EffectiveVisible);
    }

    [Fact]
    public void SuccessfulBootstrap_OpensGateAndHonorsAlwaysVisibility()
    {
        var hud = new HudStateService(HudVisibilityMode.Always);

        hud.SetRpcConnected(true);

        Assert.True(hud.Current.HasInitialConnectionReady);
        Assert.True(hud.Current.UserHudEnabled);
        Assert.True(hud.Current.EffectiveVisible);
    }

    private static DiscordConnectionCoordinator CreateCoordinator(
        IDiscordCredentialProvider credentialProvider,
        FakeRpcClient rpcClient,
        Func<DiscordTargetOptions> targetOptions,
        IDiscordChannelResolver? resolver = null) =>
        new(
            new AlwaysRunningDiscordProcessService(),
            credentialProvider,
            new FakeRpcClientFactory(rpcClient),
            new FakeAuthenticationService(),
            resolver ?? new FakeChannelResolver(),
            new DiscordMessageNormalizer(NullAppLogger.Instance),
            new DiscordMessagePipeline(),
            targetOptions,
            new ImmediateReconnectDelayStrategy(),
            NullAppLogger.Instance);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class EmptyCredentialProvider : IDiscordCredentialProvider
    {
        public bool TryGetCredentials(out DiscordCredentials? credentials)
        {
            credentials = null;
            return false;
        }
    }

    private sealed class InlineCredentialProvider(DiscordCredentials value)
        : IDiscordCredentialProvider
    {
        public bool TryGetCredentials(out DiscordCredentials? credentials)
        {
            credentials = value;
            return true;
        }
    }

    private sealed class CapturingChannelResolver : IDiscordChannelResolver
    {
        public DiscordTargetOptions? Observed { get; private set; }

        public Task<DiscordTargetChannels> ResolveAsync(
            IDiscordRpcClient rpcClient,
            DiscordTargetOptions options,
            CancellationToken cancellationToken)
        {
            Observed = options;
            return Task.FromResult(FakeChannelResolver.Targets);
        }
    }
}

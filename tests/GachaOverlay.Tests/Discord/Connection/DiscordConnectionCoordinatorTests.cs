using System.Collections.Concurrent;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Connection;
using GachaOverlay.Infrastructure.Discord.Normalization;
using GachaOverlay.Infrastructure.Discord.Authentication;
using GachaOverlay.Infrastructure.Discord.Channels;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Tests.Discord.Connection;

public sealed class DiscordConnectionCoordinatorTests
{
    [Fact]
    public async Task DiscordNotRunning_RemainsAliveAndStopsCleanlyOnCancellation()
    {
        var coordinator = new DiscordConnectionCoordinator(
            new NeverRunningDiscordProcessService(),
            new FakeCredentialProvider(),
            new FakeRpcClientFactory(),
            new FakeAuthenticationService(),
            new FakeChannelResolver(),
            new DiscordMessageNormalizer(NullAppLogger.Instance),
            new DiscordMessagePipeline(),
            new DiscordTargetOptions(),
            new ImmediateReconnectDelayStrategy(),
            NullAppLogger.Instance);
        coordinator.Start(CancellationToken.None);

        await WaitUntilAsync(() => coordinator.Status.Detail == "DiscordNotRunning");
        Assert.Equal(DiscordConnectionState.Disconnected, coordinator.Status.State);

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Disconnect_TransitionsThroughReconnectToConnected()
    {
        var first = new FakeRpcClient();
        var second = new FakeRpcClient();
        var fixture = CreateCoordinator(
            new FakeRpcClientFactory(first, second),
            new ImmediateReconnectDelayStrategy());
        var states = new ConcurrentQueue<DiscordConnectionState>();
        fixture.Coordinator.StatusChanged += status => states.Enqueue(status.State);
        fixture.Coordinator.Start(CancellationToken.None);

        await WaitUntilAsync(() => fixture.Coordinator.Status is
        {
            State: DiscordConnectionState.Connected,
            Generation: 1,
        });
        first.Disconnect(new IOException("simulated disconnect"));
        await WaitUntilAsync(() => fixture.Coordinator.Status is
        {
            State: DiscordConnectionState.Connected,
            Generation: 2,
        });

        Assert.Contains(DiscordConnectionState.Reconnecting, states);
        await fixture.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_RepeatsAuthenticationResolutionSubscriptionsAndBootstrap()
    {
        var first = new FakeRpcClient();
        var second = new FakeRpcClient();
        var factory = new FakeRpcClientFactory(first, second);
        var fixture = CreateCoordinator(factory, new ImmediateReconnectDelayStrategy());
        fixture.Coordinator.Start(CancellationToken.None);

        await WaitUntilAsync(() => fixture.Coordinator.Status.State == DiscordConnectionState.Connected);
        first.Disconnect(new IOException("simulated disconnect"));
        await WaitUntilAsync(() => fixture.Coordinator.Status is
        {
            State: DiscordConnectionState.Connected,
            Generation: 2,
        });

        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(2, fixture.Authentication.AuthenticationCount);
        Assert.Equal(2, fixture.Resolver.ResolutionCount);
        Assert.Equal(6, first.SubscriptionCount);
        Assert.Equal(6, second.SubscriptionCount);
        Assert.Equal(2, first.GetChannelCount);
        Assert.Equal(2, second.GetChannelCount);
        Assert.Equal(2, fixture.Coordinator.MessageState.Generation);
        await fixture.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_StopsReconnectDelayAndWorker()
    {
        var failing = new FakeRpcClient
        {
            ConnectException = new IOException("simulated connect failure"),
        };
        var delay = new BlockingReconnectDelayStrategy();
        var fixture = CreateCoordinator(new FakeRpcClientFactory(failing), delay);
        fixture.Coordinator.Start(CancellationToken.None);

        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await delay.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(DiscordConnectionState.Disconnected, fixture.Coordinator.Status.State);
    }

    [Fact]
    public async Task InvalidSavedTarget_PausesForUserConfigurationWithoutReconnectLoop()
    {
        var factory = new FakeRpcClientFactory(new FakeRpcClient());
        var delay = new ImmediateReconnectDelayStrategy();
        var coordinator = new DiscordConnectionCoordinator(
            new AlwaysRunningDiscordProcessService(),
            new FakeCredentialProvider(),
            factory,
            new FakeAuthenticationService(),
            new InvalidTargetResolver(),
            new DiscordMessageNormalizer(NullAppLogger.Instance),
            new DiscordMessagePipeline(),
            new DiscordTargetOptions { GuildId = "stale" },
            delay,
            NullAppLogger.Instance);
        coordinator.Start(CancellationToken.None);

        await WaitUntilAsync(() => coordinator.Status.Detail == "TargetConfigurationInvalid");
        await Task.Delay(50);

        Assert.Equal(DiscordConnectionState.ConfigurationRequired, coordinator.Status.State);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, delay.CallCount);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task AuthenticationFailure_PausesForExplicitReauthentication()
    {
        var coordinator = new DiscordConnectionCoordinator(
            new AlwaysRunningDiscordProcessService(),
            new FakeCredentialProvider(),
            new FakeRpcClientFactory(new FakeRpcClient()),
            new AuthenticationRequiredService(),
            new FakeChannelResolver(),
            new DiscordMessageNormalizer(NullAppLogger.Instance),
            new DiscordMessagePipeline(),
            new DiscordTargetOptions(),
            new ImmediateReconnectDelayStrategy(),
            NullAppLogger.Instance);
        coordinator.Start(CancellationToken.None);

        await WaitUntilAsync(() => coordinator.Status.Detail == "AuthenticationRequired");

        Assert.Equal(DiscordConnectionState.ConfigurationRequired, coordinator.Status.State);
        await coordinator.DisposeAsync();
    }

    private static CoordinatorFixture CreateCoordinator(
        FakeRpcClientFactory factory,
        IReconnectDelayStrategy reconnectDelay)
    {
        var authentication = new FakeAuthenticationService();
        var resolver = new FakeChannelResolver();
        var pipeline = new DiscordMessagePipeline();
        var coordinator = new DiscordConnectionCoordinator(
            new AlwaysRunningDiscordProcessService(),
            new FakeCredentialProvider(),
            factory,
            authentication,
            resolver,
            new DiscordMessageNormalizer(NullAppLogger.Instance),
            pipeline,
            new DiscordTargetOptions(),
            reconnectDelay,
            NullAppLogger.Instance);
        return new CoordinatorFixture(coordinator, authentication, resolver);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected coordinator state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed record CoordinatorFixture(
        DiscordConnectionCoordinator Coordinator,
        FakeAuthenticationService Authentication,
        FakeChannelResolver Resolver);

    private sealed class InvalidTargetResolver : IDiscordChannelResolver
    {
        public Task<DiscordTargetChannels> ResolveAsync(
            IDiscordRpcClient rpcClient,
            DiscordTargetOptions options,
            CancellationToken cancellationToken) =>
            Task.FromException<DiscordTargetChannels>(
                new DiscordChannelResolutionException("stale target"));
    }

    private sealed class AuthenticationRequiredService : IDiscordAuthenticationService
    {
        public Task<DiscordAuthenticationResult> AuthenticateAsync(
            IDiscordRpcClient rpcClient,
            DiscordCredentials credentials,
            CancellationToken cancellationToken) =>
            Task.FromException<DiscordAuthenticationResult>(
                new DiscordAuthenticationRequiredException("reauthenticate"));
    }
}

using Discord.WebSocket;
using LSOverlay.Backend;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Events;
using LSOverlay.Backend.Presence;
using LSOverlay.Backend.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GachaOverlay.Tests.Backend;

public sealed class BackendShutdownTests
{
    [Fact]
    public async Task ProcessExitState_RemainsReadableAfterRunAsyncOwnedHostDisposal()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<BackendConnectionHealth>();
        var host = builder.Build();
        var exitState = BackendProcessExitState.Capture(host);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(started.SetResult);

        var run = host.RunAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        lifetime.StopApplication();
        await run;

        Assert.Equal(0, exitState.ExitCode);
        Assert.Throws<ObjectDisposedException>(() =>
            host.Services.GetRequiredService<BackendConnectionHealth>());
    }

    [Fact]
    public void ProcessExitState_PreservesFaultExitCodeAfterHostDisposal()
    {
        var host = Program.CreateHost(Configuration());
        var health = host.Services.GetRequiredService<BackendConnectionHealth>();
        var exitState = BackendProcessExitState.Capture(host);
        health.Transition(
            BackendConnectionHealthState.Faulted,
            BackendConnectionHealthReason.UnexpectedFailure);

        host.Dispose();

        Assert.Equal(1, exitState.ExitCode);
    }

    [Fact]
    public async Task Worker_IntentionalStopIsCleanAndFinalMetricsRemainReadable()
    {
        var gateway = new FakeGatewayLifecycle();
        var health = new BackendConnectionHealth();
        var metrics = new BackendMetrics();
        metrics.Increment(BackendMetric.MessageCreate);
        using var worker = Worker(gateway, health, metrics);

        await worker.StartAsync(CancellationToken.None);
        await gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, gateway.StopCount);
        Assert.Equal(BackendConnectionHealthState.Stopped, health.Current.State);
        Assert.Equal(BackendConnectionHealthReason.GracefulShutdown, health.Current.Reason);
        Assert.False(health.HasFaulted);
        Assert.Equal(1, metrics.Snapshot().MessageCreate);
    }

    [Fact]
    public async Task Worker_OverlappingStopSignalsRemainIdempotent()
    {
        var gateway = new FakeGatewayLifecycle();
        var health = new BackendConnectionHealth();
        using var worker = Worker(gateway, health, new BackendMetrics());
        await worker.StartAsync(CancellationToken.None);
        await gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await Task.WhenAll(
            worker.StopAsync(CancellationToken.None),
            worker.StopAsync(CancellationToken.None));

        Assert.Equal(1, gateway.StopCount);
        Assert.False(health.HasFaulted);
    }

    [Fact]
    public async Task GatewayStop_IsSharedAndClosesCallbackGateBeforeSdkTeardown()
    {
        using var client = new DiscordSocketClient(
            DiscordGatewayPolicy.CreateSocketConfiguration());
        var adapter = Adapter(client);

        var first = adapter.StopAsync();
        var second = adapter.StopAsync();

        Assert.Same(first, second);
        Assert.True(adapter.IsStopping);
        Assert.False(adapter.CanProcessCallbacks);
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task CallbackDrainGate_RejectsLateCallbacksAndWaitsForEnteredCallback()
    {
        var gate = new BackendCallbackDrainGate();
        Assert.True(gate.TryEnter());

        var drained = gate.CloseAsync();

        Assert.False(drained.IsCompleted);
        Assert.False(gate.IsAccepting);
        Assert.False(gate.TryEnter());
        gate.Exit();
        await drained;
        Assert.True(drained.IsCompletedSuccessfully);
    }

    private static DiscordBackendWorker Worker(
        IDiscordGatewayLifecycle gateway,
        BackendConnectionHealth health,
        BackendMetrics metrics) => new(
            gateway,
            Configuration(),
            health,
            metrics,
            NullLogger<DiscordBackendWorker>.Instance);

    private static DiscordGatewayAdapter Adapter(DiscordSocketClient client)
    {
        var configuration = Configuration();
        return new DiscordGatewayAdapter(
            client,
            configuration,
            new TargetGuildFilter(configuration.TargetGuildId),
            new BackendEventJournal(1),
            new BackendMetrics(),
            new BackendConnectionHealth(),
            new TrackedHostPresenceStore(configuration.SessionHostIds),
            new GtaPresenceNormalizer(),
            NullLogger<DiscordGatewayAdapter>.Instance);
    }

    private static BackendConfiguration Configuration() => new(
        new BackendBotCredential("synthetic-token"),
        123,
        new ulong[] { 456 });

    private sealed class FakeGatewayLifecycle : IDiscordGatewayLifecycle
    {
        private int _stopCount;

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount => Volatile.Read(ref _stopCount);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stopCount);
            return Task.CompletedTask;
        }
    }
}

using System.Reflection;
using Discord.WebSocket;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Events;
using LSOverlay.Backend.Presence;
using LSOverlay.Backend.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace GachaOverlay.Tests.Backend;

public sealed class GatewayReconnectReadinessTests
{
    [Theory]
    [InlineData("Ready")]
    [InlineData("TargetGuildUnavailable")]
    [InlineData("Faulted")]
    [InlineData("Stopped")]
    public async Task LateConnectedCallbackPreservesDefinitiveState(string state)
    {
        using var client = new DiscordSocketClient(DiscordGatewayPolicy.CreateSocketConfiguration());
        var health = new BackendConnectionHealth();
        var adapter = CreateAdapter(client, health);
        health.Transition(Enum.Parse<BackendConnectionHealthState>(state), BackendConnectionHealthReason.None);
        var before = health.Current;
        var notifications = 0;
        health.Changed += _ => notifications++;

        await Connected(adapter);

        Assert.Same(before, health.Current);
        Assert.Equal(0, notifications);
    }

    [Theory]
    [InlineData("Starting")]
    [InlineData("Connecting")]
    [InlineData("Disconnected")]
    public async Task SocketConnectionAloneDoesNotImplyGuildReadiness(string state)
    {
        using var client = new DiscordSocketClient(DiscordGatewayPolicy.CreateSocketConfiguration());
        var health = new BackendConnectionHealth();
        var adapter = CreateAdapter(client, health);
        health.Transition(Enum.Parse<BackendConnectionHealthState>(state), BackendConnectionHealthReason.None);

        await Connected(adapter);

        Assert.Equal(BackendConnectionHealthState.Connecting, health.Current.State);
        Assert.Equal(BackendConnectionHealthReason.GatewayConnecting, health.Current.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatedRecoveryWorksWithEitherCallbackOrderAndRealDisconnectStillWins(bool guildFirst)
    {
        using var client = new DiscordSocketClient(DiscordGatewayPolicy.CreateSocketConfiguration());
        var health = new BackendConnectionHealth();
        var adapter = CreateAdapter(client, health);
        for (var cycle = 0; cycle < 5; cycle++)
        {
            await Invoke(adapter, "OnDisconnectedAsync", new IOException("synthetic-disconnect"));
            Assert.Equal(BackendConnectionHealthState.Disconnected, health.Current.State);
            if (!guildFirst) await Connected(adapter);
            health.Transition(BackendConnectionHealthState.Ready, BackendConnectionHealthReason.GatewayReady);
            if (guildFirst) await Connected(adapter);
            Assert.Equal(BackendConnectionHealthState.Ready, health.Current.State);
            Assert.Equal(BackendConnectionHealthReason.GatewayReady, health.Current.Reason);
        }

        health.Transition(BackendConnectionHealthState.TargetGuildUnavailable, BackendConnectionHealthReason.TargetGuildMissing);
        await Connected(adapter);
        Assert.Equal(BackendConnectionHealthState.TargetGuildUnavailable, health.Current.State);
        await adapter.StopAsync();
        var stopped = health.Current;
        await Connected(adapter);
        Assert.Same(stopped, health.Current);
    }

    private static DiscordGatewayAdapter CreateAdapter(DiscordSocketClient client, BackendConnectionHealth health) => new(
        client, new BackendConfiguration(new BackendBotCredential("synthetic-bot"), 123, Array.Empty<ulong>()),
        new TargetGuildFilter(123), new BackendEventJournal(1), new BackendMetrics(), health,
        new TrackedHostPresenceStore(Array.Empty<ulong>()), new GtaPresenceNormalizer(),
        NullLogger<DiscordGatewayAdapter>.Instance);

    private static Task Connected(DiscordGatewayAdapter adapter) => Invoke(adapter, "OnConnectedAsync");

    private static Task Invoke(DiscordGatewayAdapter adapter, string method, params object[] args) =>
        (Task)typeof(DiscordGatewayAdapter).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(adapter, args)!;
}

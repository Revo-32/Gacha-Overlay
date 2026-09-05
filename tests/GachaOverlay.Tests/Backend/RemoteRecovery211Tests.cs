using System.Threading.Channels;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Providers;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Diagnostics;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace GachaOverlay.Tests.Backend;

public sealed partial class M94ProductionRemoteModeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery211_DelayedDomains_BlockLegacyButNotIndependentChat(bool independent)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        { RemoteSelectedChannelId = "100", SalesTrackingEnabled = true });
        var fake = new Independent211Client(independent);
        var ingress = new DiscordMessagePipeline();
        var metrics = new RuntimeMetricsCollector();
        await using var coordinator = new RemoteChatProductionCoordinator(store,
            new MemoryCredentialStore("token"), ingress, Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance, _ => fake, metrics: metrics);
        coordinator.Start();
        await WaitUntilAsync(() => ingress.Current.MainChat.Count == 20);
        // These cancellable sources represent a 60-second blocked domain request.
        // We never wait 60 real seconds: the old path cannot advance until released.
        if (!independent)
        {
            await Task.Delay(150);
            Assert.False(fake.StreamStarted);
            Assert.False(fake.SalesReleased.Task.IsCompleted);
            Assert.False(fake.PresenceReleased.Task.IsCompleted);
            fake.Release();
        }
        await WaitUntilAsync(() => fake.StreamStarted);
        fake.PublishCreate(Message(26, 100, "after reconnect"));
        await WaitUntilAsync(() => ingress.Current.MainChat.Any(m => m.MessageId == "26"));
        Assert.Equal(20, ingress.Current.MainChat.Count);
        if (independent)
        {
            Assert.False(fake.SalesReleased.Task.IsCompleted);
            Assert.False(fake.PresenceReleased.Task.IsCompleted);
            Assert.Equal(ManualSalesResyncResult.Coalesced, coordinator.RequestSalesResync());
            fake.Release();
            await WaitUntilAsync(() => fake.SalesSubscriptions == 1);
            Assert.Equal(ManualSalesResyncResult.Requested, coordinator.RequestSalesResync());
            await WaitUntilAsync(() => fake.SalesSubscriptions == 2);
            Assert.Equal(1, fake.StreamStarts);
            Assert.True(metrics.Snapshot().Durations.ContainsKey("remote.chat.recovery.duration"));
            Assert.Equal(1, metrics.Snapshot().Counters["remote.session.connected"]);
            fake.PublishUpdate(Message(26, 100, "updated"));
            fake.PublishDelete(26, 100);
            await WaitUntilAsync(() => ingress.Current.MainChat.All(m => m.MessageId != "26"));
        }
    }

    [Fact]
    public async Task Recovery211_TwentyReconnects_DelayedSalesCannotHoldChatOrRetainSubscribers()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        { RemoteSelectedChannelId = "100", SalesTrackingEnabled = true });
        var clients = new List<Independent211Client>();
        var ingress = new DiscordMessagePipeline();
        await using var coordinator = new RemoteChatProductionCoordinator(store,
            new MemoryCredentialStore("token"), ingress, Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance, _ =>
            {
                var fake = new Independent211Client(true);
                clients.Add(fake);
                return fake;
            });
        coordinator.Start();
        for (var cycle = 0; cycle < 20; cycle++)
        {
            await WaitUntilAsync(() => clients.Count == cycle + 1 && clients[^1].StreamStarted);
            var current = clients[^1];
            current.PublishCreate(Message(26, 100, "live"));
            await WaitUntilAsync(() => ingress.Current.MainChat.Any(m => m.MessageId == "26"));
            Assert.Equal(20, ingress.Current.MainChat.Count);
            Assert.False(current.SalesReleased.Task.IsCompleted);
            foreach (var old in clients.Take(cycle))
            {
                Assert.True(old.Disposed);
                Assert.Equal(0, old.SubscriberCount);
                old.PublishCreate(Message(999, 100, "stale"));
                old.EmitStaleSales();
            }
            Assert.DoesNotContain(ingress.Current.MainChat, m => m.MessageId == "999");
            if (cycle < 19) await coordinator.RefreshAsync();
        }
        await coordinator.DisposeAsync();
        Assert.All(clients, c => { Assert.True(c.Disposed); Assert.Equal(0, c.SubscriberCount); Assert.Equal(0, c.ActiveStreams); });
    }

    private sealed class Independent211Client : FakeRemoteClient, ILSOverlayIndependentSessionClient, ILSOverlayRemoteSalesClient
    {
        public TaskCompletionSource<SalesBootstrapResponse> SalesReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<BootstrapResponse> PresenceReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StreamStarts { get; private set; }
        public int ActiveStreams { get; private set; }
        public int SalesSubscriptions { get; private set; }
        public int SalesSubscribers => SalesReady?.GetInvocationList().Length ?? 0;
        private Action<SalesBootstrapResponse>? _lateSales;
        public event Action<SalesBootstrapResponse>? SalesReady;
        public event Action<SalesMutationEnvelope>? SalesMutationReceived { add { } remove { } }
        public event Action<string>? SalesStreamStatusChanged { add { } remove { } }
        public Independent211Client(bool independent) : base(messageCount: 25)
        {
            Capabilities = independent ? new[] { OverlayTransportProtocol.SessionStart } : null;
            PresenceOverride = token => PresenceReleased.Task.WaitAsync(token);
        }
        public void Release()
        {
            SalesReleased.TrySetResult(new SalesBootstrapResponse(1,
                new ChatChannelDescriptor(10, 300, "sales", 1, false), "sales", 0,
                Array.Empty<ChatMessage>(), Array.Empty<SalesCompletionObservation>(), SalesBootstrapCoverage.Complete));
            PresenceReleased.TrySetResult(new BootstrapResponse(1, "presence", 0, 7, Array.Empty<HostPresenceSnapshot>()));
        }
        public void EmitStaleSales() => _lateSales?.Invoke(new SalesBootstrapResponse(1,
            new ChatChannelDescriptor(10, 300, "sales", 1, false), "stale", 99,
            Array.Empty<ChatMessage>(), Array.Empty<SalesCompletionObservation>(), SalesBootstrapCoverage.Complete));
        public Task<SalesBootstrapResponse> GetSalesBootstrapAsync(string token, CancellationToken cancellationToken = default) => SalesReleased.Task.WaitAsync(cancellationToken);
        public Task<SalesStatusActionResponse> SetSalesStatusAsync(string token, SalesStatusActionRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task StreamChatAndSalesAsync(string token, BootstrapResponse presence, ChatBootstrapResponse chat,
            SalesBootstrapResponse sales, ChannelReader<ChatBootstrapResponse> switches,
            ChannelReader<SalesBootstrapResponse> resyncs, CancellationToken cancellationToken = default) =>
            StreamChatAsync(token, presence, chat, switches, cancellationToken);
        public async Task StreamIndependentAsync(string token, ChatBootstrapResponse chat,
            ChannelReader<ChatBootstrapResponse> switches, ChannelReader<SalesBootstrapResponse> resyncs,
            Action<BootstrapResponse> presenceReady, Action connected, CancellationToken cancellationToken)
        {
            StreamStarts++;
            _lateSales = SalesReady;
            ActiveStreams++;
            connected();
            // No HTTP presence request is needed by the new handshake.
            var stream = StreamChatAsync(token, new BootstrapResponse(1, "presence", 0, 7,
                Array.Empty<HostPresenceSnapshot>()), chat, switches, cancellationToken);
            try
            {
                await foreach (var sales in resyncs.ReadAllAsync(cancellationToken))
                {
                    SalesSubscriptions++;
                    SalesReady?.Invoke(sales);
                }
            }
            finally
            {
                try { await stream; } catch (OperationCanceledException) { }
                ActiveStreams--;
            }
        }
    }
}

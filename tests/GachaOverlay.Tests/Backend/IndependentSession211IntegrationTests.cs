using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;
using Microsoft.Extensions.DependencyInjection;
using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Tests.Backend;

public sealed partial class M93KestrelChatIntegrationTests
{
    [Fact]
    public async Task IndependentSession211_RealSocket_ReconcilesReplayAndCleansTwentySessions()
    {
        await using var fixture = await ChatFixture.StartAsync();
        var token = Assert.IsType<string>(fixture.Credentials.Issue(Guid.NewGuid(), 456, 123).AccessToken);
        var timings = new List<double>();
        for (var cycle = 0; cycle < 20; cycle++)
        {
            await using var client = new LSOverlayRemoteClient(fixture.BaseUri);
            var catalog = await client.GetChatChannelsAsync(token);
            Assert.Contains(OverlayTransportProtocol.SessionStart, catalog.Capabilities!);
            // Capture HTTP snapshot, then change the canonical stream before subscribing.
            // This is the bootstrap/live race; the server journal must reconcile it.
            var chat = await client.GetChatBootstrapAsync(token, 789);
            var ingress = new DiscordMessagePipeline();
            using var adapter = new RemoteChatIngressAdapter(ingress, client, cycle + 1, "456");
            Assert.True(adapter.ApplyBootstrap(chat));
            var id = (ulong)(1000 + cycle);
            fixture.Streams.PublishUpsert(OverlayTransportProtocol.ChatMessageCreate, Message(id, 789));
            fixture.Streams.PublishUpsert(OverlayTransportProtocol.ChatMessageUpdate, Message(id, 789) with { Content = "updated" });
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var events = new ConcurrentQueue<ChatMutationEnvelope>();
            BootstrapResponse? presence = null;
            long connected = 0;
            client.ChatChannelReady += _ => ready.TrySetResult();
            client.ChatMutationReceived += events.Enqueue;
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var stream = client.StreamIndependentAsync(token, chat,
                Channel.CreateUnbounded<ChatBootstrapResponse>().Reader,
                Channel.CreateUnbounded<SalesBootstrapResponse>().Reader,
                snapshot => presence = snapshot, () => connected = Stopwatch.GetTimestamp(), cancel.Token);
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                timings.Add(Stopwatch.GetElapsedTime(connected).TotalMilliseconds);
                await Wait211Async(() => events.Count == 2);
                Assert.Equal(new[] { OverlayTransportProtocol.ChatMessageCreate, OverlayTransportProtocol.ChatMessageUpdate },
                    events.Select(e => e.EventType));
                Assert.Equal("updated", events.Last().Message!.Content);
                Assert.Equal("updated", Assert.Single(ingress.Current.MainChat.Where(m => m.MessageId == id.ToString())).Content);
                Assert.NotNull(presence);
                Assert.Equal(456UL, presence.SelfDiscordUserId);
                // No Sales bootstrap/subscription and no HTTP Presence bootstrap were issued.
                fixture.Streams.PublishUpsert(OverlayTransportProtocol.ChatMessageCreate, Message(id + 100, 789));
                await Wait211Async(() => events.Count == 3);
                Assert.InRange(ingress.Current.MainChat.Count, 1, 20);
                fixture.Streams.PublishDelete(789, id);
                await Wait211Async(() => events.Count == 4);
                Assert.DoesNotContain(ingress.Current.MainChat, m => m.MessageId == id.ToString());
                Assert.Equal(1, fixture.Services.GetRequiredService<RemoteConnectionLimiter>().Active);
                Assert.Equal(1, fixture.Services.GetRequiredService<RemotePublicationHub>().ActiveSubscriptions);
            }
            finally
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
            }
            await Wait211Async(() => fixture.Services.GetRequiredService<RemoteConnectionLimiter>().Active == 0 &&
                fixture.Services.GetRequiredService<RemotePublicationHub>().ActiveSubscriptions == 0);
        }
        var output = Environment.GetEnvironmentVariable("LSO_RECOVERY_PROFILE_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
            File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { cycles = 20, connectedToChatReadyMs = timings }));
    }

    private static async Task Wait211Async(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

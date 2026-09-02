using LSOverlay.Backend.Events;
using LSOverlay.Backend.Presence;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;
using Microsoft.AspNetCore.Http;

namespace GachaOverlay.Tests.Backend;

public sealed class M92TransportStateTests
{
    private static readonly DateTimeOffset Now = new(
        2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bootstrap_PreservesAuthenticatedSelfAndStructuredElevenOfThirtyTwo()
    {
        var store = Store();
        var hub = new RemotePublicationHub(store, generation: "runtime-a");
        var changed = Presence(11, 32);
        Assert.True(store.TryUpdate(changed, out var published));
        hub.Publish(published!);

        var bootstrap = hub.CaptureBootstrap(Identity());

        Assert.Equal(1, bootstrap.ProtocolVersion);
        Assert.Equal("runtime-a", bootstrap.Generation);
        Assert.Equal(1, bootstrap.LatestSequence);
        Assert.Equal((ulong)456, bootstrap.SelfDiscordUserId);
        var host = Assert.Single(bootstrap.TrackedHosts);
        Assert.Equal(HostPresenceState.GtaOnline, host.State);
        Assert.Equal(11, host.CurrentPlayers);
        Assert.Equal(32, host.MaximumPlayers);
        Assert.Equal(1, host.HostSlot);
    }

    [Fact]
    public async Task Resume_ReplaysAfterSequenceThenQueuesPostCutoffExactlyOnce()
    {
        var hub = new RemotePublicationHub(Store(), generation: "runtime-a");
        hub.Publish(Presence(11, 32));
        var resume = hub.PrepareResume("runtime-a", 0);
        var subscription = Assert.IsType<RemoteSubscription>(resume.Subscription);
        await using (subscription)
        {
            var replay = Assert.Single(subscription.Replay);
            Assert.Equal(1, replay.Sequence);

            hub.Publish(Presence(12, 32));
            var live = await subscription.Reader.ReadAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(2, live.Sequence);
            Assert.Equal(12, live.Payload.CurrentPlayers);
            Assert.DoesNotContain(subscription.Replay, item => item.Sequence == live.Sequence);
        }
    }

    [Fact]
    public void Resume_WrongGenerationStaleAndFutureRequireResync()
    {
        var hub = new RemotePublicationHub(Store(), journalCapacity: 2, generation: "runtime-a");
        hub.Publish(Presence(10, 32));
        hub.Publish(Presence(11, 32));
        hub.Publish(Presence(12, 32));

        Assert.Equal(ResumeDisposition.WrongGeneration,
            hub.PrepareResume("runtime-b", 3).Disposition);
        Assert.Equal(ResumeDisposition.HistoryExpired,
            hub.PrepareResume("runtime-a", 0).Disposition);
        Assert.Equal(ResumeDisposition.FutureSequence,
            hub.PrepareResume("runtime-a", 4).Disposition);
    }

    [Fact]
    public void RemoteJournal_IsBoundedMonotonicAndPresenceOnly()
    {
        var hub = new RemotePublicationHub(Store(), journalCapacity: 2, generation: "runtime-a");
        hub.Publish(Presence(10, 32));
        hub.Publish(Presence(11, 32));
        hub.Publish(Presence(12, 32));

        var journal = hub.SnapshotJournal();
        Assert.Equal(new long[] { 2, 3 }, journal.Select(item => item.Sequence));
        Assert.All(journal, item =>
            Assert.Equal(OverlayTransportProtocol.HostPresenceChanged, item.EventType));
    }

    [Fact]
    public async Task SlowSubscriber_IsRemovedWithoutBlockingPublisher()
    {
        var hub = new RemotePublicationHub(
            Store(),
            journalCapacity: 16,
            outboundCapacity: 1,
            generation: "runtime-a");
        var subscription = hub.PrepareResume("runtime-a", 0).Subscription!;

        hub.Publish(Presence(10, 32));
        hub.Publish(Presence(11, 32));

        Assert.Equal(0, hub.ActiveSubscriptions);
        await subscription.DisposeAsync();
    }

    [Fact]
    public void ConnectionLimiter_EnforcesPerInstallationAndGlobalBounds()
    {
        var limiter = new RemoteConnectionLimiter(globalLimit: 2, perInstallationLimit: 1);
        using var first = limiter.TryAcquire(Identity());
        Assert.NotNull(first);
        Assert.Null(limiter.TryAcquire(Identity()));
        using var second = limiter.TryAcquire(Identity(Guid.NewGuid()));
        Assert.NotNull(second);
        Assert.Equal(2, limiter.Active);
        Assert.Null(limiter.TryAcquire(Identity(Guid.NewGuid())));
    }

    [Fact]
    public void HttpAuthentication_AcceptsOnlyExactHeaderSchemesAndRejectsQueryCredentials()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "LSOPairing machine-secret";
        Assert.True(TransportAuthentication.TryReadPairingClaim(
            context.Request,
            out var secret));
        Assert.Equal("machine-secret", secret);

        context.Request.Headers.Authorization = "Bearer client-token";
        Assert.False(TransportAuthentication.TryReadPairingClaim(context.Request, out _));
        context.Request.QueryString = new QueryString("?access_token=client-token");
        Assert.True(TransportAuthentication.HasForbiddenCredentialQuery(context.Request));
    }

    [Fact]
    public void Heartbeat_IsControlPlaneAndDoesNotEnterJournal()
    {
        var hub = new RemotePublicationHub(Store());
        var heartbeat = new StreamServerMessage(
            1,
            OverlayTransportProtocol.Heartbeat,
            HeartbeatId: "test",
            SentAt: Now);

        Assert.Equal(OverlayTransportProtocol.Heartbeat, heartbeat.Type);
        Assert.Empty(hub.SnapshotJournal());
    }

    [Fact]
    public void AuthenticatedIdentity_CarriesOnlyInstallationUserAndGuild()
    {
        var properties = typeof(AuthenticatedClientIdentity).GetProperties();
        Assert.Equal(
            new[] { "ClientInstallationId", "DiscordUserId", "GuildId" },
            properties.Select(property => property.Name));
        Assert.DoesNotContain(properties,
            property => property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    private static TrackedHostPresenceStore Store() => new(new ulong[] { 99 }, () => Now);

    private static TrackedHostPresenceSnapshot Presence(int current, int maximum) => new(
        99,
        BackendDiscordPresenceStatus.Online,
        true,
        true,
        current,
        maximum,
        Now.AddSeconds(current));

    private static AuthenticatedClientIdentity Identity(Guid? installationId = null) => new(
        installationId ?? Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        456,
        123);
}

using System.Net;
using System.Net.WebSockets;
using System.Net.Http.Json;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Events;
using LSOverlay.Backend.Pairing;
using LSOverlay.Backend.Presence;
using LSOverlay.Backend.Runtime;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GachaOverlay.Tests.Backend;

public sealed class M92KestrelIntegrationTests
{
    [Fact]
    public async Task LoopbackKestrel_PairingBootstrapResumeAndLivePresenceWorkEndToEnd()
    {
        await using var fixture = await TransportFixture.StartAsync();
        await using var client = new LSOverlayRemoteClient(fixture.BaseUri);
        var pairing = await client.CreatePairingAsync(Guid.NewGuid());
        Assert.Equal(PairingApprovalResult.Approved,
            fixture.Pairing.Approve(123, 456, false, pairing.UserCode));
        var claim = await client.GetPairingAsync(
            pairing.PairingId,
            pairing.PairingClaimSecret);

        Assert.Equal(PairingState.Approved, claim.State);
        Assert.NotNull(claim.AccessToken);
        var bootstrap = await client.GetBootstrapAsync(claim.AccessToken);
        Assert.Equal((ulong)456, bootstrap.SelfDiscordUserId);

        var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presence = new TaskCompletionSource<HostPresenceSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.StreamLive += live.SetResult;
        client.HostPresenceChanged += value => presence.TrySetResult(value);
        using var cancellation = new CancellationTokenSource();
        var stream = client.StreamAsync(claim.AccessToken, bootstrap, cancellation.Token);
        await live.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Publication.Publish(new TrackedHostPresenceSnapshot(
            99,
            BackendDiscordPresenceStatus.Online,
            true,
            true,
            11,
            32,
            DateTimeOffset.UtcNow));
        var received = await presence.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HostPresenceState.GtaOnline, received.State);
        Assert.Equal(11, received.CurrentPlayers);
        Assert.Equal(32, received.MaximumPlayers);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream);
    }

    [Fact]
    public async Task LoopbackKestrel_RejectsQueryCredentialsAndWrongSchemes()
    {
        await using var fixture = await TransportFixture.StartAsync();
        using var http = new HttpClient { BaseAddress = fixture.BaseUri };
        using var queryToken = await http.GetAsync("api/v1/bootstrap?access_token=secret");
        using var wrongScheme = new HttpRequestMessage(HttpMethod.Get, "api/v1/bootstrap");
        wrongScheme.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "LSOPairing",
            "secret");
        using var wrongSchemeResponse = await http.SendAsync(wrongScheme);
        using var health = await http.GetAsync("healthz");
        var healthText = await health.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, queryToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSchemeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", healthText);
        Assert.DoesNotContain("Guild", healthText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoopbackKestrel_RequiresSubprotocolBeforeWebSocketAcceptance()
    {
        await using var fixture = await TransportFixture.StartAsync();
        var token = await IssueTokenAsync(fixture);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        await Assert.ThrowsAsync<WebSocketException>(() =>
            socket.ConnectAsync(fixture.StreamUri, CancellationToken.None));
    }

    [Fact]
    public async Task LoopbackKestrel_RejectsBinaryControlFrameWithControlledClose()
    {
        await using var fixture = await TransportFixture.StartAsync();
        var token = await IssueTokenAsync(fixture);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(OverlayTransportProtocol.WebSocketSubprotocol);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        await socket.ConnectAsync(fixture.StreamUri, CancellationToken.None);
        await socket.SendAsync(
            new byte[] { 1, 2, 3 },
            WebSocketMessageType.Binary,
            true,
            CancellationToken.None);

        var result = await socket.ReceiveAsync(
                new byte[128].AsMemory(),
                CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, socket.CloseStatus);
    }

    [Fact]
    public async Task LoopbackKestrel_PairingCreationRateLimitHasNoQueue()
    {
        await using var fixture = await TransportFixture.StartAsync();
        using var http = new HttpClient { BaseAddress = fixture.BaseUri };
        var responses = new List<HttpResponseMessage>();
        try
        {
            for (var index = 0; index < 6; index++)
            {
                responses.Add(await http.PostAsJsonAsync(
                    "api/v1/pairings",
                    new CreatePairingRequest(1, Guid.NewGuid()),
                    OverlayProtocolJson.Options));
            }

            Assert.All(responses.Take(5), response =>
                Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(HttpStatusCode.TooManyRequests, responses[5].StatusCode);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task LoopbackKestrel_FiveBackendRestartsRecoverTransientMembershipWithOriginalCredential()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), $"LSOverlay-M911-Restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDirectory);
        try
        {
            string token;
            await using (var original = await TransportFixture.StartAsync(stateDirectory: stateDirectory))
            {
                token = await IssueTokenAsync(original);
            }

            string? previousGeneration = null;
            for (var cycle = 0; cycle < 5; cycle++)
            {
                var requests = 0;
                var verifier = new DiscordGuildMembershipVerifier((_, _) => Task.FromResult(
                    ++requests == 1 ? GuildMembershipStatus.VerificationUnavailable : GuildMembershipStatus.Member),
                    () => DateTimeOffset.UnixEpoch);
                await using var fixture = await TransportFixture.StartAsync(verifier, stateDirectory);
                await using var client = new LSOverlayRemoteClient(fixture.BaseUri);
                var unavailable = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetBootstrapAsync(token));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
                var bootstrap = await client.GetBootstrapAsync(token);
                Assert.Equal(456UL, bootstrap.SelfDiscordUserId);
                Assert.NotEqual(previousGeneration, bootstrap.Generation);
                previousGeneration = bootstrap.Generation;
                Assert.Equal(2, requests);

                var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.StreamLive += () => live.TrySetResult();
                using var cancellation = new CancellationTokenSource();
                var stream = client.StreamAsync(token, bootstrap, cancellation.Token);
                try
                {
                    await live.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(2, requests);
                }
                finally
                {
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream);
                }
            }
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("not-member", HttpStatusCode.Forbidden)]
    [InlineData("verification-unavailable", HttpStatusCode.ServiceUnavailable)]
    public async Task M9121_PairedCredentialDoesNotBypassMembershipOrVerificationFailure(
        string outcome, HttpStatusCode expected)
    {
        var verifier = new DiscordGuildMembershipVerifier((_, _) => Task.FromResult(
            outcome == "not-member" ? GuildMembershipStatus.NotMember : GuildMembershipStatus.VerificationUnavailable),
            () => DateTimeOffset.UnixEpoch);
        await using var fixture = await TransportFixture.StartAsync(verifier);
        var token = await IssueTokenAsync(fixture);
        await using var client = new LSOverlayRemoteClient(fixture.BaseUri);

        using var http = new HttpClient { BaseAddress = fixture.BaseUri };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.GetAsync("api/v1/bootstrap");
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Forbidden)
        {
            await Assert.ThrowsAsync<RemoteAuthenticationRequiredException>(() => client.GetBootstrapAsync(token));
        }
        else
        {
            var rejected = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetBootstrapAsync(token));
            Assert.Equal(expected, rejected.StatusCode);
        }
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(OverlayTransportProtocol.WebSocketSubprotocol);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(fixture.StreamUri, default));
    }

    [Fact]
    public async Task M9121_ExistingCredentialIsRejectedWhenMembershipIsRevokedAfterLeaseExpires()
    {
        var now = DateTimeOffset.UnixEpoch;
        var status = GuildMembershipStatus.Member;
        var verifier = new DiscordGuildMembershipVerifier((_, _) => Task.FromResult(status), () => now);
        await using var fixture = await TransportFixture.StartAsync(verifier);
        var token = await IssueTokenAsync(fixture);
        await using var client = new LSOverlayRemoteClient(fixture.BaseUri);
        Assert.Equal(456UL, (await client.GetBootstrapAsync(token)).SelfDiscordUserId);

        status = GuildMembershipStatus.NotMember;
        now += DiscordGuildMembershipVerifier.CacheLifetime;

        using var http = new HttpClient { BaseAddress = fixture.BaseUri };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.GetAsync("api/v1/bootstrap");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await Assert.ThrowsAsync<RemoteAuthenticationRequiredException>(() => client.GetBootstrapAsync(token));
    }

    private static async Task<string> IssueTokenAsync(TransportFixture fixture)
    {
        await using var client = new LSOverlayRemoteClient(fixture.BaseUri);
        var pairing = await client.CreatePairingAsync(Guid.NewGuid());
        Assert.Equal(PairingApprovalResult.Approved,
            fixture.Pairing.Approve(123, 456, false, pairing.UserCode));
        var claim = await client.GetPairingAsync(pairing.PairingId, pairing.PairingClaimSecret);
        return Assert.IsType<string>(claim.AccessToken);
    }

    private sealed class TransportFixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _stateDirectory;
        private readonly bool _ownsStateDirectory;

        private TransportFixture(WebApplication app, string stateDirectory, Uri baseUri, bool ownsStateDirectory)
        {
            _app = app;
            _stateDirectory = stateDirectory;
            _ownsStateDirectory = ownsStateDirectory;
            BaseUri = baseUri;
            Pairing = app.Services.GetRequiredService<PairingService>();
            Publication = app.Services.GetRequiredService<RemotePublicationHub>();
        }

        public Uri BaseUri { get; }
        public Uri StreamUri => new UriBuilder(new Uri(BaseUri, "api/v1/stream"))
        {
            Scheme = "ws",
        }.Uri;
        public PairingService Pairing { get; }
        public RemotePublicationHub Publication { get; }

        public static async Task<TransportFixture> StartAsync(IGuildMembershipVerifier? membership = null, string? stateDirectory = null)
        {
            var ownsStateDirectory = stateDirectory is null;
            stateDirectory ??= Path.Combine(
                Path.GetTempPath(),
                $"LSOverlay-M92-Kestrel-{Guid.NewGuid():N}");
            var configuration = new BackendConfiguration(
                new BackendBotCredential("synthetic-test-token"),
                123,
                new ulong[] { 99 },
                stateDirectory,
                new Uri("http://127.0.0.1:0"));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Test",
            });
            builder.WebHost.UseUrls(configuration.ListenUri.AbsoluteUri);
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(configuration);
            var health = new BackendConnectionHealth();
            health.Transition(BackendConnectionHealthState.Ready, BackendConnectionHealthReason.GatewayReady);
            builder.Services.AddSingleton(health);
            builder.Services.AddSingleton(new TrackedHostPresenceStore(
                configuration.SessionHostIds));
            builder.Services.AddSingleton<ClientCredentialRegistry>();
            builder.Services.AddSingleton<PairingService>();
            builder.Services.AddSingleton<TransportMetrics>();
            builder.Services.AddSingleton<RemotePublicationHub>();
            builder.Services.AddSingleton<RemoteConnectionLimiter>();
            builder.Services.AddSingleton<BackendWebSocketSession>();
            builder.Services.AddSingleton<IGuildMembershipVerifier>(membership ?? new AlwaysMemberVerifier());
            builder.Services.AddTransportRateLimiting();
            var app = builder.Build();
            app.MapTransportApi();
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!;
            var baseUri = new Uri(addresses.Addresses.Single());
            return new TransportFixture(app, stateDirectory, baseUri, ownsStateDirectory);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            if (_ownsStateDirectory && Directory.Exists(_stateDirectory))
            {
                Directory.Delete(_stateDirectory, recursive: true);
            }
        }
    }

    private sealed class AlwaysMemberVerifier : IGuildMembershipVerifier
    {
        public Task<GuildMembershipStatus> VerifyAsync(
            AuthenticatedClientIdentity identity,
            CancellationToken cancellationToken) =>
            Task.FromResult(GuildMembershipStatus.Member);
    }
}

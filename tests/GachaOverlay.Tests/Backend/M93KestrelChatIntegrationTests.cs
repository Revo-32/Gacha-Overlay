using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Discord;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Presence;
using LSOverlay.Backend.Runtime;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Sales;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace GachaOverlay.Tests.Backend;

public sealed partial class M93KestrelChatIntegrationTests
{
    [Fact]
    public async Task M913_HostStopDrainsGatewayAndConnectedChatSalesPresenceWithoutLosingCredential()
    {
        await using var fixture = await ChatFixture.StartAsync(shutdownTest: true);
        await using var client = new LSOverlayRemoteClient(fixture.BaseUri);
        var claim = fixture.Credentials.Issue(Guid.NewGuid(), 456, 123);
        var token = Assert.IsType<string>(claim.AccessToken);
        var presence = await client.GetBootstrapAsync(token);
        var chat = await client.GetChatBootstrapAsync(token, 789);
        var sales = await client.GetSalesBootstrapAsync(token);
        var chatReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var salesReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ChatChannelReady += _ => chatReady.TrySetResult();
        client.SalesReady += _ => salesReady.TrySetResult();
        using var cancellation = new CancellationTokenSource();
        var stream = client.StreamChatAndSalesAsync(token, presence, chat, sales,
            System.Threading.Channels.Channel.CreateUnbounded<ChatBootstrapResponse>().Reader,
            System.Threading.Channels.Channel.CreateUnbounded<SalesBootstrapResponse>().Reader,
            cancellation.Token);
        try
        {
            await Task.WhenAll(chatReady.Task, salesReady.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Services.GetRequiredService<RemoteConnectionLimiter>().Active);
            Assert.Equal(1, fixture.Services.GetRequiredService<RemotePublicationHub>().ActiveSubscriptions);
            using var http = new HttpClient { BaseAddress = fixture.BaseUri };
            Assert.Equal("{\"status\":\"ok\"}", await http.GetStringAsync("healthz"));
            // StopApplication is the same Generic Host cancellation path used by SIGTERM.
            fixture.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, fixture.Services.GetRequiredService<RemoteConnectionLimiter>().Active);
            Assert.Equal(0, fixture.Services.GetRequiredService<RemotePublicationHub>().ActiveSubscriptions);
            Assert.Equal(1, fixture.Services.GetRequiredService<FakeGateway>().StopCount);
            Assert.False(fixture.Services.GetRequiredService<BackendConnectionHealth>().HasFaulted);
            var configuration = fixture.Services.GetRequiredService<BackendConfiguration>();
            Assert.NotNull(new ClientCredentialRegistry(configuration).Authenticate(token));
        }
        finally
        {
            cancellation.Cancel();
            var ended = await Record.ExceptionAsync(async () => await stream);
            // Host shutdown may close the transport before client cancellation arrives.
            Assert.True(ended is null or OperationCanceledException or WebSocketException,
                ended?.GetType().Name);
        }
    }

    [Fact]
    public async Task LoopbackKestrel_CatalogBootstrapSubscribeAndMutationWorkEndToEnd()
    {
        await using var fixture = await ChatFixture.StartAsync();
        await using var client = new LSOverlayRemoteClient(fixture.BaseUri);
        var claim = fixture.Credentials.Issue(Guid.NewGuid(), 456, 123);
        var token = Assert.IsType<string>(claim.AccessToken);

        var presence = await client.GetBootstrapAsync(token);
        var catalog = await client.GetChatChannelsAsync(token);
        var channel = Assert.Single(catalog.Channels.Where(item => item.Name == "main"));
        var chat = await client.GetChatBootstrapAsync(token, channel.ChannelId);
        Assert.Empty(chat.RecentMessages);

        var ready = new TaskCompletionSource<ChatBootstrapResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = new TaskCompletionSource<ChatMutationEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ChatChannelReady += value => ready.TrySetResult(value);
        client.ChatMutationReceived += value => mutation.TrySetResult(value);
        var switches = System.Threading.Channels.Channel
            .CreateUnbounded<ChatBootstrapResponse>();
        using var cancellation = new CancellationTokenSource();
        var streaming = client.StreamChatAsync(
            token,
            presence,
            chat,
            switches.Reader,
            cancellation.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Streams.PublishUpsert(
            OverlayTransportProtocol.ChatMessageCreate,
            Message(77, channel.ChannelId));
        var received = await mutation.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OverlayTransportProtocol.ChatMessageCreate, received.EventType);
        Assert.Equal(77UL, received.MessageId);
        Assert.Equal("hello", received.Message!.Content);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await streaming);
    }

    [Fact]
    public async Task LoopbackKestrel_RapidChannelSwitchCommitsLatestRequest()
    {
        await using var fixture = await ChatFixture.StartAsync();
        await using var client = new LSOverlayRemoteClient(fixture.BaseUri);
        var claim = fixture.Credentials.Issue(Guid.NewGuid(), 456, 123);
        var token = Assert.IsType<string>(claim.AccessToken);
        var presence = await client.GetBootstrapAsync(token);
        var catalog = await client.GetChatChannelsAsync(token);
        var main = await client.GetChatBootstrapAsync(token,
            catalog.Channels.Single(item => item.Name == "main").ChannelId);
        var channelC = await client.GetChatBootstrapAsync(token,
            catalog.Channels.Single(item => item.Name == "channel-c").ChannelId);
        var channelD = await client.GetChatBootstrapAsync(token,
            catalog.Channels.Single(item => item.Name == "channel-d").ChannelId);

        var readyNames = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var initialReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var latestReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ChatChannelReady += ready =>
        {
            readyNames.Enqueue(ready.Channel.Name);
            if (ready.Channel.Name == "main")
            {
                initialReady.TrySetResult();
            }

            if (ready.Channel.Name == "channel-d")
            {
                latestReady.TrySetResult();
            }
        };
        var switches = System.Threading.Channels.Channel
            .CreateUnbounded<ChatBootstrapResponse>();
        using var cancellation = new CancellationTokenSource();
        var streaming = client.StreamChatAsync(
            token,
            presence,
            main,
            switches.Reader,
            cancellation.Token);
        await initialReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await switches.Writer.WriteAsync(channelC);
        await switches.Writer.WriteAsync(channelD);
        await latestReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("channel-d", readyNames.Last());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await streaming);
    }

    [Fact]
    public async Task LoopbackKestrel_FailedSwitchPreservesOldChannel()
    {
        await using var fixture = await ChatFixture.StartAsync();
        await using var client = new LSOverlayRemoteClient(fixture.BaseUri);
        var claim = fixture.Credentials.Issue(Guid.NewGuid(), 456, 123);
        var token = Assert.IsType<string>(claim.AccessToken);
        var presence = await client.GetBootstrapAsync(token);
        var catalog = await client.GetChatChannelsAsync(token);
        var main = await client.GetChatBootstrapAsync(token,
            catalog.Channels.Single(item => item.Name == "main").ChannelId);
        var channelC = await client.GetChatBootstrapAsync(token,
            catalog.Channels.Single(item => item.Name == "channel-c").ChannelId);

        var initialReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<ChatMutationEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ChatChannelReady += ready =>
        {
            if (ready.Channel.Name == "main")
            {
                initialReady.TrySetResult();
            }
        };
        client.ChatStreamStatusChanged += (channelId, status) =>
        {
            if (channelId == channelC.Channel.ChannelId &&
                status == OverlayTransportProtocol.ChatResyncRequired)
            {
                failed.TrySetResult();
            }
        };
        client.ChatMutationReceived += value => received.TrySetResult(value);
        var switches = System.Threading.Channels.Channel
            .CreateUnbounded<ChatBootstrapResponse>();
        using var cancellation = new CancellationTokenSource();
        var streaming = client.StreamChatAsync(
            token,
            presence,
            main,
            switches.Reader,
            cancellation.Token);
        await initialReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await switches.Writer.WriteAsync(channelC with { Generation = "stale" });
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Streams.PublishUpsert(
            OverlayTransportProtocol.ChatMessageCreate,
            Message(88, main.Channel.ChannelId));
        var oldChannelEvent = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(main.Channel.ChannelId, oldChannelEvent.ChannelId);
        Assert.Equal(88UL, oldChannelEvent.MessageId);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await streaming);
    }

    [Theory]
    [InlineData("api/v1/chat/channels")]
    [InlineData("api/v1/chat/bootstrap")]
    [InlineData("api/v1/sales/bootstrap")]
    public async Task M9121_RevokedDataAccessReturns403WithoutAnAuthenticationScheme(string endpoint)
    {
        await using var fixture = await ChatFixture.StartAsync(rejectAccess: true);
        await using var client = new LSOverlayRemoteClient(fixture.BaseUri);
        var claim = fixture.Credentials.Issue(Guid.NewGuid(), 456, 123);
        using var http = new HttpClient { BaseAddress = fixture.BaseUri };
        using var request = new HttpRequestMessage(
            endpoint.EndsWith("channels", StringComparison.Ordinal) ? HttpMethod.Get : HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", claim.AccessToken);
        if (request.Method == HttpMethod.Post)
        {
            request.Content = endpoint.Contains("/sales/", StringComparison.Ordinal)
                ? JsonContent.Create(new SalesBootstrapRequest(1), options: OverlayProtocolJson.Options)
                : JsonContent.Create(new ChatBootstrapRequest(1, 789), options: OverlayProtocolJson.Options);
        }

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    private static ChatMessage Message(ulong id, ulong channelId) => new(
        id,
        123,
        channelId,
        "Default",
        0,
        new ChatAuthor(456, "member", "Member", "Member", false, false),
        "hello",
        DateTimeOffset.UtcNow,
        null,
        false,
        false,
        false,
        0,
        Array.Empty<ChatEmoji>(),
        Array.Empty<ChatAttachment>(),
        Array.Empty<ChatEmbed>(),
        Array.Empty<ChatMention>(),
        Array.Empty<ChatSticker>(),
        Array.Empty<ChatForwardSnapshot>(),
        null,
        Array.Empty<ChatComponent>(),
        null);

    private sealed class ChatFixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _stateDirectory;

        private ChatFixture(WebApplication app, string stateDirectory, Uri baseUri)
        {
            _app = app;
            _stateDirectory = stateDirectory;
            BaseUri = baseUri;
            Credentials = app.Services.GetRequiredService<ClientCredentialRegistry>();
            Streams = app.Services.GetRequiredService<ActiveChatStreamRegistry>();
        }

        public Uri BaseUri { get; }
        public ClientCredentialRegistry Credentials { get; }
        public ActiveChatStreamRegistry Streams { get; }
        public IServiceProvider Services => _app.Services;
        public Task StopAsync() => _app.StopAsync();

        public static async Task<ChatFixture> StartAsync(bool rejectAccess = false, bool shutdownTest = false)
        {
            var stateDirectory = Path.Combine(
                Path.GetTempPath(),
                $"LSOverlay-M93-Kestrel-{Guid.NewGuid():N}");
            var configuration = new BackendConfiguration(
                new BackendBotCredential("synthetic-test-token"),
                123,
                Array.Empty<ulong>(),
                stateDirectory,
                new Uri("http://127.0.0.1:0"), salesChannelId: 790);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Test",
            });
            builder.WebHost.UseUrls(configuration.ListenUri.AbsoluteUri);
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton(new TrackedHostPresenceStore(
                configuration.SessionHostIds));
            builder.Services.AddSingleton<ClientCredentialRegistry>();
            builder.Services.AddSingleton<TransportMetrics>();
            builder.Services.AddSingleton<RemotePublicationHub>();
            builder.Services.AddSingleton<RemoteConnectionLimiter>();
            builder.Services.AddSingleton<BackendWebSocketSession>();
            builder.Services.AddSingleton<IGuildMembershipVerifier, AlwaysMemberVerifier>();
            builder.Services.AddSingleton<IChatDiscordSource>(new LoopbackChatSource { RejectAccess = rejectAccess });
            builder.Services.AddSingleton<IRemoteGuildMemberSource,
                NoRemoteGuildMemberSource>();
            builder.Services.AddSingleton<CanonicalRemoteAuthorResolver>();
            builder.Services.AddSingleton<IChatAuthorizationService, ChatAuthorizationService>();
            builder.Services.AddSingleton<DiscordChatMessageNormalizer>();
            builder.Services.AddSingleton<ActiveChatStreamRegistry>();
            builder.Services.AddSingleton<RemoteChatService>();
            if (rejectAccess || shutdownTest)
            {
                builder.Services.AddSingleton<ActiveSalesStreamRegistry>();
                builder.Services.AddSingleton<RemoteSalesService>();
            }
            if (shutdownTest)
            {
                builder.Services.AddSingleton<BackendConnectionHealth>();
                builder.Services.AddSingleton<BackendMetrics>();
                builder.Services.AddSingleton<FakeGateway>();
                builder.Services.AddSingleton<IDiscordGatewayLifecycle>(services => services.GetRequiredService<FakeGateway>());
                builder.Services.AddHostedService<DiscordBackendWorker>();
            }
            var app = builder.Build();
            app.MapTransportApi();
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!;
            return new ChatFixture(
                app,
                stateDirectory,
                new Uri(addresses.Addresses.Single()));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            if (Directory.Exists(_stateDirectory))
            {
                Directory.Delete(_stateDirectory, recursive: true);
            }
        }
    }

    private sealed class FakeGateway(BackendConnectionHealth health) : IDiscordGatewayLifecycle
    {
        public int StopCount { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            health.Transition(BackendConnectionHealthState.Ready, BackendConnectionHealthReason.GatewayReady);
            return Task.CompletedTask;
        }
        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
    }

    private sealed class AlwaysMemberVerifier : IGuildMembershipVerifier
    {
        public Task<GuildMembershipStatus> VerifyAsync(
            AuthenticatedClientIdentity identity,
            CancellationToken cancellationToken) =>
            Task.FromResult(GuildMembershipStatus.Member);
    }

    private sealed class NoRemoteGuildMemberSource : IRemoteGuildMemberSource
    {
        public Task<RemoteGuildMemberResolution> ResolveAsync(
            ulong guildId,
            ulong authorId,
            CancellationToken cancellationToken) => Task.FromResult(
            new RemoteGuildMemberResolution(
                RemoteGuildMemberResolutionStatus.NotFound));
    }

    private sealed class LoopbackChatSource : IChatDiscordSource
    {
        public bool RejectAccess { get; init; }

        public Task<ChatGuildSourceResult> GetGuildAsync(
            AuthenticatedClientIdentity identity,
            CancellationToken cancellationToken)
        {
            if (RejectAccess)
            {
                return Task.FromResult(new ChatGuildSourceResult(ChatSourceStatus.NotMember, null));
            }
            const ulong read = DiscordPermissionEvaluator.ViewChannel |
                DiscordPermissionEvaluator.ReadMessageHistory;
            return Task.FromResult(new ChatGuildSourceResult(
                ChatSourceStatus.Available,
                new ChatGuildSnapshot(
                    123,
                    new[] { new ChatRolePermission(123, read) },
                    new ChatMemberSnapshot(456, Array.Empty<ulong>()),
                    new ChatMemberSnapshot(999, Array.Empty<ulong>()),
                    new[]
                    {
                        new ChatChannelSnapshot(
                            new ChatChannelDescriptor(123, 789, "main", 0, false),
                            Array.Empty<ChatPermissionOverwrite>()),
                        new ChatChannelSnapshot(
                            new ChatChannelDescriptor(123, 790, "channel-c", 1, false),
                            Array.Empty<ChatPermissionOverwrite>()),
                        new ChatChannelSnapshot(
                            new ChatChannelDescriptor(123, 791, "channel-d", 2, false),
                            Array.Empty<ChatPermissionOverwrite>()),
                    })));
        }

        public Task<ChatMessagesSourceResult> GetRecentMessagesAsync(
            ulong channelId,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(
            new ChatMessagesSourceResult(
                ChatSourceStatus.Available,
                Array.Empty<IMessage>()));

        public Task<ChatMessageSourceResult> GetMessageAsync(
            ulong channelId,
            ulong messageId,
            CancellationToken cancellationToken) => Task.FromResult(
            new ChatMessageSourceResult(ChatSourceStatus.NotFound, null));
    }
}

using System.Text.Json;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Infrastructure.Discord.Channels;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Tests.Discord.Connection;

public sealed class DiscordChannelResolverTests
{
    [Fact]
    public async Task Resolve_UsesUniqueExactTargetNamesAndReturnsStableIds()
    {
        var client = new ResolverRpcClient(
            Guilds("guild-1", "Guild"),
            Channels(
                ("main-id", "🏠메인", 0),
                ("sales-id", "🚒판매모집", 0),
                ("voice-id", "voice", 2)));
        var resolver = new DiscordChannelResolver();

        var targets = await resolver.ResolveAsync(
            client,
            new DiscordTargetOptions(),
            CancellationToken.None);

        Assert.Equal("guild-1", targets.GuildId);
        Assert.Equal("main-id", targets.MainChannelId);
        Assert.Equal("sales-id", targets.SalesChannelId);
    }

    [Fact]
    public async Task Resolve_DuplicateExactNameThrowsAmbiguityInsteadOfSelectingArbitrarily()
    {
        var client = new ResolverRpcClient(
            Guilds("guild-1", "Guild"),
            Channels(
                ("main-a", "🏠메인", 0),
                ("main-b", "🏠메인", 0),
                ("sales", "🚒판매모집", 0)));
        var resolver = new DiscordChannelResolver();

        await Assert.ThrowsAsync<DiscordChannelResolutionException>(() =>
            resolver.ResolveAsync(client, new DiscordTargetOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_ConfiguredIdsRemainIdentityWhenChannelNamesChange()
    {
        var client = new ResolverRpcClient(
            Guilds("guild-1", "Guild"),
            Channels(
                ("main-id", "renamed-main", 0),
                ("sales-id", "renamed-sales", 0)));
        var resolver = new DiscordChannelResolver();

        var targets = await resolver.ResolveAsync(
            client,
            new DiscordTargetOptions
            {
                GuildId = "guild-1",
                MainChannelId = "main-id",
                SalesChannelId = "sales-id",
            },
            CancellationToken.None);

        Assert.Equal("renamed-main", targets.MainChannelName);
        Assert.Equal("renamed-sales", targets.SalesChannelName);
    }

    [Fact]
    public async Task Resolve_FirstRunProfileRequiresExplicitMainSelection()
    {
        var client = new ResolverRpcClient(
            Guilds(ProductionServerProfile.GuildId, "Target Guild"),
            Channels(
                ("main-by-name", ProductionServerProfile.DefaultMainChannelName, 0),
                (ProductionServerProfile.SalesChannelId, ProductionServerProfile.SalesChannelName, 0)));
        var resolver = new DiscordChannelResolver();

        var error = await Assert.ThrowsAsync<DiscordChannelResolutionException>(() =>
            resolver.ResolveAsync(
                client,
                new DiscordTargetOptions
                {
                    GuildId = ProductionServerProfile.GuildId,
                    SalesChannelId = ProductionServerProfile.SalesChannelId,
                    RequireConfiguredMainChannel = true,
                },
                CancellationToken.None));

        Assert.Contains("must be selected", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Guilds(string id, string name) => Parse($$"""
        { "data": { "guilds": [{ "id": "{{id}}", "name": "{{name}}" }] } }
        """);

    private static JsonElement Channels(params (string Id, string Name, int Type)[] channels)
    {
        var payload = JsonSerializer.Serialize(new
        {
            data = new
            {
                channels = channels.Select(channel => new
                {
                    id = channel.Id,
                    name = channel.Name,
                    type = channel.Type,
                }),
            },
        });
        return Parse(payload);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class ResolverRpcClient : IDiscordRpcClient
    {
        private readonly JsonElement _guilds;
        private readonly JsonElement _channels;

        public ResolverRpcClient(JsonElement guilds, JsonElement channels)
        {
            _guilds = guilds;
            _channels = channels;
        }

        public event Action<JsonElement>? DispatchReceived
        {
            add { }
            remove { }
        }

        public Task<string> ConnectAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JsonElement> HandshakeAsync(
            string clientId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JsonElement> CommandAsync(
            string command,
            object arguments,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(command switch
            {
                "GET_GUILDS" => _guilds,
                "GET_CHANNELS" => _channels,
                _ => throw new NotSupportedException(command),
            });

        public Task<JsonElement> SubscribeAsync(
            string eventName,
            object arguments,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Exception?> WaitForDisconnectAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

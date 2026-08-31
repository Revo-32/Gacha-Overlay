using System.Text.Json;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Channels;

namespace GachaOverlay.Tests.Discord.Connection;

public sealed class M81ServerDiscoveryTests
{
    [Fact]
    public async Task Discovery_UsesFixedGuildFiltersTypesAndExcludesSalesChannel()
    {
        string? requestedGuildId = null;
        var client = CreateClient(
            includeTargetGuild: true,
            inspectCommand: (command, arguments) =>
            {
                if (command == "GET_CHANNELS")
                {
                    requestedGuildId = arguments.GetProperty("guild_id").GetString();
                }
            });
        var service = CreateService(new FakeRpcClientFactory(client));

        var result = await service.DiscoverAsync(forceRefresh: true);

        Assert.Equal(DiscordServerDiscoveryState.Ready, result.State);
        Assert.Equal("홍타 서버", result.GuildName);
        Assert.Equal("🚒판매모집", result.SalesChannelName);
        Assert.Equal(["main", "pictures"], result.MainChannels.Select(channel => channel.ChannelId));
        Assert.Equal(["#메인", "#사진"], result.MainChannels.Select(channel => channel.DisplayText));
        Assert.DoesNotContain(result.MainChannels, channel =>
            channel.ChannelId == ProductionServerProfile.SalesChannelId);
        Assert.Equal(ProductionServerProfile.GuildId, requestedGuildId);
    }

    [Fact]
    public async Task ManualRefresh_BypassesCacheWhileOrdinaryReadReusesIt()
    {
        var factory = new FakeRpcClientFactory(
            CreateClient(includeTargetGuild: true),
            CreateClient(includeTargetGuild: true));
        var service = CreateService(factory);

        var first = await service.DiscoverAsync(forceRefresh: true);
        var cached = await service.DiscoverAsync(forceRefresh: false);
        var refreshed = await service.DiscoverAsync(forceRefresh: true);

        Assert.Equal(first.RequestRevision, cached.RequestRevision);
        Assert.True(refreshed.RequestRevision > cached.RequestRevision);
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task TargetGuildMissing_DoesNotSelectAnotherGuild()
    {
        var service = CreateService(new FakeRpcClientFactory(CreateClient(includeTargetGuild: false)));

        var result = await service.DiscoverAsync(forceRefresh: true);

        Assert.Equal(DiscordServerDiscoveryState.TargetGuildMissing, result.State);
        Assert.Empty(result.MainChannels);
    }

    [Fact]
    public async Task ConcurrentRefreshes_CoalesceIntoOneRpcSession()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateClient(includeTargetGuild: true, async command =>
        {
            if (command == "GET_GUILDS")
            {
                started.TrySetResult();
                await release.Task;
            }
        });
        var factory = new FakeRpcClientFactory(client);
        var service = CreateService(factory);

        var first = service.DiscoverAsync(forceRefresh: true);
        await started.Task;
        var second = service.DiscoverAsync(forceRefresh: true);
        release.SetResult();

        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Equal(DiscordServerDiscoveryState.Ready, result.State));
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task InvalidatedInFlightResult_IsMarkedStaleAndNotCached()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstClient = CreateClient(includeTargetGuild: true, async command =>
        {
            if (command == "GET_GUILDS")
            {
                started.TrySetResult();
                await release.Task;
            }
        });
        var secondClient = CreateClient(includeTargetGuild: true);
        var factory = new FakeRpcClientFactory(firstClient, secondClient);
        var service = CreateService(factory);

        var old = service.DiscoverAsync(forceRefresh: true);
        await started.Task;
        service.Invalidate();
        release.SetResult();
        Assert.True((await old).IsStale);

        var current = await service.DiscoverAsync(forceRefresh: false);
        Assert.False(current.IsStale);
        Assert.Equal(2, factory.CreateCount);
    }

    private static DiscordServerConfigurationService CreateService(FakeRpcClientFactory factory) =>
        new(
            new AlwaysRunningDiscordProcessService(),
            new FakeCredentialProvider(),
            factory,
            new FakeAuthenticationService(),
            NullAppLogger.Instance);

    private static FakeRpcClient CreateClient(
        bool includeTargetGuild,
        Func<string, Task>? beforeCommand = null,
        Action<string, JsonElement>? inspectCommand = null) => new()
        {
            CommandHandler = async (command, arguments, _) =>
            {
                inspectCommand?.Invoke(command, arguments);
                if (beforeCommand is not null)
                {
                    await beforeCommand(command);
                }

                return command switch
                {
                    "GET_GUILDS" => Guilds(includeTargetGuild),
                    "GET_CHANNELS" => Channels(),
                    _ => Parse("{\"data\":{}}"),
                };
            },
        };

    private static JsonElement Guilds(bool includeTargetGuild) =>
        JsonSerializer.SerializeToElement(new
        {
            data = new
            {
                guilds = includeTargetGuild
                    ? new[]
                    {
                        new { id = ProductionServerProfile.GuildId, name = "홍타 서버" },
                        new { id = "other", name = "Other" },
                    }
                    : new[] { new { id = "other", name = "Other" } },
            },
        });

    private static JsonElement Channels() => JsonSerializer.SerializeToElement(new
    {
        data = new
        {
            channels = new[]
            {
                new { id = "main", name = "메인", type = 0 },
                new { id = "pictures", name = "사진", type = 0 },
                new { id = "category", name = "Category", type = 4 },
                new { id = "voice", name = "Voice", type = 2 },
                new { id = "stage", name = "Stage", type = 13 },
                new { id = "forum", name = "Forum", type = 15 },
                new { id = ProductionServerProfile.SalesChannelId, name = "🚒판매모집", type = 0 },
            },
        },
    });

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

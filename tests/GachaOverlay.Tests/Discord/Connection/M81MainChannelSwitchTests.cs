using System.Text.Json;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Connection;
using GachaOverlay.Infrastructure.Discord.Normalization;

namespace GachaOverlay.Tests.Discord.Connection;

public sealed class M81MainChannelSwitchTests
{
    [Fact]
    public async Task SuccessfulSwitch_AtomicallyReplacesMainAndPreservesSalesAndGeneration()
    {
        var client = CreateClient();
        var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);
        var before = coordinator.MessageState;

        var result = await coordinator.SwitchMainChannelAsync(
            new DiscordMainChannelOption("next", "자유채팅"));

        Assert.Equal(MainChannelSwitchStatus.Succeeded, result.Status);
        Assert.Equal(before.Generation, coordinator.MessageState.Generation);
        Assert.Equal(["new-message"], coordinator.MessageState.MainChat.Select(message => message.MessageId));
        Assert.Equal(["sales-message"], coordinator.MessageState.SalesSource.Select(message => message.MessageId));
        Assert.Equal("next", coordinator.MessageState.MainChat.Single().ChannelId);
        Assert.Equal(9, client.SubscriptionCount);
        Assert.Equal(3, client.UnsubscriptionCount);
        Assert.All(client.UnsubscribedChannelIds, id => Assert.Equal("main", id));
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task SameChannel_IsNoOpWithoutSubscriptionOrStoreChange()
    {
        var client = CreateClient();
        var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);
        var before = coordinator.MessageState;

        var result = await coordinator.SwitchMainChannelAsync(
            new DiscordMainChannelOption("main", "🏠메인"));

        Assert.Equal(MainChannelSwitchStatus.NoChange, result.Status);
        Assert.Same(before, coordinator.MessageState);
        Assert.Equal(6, client.SubscriptionCount);
        Assert.Equal(0, client.UnsubscriptionCount);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task FailedBootstrap_RetainsOldMainAndRemovesPreparedSubscription()
    {
        var client = CreateClient(failNextBootstrap: true);
        var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        var result = await coordinator.SwitchMainChannelAsync(
            new DiscordMainChannelOption("next", "자유채팅"));

        Assert.Equal(MainChannelSwitchStatus.Failed, result.Status);
        Assert.Equal(["old-message"], coordinator.MessageState.MainChat.Select(message => message.MessageId));
        Assert.Equal(["sales-message"], coordinator.MessageState.SalesSource.Select(message => message.MessageId));
        Assert.Equal(3, client.UnsubscriptionCount);
        Assert.All(client.UnsubscribedChannelIds, id => Assert.Equal("next", id));
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task NewerSelection_SupersedesLatePreviousBootstrap()
    {
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeRpcClient
        {
            CommandHandler = async (command, arguments, _) =>
            {
                if (command == "GET_CHANNELS")
                {
                    return JsonSerializer.SerializeToElement(new
                    {
                        data = new
                        {
                            channels = new[]
                            {
                                new { id = "b", name = "B", type = 0 },
                                new { id = "c", name = "C", type = 0 },
                            },
                        },
                    });
                }

                if (command == "GET_CHANNEL")
                {
                    var id = arguments.GetProperty("channel_id").GetString();
                    if (id == "b")
                    {
                        bStarted.SetResult();
                        await releaseB.Task;
                    }

                    return id switch
                    {
                        "main" => Channel("old-message", "old"),
                        "sales" => Channel("sales-message", "sales"),
                        "b" => Channel("b-message", "b"),
                        "c" => Channel("c-message", "c"),
                        _ => Parse("{\"data\":{\"messages\":[]}}"),
                    };
                }

                return Parse("{\"data\":{}}");
            },
        };
        var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        var b = coordinator.SwitchMainChannelAsync(new DiscordMainChannelOption("b", "B"));
        await bStarted.Task;
        var c = coordinator.SwitchMainChannelAsync(new DiscordMainChannelOption("c", "C"));
        releaseB.SetResult();

        Assert.Equal(MainChannelSwitchStatus.Superseded, (await b).Status);
        Assert.Equal(MainChannelSwitchStatus.Succeeded, (await c).Status);
        Assert.Equal(["c-message"], coordinator.MessageState.MainChat.Select(message => message.MessageId));
        Assert.DoesNotContain(coordinator.MessageState.MainChat, message => message.MessageId == "b-message");
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulSwitch_CommitsOnlyLatestTwentyInStableOrder()
    {
        var client = new FakeRpcClient
        {
            CommandHandler = (command, arguments, _) =>
            {
                if (command == "GET_CHANNELS")
                {
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        data = new
                        {
                            channels = new[] { new { id = "next", name = "Next", type = 0 } },
                        },
                    }));
                }

                if (command == "GET_CHANNEL")
                {
                    var id = arguments.GetProperty("channel_id").GetString();
                    return Task.FromResult(id switch
                    {
                        "main" => Channel("old-message", "old"),
                        "sales" => Channel("sales-message", "sales"),
                        "next" => JsonSerializer.SerializeToElement(new
                        {
                            data = new
                            {
                                messages = Enumerable.Range(1, 25).Select(index => new
                                {
                                    id = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    content = $"message-{index}",
                                    timestamp = new DateTimeOffset(
                                            2026,
                                            9,
                                            1,
                                            0,
                                            0,
                                            0,
                                            TimeSpan.Zero)
                                        .AddMinutes(index)
                                        .ToString("O"),
                                    author = new { id = "user", username = "User" },
                                }),
                            },
                        }),
                        _ => Parse("{\"data\":{\"messages\":[]}}"),
                    });
                }

                return Task.FromResult(Parse("{\"data\":{}}"));
            },
        };
        var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        var result = await coordinator.SwitchMainChannelAsync(
            new DiscordMainChannelOption("next", "Next"));

        Assert.Equal(MainChannelSwitchStatus.Succeeded, result.Status);
        Assert.Equal(20, coordinator.MessageState.MainChat.Count);
        Assert.Equal(
            Enumerable.Range(6, 20).Select(index => index.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            coordinator.MessageState.MainChat.Select(message => message.MessageId));
        Assert.Equal(["sales-message"], coordinator.MessageState.SalesSource.Select(
            message => message.MessageId));
        await coordinator.DisposeAsync();
    }

    private static FakeRpcClient CreateClient(bool failNextBootstrap = false) => new()
    {
        CommandHandler = (command, arguments, _) =>
        {
            if (command == "GET_CHANNELS")
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    data = new
                    {
                        channels = new[]
                        {
                            new { id = "next", name = "자유채팅", type = 0 },
                            new { id = "voice", name = "Voice", type = 2 },
                            new { id = ProductionServerProfile.SalesChannelId, name = "🚒판매모집", type = 0 },
                        },
                    },
                }));
            }

            if (command == "GET_CHANNEL")
            {
                var id = arguments.GetProperty("channel_id").GetString();
                if (id == "next" && failNextBootstrap)
                {
                    throw new IOException("simulated bootstrap failure");
                }

                return Task.FromResult(id switch
                {
                    "main" => Channel("old-message", "old"),
                    "sales" => Channel("sales-message", "sales"),
                    "next" => Channel("new-message", "new"),
                    _ => Parse("{\"data\":{\"messages\":[]}}"),
                });
            }

            return Task.FromResult(Parse("{\"data\":{}}"));
        },
    };

    private static DiscordConnectionCoordinator CreateCoordinator(FakeRpcClient client) =>
        new(
            new AlwaysRunningDiscordProcessService(),
            new FakeCredentialProvider(),
            new FakeRpcClientFactory(client),
            new FakeAuthenticationService(),
            new FakeChannelResolver(),
            new DiscordMessageNormalizer(NullAppLogger.Instance),
            new DiscordMessagePipeline(),
            new DiscordTargetOptions(),
            new ImmediateReconnectDelayStrategy(),
            NullAppLogger.Instance);

    private static JsonElement Channel(string messageId, string content) =>
        JsonSerializer.SerializeToElement(new
        {
            data = new
            {
                messages = new[]
                {
                    new
                    {
                        id = messageId,
                        content,
                        timestamp = "2026-09-01T00:00:00Z",
                        author = new { id = "user", username = "User" },
                    },
                },
            },
        });

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("Expected coordinator state was not reached.");
            }

            await Task.Delay(10);
        }
    }
}

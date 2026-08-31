using System.Text.Json;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Connection;
using GachaOverlay.Infrastructure.Discord.Normalization;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Tests.Discord.Connection;

public sealed class M755ForwardCoordinatorTests
{
    [Fact]
    public async Task LiveOpaqueWrapper_HydratesFlattenedTextFromMainSnapshot()
    {
        var mainRequests = 0;
        var client = new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) => Task.FromResult(
                channelId != "main" || Interlocked.Increment(ref mainRequests) == 1
                    ? Response()
                    : Response(SourceMessage("wrapper", "hydrated text"))),
        };
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", "\"blocked\":false"));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.MessageId == "wrapper" && message.Content == "hydrated text"));

        var wrapper = Assert.Single(coordinator.MessageState.MainChat);
        Assert.Equal(DiscordMessageFallbackKind.None, wrapper.FallbackKind);
        Assert.Equal(2, client.RequestedChannelIds.Count(id => id == "main"));
        Assert.Equal(6, client.SubscriptionCount);
    }

    [Fact]
    public async Task LiveOpaqueWrapper_HydratesFlattenedImageFromMainSnapshot()
    {
        var mainRequests = 0;
        var client = new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) => Task.FromResult(
                channelId != "main" || Interlocked.Increment(ref mainRequests) == 1
                    ? Response()
                    : Response("""
                        {
                          "id":"wrapper", "content":"",
                          "attachments":[{
                            "id":"image", "url":"https://cdn.example/image.png",
                            "content_type":"image/png"
                          }],
                          "embeds":[], "type":0
                        }
                        """)),
        };
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", "\"blocked\":false"));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.MessageId == "wrapper" && message.Attachments.Count == 1));

        var wrapper = Assert.Single(coordinator.MessageState.MainChat);
        Assert.Equal(DiscordMessageFallbackKind.None, wrapper.FallbackKind);
        Assert.Equal("https://cdn.example/image.png", Assert.Single(wrapper.Attachments).Url);
        Assert.Equal(2, client.RequestedChannelIds.Count(id => id == "main"));
    }

    [Fact]
    public async Task LiveOpaqueSticker_UsesStickerOnlyAfterPositiveUiEvidence()
    {
        var mainRequests = 0;
        var client = new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) => Task.FromResult(
                channelId != "main" || Interlocked.Increment(ref mainRequests) == 1
                    ? Response()
                    : Response("""
                        {
                          "id":"wrapper", "content":"",
                          "attachments":[], "embeds":[], "type":0
                        }
                        """)),
        };
        var resolver = new StubOpaqueMessageResolver(
            new DiscordOpaqueMessageResolution(
                DiscordOpaqueMessageResolutionKind.Sticker));
        await using var coordinator = CreateCoordinator(client, resolver);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", "\"blocked\":false"));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.MessageId == "wrapper" &&
            message.FallbackKind == DiscordMessageFallbackKind.Sticker));

        Assert.Equal(3, client.RequestedChannelIds.Count(id => id == "main"));
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task LiveOpaqueForwardedText_UsesUiContentWhenRpcRemainsOpaque()
    {
        var client = OpaqueSnapshotClient();
        var resolver = new StubOpaqueMessageResolver(
            new DiscordOpaqueMessageResolution(
                DiscordOpaqueMessageResolutionKind.ForwardedText,
                "forwarded body"));
        await using var coordinator = CreateCoordinator(client, resolver);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", "\"blocked\":false"));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.MessageId == "wrapper" && message.Content == "forwarded body"));

        var wrapper = Assert.Single(coordinator.MessageState.MainChat);
        Assert.Equal(DiscordMessageFallbackKind.None, wrapper.FallbackKind);
        Assert.Equal(DiscordForwardResolutionMode.FlattenedPayload, wrapper.Forward?.Resolution);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task LiveOpaqueForwardMarkerWithoutContent_UsesForwardFallback()
    {
        var client = OpaqueSnapshotClient();
        var resolver = new StubOpaqueMessageResolver(
            new DiscordOpaqueMessageResolution(
                DiscordOpaqueMessageResolutionKind.ForwardedMessage));
        await using var coordinator = CreateCoordinator(client, resolver);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", "\"blocked\":false"));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.MessageId == "wrapper" &&
            message.FallbackKind == DiscordMessageFallbackKind.ForwardedMessage));

        var wrapper = Assert.Single(coordinator.MessageState.MainChat);
        Assert.Equal(DiscordForwardResolutionMode.Fallback, wrapper.Forward?.Resolution);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task LiveOpaqueUnknown_UsesNeutralMessageInsteadOfSticker()
    {
        var client = OpaqueSnapshotClient();
        var resolver = new StubOpaqueMessageResolver(
            new DiscordOpaqueMessageResolution(
                DiscordOpaqueMessageResolutionKind.Unknown));
        await using var coordinator = CreateCoordinator(client, resolver);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", "\"blocked\":false"));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.MessageId == "wrapper" &&
            message.FallbackKind == DiscordMessageFallbackKind.Message));

        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task Delete_CancelsOpaqueHydrationAndDoesNotResurrectWrapper()
    {
        var hydration = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mainRequests = 0;
        var client = new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) =>
            {
                if (channelId == "main" && Interlocked.Increment(ref mainRequests) > 1)
                {
                    return hydration.Task;
                }

                return Task.FromResult(Response());
            },
        };
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", "\"blocked\":false"));
        await WaitUntilAsync(() => client.GetChannelCount == 3);
        client.Publish(DispatchDelete("wrapper"));
        hydration.SetResult(Response(SourceMessage("wrapper", "late")));
        await Task.Delay(100);

        Assert.DoesNotContain(
            coordinator.MessageState.MainChat,
            message => message.MessageId == "wrapper");
    }

    [Fact]
    public async Task SnapshotSufficient_DoesNotCallAdditionalGetChannel()
    {
        var client = new FakeRpcClient();
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", SnapshotFields("\"content\":\"snapshot text\"")));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.MessageId == "wrapper" && message.Content == "snapshot text"));

        Assert.Equal(2, client.GetChannelCount);
        Assert.Equal(6, client.SubscriptionCount);
    }

    [Fact]
    public async Task SnapshotInsufficient_UsesOneOnDemandSourceLookup()
    {
        var client = new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) => Task.FromResult(
                channelId == "source"
                    ? Response(SourceMessage("source-message", "resolved"))
                    : Response()),
        };
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", ReferenceOnly()));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.MessageId == "wrapper" &&
            message.Forward?.Resolution == DiscordForwardResolutionMode.LookupResolved));

        var wrapper = Assert.Single(coordinator.MessageState.MainChat);
        Assert.Equal("resolved", wrapper.Content);
        Assert.Equal(3, client.GetChannelCount);
        Assert.Equal(1, client.RequestedChannelIds.Count(id => id == "source"));
        Assert.Equal(6, client.SubscriptionCount);
    }

    [Fact]
    public async Task MissingSourceIdentity_DoesNotAttemptLookup()
    {
        var client = new FakeRpcClient();
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate(
            "wrapper",
            "\"message_reference\":{\"type\":1}"));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.MessageId == "wrapper"));

        var wrapper = Assert.Single(coordinator.MessageState.MainChat);
        Assert.Equal(DiscordMessageFallbackKind.ForwardedMessage, wrapper.FallbackKind);
        Assert.Equal(2, client.GetChannelCount);
    }

    [Fact]
    public async Task SameSourceForwardedTwice_SharesLookupButKeepsTwoWrappers()
    {
        var sourceResponse = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) => channelId == "source"
                ? sourceResponse.Task
                : Task.FromResult(Response()),
        };
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper-one", ReferenceOnly()));
        client.Publish(DispatchCreate("wrapper-two", ReferenceOnly()));
        await WaitUntilAsync(() => client.GetChannelCount == 3);
        sourceResponse.SetResult(Response(SourceMessage("source-message", "shared")));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Count(message =>
            message.Content == "shared") == 2);

        Assert.Equal(3, client.GetChannelCount);
        Assert.Equal(
            new[] { "wrapper-one", "wrapper-two" },
            coordinator.MessageState.MainChat.Select(message => message.MessageId));
        Assert.DoesNotContain(
            coordinator.MessageState.MainChat,
            message => message.MessageId == "source-message");
    }

    [Fact]
    public async Task Delete_IgnoresLateSourceResolutionAndDoesNotResurrectWrapper()
    {
        var sourceResponse = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) => channelId == "source"
                ? sourceResponse.Task
                : Task.FromResult(Response()),
        };
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", ReferenceOnly()));
        await WaitUntilAsync(() => client.GetChannelCount == 3);
        client.Publish(DispatchDelete("wrapper"));
        sourceResponse.SetResult(Response(SourceMessage("source-message", "late")));
        await Task.Delay(100);

        Assert.DoesNotContain(coordinator.MessageState.MainChat, message => message.MessageId == "wrapper");
    }

    [Fact]
    public async Task RetentionEviction_IgnoresLateSourceResolutionAndDoesNotReinsertWrapper()
    {
        var sourceResponse = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) => channelId == "source"
                ? sourceResponse.Task
                : Task.FromResult(Response()),
        };
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("1", ReferenceOnly()));
        await WaitUntilAsync(() => client.GetChannelCount == 3);
        for (var id = 2; id <= 21; id++)
        {
            client.Publish(DispatchCreate(id.ToString(), $"\"content\":\"message-{id}\""));
        }

        await WaitUntilAsync(() =>
            coordinator.MessageState.MainChat.Count == DiscordMessagePipeline.MainChatRetentionLimit &&
            coordinator.MessageState.MainChat.All(message => message.MessageId != "1"));
        sourceResponse.SetResult(Response(SourceMessage("source-message", "late")));
        await Task.Delay(100);

        Assert.Equal(DiscordMessagePipeline.MainChatRetentionLimit, coordinator.MessageState.MainChat.Count);
        Assert.DoesNotContain(coordinator.MessageState.MainChat, message => message.MessageId == "1");
        Assert.DoesNotContain(coordinator.MessageState.MainChat, message => message.Content == "late");
    }

    [Fact]
    public async Task ReconnectGeneration_IgnoresLateSourceResolutionFromOldClient()
    {
        var sourceResponse = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) => channelId == "source"
                ? sourceResponse.Task
                : Task.FromResult(Response()),
        };
        var second = new FakeRpcClient();
        await using var coordinator = CreateCoordinator(new FakeRpcClientFactory(first, second));
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status is
        {
            State: DiscordConnectionState.Connected,
            Generation: 1,
        });

        first.Publish(DispatchCreate("1", ReferenceOnly()));
        await WaitUntilAsync(() => first.GetChannelCount == 3);
        first.Disconnect(new IOException("simulated disconnect"));
        await WaitUntilAsync(() => coordinator.Status is
        {
            State: DiscordConnectionState.Connected,
            Generation: 2,
        });
        sourceResponse.SetResult(Response(SourceMessage("source-message", "old generation")));
        await Task.Delay(100);

        Assert.Equal(2, coordinator.MessageState.Generation);
        Assert.Empty(coordinator.MessageState.MainChat);
    }

    [Fact]
    public async Task Update_IgnoresOldLookupAndKeepsNewSnapshotContent()
    {
        var sourceResponse = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) => channelId == "source"
                ? sourceResponse.Task
                : Task.FromResult(Response()),
        };
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", ReferenceOnly()));
        await WaitUntilAsync(() => client.GetChannelCount == 3);
        client.Publish(DispatchUpdate(
            "wrapper",
            SnapshotFields("\"content\":\"new snapshot\"")));
        sourceResponse.SetResult(Response(SourceMessage("source-message", "old late value")));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.MessageId == "wrapper" && message.Content == "new snapshot"));
        await Task.Delay(100);

        var wrapper = Assert.Single(coordinator.MessageState.MainChat);
        Assert.Equal("new snapshot", wrapper.Content);
        Assert.Equal(DiscordForwardResolutionMode.Snapshot, wrapper.Forward?.Resolution);
    }

    [Fact]
    public async Task ForwardCreateUpdateDelete_UsesWrapperLifecycleOnly()
    {
        var client = new FakeRpcClient();
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate(
            "wrapper",
            SnapshotFields("\"content\":\"created\"")));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.Content == "created"));
        client.Publish(DispatchUpdate(
            "wrapper",
            SnapshotFields("\"content\":\"updated\"")));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.Content == "updated"));
        client.Publish(DispatchDelete("wrapper"));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Count == 0);

        Assert.Empty(coordinator.MessageState.MainChat);
        Assert.Equal(6, client.SubscriptionCount);
    }

    [Fact]
    public async Task SourceLookup_UnrelatedMessagesNeverEnterMainChat()
    {
        var client = new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) => Task.FromResult(
                channelId == "source"
                    ? Response(
                        SourceMessage("unrelated", "must not appear"),
                        SourceMessage("source-message", "selected"))
                    : Response()),
        };
        await using var coordinator = CreateCoordinator(client);
        coordinator.Start(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.Status.State == DiscordConnectionState.Connected);

        client.Publish(DispatchCreate("wrapper", ReferenceOnly()));
        await WaitUntilAsync(() => coordinator.MessageState.MainChat.Any(message =>
            message.Content == "selected"));

        var wrapper = Assert.Single(coordinator.MessageState.MainChat);
        Assert.Equal("wrapper", wrapper.MessageId);
        Assert.DoesNotContain("must not appear", wrapper.Content, StringComparison.Ordinal);
    }

    private static DiscordConnectionCoordinator CreateCoordinator(
        FakeRpcClient client,
        IDiscordOpaqueMessageResolver? opaqueMessageResolver = null) =>
        CreateCoordinator(new FakeRpcClientFactory(client), opaqueMessageResolver);

    private static DiscordConnectionCoordinator CreateCoordinator(
        IDiscordRpcClientFactory factory,
        IDiscordOpaqueMessageResolver? opaqueMessageResolver = null) => new(
        new AlwaysRunningDiscordProcessService(),
        new FakeCredentialProvider(),
        factory,
        new FakeAuthenticationService(),
        new FakeChannelResolver(),
        new DiscordMessageNormalizer(NullAppLogger.Instance),
        new DiscordMessagePipeline(),
        new DiscordTargetOptions(),
        new ImmediateReconnectDelayStrategy(),
        NullAppLogger.Instance,
        opaqueMessageResolver);

    private static FakeRpcClient OpaqueSnapshotClient()
    {
        var mainRequests = 0;
        return new FakeRpcClient
        {
            GetChannelAsync = (channelId, _) => Task.FromResult(
                channelId != "main" || Interlocked.Increment(ref mainRequests) == 1
                    ? Response()
                    : Response("""
                        {
                          "id":"wrapper", "content":"",
                          "attachments":[], "embeds":[], "type":0
                        }
                        """)),
        };
    }

    private static string ReferenceOnly() => """
        "message_reference":{
          "type":1, "guild_id":"guild",
          "channel_id":"source", "message_id":"source-message"
        }
        """;

    private static string SnapshotFields(string snapshotFields) => $$$"""
        "message_reference":{
          "type":1, "guild_id":"guild",
          "channel_id":"source", "message_id":"source-message"
        },
        "message_snapshots":[{"message":{ {{{snapshotFields}}} }}]
        """;

    private static JsonElement DispatchCreate(string id, string fields) =>
        Dispatch("MESSAGE_CREATE", id, fields);

    private static JsonElement DispatchUpdate(string id, string fields) =>
        Dispatch("MESSAGE_UPDATE", id, fields);

    private static JsonElement Dispatch(string eventName, string id, string fields)
    {
        using var document = JsonDocument.Parse($$$"""
            {
              "evt":"{{{eventName}}}",
              "data":{
                "channel_id":"main", "guild_id":"guild",
                "message":{
                  "id":"{{{id}}}", "channel_id":"main",
                  "author":{"id":"forwarder","username":"forwarder"},
                  "content":"", "attachments":[], "embeds":[], "type":0,
                  {{{fields}}}
                }
              }
            }
            """);
        return document.RootElement.Clone();
    }

    private static JsonElement DispatchDelete(string id)
    {
        using var document = JsonDocument.Parse(
            $$"""{ "evt":"MESSAGE_DELETE", "data":{ "channel_id":"main", "message_id":"{{id}}" } }""");
        return document.RootElement.Clone();
    }

    private static string SourceMessage(string id, string content) =>
        $$"""{ "id":"{{id}}", "content":"{{content}}" }""";

    private static JsonElement Response(params string[] messages)
    {
        using var document = JsonDocument.Parse(
            $$"""{ "data":{ "messages":[{{string.Join(',', messages)}}] } }""");
        return document.RootElement.Clone();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Expected coordinator state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class StubOpaqueMessageResolver(
        DiscordOpaqueMessageResolution resolution) : IDiscordOpaqueMessageResolver
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<DiscordOpaqueMessageResolution> ResolveAsync(
            string channelId,
            string messageId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(resolution);
        }
    }
}

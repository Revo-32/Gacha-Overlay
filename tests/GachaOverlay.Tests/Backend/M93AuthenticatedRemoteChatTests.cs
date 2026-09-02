using Discord;
using GachaOverlay.Core.Discord.Messages;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Security;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace GachaOverlay.Tests.Backend;

public sealed class M93AuthenticatedRemoteChatTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private const ulong Read = DiscordPermissionEvaluator.ViewChannel |
        DiscordPermissionEvaluator.ReadMessageHistory;

    [Fact]
    public void PermissionEvaluator_FollowsDiscordOverwriteOrder()
    {
        var roles = new[]
        {
            new ChatRolePermission(1, DiscordPermissionEvaluator.ViewChannel),
            new ChatRolePermission(2, DiscordPermissionEvaluator.ReadMessageHistory),
            new ChatRolePermission(3, 0),
        };
        var overwrites = new[]
        {
            new ChatPermissionOverwrite(1, ChatPermissionTarget.Role,
                DiscordPermissionEvaluator.ReadMessageHistory, 0),
            new ChatPermissionOverwrite(2, ChatPermissionTarget.Role, 0,
                DiscordPermissionEvaluator.ViewChannel),
            new ChatPermissionOverwrite(3, ChatPermissionTarget.Role,
                DiscordPermissionEvaluator.ViewChannel, 0),
        };

        var result = DiscordPermissionEvaluator.Compute(
            1, 10, new ulong[] { 2, 3 }, roles, overwrites);

        Assert.True(DiscordPermissionEvaluator.CanRead(result));
    }

    [Fact]
    public void PermissionEvaluator_MemberDenyWinsAfterRoleAllow()
    {
        var result = DiscordPermissionEvaluator.Compute(
            1,
            10,
            new ulong[] { 2 },
            new[]
            {
                new ChatRolePermission(1, Read),
                new ChatRolePermission(2, Read),
            },
            new[]
            {
                new ChatPermissionOverwrite(2, ChatPermissionTarget.Role, Read, 0),
                new ChatPermissionOverwrite(10, ChatPermissionTarget.Member, 0,
                    DiscordPermissionEvaluator.ReadMessageHistory),
            });

        Assert.False(DiscordPermissionEvaluator.CanRead(result));
    }

    [Fact]
    public void PermissionEvaluator_AdministratorBypassesOverwrites()
    {
        var result = DiscordPermissionEvaluator.Compute(
            1,
            10,
            Array.Empty<ulong>(),
            new[] { new ChatRolePermission(1, DiscordPermissionEvaluator.Administrator) },
            new[]
            {
                new ChatPermissionOverwrite(10, ChatPermissionTarget.Member, 0, ulong.MaxValue),
            });

        Assert.Equal(ulong.MaxValue, result);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(DiscordPermissionEvaluator.ViewChannel)]
    [InlineData(DiscordPermissionEvaluator.ReadMessageHistory)]
    public void PermissionEvaluator_RequiresViewAndHistory(ulong permissions)
    {
        Assert.False(DiscordPermissionEvaluator.CanRead(permissions));
    }

    [Fact]
    public async Task Catalog_RequiresBothUserAndBotAccess()
    {
        var source = FakeSource.WithGuild(
            channels: new[]
            {
                Channel(100),
                Channel(200, new ChatPermissionOverwrite(
                    99,
                    ChatPermissionTarget.Member,
                    0,
                    DiscordPermissionEvaluator.ViewChannel)),
                Channel(300, new ChatPermissionOverwrite(
                    10,
                    ChatPermissionTarget.Member,
                    0,
                    DiscordPermissionEvaluator.ViewChannel)),
            });
        var service = new ChatAuthorizationService(source);

        var result = await service.GetCatalogAsync(Identity(), default);

        Assert.Equal(ChatAuthorizationStatus.Authorized, result.Status);
        Assert.Equal(new ulong[] { 100 }, result.AuthorizedChannels
            .Select(channel => channel.ChannelId));
    }

    [Fact]
    public async Task AuthorizationLease_IsCachedAndSingleFlight()
    {
        var source = FakeSource.WithGuild(delay: TimeSpan.FromMilliseconds(50));
        var service = new ChatAuthorizationService(source);

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.GetCatalogAsync(Identity(), default)));

        Assert.Equal(1, source.GuildRequests);
    }

    [Fact]
    public async Task ExpiredRefreshFailure_DoesNotServeStaleAuthorization()
    {
        var now = DateTimeOffset.UtcNow;
        var source = FakeSource.WithGuild();
        var service = new ChatAuthorizationService(source, () => now);
        Assert.Equal(ChatAuthorizationStatus.Authorized,
            (await service.GetCatalogAsync(Identity(), default)).Status);
        now += ChatAuthorizationService.LeaseLifetime + TimeSpan.FromSeconds(1);
        source.GuildStatus = ChatSourceStatus.Unavailable;

        var refreshed = await service.GetCatalogAsync(Identity(), default);

        Assert.Equal(ChatAuthorizationStatus.AuthorizationUnavailable, refreshed.Status);
        Assert.Empty(refreshed.AuthorizedChannels);
    }

    [Fact]
    public void Bootstrap_JournalsLiveMutationDuringRestWindow()
    {
        var registry = new ActiveChatStreamRegistry();
        var capture = registry.Activate(Descriptor(100));
        registry.PublishUpsert(
            OverlayTransportProtocol.ChatMessageCreate,
            Message(2, 100, "live", DateTimeOffset.UtcNow.AddSeconds(1)));

        var completed = registry.CompleteBootstrap(capture, new[]
        {
            Message(1, 100, "rest", DateTimeOffset.UtcNow),
        });

        Assert.Equal(ChatResumeDisposition.Resumable, completed.Disposition);
        Assert.Equal(new ulong[] { 1, 2 }, completed.Messages.Select(item => item.MessageId));
    }

    [Fact]
    public void Bootstrap_IsLimitedToTwentyMostRecentMessages()
    {
        var registry = new ActiveChatStreamRegistry();
        var capture = registry.Activate(Descriptor(100));
        var epoch = DateTimeOffset.UtcNow;

        var completed = registry.CompleteBootstrap(capture, Enumerable.Range(1, 30)
            .Select(index => Message((ulong)index, 100, index.ToString(), epoch.AddSeconds(index)))
            .ToArray());

        Assert.Equal(20, completed.Messages.Count);
        Assert.Equal(11UL, completed.Messages[0].MessageId);
        Assert.Equal(30UL, completed.Messages[^1].MessageId);
    }

    [Fact]
    public async Task Update_PreservesCreationOrderAndCanonicalContent()
    {
        var registry = new ActiveChatStreamRegistry();
        var capture = registry.Activate(Descriptor(100));
        var epoch = DateTimeOffset.UtcNow;
        registry.CompleteBootstrap(capture, new[]
        {
            Message(1, 100, "a", epoch),
            Message(2, 100, "b", epoch.AddSeconds(1)),
        });
        var subscription = registry.PrepareResume(100, capture.Generation, 0).Subscription!;

        registry.PublishUpsert(
            OverlayTransportProtocol.ChatMessageUpdate,
            Message(1, 100, "edited", epoch.AddHours(1)));
        var update = await subscription.Reader.ReadAsync();

        Assert.Equal("edited", update.Message!.Content);
        Assert.Equal(epoch, update.Message.CreatedAt);
        await subscription.DisposeAsync();
    }

    [Fact]
    public void Delete_RemovesMessageFromBootstrapState()
    {
        var registry = new ActiveChatStreamRegistry();
        var capture = registry.Activate(Descriptor(100));
        registry.CompleteBootstrap(capture, new[] { Message(1, 100) });
        var next = registry.Activate(Descriptor(100));
        registry.PublishDelete(100, 1);

        var completed = registry.CompleteBootstrap(next, new[] { Message(1, 100) });

        Assert.Empty(completed.Messages);
    }

    [Fact]
    public async Task Resume_ReplaysThenStreamsChannelSpecificMutations()
    {
        var registry = new ActiveChatStreamRegistry();
        var capture = registry.Activate(Descriptor(100));
        registry.CompleteBootstrap(capture, Array.Empty<ChatMessage>());
        registry.PublishUpsert(
            OverlayTransportProtocol.ChatMessageCreate,
            Message(1, 100));

        var resume = registry.PrepareResume(100, capture.Generation, 0);
        Assert.Single(resume.Subscription!.Replay);
        registry.PublishDelete(100, 1);
        var live = await resume.Subscription.Reader.ReadAsync();

        Assert.Equal(OverlayTransportProtocol.ChatMessageDelete, live.EventType);
        await resume.Subscription.DisposeAsync();
    }

    [Fact]
    public async Task ChannelDeletion_NotifiesSubscriberAndRemovesActiveStream()
    {
        var registry = new ActiveChatStreamRegistry();
        var capture = registry.Activate(Descriptor(100));
        registry.CompleteBootstrap(capture, Array.Empty<ChatMessage>());
        var subscription = registry.PrepareResume(
            100,
            capture.Generation,
            0).Subscription!;

        Assert.True(registry.RemoveChannel(100));
        var notification = await subscription.Reader.ReadAsync();

        Assert.Equal(OverlayTransportProtocol.ChatChannelUnavailable,
            notification.EventType);
        Assert.False(registry.IsActive(100));
        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task AuthorizedSubscribers_FanOutIndependently()
    {
        var registry = new ActiveChatStreamRegistry();
        var capture = registry.Activate(Descriptor(100));
        registry.CompleteBootstrap(capture, Array.Empty<ChatMessage>());
        var first = registry.PrepareResume(100, capture.Generation, 0).Subscription!;
        var second = registry.PrepareResume(100, capture.Generation, 0).Subscription!;

        registry.PublishUpsert(
            OverlayTransportProtocol.ChatMessageCreate,
            Message(1, 100));
        var firstEvent = await first.Reader.ReadAsync();
        var secondEvent = await second.Reader.ReadAsync();

        Assert.Equal(firstEvent, secondEvent);
        await first.DisposeAsync();
        registry.PublishDelete(100, 1);
        Assert.Equal(OverlayTransportProtocol.ChatMessageDelete,
            (await second.Reader.ReadAsync()).EventType);
        await second.DisposeAsync();
    }

    [Fact]
    public void Journal_IsBoundedAndOldCursorRequiresResync()
    {
        var registry = new ActiveChatStreamRegistry();
        var capture = registry.Activate(Descriptor(100));
        registry.CompleteBootstrap(capture, Array.Empty<ChatMessage>());
        for (var index = 1; index <= ActiveChatStreamRegistry.JournalCapacity + 1; index++)
        {
            registry.PublishDelete(100, (ulong)index);
        }

        var result = registry.PrepareResume(100, capture.Generation, 0);

        Assert.Equal(ChatResumeDisposition.HistoryExpired, result.Disposition);
    }

    [Fact]
    public void ActiveChannelRegistry_EnforcesSixteenChannelCap()
    {
        var registry = new ActiveChatStreamRegistry();
        for (ulong channelId = 1; channelId <= ActiveChatStreamRegistry.MaximumActiveChannels; channelId++)
        {
            registry.Activate(Descriptor(channelId));
        }

        registry.Activate(Descriptor(99));

        Assert.Equal(ActiveChatStreamRegistry.MaximumActiveChannels,
            registry.ActiveChannelCount);
    }

    [Fact]
    public void IdleChannel_IsEvictedAfterTenMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new ActiveChatStreamRegistry(() => now);
        registry.Activate(Descriptor(100));
        now += ActiveChatStreamRegistry.IdleLifetime + TimeSpan.FromSeconds(1);

        registry.EvictIdle();

        Assert.False(registry.IsActive(100));
    }

    [Fact]
    public async Task UpdateCoalescer_UsesOneRefreshPerMessageBurst()
    {
        var calls = 0;
        var coalescer = new CanonicalMessageRefreshCoalescer(
            (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            _ => { });

        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => coalescer.RequestAsync(100, 200)));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void GatewayPolicy_AddsPollsWithoutGuildMembersIntent()
    {
        Assert.True(DiscordGatewayPolicy.RequiredIntents.HasFlag(
            GatewayIntents.GuildMessagePolls));
        Assert.False(DiscordGatewayPolicy.RequiredIntents.HasFlag(
            GatewayIntents.GuildMembers));
        Assert.False(DiscordGatewayPolicy.CreateSocketConfiguration().AlwaysDownloadUsers);
    }

    [Fact]
    public void Protocol_RoundTripsRichChatMessageAndUnknownComponent()
    {
        var message = Message(1, 100) with
        {
            Components = new[]
            {
                new ChatComponent(
                    "Unknown",
                    999,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<ChatComponent>(),
                    Array.Empty<ChatComponentOption>(),
                    "{\"type\":999}"),
            },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            message,
            OverlayProtocolJson.Options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(
            json,
            OverlayProtocolJson.Options);

        Assert.Equal(999, restored!.Components[0].RawType);
        Assert.Equal("{\"type\":999}", restored.Components[0].UnknownPayload);
    }

    [Fact]
    public void ForwardSnapshotContract_CannotFabricateAnAuthor()
    {
        Assert.DoesNotContain(typeof(ChatForwardSnapshot).GetProperties(),
            property => property.Name.Contains("Author", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonProductionIngressAdapter_MapsRemoteBootstrapIntoCore()
    {
        var rich = Message(1, 100) with
        {
            Attachments = new[]
            {
                new ChatAttachment(
                    5,
                    "voice.ogg",
                    "https://cdn.example/voice.ogg",
                    "https://proxy.example/voice.ogg",
                    42,
                    "audio/ogg",
                    null,
                    null,
                    "voice",
                    null,
                    false,
                    1.5,
                    "AQID",
                    true),
            },
            Components = new[]
            {
                new ChatComponent(
                    "TextDisplay",
                    10,
                    1,
                    null,
                    null,
                    "component text",
                    null,
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<ChatComponent>(),
                    Array.Empty<ChatComponentOption>()),
            },
            Poll = new ChatPoll(
                "question",
                new[] { new ChatPollAnswer(1, "answer", null, 2, false) },
                DateTimeOffset.UtcNow.AddHours(1),
                false,
                "Default",
                false),
        };
        var pipeline = new DiscordMessagePipeline();
        await using var client = new LSOverlayRemoteClient(new Uri("http://127.0.0.1:1"));
        using var adapter = new RemoteChatIngressAdapter(
            pipeline,
            client,
            1,
            "10");
        var bootstrap = new ChatBootstrapResponse(
            OverlayTransportProtocol.Version,
            Descriptor(100),
            "generation",
            0,
            new[] { rich });
        var callback = typeof(RemoteChatIngressAdapter).GetMethod(
            "OnChannelReady",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!;

        callback.Invoke(adapter, new object[] { bootstrap });

        var mapped = Assert.Single(pipeline.Current.MainChat);
        Assert.True(mapped.Attachments[0].IsVoiceMessage);
        Assert.Equal("AQID", mapped.Attachments[0].WaveformBase64);
        Assert.Equal("component text", mapped.RemoteMetadata!.Components[0].Content);
        Assert.Equal("question", mapped.RemoteMetadata.Poll!.Question);
    }

    [Fact]
    public void M93Helper_ClearsBotTokenBeforeProbeAndDocumentsExactCommand()
    {
        var helper = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tools",
            "dev",
            "run-ls-m93-local.ps1"));
        var documentation = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "docs",
            "architecture",
            "M9.3-authenticated-remote-chat.md"));
        var clear = helper.IndexOf(
            "SetEnvironmentVariable($tokenName, $null, 'Process')",
            StringComparison.Ordinal);
        var probe = helper.IndexOf(
            "& dotnet run --project $probeProject",
            StringComparison.Ordinal);

        Assert.Contains("-AsSecureString", helper, StringComparison.Ordinal);
        Assert.Contains("finally", helper, StringComparison.Ordinal);
        Assert.True(clear >= 0 && clear < probe);
        Assert.Contains(
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \".\\tools\\dev\\run-ls-m93-local.ps1\"",
            documentation,
            StringComparison.Ordinal);
    }

    private static AuthenticatedClientIdentity Identity() => new(
        Guid.NewGuid(),
        10,
        1);

    private static ChatChannelDescriptor Descriptor(ulong channelId) => new(
        1,
        channelId,
        $"channel-{channelId}",
        (int)channelId,
        false);

    private static ChatChannelSnapshot Channel(
        ulong channelId,
        params ChatPermissionOverwrite[] overwrites) => new(
        Descriptor(channelId),
        overwrites);

    private static ChatMessage Message(
        ulong id,
        ulong channelId,
        string content = "message",
        DateTimeOffset? createdAt = null) => new(
        id,
        1,
        channelId,
        "Default",
        0,
        new ChatAuthor(10, "user", "User", "Nick", false, false),
        content,
        createdAt ?? DateTimeOffset.UtcNow,
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

    private sealed class FakeSource : IChatDiscordSource
    {
        private readonly ChatGuildSnapshot _guild;
        private readonly TimeSpan _delay;
        private int _guildRequests;

        private FakeSource(ChatGuildSnapshot guild, TimeSpan delay)
        {
            _guild = guild;
            _delay = delay;
        }

        public ChatSourceStatus GuildStatus { get; set; } = ChatSourceStatus.Available;
        public int GuildRequests => Volatile.Read(ref _guildRequests);

        public static FakeSource WithGuild(
            IReadOnlyCollection<ChatChannelSnapshot>? channels = null,
            TimeSpan delay = default) => new(
            new ChatGuildSnapshot(
                1,
                new[] { new ChatRolePermission(1, Read) },
                new ChatMemberSnapshot(10, Array.Empty<ulong>()),
                new ChatMemberSnapshot(99, Array.Empty<ulong>()),
                channels ?? new[] { Channel(100) }),
            delay);

        public async Task<ChatGuildSourceResult> GetGuildAsync(
            AuthenticatedClientIdentity identity,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _guildRequests);
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            return GuildStatus == ChatSourceStatus.Available
                ? new ChatGuildSourceResult(GuildStatus, _guild)
                : new ChatGuildSourceResult(GuildStatus, null);
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

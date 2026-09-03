using System.Text.Json;
using System.Threading.Channels;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Providers;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Settings;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace GachaOverlay.Tests.Backend;

public sealed partial class M94ProductionRemoteModeTests
{
    [Fact]
    public void Settings_DefaultToRemoteEndpointAndNeverContainRemoteToken()
    {
        var settings = AppSettings.CreateDefault();

        Assert.Equal("https://overlay.revo32.cloud", settings.RemoteBackendBaseUrl);
        Assert.DoesNotContain(
            "token",
            JsonSerializer.Serialize(settings),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_MigrateRemoteFieldsAndPreserveUnknownFields()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 11,
              "chatSource": 1,
              "remoteBackendBaseUrl": "https://overlay.example/ignored/path",
              "remoteSelectedChannelId": "1234",
              "futureValue": { "kept": true }
            }
            """);
        var store = new JsonSettingsStore(path, NullAppLogger.Instance);

        var settings = store.Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("https://overlay.example", settings.RemoteBackendBaseUrl);
        Assert.Equal("1234", settings.RemoteSelectedChannelId);
        Assert.True(settings.ExtensionData?.ContainsKey("futureValue"));
        Assert.False(settings.ExtensionData?.ContainsKey("chatSource"));
    }

    [Theory]
    [InlineData("http://public.example")]
    [InlineData("file:///tmp/backend")]
    [InlineData("not-a-uri")]
    public void Settings_RejectUnsafeRemoteEndpoints(string endpoint)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, $$"""
            { "schemaVersion": 12, "remoteBackendBaseUrl": "{{endpoint}}" }
            """);
        var store = new JsonSettingsStore(path, NullAppLogger.Instance);

        Assert.Equal("https://overlay.revo32.cloud", store.Load().RemoteBackendBaseUrl);
    }

    [Fact]
    public void DpapiRemoteStore_RoundTripsWithoutPlaintext()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote-access-token.dat");
        var store = new DpapiRemoteAccessCredentialStore(path, NullAppLogger.Instance);
        const string token = "m94-secret-access-token";

        Assert.True(store.Save(token));
        Assert.True(store.TryLoad(out var restored));
        Assert.Equal(token, restored);
        Assert.Equal(RemoteCredentialStatus.Available, store.Status);
        Assert.DoesNotContain(token, Convert.ToBase64String(File.ReadAllBytes(path)));
        Assert.True(store.Clear());
        Assert.Equal(RemoteCredentialStatus.Missing, store.Status);
    }

    [Fact]
    public async Task Coordinator_RemoteBootstrapsLatestTwentyAndPublishesLiveMutation()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
        });
        var fake = new FakeRemoteClient(messageCount: 25);
        var states = new List<DiscordMessageState>();
        await using var coordinator = new RemoteChatProductionCoordinator(
            store,
            new MemoryCredentialStore("token"),
            new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance,
            _ => fake);
        coordinator.MessageStateChanged += state => states.Add(state);

        coordinator.Start();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);

        var live = states.Last(state => !state.IsBootstrapping);
        Assert.Equal(20, live.MainChat.Count);
        Assert.Equal("25", live.MainChat[^1].MessageId);

        fake.PublishCreate(Message(26, 100, "live"));
        await WaitUntilAsync(() => states.Last().MainChat.Any(message => message.MessageId == "26"));
        Assert.Equal(20, states.Last().MainChat.Count);
    }

    [Fact]
    public async Task Coordinator_ChannelSwitchCommitsOnlyAfterReady()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
        });
        var fake = new FakeRemoteClient(delaySwitchReady: true);
        await using var coordinator = new RemoteChatProductionCoordinator(
            store,
            new MemoryCredentialStore("token"),
            new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance,
            _ => fake);
        coordinator.Start();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);

        Assert.True(await coordinator.SwitchChannelAsync("200"));
        await fake.SwitchRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("100", store.Current.RemoteSelectedChannelId);

        fake.CompleteSwitch();
        await WaitUntilAsync(() => store.Current.RemoteSelectedChannelId == "200");
        Assert.Equal("200", coordinator.Snapshot.SelectedChannelId);
    }

    [Fact]
    public async Task Coordinator_NetworkFailurePreservesCredentialAndRejectsInvalidEndpoint()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
        });
        var credential = new MemoryCredentialStore("token");
        await using var coordinator = new RemoteChatProductionCoordinator(
            store,
            credential,
            new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance,
            _ => new FakeRemoteClient(failBootstrap: true));
        coordinator.Start();

        await WaitUntilAsync(() =>
            coordinator.Snapshot.Health == RemoteChatHealthState.Reconnecting);
        Assert.Equal("token", credential.Value);

        Assert.False(await coordinator.ApplyConfigurationAsync("invalid-remote-address"));
        Assert.Equal("token", credential.Value);
    }

    [Fact]
    public async Task Coordinator_StaleChannelSelectionIsClearedAgainstAuthorizedCatalog()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "999",
        });
        await using var coordinator = new RemoteChatProductionCoordinator(
            store,
            new MemoryCredentialStore("token"),
            new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance,
            _ => new FakeRemoteClient());

        coordinator.Start();
        await WaitUntilAsync(() =>
            coordinator.Snapshot.Health == RemoteChatHealthState.ChannelSelectionRequired);

        Assert.Null(store.Current.RemoteSelectedChannelId);
        Assert.Null(coordinator.Snapshot.SelectedChannelId);
        Assert.Equal(2, coordinator.Snapshot.Channels.Count);
    }

    [Fact]
    public async Task Coordinator_AuthenticationRejectionDoesNotEraseCredential()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
        });
        var credential = new MemoryCredentialStore("revoked-token");
        await using var coordinator = new RemoteChatProductionCoordinator(
            store,
            credential,
            new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance,
            _ => new FakeRemoteClient(rejectAuthentication: true));

        coordinator.Start();
        await WaitUntilAsync(() =>
            coordinator.Snapshot.Health == RemoteChatHealthState.AccessRevoked);

        Assert.Equal("revoked-token", credential.Value);
        Assert.True(coordinator.Snapshot.HasProtectedCredential);
    }

    [Fact]
    public async Task Coordinator_WebLoginStoresApprovedTokenAndStartsRemoteSession()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
        });
        var credential = new MemoryCredentialStore();
        var loginClient = new FakeRemoteClient(loginToken: "approved-token");
        var sessionClient = new FakeRemoteClient();
        var created = 0;
        await using var coordinator = new RemoteChatProductionCoordinator(
            store,
            credential,
            new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance,
            _ => Interlocked.Increment(ref created) == 1 ? loginClient : sessionClient, openBrowser: _ => { });

        await coordinator.BeginLoginAsync();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);

        Assert.Equal("approved-token", credential.Value);
        Assert.True(coordinator.Snapshot.HasProtectedCredential);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Coordinator_ReauthAfterRejectedCredentialsRestoresRecentTwentyAndLiveMessages(
        int rejectedAttempts)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
        });
        var pipeline = new DiscordMessagePipeline();
        var credential = new MemoryCredentialStore("old-backend-token");
        var sessionClient = new FakeRemoteClient(messageCount: 25);
        var clients = new Queue<FakeRemoteClient>(Enumerable.Range(0, rejectedAttempts)
            .Select(_ => new FakeRemoteClient(rejectAuthentication: true)));
        clients.Enqueue(new FakeRemoteClient(loginToken: "new-backend-token"));
        clients.Enqueue(sessionClient);
        await using var coordinator = new RemoteChatProductionCoordinator(
            store, credential, pipeline, Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance, _ => clients.Dequeue(), openBrowser: _ => { });

        coordinator.Start();
        for (var attempt = 0; attempt < rejectedAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await coordinator.RefreshAsync();
            }

            await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.AccessRevoked);
            Assert.Empty(pipeline.Current.MainChat);
        }
        var revokedGeneration = pipeline.Current.Generation;

        await coordinator.BeginLoginAsync();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);

        Assert.Equal("new-backend-token", credential.Value);
        Assert.True(pipeline.Current.Generation > revokedGeneration);
        Assert.Equal(20, pipeline.Current.MainChat.Count);
        Assert.Equal("25", pipeline.Current.MainChat[^1].MessageId);
        sessionClient.PublishCreate(Message(26, 100, "restored"));
        sessionClient.PublishUpdate(Message(26, 100, "edited"));
        Assert.Equal("edited", pipeline.Current.MainChat[^1].Content);
        sessionClient.PublishDelete(26, 100);
        Assert.DoesNotContain(pipeline.Current.MainChat, message => message.MessageId == "26");
    }

    [Fact]
    public async Task Coordinator_AccessRevokedRejectsOldCallbacksAndRecoversAfterReauthAndFiveRestarts()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
        });
        var pipeline = new DiscordMessagePipeline();
        var credential = new MemoryCredentialStore("original-token");
        var oldClient = new FakeRemoteClient(messageCount: 20);
        var clients = new Queue<FakeRemoteClient>();
        clients.Enqueue(oldClient);
        clients.Enqueue(new FakeRemoteClient(loginToken: "repaired-token"));
        foreach (var _ in Enumerable.Range(0, 6))
        {
            clients.Enqueue(new FakeRemoteClient(messageCount: 20));
        }
        await using var coordinator = new RemoteChatProductionCoordinator(
            store, credential, pipeline, Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance, _ => clients.Dequeue(), openBrowser: _ => { });
        coordinator.Start();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);

        oldClient.PublishStatus(100, OverlayTransportProtocol.ChatAccessRevoked);
        var generation = pipeline.Current.Generation;
        Assert.Empty(pipeline.Current.MainChat);
        oldClient.PublishReady(Message(99, 100, "stale snapshot"));
        oldClient.PublishCreate(Message(100, 100, "stale live"));
        Assert.Empty(pipeline.Current.MainChat);
        Assert.Equal(RemoteChatHealthState.AccessRevoked, coordinator.Snapshot.Health);

        await coordinator.BeginLoginAsync();
        for (var cycle = 0; cycle <= 5; cycle++)
        {
            if (cycle > 0)
            {
                await coordinator.RefreshAsync();
            }
            await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);
            Assert.True(pipeline.Current.Generation > generation);
            generation = pipeline.Current.Generation;
            Assert.Equal(20, pipeline.Current.MainChat.Count);
            Assert.Equal(20, pipeline.Current.MainChat.Select(message => message.MessageId).Distinct().Count());
            Assert.Equal("repaired-token", credential.Value);
        }
    }

    [Fact]
    public async Task Coordinator_ForgetCredentialThenLoginAndSelectChannelRestoresMessages()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
        });
        var pipeline = new DiscordMessagePipeline();
        var clients = new Queue<FakeRemoteClient>(new[]
        {
            new FakeRemoteClient(messageCount: 20),
            new FakeRemoteClient(loginToken: "repaired-token"),
            new FakeRemoteClient(),
            new FakeRemoteClient(messageCount: 20),
        });
        await using var coordinator = new RemoteChatProductionCoordinator(
            store, new MemoryCredentialStore("token"), pipeline, Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance, _ => clients.Dequeue(), openBrowser: _ => { });
        coordinator.Start();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);

        Assert.True(await coordinator.ForgetCredentialAsync());
        Assert.Empty(pipeline.Current.MainChat);
        var revokedGeneration = pipeline.Current.Generation;
        await coordinator.BeginLoginAsync();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.ChannelSelectionRequired);
        Assert.True(await coordinator.SwitchChannelAsync("100"));
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);

        Assert.True(pipeline.Current.Generation > revokedGeneration);
        Assert.Equal(20, pipeline.Current.MainChat.Count);
    }

    [Fact]
    public async Task Coordinator_RevocationDuringBootstrapDoesNotPublishStaleDataOrReportLive()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
        });
        var pipeline = new DiscordMessagePipeline();
        var fake = new FakeRemoteClient(
            messageCount: 20,
            beforeChatBootstrap: pipeline.ClearForAccessRevocation);
        await using var coordinator = new RemoteChatProductionCoordinator(
            store, new MemoryCredentialStore("token"), pipeline, Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance, _ => fake);

        coordinator.Start();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Reconnecting);

        Assert.Empty(pipeline.Current.MainChat);
        Assert.False(fake.StreamStarted);
    }

    [Fact]
    public void RemoteIngress_ProjectsForwardedTextMediaReplyPollAndReadOnlyComponents()
    {
        var pipeline = new DiscordMessagePipeline();
        var fake = new FakeRemoteClient();
        using var adapter = new RemoteChatIngressAdapter(pipeline, fake, 1, "7");
        var source = Message(1, 100, string.Empty) with
        {
            ForwardedSnapshots = new[]
            {
                new ChatForwardSnapshot(
                    "Default",
                    "forwarded text",
                    DateTimeOffset.UnixEpoch,
                    null,
                    new[]
                    {
                        new ChatAttachment(
                            90,
                            "forward.png",
                            "https://cdn.example/forward.png",
                            "https://proxy.example/forward.png",
                            10,
                            "image/png",
                            100,
                            80,
                            null,
                            null,
                            false,
                            null,
                            null,
                            false),
                    },
                    Array.Empty<ChatEmbed>(),
                    Array.Empty<ChatMention>(),
                    new[] { new ChatSticker(91, "wave", "Png", "https://cdn.example/wave.png") },
                    Array.Empty<ChatComponent>()),
            },
            Reference = new ChatMessageReference("Reply", 10, 100, 99, null),
            Components = new[]
            {
                new ChatComponent(
                    "Button",
                    2,
                    1,
                    "read-only",
                    "Open",
                    null,
                    null,
                    "https://example.com",
                    null,
                    true,
                    false,
                    Array.Empty<ChatComponent>(),
                    Array.Empty<ChatComponentOption>()),
            },
            Poll = new ChatPoll(
                "Choose",
                new[] { new ChatPollAnswer(1, "A", null, 2, false) },
                DateTimeOffset.UtcNow.AddHours(1),
                false,
                "Default",
                false),
        };

        fake.PublishReady(source);

        var normalized = Assert.Single(pipeline.Current.MainChat);
        Assert.Equal(string.Empty, normalized.Content);
        Assert.Empty(normalized.Attachments);
        Assert.Empty(normalized.Stickers);
        Assert.NotNull(normalized.RemoteMetadata?.Reply);
        Assert.NotNull(normalized.RemoteMetadata?.Poll);
        Assert.Single(normalized.RemoteMetadata!.Components);
        var snapshot = Assert.Single(normalized.RemoteMetadata.ForwardedSnapshots);
        Assert.Equal("forwarded text", snapshot.Content);
        Assert.Single(snapshot.Attachments);
        Assert.Single(snapshot.Stickers);
        var presentation = Assert.Single(
            new ChatPresentationSynchronizer()
                .Synchronize(pipeline.Current, "7"))
            .Message;
        Assert.NotNull(presentation);
        Assert.Equal(string.Empty, presentation!.PlainText);
        Assert.Empty(presentation.Media);
        var forwarded = Assert.Single(presentation.ForwardedMessages);
        Assert.Equal("forwarded text", forwarded.Text);
        Assert.Single(forwarded.Media);
        Assert.Single(forwarded.Stickers);
        Assert.NotNull(presentation.RemoteMetadata?.Poll);
    }

    [Fact]
    public void RemoteIngress_UpdateKeepsOrderAndDeleteRemovesExactMessage()
    {
        var pipeline = new DiscordMessagePipeline();
        var fake = new FakeRemoteClient(messageCount: 2);
        using var adapter = new RemoteChatIngressAdapter(pipeline, fake, 1, "7");
        fake.PublishReady(Message(1, 100, "first"), Message(2, 100, "second"));

        fake.PublishUpdate(Message(1, 100, "edited"));

        Assert.Equal(new[] { "1", "2" }, pipeline.Current.MainChat.Select(x => x.MessageId));
        Assert.Equal("edited", pipeline.Current.MainChat[0].Content);

        fake.PublishDelete(1, 100);
        var remaining = Assert.Single(pipeline.Current.MainChat);
        Assert.Equal("2", remaining.MessageId);
    }

    private static JsonSettingsStore CreateStore(
        TemporaryDirectory directory,
        AppSettings settings)
    {
        var store = new JsonSettingsStore(
            Path.Combine(directory.Path, "settings.json"),
            NullAppLogger.Instance);
        Assert.True(store.Save(settings));
        return store;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("Expected remote state was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private static ChatMessage Message(ulong id, ulong channelId, string content) => new(
        id,
        10,
        channelId,
        "Default",
        0,
        new ChatAuthor(7, "user", "Display", "Guild Nick", false, false),
        content,
        DateTimeOffset.UnixEpoch.AddSeconds((long)id),
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

    private sealed class MemoryCredentialStore : IRemoteAccessCredentialStore
    {
        public MemoryCredentialStore(string? value = null) => Value = value;

        public string? Value { get; private set; }

        public RemoteCredentialStatus Status => string.IsNullOrWhiteSpace(Value)
            ? RemoteCredentialStatus.Missing
            : RemoteCredentialStatus.Available;

        public bool TryLoad(out string? accessToken)
        {
            accessToken = Value;
            return !string.IsNullOrWhiteSpace(Value);
        }

        public bool Save(string accessToken)
        {
            Value = accessToken;
            return true;
        }

        public bool Clear()
        {
            Value = null;
            return true;
        }
    }

    [Fact]
    public async Task RecoveryAudit_RequiresCompleteSalesAndDropsReadinessOnDegradation()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
            SalesTrackingEnabled = true,
        });
        using var audit = new RemoteRecoveryAudit("test");
        var fake = new FakeRecoveryRemoteClient();
        await using var coordinator = new RemoteChatProductionCoordinator(
            store, new MemoryCredentialStore("token"), new DiscordMessagePipeline(),
            Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance, _ => fake, audit);
        coordinator.Start();
        await WaitUntilAsync(() => audit.Current.ChatStreamReady && audit.Current.PresenceStreamLive);
        Assert.False(audit.Current.Ready);
        fake.PublishSales(SalesBootstrapCoverage.Truncated);
        Assert.False(audit.Current.Ready);
        fake.PublishSales(SalesBootstrapCoverage.Complete);
        Assert.True(audit.Current.Ready);
        fake.PublishSales(SalesBootstrapCoverage.Unavailable);
        Assert.False(audit.Current.Ready);
        fake.PublishSales(SalesBootstrapCoverage.Complete);
        Assert.True(audit.Current.Ready);
        fake.PublishSalesStatus(OverlayTransportProtocol.SalesFailed);
        Assert.False(audit.Current.Ready);
        fake.PublishSales(SalesBootstrapCoverage.Complete);
        fake.PublishStatus(100, OverlayTransportProtocol.ChatAuthorizationUnavailable);
        Assert.False(audit.Current.Ready);
        fake.PublishStatus(100, OverlayTransportProtocol.ChatAccessRevoked);
        fake.PublishReady(Message(99, 100, "stale"));
        Assert.True(audit.Current.AuthenticationRequired);
        Assert.False(audit.Current.Ready);
    }

    [Fact]
    public async Task RecoveryAudit_Presence503RetriesAutomaticallyWithoutReauthAndLogsSafePhase()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
            SalesTrackingEnabled = true,
        });
        using var audit = new RemoteRecoveryAudit("test");
        var unavailable = new FakeRecoveryRemoteClient(new HttpRequestException(
            "private-bootstrap-body", null, System.Net.HttpStatusCode.ServiceUnavailable));
        var recovered = new FakeRecoveryRemoteClient();
        var clients = new Queue<FakeRecoveryRemoteClient>(new[] { unavailable, recovered });
        var credentials = new MemoryCredentialStore("private-remote-credential");
        var logger = new RecoveryLogger();
        var pipeline = new DiscordMessagePipeline();
        await using var coordinator = new RemoteChatProductionCoordinator(
            store, credentials, pipeline, Path.Combine(directory.Path, "install.txt"),
            logger, _ => clients.Dequeue(), audit);
        coordinator.Start();
        await WaitUntilAsync(() => audit.Current.Attempt >= 2 && audit.Current.ChatStreamReady);
        recovered.PublishSales(SalesBootstrapCoverage.Complete);
        await WaitUntilAsync(() => audit.Current.Ready);
        Assert.Equal("private-remote-credential", credentials.Value);
        Assert.Equal(20, pipeline.Current.MainChat.Count);
        var warnings = string.Join("\n", logger.Warnings);
        Assert.Contains("phase=PresenceBootstrap http_status=503", warnings, StringComparison.Ordinal);
        Assert.DoesNotContain("private-bootstrap-body", warnings, StringComparison.Ordinal);
        Assert.DoesNotContain("private-remote-credential", warnings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryAudit_FiveRefreshedSessionsRequireFreshCompleteEvidence()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with
        {
            RemoteSelectedChannelId = "100",
            SalesTrackingEnabled = true,
        });
        using var audit = new RemoteRecoveryAudit("test");
        var fakes = Enumerable.Range(0, 6).Select(_ => new FakeRecoveryRemoteClient()).ToArray();
        var clients = new Queue<FakeRecoveryRemoteClient>(fakes);
        var credentials = new MemoryCredentialStore("token");
        var pipeline = new DiscordMessagePipeline();
        await using var coordinator = new RemoteChatProductionCoordinator(
            store, credentials, pipeline, Path.Combine(directory.Path, "install.txt"),
            NullAppLogger.Instance, _ => clients.Dequeue(), audit);
        coordinator.Start();
        string? epoch = null;
        long attempt = 0;
        for (var cycle = 0; cycle <= 5; cycle++)
        {
            if (cycle > 0) { await coordinator.RefreshAsync(); }
            await WaitUntilAsync(() => audit.Current.Attempt > attempt && audit.Current.ChatStreamReady);
            Assert.False(audit.Current.Ready);
            if (cycle > 0)
            {
                Assert.True(fakes[cycle - 1].Disposed);
                Assert.Equal(0, fakes[cycle - 1].SubscriberCount);
                fakes[cycle - 1].PublishSales(SalesBootstrapCoverage.Complete);
                Assert.False(audit.Current.Ready);
            }
            fakes[cycle].PublishSales(SalesBootstrapCoverage.Complete);
            await WaitUntilAsync(() => audit.Current.Ready);
            Assert.NotEqual(epoch, audit.Current.BackendEpoch);
            epoch = audit.Current.BackendEpoch;
            attempt = audit.Current.Attempt;
            Assert.Equal(20, pipeline.Current.MainChat.Count);
            Assert.Equal("token", credentials.Value);
        }
    }

    [Fact]
    public async Task M912_RefreshCancelsOldChannelRequestBeforeDisposingClient()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with { RemoteSelectedChannelId = "100" });
        var old = new FakeRemoteClient();
        var next = new FakeRemoteClient();
        var clients = new Queue<FakeRemoteClient>(new[] { old, next });
        await using var coordinator = new RemoteChatProductionCoordinator(store, new MemoryCredentialStore("token"),
            new DiscordMessagePipeline(), Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance,
            _ => clients.Dequeue());
        coordinator.Start();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settledBeforeDispose = false;
        old.BootstrapOverride = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { settledBeforeDispose = !old.Disposed; }
            throw new InvalidOperationException("Cancellation expected");
        };
        var switching = coordinator.SwitchChannelAsync("200");
        await started.Task;
        await coordinator.RefreshAsync();
        Assert.False(await switching);
        Assert.True(settledBeforeDispose);
        Assert.True(old.Disposed);
        Assert.Equal(0, old.SubscriberCount);
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.Live);
        Assert.Equal("100", coordinator.Snapshot.SelectedChannelId);
    }

    [Fact]
    public async Task M912_StaleChatBootstrapCancelsAndJoinsSiblingPresence()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault() with { RemoteSelectedChannelId = "100" });
        var pipeline = new DiscordMessagePipeline();
        var old = new FakeRemoteClient(beforeChatBootstrap: pipeline.ClearForAccessRevocation);
        var settled = false;
        old.PresenceOverride = async token =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { settled = true; }
            throw new InvalidOperationException("Cancellation expected");
        };
        await using var coordinator = new RemoteChatProductionCoordinator(store, new MemoryCredentialStore("token"),
            pipeline, Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance, _ => old);
        coordinator.Start();
        await WaitUntilAsync(() => old.Disposed);
        Assert.True(settled);
        Assert.Equal(0, old.SubscriberCount);
        Assert.False(old.StreamStarted);
        Assert.Empty(pipeline.Current.MainChat);
    }

    [Fact]
    public async Task M912_RestartAfterNaturalCompletionDisposesPreviousLinkedCts()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, AppSettings.CreateDefault());
        await using var coordinator = new RemoteChatProductionCoordinator(store, new MemoryCredentialStore("token"),
            new DiscordMessagePipeline(), Path.Combine(directory.Path, "install.txt"), NullAppLogger.Instance,
            _ => new FakeRemoteClient());
        coordinator.Start();
        await WaitUntilAsync(() => coordinator.Snapshot.Health == RemoteChatHealthState.ChannelSelectionRequired);
        var field = typeof(RemoteChatProductionCoordinator).GetField("_sessionCancellation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var previous = Assert.IsAssignableFrom<CancellationTokenSource>(field.GetValue(coordinator));
        coordinator.Start();
        Assert.Throws<ObjectDisposedException>(() => previous.Token);
    }

    private sealed class FakeRecoveryRemoteClient : FakeRemoteClient, ILSOverlayRemoteSalesClient
    {
        public FakeRecoveryRemoteClient(Exception? presenceFailure = null)
            : base(messageCount: 20, presenceGeneration: Guid.NewGuid().ToString("N"), presenceFailure: presenceFailure) { }

        public event Action<SalesBootstrapResponse>? SalesReady;
        public event Action<SalesMutationEnvelope>? SalesMutationReceived { add { } remove { } }
        public event Action<string>? SalesStreamStatusChanged;

        public void PublishSales(SalesBootstrapCoverage coverage) => SalesReady?.Invoke(SalesBootstrap(coverage));
        public void PublishSalesStatus(string status) => SalesStreamStatusChanged?.Invoke(status);

        public Task<SalesBootstrapResponse> GetSalesBootstrapAsync(string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(SalesBootstrap(SalesBootstrapCoverage.Complete));

        public Task<SalesStatusActionResponse> SetSalesStatusAsync(string accessToken, SalesStatusActionRequest request,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Audit must not write to Discord.");

        public Task StreamChatAndSalesAsync(string accessToken, BootstrapResponse presenceBootstrap,
            ChatBootstrapResponse initialChatBootstrap, SalesBootstrapResponse salesBootstrap,
            ChannelReader<ChatBootstrapResponse> channelSwitches, ChannelReader<SalesBootstrapResponse> salesResyncs,
            CancellationToken cancellationToken = default) =>
            StreamChatAsync(accessToken, presenceBootstrap, initialChatBootstrap, channelSwitches, cancellationToken);

        private static SalesBootstrapResponse SalesBootstrap(SalesBootstrapCoverage coverage) => new(
            OverlayTransportProtocol.Version, new ChatChannelDescriptor(10, 300, "sales", 1, false),
            "sales-generation", 0, Array.Empty<ChatMessage>(), Array.Empty<SalesCompletionObservation>(), coverage);
    }

    private sealed class RecoveryLogger : IAppLogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Warnings { get; } = new();
        public void Information(string category, string message) { }
        public void Warning(string category, string message) => Warnings.Enqueue(message);
        public void Error(string category, string message, Exception? exception = null) { }
    }

    private class FakeRemoteClient : ILSOverlayRemoteClient, ILSOverlayDiscordWebAuthClient
    {
        private readonly int _messageCount;
        private readonly bool _failBootstrap;
        private readonly bool _delaySwitchReady;
        private readonly bool _rejectAuthentication;
        private readonly string? _loginToken;
        private readonly Action? _beforeChatBootstrap;
        private readonly string _presenceGeneration;
        private readonly Exception? _presenceFailure;
        private ChatBootstrapResponse? _pendingSwitch;

        public FakeRemoteClient(
            int messageCount = 1,
            bool failBootstrap = false,
            bool delaySwitchReady = false,
            string? loginToken = null,
            bool rejectAuthentication = false,
            Action? beforeChatBootstrap = null,
            string presenceGeneration = "presence",
            Exception? presenceFailure = null)
        {
            _messageCount = messageCount;
            _failBootstrap = failBootstrap;
            _delaySwitchReady = delaySwitchReady;
            _loginToken = loginToken;
            _rejectAuthentication = rejectAuthentication;
            _beforeChatBootstrap = beforeChatBootstrap;
            _presenceGeneration = presenceGeneration;
            _presenceFailure = presenceFailure;
        }

        public IReadOnlyList<ChatChannelDescriptor>? ChannelCatalogOverride { get; set; }
        public bool StreamStarted { get; private set; }
        public bool Disposed { get; private set; }
        public int SubscriberCount => (StreamLive?.GetInvocationList().Length ?? 0) +
            (ChatChannelReady?.GetInvocationList().Length ?? 0) +
            (ChatMutationReceived?.GetInvocationList().Length ?? 0) +
            (ChatStreamStatusChanged?.GetInvocationList().Length ?? 0);
        public Func<ulong, CancellationToken, Task<ChatBootstrapResponse>>? BootstrapOverride { get; set; }
        public Func<CancellationToken, Task<BootstrapResponse>>? PresenceOverride { get; set; }

        public TaskCompletionSource SwitchRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action? StreamLive;
        public event Action<HostPresenceSnapshot>? HostPresenceChanged
        {
            add { }
            remove { }
        }
        public event Action<ChatBootstrapResponse>? ChatChannelReady;
        public event Action<ChatMutationEnvelope>? ChatMutationReceived;
        public event Action<ulong, string>? ChatStreamStatusChanged;

        public virtual Task<DiscordWebAuthStartResponse?> StartDiscordWebAuthAsync(Guid installation, CancellationToken cancellationToken = default) =>
            Task.FromResult<DiscordWebAuthStartResponse?>(new(1, Guid.NewGuid(), "synthetic-claim",
                "https://discord.com/oauth2/authorize?scope=identify", DateTimeOffset.UtcNow.AddMinutes(5)));

        public virtual Task<DiscordWebAuthClaimResult> GetDiscordWebAuthStatusAsync(Guid session, string claim, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscordWebAuthClaimResult(1, _loginToken is null ? DiscordWebAuthStatus.Expired : DiscordWebAuthStatus.Approved,
                AccessToken: _loginToken, CredentialExpiresAt: DateTimeOffset.UtcNow.AddDays(180)));

        public virtual Task CancelDiscordWebAuthAsync(Guid session, string claim, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<BootstrapResponse> GetBootstrapAsync(
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            if (PresenceOverride is not null) { return PresenceOverride(cancellationToken); }
            if (_presenceFailure is not null)
            {
                return Task.FromException<BootstrapResponse>(_presenceFailure);
            }
            if (_failBootstrap)
            {
                throw new HttpRequestException("offline");
            }

            if (_rejectAuthentication)
            {
                throw new RemoteAuthenticationRequiredException();
            }

            return Task.FromResult(new BootstrapResponse(
                OverlayTransportProtocol.Version,
                _presenceGeneration,
                0,
                7,
                Array.Empty<HostPresenceSnapshot>()));
        }

        public Task<ChatChannelCatalogResponse> GetChatChannelsAsync(
            string accessToken,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new ChatChannelCatalogResponse(
                    OverlayTransportProtocol.Version,
                    ChannelCatalogOverride ?? new[]
                    {
                        new ChatChannelDescriptor(10, 100, "main", 0, false),
                        new ChatChannelDescriptor(10, 200, "second", 1, false),
                    }));

        public Task<ChatBootstrapResponse> GetChatBootstrapAsync(
            string accessToken,
            ulong channelId,
            CancellationToken cancellationToken = default)
        {
            _beforeChatBootstrap?.Invoke();
            if (BootstrapOverride is not null) { return BootstrapOverride(channelId, cancellationToken); }
            return Task.FromResult(Bootstrap(channelId));
        }

        public async Task StreamChatAsync(
            string accessToken,
            BootstrapResponse presenceBootstrap,
            ChatBootstrapResponse initialChatBootstrap,
            ChannelReader<ChatBootstrapResponse> channelSwitches,
            CancellationToken cancellationToken = default)
        {
            StreamStarted = true;
            ChatChannelReady?.Invoke(initialChatBootstrap);
            StreamLive?.Invoke();
            while (await channelSwitches.WaitToReadAsync(cancellationToken))
            {
                while (channelSwitches.TryRead(out var next))
                {
                    if (_delaySwitchReady)
                    {
                        _pendingSwitch = next;
                        SwitchRequested.TrySetResult();
                    }
                    else
                    {
                        ChatChannelReady?.Invoke(next);
                    }
                }
            }
        }

        public void PublishCreate(ChatMessage message) => ChatMutationReceived?.Invoke(
            new ChatMutationEnvelope(
                OverlayTransportProtocol.Version,
                "chat",
                1,
                OverlayTransportProtocol.ChatMessageCreate,
                message.ChannelId,
                message.MessageId,
                message));

        public void PublishUpdate(ChatMessage message) => ChatMutationReceived?.Invoke(
            new ChatMutationEnvelope(
                OverlayTransportProtocol.Version,
                "chat",
                2,
                OverlayTransportProtocol.ChatMessageUpdate,
                message.ChannelId,
                message.MessageId,
                message));

        public void PublishDelete(ulong messageId, ulong channelId) =>
            ChatMutationReceived?.Invoke(new ChatMutationEnvelope(
                OverlayTransportProtocol.Version,
                "chat",
                3,
                OverlayTransportProtocol.ChatMessageDelete,
                channelId,
                messageId,
                null));

        public void PublishReady(params ChatMessage[] messages) => ChatChannelReady?.Invoke(
            new ChatBootstrapResponse(
                OverlayTransportProtocol.Version,
                new ChatChannelDescriptor(10, 100, "main", 0, false),
                "chat-100",
                messages.Length,
                messages));

        public void PublishStatus(ulong channelId, string status) =>
            ChatStreamStatusChanged?.Invoke(channelId, status);

        public void CompleteSwitch()
        {
            if (_pendingSwitch is not null)
            {
                ChatChannelReady?.Invoke(_pendingSwitch);
                _pendingSwitch = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private ChatBootstrapResponse Bootstrap(ulong channelId) => new(
            OverlayTransportProtocol.Version,
            new ChatChannelDescriptor(10, channelId, channelId == 100 ? "main" : "second", 0, false),
            $"chat-{channelId}",
            (long)_messageCount,
            Enumerable.Range(1, _messageCount)
                .Select(id => Message((ulong)id, channelId, $"message-{id}"))
                .ToArray());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"GachaOverlay-M94-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

using System.Text.RegularExpressions;
using Discord;
using GachaOverlay.Core.Sales;
using GachaOverlay.Tests.Sales;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Sales;
using LSOverlay.Backend.Security;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests.Backend;

public sealed class M981CorrectiveTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void WindowEviction_RemovesOldestWithoutCreatingDeleteTombstone()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplyAuthoritativeWindowSnapshot(Enumerable.Range(1, 30)
            .Select(index => SalesTestFactory.Message(
                index.ToString(),
                seconds: index)));

        engine.ApplyAuthoritativeWindowSnapshot(Enumerable.Range(2, 30)
            .Select(index => SalesTestFactory.Message(
                index.ToString(),
                seconds: index)));

        Assert.Equal(AuthoritativeSalesWindow.Size, engine.Records.Count);
        Assert.DoesNotContain(engine.Records, record => record.MessageId == "1");
        Assert.DoesNotContain(engine.Records, record =>
            record.DomainState == SaleDomainState.Deleted);
        Assert.Equal("2", engine.Records[0].MessageId);
        Assert.Equal("31", engine.Records[^1].MessageId);
    }

    [Fact]
    public void ExactDiscordDelete_RemainsADeletedTombstone()
    {
        var engine = SalesTestFactory.Engine();
        engine.ApplyAuthoritativeWindowSnapshot(new[] { SalesTestFactory.Message("1") });

        engine.ApplySourceDelete("1");

        var record = Assert.Single(engine.Records);
        Assert.Equal(SaleDomainState.Deleted, record.DomainState);
    }

    [Fact]
    public async Task RemoteDelete_RequestsSalesOnlyCanonicalBackfillAfterExactDelete()
    {
        var configuration = Configuration();
        var registry = new ActiveSalesStreamRegistry(configuration);
        var completed = registry.CompleteBootstrap(
            registry.Activate(),
            new[] { Message(1) },
            new[] { Observation(1) });
        var normalizer = new DiscordChatMessageNormalizer(
            new CanonicalRemoteAuthorResolver(new MissingMemberSource()));
        var service = new RemoteSalesService(
            configuration,
            new UnusedAuthorizationService(),
            new UnusedChatSource(),
            normalizer,
            registry);

        service.ReceiveDelete(10, configuration.SalesChannelId, 1);

        var resume = registry.PrepareResume(
            completed.Generation,
            completed.LatestSequence);
        await using var subscription = Assert.IsType<SalesStreamSubscription>(
            resume.Subscription);
        Assert.Equal(
            new[]
            {
                OverlayTransportProtocol.SalesMessageDelete,
                OverlayTransportProtocol.SalesResyncRequired,
            },
            subscription.Replay.Select(item => item.EventType).ToArray());
    }

    [Fact]
    public async Task UpdatedMessage_RetainsOriginalWindowOrderingTimestamp()
    {
        var registry = new ActiveSalesStreamRegistry(Configuration());
        var original = Message(1);
        var completed = registry.CompleteBootstrap(
            registry.Activate(),
            new[] { original },
            new[] { Observation(1) });

        Assert.True(registry.PublishUpsert(
            OverlayTransportProtocol.SalesMessageUpdate,
            original with { CreatedAt = original.CreatedAt.AddDays(30), Content = "updated" },
            Observation(1)));

        var resume = registry.PrepareResume(
            completed.Generation,
            completed.LatestSequence);
        var mutation = Assert.Single(resume.Subscription!.Replay);
        Assert.Equal(original.CreatedAt, mutation.Message!.CreatedAt);
        Assert.Equal("updated", mutation.Message.Content);
        await resume.Subscription.DisposeAsync();
    }

    [Fact]
    public void HostSlotContractAndPublicConfigurationFilesContainNoRawHostIds()
    {
        Assert.Equal(
            new[]
            {
                nameof(HostPresenceSnapshot.HostSlot),
                nameof(HostPresenceSnapshot.State),
                nameof(HostPresenceSnapshot.CurrentPlayers),
                nameof(HostPresenceSnapshot.MaximumPlayers),
                nameof(HostPresenceSnapshot.ObservedAt),
            },
            typeof(HostPresenceSnapshot).GetProperties().Select(property => property.Name));

        var paths = new[]
        {
            Path.Combine("src", "LSOverlay.Backend", "Configuration", "BackendConfiguration.cs"),
            Path.Combine("src", "LSOverlay.Backend", "Presence", "TrackedHostPresenceStore.cs"),
            Path.Combine("src", "GachaOverlay.App", "Presentation", "SessionHudViewModel.cs"),
            Path.Combine("tools", "dev", "run-ls-m98-local.ps1"),
            Path.Combine("docs", "architecture", "M9.8.1-explicit-sales-window-and-two-host-selection.md"),
        };
        foreach (var path in paths)
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot, path));
            Assert.Empty(Regex.Matches(source, @"(?<!\d)\d{17,20}(?!\d)"));
        }
    }

    [Fact]
    public void ValidationHelper_ClearsBackendOnlyHostConfigurationBeforeWpfLaunch()
    {
        var shared = File.ReadAllText(Path.Combine(
            RepositoryRoot, "tools", "dev", "run-ls-m94-local.ps1"));
        var setHost1 = shared.LastIndexOf(
            "SetEnvironmentVariable($host1Name", StringComparison.Ordinal);
        var clear = shared.IndexOf(
            "Clear-BackendEnvironment", setHost1, StringComparison.Ordinal);
        var launch = shared.IndexOf(
            "$activeWpfProcess = Start-WpfApplication", clear, StringComparison.Ordinal);

        Assert.True(setHost1 >= 0);
        Assert.True(clear > setHost1);
        Assert.True(launch > clear);
        Assert.Contains("$host1Name,", shared, StringComparison.Ordinal);
        Assert.Contains("$host2Name,", shared, StringComparison.Ordinal);
        Assert.Contains("unknown process", shared, StringComparison.Ordinal);
    }

    private static BackendConfiguration Configuration() => new(
        new BackendBotCredential("synthetic-token"),
        10,
        Array.Empty<ulong>(),
        Path.Combine(Path.GetTempPath(), "m981-tests"));

    private static SalesCompletionObservation Observation(ulong messageId) => new(
        messageId,
        false,
        false,
        SalesEvidenceCoverage.Complete,
        SalesTestFactory.Epoch);

    private static ChatMessage Message(ulong messageId) => new(
        messageId,
        10,
        RemoteSalesPolicy.ProductionSalesChannelId,
        "Default",
        0,
        new ChatAuthor(20, "seller", "Seller", "Seller", false, false),
        $"sale-{messageId}",
        SalesTestFactory.Epoch.AddSeconds(messageId),
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

    private sealed class MissingMemberSource : IRemoteGuildMemberSource
    {
        public Task<RemoteGuildMemberResolution> ResolveAsync(
            ulong guildId,
            ulong authorId,
            CancellationToken cancellationToken) => Task.FromResult(
            new RemoteGuildMemberResolution(RemoteGuildMemberResolutionStatus.NotFound));
    }

    private sealed class UnusedAuthorizationService : IChatAuthorizationService
    {
        public Task<ChatAuthorizationResult> GetCatalogAsync(
            AuthenticatedClientIdentity identity,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChatAuthorizationResult> AuthorizeChannelAsync(
            AuthenticatedClientIdentity identity,
            ulong channelId,
            bool forceRefresh,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void InvalidateGuild(ulong guildId)
        {
        }
    }

    private sealed class UnusedChatSource : IChatDiscordSource
    {
        public Task<ChatGuildSourceResult> GetGuildAsync(
            AuthenticatedClientIdentity identity,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChatMessagesSourceResult> GetRecentMessagesAsync(
            ulong channelId,
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChatMessageSourceResult> GetMessageAsync(
            ulong channelId,
            ulong messageId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

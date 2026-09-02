using Discord;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Sales;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Json;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace GachaOverlay.Tests.Backend;

public sealed class M97SalesStatusActionTests
{
    private const ulong GuildId = 1;
    private const ulong ChannelId = 20;
    private const ulong UserId = 10;
    private const ulong MessageId = 30;

    [Fact]
    public void EmojiContract_UsesExactProductionIdsAndNames()
    {
        Assert.True(RemoteSalesPolicy.IsSellingMarker(
            1523085309443571762,
            "ignored"));
        Assert.True(RemoteSalesPolicy.IsNegotiatingMarker(
            1524773310288756869,
            "ignored"));
        Assert.True(RemoteSalesPolicy.IsSoldMarker(
            1451583544295034940,
            "ignored"));
        Assert.False(RemoteSalesPolicy.IsSellingMarker(1, "SELL_onsale"));
        Assert.False(RemoteSalesPolicy.IsNegotiatingMarker(1, "SELL_working"));
    }

    [Fact]
    public void HumanMarkers_AreNeverIncludedInClearPlan()
    {
        var humanOnly = Observation(
            sold: true,
            closed: true,
            botSelling: false,
            botNegotiating: false,
            botCompleted: false);

        Assert.Empty(RemoteSalesActionService.CreatePlan(humanOnly, SalesStatus.Clear));
    }

    [Fact]
    public void DesiredStatus_AddsFirstThenRemovesOnlyOtherBotMarkers()
    {
        var plan = RemoteSalesActionService.CreatePlan(
            Observation(botNegotiating: true, botCompleted: true),
            SalesStatus.Selling);

        Assert.Equal(
            new[]
            {
                new SalesStatusMutation(SalesStatus.Selling, Add: true),
                new SalesStatusMutation(SalesStatus.Negotiating, Add: false),
                new SalesStatusMutation(SalesStatus.Completed, Add: false),
            },
            plan);
    }

    [Fact]
    public void ExactDesiredBotState_IsIdempotent()
    {
        Assert.Empty(RemoteSalesActionService.CreatePlan(
            Observation(botSelling: true),
            SalesStatus.Selling));
    }

    [Fact]
    public void Clear_RemovesOnlyThreeApprovedBotOwnedMarkers()
    {
        var plan = RemoteSalesActionService.CreatePlan(
            Observation(
                sold: true,
                closed: true,
                botSelling: true,
                botNegotiating: true,
                botCompleted: true),
            SalesStatus.Clear);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, mutation => Assert.False(mutation.Add));
        Assert.Equal(
            new[] { SalesStatus.Selling, SalesStatus.Negotiating, SalesStatus.Completed },
            plan.Select(mutation => mutation.Status));
    }

    [Fact]
    public async Task NonOwner_IsRejectedWithoutAnyDiscordMutation()
    {
        var fixture = CreateFixture(authorId: UserId + 1);

        var response = await fixture.Service.SetStatusAsync(
            Identity(),
            Request(fixture.Generation, SalesStatus.Selling),
            CancellationToken.None);

        Assert.Equal(SalesStatusActionDisposition.RejectedNotOwner, response.Disposition);
        Assert.Empty(fixture.Source.Mutations);
        Assert.False(fixture.Authorization.LastForceRefresh);
    }

    [Fact]
    public async Task OwnerClear_DeletesOnlyBotOwnedMarkers()
    {
        var fixture = CreateFixture(
            authorId: UserId,
            observation: Observation(
                sold: true,
                closed: true,
                botSelling: true,
                botNegotiating: false,
                botCompleted: true));

        var response = await fixture.Service.SetStatusAsync(
            Identity(),
            Request(fixture.Generation, SalesStatus.Clear),
            CancellationToken.None);

        Assert.Equal(SalesStatusActionDisposition.Accepted, response.Disposition);
        Assert.Equal(
            new[]
            {
                new SalesStatusMutation(SalesStatus.Selling, Add: false),
                new SalesStatusMutation(SalesStatus.Completed, Add: false),
            },
            fixture.Source.Mutations);
    }

    [Fact]
    public async Task DuplicateClientRequest_IsExecutedOnce()
    {
        var fixture = CreateFixture(authorId: UserId);
        var request = Request(fixture.Generation, SalesStatus.Selling);

        var first = fixture.Service.SetStatusAsync(
            Identity(), request, CancellationToken.None);
        var second = fixture.Service.SetStatusAsync(
            Identity(), request, CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Equal(first.Result, second.Result);
        Assert.Single(fixture.Source.Mutations);
    }

    [Fact]
    public async Task WrongSalesGeneration_IsRejectedBeforeDiscordAccess()
    {
        var fixture = CreateFixture(authorId: UserId);

        var response = await fixture.Service.SetStatusAsync(
            Identity(),
            Request("stale", SalesStatus.Completed),
            CancellationToken.None);

        Assert.Equal(SalesStatusActionDisposition.RejectedStale, response.Disposition);
        Assert.Equal(0, fixture.Authorization.AuthorizeCalls);
        Assert.Empty(fixture.Source.Mutations);
    }

    [Fact]
    public async Task MissingMessage_IsRejectedSafely()
    {
        var fixture = CreateFixture(authorId: UserId);
        fixture.Source.MessageStatus = SalesStatusDiscordResult.NotFound;

        var response = await fixture.Service.SetStatusAsync(
            Identity(),
            Request(fixture.Generation, SalesStatus.Completed),
            CancellationToken.None);

        Assert.Equal(
            SalesStatusActionDisposition.RejectedMessageMissing,
            response.Disposition);
        Assert.Empty(fixture.Source.Mutations);
    }

    [Fact]
    public async Task RevokedAuthorization_StopsBeforeCanonicalMessageLookup()
    {
        var fixture = CreateFixture(authorId: UserId);
        fixture.Authorization.Status = ChatAuthorizationStatus.AccessRevoked;

        var response = await fixture.Service.SetStatusAsync(
            Identity(),
            Request(fixture.Generation, SalesStatus.Negotiating),
            CancellationToken.None);

        Assert.Equal(
            SalesStatusActionDisposition.RejectedUnauthorized,
            response.Disposition);
        Assert.Equal(0, fixture.Source.MessageLookups);
        Assert.Empty(fixture.Source.Mutations);
    }

    [Fact]
    public async Task BotWithoutAddReactions_IsRejectedWithoutMutation()
    {
        var fixture = CreateFixture(authorId: UserId);
        fixture.Authorization.BotCanReact = false;

        var response = await fixture.Service.SetStatusAsync(
            Identity(),
            Request(fixture.Generation, SalesStatus.Selling),
            CancellationToken.None);

        Assert.Equal(
            SalesStatusActionDisposition.RejectedUnavailable,
            response.Disposition);
        Assert.Empty(fixture.Source.Mutations);
    }

    [Fact]
    public async Task AlreadyMatchingBotStatus_ReturnsNoOp()
    {
        var fixture = CreateFixture(
            authorId: UserId,
            observation: Observation(botSelling: true));

        var response = await fixture.Service.SetStatusAsync(
            Identity(),
            Request(fixture.Generation, SalesStatus.Selling),
            CancellationToken.None);

        Assert.Equal(SalesStatusActionDisposition.NoOp, response.Disposition);
        Assert.Empty(fixture.Source.Mutations);
    }

    [Fact]
    public async Task NinthActionInsideMinute_IsRateLimited()
    {
        var fixture = CreateFixture(authorId: UserId);
        SalesStatusActionResponse? response = null;
        for (var index = 0; index < RemoteSalesActionService.MaximumRequestsPerMinute + 1;
             index++)
        {
            response = await fixture.Service.SetStatusAsync(
                Identity(),
                Request(fixture.Generation, SalesStatus.Selling),
                CancellationToken.None);
        }

        Assert.NotNull(response);
        Assert.Equal(
            SalesStatusActionDisposition.RejectedRateLimited,
            response.Disposition);
        Assert.Equal(
            RemoteSalesActionService.MaximumRequestsPerMinute,
            fixture.Source.Mutations.Count);
    }

    [Fact]
    public async Task WrongGuild_IsRejectedBeforeRateOrDiscordWork()
    {
        var fixture = CreateFixture(authorId: UserId);
        var identity = Identity() with { GuildId = GuildId + 1 };

        var response = await fixture.Service.SetStatusAsync(
            identity,
            Request(fixture.Generation, SalesStatus.Selling),
            CancellationToken.None);

        Assert.Equal(
            SalesStatusActionDisposition.RejectedUnauthorized,
            response.Disposition);
        Assert.Equal(0, fixture.Authorization.AuthorizeCalls);
        Assert.Empty(fixture.Source.Mutations);
    }

    [Fact]
    public async Task NewerSameMessageRequest_SupersedesOlderBeforeMutation()
    {
        var fixture = CreateFixture(authorId: UserId);
        fixture.Source.BlockFirstMessageLookup = true;
        var older = fixture.Service.SetStatusAsync(
            Identity(),
            Request(fixture.Generation, SalesStatus.Selling),
            CancellationToken.None);
        await fixture.Source.FirstMessageLookup.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var newer = fixture.Service.SetStatusAsync(
            Identity(),
            Request(fixture.Generation, SalesStatus.Completed),
            CancellationToken.None);
        fixture.Source.ReleaseFirstMessageLookup.TrySetResult();
        await Task.WhenAll(older, newer);

        Assert.Equal(SalesStatusActionDisposition.RejectedStale, older.Result.Disposition);
        Assert.Equal(SalesStatusActionDisposition.Accepted, newer.Result.Disposition);
        Assert.Equal(
            new[] { new SalesStatusMutation(SalesStatus.Completed, Add: true) },
            fixture.Source.Mutations);
    }

    [Fact]
    public void BotWritePermission_RequiresReadHistoryAndAddReactions()
    {
        var complete = DiscordPermissionEvaluator.ViewChannel |
            DiscordPermissionEvaluator.ReadMessageHistory |
            DiscordPermissionEvaluator.AddReactions;

        Assert.True(DiscordPermissionEvaluator.CanAddReactions(complete));
        Assert.False(DiscordPermissionEvaluator.CanAddReactions(
            complete & ~DiscordPermissionEvaluator.AddReactions));
        Assert.False(DiscordPermissionEvaluator.CanAddReactions(
            complete & ~DiscordPermissionEvaluator.ReadMessageHistory));
    }

    [Fact]
    public async Task AuthorizationLease_CachesBotReactionCapability()
    {
        var permissions = DiscordPermissionEvaluator.ViewChannel |
            DiscordPermissionEvaluator.ReadMessageHistory |
            DiscordPermissionEvaluator.AddReactions;
        var source = new PermissionChatSource(permissions);
        var authorization = new ChatAuthorizationService(source);

        var first = await authorization.AuthorizeChannelAsync(
            Identity(),
            ChannelId,
            forceRefresh: false,
            CancellationToken.None);
        var second = await authorization.AuthorizeChannelAsync(
            Identity(),
            ChannelId,
            forceRefresh: false,
            CancellationToken.None);

        Assert.Equal(ChatAuthorizationStatus.Authorized, first.Status);
        Assert.Contains(
            first.BotReactionAuthorizedChannels!,
            channel => channel.ChannelId == ChannelId);
        Assert.Equal(first.ValidUntil, second.ValidUntil);
        Assert.Equal(1, source.GuildRequests);
    }

    [Fact]
    public void ProtocolRequest_DoesNotExposeGuildChannelOrEmojiSelection()
    {
        var names = typeof(SalesStatusActionRequest).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("GuildId", names);
        Assert.DoesNotContain("ChannelId", names);
        Assert.DoesNotContain("EmojiId", names);
        Assert.DoesNotContain("EmojiName", names);
    }

    [Fact]
    public void BackendHttpBinding_AcceptsProtocolCamelCaseStatusEnum()
    {
        var configuration = new BackendConfiguration(
            new BackendBotCredential("secret"),
            GuildId,
            Array.Empty<ulong>(),
            stateDirectory: Path.Combine(Path.GetTempPath(), "m97-json-tests"),
            listenUri: new Uri("http://127.0.0.1:5197"),
            salesChannelId: ChannelId);
        using var host = LSOverlay.Backend.Program.CreateHost(configuration);
        var serverOptions = host.Services
            .GetRequiredService<IOptions<HttpJsonOptions>>()
            .Value.SerializerOptions;
        var request = Request("generation", SalesStatus.Selling);
        var json = JsonSerializer.Serialize(request, OverlayProtocolJson.Options);

        var restored = JsonSerializer.Deserialize<SalesStatusActionRequest>(
            json,
            serverOptions);

        Assert.NotNull(restored);
        Assert.Equal(SalesStatus.Selling, restored.DesiredStatus);
        Assert.Contains("\"desiredStatus\":\"selling\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationHelper_IsDedicatedAndIncludesHumanReactionPreservation()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var wrapper = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "dev",
            "run-ls-m97-local.ps1"));
        var shared = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "dev",
            "run-ls-m94-local.ps1"));

        Assert.Contains("ValidationMilestone 'M9.7'", wrapper, StringComparison.Ordinal);
        Assert.Contains("manually added human reaction remains untouched", shared,
            StringComparison.Ordinal);
        Assert.Contains("another user reaction remains untouched", shared,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiscordAdapter_UsesBotIdentityAndNeverEnumeratesReactionUsers()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "LSOverlay.Backend",
            "Sales",
            "SalesStatusDiscordSource.cs"));

        Assert.Contains("_client.Rest.CurrentUser.Id", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetReactionUsers", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetReactionsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ManageMessages", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Administrator", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M912_UnfinishedActionsStayBoundedEvenWhenDedupeEntriesAgeOut()
    {
        var fixture = CreateFixture(UserId);
        fixture.Source.BlockFirstMessageLookup = true;
        var pending = new List<Task<SalesStatusActionResponse>>();
        pending.Add(fixture.Service.SetStatusAsync(Identity(), Request(fixture.Generation, SalesStatus.Clear), CancellationToken.None));
        await fixture.Source.FirstMessageLookup.Task;
        try
        {
            for (var i = 1; i < RemoteSalesActionService.DedupeCapacity; i++)
            {
                var identity = new AuthenticatedClientIdentity(Guid.NewGuid(), (ulong)(1000 + i), GuildId);
                pending.Add(fixture.Service.SetStatusAsync(identity, Request(fixture.Generation, SalesStatus.Clear), CancellationToken.None));
            }
            var overflow = fixture.Service.SetStatusAsync(new AuthenticatedClientIdentity(Guid.NewGuid(), 9000, GuildId),
                Request(fixture.Generation, SalesStatus.Clear), CancellationToken.None);
            Assert.True(overflow.IsCompleted);
            Assert.Equal(SalesStatusActionDisposition.RejectedUnavailable, (await overflow).Disposition);
        }
        finally
        {
            fixture.Source.ReleaseFirstMessageLookup.TrySetResult();
            await Task.WhenAll(pending);
        }
        Assert.Empty(fixture.Source.Mutations);
    }

    private static Fixture CreateFixture(
        ulong authorId,
        SalesCompletionObservation? observation = null)
    {
        var configuration = new BackendConfiguration(
            new BackendBotCredential("secret"),
            GuildId,
            Array.Empty<ulong>(),
            stateDirectory: Path.Combine(Path.GetTempPath(), "m97-tests"),
            salesChannelId: ChannelId);
        var authorization = new AuthorizedChatService();
        var discord = new FakeSalesStatusSource(
            new SalesStatusMessageSnapshot(
                MessageId,
                authorId,
                observation ?? Observation(),
                new object()));
        var streams = new ActiveSalesStreamRegistry(configuration);
        var generation = streams.Activate().Generation;
        var chat = new EmptyChatSource();
        var normalizer = new DiscordChatMessageNormalizer(
            new CanonicalRemoteAuthorResolver(new MissingMemberSource()));
        var remoteSales = new RemoteSalesService(
            configuration,
            authorization,
            chat,
            normalizer,
            streams);
        var service = new RemoteSalesActionService(
            configuration,
            authorization,
            discord,
            streams,
            remoteSales,
            new TransportMetrics(),
            NullLogger<RemoteSalesActionService>.Instance);
        return new Fixture(service, discord, authorization, generation);
    }

    private static AuthenticatedClientIdentity Identity() => new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        UserId,
        GuildId);

    private static SalesStatusActionRequest Request(
        string generation,
        SalesStatus status) => new(
            OverlayTransportProtocol.Version,
            MessageId,
            status,
            Guid.NewGuid(),
            generation);

    private static SalesCompletionObservation Observation(
        bool sold = false,
        bool closed = false,
        bool botSelling = false,
        bool botNegotiating = false,
        bool botCompleted = false) => new(
            MessageId,
            sold,
            closed,
            SalesEvidenceCoverage.Complete,
            DateTimeOffset.UtcNow,
            botSelling,
            botNegotiating,
            botCompleted);

    private sealed record Fixture(
        RemoteSalesActionService Service,
        FakeSalesStatusSource Source,
        AuthorizedChatService Authorization,
        string Generation);

    private sealed class FakeSalesStatusSource : ISalesStatusDiscordSource
    {
        private readonly SalesStatusMessageSnapshot _message;

        public FakeSalesStatusSource(SalesStatusMessageSnapshot message) =>
            _message = message;

        public SalesStatusDiscordResult MessageStatus { get; set; } =
            SalesStatusDiscordResult.Success;
        public bool BlockFirstMessageLookup { get; set; }
        public TaskCompletionSource FirstMessageLookup { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstMessageLookup { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _messageLookups;
        public int MessageLookups => Volatile.Read(ref _messageLookups);
        public List<SalesStatusMutation> Mutations { get; } = new();

        public async Task<SalesStatusMessageResult> GetMessageAsync(
            ulong channelId,
            ulong messageId,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _messageLookups) == 1 &&
                BlockFirstMessageLookup)
            {
                FirstMessageLookup.TrySetResult();
                await ReleaseFirstMessageLookup.Task.WaitAsync(cancellationToken);
            }

            return new SalesStatusMessageResult(
                MessageStatus,
                MessageStatus == SalesStatusDiscordResult.Success ? _message : null);
        }

        public Task<SalesStatusDiscordResult> AddOwnReactionAsync(
            SalesStatusMessageSnapshot message,
            SalesStatus status,
            CancellationToken cancellationToken)
        {
            Mutations.Add(new SalesStatusMutation(status, Add: true));
            return Task.FromResult(SalesStatusDiscordResult.Success);
        }

        public Task<SalesStatusDiscordResult> RemoveOwnReactionAsync(
            SalesStatusMessageSnapshot message,
            SalesStatus status,
            CancellationToken cancellationToken)
        {
            Mutations.Add(new SalesStatusMutation(status, Add: false));
            return Task.FromResult(SalesStatusDiscordResult.Success);
        }
    }

    private sealed class AuthorizedChatService : IChatAuthorizationService
    {
        public ChatAuthorizationStatus Status { get; set; } =
            ChatAuthorizationStatus.Authorized;
        public bool BotCanReact { get; set; } = true;
        public int AuthorizeCalls { get; private set; }
        public bool LastForceRefresh { get; private set; }

        public Task<ChatAuthorizationResult> GetCatalogAsync(
            AuthenticatedClientIdentity identity,
            CancellationToken cancellationToken) => Task.FromResult(Result());

        public Task<ChatAuthorizationResult> AuthorizeChannelAsync(
            AuthenticatedClientIdentity identity,
            ulong channelId,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            AuthorizeCalls++;
            LastForceRefresh = forceRefresh;
            return Task.FromResult(Result());
        }

        public void InvalidateGuild(ulong guildId)
        {
        }

        private ChatAuthorizationResult Result() => new(
            Status,
            Status == ChatAuthorizationStatus.Authorized
                ? new ChatChannelDescriptor(GuildId, ChannelId, "sales", 1, false)
                : null,
            new[] { new ChatChannelDescriptor(GuildId, ChannelId, "sales", 1, false) },
            DateTimeOffset.UtcNow.AddMinutes(1),
            BotCanReact
                ? new[]
                {
                    new ChatChannelDescriptor(GuildId, ChannelId, "sales", 1, false),
                }
                : Array.Empty<ChatChannelDescriptor>());
    }

    private sealed class EmptyChatSource : IChatDiscordSource
    {
        public Task<ChatGuildSourceResult> GetGuildAsync(
            AuthenticatedClientIdentity identity,
            CancellationToken cancellationToken) => Task.FromResult(
            new ChatGuildSourceResult(ChatSourceStatus.NotFound, null));

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

    private sealed class PermissionChatSource : IChatDiscordSource
    {
        private readonly ulong _permissions;

        public PermissionChatSource(ulong permissions) => _permissions = permissions;

        public int GuildRequests { get; private set; }

        public Task<ChatGuildSourceResult> GetGuildAsync(
            AuthenticatedClientIdentity identity,
            CancellationToken cancellationToken)
        {
            GuildRequests++;
            return Task.FromResult(new ChatGuildSourceResult(
                ChatSourceStatus.Available,
                new ChatGuildSnapshot(
                    GuildId,
                    new[] { new ChatRolePermission(GuildId, _permissions) },
                    new ChatMemberSnapshot(UserId, Array.Empty<ulong>()),
                    new ChatMemberSnapshot(99, Array.Empty<ulong>()),
                    new[]
                    {
                        new ChatChannelSnapshot(
                            new ChatChannelDescriptor(
                                GuildId,
                                ChannelId,
                                "sales",
                                1,
                                false),
                            Array.Empty<ChatPermissionOverwrite>()),
                    })));
        }

        public Task<ChatMessagesSourceResult> GetRecentMessagesAsync(
            ulong channelId,
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChatMessageSourceResult> GetMessageAsync(
            ulong channelId,
            ulong messageId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MissingMemberSource : IRemoteGuildMemberSource
    {
        public Task<RemoteGuildMemberResolution> ResolveAsync(
            ulong guildId,
            ulong authorId,
            CancellationToken cancellationToken) => Task.FromResult(
            new RemoteGuildMemberResolution(RemoteGuildMemberResolutionStatus.NotFound));
    }
}

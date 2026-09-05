using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Security;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests.Backend;

public sealed class PermissionBatch211Tests
{
    private static readonly AuthenticatedClientIdentity Identity = new(Guid.NewGuid(), 10, 1);

    [Fact]
    public async Task ConcurrentAndSequentialConsumersShareOneSnapshot()
    {
        var gate = new TaskCompletionSource<ChatGuildSourceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Fetch = _ => gate.Task };
        var service = new ChatAuthorizationService(source);
        using var batch = new ChatAuthorizationService.RefreshBatch(default);
        using var scope = batch.Enter();
        var chat = service.AuthorizeChannelAsync(Identity, 100, true, default);
        var sales = service.AuthorizeChannelAsync(Identity, 200, true, default);
        Assert.Equal(1, source.Count);
        gate.SetResult(Source.Allowed());
        Assert.Equal(ChatAuthorizationStatus.Authorized, (await chat).Status);
        Assert.Same((await chat).AuthorizedChannels, (await sales).AuthorizedChannels);
        await service.AuthorizeChannelAsync(Identity, 200, true, default);
        Assert.Equal(1, source.Count);
    }

    [Fact]
    public async Task PeriodicBatchesRefreshOnceEachWithoutExtendingTtl()
    {
        var source = new Source();
        var service = new ChatAuthorizationService(source);
        for (var cycle = 1; cycle <= 4; cycle++)
        {
            using var batch = new ChatAuthorizationService.RefreshBatch(default);
            using var scope = batch.Enter();
            await service.AuthorizeChannelAsync(Identity, 100, true, default);
            await service.AuthorizeChannelAsync(Identity, 200, true, default);
            Assert.Equal(cycle, source.Count);
        }
        Assert.Equal(TimeSpan.FromMinutes(2), ChatAuthorizationService.LeaseLifetime);
    }

    [Fact]
    public async Task RevocationIsAppliedPerChannelFromSameFreshEvidence()
    {
        var source = new Source();
        var service = new ChatAuthorizationService(source);
        await service.AuthorizeChannelAsync(Identity, 200, true, default);
        source.Fetch = _ => Task.FromResult(Source.Allowed(denySales: true));
        using var batch = new ChatAuthorizationService.RefreshBatch(default);
        using var scope = batch.Enter();
        Assert.Equal(ChatAuthorizationStatus.Authorized, (await service.AuthorizeChannelAsync(Identity, 100, true, default)).Status);
        Assert.Equal(ChatAuthorizationStatus.AccessRevoked, (await service.AuthorizeChannelAsync(Identity, 200, true, default)).Status);
        Assert.Equal(2, source.Count);
    }

    [Fact]
    public async Task TransientFailureIsSharedButNextCycleRetries()
    {
        var source = new Source { Fetch = _ => Task.FromResult(new ChatGuildSourceResult(ChatSourceStatus.Unavailable, null)) };
        var service = new ChatAuthorizationService(source);
        using (var batch = new ChatAuthorizationService.RefreshBatch(default))
        using (batch.Enter())
        {
            Assert.Equal(ChatAuthorizationStatus.AuthorizationUnavailable, (await service.AuthorizeChannelAsync(Identity, 100, true, default)).Status);
            Assert.Equal(ChatAuthorizationStatus.AuthorizationUnavailable, (await service.AuthorizeChannelAsync(Identity, 200, true, default)).Status);
            Assert.Equal(1, source.Count);
        }
        source.Fetch = _ => Task.FromResult(Source.Allowed());
        using var next = new ChatAuthorizationService.RefreshBatch(default);
        using var scope = next.Enter();
        Assert.Equal(ChatAuthorizationStatus.Authorized, (await service.AuthorizeChannelAsync(Identity, 100, true, default)).Status);
        Assert.Equal(2, source.Count);
    }

    [Fact]
    public async Task LateCancelledGenerationCannotResurrectCachedAccess()
    {
        var gate = new TaskCompletionSource<ChatGuildSourceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Fetch = _ => gate.Task }; // deliberately ignores cancellation
        var service = new ChatAuthorizationService(source);
        using var cancellation = new CancellationTokenSource();
        Task<ChatAuthorizationResult> old;
        using (var previous = new ChatAuthorizationService.RefreshBatch(cancellation.Token))
        {
            using (previous.Enter()) old = service.AuthorizeChannelAsync(Identity, 100, true, default);
            cancellation.Cancel();
            source.Fetch = _ => Task.FromResult(new ChatGuildSourceResult(ChatSourceStatus.NotMember, null));
            using var next = new ChatAuthorizationService.RefreshBatch(default);
            using (next.Enter())
                Assert.Equal(ChatAuthorizationStatus.AccessRevoked, (await service.AuthorizeChannelAsync(Identity, 100, true, default)).Status);
            gate.SetResult(Source.Allowed());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        }
        Assert.Equal(ChatAuthorizationStatus.AccessRevoked, (await service.GetCatalogAsync(Identity, default)).Status);
        Assert.Equal(2, source.Count);
    }

    [Fact]
    public async Task InvalidatedOrExpiredBatchNeverReturnsOldAllow()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new Source();
        var service = new ChatAuthorizationService(source, () => now);
        using var batch = new ChatAuthorizationService.RefreshBatch(default);
        using var scope = batch.Enter();
        await service.AuthorizeChannelAsync(Identity, 100, true, default);
        service.InvalidateGuild(1);
        Assert.Equal(ChatAuthorizationStatus.AuthorizationUnavailable, (await service.AuthorizeChannelAsync(Identity, 200, true, default)).Status);
        now += TimeSpan.FromMinutes(3);
        Assert.Equal(ChatAuthorizationStatus.AuthorizationUnavailable, (await service.AuthorizeChannelAsync(Identity, 100, true, default)).Status);
        Assert.Equal(1, source.Count);
    }

    [Fact]
    public async Task InstallationsAreIsolatedAndLegacyForceStillRefreshes()
    {
        var source = new Source();
        var service = new ChatAuthorizationService(source);
        using (var batch = new ChatAuthorizationService.RefreshBatch(default))
        using (batch.Enter())
        {
            await service.AuthorizeChannelAsync(Identity, 100, true, default);
            await service.AuthorizeChannelAsync(Identity with { ClientInstallationId = Guid.NewGuid() }, 100, true, default);
            Assert.Equal(2, source.Count);
        }
        await service.AuthorizeChannelAsync(Identity, 100, true, default);
        await service.AuthorizeChannelAsync(Identity, 100, true, default);
        Assert.Equal(4, source.Count);
    }

    [Fact]
    public async Task FaultIsSharedOnlyWithinBatchAndNextBatchCanRecover()
    {
        var source = new Source { Fetch = _ => Task.FromException<ChatGuildSourceResult>(new HttpRequestException("test")) };
        var service = new ChatAuthorizationService(source);
        using (var batch = new ChatAuthorizationService.RefreshBatch(default))
        using (batch.Enter())
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => service.AuthorizeChannelAsync(Identity, 100, true, default));
            await Assert.ThrowsAsync<HttpRequestException>(() => service.AuthorizeChannelAsync(Identity, 200, true, default));
            Assert.Equal(1, source.Count);
        }
        source.Fetch = _ => Task.FromResult(Source.Allowed());
        using var next = new ChatAuthorizationService.RefreshBatch(default);
        using var scope = next.Enter();
        Assert.Equal(ChatAuthorizationStatus.Authorized, (await service.AuthorizeChannelAsync(Identity, 100, true, default)).Status);
        Assert.Equal(2, source.Count);
    }

    [Fact]
    public async Task OneCancelledWaiterDoesNotCancelOtherConsumer()
    {
        var gate = new TaskCompletionSource<ChatGuildSourceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Fetch = _ => gate.Task };
        var service = new ChatAuthorizationService(source);
        using var batch = new ChatAuthorizationService.RefreshBatch(default);
        using var scope = batch.Enter();
        using var waiter = new CancellationTokenSource();
        var chat = service.AuthorizeChannelAsync(Identity, 100, true, waiter.Token);
        var sales = service.AuthorizeChannelAsync(Identity, 200, true, default);
        waiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat);
        gate.SetResult(Source.Allowed());
        Assert.Equal(ChatAuthorizationStatus.Authorized, (await sales).Status);
        Assert.Equal(1, source.Count);
    }

    [Fact]
    public async Task ExpiredEvidenceAndGuildRevocationCannotRemainAllowed()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new Source();
        var service = new ChatAuthorizationService(source, () => now);
        using (var batch = new ChatAuthorizationService.RefreshBatch(default))
        using (batch.Enter())
        {
            await service.AuthorizeChannelAsync(Identity, 100, true, default);
            now += TimeSpan.FromMinutes(3);
            Assert.Equal(ChatAuthorizationStatus.AuthorizationUnavailable, (await service.AuthorizeChannelAsync(Identity, 200, true, default)).Status);
        }
        source.Fetch = _ => Task.FromResult(new ChatGuildSourceResult(ChatSourceStatus.NotMember, null));
        using var next = new ChatAuthorizationService.RefreshBatch(default);
        using var scope = next.Enter();
        Assert.Equal(ChatAuthorizationStatus.AccessRevoked, (await service.AuthorizeChannelAsync(Identity, 100, true, default)).Status);
        Assert.Equal(ChatAuthorizationStatus.AccessRevoked, (await service.AuthorizeChannelAsync(Identity, 200, true, default)).Status);
        Assert.Equal(2, source.Count);
    }

    private sealed class Source : IChatDiscordSource
    {
        public int Count;
        public Func<CancellationToken, Task<ChatGuildSourceResult>> Fetch = _ => Task.FromResult(Allowed());
        public Task<ChatGuildSourceResult> GetGuildAsync(AuthenticatedClientIdentity identity, CancellationToken token)
        {
            Interlocked.Increment(ref Count);
            return Fetch(token);
        }
        public static ChatGuildSourceResult Allowed(bool denySales = false) => new(ChatSourceStatus.Available,
            new ChatGuildSnapshot(1,
                new[] { new ChatRolePermission(1, DiscordPermissionEvaluator.ViewChannel | DiscordPermissionEvaluator.ReadMessageHistory) },
                new ChatMemberSnapshot(10, Array.Empty<ulong>()), new ChatMemberSnapshot(99, Array.Empty<ulong>()),
                new[] {
                    new ChatChannelSnapshot(new ChatChannelDescriptor(1, 100, "chat", 0, false), Array.Empty<ChatPermissionOverwrite>()),
                    new ChatChannelSnapshot(new ChatChannelDescriptor(1, 200, "sales", 1, false), denySales
                        ? new[] { new ChatPermissionOverwrite(10, ChatPermissionTarget.Member, 0, DiscordPermissionEvaluator.ViewChannel) }
                        : Array.Empty<ChatPermissionOverwrite>()) }));
        public Task<ChatMessagesSourceResult> GetRecentMessagesAsync(ulong channelId, int limit, CancellationToken token) => throw new NotSupportedException();
        public Task<ChatMessageSourceResult> GetMessageAsync(ulong channelId, ulong messageId, CancellationToken token) => throw new NotSupportedException();
    }
}

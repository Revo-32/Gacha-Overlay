using System.Net;
using System.Reflection;
using Discord.Net;
using Discord.Net.Rest;
using Discord.WebSocket;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Security;

namespace GachaOverlay.Tests.Backend;

public sealed class M911BackendStartupRecoveryTests
{
    private static readonly AuthenticatedClientIdentity Identity = new(Guid.NewGuid(), 20, 10);
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task ColdDiscordChatSource_ReturnsUnavailableWithoutStartingRestRequests()
    {
        var transport = DispatchProxy.Create<IRestClient, NoNetworkRestProxy>();
        using var client = new DiscordSocketClient(new DiscordSocketConfig
        {
            RestClientProvider = _ => transport,
        });
        var source = new DiscordNetChatSource(client);

        var result = await source.GetGuildAsync(Identity, CancellationToken.None);

        Assert.Equal(ChatSourceStatus.Unavailable, result.Status);
        Assert.Null(result.Guild);
        Assert.Equal(0, ((NoNetworkRestProxy)transport).SendAttempts);
        var authorization = new ChatAuthorizationService(source);
        Assert.Equal(ChatAuthorizationStatus.AuthorizationUnavailable,
            (await authorization.GetCatalogAsync(Identity, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ColdDiscordMembership_ReturnsUnavailableWithoutStartingRestRequests()
    {
        var transport = DispatchProxy.Create<IRestClient, NoNetworkRestProxy>();
        using var client = new DiscordSocketClient(new DiscordSocketConfig
        {
            RestClientProvider = _ => transport,
        });
        var verifier = new DiscordGuildMembershipVerifier(client);

        Assert.Equal(GuildMembershipStatus.VerificationUnavailable,
            await verifier.VerifyAsync(Identity, CancellationToken.None));
        Assert.Equal(0, ((NoNetworkRestProxy)transport).SendAttempts);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("timeout")]
    [InlineData("http503")]
    [InlineData("http403")]
    [InlineData("http429")]
    [InlineData("network")]
    [InlineData("internal-cancellation")]
    public async Task TransientMembershipFailure_DoesNotPoisonNextBootstrapForFiveMinutes(string failure)
    {
        var calls = 0;
        var verifier = new DiscordGuildMembershipVerifier((_, _) =>
        {
            calls++;
            if (calls > 1) { return Task.FromResult(GuildMembershipStatus.Member); }
            return failure switch
            {
                "timeout" => Task.FromException<GuildMembershipStatus>(new TimeoutException()),
                "http503" => Task.FromException<GuildMembershipStatus>(new HttpException(HttpStatusCode.ServiceUnavailable, null!)),
                "http403" => Task.FromException<GuildMembershipStatus>(new HttpException(HttpStatusCode.Forbidden, null!)),
                "http429" => Task.FromException<GuildMembershipStatus>(new HttpException(HttpStatusCode.TooManyRequests, null!)),
                "network" => Task.FromException<GuildMembershipStatus>(new HttpRequestException()),
                "internal-cancellation" => Task.FromException<GuildMembershipStatus>(new OperationCanceledException()),
                _ => Task.FromResult(GuildMembershipStatus.VerificationUnavailable),
            };
        }, () => Epoch);

        Assert.Equal(GuildMembershipStatus.VerificationUnavailable, await verifier.VerifyAsync(Identity, CancellationToken.None));
        Assert.Equal(GuildMembershipStatus.Member, await verifier.VerifyAsync(Identity, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DefinitiveMembershipDecision_RetainsExistingCacheLifetime(bool isMember)
    {
        var calls = 0;
        var now = Epoch;
        var expected = isMember ? GuildMembershipStatus.Member : GuildMembershipStatus.NotMember;
        var verifier = new DiscordGuildMembershipVerifier((_, _) =>
        {
            calls++;
            return Task.FromResult(expected);
        }, () => now);
        Assert.Equal(expected, await verifier.VerifyAsync(Identity, default));
        now += DiscordGuildMembershipVerifier.CacheLifetime - TimeSpan.FromMilliseconds(1);
        Assert.Equal(expected, await verifier.VerifyAsync(Identity, default));
        Assert.Equal(1, calls);
        now += TimeSpan.FromMilliseconds(1);
        Assert.Equal(expected, await verifier.VerifyAsync(Identity, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Definitive404_RemainsNotMemberAndIsCached()
    {
        var calls = 0;
        var verifier = new DiscordGuildMembershipVerifier((_, _) =>
        {
            calls++;
            return Task.FromException<GuildMembershipStatus>(new HttpException(HttpStatusCode.NotFound, null!));
        }, () => Epoch);
        Assert.Equal(GuildMembershipStatus.NotMember, await verifier.VerifyAsync(Identity, default));
        Assert.Equal(GuildMembershipStatus.NotMember, await verifier.VerifyAsync(Identity, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExpiredMemberLease_DoesNotAuthorizeWhileVerificationIsUnavailable()
    {
        var now = Epoch;
        var results = new Queue<GuildMembershipStatus>(new[]
        {
            GuildMembershipStatus.Member, GuildMembershipStatus.VerificationUnavailable, GuildMembershipStatus.NotMember,
        });
        var verifier = new DiscordGuildMembershipVerifier((_, _) => Task.FromResult(results.Dequeue()), () => now);
        Assert.Equal(GuildMembershipStatus.Member, await verifier.VerifyAsync(Identity, default));
        now += DiscordGuildMembershipVerifier.CacheLifetime;
        Assert.Equal(GuildMembershipStatus.VerificationUnavailable, await verifier.VerifyAsync(Identity, default));
        Assert.Equal(GuildMembershipStatus.NotMember, await verifier.VerifyAsync(Identity, default));
    }

    [Fact]
    public async Task LateTransientFailure_DoesNotOverwriteConcurrentSuccessfulVerification()
    {
        var delayed = new TaskCompletionSource<GuildMembershipStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var verifier = new DiscordGuildMembershipVerifier((_, _) =>
            ++calls == 1 ? delayed.Task : Task.FromResult(GuildMembershipStatus.Member), () => Epoch);
        var pending = verifier.VerifyAsync(Identity, default);
        Assert.Equal(GuildMembershipStatus.Member, await verifier.VerifyAsync(Identity, default));
        delayed.SetResult(GuildMembershipStatus.VerificationUnavailable);
        Assert.Equal(GuildMembershipStatus.VerificationUnavailable, await pending);
        Assert.Equal(GuildMembershipStatus.Member, await verifier.VerifyAsync(Identity, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CallerCancellation_IsPropagatedWithoutCachingOrBlockingTheNextRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var verifier = new DiscordGuildMembershipVerifier((_, token) =>
        {
            if (++calls == 1)
            {
                cancellation.Cancel();
                return Task.FromCanceled<GuildMembershipStatus>(token);
            }
            return Task.FromResult(GuildMembershipStatus.Member);
        }, () => Epoch);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(Identity, cancellation.Token));
        Assert.Equal(GuildMembershipStatus.Member, await verifier.VerifyAsync(Identity, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(Identity, cancellation.Token));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SuccessfulLookupThatIgnoresCallerCancellation_IsNotCached()
    {
        using var cancellation = new CancellationTokenSource();
        var verifier = new DiscordGuildMembershipVerifier((_, token) =>
        {
            if (token.CanBeCanceled)
            {
                cancellation.Cancel();
                return Task.FromResult(GuildMembershipStatus.Member);
            }
            return Task.FromResult(GuildMembershipStatus.NotMember);
        }, () => Epoch);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(Identity, cancellation.Token));
        Assert.Equal(GuildMembershipStatus.NotMember, await verifier.VerifyAsync(Identity, default));
    }

    [Fact]
    public async Task Cache_RemainsBoundedAndKeepsGuildAndUserIdentitySeparate()
    {
        var now = Epoch;
        var calls = 0;
        var verifier = new DiscordGuildMembershipVerifier((_, _) =>
        {
            calls++;
            return Task.FromResult(GuildMembershipStatus.Member);
        }, () => now);
        for (var i = 0; i <= DiscordGuildMembershipVerifier.MaximumCacheEntries; i++)
        {
            now += TimeSpan.FromMilliseconds(1);
            await verifier.VerifyAsync(Identity with { DiscordUserId = (ulong)i + 1 }, default);
        }
        await verifier.VerifyAsync(Identity with { DiscordUserId = 1 }, default);
        Assert.Equal(DiscordGuildMembershipVerifier.MaximumCacheEntries + 2, calls);
        await verifier.VerifyAsync(Identity with { DiscordUserId = 1, GuildId = 11 }, default);
        Assert.Equal(DiscordGuildMembershipVerifier.MaximumCacheEntries + 3, calls);
    }

    public class NoNetworkRestProxy : DispatchProxy
    {
        public int SendAttempts { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "SendAsync")
            {
                SendAttempts++;
                throw new InvalidOperationException("External network is forbidden in this regression test.");
            }
            return null;
        }
    }
}

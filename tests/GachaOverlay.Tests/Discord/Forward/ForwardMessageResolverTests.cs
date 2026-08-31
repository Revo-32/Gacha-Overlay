using System.Text.Json;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Forward;
using GachaOverlay.Infrastructure.Discord.Normalization;

namespace GachaOverlay.Tests.Discord.Forward;

public sealed class ForwardMessageResolverTests
{
    private readonly DiscordMessageNormalizer _normalizer = new(NullAppLogger.Instance);

    [Fact]
    public async Task CacheMiss_CallsGetChannelOnceAndResolvesMatchingSource()
    {
        var resolver = CreateResolver();
        var calls = 0;
        var key = Key("source");

        var content = await resolver.ResolveAsync(
            key,
            (_, _) =>
            {
                calls++;
                return Task.FromResult(Response(
                    Message("unrelated", "ignore"),
                    Message("source", "resolved")));
            },
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("resolved", content?.Content);
    }

    [Fact]
    public async Task CacheHit_DoesNotCallGetChannelAgain()
    {
        var resolver = CreateResolver();
        var calls = 0;
        var key = Key("source");
        Task<JsonElement> Lookup(string _, CancellationToken __)
        {
            calls++;
            return Task.FromResult(Response(Message("source", "resolved")));
        }

        await resolver.ResolveAsync(key, Lookup, CancellationToken.None);
        await resolver.ResolveAsync(key, Lookup, CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SourceMessageNotFound_ReturnsNull()
    {
        var resolver = CreateResolver();

        var content = await resolver.ResolveAsync(
            Key("missing"),
            (_, _) => Task.FromResult(Response(Message("other", "ignore"))),
            CancellationToken.None);

        Assert.Null(content);
    }

    [Fact]
    public async Task ConcurrentSameSource_UsesSingleFlight()
    {
        var resolver = CreateResolver();
        var response = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var key = Key("source");
        Task<JsonElement> Lookup(string _, CancellationToken __)
        {
            Interlocked.Increment(ref calls);
            return response.Task;
        }

        var first = resolver.ResolveAsync(key, Lookup, CancellationToken.None);
        var second = resolver.ResolveAsync(key, Lookup, CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        response.SetResult(Response(Message("source", "resolved")));

        Assert.Same(first, second);
        Assert.Equal("resolved", (await first)?.Content);
        Assert.Equal("resolved", (await second)?.Content);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NewGeneration_DoesNotJoinOrCachePreviousGenerationLookup()
    {
        var resolver = CreateResolver();
        var key = Key("source");
        var oldResponse = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var currentResponse = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCalls = 0;
        var currentCalls = 0;

        resolver.BeginGeneration(1);
        var oldTask = resolver.ResolveAsync(
            key,
            (_, _) =>
            {
                Interlocked.Increment(ref oldCalls);
                return oldResponse.Task;
            },
            CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref oldCalls) == 1);

        resolver.BeginGeneration(2);
        var currentTask = resolver.ResolveAsync(
            key,
            (_, _) =>
            {
                Interlocked.Increment(ref currentCalls);
                return currentResponse.Task;
            },
            CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref currentCalls) == 1);

        Assert.NotSame(oldTask, currentTask);
        oldResponse.SetResult(Response(Message("source", "old-generation")));
        Assert.Equal("old-generation", (await oldTask)?.Content);
        Assert.Equal(0, resolver.CachedEntryCount);

        currentResponse.SetResult(Response(Message("source", "current-generation")));
        Assert.Equal("current-generation", (await currentTask)?.Content);
        Assert.Equal(1, resolver.CachedEntryCount);
        Assert.Equal(0, resolver.InFlightCount);
    }

    [Fact]
    public async Task NegativeCache_PreventsImmediateRepeatedLookup()
    {
        var resolver = CreateResolver();
        var calls = 0;
        var key = Key("missing");
        Task<JsonElement> Lookup(string _, CancellationToken __)
        {
            calls++;
            return Task.FromResult(Response(Message("other", "ignore")));
        }

        Assert.Null(await resolver.ResolveAsync(key, Lookup, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(key, Lookup, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NegativeCache_ExpiresAfterConfiguredTtl()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var resolver = CreateResolver(timeProvider: time, negativeTtl: TimeSpan.FromSeconds(30));
        var calls = 0;
        var key = Key("missing");
        Task<JsonElement> Lookup(string _, CancellationToken __)
        {
            calls++;
            return Task.FromResult(Response(Message("other", "ignore")));
        }

        await resolver.ResolveAsync(key, Lookup, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(31));
        await resolver.ResolveAsync(key, Lookup, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CacheCapacity_IsBounded()
    {
        var resolver = CreateResolver(capacity: 2);

        await ResolveSuccessful(resolver, Key("one"));
        await ResolveSuccessful(resolver, Key("two"));
        await ResolveSuccessful(resolver, Key("three"));

        Assert.Equal(2, resolver.CachedEntryCount);
    }

    [Fact]
    public async Task CacheEviction_IsLeastRecentlyUsed()
    {
        var resolver = CreateResolver(capacity: 2);
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        Task<JsonElement> Lookup(string channelId, CancellationToken _)
        {
            calls[channelId] = calls.GetValueOrDefault(channelId) + 1;
            return Task.FromResult(Response(Message(channelId, channelId)));
        }

        var one = Key("one");
        var two = Key("two");
        var three = Key("three");
        await resolver.ResolveAsync(one, Lookup, CancellationToken.None);
        await resolver.ResolveAsync(two, Lookup, CancellationToken.None);
        await resolver.ResolveAsync(one, Lookup, CancellationToken.None);
        await resolver.ResolveAsync(three, Lookup, CancellationToken.None);
        await resolver.ResolveAsync(two, Lookup, CancellationToken.None);

        Assert.Equal(1, calls["one"]);
        Assert.Equal(2, calls["two"]);
    }

    [Fact]
    public async Task Cancellation_CancelsPendingLookupWithoutCachingFailure()
    {
        var resolver = CreateResolver();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var task = resolver.ResolveAsync(
            Key("source"),
            async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Response();
            },
            cancellation.Token);
        await started.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await WaitUntilAsync(() => resolver.InFlightCount == 0);
        Assert.Equal(0, resolver.CachedEntryCount);
    }

    [Fact]
    public void OnlyMatchingSourceMessageId_IsNormalized()
    {
        var response = Response(
            Message("unrelated", "private unrelated content"),
            Message("source", "selected"));

        var found = _normalizer.TryNormalizeForwardSource(
            response,
            Key("source"),
            out var content);

        Assert.True(found);
        Assert.Equal("selected", content?.Content);
        Assert.DoesNotContain("private unrelated content", content?.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_DoesNotPersistSourceContentToDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gacha-forward-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var resolver = CreateResolver();
            await ResolveSuccessful(resolver, Key("source"));

            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    private ForwardMessageResolver CreateResolver(
        int capacity = ForwardMessageResolver.DefaultCapacity,
        TimeSpan? negativeTtl = null,
        TimeProvider? timeProvider = null) =>
        new(
            _normalizer,
            NullAppLogger.Instance,
            capacity,
            negativeTtl,
            timeProvider);

    private static async Task<DiscordForwardContent?> ResolveSuccessful(
        ForwardMessageResolver resolver,
        DiscordForwardSourceKey key) =>
        await resolver.ResolveAsync(
            key,
            (channelId, _) => Task.FromResult(Response(Message(channelId, channelId))),
            CancellationToken.None);

    private static DiscordForwardSourceKey Key(string id) => new("guild", id, id);

    private static string Message(string id, string content) =>
        $$"""{ "id":"{{id}}", "content":"{{content}}" }""";

    private static JsonElement Response(params string[] messages)
    {
        using var document = JsonDocument.Parse(
            $$"""{ "data": { "messages": [{{string.Join(',', messages)}}] } }""");
        return document.RootElement.Clone();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Expected condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}

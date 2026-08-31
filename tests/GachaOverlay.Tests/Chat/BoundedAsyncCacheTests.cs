using GachaOverlay.Core.Caching;

namespace GachaOverlay.Tests.Chat;

public sealed class BoundedAsyncCacheTests
{
    [Fact]
    public async Task ConcurrentRequests_ShareOneInFlightLoad()
    {
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        using var cache = new BoundedAsyncCache<string>(4, _ =>
        {
            Interlocked.Increment(ref loads);
            return gate.Task;
        });

        var first = cache.GetAsync("emoji");
        var second = cache.GetAsync("emoji");
        gate.SetResult("image");

        Assert.Equal("image", await first);
        Assert.Equal("image", await second);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Capacity_EvictsLeastRecentlyUsedEntry()
    {
        var loads = new Dictionary<string, int>(StringComparer.Ordinal);
        using var cache = new BoundedAsyncCache<string>(2, key =>
        {
            loads[key] = loads.GetValueOrDefault(key) + 1;
            return Task.FromResult<string?>(key);
        });

        await cache.GetAsync("a");
        await cache.GetAsync("b");
        await cache.GetAsync("a");
        await cache.GetAsync("c");
        await cache.GetAsync("b");

        Assert.Equal(2, loads["b"]);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelSharedLoad()
    {
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new BoundedAsyncCache<string>(2, _ => gate.Task);
        using var cancellation = new CancellationTokenSource();
        var canceledCaller = cache.GetAsync("x", cancellation.Token);
        var survivingCaller = cache.GetAsync("x");

        cancellation.Cancel();
        gate.SetResult("value");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCaller);
        Assert.Equal("value", await survivingCaller);
    }

    [Fact]
    public async Task FailedValue_UsesCooldownInsteadOfRetryStorm()
    {
        var loads = 0;
        using var cache = new BoundedAsyncCache<string>(
            2,
            _ =>
            {
                Interlocked.Increment(ref loads);
                return Task.FromResult<string?>(null);
            },
            TimeSpan.FromMinutes(1));

        Assert.Null(await cache.GetAsync("missing"));
        Assert.Null(await cache.GetAsync("missing"));

        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Clear_DropsCompletedEntries()
    {
        var loads = 0;
        using var cache = new BoundedAsyncCache<string>(2, key =>
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult<string?>($"{key}-{loads}");
        });

        Assert.Equal("item-1", await cache.GetAsync("item"));
        cache.Clear();
        Assert.Equal("item-2", await cache.GetAsync("item"));
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Clear_DoesNotAllowAnOlderInFlightLoadToRepopulateCache()
    {
        var gates = new[]
        {
            new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var started = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var loads = 0;
        using var cache = new BoundedAsyncCache<string>(2, _ =>
        {
            var index = Interlocked.Increment(ref loads) - 1;
            started[index].SetResult();
            return gates[index].Task;
        });

        var oldRequest = cache.GetAsync("item");
        await started[0].Task;
        cache.Clear();
        var currentRequest = cache.GetAsync("item");
        await started[1].Task;

        gates[0].SetResult("old");
        Assert.Equal("old", await oldRequest);
        Assert.Equal(0, cache.Count);

        gates[1].SetResult("current");
        Assert.Equal("current", await currentRequest);
        Assert.Equal("current", await cache.GetAsync("item"));
        Assert.Equal(2, loads);
    }
}

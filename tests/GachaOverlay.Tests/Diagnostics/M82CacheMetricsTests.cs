using GachaOverlay.Core.Caching;

namespace GachaOverlay.Tests.Diagnostics;

public sealed class M82CacheMetricsTests
{
    [Fact]
    public async Task Cache_observability_preserves_bounding_eviction_and_generation_safety()
    {
        var observations = new List<BoundedCacheEvent>();
        using var cache = new BoundedAsyncCache<Value>(
            2,
            key => Task.FromResult<Value?>(new Value(key, long.MaxValue)),
            observer: observations.Add);

        await cache.GetAsync("a");
        await cache.GetAsync("a");
        await cache.GetAsync("b");
        await cache.GetAsync("c");

        Assert.Equal(2, cache.Count);
        Assert.Contains(BoundedCacheEvent.Hit, observations);
        Assert.Equal(3, observations.Count(item => item == BoundedCacheEvent.Miss));
        Assert.Contains(BoundedCacheEvent.Evicted, observations);
        Assert.Equal(long.MaxValue, cache.EstimateSize(value => value.EstimatedBytes));
    }

    [Fact]
    public async Task Failure_cooldown_keys_are_bounded_under_unique_url_stress()
    {
        using var cache = new BoundedAsyncCache<Value>(
            8,
            _ => Task.FromResult<Value?>(null),
            failureCooldown: TimeSpan.FromHours(1));

        for (var index = 0; index < 1_000; index++)
        {
            Assert.Null(await cache.GetAsync($"failed-{index}"));
        }

        Assert.Equal(0, cache.Count);
        Assert.InRange(cache.FailureCooldownCount, 0, 8);
    }

    private sealed record Value(string Key, long EstimatedBytes);
}

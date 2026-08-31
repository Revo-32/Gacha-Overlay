using GachaOverlay.Infrastructure.Lifecycle;

namespace GachaOverlay.Tests.Lifecycle;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquire_RejectsConcurrentOwner_AndReleasesOnDispose()
    {
        var name = $@"Local\GachaOverlay.Tests.{Guid.NewGuid():N}";

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first, out var firstError));
        Assert.Null(firstError);
        Assert.NotNull(first);

        Assert.False(SingleInstanceGuard.TryAcquire(name, out var second, out var secondError));
        Assert.Null(secondError);
        Assert.Null(second);

        first!.Dispose();

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var afterRelease, out var thirdError));
        Assert.Null(thirdError);
        afterRelease!.Dispose();
    }
}

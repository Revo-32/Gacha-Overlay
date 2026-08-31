namespace GachaOverlay.Infrastructure.Discord.Connection;

public interface IReconnectDelayStrategy
{
    Task DelayAsync(int consecutiveFailureCount, CancellationToken cancellationToken);
}

public sealed class ExponentialReconnectDelayStrategy : IReconnectDelayStrategy
{
    private static readonly TimeSpan[] Delays =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    };

    public Task DelayAsync(int consecutiveFailureCount, CancellationToken cancellationToken)
    {
        var index = Math.Clamp(consecutiveFailureCount - 1, 0, Delays.Length - 1);
        return Task.Delay(Delays[index], cancellationToken);
    }
}

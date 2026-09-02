using Microsoft.Extensions.Hosting;

namespace LSOverlay.Backend.Chat;

internal sealed class ActiveChatStreamEvictionWorker : BackgroundService
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ActiveChatStreamRegistry _streams;

    public ActiveChatStreamEvictionWorker(ActiveChatStreamRegistry streams)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            _streams.EvictIdle();
        }
    }
}

using GachaOverlay.Core.Lifecycle;

namespace GachaOverlay.Infrastructure.Lifecycle;

public sealed class ApplicationLifetime : IApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _stoppingSource = new();
    private readonly CancellationToken _stoppingToken;
    private int _stopRequested;
    private int _disposed;

    public ApplicationLifetime()
    {
        _stoppingToken = _stoppingSource.Token;
    }

    public CancellationToken Stopping => _stoppingToken;

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
        {
            _stoppingSource.Cancel();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        _stoppingSource.Dispose();
    }
}

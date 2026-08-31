namespace GachaOverlay.Infrastructure.Lifecycle;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private int _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static bool TryAcquire(
        string mutexName,
        out SingleInstanceGuard? guard,
        out Exception? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        guard = null;
        error = null;

        try
        {
            var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            guard = new SingleInstanceGuard(mutex);
            return true;
        }
        catch (Exception exception)
        {
            error = exception;
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The owning thread may already have terminated during shutdown.
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}

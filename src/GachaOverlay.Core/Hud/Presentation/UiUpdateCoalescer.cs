namespace GachaOverlay.Core.Hud.Presentation;

public interface IUiCallbackScheduler
{
    void Schedule(Action callback);
}

public sealed class UiUpdateCoalescer : IDisposable
{
    private readonly IUiCallbackScheduler _scheduler;
    private readonly Action<int> _callback;
    private readonly Action<Exception>? _exceptionHandler;
    private int _requestCount;
    private int _scheduled;
    private int _disposed;

    public UiUpdateCoalescer(
        IUiCallbackScheduler scheduler,
        Action<int> callback,
        Action<Exception>? exceptionHandler = null)
    {
        _scheduler = scheduler;
        _callback = callback;
        _exceptionHandler = exceptionHandler;
    }

    public bool Request()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        Interlocked.Increment(ref _requestCount);
        ScheduleIfNeeded();
        return true;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        Interlocked.Exchange(ref _requestCount, 0);
    }

    private void ScheduleIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _scheduler.Schedule(Execute);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _scheduled, 0);
            HandleException(exception);
        }
    }

    private void Execute()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Exchange(ref _scheduled, 0);
            return;
        }

        var requests = Interlocked.Exchange(ref _requestCount, 0);
        try
        {
            if (requests > 0)
            {
                _callback(requests);
            }
        }
        catch (Exception exception)
        {
            HandleException(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _scheduled, 0);
            if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _requestCount) > 0)
            {
                ScheduleIfNeeded();
            }
        }
    }

    private void HandleException(Exception exception)
    {
        try
        {
            _exceptionHandler?.Invoke(exception);
        }
        catch
        {
        }
    }
}

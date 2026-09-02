namespace LSOverlay.Backend.Discord;

internal sealed class BackendCallbackDrainGate
{
    private readonly object _sync = new();
    private bool _accepting = true;
    private int _activeCallbacks;
    private TaskCompletionSource? _drained;

    public bool IsAccepting
    {
        get
        {
            lock (_sync)
            {
                return _accepting;
            }
        }
    }

    public bool TryEnter()
    {
        lock (_sync)
        {
            if (!_accepting)
            {
                return false;
            }

            _activeCallbacks++;
            return true;
        }
    }

    public void Exit()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            if (_activeCallbacks <= 0)
            {
                throw new InvalidOperationException("No callback is active.");
            }

            _activeCallbacks--;
            if (!_accepting && _activeCallbacks == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult();
    }

    public Task CloseAsync()
    {
        lock (_sync)
        {
            _accepting = false;
            if (_activeCallbacks == 0)
            {
                return Task.CompletedTask;
            }

            return (_drained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }
}

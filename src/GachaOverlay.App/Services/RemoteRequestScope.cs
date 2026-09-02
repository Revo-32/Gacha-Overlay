namespace GachaOverlay.App.Services;

internal enum RemoteRequestKind { ChannelSwitch, SalesResync, SalesAction }

// One operation of each kind per connection; retired connections cancel and join
// their requests before the HTTP client is disposed. No queue of obsolete work.
internal sealed class RemoteRequestScope : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly Dictionary<RemoteRequestKind, Task> _pending = new();
    private Task? _retirement;
    private bool _stopped;

    public RemoteRequestScope(CancellationToken parent) =>
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);

    public Task<T>? TryRun<T>(RemoteRequestKind kind, Func<CancellationToken, Task<T>> operation)
    {
        lock (_sync)
        {
            if (_stopped || _pending.ContainsKey(kind)) { return null; }
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(kind, settled.Task);
            return RunAsync(kind, operation, settled);
        }
    }

    private async Task<T> RunAsync<T>(RemoteRequestKind kind,
        Func<CancellationToken, Task<T>> operation, TaskCompletionSource settled)
    {
        try
        {
            var result = await operation(_cancellation.Token).ConfigureAwait(false);
            _cancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            lock (_sync)
            {
                _pending.Remove(kind);
                settled.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_retirement is not null) { return new ValueTask(_retirement); }
            _stopped = true;
            // Snapshot before cancellation: synchronous callbacks may settle requests.
            var pending = _pending.Values.ToArray();
            _retirement = RetireAsync(pending);
            return new ValueTask(_retirement);
        }
    }

    private async Task RetireAsync(Task[] pending)
    {
        _cancellation.Cancel();
        await Task.WhenAll(pending).ConfigureAwait(false);
        _cancellation.Dispose();
    }
}

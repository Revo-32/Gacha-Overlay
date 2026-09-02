namespace LSOverlay.Backend.Chat;

internal sealed class CanonicalMessageRefreshCoalescer
{
    public const int MaximumPendingMessages = 256;
    public static readonly TimeSpan CoalescingDelay = TimeSpan.FromMilliseconds(100);

    private readonly object _sync = new();
    private readonly Dictionary<(ulong ChannelId, ulong MessageId), Task> _pending = new();
    private readonly Func<ulong, ulong, CancellationToken, Task> _refresh;
    private readonly Action<ulong> _capacityExceeded;

    public CanonicalMessageRefreshCoalescer(
        Func<ulong, ulong, CancellationToken, Task> refresh,
        Action<ulong> capacityExceeded)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _capacityExceeded = capacityExceeded ??
            throw new ArgumentNullException(nameof(capacityExceeded));
    }

    public Task RequestAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken = default)
    {
        var key = (channelId, messageId);
        lock (_sync)
        {
            if (_pending.TryGetValue(key, out var pending))
            {
                return pending.WaitAsync(cancellationToken);
            }

            if (_pending.Count >= MaximumPendingMessages)
            {
                _capacityExceeded(channelId);
                return Task.CompletedTask;
            }

            var started = RunAsync(key);
            _pending.Add(key, started);
            return started.WaitAsync(cancellationToken);
        }
    }

    private async Task RunAsync((ulong ChannelId, ulong MessageId) key)
    {
        try
        {
            await Task.Delay(CoalescingDelay).ConfigureAwait(false);
            await _refresh(key.ChannelId, key.MessageId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _pending.Remove(key);
            }
        }
    }
}

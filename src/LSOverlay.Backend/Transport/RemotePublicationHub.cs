using System.Threading.Channels;
using LSOverlay.Backend.Events;
using LSOverlay.Backend.Presence;
using LSOverlay.Backend.Security;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Transport;

internal interface IRemotePresencePublisher
{
    void Publish(TrackedHostPresenceSnapshot snapshot);
}

internal enum ResumeDisposition
{
    Resumable,
    WrongGeneration,
    HistoryExpired,
    FutureSequence,
}

internal sealed record RemoteResumeResult(
    ResumeDisposition Disposition,
    RemoteSubscription? Subscription,
    long LatestSequence);

internal sealed class RemoteSubscription : IAsyncDisposable
{
    private readonly RemotePublicationHub _owner;
    private int _disposed;

    internal RemoteSubscription(
        RemotePublicationHub owner,
        Guid id,
        IReadOnlyList<ProtocolEventEnvelope> replay,
        Channel<ProtocolEventEnvelope> channel)
    {
        _owner = owner;
        Id = id;
        Replay = replay;
        Reader = channel.Reader;
        Writer = channel.Writer;
    }

    internal Guid Id { get; }
    internal ChannelWriter<ProtocolEventEnvelope> Writer { get; }
    public IReadOnlyList<ProtocolEventEnvelope> Replay { get; }
    public ChannelReader<ProtocolEventEnvelope> Reader { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.Remove(Id);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class RemotePublicationHub : IRemotePresencePublisher
{
    public const int DefaultJournalCapacity = 2048;
    public const int DefaultOutboundCapacity = 256;

    private readonly object _sync = new();
    private readonly ProtocolEventEnvelope?[] _journal;
    private readonly Dictionary<ulong, HostPresenceSnapshot> _hosts = new();
    private readonly Dictionary<ulong, int> _hostSlots = new();
    private readonly Dictionary<Guid, RemoteSubscription> _subscriptions = new();
    private readonly int _outboundCapacity;
    private int _start;
    private int _count;
    private long _sequence;

    public RemotePublicationHub(
        TrackedHostPresenceStore presenceStore,
        int journalCapacity = DefaultJournalCapacity,
        int outboundCapacity = DefaultOutboundCapacity,
        string? generation = null)
    {
        ArgumentNullException.ThrowIfNull(presenceStore);
        if (journalCapacity <= 0 || outboundCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(journalCapacity));
        }

        Generation = generation ?? Guid.NewGuid().ToString("N");
        _journal = new ProtocolEventEnvelope[journalCapacity];
        _outboundCapacity = outboundCapacity;
        foreach (var state in presenceStore.Snapshot())
        {
            var slot = presenceStore.GetStableIndex(state.HostId);
            _hostSlots.Add(state.HostId, slot);
            _hosts.Add(state.HostId, Map(slot, state));
        }
    }

    public string Generation { get; }

    public int JournalCapacity => _journal.Length;

    public int ActiveSubscriptions
    {
        get
        {
            lock (_sync)
            {
                return _subscriptions.Count;
            }
        }
    }

    public void Publish(TrackedHostPresenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            if (!_hostSlots.TryGetValue(snapshot.HostId, out var slot))
            {
                return;
            }

            var payload = Map(slot, snapshot);
            _hosts[snapshot.HostId] = payload;
            var envelope = new ProtocolEventEnvelope(
                OverlayTransportProtocol.Version,
                Generation,
                checked(++_sequence),
                OverlayTransportProtocol.HostPresenceChanged,
                payload);
            Append(envelope);

            List<Guid>? slow = null;
            foreach (var pair in _subscriptions)
            {
                if (!pair.Value.Writer.TryWrite(envelope))
                {
                    (slow ??= new List<Guid>()).Add(pair.Key);
                }
            }

            if (slow is not null)
            {
                foreach (var id in slow)
                {
                    if (_subscriptions.Remove(id, out var subscription))
                    {
                        subscription.Writer.TryComplete(
                            new InvalidOperationException("Slow client outbound queue is full."));
                    }
                }
            }
        }
    }

    public BootstrapResponse CaptureBootstrap(AuthenticatedClientIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_sync)
        {
            return new BootstrapResponse(
                OverlayTransportProtocol.Version,
                Generation,
                _sequence,
                identity.DiscordUserId,
                _hostSlots
                    .OrderBy(pair => pair.Value)
                    .Select(pair => _hosts[pair.Key])
                    .ToArray());
        }
    }

    public RemoteResumeResult PrepareResume(string generation, long afterSequence)
    {
        lock (_sync)
        {
            if (!string.Equals(generation, Generation, StringComparison.Ordinal))
            {
                return new RemoteResumeResult(
                    ResumeDisposition.WrongGeneration,
                    null,
                    _sequence);
            }

            if (afterSequence > _sequence || afterSequence < 0)
            {
                return new RemoteResumeResult(
                    ResumeDisposition.FutureSequence,
                    null,
                    _sequence);
            }

            if (_count > 0)
            {
                var earliest = _journal[_start]!.Sequence;
                if (afterSequence < earliest - 1)
                {
                    return new RemoteResumeResult(
                        ResumeDisposition.HistoryExpired,
                        null,
                        _sequence);
                }
            }

            var cutoff = _sequence;
            var replay = SnapshotJournal()
                .Where(item => item.Sequence > afterSequence && item.Sequence <= cutoff)
                .ToArray();
            var channel = Channel.CreateBounded<ProtocolEventEnvelope>(
                new BoundedChannelOptions(_outboundCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                });
            var id = Guid.NewGuid();
            var subscription = new RemoteSubscription(this, id, replay, channel);
            _subscriptions.Add(id, subscription);
            return new RemoteResumeResult(
                ResumeDisposition.Resumable,
                subscription,
                cutoff);
        }
    }

    internal IReadOnlyList<ProtocolEventEnvelope> SnapshotJournal()
    {
        lock (_sync)
        {
            var result = new ProtocolEventEnvelope[_count];
            for (var index = 0; index < _count; index++)
            {
                result[index] = _journal[(_start + index) % _journal.Length]!;
            }

            return result;
        }
    }

    internal void Remove(Guid id)
    {
        lock (_sync)
        {
            if (_subscriptions.Remove(id, out var subscription))
            {
                subscription.Writer.TryComplete();
            }
        }
    }

    private void Append(ProtocolEventEnvelope envelope)
    {
        var index = (_start + _count) % _journal.Length;
        if (_count == _journal.Length)
        {
            index = _start;
            _start = (_start + 1) % _journal.Length;
        }
        else
        {
            _count++;
        }

        _journal[index] = envelope;
    }

    private static HostPresenceSnapshot Map(
        int slot,
        TrackedHostPresenceSnapshot snapshot)
    {
        var state = snapshot.DiscordStatus switch
        {
            BackendDiscordPresenceStatus.AwaitingPresence =>
                HostPresenceState.AwaitingPresence,
            BackendDiscordPresenceStatus.Offline => HostPresenceState.Offline,
            _ when snapshot.GtaOnlineActive => HostPresenceState.GtaOnline,
            _ => HostPresenceState.OnlineButNotGtaOnline,
        };
        return new HostPresenceSnapshot(
            slot,
            state,
            state == HostPresenceState.GtaOnline ? snapshot.CurrentPlayers : null,
            state == HostPresenceState.GtaOnline ? snapshot.MaximumPlayers : null,
            snapshot.ObservedAt);
    }
}

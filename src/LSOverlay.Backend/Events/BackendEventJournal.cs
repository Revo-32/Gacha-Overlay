using GachaOverlay.Core.Providers;

namespace LSOverlay.Backend.Events;

internal sealed record BackendJournalEntry(
    OverlayEventPosition Position,
    IBackendSignal Signal);

internal sealed class BackendEventJournal
{
    public const int DefaultCapacity = 2048;

    private readonly object _sync = new();
    private readonly BackendJournalEntry?[] _entries;
    private readonly long _generation;
    private int _start;
    private int _count;
    private long _sequence;

    public BackendEventJournal(
        long generation,
        int capacity = DefaultCapacity)
    {
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _generation = generation;
        _entries = new BackendJournalEntry[capacity];
    }

    public int Capacity => _entries.Length;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public BackendJournalEntry Append(IBackendSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        lock (_sync)
        {
            var entry = new BackendJournalEntry(
                new OverlayEventPosition(
                    OverlayProtocolVersion.Current,
                    checked(++_sequence),
                    _generation),
                signal);
            var index = (_start + _count) % _entries.Length;
            if (_count == _entries.Length)
            {
                index = _start;
                _start = (_start + 1) % _entries.Length;
            }
            else
            {
                _count++;
            }

            _entries[index] = entry;
            return entry;
        }
    }

    public IReadOnlyList<BackendJournalEntry> Snapshot()
    {
        lock (_sync)
        {
            var result = new BackendJournalEntry[_count];
            for (var index = 0; index < _count; index++)
            {
                result[index] = _entries[(_start + index) % _entries.Length]!;
            }

            return result;
        }
    }
}

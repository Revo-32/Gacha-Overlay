namespace GachaOverlay.Core.Timers;

public enum TimerClockMode
{
    WallClock,
    OnlinePlaytime,
}

public enum SharedTimerState
{
    Inactive,
    Running,
    Paused,
    Ready,
    Completed,
}

public enum OnlinePlaytimeAvailability
{
    Unknown,
    Offline,
    Online,
}

public interface IOnlinePlaytimeStatusSource
{
    OnlinePlaytimeAvailability Current { get; }
}

public sealed record SharedTimerPersistedEntry(
    string TimerId,
    TimerClockMode ClockMode,
    TimeSpan RequiredDuration,
    DateTimeOffset? ReadyAtUtc,
    TimeSpan AccumulatedOnlineTime,
    SharedTimerState State,
    bool CompletionRaised,
    DateTimeOffset UpdatedAtUtc);

public sealed record SharedTimerSnapshot(
    string TimerId,
    TimerClockMode ClockMode,
    SharedTimerState State,
    TimeSpan RequiredDuration,
    TimeSpan AccumulatedOnlineTime,
    TimeSpan Remaining,
    DateTimeOffset? ReadyAtUtc);

public sealed record SharedTimerCompletion(string TimerId, TimerClockMode ClockMode);

public interface ISharedTimerStore
{
    IReadOnlyList<SharedTimerPersistedEntry> Load();

    bool Save(IReadOnlyCollection<SharedTimerPersistedEntry> entries);
}

public sealed class SharedTimerRegistry
{
    public const int DefaultCapacity = 64;
    private readonly object _sync = new();
    private readonly ISharedTimerStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly Dictionary<string, SharedTimerPersistedEntry> _entries =
        new(StringComparer.Ordinal);
    private long _lastTimestamp;
    private OnlinePlaytimeAvailability _lastOnlineAvailability =
        OnlinePlaytimeAvailability.Unknown;

    public SharedTimerRegistry(
        ISharedTimerStore store,
        TimeProvider? timeProvider = null,
        int capacity = DefaultCapacity)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastTimestamp = _timeProvider.GetTimestamp();
        foreach (var entry in SafeLoad()
                     .Where(IsValid)
                     .OrderByDescending(entry => entry.UpdatedAtUtc)
                     .Take(capacity)
                     .OrderBy(entry => entry.UpdatedAtUtc))
        {
            _entries[entry.TimerId] = Normalize(entry);
        }
    }

    public event Action<SharedTimerCompletion>? Completed;

    public void Start(string timerId, TimerClockMode mode, TimeSpan duration)
    {
        var id = NormalizeId(timerId);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(365))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        lock (_sync)
        {
            EnsureCapacityFor(id);
            var now = _timeProvider.GetUtcNow();
            _entries[id] = new SharedTimerPersistedEntry(
                id,
                mode,
                duration,
                mode == TimerClockMode.WallClock ? now + duration : null,
                TimeSpan.Zero,
                mode == TimerClockMode.OnlinePlaytime &&
                    _lastOnlineAvailability != OnlinePlaytimeAvailability.Online
                        ? SharedTimerState.Paused
                        : SharedTimerState.Running,
                CompletionRaised: false,
                now);
            PersistUnderLock();
        }
    }

    public bool Stop(string timerId)
    {
        var id = NormalizeId(timerId);
        lock (_sync)
        {
            if (!_entries.Remove(id))
            {
                return false;
            }

            PersistUnderLock();
            return true;
        }
    }

    public bool MarkCompleted(string timerId)
    {
        var id = NormalizeId(timerId);
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var entry) ||
                entry.State != SharedTimerState.Ready)
            {
                return false;
            }

            _entries[id] = entry with
            {
                State = SharedTimerState.Completed,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
            };
            PersistUnderLock();
            return true;
        }
    }

    public IReadOnlyList<SharedTimerSnapshot> Update(IOnlinePlaytimeStatusSource onlineStatus) =>
        Update(onlineStatus?.Current ?? OnlinePlaytimeAvailability.Unknown);

    public IReadOnlyList<SharedTimerSnapshot> Update(OnlinePlaytimeAvailability onlineAvailability)
    {
        if (!Enum.IsDefined(onlineAvailability))
        {
            onlineAvailability = OnlinePlaytimeAvailability.Unknown;
        }

        List<SharedTimerCompletion> completions = [];
        SharedTimerSnapshot[] snapshots;
        lock (_sync)
        {
            var timestamp = _timeProvider.GetTimestamp();
            var elapsed = _timeProvider.GetElapsedTime(_lastTimestamp, timestamp);
            _lastTimestamp = timestamp;
            var now = _timeProvider.GetUtcNow();
            var changed = false;
            foreach (var pair in _entries.ToArray())
            {
                var entry = pair.Value;
                if (entry.State is SharedTimerState.Ready or SharedTimerState.Completed)
                {
                    continue;
                }

                if (entry.ClockMode == TimerClockMode.WallClock)
                {
                    var ready = entry.ReadyAtUtc <= now;
                    var next = entry with
                    {
                        State = ready ? SharedTimerState.Ready : SharedTimerState.Running,
                        CompletionRaised = entry.CompletionRaised || ready,
                        UpdatedAtUtc = now,
                    };
                    _entries[pair.Key] = next;
                    changed |= next != entry;
                    if (ready && !entry.CompletionRaised)
                    {
                        completions.Add(new SharedTimerCompletion(entry.TimerId, entry.ClockMode));
                    }
                }
                else
                {
                    var accumulated = entry.AccumulatedOnlineTime;
                    if (_lastOnlineAvailability == OnlinePlaytimeAvailability.Online &&
                        elapsed > TimeSpan.Zero)
                    {
                        accumulated = Min(entry.RequiredDuration, accumulated + elapsed);
                    }

                    var ready = accumulated >= entry.RequiredDuration;
                    var next = entry with
                    {
                        AccumulatedOnlineTime = accumulated,
                        State = ready
                            ? SharedTimerState.Ready
                            : onlineAvailability == OnlinePlaytimeAvailability.Online
                                ? SharedTimerState.Running
                                : SharedTimerState.Paused,
                        CompletionRaised = entry.CompletionRaised || ready,
                        UpdatedAtUtc = now,
                    };
                    _entries[pair.Key] = next;
                    changed |= next != entry;
                    if (ready && !entry.CompletionRaised)
                    {
                        completions.Add(new SharedTimerCompletion(entry.TimerId, entry.ClockMode));
                    }
                }
            }

            _lastOnlineAvailability = onlineAvailability;
            if (changed)
            {
                PersistUnderLock();
            }

            snapshots = _entries.Values
                .OrderBy(entry => entry.TimerId, StringComparer.Ordinal)
                .Select(entry => ToSnapshot(entry, now))
                .ToArray();
        }

        foreach (var completion in completions)
        {
            Completed?.Invoke(completion);
        }

        return snapshots;
    }

    private IReadOnlyList<SharedTimerPersistedEntry> SafeLoad()
    {
        try
        {
            return _store.Load() ?? Array.Empty<SharedTimerPersistedEntry>();
        }
        catch
        {
            return Array.Empty<SharedTimerPersistedEntry>();
        }
    }

    private void PersistUnderLock()
    {
        try
        {
            _store.Save(_entries.Values.ToArray());
        }
        catch
        {
        }
    }

    private void EnsureCapacityFor(string id)
    {
        if (_entries.ContainsKey(id) || _entries.Count < _capacity)
        {
            return;
        }

        var remove = _entries.Values
            .OrderBy(entry => entry.State == SharedTimerState.Completed ? 0 : 1)
            .ThenBy(entry => entry.UpdatedAtUtc)
            .First();
        _entries.Remove(remove.TimerId);
    }

    private static SharedTimerSnapshot ToSnapshot(
        SharedTimerPersistedEntry entry,
        DateTimeOffset now)
    {
        var remaining = entry.ClockMode == TimerClockMode.WallClock
            ? Max(TimeSpan.Zero, (entry.ReadyAtUtc ?? now) - now)
            : Max(TimeSpan.Zero, entry.RequiredDuration - entry.AccumulatedOnlineTime);
        return new SharedTimerSnapshot(
            entry.TimerId,
            entry.ClockMode,
            entry.State,
            entry.RequiredDuration,
            entry.AccumulatedOnlineTime,
            remaining,
            entry.ReadyAtUtc);
    }

    private static bool IsValid(SharedTimerPersistedEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.TimerId) &&
        entry.TimerId.Length <= 128 &&
        Enum.IsDefined(entry.ClockMode) &&
        entry.RequiredDuration > TimeSpan.Zero &&
        entry.RequiredDuration <= TimeSpan.FromDays(365) &&
        entry.AccumulatedOnlineTime >= TimeSpan.Zero &&
        entry.AccumulatedOnlineTime <= entry.RequiredDuration &&
        (entry.ClockMode != TimerClockMode.WallClock || entry.ReadyAtUtc.HasValue) &&
        entry.UpdatedAtUtc != default;

    private static SharedTimerPersistedEntry Normalize(SharedTimerPersistedEntry entry) =>
        entry with
        {
            TimerId = entry.TimerId.Trim(),
            ReadyAtUtc = entry.ReadyAtUtc?.ToUniversalTime(),
            UpdatedAtUtc = entry.UpdatedAtUtc.ToUniversalTime(),
            State = entry.State == SharedTimerState.Inactive
                ? SharedTimerState.Running
                : Enum.IsDefined(entry.State)
                    ? entry.State
                    : SharedTimerState.Running,
        };

    private static string NormalizeId(string timerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timerId);
        var id = timerId.Trim();
        if (id.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(timerId));
        }

        return id;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
}

using LSOverlay.Backend.Events;

namespace LSOverlay.Backend.Presence;

internal sealed class TrackedHostPresenceStore
{
    private readonly object _sync = new();
    private readonly Dictionary<ulong, TrackedHostPresenceSnapshot> _states;
    private readonly Dictionary<ulong, int> _indexes;

    public TrackedHostPresenceStore(
        IReadOnlyList<ulong> trackedHostIds,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(trackedHostIds);
        if (trackedHostIds.Count > Configuration.BackendConfiguration.MaximumSessionHosts)
        {
            throw new ArgumentOutOfRangeException(nameof(trackedHostIds));
        }

        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        _states = new Dictionary<ulong, TrackedHostPresenceSnapshot>(trackedHostIds.Count);
        _indexes = new Dictionary<ulong, int>(trackedHostIds.Count);
        for (var index = 0; index < trackedHostIds.Count; index++)
        {
            var id = trackedHostIds[index];
            if (id == 0 || _states.ContainsKey(id))
            {
                throw new ArgumentException(
                    "Tracked hosts must contain unique non-zero IDs.",
                    nameof(trackedHostIds));
            }

            _indexes[id] = index + 1;
            _states[id] = new TrackedHostPresenceSnapshot(
                id,
                BackendDiscordPresenceStatus.AwaitingPresence,
                false,
                false,
                null,
                null,
                now);
        }
    }

    public int Count => _states.Count;

    public bool IsTracked(ulong hostId) => _indexes.ContainsKey(hostId);

    public int GetStableIndex(ulong hostId) =>
        _indexes.TryGetValue(hostId, out var index) ? index : 0;

    public bool TryUpdate(
        TrackedHostPresenceSnapshot next,
        out TrackedHostPresenceSnapshot? changed)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (_sync)
        {
            if (!_states.TryGetValue(next.HostId, out var previous))
            {
                changed = null;
                return false;
            }

            if (previous.SemanticallyEquals(next))
            {
                changed = null;
                return false;
            }

            _states[next.HostId] = next;
            changed = next;
            return true;
        }
    }

    public IReadOnlyList<TrackedHostPresenceSnapshot> Snapshot()
    {
        lock (_sync)
        {
            return _indexes
                .OrderBy(pair => pair.Value)
                .Select(pair => _states[pair.Key])
                .ToArray();
        }
    }
}

namespace GachaOverlay.Core.Timers;

public enum GtaoTimerSlot
{
    General,
    Bunker,
    Lsd,
}

public static class GtaoTimerPresets
{
    public static readonly IReadOnlyList<int> General = [12, 24, 48];
    public static readonly IReadOnlyList<int> Bunker = [40, 130];
    public static readonly IReadOnlyList<int> Lsd = [90, 150];

    public static int Normalize(GtaoTimerSlot slot, int minutes) =>
        Values(slot).Contains(minutes) ? minutes : Values(slot)[0];

    public static IReadOnlyList<int> Values(GtaoTimerSlot slot) => slot switch
    {
        GtaoTimerSlot.General => General,
        GtaoTimerSlot.Bunker => Bunker,
        GtaoTimerSlot.Lsd => Lsd,
        _ => General,
    };
}

public sealed record GtaoTimerSnapshot(
    GtaoTimerSlot Slot,
    bool IsVisible,
    bool IsExpired,
    TimeSpan Remaining);

public sealed class GtaoTimerEngine
{
    public static readonly TimeSpan ExpiryEmphasisDuration = TimeSpan.FromSeconds(4);
    private readonly Dictionary<GtaoTimerSlot, Entry> _entries = new();

    public void Start(GtaoTimerSlot slot, TimeSpan duration, TimeSpan now)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _entries[slot] = new Entry(now + duration, null);
    }

    public IReadOnlyList<GtaoTimerSnapshot> Read(TimeSpan now)
    {
        var result = new List<GtaoTimerSnapshot>();
        foreach (var slot in Enum.GetValues<GtaoTimerSlot>())
        {
            if (!_entries.TryGetValue(slot, out var entry))
            {
                continue;
            }

            var remaining = entry.Deadline - now;
            if (remaining > TimeSpan.Zero)
            {
                result.Add(new GtaoTimerSnapshot(slot, true, false, remaining));
                continue;
            }

            var expiredAt = entry.ExpiredAt ?? now;
            if (entry.ExpiredAt is null)
            {
                entry = entry with { ExpiredAt = expiredAt };
                _entries[slot] = entry;
            }

            if (now - expiredAt < ExpiryEmphasisDuration)
            {
                result.Add(new GtaoTimerSnapshot(slot, true, true, TimeSpan.Zero));
            }
            else
            {
                _entries.Remove(slot);
            }
        }

        return result;
    }

    public static string FormatRemaining(TimeSpan remaining)
    {
        var totalSeconds = Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds));
        var value = TimeSpan.FromSeconds(totalSeconds);
        return totalSeconds >= 3600
            ? $"{(long)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private sealed record Entry(TimeSpan Deadline, TimeSpan? ExpiredAt);
}

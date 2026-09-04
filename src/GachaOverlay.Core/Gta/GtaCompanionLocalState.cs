namespace GachaOverlay.Core.Gta;

public sealed record GtaDailySlotState(
    int Slot,
    string? ChallengeId,
    string? CustomText,
    bool Completed);

public sealed record GtaCompanionLocalState(
    int SchemaVersion,
    string DailyCycleKey,
    IReadOnlyList<GtaDailySlotState> DailySlots,
    string WeeklyCycleKey,
    string? WeeklyChallengeKey,
    bool WeeklyCompleted)
{
    public const int CurrentSchemaVersion = 1;

    public static GtaCompanionLocalState CreateDefault(KstResetSchedule schedule, DateTimeOffset now) => new(
        CurrentSchemaVersion,
        schedule.GetDailyCycleKey(now),
        Enumerable.Range(1, 3).Select(slot => new GtaDailySlotState(slot, null, null, false)).ToArray(),
        schedule.GetWeeklyCycleKey(now),
        null,
        false);
}

public interface IGtaCompanionStateStore
{
    GtaCompanionLocalState? Load();

    bool Save(GtaCompanionLocalState state);
}

public sealed class GtaCompanionStateManager
{
    private readonly object _sync = new();
    private readonly IGtaCompanionStateStore _store;
    private readonly KstResetSchedule _schedule;
    private GtaCompanionLocalState _current;

    public GtaCompanionStateManager(
        IGtaCompanionStateStore store,
        DateTimeOffset now,
        KstResetSchedule? schedule = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _schedule = schedule ?? new KstResetSchedule();
        _current = Normalize(store.Load(), now);
        _ = PersistIfResetRequired(now);
    }

    public event Action<GtaCompanionLocalState>? Changed;

    public GtaCompanionLocalState Current
    {
        get { lock (_sync) return _current; }
    }

    public bool ApplyTime(DateTimeOffset now) => PersistIfResetRequired(now);

    public bool SelectDaily(int slot, string? challengeId, string? customText, DateTimeOffset now)
    {
        if (slot is < 1 or > 3)
        {
            return false;
        }

        ApplyTime(now);
        var normalizedId = NormalizeId(challengeId);
        var normalizedCustom = normalizedId == GtaDailyChallengeCatalog.CustomChallengeId
            ? NormalizeCustom(customText)
            : null;
        if (normalizedId is not null && normalizedId != GtaDailyChallengeCatalog.CustomChallengeId &&
            !GtaDailyChallengeCatalog.SearchableEntries.Any(entry => entry.ChallengeId == normalizedId))
        {
            return false;
        }

        lock (_sync)
        {
            var duplicate = _current.DailySlots.Any(item => item.Slot != slot &&
                ((normalizedId is not null && normalizedId != GtaDailyChallengeCatalog.CustomChallengeId &&
                  item.ChallengeId == normalizedId) ||
                 (normalizedId == GtaDailyChallengeCatalog.CustomChallengeId &&
                  NormalizeCustom(item.CustomText) == normalizedCustom && normalizedCustom is not null)));
            if (duplicate)
            {
                return false;
            }

            var slots = _current.DailySlots.Select(item => item.Slot == slot
                ? new GtaDailySlotState(slot, normalizedId, normalizedCustom, false)
                : item).ToArray();
            return SaveLocked(_current with { DailySlots = slots });
        }
    }

    public bool ToggleDailyCompletion(int slot, DateTimeOffset now)
    {
        ApplyTime(now);
        lock (_sync)
        {
            var selected = _current.DailySlots.FirstOrDefault(item => item.Slot == slot);
            if (selected?.ChallengeId is null ||
                selected.ChallengeId == GtaDailyChallengeCatalog.CustomChallengeId &&
                string.IsNullOrWhiteSpace(selected.CustomText))
            {
                return false;
            }

            var slots = _current.DailySlots.Select(item => item.Slot == slot
                ? item with { Completed = !item.Completed }
                : item).ToArray();
            return SaveLocked(_current with { DailySlots = slots });
        }
    }

    public bool ObserveWeeklyChallenge(string? challengeKey, DateTimeOffset now)
    {
        ApplyTime(now);
        var normalized = NormalizeId(challengeKey);
        lock (_sync)
        {
            if (_current.WeeklyChallengeKey == normalized)
            {
                return true;
            }

            return SaveLocked(_current with
            {
                WeeklyChallengeKey = normalized,
                WeeklyCompleted = false,
            });
        }
    }

    public bool ToggleWeeklyCompletion(DateTimeOffset now)
    {
        ApplyTime(now);
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_current.WeeklyChallengeKey))
            {
                return false;
            }

            return SaveLocked(_current with { WeeklyCompleted = !_current.WeeklyCompleted });
        }
    }

    private bool PersistIfResetRequired(DateTimeOffset now)
    {
        GtaCompanionLocalState? changed = null;
        lock (_sync)
        {
            var dailyKey = _schedule.GetDailyCycleKey(now);
            var weeklyKey = _schedule.GetWeeklyCycleKey(now);
            var next = _current;
            if (next.DailyCycleKey != dailyKey)
            {
                next = next with
                {
                    DailyCycleKey = dailyKey,
                    DailySlots = Enumerable.Range(1, 3)
                        .Select(slot => new GtaDailySlotState(slot, null, null, false))
                        .ToArray(),
                };
            }

            if (next.WeeklyCycleKey != weeklyKey)
            {
                next = next with
                {
                    WeeklyCycleKey = weeklyKey,
                    WeeklyChallengeKey = null,
                    WeeklyCompleted = false,
                };
            }

            if (next != _current && _store.Save(next))
            {
                _current = next;
                changed = next;
            }
        }

        if (changed is not null) Changed?.Invoke(changed);
        return changed is not null;
    }

    private bool SaveLocked(GtaCompanionLocalState next)
    {
        if (next == _current)
        {
            return true;
        }

        if (!_store.Save(next))
        {
            return false;
        }

        _current = next;
        Changed?.Invoke(next);
        return true;
    }

    private GtaCompanionLocalState Normalize(GtaCompanionLocalState? source, DateTimeOffset now)
    {
        var fallback = GtaCompanionLocalState.CreateDefault(_schedule, now);
        if (source is null || source.SchemaVersion != GtaCompanionLocalState.CurrentSchemaVersion)
        {
            return fallback;
        }

        var bySlot = (source.DailySlots ?? Array.Empty<GtaDailySlotState>())
            .Where(item => item.Slot is >= 1 and <= 3)
            .GroupBy(item => item.Slot)
            .ToDictionary(group => group.Key, group => group.First());
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var usedCustom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = Enumerable.Range(1, 3).Select(slot =>
        {
            if (!bySlot.TryGetValue(slot, out var item))
            {
                return new GtaDailySlotState(slot, null, null, false);
            }

            var id = NormalizeId(item.ChallengeId);
            var custom = id == GtaDailyChallengeCatalog.CustomChallengeId
                ? NormalizeCustom(item.CustomText)
                : null;
            var valid = id is null || id == GtaDailyChallengeCatalog.CustomChallengeId &&
                custom is not null && usedCustom.Add(custom) ||
                id != GtaDailyChallengeCatalog.CustomChallengeId &&
                GtaDailyChallengeCatalog.SearchableEntries.Any(entry => entry.ChallengeId == id) &&
                usedIds.Add(id);
            return valid
                ? new GtaDailySlotState(slot, id, custom, item.Completed && id is not null)
                : new GtaDailySlotState(slot, null, null, false);
        }).ToArray();
        return source with
        {
            SchemaVersion = GtaCompanionLocalState.CurrentSchemaVersion,
            DailyCycleKey = string.IsNullOrWhiteSpace(source.DailyCycleKey)
                ? fallback.DailyCycleKey
                : source.DailyCycleKey,
            DailySlots = slots,
            WeeklyCycleKey = string.IsNullOrWhiteSpace(source.WeeklyCycleKey)
                ? fallback.WeeklyCycleKey
                : source.WeeklyCycleKey,
            WeeklyChallengeKey = NormalizeId(source.WeeklyChallengeKey),
            WeeklyCompleted = source.WeeklyCompleted && !string.IsNullOrWhiteSpace(source.WeeklyChallengeKey),
        };
    }

    private static string? NormalizeId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= 128 ? value.Trim() : null;

    private static string? NormalizeCustom(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = string.Join(' ', value.Trim().Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 160 ? normalized : normalized[..160].TrimEnd();
    }
}

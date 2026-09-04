using System.Text.Json;

namespace GachaOverlay.Core.Gta;

public sealed class GtaEventResolver
{
    public const int MaximumCampaigns = 8;
    private static readonly JsonSerializerOptions ComparisonJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly KstResetSchedule _schedule;
    private GtaTrustedEventState _state = GtaTrustedEventState.Empty;

    public GtaEventResolver(KstResetSchedule? schedule = null)
    {
        _schedule = schedule ?? new KstResetSchedule();
    }

    public GtaTrustedEventState TrustedState => _state;

    public bool Restore(GtaTrustedEventState? state, DateTimeOffset now)
    {
        _state = Normalize(state);
        return EvaluateTransitions(now);
    }

    public bool ApplyWeek(GtaEventWeek week, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(week);
        var currentKey = _schedule.GetWeeklyCycleKey(now);
        var next = ReconcileSameSource(_state, week, now);
        if (string.CompareOrdinal(week.WeekKey, currentKey) > 0)
        {
            if (next.StagedWeek is not { } staged || staged.WeekKey != week.WeekKey ||
                staged.ParsedAt <= week.ParsedAt)
            {
                if (!SemanticEquals(next.StagedWeek, week))
                {
                    next = next with { StagedWeek = week, LastUpdatedAt = now.ToUniversalTime() };
                }
            }
        }
        else if (string.CompareOrdinal(week.WeekKey, currentKey) == 0)
        {
            if (next.ActiveWeek is not { } active || active.WeekKey != week.WeekKey ||
                active.ParsedAt <= week.ParsedAt)
            {
                if (!SemanticEquals(next.ActiveWeek, week))
                {
                    next = next with { ActiveWeek = week, LastUpdatedAt = now.ToUniversalTime() };
                }
            }
        }
        else if (next.ActiveWeek is null)
        {
            // Retain an older trusted source as Last-Good without presenting it as current.
            next = next with { ActiveWeek = week, LastUpdatedAt = now.ToUniversalTime() };
        }

        var changed = !SemanticEquals(_state, next);
        _state = next;
        changed |= EvaluateTransitions(now);
        return changed;
    }

    private static GtaTrustedEventState ReconcileSameSource(
        GtaTrustedEventState state,
        GtaEventWeek week,
        DateTimeOffset now)
    {
        var removeActive = state.ActiveWeek is { } active &&
            active.SourceMessageId == week.SourceMessageId &&
            active.WeekKey != week.WeekKey;
        var removeStaged = state.StagedWeek is { } staged &&
            staged.SourceMessageId == week.SourceMessageId &&
            staged.WeekKey != week.WeekKey;
        return removeActive || removeStaged
            ? state with
            {
                ActiveWeek = removeActive ? null : state.ActiveWeek,
                StagedWeek = removeStaged ? null : state.StagedWeek,
                LastUpdatedAt = now.ToUniversalTime(),
            }
            : state;
    }

    public bool ApplyCampaign(GtaEventCampaign campaign, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var existing = _state.RelevantCampaigns.FirstOrDefault(item =>
            item.CampaignKey == campaign.CampaignKey);
        if (SemanticEquals(existing, campaign))
        {
            return false;
        }

        var campaigns = _state.RelevantCampaigns
            .Where(item => item.CampaignKey != campaign.CampaignKey)
            .Append(campaign)
            .OrderByDescending(item => item.StartAt ?? item.ParsedAt)
            .Take(MaximumCampaigns)
            .ToArray();
        var next = _state with
        {
            RelevantCampaigns = campaigns,
            LastUpdatedAt = now.ToUniversalTime(),
        };
        _state = next;
        return true;
    }

    public bool EvaluateTransitions(DateTimeOffset now)
    {
        var currentKey = _schedule.GetWeeklyCycleKey(now);
        if (_state.StagedWeek is null ||
            string.CompareOrdinal(_state.StagedWeek.WeekKey, currentKey) > 0)
        {
            return false;
        }

        var next = _state with
        {
            ActiveWeek = _state.StagedWeek,
            StagedWeek = null,
            LastUpdatedAt = now.ToUniversalTime(),
        };
        if (SemanticEquals(_state, next))
        {
            return false;
        }

        _state = next;
        return true;
    }

    public GtaResolvedEventState Resolve(DateTimeOffset now)
    {
        EvaluateTransitions(now);
        var currentKey = _schedule.GetWeeklyCycleKey(now);
        var current = _state.ActiveWeek?.WeekKey == currentKey ? _state.ActiveWeek : null;
        var reset = _schedule.GetWeeklyCycleStart(now);
        var availability = current is not null
            ? GtaResolvedAvailability.Available
            : now - reset <= KstResetSchedule.WeeklyPreparationGrace
                ? GtaResolvedAvailability.Preparing
                : GtaResolvedAvailability.Unavailable;
        var localNow = _schedule.ToKst(now);
        var campaign = _state.RelevantCampaigns
            .Where(item => item.EndAt is null || item.EndAt >= localNow)
            .OrderBy(item => item.StartAt ?? item.ParsedAt)
            .FirstOrDefault();
        return new GtaResolvedEventState(availability, current, campaign, now.ToUniversalTime());
    }

    private static GtaTrustedEventState Normalize(GtaTrustedEventState? state)
    {
        if (state is null || state.SchemaVersion != GtaTrustedEventState.CurrentSchemaVersion)
        {
            return GtaTrustedEventState.Empty;
        }

        return state with
        {
            RelevantCampaigns = (state.RelevantCampaigns ?? Array.Empty<GtaEventCampaign>())
                .DistinctBy(campaign => campaign.CampaignKey)
                .Take(MaximumCampaigns)
                .ToArray(),
        };
    }

    private static bool SemanticEquals<T>(T? left, T? right) =>
        JsonSerializer.Serialize(left, ComparisonJson) == JsonSerializer.Serialize(right, ComparisonJson);
}

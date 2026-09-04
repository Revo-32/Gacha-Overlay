using GachaOverlay.Core.Timers;

namespace GachaOverlay.Core.Business;

public static class BusinessTimerIds
{
    public const string Bunker = "business.bunker.supply";
    public const string Acid = "business.acid.supply";
    public const string AcidBoost = "business.acid.boost";
    public const string AcidBoostAllowance = "business.acid.boost.allowance";
    public const string MansionBunker = "business.mansion.bunker";
    public const string MansionAcid = "business.mansion.acid";
    public const string Nightclub = "business.nightclub.popularity";
    public const string CarWash = "business.carwash.heat";
    public const string AirFreight = "business.airfreight.staff";
    public const string CayoHardMode = "business.heist.cayo.hard";
    public const string KortzHardMode = "business.heist.kortz.hard";

    public static string Cargo(int slot) => $"business.cargo.{Math.Clamp(slot, 1, 5)}";
    public static string Heist(BusinessHeistKind kind) => $"business.heist.{kind.ToString().ToLowerInvariant()}";
}

public sealed record AcidBoostState(
    DateTimeOffset ActivatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    double RemainingBoostedProductUnits,
    bool IsActive);

public sealed class BusinessManagerEngine : IDisposable
{
    private const int PersistenceIntervalUpdates = 30;
    private readonly SharedTimerRegistry _timers;
    private IReadOnlyDictionary<string, SharedTimerSnapshot> _last =
        new Dictionary<string, SharedTimerSnapshot>(StringComparer.Ordinal);
    private OnlinePlaytimeAvailability _availability = OnlinePlaytimeAvailability.Unknown;
    private int _updatesSincePersistence;
    private bool _disposed;

    public BusinessManagerEngine(SharedTimerRegistry timers)
    {
        _timers = timers ?? throw new ArgumentNullException(nameof(timers));
        _timers.Completed += OnCompleted;
        _last = _timers.Update(OnlinePlaytimeAvailability.Unknown)
            .ToDictionary(item => item.TimerId, StringComparer.Ordinal);
        ReconcilePersistedAcidBoost();
        _last = _timers.Update(OnlinePlaytimeAvailability.Unknown)
            .ToDictionary(item => item.TimerId, StringComparer.Ordinal);
    }

    public event Action<SharedTimerCompletion>? Ready;
    public event Action<SharedTimerSnapshot>? EarlyAlert;

    public IReadOnlyList<SharedTimerSnapshot> Update(
        OnlinePlaytimeAvailability availability,
        int earlyAlertMinutes = 0)
    {
        ThrowIfDisposed();
        _availability = availability;
        var persist = ++_updatesSincePersistence >= PersistenceIntervalUpdates;
        _timers.Update(availability, CalculateOnlineProgress, persist);
        var snapshots = _timers.Update(availability, CalculateOnlineProgress, persistChanges: false);
        if (persist) _updatesSincePersistence = 0;
        _last = snapshots.ToDictionary(item => item.TimerId, StringComparer.Ordinal);
        RaiseEarlyAlerts(snapshots, NormalizeEarlyAlertMinutes(earlyAlertMinutes));
        return snapshots;
    }

    public static int NormalizeEarlyAlertMinutes(int value) => value is 5 or 10 ? value : 0;

    public TimeSpan EstimateRemaining(SharedTimerSnapshot timer, DateTimeOffset now)
    {
        if (timer.ClockMode == TimerClockMode.WallClock ||
            (timer.TimerId != BusinessTimerIds.Bunker && timer.TimerId != BusinessTimerIds.Acid))
            return timer.Remaining;

        if (timer.TimerId == BusinessTimerIds.Acid)
        {
            var simulated = SimulateAcidProduction(
                now,
                now + TimeSpan.FromDays(365),
                timer.Remaining);
            return simulated.SupplyDepleted
                ? simulated.ElapsedUntilSupplyDepleted
                : timer.Remaining;
        }

        var remainingWork = timer.Remaining.TotalSeconds;
        var cursor = now;
        foreach (var boundary in ModifierEndTimes(timer.TimerId, now))
        {
            var rate = CurrentRate(timer.TimerId, cursor);
            var availableSeconds = Math.Max(0, (boundary - cursor).TotalSeconds);
            var completedWork = availableSeconds * rate;
            if (completedWork >= remainingWork)
                return TimeSpan.FromSeconds(remainingWork / rate);
            remainingWork -= completedWork;
            cursor = boundary;
        }

        return cursor - now + TimeSpan.FromSeconds(remainingWork);
    }

    public void StartBunker() => StartOnline(BusinessTimerIds.Bunker, BusinessMechanicCatalog.BunkerNormalSupply);
    public void StartAcid() => StartOnline(BusinessTimerIds.Acid, BusinessMechanicCatalog.AcidNormalSupply);
    public void StartAcidBoost()
    {
        ThrowIfDisposed();
        _timers.Stop(BusinessTimerIds.AcidBoost);
        _timers.Stop(BusinessTimerIds.AcidBoostAllowance);
        _timers.Start(BusinessTimerIds.AcidBoost, TimerClockMode.WallClock,
            BusinessMechanicCatalog.AcidBoostExpiration);
        _timers.Start(BusinessTimerIds.AcidBoostAllowance, TimerClockMode.OnlinePlaytime,
            BusinessMechanicCatalog.AcidBoostAllowanceWork);
        Capture();
    }

    public AcidBoostState? GetAcidBoostState()
    {
        if (!TryGetAcidBoost(out var window, out var allowance) || window!.ReadyAtUtc is not { } expires)
            return null;

        var remainingUnits = allowance.Remaining.Ticks /
            (double)BusinessMechanicCatalog.AcidProductUnitDuration.Ticks;
        return new AcidBoostState(
            expires - window.RequiredDuration,
            expires,
            Math.Clamp(remainingUnits, 0, BusinessMechanicCatalog.AcidBoostProductUnitAllowance),
            window.State == SharedTimerState.Running &&
            allowance.State is SharedTimerState.Running or SharedTimerState.Paused &&
            remainingUnits > 0);
    }

    public void StartMansionBoost(bool acid)
    {
        Stop(BusinessTimerIds.MansionBunker);
        Stop(BusinessTimerIds.MansionAcid);
        StartWall(acid ? BusinessTimerIds.MansionAcid : BusinessTimerIds.MansionBunker,
            BusinessMechanicCatalog.MansionBoostWindow);
    }

    public void StartNightclub(int targetIncome, bool staffUpgrade) =>
        StartOnline(BusinessTimerIds.Nightclub,
            BusinessMechanicCatalog.NightclubTimeUntilBelowTarget(targetIncome, staffUpgrade));

    public void StartCarWash(int ownedBusinesses) =>
        StartOnline(BusinessTimerIds.CarWash, BusinessMechanicCatalog.CarWashTimeUntilMinimum(ownedBusinesses));

    public void StartCargo(int slot) =>
        StartWall(BusinessTimerIds.Cargo(slot), BusinessMechanicCatalog.WarehouseStaffDispatch);

    public void StartAirFreight() =>
        StartWall(BusinessTimerIds.AirFreight, BusinessMechanicCatalog.AirFreightStaffDispatch);

    public void StartStaff(string timerId) => StartWall(timerId, BusinessMechanicCatalog.StaffDispatch);

    public void StartHeist(BusinessHeistKind kind)
    {
        if (kind is BusinessHeistKind.CayoGroup or BusinessHeistKind.CayoSolo)
        {
            Stop(BusinessTimerIds.Heist(BusinessHeistKind.CayoGroup));
            Stop(BusinessTimerIds.Heist(BusinessHeistKind.CayoSolo));
            Stop(BusinessTimerIds.CayoHardMode);
        }
        else if (kind == BusinessHeistKind.Kortz)
        {
            Stop(BusinessTimerIds.KortzHardMode);
        }

        StartWall(BusinessTimerIds.Heist(kind), BusinessMechanicCatalog.HeistCooldown(kind));
    }

    public bool Stop(string timerId)
    {
        ThrowIfDisposed();
        if (timerId is BusinessTimerIds.AcidBoost or BusinessTimerIds.AcidBoostAllowance)
        {
            var boostStopped = _timers.Stop(BusinessTimerIds.AcidBoost);
            var allowanceStopped = _timers.Stop(BusinessTimerIds.AcidBoostAllowance);
            if (boostStopped || allowanceStopped) Capture();
            return boostStopped || allowanceStopped;
        }

        var stopped = _timers.Stop(timerId);
        if (stopped) Capture();
        return stopped;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _timers.Persist();
        _disposed = true;
        _timers.Completed -= OnCompleted;
    }

    private void StartOnline(string id, TimeSpan duration)
    {
        ThrowIfDisposed();
        _timers.Start(id, TimerClockMode.OnlinePlaytime, duration);
        Capture();
    }

    private void StartWall(string id, TimeSpan duration)
    {
        ThrowIfDisposed();
        _timers.Start(id, TimerClockMode.WallClock, duration);
        Capture();
    }

    private void Capture()
    {
        _last = _timers.Update(_availability, CalculateOnlineProgress)
            .ToDictionary(item => item.TimerId, StringComparer.Ordinal);
    }

    private TimeSpan CalculateOnlineProgress(string timerId, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return TimeSpan.Zero;
        if (timerId == BusinessTimerIds.Acid || timerId == BusinessTimerIds.AcidBoostAllowance)
        {
            if (!_last.TryGetValue(BusinessTimerIds.Acid, out var acid) ||
                acid.State is SharedTimerState.Ready or SharedTimerState.Completed)
                return TimeSpan.Zero;

            var progress = SimulateAcidProduction(from, to, acid.Remaining);
            return timerId == BusinessTimerIds.Acid
                ? progress.ProductionWork
                : progress.BoostedProductionWork;
        }

        if (timerId != BusinessTimerIds.Bunker && timerId != BusinessTimerIds.Acid)
            return to - from;

        var windows = new List<(DateTimeOffset Start, DateTimeOffset End, double Multiplier)>();
        AddWindow(BusinessTimerIds.MansionBunker, BusinessMechanicCatalog.MansionMultiplier);

        var boundaries = new SortedSet<DateTimeOffset> { from, to };
        foreach (var window in windows)
        {
            if (window.Start > from && window.Start < to) boundaries.Add(window.Start);
            if (window.End > from && window.End < to) boundaries.Add(window.End);
        }

        var points = boundaries.ToArray();
        double ticks = 0;
        for (var index = 0; index + 1 < points.Length; index++)
        {
            var segmentStart = points[index];
            var segmentEnd = points[index + 1];
            var midpoint = segmentStart + TimeSpan.FromTicks((segmentEnd - segmentStart).Ticks / 2);
            var rate = windows.Where(window => midpoint >= window.Start && midpoint < window.End)
                .Aggregate(1d, (current, window) => current * window.Multiplier);
            ticks += (segmentEnd - segmentStart).Ticks * rate;
        }

        return TimeSpan.FromTicks((long)Math.Clamp(ticks, 0, TimeSpan.FromDays(365).Ticks));

        void AddWindow(string id, double multiplier)
        {
            if (!_last.TryGetValue(id, out var timer) || timer.ReadyAtUtc is not { } end) return;
            windows.Add((end - timer.RequiredDuration, end, multiplier));
        }
    }

    private IEnumerable<DateTimeOffset> ModifierEndTimes(string timerId, DateTimeOffset now)
    {
        var ids = timerId == BusinessTimerIds.Bunker
            ? new[] { BusinessTimerIds.MansionBunker }
            : new[] { BusinessTimerIds.MansionAcid };
        return ids.Select(id => _last.TryGetValue(id, out var item) ? item.ReadyAtUtc : null)
            .Where(value => value > now)
            .Select(value => value!.Value)
            .Distinct()
            .OrderBy(value => value);
    }

    private double CurrentRate(string timerId, DateTimeOffset when)
    {
        double rate = 1;
        if (timerId == BusinessTimerIds.Bunker && IsActiveAt(BusinessTimerIds.MansionBunker, when))
            rate *= BusinessMechanicCatalog.MansionMultiplier;
        if (timerId == BusinessTimerIds.Acid)
        {
            if (IsActiveAt(BusinessTimerIds.MansionAcid, when))
                rate *= BusinessMechanicCatalog.MansionMultiplier;
        }
        return rate;
    }

    private AcidIntervalProgress SimulateAcidProduction(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan supplyRemaining)
    {
        if (to <= from || supplyRemaining <= TimeSpan.Zero)
            return new AcidIntervalProgress(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
                supplyRemaining <= TimeSpan.Zero);

        var supplyTicks = (double)supplyRemaining.Ticks;
        var allowanceTicks = TryGetAcidBoost(out var boostWindow, out var allowance)
            ? (double)allowance.Remaining.Ticks
            : 0;
        var boostExpires = boostWindow?.ReadyAtUtc;
        var boostStarts = boostExpires - boostWindow?.RequiredDuration;

        var boundaries = new SortedSet<DateTimeOffset> { from, to };
        AddBoundaries(BusinessTimerIds.MansionAcid);
        if (boostStarts is { } boostActivation && boostActivation > from && boostActivation < to)
            boundaries.Add(boostActivation);
        if (boostExpires is { } boostExpiration && boostExpiration > from && boostExpiration < to)
            boundaries.Add(boostExpiration);

        double productionTicks = 0;
        double boostedTicks = 0;
        double elapsedTicks = 0;
        var points = boundaries.ToArray();
        for (var index = 0; index + 1 < points.Length && supplyTicks > 0; index++)
        {
            var segmentStart = points[index];
            var segmentEnd = points[index + 1];
            var midpoint = segmentStart + TimeSpan.FromTicks((segmentEnd - segmentStart).Ticks / 2);
            var mansionRate = IsActiveAt(BusinessTimerIds.MansionAcid, midpoint)
                ? BusinessMechanicCatalog.MansionMultiplier
                : 1d;
            var boostActive = allowanceTicks > 0 &&
                boostStarts is { } activated && boostExpires is { } expires &&
                midpoint >= activated && midpoint < expires;
            var availableRealTicks = (double)(segmentEnd - segmentStart).Ticks;

            if (boostActive)
            {
                var combinedRate = mansionRate * BusinessMechanicCatalog.AcidOwnBoostMultiplier;
                var boostedProduction = Math.Min(
                    availableRealTicks * combinedRate,
                    Math.Min(allowanceTicks, supplyTicks));
                var boostedElapsed = boostedProduction / combinedRate;
                productionTicks += boostedProduction;
                boostedTicks += boostedProduction;
                allowanceTicks -= boostedProduction;
                supplyTicks -= boostedProduction;
                availableRealTicks -= boostedElapsed;
                elapsedTicks += boostedElapsed;

                if (supplyTicks <= 0) break;
            }

            if (availableRealTicks > 0)
            {
                var normalProduction = Math.Min(availableRealTicks * mansionRate, supplyTicks);
                productionTicks += normalProduction;
                supplyTicks -= normalProduction;
                elapsedTicks += normalProduction / mansionRate;
                if (supplyTicks <= 0) break;

                elapsedTicks += availableRealTicks - normalProduction / mansionRate;
            }
        }

        return new AcidIntervalProgress(
            FromTicks(productionTicks),
            FromTicks(boostedTicks),
            FromTicks(elapsedTicks),
            supplyTicks <= 0);

        void AddBoundaries(string id)
        {
            if (!_last.TryGetValue(id, out var timer) || timer.ReadyAtUtc is not { } end) return;
            var start = end - timer.RequiredDuration;
            if (start > from && start < to) boundaries.Add(start);
            if (end > from && end < to) boundaries.Add(end);
        }
    }

    private bool TryGetAcidBoost(
        out SharedTimerSnapshot? window,
        out SharedTimerSnapshot allowance)
    {
        var hasWindow = _last.TryGetValue(BusinessTimerIds.AcidBoost, out var foundWindow);
        var hasAllowance = _last.TryGetValue(BusinessTimerIds.AcidBoostAllowance, out var foundAllowance);
        window = foundWindow;
        allowance = foundAllowance!;
        return hasWindow && hasAllowance &&
            foundWindow!.RequiredDuration == BusinessMechanicCatalog.AcidBoostExpiration &&
            foundAllowance!.RequiredDuration == BusinessMechanicCatalog.AcidBoostAllowanceWork &&
            foundWindow.State is SharedTimerState.Running or SharedTimerState.Paused &&
            foundAllowance.State is SharedTimerState.Running or SharedTimerState.Paused;
    }

    private void ReconcilePersistedAcidBoost()
    {
        var hasWindow = _last.TryGetValue(BusinessTimerIds.AcidBoost, out var window);
        var hasAllowance = _last.TryGetValue(BusinessTimerIds.AcidBoostAllowance, out var allowance);
        var validPair = hasWindow && hasAllowance &&
            window!.RequiredDuration == BusinessMechanicCatalog.AcidBoostExpiration &&
            allowance!.RequiredDuration == BusinessMechanicCatalog.AcidBoostAllowanceWork &&
            window.State is SharedTimerState.Running or SharedTimerState.Paused &&
            allowance.State is SharedTimerState.Running or SharedTimerState.Paused;
        if (validPair || (!hasWindow && !hasAllowance)) return;

        _timers.Stop(BusinessTimerIds.AcidBoost);
        _timers.Stop(BusinessTimerIds.AcidBoostAllowance);
    }

    private static TimeSpan FromTicks(double ticks) => TimeSpan.FromTicks(
        (long)Math.Clamp(Math.Round(ticks), 0, TimeSpan.FromDays(365).Ticks));

    private bool IsActiveAt(string id, DateTimeOffset when) =>
        _last.TryGetValue(id, out var item) && item.ReadyAtUtc is { } end &&
        when >= end - item.RequiredDuration && when < end;

    private void OnCompleted(SharedTimerCompletion completion)
    {
        if (completion.TimerId is BusinessTimerIds.AcidBoost or BusinessTimerIds.AcidBoostAllowance)
        {
            var stoppedWindow = _timers.Stop(BusinessTimerIds.AcidBoost);
            var stoppedAllowance = _timers.Stop(BusinessTimerIds.AcidBoostAllowance);
            if (stoppedWindow || stoppedAllowance)
            {
                Ready?.Invoke(new SharedTimerCompletion(
                    BusinessTimerIds.AcidBoost,
                    TimerClockMode.WallClock));
            }
            return;
        }

        if (completion.TimerId == BusinessTimerIds.Heist(BusinessHeistKind.CayoGroup) ||
            completion.TimerId == BusinessTimerIds.Heist(BusinessHeistKind.CayoSolo))
        {
            StartWall(BusinessTimerIds.CayoHardMode, BusinessMechanicCatalog.CayoHardModeWindow);
        }
        else if (completion.TimerId == BusinessTimerIds.Heist(BusinessHeistKind.Kortz))
        {
            StartWall(BusinessTimerIds.KortzHardMode, BusinessMechanicCatalog.KortzHardModeWindow);
        }
        else if (completion.TimerId is BusinessTimerIds.CayoHardMode or BusinessTimerIds.KortzHardMode)
        {
            _timers.Stop(completion.TimerId);
            return;
        }
        else
        {
            _timers.Persist();
        }

        Ready?.Invoke(completion);
    }

    private void RaiseEarlyAlerts(
        IReadOnlyList<SharedTimerSnapshot> snapshots,
        int earlyAlertMinutes)
    {
        if (earlyAlertMinutes == 0) return;
        var threshold = TimeSpan.FromMinutes(earlyAlertMinutes);
        foreach (var snapshot in snapshots)
        {
            if (!SupportsEarlyAlert(snapshot.TimerId) || snapshot.EarlyAlertRaised ||
                snapshot.State is SharedTimerState.Ready or SharedTimerState.Completed ||
                snapshot.Remaining <= TimeSpan.Zero || snapshot.Remaining > threshold ||
                !_timers.TryMarkEarlyAlertRaised(snapshot.TimerId))
            {
                continue;
            }

            EarlyAlert?.Invoke(snapshot with { EarlyAlertRaised = true });
        }
    }

    private static bool SupportsEarlyAlert(string timerId) =>
        timerId is BusinessTimerIds.Bunker or BusinessTimerIds.Acid or
            BusinessTimerIds.Nightclub or BusinessTimerIds.CarWash or BusinessTimerIds.AirFreight ||
        timerId.StartsWith("business.cargo.", StringComparison.Ordinal) ||
        timerId.StartsWith("business.heist.", StringComparison.Ordinal) &&
        timerId is not BusinessTimerIds.CayoHardMode and not BusinessTimerIds.KortzHardMode;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record AcidIntervalProgress(
        TimeSpan ProductionWork,
        TimeSpan BoostedProductionWork,
        TimeSpan ElapsedUntilSupplyDepleted,
        bool SupplyDepleted);
}

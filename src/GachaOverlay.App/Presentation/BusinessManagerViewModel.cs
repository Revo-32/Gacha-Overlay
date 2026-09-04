using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Business;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Timers;

namespace GachaOverlay.App.Presentation;

internal sealed class BusinessManagerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly BusinessManagerEngine _engine;
    private readonly RemoteOnlinePlaytimeStatusSource _online;
    private readonly DispatcherTimer _timer;
    private readonly GtaoTimerHudViewModel _generalTimers;
    private readonly IRuntimeMetrics? _metrics;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, DateTimeOffset> _attentionUntil = new(StringComparer.Ordinal);
    private bool _notificationPending;
    private DateTimeOffset _generalAttentionUntil;
    private AppSettings _settings;
    private bool _interactive;
    private bool _sectionsDirty = true;
    private bool _disposed;

    public BusinessManagerViewModel(
        BusinessManagerEngine engine,
        RemoteOnlinePlaytimeStatusSource online,
        AppSettings settings,
        Dispatcher dispatcher,
        GtaoTimerHudViewModel generalTimers,
        IRuntimeMetrics? metrics = null)
    {
        _engine = engine;
        _online = online;
        _settings = settings;
        _generalTimers = generalTimers;
        _metrics = metrics;
        _dispatcher = dispatcher;
        _engine.Ready += OnReady;
        _engine.EarlyAlert += OnEarlyAlert;
        _generalTimers.TimerCompleted += OnGeneralTimerCompleted;
        StartGeneral12Command = new RelayCommand(() => StartGeneral(12), () => IsInteractive);
        StartGeneral24Command = new RelayCommand(() => StartGeneral(24), () => IsInteractive);
        StartGeneral48Command = new RelayCommand(() => StartGeneral(48), () => IsInteractive);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => Refresh(), dispatcher);
        _timer.Start();
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<BusinessTimerNotification>? NotificationRequested;
    public ObservableCollection<BusinessSectionViewModel> Sections { get; } = new();
    public ICommand StartGeneral12Command { get; }
    public ICommand StartGeneral24Command { get; }
    public ICommand StartGeneral48Command { get; }
    public string GeneralTimerStatus => _generalTimers.GeneralStatus;
    public bool IsGeneralTimerAttentionActive => DateTimeOffset.UtcNow < _generalAttentionUntil;

    public bool IsInteractive
    {
        get => _interactive;
        private set
        {
            if (_interactive == value) return;
            _interactive = value;
            OnPropertyChanged();
            ((RelayCommand)StartGeneral12Command).RaiseCanExecuteChanged();
            ((RelayCommand)StartGeneral24Command).RaiseCanExecuteChanged();
            ((RelayCommand)StartGeneral48Command).RaiseCanExecuteChanged();
        }
    }

    public void SetUnlocked(bool unlocked)
    {
        IsInteractive = unlocked;
        foreach (var row in Sections.SelectMany(section => section.Rows)) row.SetInteractive(unlocked);
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _online.ApplySettings(settings);
        _sectionsDirty = true;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        var snapshots = _engine.Update(_online.Current, _settings.BusinessTimerEarlyAlertMinutes)
            .ToDictionary(item => item.TimerId, StringComparer.Ordinal);
        _metrics?.SetGauge(RuntimeMetricNames.BusinessActiveTimers,
            snapshots.Values.Count(item => item.State == SharedTimerState.Running));
        _metrics?.SetGauge(RuntimeMetricNames.BusinessWallClockTimers,
            snapshots.Values.Count(item => item.ClockMode == TimerClockMode.WallClock));
        _metrics?.SetGauge(RuntimeMetricNames.BusinessOnlineTimers,
            snapshots.Values.Count(item => item.ClockMode == TimerClockMode.OnlinePlaytime));
        _metrics?.SetGauge(RuntimeMetricNames.BusinessPausedTimers,
            snapshots.Values.Count(item => item.State == SharedTimerState.Paused));
        _metrics?.SetGauge(RuntimeMetricNames.BusinessPausedUnknownPresence,
            _online.Current == OnlinePlaytimeAvailability.Unknown
                ? snapshots.Values.Count(item => item.State == SharedTimerState.Paused)
                : 0);
        _metrics?.SetGauge(RuntimeMetricNames.BusinessReadyTimers,
            snapshots.Values.Count(item => item.State is SharedTimerState.Ready or SharedTimerState.Completed));
        _metrics?.SetGauge(RuntimeMetricNames.BusinessMansionBoostActive,
            IsRunning(BusinessTimerIds.MansionBunker) || IsRunning(BusinessTimerIds.MansionAcid) ? 1 : 0);
        _metrics?.SetState(RuntimeMetricNames.BusinessOnlineState, _online.Current.ToString());
        SynchronizeSections(BuildSections(snapshots, now));
        RefreshAttention(now);
        OnPropertyChanged(nameof(GeneralTimerStatus));
        OnPropertyChanged(nameof(IsGeneralTimerAttentionActive));

        bool IsRunning(string id) => snapshots.TryGetValue(id, out var timer) &&
            timer.State == SharedTimerState.Running;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _engine.Ready -= OnReady;
        _engine.EarlyAlert -= OnEarlyAlert;
        _generalTimers.TimerCompleted -= OnGeneralTimerCompleted;
        _engine.Dispose();
    }

    private IReadOnlyList<BusinessSectionViewModel> BuildSections(
        IReadOnlyDictionary<string, SharedTimerSnapshot> snapshots,
        DateTimeOffset now)
    {
        var result = new List<BusinessSectionViewModel>();
        if (_settings.BusinessBunkerEnabled)
        {
            var bunker = new List<BusinessTimerRowViewModel>
            {
                _settings.BusinessBunkerUpgraded
                    ? Row("보급품 소모", BusinessTimerIds.Bunker, _engine.StartBunker, "보급품 채움")
                    : Unsupported("비업그레이드 생산 시간 미검증"),
            };
            if (_settings.BusinessMansionBoostEnabled && _settings.BusinessBunkerUpgraded)
                bunker.Add(Row("맨션 생산 부스트", BusinessTimerIds.MansionBunker,
                    () => _engine.StartMansionBoost(false), "생산 부스트"));
            Add("벙커", bunker.ToArray());
        }
        if (_settings.BusinessNightclubEnabled)
            Add("나이트클럽", Row($"금고수입 ${_settings.BusinessNightclubMinimumIncome:N0}",
                BusinessTimerIds.Nightclub,
                () => _engine.StartNightclub(_settings.BusinessNightclubMinimumIncome,
                    _settings.BusinessNightclubStaffUpgrade), "인기도 최대"));
        if (_settings.BusinessAcidEnabled)
        {
            var acid = _settings.BusinessAcidUpgraded
                ? new List<BusinessTimerRowViewModel>
                {
                    Row("보급품 소모", BusinessTimerIds.Acid, _engine.StartAcid, "보급품 채움"),
                    Row("자체 생산 부스트", BusinessTimerIds.AcidBoost, _engine.StartAcidBoost, "자체 부스트"),
                }
                : [Unsupported("장비 미업그레이드 생산 시간 미검증")];
            if (_settings.BusinessMansionBoostEnabled && _settings.BusinessAcidUpgraded)
                acid.Add(Row("맨션 생산 부스트", BusinessTimerIds.MansionAcid,
                    () => _engine.StartMansionBoost(true), "생산 부스트"));
            Add("LSD", acid.ToArray());
        }
        if (_settings.BusinessCarWashEnabled)
            Add("세차장", Row("용의도 Max",
                BusinessTimerIds.CarWash, () => _engine.StartCarWash(_settings.BusinessMoneyFrontCount), "용의도 0%"));
        if (_settings.BusinessSpecialCargoEnabled)
        {
            var names = new[]
            {
                _settings.BusinessSpecialCargoWarehouse1Name,
                _settings.BusinessSpecialCargoWarehouse2Name,
                _settings.BusinessSpecialCargoWarehouse3Name,
                _settings.BusinessSpecialCargoWarehouse4Name,
                _settings.BusinessSpecialCargoWarehouse5Name,
            };
            Add("스페셜 패키지", Enumerable.Range(1, _settings.BusinessSpecialCargoWarehouseCount)
                .Select(slot => Row(names[slot - 1], BusinessTimerIds.Cargo(slot), () => _engine.StartCargo(slot), "직원 파견"))
                .ToArray());
        }
        if (_settings.BusinessAirFreightEnabled)
            Add("항공 화물", Row("루스터 조달", BusinessTimerIds.AirFreight, _engine.StartAirFreight, "직원 파견"));

        var heists = new List<BusinessTimerRowViewModel>();
        if (_settings.BusinessOriginalHeistEnabled) heists.Add(Heist("구습", BusinessHeistKind.Original));
        if (_settings.BusinessDoomsdayHeistEnabled) heists.Add(Heist("신습", BusinessHeistKind.Doomsday));
        if (_settings.BusinessCasinoHeistEnabled) heists.Add(Heist("카습", BusinessHeistKind.Casino));
        if (_settings.BusinessCayoHeistEnabled)
        {
            var cooldownId = Preferred(
                    BusinessTimerIds.Heist(BusinessHeistKind.CayoGroup),
                    BusinessTimerIds.Heist(BusinessHeistKind.CayoSolo));
            heists.Add(Row("페습", cooldownId,
                () => _engine.StartHeist(BusinessHeistKind.CayoGroup), "그룹 완료",
                () => _engine.StartHeist(BusinessHeistKind.CayoSolo), "솔로 완료"));
            heists.Add(HardRow(cooldownId, BusinessTimerIds.CayoHardMode));
        }
        if (_settings.BusinessKortzHeistEnabled)
        {
            var cooldownId = BusinessTimerIds.Heist(BusinessHeistKind.Kortz);
            heists.Add(Row("코습", cooldownId,
                () => _engine.StartHeist(BusinessHeistKind.Kortz), "습격 완료"));
            heists.Add(HardRow(cooldownId, BusinessTimerIds.KortzHardMode));
        }
        if (heists.Count > 0) Add("습격 쿨다운", heists.ToArray());
        return result;

        BusinessTimerRowViewModel Heist(string label, BusinessHeistKind kind) =>
            Row(label, BusinessTimerIds.Heist(kind), () => _engine.StartHeist(kind), "습격 완료");

        BusinessTimerRowViewModel HardRow(string cooldownId, string hardModeId)
        {
            var status = "대기";
            var accent = string.Empty;
            if (snapshots.TryGetValue(hardModeId, out var hardMode))
            {
                accent = "가능 ·";
                status = FormatTime(_engine.EstimateRemaining(hardMode, now));
            }
            else if (snapshots.TryGetValue(cooldownId, out var cooldown))
            {
                status = cooldown.State is SharedTimerState.Ready or SharedTimerState.Completed
                    ? "종료"
                    : $"{FormatTime(_engine.EstimateRemaining(cooldown, now))} 후 가능";
            }

            return new BusinessTimerRowViewModel(
                "하드", hardModeId, status, () => { }, string.Empty, null, string.Empty,
                () => { }, IsInteractive, available: false, refresh: null,
                availabilityAccentText: accent);
        }

        string Preferred(params string[] ids) => ids.FirstOrDefault(snapshots.ContainsKey) ?? ids[0];

        BusinessTimerRowViewModel Row(
            string label,
            string id,
            Action start,
            string primaryLabel = "시작 / 재시작",
            Action? secondary = null,
            string secondaryLabel = "")
        {
            snapshots.TryGetValue(id, out var snapshot);
            var status = snapshot is null
                ? "대기"
                : FormatStatus(snapshot, _engine.EstimateRemaining(snapshot, now), now);
            return new BusinessTimerRowViewModel(label, id, status, start, primaryLabel, secondary,
                secondaryLabel, () => _engine.Stop(id), IsInteractive, available: true,
                refresh: Refresh);
        }

        BusinessTimerRowViewModel Unsupported(string label) =>
            new(label, string.Empty, "설정 조합 검증 필요", () => { }, "지원 안 함", null,
                string.Empty, () => { }, IsInteractive, available: false, refresh: null);

        void Add(string title, params BusinessTimerRowViewModel[] rows) =>
            result.Add(new BusinessSectionViewModel(title, rows));
    }

    private void SynchronizeSections(IReadOnlyList<BusinessSectionViewModel> next)
    {
        if (_sectionsDirty || !HasSameStructure(next))
        {
            Sections.Clear();
            foreach (var section in next) Sections.Add(section);
            _sectionsDirty = false;
            OnPropertyChanged(nameof(Sections));
            return;
        }

        for (var sectionIndex = 0; sectionIndex < Sections.Count; sectionIndex++)
        for (var rowIndex = 0; rowIndex < Sections[sectionIndex].Rows.Count; rowIndex++)
            Sections[sectionIndex].Rows[rowIndex].UpdatePresentation(next[sectionIndex].Rows[rowIndex]);
    }

    private bool HasSameStructure(IReadOnlyList<BusinessSectionViewModel> next)
    {
        if (Sections.Count != next.Count) return false;
        for (var sectionIndex = 0; sectionIndex < Sections.Count; sectionIndex++)
        {
            var current = Sections[sectionIndex];
            var candidate = next[sectionIndex];
            if (!string.Equals(current.Title, candidate.Title, StringComparison.Ordinal) ||
                current.Rows.Count != candidate.Rows.Count)
                return false;
            for (var rowIndex = 0; rowIndex < current.Rows.Count; rowIndex++)
            {
                if (!current.Rows[rowIndex].HasSameStructure(candidate.Rows[rowIndex])) return false;
            }
        }

        return true;
    }

    private static string FormatStatus(SharedTimerSnapshot snapshot, TimeSpan remaining, DateTimeOffset now)
    {
        if (snapshot.State is SharedTimerState.Ready or SharedTimerState.Completed)
        {
            if (snapshot.TimerId is BusinessTimerIds.Bunker or BusinessTimerIds.Acid) return "보급 필요";
            if (snapshot.TimerId == BusinessTimerIds.AirFreight ||
                snapshot.TimerId.StartsWith("business.cargo.", StringComparison.Ordinal)) return "파견 가능";
            if (snapshot.TimerId.StartsWith("business.heist.", StringComparison.Ordinal)) return "준비";
            return "준비 완료";
        }
        var time = FormatTime(remaining);
        if (snapshot.State == SharedTimerState.Paused) return $"일시 정지 · {time}";
        if (snapshot.TimerId is BusinessTimerIds.CayoHardMode or BusinessTimerIds.KortzHardMode)
            return $"가능 · {time}";
        return snapshot.ClockMode == TimerClockMode.WallClock
            ? $"{time} · {(now + remaining).ToLocalTime():HH:mm} 예정"
            : time;
    }

    private static string FormatTime(TimeSpan remaining) => remaining.TotalHours >= 1
        ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
        : $"{remaining.Minutes:00}:{remaining.Seconds:00}";

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnReady(SharedTimerCompletion completion)
    {
        TriggerAttention(ReadyAttentionKey(completion.TimerId));
        QueueNotification(new BusinessTimerNotification(
            completion.TimerId,
            BusinessTimerNotificationKind.Ready));
    }

    private void OnEarlyAlert(SharedTimerSnapshot snapshot)
    {
        TriggerAttention(snapshot.TimerId);
        QueueNotification(new BusinessTimerNotification(
            snapshot.TimerId,
            BusinessTimerNotificationKind.EarlyAlert));
    }

    private void OnGeneralTimerCompleted(GtaoTimerSlot slot)
    {
        if (slot != GtaoTimerSlot.General) return;
        _generalAttentionUntil = DateTimeOffset.UtcNow + GtaoTimerEngine.ExpiryEmphasisDuration;
        OnPropertyChanged(nameof(IsGeneralTimerAttentionActive));
    }

    private void StartGeneral(int minutes)
    {
        _generalTimers.StartGeneral(minutes);
        OnPropertyChanged(nameof(GeneralTimerStatus));
    }

    private void TriggerAttention(string timerId)
    {
        _attentionUntil[timerId] = DateTimeOffset.UtcNow + GtaoTimerEngine.ExpiryEmphasisDuration;
        RefreshAttention(DateTimeOffset.UtcNow);
    }

    private void RefreshAttention(DateTimeOffset now)
    {
        foreach (var expired in _attentionUntil.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            _attentionUntil.Remove(expired);
        foreach (var row in Sections.SelectMany(section => section.Rows))
            row.SetAttention(_attentionUntil.TryGetValue(row.TimerId, out var until) && until > now);
    }

    private void QueueNotification(BusinessTimerNotification notification)
    {
        if (_notificationPending) return;
        _notificationPending = true;
        _dispatcher.BeginInvoke(() =>
        {
            _notificationPending = false;
            NotificationRequested?.Invoke(notification);
        }, DispatcherPriority.Background);
    }

    private static string ReadyAttentionKey(string timerId)
    {
        if (timerId == BusinessTimerIds.Heist(BusinessHeistKind.CayoGroup) ||
            timerId == BusinessTimerIds.Heist(BusinessHeistKind.CayoSolo))
            return BusinessTimerIds.CayoHardMode;
        if (timerId == BusinessTimerIds.Heist(BusinessHeistKind.Kortz))
            return BusinessTimerIds.KortzHardMode;
        return timerId;
    }
}

internal enum BusinessTimerNotificationKind
{
    EarlyAlert,
    Ready,
}

internal sealed record BusinessTimerNotification(
    string TimerId,
    BusinessTimerNotificationKind Kind);

internal sealed record BusinessSectionViewModel(
    string Title,
    IReadOnlyList<BusinessTimerRowViewModel> Rows);

internal sealed class BusinessTimerRowViewModel : INotifyPropertyChanged
{
    private bool _interactive;
    private readonly bool _available;
    private readonly Action? _refresh;
    private string _status;
    private string _availabilityAccentText;
    private bool _attentionActive;
    public BusinessTimerRowViewModel(string label, string timerId, string status, Action start,
        string primaryLabel, Action? secondary, string secondaryLabel, Action stop, bool interactive,
        bool available, Action? refresh = null, string availabilityAccentText = "")
    {
        Label = label; TimerId = timerId; _status = status; PrimaryLabel = primaryLabel;
        SecondaryLabel = secondaryLabel; _interactive = interactive; _available = available; _refresh = refresh;
        _availabilityAccentText = availabilityAccentText;
        HasSecondary = secondary is not null;
        PrimaryCommand = new RelayCommand(() => Execute(start), () => IsInteractive);
        SecondaryCommand = new RelayCommand(() => Execute(secondary ?? (() => { })), () => CanUseSecondary);
        StopCommand = new RelayCommand(() => Execute(stop), () => CanStop);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Label { get; }
    public string TimerId { get; }
    public string Status => _status;
    public string PrimaryLabel { get; }
    public string SecondaryLabel { get; }
    public bool HasSecondary { get; }
    public ICommand PrimaryCommand { get; }
    public ICommand SecondaryCommand { get; }
    public ICommand StopCommand { get; }
    public bool IsInteractive => _interactive && _available;
    public bool CanUseSecondary => IsInteractive && HasSecondary;
    public bool CanStop => IsInteractive && Status != "대기";
    public bool IsAttentionActive => _attentionActive;
    public string AvailabilityAccentText => _availabilityAccentText;
    public bool HasAvailabilityAccent => !string.IsNullOrEmpty(_availabilityAccentText);
    public bool IsHardTrackingRow => TimerId is BusinessTimerIds.CayoHardMode or BusinessTimerIds.KortzHardMode;

    public void SetInteractive(bool value)
    {
        if (_interactive == value) return;
        _interactive = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInteractive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUseSecondary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStop)));
        ((RelayCommand)PrimaryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)SecondaryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
    }

    public void UpdatePresentation(BusinessTimerRowViewModel other)
    {
        if (!string.Equals(_status, other.Status, StringComparison.Ordinal))
        {
            _status = other.Status;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStop)));
            ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
        }
        if (!string.Equals(_availabilityAccentText, other.AvailabilityAccentText, StringComparison.Ordinal))
        {
            _availabilityAccentText = other.AvailabilityAccentText;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailabilityAccentText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAvailabilityAccent)));
        }
    }

    public void SetAttention(bool value)
    {
        if (_attentionActive == value) return;
        _attentionActive = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAttentionActive)));
    }

    public bool HasSameStructure(BusinessTimerRowViewModel other) =>
        string.Equals(Label, other.Label, StringComparison.Ordinal) &&
        string.Equals(TimerId, other.TimerId, StringComparison.Ordinal) &&
        string.Equals(PrimaryLabel, other.PrimaryLabel, StringComparison.Ordinal) &&
        string.Equals(SecondaryLabel, other.SecondaryLabel, StringComparison.Ordinal) &&
        HasSecondary == other.HasSecondary && _available == other._available;

    private void Execute(Action action)
    {
        action();
        _refresh?.Invoke();
    }
}

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
    private readonly Action<int> _startGeneral;
    private readonly IRuntimeMetrics? _metrics;
    private readonly Dispatcher _dispatcher;
    private bool _readyNotificationPending;
    private AppSettings _settings;
    private bool _interactive;
    private bool _disposed;

    public BusinessManagerViewModel(
        BusinessManagerEngine engine,
        RemoteOnlinePlaytimeStatusSource online,
        AppSettings settings,
        Dispatcher dispatcher,
        Action<int> startGeneral,
        IRuntimeMetrics? metrics = null)
    {
        _engine = engine;
        _online = online;
        _settings = settings;
        _startGeneral = startGeneral;
        _metrics = metrics;
        _dispatcher = dispatcher;
        _engine.Ready += OnReady;
        StartGeneral12Command = new RelayCommand(() => _startGeneral(12), () => IsInteractive);
        StartGeneral24Command = new RelayCommand(() => _startGeneral(24), () => IsInteractive);
        StartGeneral48Command = new RelayCommand(() => _startGeneral(48), () => IsInteractive);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => Refresh(), dispatcher);
        _timer.Start();
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<SharedTimerCompletion>? Ready;
    public ObservableCollection<BusinessSectionViewModel> Sections { get; } = new();
    public ICommand StartGeneral12Command { get; }
    public ICommand StartGeneral24Command { get; }
    public ICommand StartGeneral48Command { get; }

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
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        var snapshots = _engine.Update(_online.Current).ToDictionary(item => item.TimerId, StringComparer.Ordinal);
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
        var sections = BuildSections(snapshots, now);
        Sections.Clear();
        foreach (var section in sections) Sections.Add(section);
        OnPropertyChanged(nameof(Sections));

        bool IsRunning(string id) => snapshots.TryGetValue(id, out var timer) &&
            timer.State == SharedTimerState.Running;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _engine.Ready -= OnReady;
        _engine.Dispose();
    }

    private IReadOnlyList<BusinessSectionViewModel> BuildSections(
        IReadOnlyDictionary<string, SharedTimerSnapshot> snapshots,
        DateTimeOffset now)
    {
        var result = new List<BusinessSectionViewModel>();
        if (_settings.BusinessBunkerEnabled)
            Add("벙커", _settings.BusinessBunkerUpgraded
                ? Row("공급품 100% → 0%", BusinessTimerIds.Bunker, _engine.StartBunker, "보급 채움")
                : Unsupported("비업그레이드 생산 시간 미검증"));
        if (_settings.BusinessNightclubEnabled)
            Add("나이트클럽", Row($"안전 수입 ${_settings.BusinessNightclubMinimumIncome:N0}",
                BusinessTimerIds.Nightclub,
                () => _engine.StartNightclub(_settings.BusinessNightclubMinimumIncome,
                    _settings.BusinessNightclubStaffUpgrade), "인기도 최대"));
        if (_settings.BusinessAcidEnabled)
            Add("산성 연구소", _settings.BusinessAcidUpgraded
                ? new[]
                {
                    Row("공급품 100% → 0%", BusinessTimerIds.Acid, _engine.StartAcid, "보급 채움"),
                    Row("생산 속도 보정", BusinessTimerIds.AcidBoost, _engine.StartAcidBoost, "생산 부스트"),
                }
                : new[] { Unsupported("장비 미업그레이드 생산 시간 미검증") });
        if (_settings.BusinessCarWashEnabled)
            Add("세차장 · 위장 사업장", Row("용의도 0% → 100%",
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
        if (_settings.BusinessOriginalHeistEnabled) heists.Add(Heist("오리지널 습격", BusinessHeistKind.Original));
        if (_settings.BusinessDoomsdayHeistEnabled) heists.Add(Heist("심판의 날 습격", BusinessHeistKind.Doomsday));
        if (_settings.BusinessCasinoHeistEnabled) heists.Add(Heist("다이아몬드 카지노 습격", BusinessHeistKind.Casino));
        if (_settings.BusinessCayoHeistEnabled)
            heists.Add(snapshots.ContainsKey(BusinessTimerIds.CayoHardMode)
                ? Row("카요 페리코", BusinessTimerIds.CayoHardMode,
                    () => _engine.Stop(BusinessTimerIds.CayoHardMode), "준비 시작")
                : Row("카요 페리코", Preferred(
                        BusinessTimerIds.Heist(BusinessHeistKind.CayoGroup),
                        BusinessTimerIds.Heist(BusinessHeistKind.CayoSolo)),
                    () => _engine.StartHeist(BusinessHeistKind.CayoGroup), "그룹 완료",
                    () => _engine.StartHeist(BusinessHeistKind.CayoSolo), "솔로 완료"));
        if (_settings.BusinessKortzHeistEnabled)
            heists.Add(snapshots.ContainsKey(BusinessTimerIds.KortzHardMode)
                ? Row("코르츠", BusinessTimerIds.KortzHardMode,
                    () => _engine.Stop(BusinessTimerIds.KortzHardMode), "준비 시작")
                : Row("코르츠", BusinessTimerIds.Heist(BusinessHeistKind.Kortz),
                    () => _engine.StartHeist(BusinessHeistKind.Kortz), "습격 완료"));
        if (heists.Count > 0) Add("습격 쿨다운", heists.ToArray());

        if (_settings.BusinessMansionBoostEnabled)
            Add("맨션 부스트", Row("24시간 x3", Preferred(
                    BusinessTimerIds.MansionBunker, BusinessTimerIds.MansionAcid),
                () => _engine.StartMansionBoost(false), "벙커",
                () => _engine.StartMansionBoost(true), "산성 연구소"));
        return result;

        BusinessTimerRowViewModel Heist(string label, BusinessHeistKind kind) =>
            Row(label, BusinessTimerIds.Heist(kind), () => _engine.StartHeist(kind), "습격 완료");

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
                secondaryLabel, () => _engine.Stop(id), IsInteractive, available: true);
        }

        BusinessTimerRowViewModel Unsupported(string label) =>
            new(label, string.Empty, "설정 조합 검증 필요", () => { }, "지원 안 함", null,
                string.Empty, () => { }, IsInteractive, available: false);

        void Add(string title, params BusinessTimerRowViewModel[] rows) =>
            result.Add(new BusinessSectionViewModel(title, rows));
    }

    private static string FormatStatus(SharedTimerSnapshot snapshot, TimeSpan remaining, DateTimeOffset now)
    {
        if (snapshot.State is SharedTimerState.Ready or SharedTimerState.Completed)
        {
            if (snapshot.TimerId is BusinessTimerIds.Bunker or BusinessTimerIds.Acid) return "보급 필요";
            if (snapshot.TimerId == BusinessTimerIds.AirFreight ||
                snapshot.TimerId.StartsWith("business.cargo.", StringComparison.Ordinal)) return "파견 가능";
            return "준비 완료";
        }
        var time = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
        if (snapshot.State == SharedTimerState.Paused) return $"일시 정지 · {time}";
        if (snapshot.TimerId is BusinessTimerIds.CayoHardMode or BusinessTimerIds.KortzHardMode)
            return $"하드 모드 가능 · {time}";
        return snapshot.ClockMode == TimerClockMode.WallClock
            ? $"{time} · {(now + remaining).ToLocalTime():HH:mm} 예정"
            : time;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnReady(SharedTimerCompletion completion)
    {
        if (_readyNotificationPending) return;
        _readyNotificationPending = true;
        _dispatcher.BeginInvoke(() =>
        {
            _readyNotificationPending = false;
            Ready?.Invoke(completion);
        }, DispatcherPriority.Background);
    }
}

internal sealed record BusinessSectionViewModel(
    string Title,
    IReadOnlyList<BusinessTimerRowViewModel> Rows);

internal sealed class BusinessTimerRowViewModel : INotifyPropertyChanged
{
    private bool _interactive;
    private readonly bool _available;
    public BusinessTimerRowViewModel(string label, string timerId, string status, Action start,
        string primaryLabel, Action? secondary, string secondaryLabel, Action stop, bool interactive,
        bool available)
    {
        Label = label; TimerId = timerId; Status = status; PrimaryLabel = primaryLabel;
        SecondaryLabel = secondaryLabel; _interactive = interactive; _available = available;
        PrimaryCommand = new RelayCommand(start, () => IsInteractive);
        SecondaryCommand = new RelayCommand(secondary ?? (() => { }), () => IsInteractive && HasSecondary);
        StopCommand = new RelayCommand(stop, () => IsInteractive && Status != "대기");
        HasSecondary = secondary is not null;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Label { get; }
    public string TimerId { get; }
    public string Status { get; }
    public string PrimaryLabel { get; }
    public string SecondaryLabel { get; }
    public bool HasSecondary { get; }
    public ICommand PrimaryCommand { get; }
    public ICommand SecondaryCommand { get; }
    public ICommand StopCommand { get; }
    public bool IsInteractive => _interactive && _available;
    public void SetInteractive(bool value)
    {
        _interactive = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInteractive)));
        ((RelayCommand)PrimaryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)SecondaryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
    }
}

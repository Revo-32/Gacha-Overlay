using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using GachaOverlay.Core.Gta;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;
using LSOverlay.Protocol;

namespace GachaOverlay.App.Presentation;

internal sealed class GtaCompanionViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly GtaCompanionStateManager _state;
    private readonly KstResetSchedule _schedule;
    private readonly DispatcherTimer _timer;
    private GtaCompanionSnapshot? _snapshot;
    private AppSettings _settings;
    private bool _isUnlocked;
    private string _dailyResetText = string.Empty;
    private string _weeklyResetText = string.Empty;

    public GtaCompanionViewModel(
        GtaCompanionStateManager state,
        ILocalizationService localization,
        AppSettings settings,
        Dispatcher dispatcher,
        KstResetSchedule? schedule = null)
    {
        _state = state;
        Localization = localization;
        _settings = settings;
        _schedule = schedule ?? new KstResetSchedule();
        DailySlots = new ObservableCollection<GtaDailySlotViewModel>(
            Enumerable.Range(1, 3).Select(slot => new GtaDailySlotViewModel(
                slot,
                _state,
                RefreshFromLocalState)));
        ToggleWeeklyCompletionCommand = new RelayCommand(
            () => { if (IsInteractive) _state.ToggleWeeklyCompletion(DateTimeOffset.UtcNow); },
            () => IsInteractive && HasWeeklyChallenge);
        _state.Changed += OnLocalStateChanged;
        _timer = new DispatcherTimer(
            TimeSpan.FromMinutes(1),
            DispatcherPriority.Background,
            (_, _) => RefreshTime(),
            dispatcher);
        _timer.Start();
        RefreshFromLocalState();
        RefreshTime();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ILocalizationService Localization { get; }

    public ObservableCollection<GtaDailySlotViewModel> DailySlots { get; }

    public ICommand ToggleWeeklyCompletionCommand { get; }

    public bool ShowDaily => _settings.GtaCompanionDailyEnabled;

    public bool ShowWeekly => _settings.GtaCompanionWeeklyEnabled;

    public bool ShowWeeklyEvents => _settings.GtaCompanionWeeklyEventsEnabled;

    public bool IsChallengeOnly => !ShowWeeklyEvents;

    public bool IsInteractive => _isUnlocked && _settings.GtaCompanionEnabled;

    public string DailyResetText
    {
        get => _dailyResetText;
        private set => Set(ref _dailyResetText, value);
    }

    public string WeeklyResetText
    {
        get => _weeklyResetText;
        private set => Set(ref _weeklyResetText, value);
    }

    public bool HasWeeklyChallenge => _snapshot?.CurrentWeek?.WeeklyChallenge is not null;

    public string WeeklyChallengeText =>
        _snapshot?.CurrentWeek?.WeeklyChallenge?.DisplayTextKo ?? "현재 주간 도전 정보를 준비 중입니다.";

    public string WeeklyRewardText =>
        _snapshot?.CurrentWeek?.WeeklyChallenge?.RewardTextKo ?? string.Empty;

    public bool WeeklyCompleted => _state.Current.WeeklyCompleted;

    public IReadOnlyList<string> WeeklyEventItems => BuildWeeklyEventItems(_snapshot?.CurrentWeek);

    public bool HasCampaign => _snapshot?.Campaign is not null;

    public string CampaignTitle => _snapshot?.Campaign?.TitleKo ?? string.Empty;

    public IReadOnlyList<string> CampaignItems => BuildCampaignItems(_snapshot?.Campaign);

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        RefreshAll();
    }

    public void SetUnlocked(bool unlocked)
    {
        if (_isUnlocked == unlocked) return;
        _isUnlocked = unlocked;
        RefreshAll();
    }

    public void ApplySnapshot(GtaCompanionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _state.ObserveWeeklyChallenge(
            snapshot.CurrentWeek?.WeeklyChallenge?.ChallengeKey,
            DateTimeOffset.UtcNow);
        RefreshAll();
    }

    public void Dispose()
    {
        _timer.Stop();
        _state.Changed -= OnLocalStateChanged;
    }

    private void OnLocalStateChanged(GtaCompanionLocalState state) => RefreshFromLocalState();

    private void RefreshFromLocalState()
    {
        var current = _state.Current;
        foreach (var slot in DailySlots)
        {
            slot.Refresh(current.DailySlots.First(item => item.Slot == slot.Slot));
        }
        OnPropertyChanged(nameof(WeeklyCompleted));
        (ToggleWeeklyCompletionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RefreshTime()
    {
        var now = DateTimeOffset.UtcNow;
        _state.ApplyTime(now);
        DailyResetText = $"일일 초기화까지 {KstResetSchedule.FormatCountdown(_schedule.GetNextDailyReset(now), now)}";
        WeeklyResetText = $"주간 초기화까지 {KstResetSchedule.FormatCountdown(_schedule.GetNextWeeklyReset(now), now)}";
    }

    private void RefreshAll()
    {
        foreach (var name in new[]
        {
            nameof(ShowDaily), nameof(ShowWeekly), nameof(ShowWeeklyEvents),
            nameof(IsChallengeOnly), nameof(IsInteractive),
            nameof(HasWeeklyChallenge), nameof(WeeklyChallengeText), nameof(WeeklyRewardText),
            nameof(WeeklyCompleted), nameof(WeeklyEventItems), nameof(HasCampaign),
            nameof(CampaignTitle), nameof(CampaignItems),
        }) OnPropertyChanged(name);
        foreach (var slot in DailySlots) slot.SetInteractive(IsInteractive);
        (ToggleWeeklyCompletionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static IReadOnlyList<string> BuildWeeklyEventItems(GtaCompanionWeek? week)
    {
        if (week is null) return Array.Empty<string>();
        return week.Bonuses.Concat(week.Discounts).Concat(week.FreeItems).Concat(week.OtherEvents)
            .Select(item => item.DisplayTextKo)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(48)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildCampaignItems(GtaCompanionCampaign? campaign)
    {
        if (campaign is null) return Array.Empty<string>();
        return campaign.GoalsKo.Concat(campaign.RewardsKo)
            .Concat(campaign.UpcomingWeeks.Select(week => week.DisplayTextKo))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(24)
            .ToArray();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed record GtaDailyChallengeOption(string? ChallengeId, string DisplayText)
{
    public static IReadOnlyList<GtaDailyChallengeOption> All { get; } =
        new[] { new GtaDailyChallengeOption(null, "선택 안 함") }
            .Concat(GtaDailyChallengeCatalog.SearchableEntries
            .Select(item => new GtaDailyChallengeOption(item.ChallengeId, item.KoreanDisplayName))
            .Append(new GtaDailyChallengeOption(GtaDailyChallengeCatalog.CustomChallengeId, "직접 입력")))
            .ToArray();
}

internal sealed class GtaDailySlotViewModel : INotifyPropertyChanged
{
    private readonly GtaCompanionStateManager _state;
    private readonly Action _refresh;
    private bool _refreshing;
    private bool _interactive;
    private string? _selectedChallengeId;
    private string? _customText;
    private bool _completed;

    public GtaDailySlotViewModel(int slot, GtaCompanionStateManager state, Action refresh)
    {
        Slot = slot;
        _state = state;
        _refresh = refresh;
        ToggleCompletionCommand = new RelayCommand(
            () => { if (IsInteractive) _state.ToggleDailyCompletion(Slot, DateTimeOffset.UtcNow); },
            () => IsInteractive && HasSelection);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public int Slot { get; }
    public string Label => $"도전 {Slot}";
    public IReadOnlyList<GtaDailyChallengeOption> Options => GtaDailyChallengeOption.All;
    public ICommand ToggleCompletionCommand { get; }
    public bool IsInteractive => _interactive;
    public bool IsCustom => SelectedChallengeId == GtaDailyChallengeCatalog.CustomChallengeId;
    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectedChallengeId) &&
        (!IsCustom || !string.IsNullOrWhiteSpace(CustomText));

    public string? SelectedChallengeId
    {
        get => _selectedChallengeId;
        set
        {
            if (_selectedChallengeId == value) return;
            _selectedChallengeId = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(IsCustom)); OnPropertyChanged(nameof(HasSelection));
            if (!_refreshing && !_state.SelectDaily(Slot, value, CustomText, DateTimeOffset.UtcNow)) _refresh();
            RaiseCommand();
        }
    }

    public string? CustomText
    {
        get => _customText;
        set
        {
            if (_customText == value) return;
            _customText = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(HasSelection));
            if (!_refreshing && IsCustom && !_state.SelectDaily(Slot, SelectedChallengeId, value, DateTimeOffset.UtcNow)) _refresh();
            RaiseCommand();
        }
    }

    public bool Completed
    {
        get => _completed;
        set { if (_completed != value && !_refreshing) _state.ToggleDailyCompletion(Slot, DateTimeOffset.UtcNow); }
    }

    public void Refresh(GtaDailySlotState state)
    {
        _refreshing = true;
        _selectedChallengeId = state.ChallengeId;
        _customText = state.CustomText;
        _completed = state.Completed;
        _refreshing = false;
        OnPropertyChanged(nameof(SelectedChallengeId)); OnPropertyChanged(nameof(CustomText));
        OnPropertyChanged(nameof(Completed)); OnPropertyChanged(nameof(IsCustom)); OnPropertyChanged(nameof(HasSelection));
        RaiseCommand();
    }

    public void SetInteractive(bool value)
    {
        if (_interactive == value) return;
        _interactive = value;
        OnPropertyChanged(nameof(IsInteractive));
        RaiseCommand();
    }

    private void RaiseCommand() => (ToggleCompletionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

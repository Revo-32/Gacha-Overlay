using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Timers;

namespace GachaOverlay.App.Presentation;

internal sealed class GtaoTimerHudViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly GtaoTimerEngine _engine = new();
    private readonly HashSet<GtaoTimerSlot> _completedSlots = new();
    private readonly DispatcherTimer _refreshTimer;
    private AppSettings _settings;
    private bool _isVisible;

    public GtaoTimerHudViewModel(
        ILocalizationService localization,
        AppSettings settings,
        Dispatcher dispatcher)
    {
        _localization = localization;
        _settings = settings;
        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => Refresh(),
            dispatcher);
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? CompletionSoundRequested;

    public ObservableCollection<GtaoTimerHudItemViewModel> Items { get; } = new();

    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public void ApplySettings(AppSettings settings) => _settings = settings;

    public void Start(GtaoTimerSlot slot)
    {
        var minutes = slot switch
        {
            GtaoTimerSlot.General => _settings.GeneralTimerMinutes,
            GtaoTimerSlot.Bunker => _settings.BunkerTimerMinutes,
            GtaoTimerSlot.Lsd => _settings.LsdTimerMinutes,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
        _completedSlots.Remove(slot);
        _engine.Start(slot, TimeSpan.FromMinutes(minutes), Now());
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
        Refresh();
    }

    public void StartGeneral(int minutes)
    {
        var normalized = GtaoTimerPresets.Normalize(GtaoTimerSlot.General, minutes);
        _completedSlots.Remove(GtaoTimerSlot.General);
        _engine.Start(GtaoTimerSlot.General, TimeSpan.FromMinutes(normalized), Now());
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
        Refresh();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private void Refresh()
    {
        var snapshots = _engine.Read(Now());
        var completionDetected = snapshots.Any(snapshot =>
            snapshot.IsExpired && _completedSlots.Add(snapshot.Slot));
        var next = snapshots.Select(snapshot => new GtaoTimerHudItemViewModel(
            snapshot.Slot,
            Label(snapshot.Slot),
            snapshot.IsExpired ? _localization["TimerExpired"] : GtaoTimerEngine.FormatRemaining(snapshot.Remaining),
            snapshot.IsExpired)).ToArray();
        Items.Clear();
        foreach (var item in next) Items.Add(item);
        IsVisible = Items.Count > 0;
        if (!IsVisible) _refreshTimer.Stop();
        if (completionDetected && _settings.TimerCompletionSoundEnabled)
        {
            CompletionSoundRequested?.Invoke();
        }
    }

    private string Label(GtaoTimerSlot slot) => _localization[slot switch
    {
        GtaoTimerSlot.General => "TimerGeneralShort",
        GtaoTimerSlot.Bunker => "TimerBunkerShort",
        GtaoTimerSlot.Lsd => "TimerLsdShort",
        _ => "TimerGeneralShort",
    }];

    private void OnLanguageChanged(object? sender, EventArgs args) => Refresh();

    private static TimeSpan Now() => Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());
}

internal sealed record GtaoTimerHudItemViewModel(
    GtaoTimerSlot Slot,
    string Label,
    string Value,
    bool IsExpired);

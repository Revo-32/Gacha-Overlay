using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using GachaOverlay.Core.Attention;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Hud.Game;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Attention;

namespace GachaOverlay.App.Presentation;

internal sealed class NotificationCenterViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly NotificationHistory _history = new();
    private readonly JsonNotificationStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly IAppLogger? _logger;
    private readonly ChatTypographyResolver _typography;
    private readonly Task _loaded;
    private readonly Queue<Action> _pendingActions = new();
    private bool _initialized;
    private readonly object _saveSync = new();
    private AttentionHistoryState? _pendingSave;
    private Task _writer = Task.CompletedTask;
    private bool _writing;
    private DiscordMessageMutation? _lastMutation;
    private long _generation = -1;
    private bool _detected;
    private bool _open;
    private bool _disposed;
    public NotificationCenterViewModel(string path, Dispatcher dispatcher, IAppLogger? logger = null)
    {
        _store = new(path);
        _dispatcher = dispatcher;
        _logger = logger;
        _typography = new ChatTypographyResolver(logger ?? NullAppLogger.Instance);
        ApplySettings(AppSettings.CreateDefault());
        ToggleCommand = new RelayCommand(() => { _open = !_open; Refresh(); });
        MarkAllReadCommand = new RelayCommand(() => _history.MarkRead());
        _loaded = LoadAsync();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<NotificationItemViewModel> Items { get; } = [];
    public bool IsOpen => _open;
    public int UnreadCount => _history.Items.Count(x => !x.IsRead);
    public bool HasUnread => UnreadCount > 0;
    public ICommand ToggleCommand { get; }
    public ICommand MarkAllReadCommand { get; }
    public System.Windows.Media.FontFamily MessageFontFamily { get; private set; } = new("Segoe UI");
    public System.Windows.Media.FontFamily ContextFontFamily { get; private set; } = new("Segoe UI");
    public System.Windows.FontWeight MessageFontWeight { get; private set; }
    public System.Windows.FontWeight ContextFontWeight { get; private set; }
    public double FontSizeDip { get; private set; }
    public double CaptionFontSize => Math.Max(10, FontSizeDip * 0.75);
    public double LineHeight { get; private set; }
    public void ApplySettings(AppSettings settings)
    {
        var resolved = _typography.Resolve(settings.ChatFontPreset);
        MessageFontFamily = resolved.Message.FontFamily;
        ContextFontFamily = resolved.Nickname.FontFamily;
        MessageFontWeight = resolved.Message.FontWeight;
        ContextFontWeight = resolved.Nickname.FontWeight;
        // Keep history compact without changing the user's chat typography settings.
        FontSizeDip = 12 * 96d / 72d;
        LineHeight = GachaOverlay.Core.Chat.ChatVisualMetrics.CalculateLineHeight(FontSizeDip, settings.ChatLineHeightMultiplier);
        foreach (var name in new[] { nameof(MessageFontFamily), nameof(ContextFontFamily), nameof(MessageFontWeight),
            nameof(ContextFontWeight), nameof(FontSizeDip), nameof(CaptionFontSize), nameof(LineHeight) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public void Close() { _open = false; Refresh(); }
    public void SetOwner(string owner) => Submit(() => _history.SetOwner(owner));
    public void ObserveLiveMutation(DiscordMessageMutation mutation, NormalizedDiscordMessage? message) =>
        Submit(() => _history.ObserveMutation(mutation, message));
    public void Observe(DiscordMessageState state) => Submit(() =>
    {
        if (state.IsBootstrapping) return;
        var snapshot = _generation != state.Generation;
        _generation = state.Generation;
        foreach (var message in state.MainChat) _history.ObserveChat(message, false);
        var mutation = state.LastMutation;
        if (!snapshot && mutation is not null && !ReferenceEquals(mutation, _lastMutation))
        {
            _history.ObserveMutation(mutation, state.MainChat.FirstOrDefault(x => x.MessageId == mutation.MessageId));
        }
        _lastMutation = mutation;
    });
    public void Sales(string messageId, bool current) => Submit(() => _history.Add(new(
        $"sales:{messageId}:{(current ? "current" : "next")}",
        current ? AttentionEventType.SalesCurrentTurn : AttentionEventType.SalesNextTurn,
        DateTimeOffset.UtcNow, messageId, "판매 대기열", current ? "판매할 차례입니다!" : "다음 판매 차례입니다.",
        false, current ? AttentionPriority.High : AttentionPriority.Medium)));
    public void Gta(GtaClientState state, bool dependentActive) => Submit(() =>
    {
        if (_detected == state.ProcessRunning) return;
        _detected = state.ProcessRunning;
        _history.Add(new($"gta:{DateTimeOffset.UtcNow.UtcTicks}:{state.Generation}",
            _detected ? AttentionEventType.GtaClientDetected : AttentionEventType.GtaClientLost,
            DateTimeOffset.UtcNow, state.Generation.ToString(), "GTA 클라이언트",
            _detected ? "GTA 감지됨" : dependentActive ? "GTA 실행을 확인할 수 없어 생산 타이머를 일시 정지합니다." : "GTA 미감지",
            false, !_detected && dependentActive ? AttentionPriority.Medium : AttentionPriority.Informational));
    });
    private async Task LoadAsync()
    {
        var state = await Task.Run(_store.Load);
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            _history.Restore(state);
            _history.Changed += OnChanged;
            _initialized = true;
            while (_pendingActions.TryDequeue(out var action)) action();
            Refresh();
        });
    }
    private void Submit(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => Submit(action)); return; }
        if (!_initialized)
        {
            // Startup storage is tiny; cap pending semantic observations as well as history.
            if (_pendingActions.Count < 64) _pendingActions.Enqueue(action);
            return;
        }
        action();
    }
    private void OnChanged()
    {
        Refresh();
        lock (_saveSync)
        {
            _pendingSave = _history.Snapshot();
            if (_writing) return;
            _writing = true;
            _writer = Task.Run(() =>
            {
                while (true)
                {
                    AttentionHistoryState state;
                    lock (_saveSync)
                    {
                        if (_pendingSave is null) { _writing = false; return; }
                        state = _pendingSave;
                        _pendingSave = null;
                    }
                    if (!_store.Save(state))
                        _logger?.Warning("ATTENTION", "알림 이력을 저장하지 못했습니다. 현재 세션의 알림은 유지됩니다.");
                }
            });
        }
    }
    private void Refresh()
    {
        Items.Clear();
        foreach (var entry in _history.Items) Items.Add(new(entry, () => _history.MarkRead(entry.Id)));
        foreach (var name in new[] { nameof(IsOpen), nameof(UnreadCount), nameof(HasUnread) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public void Dispose()
    {
        _disposed = true;
        _pendingActions.Clear();
        _history.Changed -= OnChanged;
        // Writes are coalesced and already contain immutable, bounded snapshots.
        _writer.GetAwaiter().GetResult();
    }
}

internal sealed class NotificationItemViewModel(AttentionEntry entry, Action markRead)
{
    public string Context => entry.Context;
    public string Preview => entry.Preview;
    public string Time => entry.Timestamp.ToLocalTime().ToString("MM/dd HH:mm");
    public string ReadLabel => entry.IsRead ? "읽음" : "읽음 처리";
    public string TypeLabel => entry.Type switch
    {
        AttentionEventType.DirectSelfMention => "나를 멘션",
        AttentionEventType.ReplyToSelf => "내 메시지 답장",
        AttentionEventType.SalesCurrentTurn => "판매 차례",
        AttentionEventType.SalesNextTurn => "다음 판매",
        AttentionEventType.GtaClientDetected => "GTA 감지",
        _ => "GTA 미감지",
    };
    public bool IsUnread => !entry.IsRead;
    public ICommand MarkReadCommand { get; } = new RelayCommand(markRead);
}

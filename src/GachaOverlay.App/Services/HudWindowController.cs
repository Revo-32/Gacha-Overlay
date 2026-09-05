using System.Windows;
using System.Diagnostics;
using GachaOverlay.Core.Diagnostics;
using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Hud.Presentation;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Timers;

namespace GachaOverlay.App.Services;

internal sealed class HudWindowController : IDisposable
{
    private readonly HudWindow _window;
    private readonly HudShellViewModel _viewModel;
    private readonly HudStateService _stateService;
    private readonly WindowInteropService _interop;
    private readonly WindowPlacementService _placement;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly GameForegroundMonitor _gameMonitor;
    private readonly ModifierDragService _modifierDrag;
    private readonly ChatPresentationCoordinator _chat;
    private readonly UiUpdateCoalescer _updateCoalescer;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly Action _openHudSettings;
    private AppSettings _settings;
    private RemoteChatHealthState _connectionState = RemoteChatHealthState.Disconnected;
    private string _connectionDetail = "NotStarted";
    private string? _foregroundProcess;
    private HudSessionState? _lastAppliedState;
    private DiscordMessageState? _pendingChatState;
    private long? _pendingChatReceivedAt;
    private readonly IRuntimeMetrics? _metrics;
    private string? _authenticatedUserId;
    private System.Windows.Size _chatAvailableSize;
    private bool _started;
    private bool _disposed;

    public HudWindowController(
        HudWindow window,
        HudShellViewModel viewModel,
        HudStateService stateService,
        WindowInteropService interop,
        WindowPlacementService placement,
        GlobalHotkeyService hotkeys,
        GameForegroundMonitor gameMonitor,
        ModifierDragService modifierDrag,
        ChatPresentationCoordinator chat,
        Action openHudSettings,
        ILocalizationService localization,
        IAppLogger logger,
        AppSettings initialSettings,
        IRuntimeMetrics? metrics = null)
    {
        _window = window;
        _viewModel = viewModel;
        _stateService = stateService;
        _interop = interop;
        _placement = placement;
        _hotkeys = hotkeys;
        _gameMonitor = gameMonitor;
        _modifierDrag = modifierDrag;
        _chat = chat;
        _openHudSettings = openHudSettings;
        _localization = localization;
        _logger = logger;
        _settings = initialSettings;
        _metrics = metrics;
        _updateCoalescer = new UiUpdateCoalescer(
            new DispatcherCallbackScheduler(window.Dispatcher),
            ExecutePresentationUpdate,
            exception => logger.Error("UI", "Coalesced HUD update failed.", exception));
    }

    public event Action<HudSessionState>? StateApplied;
    public event Action<int>? ChannelStepRequested;
    public event Action<GtaoTimerSlot>? TimerStartRequested;
    public event Action? GtaCompanionVisibilityToggleRequested;
    public event Action? BusinessManagerVisibilityToggleRequested;
    private void OnChannelStep(int direction) => ChannelStepRequested?.Invoke(direction);
    private void OnTimerStart(GtaoTimerSlot slot) => TimerStartRequested?.Invoke(slot);
    private void OnGtaCompanionVisibilityToggle() => GtaCompanionVisibilityToggleRequested?.Invoke();
    private void OnBusinessManagerVisibilityToggle() => BusinessManagerVisibilityToggleRequested?.Invoke();

    public HudSessionState State => _stateService.Current;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _window.DataContext = _viewModel;
        _stateService.StateChanged += OnStateChanged;
        _window.DragRequested += OnDragRequested;
        _window.SettingsRequested += OnSettingsRequested;
        _window.ChatAvailableSizeChanged += OnChatAvailableSizeChanged;
        _hotkeys.LockToggleRequested += OnLockToggleRequested;
        _hotkeys.VisibilityToggleRequested += OnVisibilityToggleRequested;
        _hotkeys.ChannelStepRequested += OnChannelStep;
        _hotkeys.TimerStartRequested += OnTimerStart;
        _hotkeys.GtaCompanionVisibilityToggleRequested += OnGtaCompanionVisibilityToggle;
        _hotkeys.BusinessManagerVisibilityToggleRequested += OnBusinessManagerVisibilityToggle;
        _gameMonitor.ForegroundChanged += OnGameForegroundChanged;
        _modifierDrag.DragCompleted += OnModifierDragCompleted;
        _localization.LanguageChanged += OnLanguageChanged;

        _interop.Initialize();
        _placement.Start();
        _window.SetAppearance(_settings);
        _viewModel.ApplySettings(_settings);
        _hotkeys.Bind(_settings);
        _gameMonitor.SetEnabled(
            _settings.HudVisibilityMode == HudVisibilityMode.GameForegroundOnly);
        ApplyState(_stateService.Current, force: true);
        _logger.Information("HUD", "Created; initial connection gate is closed.");
    }

    public void ToggleLock() => RunOnUi(_stateService.ToggleLock);

    public void ToggleUserVisibility() => RunOnUi(_stateService.ToggleUserVisibility);

    public void ShowHud() => RunOnUi(() => _stateService.SetUserHudEnabled(true));

    public void HideHud() => RunOnUi(() => _stateService.SetUserHudEnabled(false));

    public void ResetPlacement() => RunOnUi(_placement.ResetToDefault);

    public void ResetPosition() => RunOnUi(_placement.ResetPosition);

    public void ResetSize() => RunOnUi(_placement.ResetSize);

    public void CenterOnCurrentDisplay() => RunOnUi(_placement.CenterOnCurrentDisplay);

    public void NotifyCommittedChannel(string label) => RunOnUi(() => _viewModel.Chat.NotifyCommittedChannel(label));

    public void ClearMediaCache() => RunOnUi(_chat.ClearMediaCache);

    public bool TryApplyHotkeySettings(AppSettings settings) => !_disposed &&
        (_window.Dispatcher.CheckAccess() ? _hotkeys.Bind(settings) : _window.Dispatcher.Invoke(() => _hotkeys.Bind(settings)));

    public bool TryApplyHotkeys(HotkeySetting lockSetting, HotkeySetting visibilitySetting)
    {
        if (_disposed)
        {
            return false;
        }

        bool Apply()
        {
            if (!_hotkeys.TryBind(lockSetting, visibilitySetting))
            {
                return false;
            }

            _settings = _settings with
            {
                HudLockHotkey = lockSetting,
                HudVisibilityHotkey = visibilitySetting,
            };
            return true;
        }

        return _window.Dispatcher.CheckAccess()
            ? Apply()
            : _window.Dispatcher.Invoke(Apply);
    }

    public void OnRemoteConnectionStatus(RemoteChatSnapshot snapshot) =>
        RunOnUi(() =>
        {
            _connectionState = snapshot.Health;
            _connectionDetail = snapshot.Detail;
            if (snapshot.Health == RemoteChatHealthState.Live &&
                !_stateService.Current.HasInitialConnectionReady)
            {
                _stateService.MarkInitialConnectionReady();
                _logger.Information("HUD", "Initial connection gate opened by Remote Chat.");
            }

            _updateCoalescer.Request();
        });

    public void OnDiscordMessageStateChanged(DiscordMessageState state)
    {
        var receivedAt = Stopwatch.GetTimestamp();
        RunOnUi(() =>
        {
            _pendingChatState = state;
            _pendingChatReceivedAt = receivedAt;
            _updateCoalescer.Request();
        });
    }

    public void OnAuthenticatedUserChanged(DiscordAuthenticatedUser user) =>
        RunOnUi(() =>
        {
            _authenticatedUserId = user.UserId;
            _updateCoalescer.Request();
        });

    public void ApplySettings(AppSettings settings) =>
        RunOnUi(() =>
        {
            var old = _settings;
            _settings = settings;
            _window.SetAppearance(settings);
            _viewModel.ApplySettings(settings);
            _chat.ApplySettings(settings);

            if (old.HudLockHotkey != settings.HudLockHotkey ||
                old.HudVisibilityHotkey != settings.HudVisibilityHotkey ||
                old.PreviousMainChannelHotkey != settings.PreviousMainChannelHotkey ||
                old.NextMainChannelHotkey != settings.NextMainChannelHotkey ||
                old.GeneralTimerHotkey != settings.GeneralTimerHotkey ||
                old.BunkerTimerHotkey != settings.BunkerTimerHotkey ||
                old.LsdTimerHotkey != settings.LsdTimerHotkey ||
                old.GtaCompanionVisibilityHotkey != settings.GtaCompanionVisibilityHotkey ||
                old.BusinessManagerVisibilityHotkey != settings.BusinessManagerVisibilityHotkey)
            {
                _hotkeys.Bind(settings);
            }

            if (old.HudVisibilityMode != settings.HudVisibilityMode)
            {
                _stateService.SetVisibilityMode(settings.HudVisibilityMode);
                _gameMonitor.SetEnabled(
                    settings.HudVisibilityMode == HudVisibilityMode.GameForegroundOnly);
            }

            UpdateModifierDrag(_stateService.Current);
            _updateCoalescer.Request();
        });

    public void RefreshTheme() => RunOnUi(_window.RefreshTheme);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateService.StateChanged -= OnStateChanged;
        _window.DragRequested -= OnDragRequested;
        _window.SettingsRequested -= OnSettingsRequested;
        _window.ChatAvailableSizeChanged -= OnChatAvailableSizeChanged;
        _hotkeys.LockToggleRequested -= OnLockToggleRequested;
        _hotkeys.VisibilityToggleRequested -= OnVisibilityToggleRequested;
        _hotkeys.ChannelStepRequested -= OnChannelStep;
        _hotkeys.TimerStartRequested -= OnTimerStart;
        _hotkeys.GtaCompanionVisibilityToggleRequested -= OnGtaCompanionVisibilityToggle;
        _hotkeys.BusinessManagerVisibilityToggleRequested -= OnBusinessManagerVisibilityToggle;
        _gameMonitor.ForegroundChanged -= OnGameForegroundChanged;
        _modifierDrag.DragCompleted -= OnModifierDragCompleted;
        _localization.LanguageChanged -= OnLanguageChanged;
        _hotkeys.Dispose();
        _gameMonitor.Dispose();
        _modifierDrag.Dispose();
        _chat.Dispose();
        _updateCoalescer.Dispose();
        _placement.Dispose();
        _interop.Dispose();
        _window.AllowClose = true;
        if (_window.IsLoaded)
        {
            _window.Close();
        }
    }

    private void OnStateChanged(HudSessionState state) =>
        RunOnUi(() => ApplyState(state, force: false));

    private void ApplyState(HudSessionState state, bool force)
    {
        if (_disposed)
        {
            return;
        }

        var previous = _lastAppliedState;
        if (force || previous?.IsLocked != state.IsLocked)
        {
            _interop.ApplyClickThrough(state.IsClickThrough);
            _logger.Information("HUD", state.IsLocked ? "Locked." : "Unlocked.");
            _logger.Information(
                "HUD",
                state.IsClickThrough ? "Click-through enabled." : "Click-through disabled.");
        }

        if (state.EffectiveVisible && !_window.IsVisible)
        {
            _window.Show();
            _interop.ApplyTopmost();
            _logger.Information("HUD", $"Shown reason={GetVisibilityReason(state)}.");
        }
        else if (!state.EffectiveVisible && _window.IsVisible)
        {
            _window.Hide();
            _logger.Information("HUD", $"Hidden reason={GetVisibilityReason(state)}.");
        }
        else if (state.EffectiveVisible && (force || previous?.EffectiveVisible != true))
        {
            _interop.ApplyTopmost();
        }

        _chat.SetAnimationsVisible(state.EffectiveVisible);

        UpdateModifierDrag(state);
        _lastAppliedState = state;
        _updateCoalescer.Request();
        try
        {
            StateApplied?.Invoke(state);
        }
        catch (Exception exception)
        {
            _logger.Error("HUD", "HUD state subscriber failed.", exception);
        }
    }

    private void UpdateModifierDrag(HudSessionState state) =>
        _modifierDrag.SetEnabled(
            _settings.HudModifierDragEnabled &&
            state.IsLocked &&
            state.EffectiveVisible);

    private void OnDragRequested()
    {
        if (_stateService.Current.IsLocked)
        {
            return;
        }

        try
        {
            _window.DragMove();
            _placement.SaveNow();
        }
        catch (InvalidOperationException exception)
        {
            _logger.Warning("HUD", $"Drag gesture ended unexpectedly: {exception.Message}");
        }
    }

    private void OnSettingsRequested()
    {
        if (!_disposed && !_stateService.Current.IsLocked)
        {
            _logger.Information("HUD", "Settings input accepted while unlocked.");
            _openHudSettings();
        }
    }

    private void OnLockToggleRequested() => _stateService.ToggleLock();

    private void OnVisibilityToggleRequested() => _stateService.ToggleUserVisibility();

    private void OnGameForegroundChanged(GameForegroundSnapshot snapshot) =>
        RunOnUi(() =>
        {
            _foregroundProcess = snapshot.ProcessName;
            _stateService.SetTargetGameForeground(snapshot.IsTargetGame);
            _updateCoalescer.Request();
        });

    private void OnModifierDragCompleted() => RunOnUi(_placement.SaveNow);

    private void OnLanguageChanged(object? sender, EventArgs eventArgs) =>
        _updateCoalescer.Request();

    private void OnChatAvailableSizeChanged(System.Windows.Size size)
    {
        _chatAvailableSize = size;
        _updateCoalescer.Request();
    }

    private void ExecutePresentationUpdate(int requestCount)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.Update(
            _stateService.Current,
            _connectionState.ToString(),
            _connectionDetail,
            _foregroundProcess);
        if (_pendingChatState is not null)
        {
            _chat.ApplyState(_pendingChatState, _authenticatedUserId);
            if (_pendingChatReceivedAt is { } receivedAt)
            {
                _metrics?.RecordDuration("wpf.chat.store_to_presentation.duration", Stopwatch.GetElapsedTime(receivedAt));
                _pendingChatReceivedAt = null;
            }
        }

        if (_chatAvailableSize.Width > 0 && _chatAvailableSize.Height > 0)
        {
            _chat.EvaluateResponsive(
                _chatAvailableSize,
                System.Windows.Media.VisualTreeHelper.GetDpi(_window).PixelsPerDip);
        }

        _viewModel.Sales.UpdateHudContext(
            _stateService.Current.EffectiveVisible,
            _chat.CurrentResponsiveLevel == ChatResponsiveLevel.UltraCompact,
            SystemParameters.ClientAreaAnimation,
            !_stateService.Current.IsLocked);
        _viewModel.Session.UpdateLayout(
            _chat.CurrentResponsiveLevel == ChatResponsiveLevel.UltraCompact);
        if (requestCount > 1)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UI] Coalesced HUD update executed requests={requestCount}.");
        }
    }

    private void RunOnUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_window.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
    }

    private static string GetVisibilityReason(HudSessionState state)
    {
        if (!state.HasInitialConnectionReady)
        {
            return "InitialConnectionGate";
        }

        if (!state.UserHudEnabled)
        {
            return "User";
        }

        if (state.VisibilityMode == HudVisibilityMode.GameForegroundOnly &&
            !state.IsTargetGameForeground)
        {
            return "GameNotForeground";
        }

        return state.VisibilityMode == HudVisibilityMode.Always ? "Always" : "GameForeground";
    }
}

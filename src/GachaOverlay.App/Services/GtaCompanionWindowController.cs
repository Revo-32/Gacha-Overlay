using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Hud.Geometry;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using LSOverlay.Protocol;

namespace GachaOverlay.App.Services;

internal sealed class GtaCompanionWindowController : IDisposable
{
    public const string WindowId = "gta-companion";
    private static readonly FloatingHudPlacementOptions PlacementOptions = new(
        DefaultWidth: 360,
        DefaultHeight: 470,
        MinimumWidth: 300,
        MinimumHeight: 180,
        MinimumVisibleWidth: 72,
        MinimumVisibleHeight: 40,
        Margin: 24);

    private readonly GtaCompanionWindow _window;
    private readonly GtaCompanionViewModel _viewModel;
    private readonly HudStateService _hudState;
    private readonly WindowInteropService _interop;
    private readonly DisplayTopologyService _displays;
    private readonly FloatingHudPlacementEngine _placement = new();
    private readonly ISettingsStore _settingsStore;
    private readonly IAppLogger _logger;
    private readonly DispatcherTimer _saveTimer;
    private AppSettings _settings;
    private bool _temporaryVisible = true;
    private bool _applyingGeometry;
    private bool _started;
    private bool _disposed;

    public GtaCompanionWindowController(
        GtaCompanionWindow window,
        GtaCompanionViewModel viewModel,
        HudStateService hudState,
        ISettingsStore settingsStore,
        IAppLogger logger,
        AppSettings settings)
    {
        _window = window;
        _viewModel = viewModel;
        _hudState = hudState;
        _settingsStore = settingsStore;
        _logger = logger;
        _settings = settings;
        _displays = new DisplayTopologyService();
        _interop = new WindowInteropService(window, () => _hudState.IsLocked, logger);
        _saveTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(600),
            DispatcherPriority.ApplicationIdle,
            OnSaveTimer,
            window.Dispatcher);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        _window.DataContext = _viewModel;
        _window.DragRequested += OnDragRequested;
        _window.LocationChanged += OnGeometryChanged;
        _window.SizeChanged += OnGeometryChanged;
        _hudState.StateChanged += OnHudStateChanged;
        _interop.DisplayTopologyChanged += OnDisplayTopologyChanged;
        _interop.Initialize();
        RestoreGeometry();
        ApplyState();
        _logger.Information("GTA-COMPANION", "Independent floating HUD initialized.");
    }

    public void ApplySettings(AppSettings settings)
    {
        if (_disposed) return;
        RunOnUi(() =>
        {
            var modeChanged = _settings.GtaCompanionWeeklyEventsEnabled != settings.GtaCompanionWeeklyEventsEnabled;
            _settings = settings;
            _viewModel.ApplySettings(settings);
            _window.SetSurfaceOpacity(settings.GtaCompanionSurfaceOpacity);
            ApplyState();
            if (modeChanged) _window.ApplyChallengeOnlyLayout(_viewModel.IsChallengeOnly);
        });
    }

    public void ApplySnapshot(GtaCompanionSnapshot snapshot) =>
        RunOnUi(() => _viewModel.ApplySnapshot(snapshot));

    public void ToggleTemporaryVisibility() => RunOnUi(() =>
    {
        _temporaryVisible = !_temporaryVisible;
        ApplyState();
    });

    public void RefreshTheme() => RunOnUi(_window.RefreshTheme);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Stop();
        if (_started)
        {
            SaveGeometry();
            _window.DragRequested -= OnDragRequested;
            _window.LocationChanged -= OnGeometryChanged;
            _window.SizeChanged -= OnGeometryChanged;
            _hudState.StateChanged -= OnHudStateChanged;
            _interop.DisplayTopologyChanged -= OnDisplayTopologyChanged;
        }
        _viewModel.Dispose();
        _interop.Dispose();
        _window.AllowClose = true;
        if (_window.IsLoaded) _window.Close();
    }

    private void ApplyState()
    {
        var anySection = _settings.GtaCompanionDailyEnabled ||
            _settings.GtaCompanionWeeklyEnabled ||
            _settings.GtaCompanionWeeklyEventsEnabled;
        var show = _settings.GtaCompanionEnabled && _temporaryVisible && anySection;
        _viewModel.SetUnlocked(!_hudState.IsLocked);
        _window.SetSurfaceOpacity(_settings.GtaCompanionSurfaceOpacity);
        _interop.ApplyClickThrough(_hudState.IsLocked);
        if (show && !_window.IsVisible)
        {
            _window.Show();
            _interop.ApplyTopmost();
            _window.ApplyChallengeOnlyLayout(_viewModel.IsChallengeOnly);
        }
        else if (!show && _window.IsVisible)
        {
            _window.Hide();
        }
        else if (show)
        {
            _interop.ApplyTopmost();
        }
    }

    private void RestoreGeometry()
    {
        try
        {
            var persisted = _settingsStore.Current.GtaCompanionWindowGeometry;
            var normalized = NormalizeLegacyDevelopmentGeometry(persisted);
            var result = _placement.Resolve(
                normalized,
                _displays.GetWorkingAreas(),
                PlacementOptions);
            _applyingGeometry = true;
            _interop.SetWindowBounds(new HudWindowGeometry(
                result.Geometry.X, result.Geometry.Y, result.Geometry.Width, result.Geometry.Height,
                result.Geometry.DisplayId, result.Geometry.Dpi));
            if (result.WasCorrected || normalized != persisted)
            {
                _settingsStore.Update(settings => settings with { GtaCompanionWindowGeometry = result.Geometry });
            }
        }
        finally
        {
            _applyingGeometry = false;
        }
    }

    private void SaveGeometry()
    {
        if (_applyingGeometry || !_interop.TryGetWindowRectangle(out var rectangle)) return;
        var displays = _displays.GetWorkingAreas();
        var display = displays.MaxBy(candidate =>
        {
            var intersection = rectangle.Intersection(candidate.Bounds);
            return intersection.Width * intersection.Height;
        });
        var geometry = new FloatingHudGeometry(
            rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height,
            display?.Id, display?.Dpi ?? _interop.GetWindowDpi());
        if (!_settingsStore.Update(settings => settings with { GtaCompanionWindowGeometry = geometry }))
            _logger.Warning("GTA-COMPANION", "Floating HUD geometry could not be persisted.");
    }

    private void OnHudStateChanged(HudSessionState state) => RunOnUi(ApplyState);

    private void OnDragRequested()
    {
        if (_hudState.IsLocked) return;
        try { _window.DragMove(); SaveGeometry(); }
        catch (InvalidOperationException) { }
    }

    private static FloatingHudGeometry? NormalizeLegacyDevelopmentGeometry(FloatingHudGeometry? geometry)
    {
        if (geometry is null)
        {
            return geometry;
        }

        var isInitialM2Default = Math.Abs(geometry.Width - 390) <= 8 &&
            Math.Abs(geometry.Height - 650) <= 8;
        var isPreviousCorrectiveDefault = Math.Abs(geometry.Width - 360) <= 8 &&
            Math.Abs(geometry.Height - 500) <= 8;
        return isInitialM2Default || isPreviousCorrectiveDefault
            ? geometry with { Width = PlacementOptions.DefaultWidth, Height = PlacementOptions.DefaultHeight }
            : geometry;
    }

    private void OnGeometryChanged(object? sender, EventArgs eventArgs)
    {
        if (_applyingGeometry || _disposed) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimer(object? sender, EventArgs eventArgs)
    {
        _saveTimer.Stop();
        SaveGeometry();
    }

    private void OnDisplayTopologyChanged() => RunOnUi(() =>
    {
        SaveGeometry();
        RestoreGeometry();
    });

    private void RunOnUi(Action action)
    {
        if (_disposed) return;
        if (_window.Dispatcher.CheckAccess()) action();
        else _window.Dispatcher.BeginInvoke(action);
    }
}

using System.Windows.Threading;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Hud.Geometry;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Services;

internal sealed class BusinessManagerWindowController : IDisposable
{
    public const string WindowId = "business-manager";
    private static readonly FloatingHudPlacementOptions PlacementOptions = new(
        390, 620, 320, 220, 72, 40, 24);
    private readonly BusinessManagerWindow _window;
    private readonly BusinessManagerViewModel _viewModel;
    private readonly HudStateService _hudState;
    private readonly WindowInteropService _interop;
    private readonly DisplayTopologyService _displays = new();
    private readonly FloatingHudPlacementEngine _placement = new();
    private readonly ISettingsStore _settingsStore;
    private readonly IAppLogger _logger;
    private readonly DispatcherTimer _saveTimer;
    private AppSettings _settings;
    private bool _temporaryVisible = true;
    private bool _applyingGeometry;
    private bool _started;
    private bool _disposed;

    public BusinessManagerWindowController(BusinessManagerWindow window,
        BusinessManagerViewModel viewModel, HudStateService hudState,
        ISettingsStore settingsStore, IAppLogger logger, AppSettings settings)
    {
        _window = window; _viewModel = viewModel; _hudState = hudState;
        _settingsStore = settingsStore; _logger = logger; _settings = settings;
        _interop = new WindowInteropService(window, () => _hudState.IsLocked, logger);
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600),
            DispatcherPriority.ApplicationIdle, OnSaveTimer, window.Dispatcher);
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
        _logger.Information("BUSINESS", $"Floating HUD initialized WindowId={WindowId}.");
    }

    public void ApplySettings(AppSettings settings) => RunOnUi(() =>
    {
        _settings = settings;
        _viewModel.ApplySettings(settings);
        ApplyState();
    });

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
        var show = _settings.BusinessManagerEnabled && _temporaryVisible &&
            _hudState.Current.EffectiveVisible;
        _viewModel.SetUnlocked(!_hudState.IsLocked);
        _window.SetSurfaceOpacity(_settings.BusinessManagerSurfaceOpacity);
        _interop.ApplyClickThrough(_hudState.IsLocked);
        if (show && !_window.IsVisible) { _window.Show(); _interop.ApplyTopmost(); }
        else if (!show && _window.IsVisible) _window.Hide();
        else if (show) _interop.ApplyTopmost();
    }

    private void RestoreGeometry()
    {
        try
        {
            var result = _placement.Resolve(_settingsStore.Current.BusinessManagerWindowGeometry,
                _displays.GetWorkingAreas(), PlacementOptions);
            _applyingGeometry = true;
            _interop.SetWindowBounds(new HudWindowGeometry(result.Geometry.X, result.Geometry.Y,
                result.Geometry.Width, result.Geometry.Height, result.Geometry.DisplayId, result.Geometry.Dpi));
            if (result.WasCorrected)
                _settingsStore.Update(value => value with { BusinessManagerWindowGeometry = result.Geometry });
        }
        finally { _applyingGeometry = false; }
    }

    private void SaveGeometry()
    {
        if (_applyingGeometry || !_interop.TryGetWindowRectangle(out var rectangle)) return;
        var display = _displays.GetWorkingAreas().MaxBy(candidate =>
        {
            var intersection = rectangle.Intersection(candidate.Bounds);
            return intersection.Width * intersection.Height;
        });
        var geometry = new FloatingHudGeometry(rectangle.X, rectangle.Y, rectangle.Width,
            rectangle.Height, display?.Id, display?.Dpi ?? _interop.GetWindowDpi());
        if (!_settingsStore.Update(value => value with { BusinessManagerWindowGeometry = geometry }))
            _logger.Warning("BUSINESS", "Floating HUD geometry could not be persisted.");
    }

    private void OnHudStateChanged(HudSessionState state) => RunOnUi(ApplyState);
    private void OnDragRequested()
    {
        if (_hudState.IsLocked) return;
        try { _window.DragMove(); SaveGeometry(); } catch (InvalidOperationException) { }
    }
    private void OnGeometryChanged(object? sender, EventArgs args)
    { if (!_applyingGeometry && !_disposed) { _saveTimer.Stop(); _saveTimer.Start(); } }
    private void OnSaveTimer(object? sender, EventArgs args) { _saveTimer.Stop(); SaveGeometry(); }
    private void OnDisplayTopologyChanged() => RunOnUi(() => { SaveGeometry(); RestoreGeometry(); });
    private void RunOnUi(Action action)
    {
        if (_disposed) return;
        if (_window.Dispatcher.CheckAccess()) action(); else _window.Dispatcher.BeginInvoke(action);
    }
}

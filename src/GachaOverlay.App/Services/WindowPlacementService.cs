using System.Windows;
using System.Windows.Threading;
using GachaOverlay.Core.Hud.Geometry;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Services;

internal sealed class WindowPlacementService : IDisposable
{
    private readonly Window _window;
    private readonly WindowInteropService _interop;
    private readonly DisplayTopologyService _displayTopology;
    private readonly WindowPlacementEngine _placementEngine;
    private readonly ISettingsStore _settingsStore;
    private readonly IAppLogger _logger;
    private readonly DispatcherTimer _saveTimer;
    private bool _applyingPlacement;
    private bool _started;
    private bool _disposed;

    public WindowPlacementService(
        Window window,
        WindowInteropService interop,
        DisplayTopologyService displayTopology,
        WindowPlacementEngine placementEngine,
        ISettingsStore settingsStore,
        IAppLogger logger)
    {
        _window = window;
        _interop = interop;
        _displayTopology = displayTopology;
        _placementEngine = placementEngine;
        _settingsStore = settingsStore;
        _logger = logger;
        _saveTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(600),
            DispatcherPriority.ApplicationIdle,
            OnSaveTimer,
            window.Dispatcher)
        {
            IsEnabled = false,
        };
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        Restore();
        _window.LocationChanged += OnGeometryChanged;
        _window.SizeChanged += OnGeometryChanged;
        _interop.DisplayTopologyChanged += OnDisplayTopologyChanged;
    }

    public void ResetToDefault()
    {
        if (!_settingsStore.Update(settings => settings with { HudWindowGeometry = null }))
        {
            _logger.Warning("HUD", "HUD geometry reset could not be persisted.");
        }

        Restore();
    }

    public void ResetPosition()
    {
        if (!TryGetCurrentContext(out var rectangle, out var display))
        {
            ResetToDefault();
            return;
        }

        var margin = WindowPlacementEngine.DefaultMarginDip * display.Scale;
        ApplyAndSave(new HudWindowGeometry(
            Math.Max(display.Bounds.X, display.Bounds.Right - rectangle.Width - margin),
            Math.Max(display.Bounds.Y, display.Bounds.Y + margin),
            rectangle.Width,
            rectangle.Height,
            display.Id,
            display.Dpi));
    }

    public void ResetSize()
    {
        if (!TryGetCurrentContext(out var rectangle, out var display))
        {
            ResetToDefault();
            return;
        }

        var width = Math.Min(
            WindowPlacementEngine.DefaultWidthDip * display.Scale,
            display.Bounds.Width);
        var height = Math.Min(
            WindowPlacementEngine.DefaultHeightDip * display.Scale,
            display.Bounds.Height);
        ApplyAndSave(new HudWindowGeometry(
            Math.Clamp(rectangle.X, display.Bounds.X, display.Bounds.Right - width),
            Math.Clamp(rectangle.Y, display.Bounds.Y, display.Bounds.Bottom - height),
            width,
            height,
            display.Id,
            display.Dpi));
    }

    public void CenterOnCurrentDisplay()
    {
        if (!TryGetCurrentContext(out var rectangle, out var display))
        {
            ResetToDefault();
            return;
        }

        var width = Math.Min(rectangle.Width, display.Bounds.Width);
        var height = Math.Min(rectangle.Height, display.Bounds.Height);
        ApplyAndSave(new HudWindowGeometry(
            display.Bounds.X + ((display.Bounds.Width - width) / 2),
            display.Bounds.Y + ((display.Bounds.Height - height) / 2),
            width,
            height,
            display.Id,
            display.Dpi));
    }

    public void SaveNow()
    {
        if (_applyingPlacement || !_interop.TryGetWindowRectangle(out var rectangle))
        {
            return;
        }

        var displays = _displayTopology.GetWorkingAreas();
        var display = FindBestDisplay(rectangle, displays);
        var geometry = new HudWindowGeometry(
            rectangle.X,
            rectangle.Y,
            rectangle.Width,
            rectangle.Height,
            display?.Id,
            display?.Dpi ?? _interop.GetWindowDpi());
        if (!_settingsStore.Update(settings => settings with { HudWindowGeometry = geometry }))
        {
            _logger.Warning("HUD", "HUD geometry could not be persisted.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimer;
        if (_started)
        {
            _window.LocationChanged -= OnGeometryChanged;
            _window.SizeChanged -= OnGeometryChanged;
            _interop.DisplayTopologyChanged -= OnDisplayTopologyChanged;
            SaveNow();
        }
    }

    private void Restore()
    {
        try
        {
            var result = _placementEngine.Resolve(
                _settingsStore.Current.HudWindowGeometry,
                _displayTopology.GetWorkingAreas());
            _applyingPlacement = true;
            _interop.SetWindowBounds(result.Geometry);
            _logger.Information(
                "HUD",
                $"Geometry {(result.WasCorrected ? "corrected" : "restored")} reason={result.Reason} x={result.Geometry.X:F0} y={result.Geometry.Y:F0} width={result.Geometry.Width:F0} height={result.Geometry.Height:F0} display={result.Geometry.DisplayId ?? "unknown"} dpi={result.Geometry.Dpi:F0}.");
        }
        catch (Exception exception)
        {
            _logger.Error("HUD", "HUD geometry restore failed; WPF defaults remain active.", exception);
        }
        finally
        {
            _applyingPlacement = false;
        }
    }

    private bool TryGetCurrentContext(
        out HudRectangle rectangle,
        out DisplayWorkingArea display)
    {
        if (!_interop.TryGetWindowRectangle(out rectangle))
        {
            display = default!;
            return false;
        }

        display = FindBestDisplay(rectangle, _displayTopology.GetWorkingAreas())!;
        return display is not null;
    }

    private void ApplyAndSave(HudWindowGeometry geometry)
    {
        try
        {
            _applyingPlacement = true;
            _interop.SetWindowBounds(geometry);
        }
        finally
        {
            _applyingPlacement = false;
        }

        SaveNow();
    }

    private void OnGeometryChanged(object? sender, EventArgs eventArgs)
    {
        if (_applyingPlacement || _disposed)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimer(object? sender, EventArgs eventArgs)
    {
        _saveTimer.Stop();
        SaveNow();
    }

    private void OnDisplayTopologyChanged()
    {
        if (_disposed)
        {
            return;
        }

        _logger.Information("HUD", "Display topology changed.");
        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                SaveNow();
                Restore();
            }));
    }

    private static DisplayWorkingArea? FindBestDisplay(
        HudRectangle rectangle,
        IReadOnlyList<DisplayWorkingArea> displays) =>
        displays.Where(display => display.IsValid)
            .MaxBy(display =>
            {
                var overlap = rectangle.Intersection(display.Bounds);
                return overlap.Width * overlap.Height;
            });
}

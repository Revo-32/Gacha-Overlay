namespace GachaOverlay.Core.Hud.Geometry;

public sealed record FloatingHudGeometry(
    double X,
    double Y,
    double Width,
    double Height,
    string? DisplayId = null,
    double Dpi = 96)
{
    public HudRectangle Rectangle => new(X, Y, Width, Height);
}

public sealed record FloatingHudPlacementOptions(
    double DefaultWidth,
    double DefaultHeight,
    double MinimumWidth,
    double MinimumHeight,
    double MinimumVisibleWidth = 72,
    double MinimumVisibleHeight = 40,
    double Margin = 24)
{
    public bool IsValid =>
        double.IsFinite(DefaultWidth) && DefaultWidth > 0 &&
        double.IsFinite(DefaultHeight) && DefaultHeight > 0 &&
        double.IsFinite(MinimumWidth) && MinimumWidth > 0 &&
        double.IsFinite(MinimumHeight) && MinimumHeight > 0 &&
        double.IsFinite(MinimumVisibleWidth) && MinimumVisibleWidth > 0 &&
        double.IsFinite(MinimumVisibleHeight) && MinimumVisibleHeight > 0 &&
        double.IsFinite(Margin) && Margin >= 0;
}

public sealed record FloatingHudPlacementResult(
    FloatingHudGeometry Geometry,
    bool WasCorrected,
    string Reason);

public sealed record FloatingHudWindowState(
    string WindowId,
    FloatingHudGeometry? Geometry,
    bool UserVisible = true);

public sealed record FloatingHudInteractionState(bool IsLocked)
{
    public bool IsClickThrough => IsLocked;

    public bool IsInteractive => !IsLocked;
}

public interface IGlobalHudLockSource
{
    bool IsLocked { get; }

    event Action<bool>? LockChanged;
}

public sealed class FloatingHudGeometryEditor
{
    public FloatingHudGeometry Move(
        FloatingHudGeometry geometry,
        double horizontalChange,
        double verticalChange)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return geometry with
        {
            X = AddFinite(geometry.X, horizontalChange),
            Y = AddFinite(geometry.Y, verticalChange),
        };
    }

    public FloatingHudGeometry Resize(
        FloatingHudGeometry geometry,
        double widthChange,
        double heightChange,
        FloatingHudPlacementOptions options)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValid)
        {
            throw new ArgumentException("Floating HUD placement options are invalid.", nameof(options));
        }

        return geometry with
        {
            Width = Math.Max(options.MinimumWidth, AddFinite(geometry.Width, widthChange)),
            Height = Math.Max(options.MinimumHeight, AddFinite(geometry.Height, heightChange)),
        };
    }

    private static double AddFinite(double value, double change) =>
        double.IsFinite(value) && double.IsFinite(change) && double.IsFinite(value + change)
            ? value + change
            : value;
}

public sealed class FloatingHudPlacementEngine
{
    public FloatingHudPlacementResult Resolve(
        FloatingHudGeometry? saved,
        IReadOnlyList<DisplayWorkingArea> displays,
        FloatingHudPlacementOptions options)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValid)
        {
            throw new ArgumentException("Floating HUD placement options are invalid.", nameof(options));
        }

        var validDisplays = displays.Where(display => display.IsValid).ToArray();
        if (validDisplays.Length == 0)
        {
            return new FloatingHudPlacementResult(
                new FloatingHudGeometry(24, 24, options.DefaultWidth, options.DefaultHeight),
                true,
                "NoValidDisplayData");
        }

        if (saved is null || !saved.Rectangle.IsFiniteAndPositive)
        {
            return Default(validDisplays, options);
        }

        var target = FindTarget(saved, validDisplays);
        var scale = DisplayWorkingArea.NormalizeDpi(target.Dpi) /
            DisplayWorkingArea.NormalizeDpi(saved.Dpi);
        var width = Math.Clamp(
            saved.Width * scale,
            Math.Min(options.MinimumWidth * target.Scale, target.Bounds.Width),
            target.Bounds.Width);
        var height = Math.Clamp(
            saved.Height * scale,
            Math.Min(options.MinimumHeight * target.Scale, target.Bounds.Height),
            target.Bounds.Height);
        var candidate = new HudRectangle(saved.X, saved.Y, width, height);
        var visibleWidth = Math.Min(width, options.MinimumVisibleWidth * target.Scale);
        var visibleHeight = Math.Min(height, options.MinimumVisibleHeight * target.Scale);
        var intersectsEnough = validDisplays.Any(display =>
        {
            var intersection = candidate.Intersection(display.Bounds);
            return intersection.Width >= Math.Min(width, options.MinimumVisibleWidth * display.Scale) &&
                intersection.Height >= Math.Min(height, options.MinimumVisibleHeight * display.Scale);
        });
        var x = intersectsEnough
            ? candidate.X
            : Math.Clamp(candidate.X, target.Bounds.X - width + visibleWidth, target.Bounds.Right - visibleWidth);
        var y = intersectsEnough
            ? candidate.Y
            : Math.Clamp(candidate.Y, target.Bounds.Y - height + visibleHeight, target.Bounds.Bottom - visibleHeight);
        var corrected = !NearlyEqual(saved.Width, width) || !NearlyEqual(saved.Height, height) ||
            !NearlyEqual(saved.X, x) || !NearlyEqual(saved.Y, y);
        return new FloatingHudPlacementResult(
            new FloatingHudGeometry(x, y, width, height, target.Id, target.Dpi),
            corrected,
            intersectsEnough
                ? corrected ? "SizeOrDpiCorrected" : "SavedGeometryValid"
                : "OffScreenCorrected");
    }

    private static FloatingHudPlacementResult Default(
        IReadOnlyList<DisplayWorkingArea> displays,
        FloatingHudPlacementOptions options)
    {
        var display = displays.FirstOrDefault(item => item.IsPrimary) ?? displays[0];
        var width = Math.Min(options.DefaultWidth * display.Scale, display.Bounds.Width);
        var height = Math.Min(options.DefaultHeight * display.Scale, display.Bounds.Height);
        var margin = options.Margin * display.Scale;
        return new FloatingHudPlacementResult(
            new FloatingHudGeometry(
                Math.Max(display.Bounds.X, display.Bounds.Right - width - margin),
                Math.Max(display.Bounds.Y, display.Bounds.Y + margin),
                width,
                height,
                display.Id,
                display.Dpi),
            false,
            "DefaultPlacement");
    }

    private static DisplayWorkingArea FindTarget(
        FloatingHudGeometry saved,
        IReadOnlyList<DisplayWorkingArea> displays)
    {
        var byId = displays.FirstOrDefault(display =>
            string.Equals(display.Id, saved.DisplayId, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        return displays.MaxBy(display =>
        {
            var intersection = saved.Rectangle.Intersection(display.Bounds);
            return intersection.Width * intersection.Height;
        })!;
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) < 0.01;
}

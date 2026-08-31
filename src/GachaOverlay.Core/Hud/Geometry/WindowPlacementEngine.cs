namespace GachaOverlay.Core.Hud.Geometry;

public sealed record WindowPlacementResult(
    HudWindowGeometry Geometry,
    bool WasCorrected,
    string Reason);

public sealed class WindowPlacementEngine
{
    public const double DefaultWidthDip = 520;
    public const double DefaultHeightDip = 320;
    public const double MinimumWidthDip = 240;
    public const double MinimumHeightDip = 150;
    public const double MinimumVisibleWidthDip = 96;
    public const double MinimumVisibleHeightDip = 48;
    public const double DefaultMarginDip = 32;

    public WindowPlacementResult Resolve(
        HudWindowGeometry? saved,
        IReadOnlyList<DisplayWorkingArea> displays)
    {
        var validDisplays = displays.Where(display => display.IsValid).ToArray();
        if (validDisplays.Length == 0)
        {
            return new WindowPlacementResult(
                new HudWindowGeometry(32, 32, DefaultWidthDip, DefaultHeightDip),
                true,
                "NoValidDisplayData");
        }

        if (saved is null || !saved.Rectangle.IsFiniteAndPositive)
        {
            return CreateDefault(validDisplays);
        }

        var original = saved.Rectangle;
        var target = SelectTargetDisplay(saved, original, validDisplays);
        var savedDpi = DisplayWorkingArea.NormalizeDpi(saved.Dpi);
        var targetDpi = DisplayWorkingArea.NormalizeDpi(target.Dpi);
        var dpiScale = targetDpi / savedDpi;
        var width = saved.Width * dpiScale;
        var height = saved.Height * dpiScale;
        var minimumWidth = Math.Min(MinimumWidthDip * target.Scale, target.Bounds.Width);
        var minimumHeight = Math.Min(MinimumHeightDip * target.Scale, target.Bounds.Height);

        width = Math.Clamp(width, minimumWidth, target.Bounds.Width);
        height = Math.Clamp(height, minimumHeight, target.Bounds.Height);
        var candidate = new HudRectangle(saved.X, saved.Y, width, height);

        var sizeCorrected =
            !NearlyEqual(candidate.Width, original.Width) ||
            !NearlyEqual(candidate.Height, original.Height);

        if (HasRecoverableVisibleRegion(candidate, validDisplays))
        {
            var visibleDisplay = FindBestIntersectingDisplay(candidate, validDisplays);
            return new WindowPlacementResult(
                new HudWindowGeometry(
                    candidate.X,
                    candidate.Y,
                    candidate.Width,
                    candidate.Height,
                    visibleDisplay.Id,
                    DisplayWorkingArea.NormalizeDpi(visibleDisplay.Dpi)),
                sizeCorrected,
                sizeCorrected ? "SizeOrDpiCorrected" : "SavedGeometryValid");
        }

        var minVisibleWidth = Math.Min(candidate.Width, MinimumVisibleWidthDip * target.Scale);
        var minVisibleHeight = Math.Min(candidate.Height, MinimumVisibleHeightDip * target.Scale);
        var correctedX = Math.Clamp(
            candidate.X,
            target.Bounds.X - candidate.Width + minVisibleWidth,
            target.Bounds.Right - minVisibleWidth);
        var correctedY = Math.Clamp(
            candidate.Y,
            target.Bounds.Y - candidate.Height + minVisibleHeight,
            target.Bounds.Bottom - minVisibleHeight);

        return new WindowPlacementResult(
            new HudWindowGeometry(
                correctedX,
                correctedY,
                candidate.Width,
                candidate.Height,
                target.Id,
                targetDpi),
            true,
            "OffScreenCorrected");
    }

    private static WindowPlacementResult CreateDefault(
        IReadOnlyList<DisplayWorkingArea> displays)
    {
        var display = displays.FirstOrDefault(item => item.IsPrimary) ?? displays[0];
        var width = Math.Min(DefaultWidthDip * display.Scale, display.Bounds.Width);
        var height = Math.Min(DefaultHeightDip * display.Scale, display.Bounds.Height);
        var margin = DefaultMarginDip * display.Scale;
        var x = Math.Max(display.Bounds.X, display.Bounds.Right - width - margin);
        var y = Math.Max(display.Bounds.Y, display.Bounds.Y + margin);
        return new WindowPlacementResult(
            new HudWindowGeometry(x, y, width, height, display.Id, display.Dpi),
            false,
            "DefaultPlacement");
    }

    private static DisplayWorkingArea SelectTargetDisplay(
        HudWindowGeometry saved,
        HudRectangle rectangle,
        IReadOnlyList<DisplayWorkingArea> displays)
    {
        var savedDisplay = displays.FirstOrDefault(display =>
            string.Equals(display.Id, saved.DisplayId, StringComparison.OrdinalIgnoreCase));
        if (savedDisplay is not null)
        {
            return savedDisplay;
        }

        var intersecting = FindBestIntersectingDisplay(rectangle, displays);
        if (rectangle.Intersection(intersecting.Bounds).Width > 0)
        {
            return intersecting;
        }

        return displays.MinBy(display => DistanceSquared(rectangle, display.Bounds))!;
    }

    private static DisplayWorkingArea FindBestIntersectingDisplay(
        HudRectangle rectangle,
        IReadOnlyList<DisplayWorkingArea> displays) =>
        displays.MaxBy(display =>
        {
            var intersection = rectangle.Intersection(display.Bounds);
            return intersection.Width * intersection.Height;
        })!;

    private static bool HasRecoverableVisibleRegion(
        HudRectangle rectangle,
        IReadOnlyList<DisplayWorkingArea> displays) =>
        displays.Any(display =>
        {
            var intersection = rectangle.Intersection(display.Bounds);
            return intersection.Width >= Math.Min(
                    rectangle.Width,
                    MinimumVisibleWidthDip * display.Scale) &&
                intersection.Height >= Math.Min(
                    rectangle.Height,
                    MinimumVisibleHeightDip * display.Scale);
        });

    private static double DistanceSquared(HudRectangle window, HudRectangle display)
    {
        var dx = window.CenterX < display.X
            ? display.X - window.CenterX
            : window.CenterX > display.Right
                ? window.CenterX - display.Right
                : 0;
        var dy = window.CenterY < display.Y
            ? display.Y - window.CenterY
            : window.CenterY > display.Bottom
                ? window.CenterY - display.Bottom
                : 0;
        return (dx * dx) + (dy * dy);
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) < 0.01;
}

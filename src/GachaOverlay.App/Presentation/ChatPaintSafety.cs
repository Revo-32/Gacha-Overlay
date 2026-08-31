using System.Windows;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Presentation;

internal static class ChatPaintSafety
{
    // Constant viewport gutter for the renderer's maximum 10-DIP paint overflow.
    // It must not change when outline paint settings are toggled.
    internal const double MaximumOutlineOverflow = 11;

    public static Thickness CalculateViewportPadding(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new Thickness(MaximumOutlineOverflow);
    }
}

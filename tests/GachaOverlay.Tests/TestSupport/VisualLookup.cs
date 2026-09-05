using System.Windows;
using System.Windows.Media;

namespace GachaOverlay.Tests.TestSupport;

internal static class VisualLookup
{
    // Template names have their own WPF namescope. Keep layout assertions on the actual rendered element.
    internal static object? Find(FrameworkElement root, string name)
    {
        if (root.Name == name) return root;
        if (root.FindName(name) is { } named) return named;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement child && Find(child, name) is { } match)
                return match;
        return null;
    }
}

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace GachaOverlay.App.Services;

internal static class WpfHitTestAncestry
{
    public static bool HasInteractiveAncestor(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase or
                System.Windows.Controls.Primitives.TextBoxBase or
                System.Windows.Controls.PasswordBox or
                System.Windows.Controls.ComboBox or
                System.Windows.Controls.Slider or
                System.Windows.Controls.Primitives.ScrollBar)
            {
                return true;
            }

            current = GetParent(current);
        }

        return false;
    }

    internal static DependencyObject? GetParent(DependencyObject current) => current switch
    {
        ContentElement content =>
            ContentOperations.GetParent(content) ??
            (content as FrameworkContentElement)?.Parent,
        Visual or Visual3D =>
            VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current),
        _ => LogicalTreeHelper.GetParent(current),
    };
}

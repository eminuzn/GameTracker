using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace GameTracker.Desktop;

public static class GameCardInteraction
{
    public static ListBoxItem? FindItem(DependencyObject? source)
    {
        for (var current = source; current is not null; current = Parent(current))
        {
            if (current is ButtonBase or TextBoxBase or ScrollBar) return null;
            if (current is ListBoxItem item) return item;
        }
        return null;
    }
    private static DependencyObject? Parent(DependencyObject source) =>
        (source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : null)
        ?? (source as FrameworkContentElement)?.Parent ?? LogicalTreeHelper.GetParent(source);
}

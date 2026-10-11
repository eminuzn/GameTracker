using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using GameTracker.Core;
using GameTracker.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // In-memory WPF regression tests: no user database, folders, tray or network.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = new ObservableCollection<Game>();
        var factory = new FrameworkElementFactory(typeof(LibraryGridPanel));
        var list = new ListBox { ItemsSource = source, ItemsPanel = new ItemsPanelTemplate(factory) };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Game.Name)));
        list.ItemTemplate = new DataTemplate { VisualTree = label };
        var window = new Window { Width = 880, Height = 620, Content = list, ShowInTaskbar = false };
        var assertions = 0;
        void Require(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
        void Flush()
        {
            for (var i = 0; i < 3; i++)
            {
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            }
        }
        try
        {
            window.Show(); Flush();
            var interactiveLabel = new TextBlock { Text = "Double click to play" };
            var favoriteButton = new Button { Content = new TextBlock { Text = "★" } };
            var editBox = new TextBox();
            var cardContents = new StackPanel(); cardContents.Children.Add(interactiveLabel); cardContents.Children.Add(favoriteButton); cardContents.Children.Add(editBox);
            var interactiveCard = new ListBoxItem { Content = cardContents }; interactiveCard.ApplyTemplate();
            Require(GameCardInteraction.FindItem(interactiveLabel) == interactiveCard, "Game title must resolve to the clicked card");
            Require(GameCardInteraction.FindItem((TextBlock)favoriteButton.Content) is null, "Double clicking favorite must not launch game");
            Require(GameCardInteraction.FindItem(editBox) is null, "Double clicking editable text must not launch game");
            Require(GameCardInteraction.FindItem(list) is null, "Double clicking library background must not launch game");
            var grid = Descendant<LibraryGridPanel>(list)!;
            var scroll = Descendant<ScrollViewer>(list)!;
            void CheckVisible()
            {
                var count = list.Items.Count;
                Require(Math.Abs(grid.ExtentHeight - Math.Ceiling(count / (double)grid.Columns) * grid.RowHeight) < 1, "Grid extent lost rows");
                var first = (int)Math.Floor(grid.VerticalOffset / grid.RowHeight) * grid.Columns;
                var last = Math.Min(count - 1, (int)Math.Ceiling((grid.VerticalOffset + grid.ViewportHeight) / grid.RowHeight) * grid.Columns - 1);
                for (var index = first; index <= last; index++)
                {
                    var container = list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
                    Require(container is not null && VisualTreeHelper.GetParent(container) == grid, $"Visible card {index} detached at offset {grid.VerticalOffset}");
                    Require(ReferenceEquals(container!.DataContext, list.Items[index]), $"Visible card {index} has stale game binding");
                    Require(Math.Abs(container.TranslatePoint(new Point(), grid).Y - (index / grid.Columns * grid.RowHeight - grid.VerticalOffset)) < 1, $"Visible card {index} in wrong row");
                }
                Require(VisualTreeHelper.GetChildrenCount(grid) <= (Math.Ceiling(grid.ViewportHeight / grid.RowHeight) + 4) * grid.Columns, "Grid stopped virtualizing");
            }
            foreach (var count in new[] { 7, 13, 123 })
            {
                source.Clear();
                for (var i = 0; i < count; i++) source.Add(new Game { Name = $"Demo {i:D3}", SizeBytes = 500_000_000, LaunchExe = i % 2 == 0 ? "demo.exe" : null });
                foreach (var columns in new[] { 3, 4 })
                {
                    grid.Columns = columns; Flush();
                    for (var cycle = 0; cycle < 12; cycle++)
                    {
                        scroll.ScrollToEnd(); Flush(); CheckVisible();
                        scroll.ScrollToTop(); Flush(); CheckVisible();
                    }
                    // Stepped wheel/page movement overlaps realized rows, unlike end/top jumps.
                    for (var cycle = 0; cycle < 24; cycle++)
                    {
                        grid.SetVerticalOffset(cycle * 176); Flush(); CheckVisible();
                    }
                    for (var cycle = 23; cycle >= 0; cycle--)
                    {
                        grid.SetVerticalOffset(cycle * 176); Flush(); CheckVisible();
                    }
                    scroll.ScrollToEnd(); Flush();
                    var view = (ListCollectionView)CollectionViewSource.GetDefaultView(source);
                    view.Filter = item => ((Game)item).NeedsExeSelection; Flush(); CheckVisible();
                    view.Filter = _ => false; Flush(); CheckVisible();
                    Require(VisualTreeHelper.GetChildrenCount(grid) == 0 && grid.VerticalOffset == 0, "Empty filter retained stale cards/offset");
                    view.Filter = null; Flush(); CheckVisible();
                    source.Add(new Game { Name = "Added during gallery view", SizeBytes = 500_000_000 }); Flush();
                    scroll.ScrollToEnd(); Flush(); CheckVisible(); source.RemoveAt(0); Flush(); CheckVisible();
                    window.Height = 380; Flush(); CheckVisible(); window.Height = 620; Flush(); CheckVisible();
                    Console.WriteLine($"PASS grid: {count} games, {columns} columns, repeated end/top and overlapping scrolling, filters, collection changes and resize");
                }
            }
            Console.WriteLine($"{assertions} desktop assertions passed across 6 grid scenarios and card interactions.");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static T? Descendant<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Descendant<T>(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
        return null;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace GameTracker.Desktop;

// A fixed-column gallery with pixel scrolling and recycled item containers.
// Only visible rows plus one row on either side are measured/rendered.
public sealed class LibraryGridPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(LibraryGridPanel),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure), value => value is int columns && columns > 0);
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(nameof(RowHeight), typeof(double), typeof(LibraryGridPanel),
        new FrameworkPropertyMetadata(400d, FrameworkPropertyMetadataOptions.AffectsMeasure), value => value is double height && double.IsFinite(height) && height > 0);
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth { get; private set; }
    public double ExtentHeight { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public double HorizontalOffset => 0;
    public double VerticalOffset { get; private set; }
    public ScrollViewer? ScrollOwner { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return new();
        ViewportWidth = double.IsFinite(availableSize.Width) ? availableSize.Width : Math.Max(0, ScrollOwner?.ActualWidth ?? ActualWidth);
        ViewportHeight = double.IsFinite(availableSize.Height) ? availableSize.Height : Math.Max(RowHeight, ScrollOwner?.ActualHeight ?? ActualHeight);
        ExtentWidth = ViewportWidth; ExtentHeight = Math.Ceiling(owner.Items.Count / (double)Columns) * RowHeight;
        VerticalOffset = Math.Clamp(VerticalOffset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        ScrollOwner?.InvalidateScrollInfo();
        var width = ViewportWidth / Columns;
        var first = Math.Max(0, ((int)(VerticalOffset / RowHeight) - 1) * Columns);
        var last = Math.Min(owner.Items.Count - 1, ((int)Math.Ceiling((VerticalOffset + ViewportHeight) / RowHeight) + 1) * Columns - 1);
        // Accessing the children initializes the panel's item generator.
        _ = InternalChildren;
        var generator = ItemContainerGenerator;
        var position = generator.GeneratorPositionFromIndex(first);
        var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
        if (last >= first)
        {
            using (generator.StartAt(position, GeneratorDirection.Forward, true))
                for (var index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var isNew);
                    if (isNew)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(width, RowHeight));
                }
        }
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var itemPosition = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(itemPosition);
            if (index >= first && index <= last) continue;
            if (generator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(itemPosition, 1); else generator.Remove(itemPosition, 1);
            RemoveInternalChildRange(i, 1);
        }
        return new Size(ViewportWidth, Math.Min(ViewportHeight, ExtentHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width / Columns;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= 0) InternalChildren[i].Arrange(new Rect(index % Columns * width, index / Columns * RowHeight - VerticalOffset, width, RowHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        ItemContainerGenerator.RemoveAll();
        RemoveInternalChildRange(0, InternalChildren.Count);
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= ItemsControl.GetItemsOwner(this)?.Items.Count) return;
        var rowTop = index / Columns * RowHeight;
        if (rowTop < VerticalOffset) SetVerticalOffset(rowTop);
        else if (rowTop + RowHeight > VerticalOffset + ViewportHeight) SetVerticalOffset(RowHeight > ViewportHeight ? rowTop : rowTop + RowHeight - ViewportHeight);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? child = visual;
        while (child is not null && VisualTreeHelper.GetParent(child) != this) child = VisualTreeHelper.GetParent(child);
        if (child is not UIElement element) return Rect.Empty;
        var childIndex = InternalChildren.IndexOf(element);
        if (childIndex < 0) return Rect.Empty;
        var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        if (index < 0) return Rect.Empty;
        BringIndexIntoView(index);
        return new Rect(index % Columns * ViewportWidth / Columns, index / Columns * RowHeight - VerticalOffset, ViewportWidth / Columns, Math.Min(RowHeight, ViewportHeight));
    }

    public void SetVerticalOffset(double offset)
    {
        // ScrollViewer.ScrollToEnd/ScrollToTop use infinite offsets.
        if (double.IsNaN(offset)) return;
        offset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (Math.Abs(offset - VerticalOffset) < 0.1) return;
        VerticalOffset = offset; InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo();
    }
    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset(VerticalOffset - 32);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 32);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 96);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 96);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
}

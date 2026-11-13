using System.Windows;
using System.Windows.Controls;

namespace BrimDeck;

// A list of the clipboard and file transfer page that builds and lays out only the rows on screen and a few either side
// (VirtualizingStackPanel), and builds the others as they scroll into view. A page with many entries laid out every row again at
// each change of its width, which made resizing the panel stutter; this keeps that work to the rows that show.
// The rows are made by the page (build) and handed back when they leave (release). It scrolls by pixels, as the whole list did.
internal sealed class ShelfRowList : ItemsControl
{
    private readonly Func<object, FrameworkElement> _build;
    private readonly Action<object, FrameworkElement>? _release;
    public ScrollViewer Viewer { get; }

    public ShelfRowList(Func<object, FrameworkElement> build, Action<object, FrameworkElement>? release = null)
    {
        _build = build; _release = release;
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var viewer = new FrameworkElementFactory(typeof(ScrollViewer), "PART_Scroll");
        viewer.SetValue(ScrollViewer.CanContentScrollProperty, true);
        viewer.SetValue(FocusableProperty, false);
        viewer.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        viewer.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        viewer.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        Template = new ControlTemplate(typeof(ShelfRowList)) { VisualTree = viewer };
        Focusable = false;
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Standard);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        // A few rows beyond each end of the view, so a wheel step finds them built.
        VirtualizingPanel.SetCacheLengthUnit(this, VirtualizationCacheLengthUnit.Item);
        VirtualizingPanel.SetCacheLength(this, new VirtualizationCacheLength(4, 4));
        ApplyTemplate();
        Viewer = (ScrollViewer)Template.FindName("PART_Scroll", this);
    }

    protected override bool IsItemItsOwnContainerOverride(object item) => false;
    protected override DependencyObject GetContainerForItemOverride() => new ContentPresenter();
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ContentPresenter presenter) presenter.Content = _build(item);
    }
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is not ContentPresenter presenter) return;
        if (presenter.Content is FrameworkElement row) _release?.Invoke(item, row);
        presenter.Content = null;
    }
}

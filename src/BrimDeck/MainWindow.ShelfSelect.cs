using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Choosing several entries of a column at once, as icons are chosen on the desktop. Ctrl with a click adds or takes one, Shift
// with a click takes all from the last one chosen to this one, and a drag that starts on an empty part of the list draws a
// frame that chooses what it touches. The right button on one of the chosen then acts on all of them (ClipMenu, FileMenu): into
// or out of a category, deleted or removed. What is chosen is in one column at a time (_picked): choosing in the other lets go
// of it. A click on an entry by itself still pastes it and lets go of the choice; a click on an empty part only lets go.
public partial class MainWindow
{
    private Guid? _clipAnchor;
    private bool _banding;

    // How a chosen entry looks. Its fill is the theme's fill for a range chosen (the accent, thin), the color of the frame that
    // chooses, and not the grey of an entry under the pointer, which several chosen rows would otherwise look like. In a list,
    // chosen rows that stand one under the other are one block: the corners between them are square, and only the block's own
    // corners are round. Tiles and blocks keep their gaps and their corners.
    private Brush PickedFill => UI.Brush(Look.Hex("rangeFill"));
    private CornerRadius RowCorners(bool above, bool below)
    {
        double round = Look.Corner(6);
        return new CornerRadius(above ? 0 : round, above ? 0 : round, below ? 0 : round, below ? 0 : round);
    }
    // order: the list's entries from top to bottom.
    private void JoinPicked(Border row, Guid id, IEnumerable<Guid> order)
    {
        if (!_picked.Contains(id)) return;
        Guid? before = null; bool above = false, below = false, found = false;
        foreach (var other in order)
        {
            if (found) { below = _picked.Contains(other); break; }
            if (other == id) { found = true; above = before is { } last && _picked.Contains(last); }
            before = other;
        }
        row.CornerRadius = RowCorners(above, below);
    }

    // Keeps what is chosen to the side given.
    private void PickOnly(ShelfSide side)
    {
        if (_picked.Count == 0) return;
        var other = side == ShelfSide.Clipboard ? ShelfData.Files.Select(file => file.Id).ToHashSet() : ShelfData.Clips.Select(clip => clip.Id).ToHashSet();
        _picked.RemoveWhere(other.Contains);
    }
    private void ClickClip(ClipEntry clip)
    {
        bool ctrl = PasteInput.KeyDown(0x11), shift = PasteInput.KeyDown(0x10);
        if (ctrl || shift)
        {
            // As the files (ClickFile).
            PickOnly(ShelfSide.Clipboard);
            var visible = VisibleClips();
            if (shift && _clipAnchor is { } anchor && visible.FindIndex(c => c.Id == anchor) is var from and >= 0)
            {
                int to = visible.FindIndex(c => c.Id == clip.Id);
                if (!ctrl) _picked.Clear();
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) _picked.Add(visible[i].Id);
            }
            else if (!_picked.Remove(clip.Id)) { _picked.Add(clip.Id); _clipAnchor = clip.Id; }
            RenderShelf();
            return;
        }
        if (_picked.Count > 0) { _picked.Clear(); RenderShelf(); }
        _clipAnchor = clip.Id;
        PasteClip(clip);
    }
    // The menu of several chosen clipboard entries: into or out of "常用" and the user's categories, and deleted, all at once.
    private ContextMenu ClipsMenu(IReadOnlyList<ClipEntry> clips)
    {
        var menu = NewMenu();
        var ids = clips.Select(clip => clip.Id).ToList();
        bool favorite = clips.All(clip => clip.Categories.Contains(ShelfData.Favorites));
        menu.Items.Add(Item(favorite ? Loc.T("移出常用", "Remove from favorites") : Loc.T("加入常用", "Add to favorites"),
            () => SetCategory(ShelfSide.Clipboard, ids, ShelfData.Favorites, add: !favorite), StarData));
        if (FilingMenu(ShelfSide.Clipboard, ids) is { } filing) menu.Items.Add(filing);
        menu.Items.Add(Separator());
        menu.Items.Add(Item(Loc.T($"删除 {clips.Count} 条", $"Delete {clips.Count} entries"), () => DeleteClips(clips), MenuIcons.Delete, danger: true));
        return menu;
    }

    // The list with the ground a frame is drawn on. A press that no entry and no scroll bar takes (they mark it handled) starts
    // here: moved, it draws the frame, and the entries on screen that the frame touches are chosen as it grows and shrinks, with
    // Ctrl or Shift in addition to what was chosen before; let go without moving, it lets go of the choice. The frame takes in
    // what is on screen: the list does not scroll under it.
    private FrameworkElement Banded(FrameworkElement list, ShelfSide side)
    {
        var accent = Look.ColorOf("accent");
        var band = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(34, accent.R, accent.G, accent.B)), Stroke = new SolidColorBrush(Color.FromArgb(190, accent.R, accent.G, accent.B)), StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, IsHitTestVisible = false, SnapsToDevicePixels = true
        };
        var host = new Grid { Background = Brushes.Transparent };
        host.Children.Add(list); host.Children.Add(band);
        Point? start = null; bool additive = false; HashSet<Guid> kept = [], before = [];
        // The list's entries in order, where its rows stand one under the other and join when chosen (JoinPicked).
        List<Guid>? order = null;
        IEnumerable<(Guid Id, Border Frame)> Shown() => _shelfRows.Where(row => row.Value.Row.IsVisible && host.IsAncestorOf(row.Value.Row)).Select(row => (row.Key, row.Value.Row));
        host.MouseLeftButtonDown += (_, e) =>
        {
            if (_shelfEdit is not null) return;
            start = e.GetPosition(host);
            additive = PasteInput.KeyDown(0x11) || PasteInput.KeyDown(0x10);
            PickOnly(side);
            before = [.. _picked];
            kept = additive ? [.. _picked] : [];
            order = side == ShelfSide.Clipboard
                ? _shelfLayout.ClipView != ClipView.Blocks ? [.. VisibleClips().Select(clip => clip.Id)] : null
                : _shelfLayout.FileView is FileView.Single or FileView.Two ? [.. VisibleFiles().Select(file => file.Id)] : null;
            host.CaptureMouse();
            e.Handled = true;
        };
        host.MouseMove += (_, e) =>
        {
            if (start is not { } from || !host.IsMouseCaptured) return;
            var now = e.GetPosition(host);
            if (!_banding && Math.Abs(now.X - from.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _banding = true;
            var area = new Rect(from, now);
            area.Intersect(new Rect(0, 0, host.ActualWidth, host.ActualHeight));
            if (area.IsEmpty) return;
            band.Margin = new Thickness(area.X, area.Y, 0, 0); band.Width = area.Width; band.Height = area.Height; band.Visibility = Visibility.Visible;
            var chosen = new HashSet<Guid>(kept);
            foreach (var (id, frame) in Shown())
                if (frame.TransformToAncestor(host).TransformBounds(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight)).IntersectsWith(area)) chosen.Add(id);
            if (chosen.SetEquals(_picked)) return;
            _picked.Clear(); _picked.UnionWith(chosen);
            // The rows are painted as they stand; they are built again, with their own state, when the frame is let go.
            foreach (var (id, frame) in Shown())
            {
                bool picked = _picked.Contains(id);
                frame.Background = picked ? PickedFill : frame.Tag as Brush ?? Brushes.Transparent;
                if (order is null) continue;
                int at = picked ? order.IndexOf(id) : -1;
                frame.CornerRadius = RowCorners(at > 0 && _picked.Contains(order[at - 1]), at >= 0 && at < order.Count - 1 && _picked.Contains(order[at + 1]));
            }
        };
        host.MouseLeftButtonUp += (_, e) =>
        {
            if (start is null) return;
            e.Handled = true;
            bool drawn = _banding;
            start = null; _banding = false; band.Visibility = Visibility.Collapsed;
            host.ReleaseMouseCapture();
            if (!drawn && !additive) _picked.Clear();
            if (!before.SetEquals(_picked)) RenderShelf();
            else if (_expanded && !_pointerInside && !ShelfHolds) PointerChanged(false);
        };
        host.LostMouseCapture += (_, _) => { start = null; _banding = false; band.Visibility = Visibility.Collapsed; };
        return host;
    }
}

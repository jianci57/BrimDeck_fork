using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Native;
using Path = System.Windows.Shapes.Path;

namespace BrimDeck;

// The other ways the clipboard and file transfer page shows its entries, chosen for each column in the settings (ClipView,
// FileView; the sizes are ShelfLayout's). The clipboard: three lines high, with what the entry is and when it was copied at its
// right; or blocks in columns. The file transfer: two lines; or a large icon over the name, in a grid, in one row that scrolls
// sideways, or in one column. Every way keeps the one-line row's behaviour (AttachRow): a click pastes, a drag takes the entry
// out, the right button opens its menu, and under the pointer the line that holds the least gives way to the entry's buttons.
public partial class MainWindow
{
    // ---- The lists ----
    private void ForgetRow(Guid id, FrameworkElement row)
    {
        if (_shelfRows.TryGetValue(id, out var kept) && (kept.Row == row || kept.Row.Parent == row)) _shelfRows.Remove(id);
        if (_rowRules.TryGetValue(id, out var rule) && row.IsAncestorOf(rule)) _rowRules.Remove(id);
    }
    private FrameworkElement ClipList(List<ClipEntry> clips, ShelfLayout layout)
    {
        ShelfRowList list;
        if (layout.ClipView == ClipView.Blocks)
            list = new ShelfRowList(item => BlockRow((ClipEntry[])item, layout), (item, row) => { foreach (var clip in (ClipEntry[])item) ForgetRow(clip.Id, row); })
            { ItemsSource = clips.Chunk(layout.ClipColumns).ToList(), Margin = new Thickness(-8, 0, 0, 0) };
        else
        {
            // Only the rows in view are built (ShelfRowList); a row reaches from the list's left edge, its text 8 DIP in.
            list = new ShelfRowList(
                item =>
                {
                    var clip = (ClipEntry)item;
                    int at = clips.IndexOf(clip);
                    var row = layout.ClipView == ClipView.Three
                        ? ClipRowThree(clip, layout, at > 0 ? clips[at - 1].Id : null, at >= 0 && at < clips.Count - 1 ? clips[at + 1].Id : null) : ClipRow(clip, layout);
                    row.Margin = new Thickness(0);
                    JoinPicked(row, clip.Id, clips.Select(entry => entry.Id));
                    return row;
                },
                (item, row) => ForgetRow(((ClipEntry)item).Id, row))
            { ItemsSource = clips, Margin = new Thickness(-8, 0, 0, 0) };
        }
        _ = new WheelGlide(list.Viewer, WheelStep(layout.ClipView switch { ClipView.Blocks => layout.BlockHeight + layout.BlockGap, ClipView.Three => layout.ThreeRow, _ => layout.Row }));
        Fade(PassWheel(list.Viewer), ShelfSide.Clipboard, _clipOffset);
        return list;
    }
    // As the clipboard's list: only the rows in view are built. What is picked is kept by the page (_picked), so a row built as it
    // scrolls into view shows it.
    private FrameworkElement FileList(List<TransferFile> files, ShelfLayout layout)
    {
        var view = layout.FileView;
        // The icons are asked for at once, in the list's order (ShelfFiles reads them on a thread of its own), so that most are
        // there by the time their files scroll into view.
        int pixels = LargePixels(layout);
        foreach (var file in files.Take(300)) { if (view == FileView.Single) ShelfFiles.Icon(file.Path); else ShelfFiles.Large(file.Path, pixels); }
        if (view == FileView.Row)
        {
            // The row starts at the column's edge, and every file in it is the same width: the list reckons its length and where
            // it stands from the files it has built (ShelfRowList), and one wider than the others (the first once stood 8 DIP in
            // from a list that began left of the column) threw that reckoning off as the row scrolled.
            var row = new ShelfRowList(
                item => FileTile((TransferFile)item, layout, layout.TileAcross, across: true),
                (item, tile) => ForgetRow(((TransferFile)item).Id, tile), across: true)
            { ItemsSource = files };
            return AcrossList(row.Viewer, row, layout.TileAcross);
        }
        ShelfRowList list;
        if (view == FileView.Grid)
            list = new ShelfRowList(item => TileRow((TransferFile[])item, layout), (item, row) => { foreach (var file in (TransferFile[])item) ForgetRow(file.Id, row); })
            { ItemsSource = files.Chunk(layout.FileColumns).ToList(), Margin = new Thickness(-8, 0, 0, 0) };
        else
            list = new ShelfRowList(
                item =>
                {
                    var file = (TransferFile)item;
                    if (view == FileView.Column) { var tile = FileTile(file, layout, layout.FileColumn, across: false); tile.Margin = new Thickness(9, 1, 1, 1); return tile; }
                    var row = view == FileView.Two ? FileRowTwo(file, layout) : FileRow(file, layout);
                    row.Margin = new Thickness(0);
                    JoinPicked(row, file.Id, files.Select(entry => entry.Id));
                    return row;
                },
                (item, row) => ForgetRow(((TransferFile)item).Id, row))
            { ItemsSource = files, Margin = new Thickness(-8, 0, 0, 0) };
        _ = new WheelGlide(list.Viewer, WheelStep(view switch { FileView.Grid or FileView.Column => layout.TileHeight, FileView.Two => layout.TwoRow, _ => layout.Row }));
        Fade(PassWheel(list.Viewer), ShelfSide.Files, _fileOffset);
        return list;
    }

    // What a step of the wheel moves a list by (WheelGlide): one entry where entries are tall, two of the low rows.
    private static double WheelStep(double entry) => entry >= 56 ? entry : 2 * entry;

    // ---- The buttons under the pointer ----
    // A button of a row: its icon (MenuIcons), what it does, said when the pointer rests on it, and the action.
    // Solid: the icon is filled, for a state that is on ("常用" on an entry that is in it).
    private readonly record struct RowDeed(string Icon, string Note, Action Act, bool Solid = false);
    // A file's buttons before "›": open it, show it in its folder, take it off the list. `room` is how many buttons the place has
    // room for, "›" among them: short of it "在文件夹中显示" goes first, then "打开". A file that is gone can only be taken off.
    private List<RowDeed> FileDeeds(TransferFile file, bool exists, int room)
    {
        var deeds = new List<RowDeed>();
        if (exists && room >= 3) deeds.Add(new(MenuIcons.Open, Loc.T("打开", "Open"), () => OpenFile(file.Path)));
        if (exists && room >= 4) deeds.Add(new(MenuIcons.Folder, Loc.T("在文件夹中显示", "Show in folder"), () => ShowInFolder(file.Path)));
        deeds.Add(new(BinIcon, Loc.T("从文件中转中移除", "Remove from the file transfer"), () => RemoveFiles([file])));
        return deeds;
    }
    // A clipboard entry's buttons before "›": into or out of "常用" (the star, filled while the entry is in it), and, for
    // anything but a picture, "粘贴为纯文本".
    private List<RowDeed> ClipDeeds(ClipEntry clip)
    {
        bool favorite = clip.Categories.Contains(ShelfData.Favorites);
        var deeds = new List<RowDeed>
        {
            new(StarData, favorite ? Loc.T("移出常用", "Remove from favorites") : Loc.T("加入常用", "Add to favorites"),
                () => SetCategory(ShelfSide.Clipboard, [clip.Id], ShelfData.Favorites, add: !favorite), favorite)
        };
        if (clip.Kind != ClipKind.Image) deeds.Add(new(MenuIcons.PlainText, Loc.T("粘贴为纯文本", "Paste as plain text"), () => PasteClip(clip, plain: true)));
        return deeds;
    }
    // The buttons in a line: small faces side by side, "›" (the details) last. Where they stand in a line of text they are only
    // as tall as that line, their icons 12 DIP; where they have the row's height (ClipView.Three) they are larger (icon).
    private StackPanel DeedStrip(IReadOnlyList<RowDeed> deeds, Action detail, double width, double height, double icon = 12)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var deed in deeds)
        {
            // The menu's 16 unit drawing at the icon's size.
            var glyph = MenuGlyph(deed.Icon, Look.Secondary);
            glyph.Margin = new Thickness(0); glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.LayoutTransform = new ScaleTransform(icon / 16, icon / 16);
            if (deed.Solid) glyph.Fill = glyph.Stroke;
            var button = DeedButton(glyph, width, height, deed.Act);
            HoverTip(button, deed.Note);
            AutomationProperties.SetName(button, deed.Note);
            // Out of the layout until the pointer is on the entry (ShowMore), so that the line the buttons stand in is no wider
            // at rest than what it shows then.
            button.Visibility = Visibility.Collapsed;
            strip.Children.Add(button);
        }
        var chevron = SettingsWindow.Chevron(); chevron.FontSize = icon > 12 ? 11 : 10; chevron.Margin = new Thickness(0); chevron.Foreground = UI.Brush(Look.Secondary);
        chevron.HorizontalAlignment = HorizontalAlignment.Center; chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.RenderTransformOrigin = new Point(.5, .5); chevron.RenderTransform = new RotateTransform(-90);
        var open = DeedButton(chevron, width, height, detail);
        AutomationProperties.SetName(open, Loc.T("查看详情", "Details"));
        strip.Children.Add(open);
        return strip;
    }
    private Border DeedButton(FrameworkElement icon, double width, double height, Action act)
    {
        var button = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(Look.Corner(4)), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = icon };
        void Paint(string color) { if (icon is Shape shape) { shape.Stroke = UI.Brush(color); if (shape.Fill is not null) shape.Fill = shape.Stroke; } else if (icon is TextBlock text) text.Foreground = UI.Brush(color); }
        button.MouseEnter += (_, _) => { button.Background = UI.Brush(Look.Hex("pressed")); Paint(Look.Primary); };
        button.MouseLeave += (_, _) => { button.Background = Brushes.Transparent; Paint(Look.Secondary); };
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        return button;
    }
    // The line that gives way to the buttons: its text (the size, the time, "已不存在") and, in its place under the pointer, the
    // buttons, at the side given. It is the row's end as the one-line row has one (RowEnd), so the row lights, flashes "已粘贴"
    // and takes a folder's figures as that row does.
    private static Grid DeedLine(TextBlock label, StackPanel strip, double height, HorizontalAlignment side)
    {
        var more = new Border { Child = strip, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
        var line = new Grid { Height = height };
        label.HorizontalAlignment = side;
        line.Children.Add(label); line.Children.Add(more);
        line.Tag = new RowEnd(label, more);
        return line;
    }

    // ---- A file under a large icon (FileView.Grid, Row and Column) ----
    // The icon, at the size ShelfLayout gives the page's step, and under it the name on two lines, cut at the end of its stem with
    // the extension kept (Shelf.TileName). The second line gives way: to the buttons under the pointer, to "已粘贴" after a paste,
    // and for good to "已不存在" when the file is gone; the name then stands on the first line alone. `width` is the cell's width,
    // which says how many buttons it has room for; in the one row it is also the tile's own width.
    private Border FileTile(TransferFile file, ShelfLayout layout, double width, bool across)
    {
        bool exists = ShelfFiles.Exists(file.Path), picked = _picked.Contains(file.Id);
        double line = layout.TileLine, size = layout.TileText;
        var frame = new Border
        {
            // The tile stands 1 DIP inside its cell on every side: lit or chosen, tiles next to each other keep a gap between them
            // rather than running together.
            // In the one row the tiles stand in the middle of the list's height.
            Height = layout.TileHeight - 2, Margin = new Thickness(1), CornerRadius = new CornerRadius(Look.Corner(6)), Cursor = Cursors.Hand,
            VerticalAlignment = across ? VerticalAlignment.Center : VerticalAlignment.Top,
            Background = picked ? PickedFill : Brushes.Transparent
        };
        if (across) frame.Width = width - 2;
        var (icon, picture) = LargeIcon(file, layout, exists);
        icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, layout.TileTop - 1, 0, 0);

        TextBlock Line() => new()
        {
            FontFamily = UI.PanelFont, FontSize = size, Foreground = UI.Brush(Look.Primary), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.NoWrap,
            Height = line, LineHeight = line, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Opacity = exists ? 1 : .45
        };
        var first = Line(); var second = Line();
        // The whole name is said on the name alone, when it is cut: not over the icon, nor over the buttons, which say what they do.
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
        names.Children.Add(first); names.Children.Add(second);
        var label = Label(exists ? "" : Loc.T("已不存在", "Missing"), layout.Aux, exists ? Look.Tertiary : WarnColor, HorizontalAlignment.Center);
        label.TextAlignment = TextAlignment.Center;
        // As many buttons as the name's width holds at 16 DIP each, four at most, and each no wider than 18 (16 on the compact
        // page): together they are never wider than the name's lines.
        double room = width - 6;
        int count = (int)Math.Clamp(Math.Floor(room / 16), 2, 4);
        double each = Math.Min(layout.Tier == ShelfTier.Compact ? 16 : 18, Math.Floor(room / count));
        var slot = DeedLine(label, DeedStrip(FileDeeds(file, exists, count), () => OpenDetail(ShelfSide.Files, file.Id), each, line), line, HorizontalAlignment.Center);
        var end = (RowEnd)slot.Tag;
        slot.VerticalAlignment = VerticalAlignment.Bottom;
        var text = new Grid { Height = 2 * line, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, layout.TileTop - 1 + layout.TileIcon + layout.TileGap, 2, 0) };
        text.Children.Add(names); text.Children.Add(slot);

        // The name is laid out for the width the tile has, and again when that changes or the second line is taken.
        int lines = exists ? 2 : 1; double laid = -1;
        bool cut = false;
        WholeNameTip(names, () => cut ? file.Name : null);
        void Lay()
        {
            if (laid <= 0) return;
            var split = Shelf.TileName(file.Name, laid - 1, lines, value => TextWidth(value, size, FontWeights.Normal));
            first.Inlines.Clear(); second.Inlines.Clear();
            AddHighlighted(first.Inlines, split.Two ? split.First : split.First + split.Extension, Look.Primary);
            if (split.Two) AddHighlighted(second.Inlines, split.Second + split.Extension, Look.Primary);
            second.Visibility = split.Two ? Visibility.Visible : Visibility.Collapsed;
            cut = split.Cut;
        }
        text.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - laid) > .5) { laid = e.NewSize.Width; Lay(); } };
        end.Narrow = narrow => { int now = narrow || !exists ? 1 : 2; if (now != lines) { lines = now; Lay(); } };
        end.Refresh = () => picture.Source = LargeSource(file, layout, exists);

        // The name and the buttons ask for no width of their own (Unsized): the tile is as wide as its cell, whatever they hold.
        // Buttons a little wider than the cell once made every cell of the grid's row wider while the pointer was on one file,
        // and the other files of the row moved aside.
        var body = new Grid();
        body.Children.Add(icon); body.Children.Add(new Unsized { Child = text });
        frame.Child = body;
        AutomationProperties.SetName(frame, file.Name);
        _shelfRows[file.Id] = (frame, slot);
        AttachFile(frame, slot, file, exists);
        return frame;
    }
    // Shows its child in the width it is given and asks for none itself.
    private sealed class Unsized : Decorator
    {
        protected override Size MeasureOverride(Size constraint)
        {
            if (Child is null) return default;
            Child.Measure(constraint);
            return new Size(0, Child.DesiredSize.Height);
        }
    }
    // The grid's row: its files in as many equal cells as the grid has columns; the last row's stand at the left.
    private FrameworkElement TileRow(TransferFile[] files, ShelfLayout layout)
    {
        var row = new UniformGrid { Rows = 1, Columns = layout.FileColumns, Margin = new Thickness(8, 0, 0, 0) };
        double width = layout.FileColumn / Math.Max(1, layout.FileColumns);
        foreach (var file in files) row.Children.Add(FileTile(file, layout, width, across: false));
        return row;
    }
    // The file's picture at the tile's size: a picture file's own, read in the background (ShelfFiles.Large, which says when it
    // has arrived: FolderFiguresChanged), else the Explorer's icon.
    private ImageSource? LargeSource(TransferFile file, ShelfLayout layout, bool exists) =>
        (exists ? ShelfFiles.Large(file.Path, LargePixels(layout)) : null) ?? ShelfFiles.Icon(file.Path);
    private int LargePixels(ShelfLayout layout) => (int)Math.Ceiling(layout.TileIcon * VisualTreeHelper.GetDpi(this).DpiScaleX * Math.Max(1, _scale));
    // The picture in a square box of the icon's size, in its middle: a picture file's own picture is seldom square, and would
    // otherwise stand at the box's top or left.
    private (Grid Box, Image Picture) LargeIcon(TransferFile file, ShelfLayout layout, bool exists)
    {
        var picture = new Image { Source = LargeSource(file, layout, exists), Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = exists ? 1 : .45 };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        return (new Grid { Width = layout.TileIcon, Height = layout.TileIcon, Children = { picture } }, picture);
    }
    // The whole of a name that is shown cut: said after a moment while the pointer rests on the name, and gone the moment the
    // pointer leaves the name. WPF's own note stays open while the pointer moves toward it; an entry's buttons lie that way, so
    // the name stayed up over them and their own notes stood on top of it.
    private static void WholeNameTip(FrameworkElement owner, Func<string?> whole)
    {
        var tip = new ToolTip { PlacementTarget = owner, Placement = PlacementMode.Mouse };
        var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            if (!owner.IsMouseOver || whole() is not { } name) return;
            tip.Content = name; tip.IsOpen = true;
        };
        owner.MouseEnter += (_, _) => { wait.Stop(); wait.Start(); };
        owner.MouseLeave += (_, _) => { wait.Stop(); tip.IsOpen = false; };
        owner.Unloaded += (_, _) => { wait.Stop(); tip.IsOpen = false; };
    }
    // What every way of showing a file shares with the one-line row: the click, the menu, dragging out, and the flash after a paste.
    private void AttachFile(Border frame, Grid aux, TransferFile file, bool exists)
    {
        AttachRow(frame, aux, ShelfSide.Files, file.Id, picked: _picked.Contains(file.Id),
            click: () => ClickFile(file),
            menu: () => FileMenu(file),
            drag: null);
        AttachFileDrag(frame, () => _picked.Contains(file.Id) && _picked.Count > 1 ? ShelfData.Files.Where(f => _picked.Contains(f.Id) && ShelfFiles.Exists(f.Path)).ToList() : exists ? [file] : []);
        if (_flash is { } flash && flash.Id == file.Id && flash.Until > Environment.TickCount64) ShowFlash(frame, aux, flash.Text);
    }

    // ---- A file on two lines (FileView.Two) ----
    // The large icon at the left; the name, and under it the size and the date it was changed. Under the pointer the buttons
    // show at the row's right end, as on the one-line row, and the two lines stay as they are.
    private Border FileRowTwo(TransferFile file, ShelfLayout layout)
    {
        bool exists = ShelfFiles.Exists(file.Path), folder = exists && ShelfFiles.IsFolder(file.Path);
        var frame = RowFrame(layout, layout.TwoRow, _picked.Contains(file.Id));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.TileIcon + 10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var (icon, picture) = LargeIcon(file, layout, exists);
        icon.HorizontalAlignment = HorizontalAlignment.Left; icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        string Facts()
        {
            if (!exists) return Loc.T("已不存在", "Missing");
            string facts = folder ? FolderFacts(file.Path) : ShelfFiles.FileSize(file.Path) is { } bytes ? Shelf.Size(bytes) : "";
            if (ShelfFiles.Modified(file.Path) is { } modified) facts += (facts.Length > 0 ? " · " : "") + modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return facts;
        }
        var (stemText, extension) = Shelf.SplitName(file.Name);
        var stem = Label(stemText, layout.Body, Look.Primary, HorizontalAlignment.Left);
        var ext = Label(extension, layout.Body, Look.Primary, HorizontalAlignment.Left);
        if (Searching)
        {
            stem.Inlines.Clear(); AddHighlighted(stem.Inlines, stemText, Look.Primary);
            ext.Inlines.Clear(); AddHighlighted(ext.Inlines, extension, Look.Primary);
        }
        var name = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left, Opacity = exists ? 1 : .45 };
        DockPanel.SetDock(ext, System.Windows.Controls.Dock.Right);
        name.Children.Add(ext); name.Children.Add(stem);
        // The row's end keeps room for one button (AuxSlot), as the one-line row's does.
        double room = Math.Max(20, layout.FileColumn - layout.TileIcon - 10 - 34);
        if (TextWidth(file.Name, layout.Body, FontWeights.Normal) > room) WholeNameTip(name, () => file.Name);
        var facts = Label(Facts(), layout.Aux, exists ? Look.Tertiary : WarnColor, HorizontalAlignment.Left);
        Typography.SetNumeralAlignment(facts, FontNumeralAlignment.Tabular);
        // The row's end as the one-line row has it: empty at rest, the buttons under the pointer, "已粘贴" after a paste. In a
        // column too narrow for all four buttons beside a name, "在文件夹中显示" goes first.
        var aux = AuxSlot("", layout, () => OpenDetail(ShelfSide.Files, file.Id), extras: FileDeeds(file, exists, layout.FileColumn >= 200 ? 4 : 3));
        // A folder's figures arrive after the row is built (FolderFiguresChanged), a picture's own picture likewise.
        ((RowEnd)aux.Tag).Refresh = () =>
        {
            picture.Source = LargeSource(file, layout, exists);
            if (folder) facts.Text = Facts();
        };
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(name); lines.Children.Add(facts);
        Grid.SetColumn(lines, 1); grid.Children.Add(lines);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(aux, 2); grid.Children.Add(aux);
        frame.Child = grid;
        AutomationProperties.SetName(frame, file.Name);
        _shelfRows[file.Id] = (frame, aux);
        AttachFile(frame, aux, file, exists);
        return frame;
    }

    // ---- The one row (FileView.Row) ----
    // The row scrolls sideways and follows the hand (WheelGlide, as the lists do): a step of the wheel moves it by one file's
    // width. A touchpad's sideways swipe (the horizontal wheel: ShelfRowSwipe) moves it directly, as far as the fingers go. Every
    // file is the same size.
    private ScrollViewer? _acrossViewer;
    private WheelGlide? _acrossGlide;
    private FrameworkElement AcrossList(ScrollViewer viewer, FrameworkElement list, double step)
    {
        _acrossViewer = viewer; _acrossGlide = new WheelGlide(viewer, step, across: true);
        viewer.Tag = ShelfSide.Files;
        // A thin handle under the row while it moves, as the lists have at their right; it goes when the row has rested a moment.
        var bar = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = UI.Brush(Look.Hex("scrollThumb")), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0, IsHitTestVisible = false };
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var rest = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        rest.Tick += (_, _) => { rest.Stop(); bar.BeginAnimation(OpacityProperty, Soft.Enabled ? new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) : null); if (!Soft.Enabled) bar.Opacity = 0; };
        // The row fades out at a side while more of it lies beyond that side.
        void Mask()
        {
            bool before = viewer.HorizontalOffset > 1, after = viewer.ScrollableWidth > 1 && viewer.HorizontalOffset < viewer.ScrollableWidth - 1;
            if (!before && !after) { viewer.OpacityMask = null; return; }
            fade.GradientStops.Clear();
            fade.GradientStops.Add(new GradientStop(before ? Colors.Transparent : Colors.Black, 0));
            fade.GradientStops.Add(new GradientStop(Colors.Black, before ? .08 : 0));
            fade.GradientStops.Add(new GradientStop(Colors.Black, after ? .9 : 1));
            fade.GradientStops.Add(new GradientStop(after ? Colors.Transparent : Colors.Black, 1));
            viewer.OpacityMask = fade;
        }
        viewer.ScrollChanged += (_, e) =>
        {
            Mask();
            if (viewer.ScrollableWidth < 1 || viewer.ExtentWidth <= 0 || e.HorizontalChange == 0) return;
            double view = viewer.ViewportWidth;
            bar.Width = Math.Max(24, view * view / viewer.ExtentWidth);
            bar.Margin = new Thickness((view - bar.Width) * viewer.HorizontalOffset / viewer.ScrollableWidth, 0, 0, 0);
            bar.BeginAnimation(OpacityProperty, null); bar.Opacity = 1;
            rest.Stop(); rest.Start();
        };
        double offset = _fileOffset;
        viewer.Loaded += (_, _) => { if (offset > 0) viewer.ScrollToHorizontalOffset(offset); Mask(); };
        viewer.Unloaded += (_, _) => { rest.Stop(); if (_acrossViewer == viewer) { _acrossViewer = null; _acrossGlide = null; } };
        // The viewer marks every wheel handled; with nothing to scroll the mark is taken back (PassWheel).
        viewer.AddHandler(MouseWheelEvent, new MouseWheelEventHandler((_, e) => { if (viewer.ScrollableWidth < 1) e.Handled = false; }), true);
        var host = new Grid();
        host.Children.Add(list); host.Children.Add(bar);
        return host;
    }
    // The horizontal wheel on the page (a touchpad's two-finger sideways swipe). A swipe belongs to one of two: to the one row
    // (FileView.Row), which then moves with the fingers, or to the page, which switches the stacked cards with it
    // (ShelfWheelSwipe). Which, is settled as the swipe begins, after a quiet wheel or when it turns the other way: the row's when
    // the pointer is over the row and the row has somewhere to go that way, else the page's. It keeps to that for all its
    // coasting parts. So the rest of the swipe that brought the file transfer forward does not push the row along, now that the
    // row lies under the pointer, and a swipe back from the row's start switches the cards rather than being lost in the row.
    private long _sideAt;
    private int _sideWay;
    private bool _sideRow;
    private bool ShelfSideWheel(int delta)
    {
        if (delta == 0) return false;
        long now = Environment.TickCount64;
        if (now - _sideAt >= HorizontalSwipe.Quiet.TotalMilliseconds || Math.Sign(delta) != _sideWay)
            _sideRow = _acrossViewer is { IsVisible: true } viewer && _acrossGlide is { } row && IsShelfPage && _expanded && row.Reaches(delta)
                && PresentationSource.FromVisual(viewer) is not null && ScreenBounds(viewer).Contains(WindowsHost.Cursor());
        _sideAt = now; _sideWay = Math.Sign(delta);
        if (!_sideRow) return ShelfWheelSwipe(delta);
        _acrossGlide?.Swipe(delta);
        return true;
    }

    // ---- A clipboard entry three lines high (ClipView.Three) ----
    // The entry's text as its rows and blocks show it: wrapped onto up to `lines` lines and cut with "…" after them; code keeps
    // its own lines and is not wrapped; a link has its address after the host in the third tone.
    private TextBlock ClipLines(ClipEntry clip, double size, double line, int lines)
    {
        bool code = clip.Kind == ClipKind.Code, color = clip.Kind == ClipKind.Color;
        var block = new TextBlock
        {
            FontFamily = code || color ? new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI") : UI.PanelFont, FontSize = code || color ? size - .5 : size, Foreground = UI.Brush(Look.Primary),
            VerticalAlignment = VerticalAlignment.Top, TextWrapping = code ? TextWrapping.NoWrap : TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = line, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = lines * line
        };
        if (clip.Kind == ClipKind.Link && !Searching)
        {
            var (host, rest) = Shelf.SplitLink(clip.Text);
            block.Inlines.Add(new Run(host)); block.Inlines.Add(new Run(rest) { Foreground = UI.Brush(Look.Tertiary) });
            return block;
        }
        var kept = clip.Text.Replace("\r\n", "\n").Split('\n').Select(text => text.TrimEnd()).Where(text => text.Trim().Length > 0).Take(lines);
        AddHighlighted(block.Inlines, string.Join("\n", kept.Select(text => code ? text.Replace("\t", "  ") : text.Trim())), Look.Primary);
        return block;
    }
    // The entry's own lines that have text: what its rows count as its lines.
    private static int TextLines(ClipEntry clip) => clip.Text.Replace("\r\n", "\n").Split('\n').Count(text => text.Trim().Length > 0);
    // A picture of the entry in a frame of the height given, as wide as the picture's shape makes it within the room.
    private Border ClipThumb(ClipEntry clip, double height, double room)
    {
        double shape = clip.PixelHeight > 0 ? (double)clip.PixelWidth / clip.PixelHeight : 1.6;
        double width = Math.Round(Math.Clamp(height * shape, height * .75, Math.Max(height * .75, Math.Min(room, height * 3.2))));
        var thumb = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(Look.Corner(4)), Background = UI.Brush(Look.Fill), ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        ShowThumb(thumb, clip.Id, width);
        return thumb;
    }
    // In "全部", the categories the entry is in, in their order (in a category of its own the list already says it).
    private List<ShelfCategory> ClipMarks(ClipEntry clip) => _clipTab is null ? ShelfData.ClipCategories.Where(category => clip.Categories.Contains(category.Id)).ToList() : [];
    private FrameworkElement MarkGlyph(ShelfCategory category)
    {
        FrameworkElement glyph = category.Id == ShelfData.Favorites ? StarGlyph(9, Look.Tertiary, filled: true) : new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(category.Color) };
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return glyph;
    }
    // At the left the text, on one line when it is one line and on up to three when it is longer, or the picture, or the color;
    // at the right three lines of facts: when it was copied, with the time of day (Shelf.WhenExact), and before it the marks of
    // its categories as the one-line row has them; what it holds, in characters and lines, or a picture's size; and the
    // application it was copied from. Under the pointer the three
    // lines give way to the entry's buttons, which stand at the right in the middle of the row's height. A thin line parts the
    // entries.
    // previous, next: the entries above and below in the list, for the lines between them.
    private readonly Dictionary<Guid, FrameworkElement> _rowRules = [];
    private Border ClipRowThree(ClipEntry clip, ShelfLayout layout, Guid? previous, Guid? next)
    {
        double line = layout.ThreeLine, pad = layout.ThreePad;
        var frame = RowFrame(layout, layout.ThreeRow, _picked.Contains(clip.Id));
        frame.Padding = new Thickness(8, pad, 8, pad);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ThreeSide) });
        double room = Math.Max(40, layout.ClipColumn - layout.ThreeSide - 10 - 8);
        FrameworkElement content;
        if (clip.Kind == ClipKind.Image) content = ClipThumb(clip, layout.ThreeThumb, room);
        else if (clip.Kind == ClipKind.Color && Shelf.TryColor(clip.Text, out var c))
        {
            var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(Look.Corner(3)), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                BorderBrush = UI.Brush(Look.Hairline), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Math.Round((line - 12) / 2), 8, 0) };
            var panel = new DockPanel { VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(swatch, System.Windows.Controls.Dock.Left);
            panel.Children.Add(swatch); panel.Children.Add(ClipLines(clip, layout.Body, line, 1));
            content = panel;
        }
        else content = ClipLines(clip, layout.Body, line, 3);
        grid.Children.Add(content);

        TextBlock Fact(string text)
        {
            var fact = Label(text, layout.Aux, Look.Tertiary, HorizontalAlignment.Right);
            Typography.SetNumeralAlignment(fact, FontNumeralAlignment.Tabular);
            return fact;
        }
        var when = Fact(clip.Manual ? Loc.T("常用语", "Phrase") : Shelf.WhenExact(clip.Copied, DateTimeOffset.Now));
        // Under the pointer nothing of the three lines stays: the buttons take their place, at the size of the one-line row's.
        var strip = DeedStrip(ClipDeeds(clip), () => OpenDetail(ShelfSide.Clipboard, clip.Id), 26, 24, 14);
        strip.Margin = new Thickness(0, 0, -4, 0);
        var more = new Border { Child = strip, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
        // The second and third lines, which go with the first under the pointer (RowEnd.Others).
        var side = new StackPanel();
        string holds = clip.Kind == ClipKind.Image ? $"{clip.PixelWidth}×{clip.PixelHeight}" : clip.Kind == ClipKind.Color ? Loc.T("颜色", "Color") : Shelf.Count(clip.Text);
        side.Children.Add(new Grid { Height = line, Children = { Fact(holds) } });
        // The application the entry was copied from, as its details name it; a phrase has none.
        if (!clip.Manual && clip.Source.Length > 0) side.Children.Add(new Grid { Height = line, Children = { Fact(clip.Source) } });
        // The first line: the marks of the entry's categories (a star for "常用", a dot in each category's color, three at most;
        // the note names them all), then the time.
        var first = new DockPanel { Height = line, HorizontalAlignment = HorizontalAlignment.Right };
        FrameworkElement? mark = null;
        var marked = ClipMarks(clip);
        if (marked.Count > 0)
        {
            var marks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent, ToolTip = string.Join(Loc.T("、", ", "), marked.Select(Shelf.CategoryName)) };
            foreach (var category in marked.Take(3))
            {
                var glyph = MarkGlyph(category);
                if (marks.Children.Count > 0) glyph.Margin = new Thickness(3, 0, 0, 0);
                marks.Children.Add(glyph);
            }
            DockPanel.SetDock(marks, System.Windows.Controls.Dock.Left);
            first.Children.Add(marks);
            mark = marks;
        }
        first.Children.Add(when);
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        lines.Children.Add(first);
        lines.Children.Add(side);
        var slot = new Grid { Tag = new RowEnd(when, more) { Others = side, Mark = mark } };
        slot.Children.Add(lines); slot.Children.Add(more);
        Grid.SetColumn(slot, 2); grid.Children.Add(slot);
        var body = new Grid();
        body.Children.Add(grid);
        // The thin line over the entry (none over the first). A line shows only between two entries at rest: against an entry
        // that is lit or chosen it goes, on both of that entry's sides, so the entry's fill stands clear of lines, which in a
        // theme of strong lines and fills otherwise run along and through its edge.
        bool Parted(Guid? above, Guid below) => above is { } id && !_picked.Contains(id) && !_picked.Contains(below);
        if (previous is not null)
        {
            var rule = new Border { Height = 1, Background = UI.Brush(Look.Hairline), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -pad, 0, 0), IsHitTestVisible = false,
                Opacity = Parted(previous, clip.Id) ? 1 : 0 };
            body.Children.Add(rule);
            _rowRules[clip.Id] = rule;
        }
        void Rules(bool lit)
        {
            if (_rowRules.TryGetValue(clip.Id, out var own)) own.Opacity = !lit && Parted(previous, clip.Id) ? 1 : 0;
            if (next is { } after && _rowRules.TryGetValue(after, out var below)) below.Opacity = !lit && Parted(clip.Id, after) ? 1 : 0;
        }
        frame.MouseEnter += (_, _) => Rules(true);
        frame.MouseLeave += (_, _) => Rules(false);
        frame.Child = body;
        AutomationProperties.SetName(frame, clip.Kind == ClipKind.Image ? Loc.T("图片", "Picture") : Shelf.FirstLine(clip.Text).First);
        _shelfRows[clip.Id] = (frame, slot);
        AttachRow(frame, slot, ShelfSide.Clipboard, clip.Id, picked: _picked.Contains(clip.Id), click: () => ClickClip(clip), menu: () => ClipMenu(clip), drag: () => ClipDrag(clip));
        if (_flash is { } flash && flash.Id == clip.Id && flash.Until > Environment.TickCount64) ShowFlash(frame, slot, flash.Text);
        return frame;
    }

    // ---- A clipboard entry as a block (ClipView.Blocks) ----
    // The text wrapped inside the block; a picture over the whole block; a color as a swatch. Along the bottom: the lines there
    // was no room for (a color's value instead), the marks of its categories, and the time, which gives way to the buttons under
    // the pointer.
    private Border ClipBlock(ClipEntry clip, ShelfLayout layout, double width)
    {
        double corner = Look.Corner(8), side = layout.BlockSide;
        var rest = UI.Brush(Look.Fill);
        // The block's own fill at rest, kept on it for the row's lighting to go back to (AttachRow, FlashEnded).
        bool picked = _picked.Contains(clip.Id);
        var frame = new Border { Height = layout.BlockHeight, CornerRadius = new CornerRadius(corner), Background = picked ? PickedFill : rest, Cursor = Cursors.Hand, Tag = rest };
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.BlockFoot) });
        bool picture = clip.Kind == ClipKind.Image;
        string left = "";
        if (picture)
        {
            var art = new Border { CornerRadius = new CornerRadius(corner), Background = UI.Brush(Look.Fill), IsHitTestVisible = false };
            ShowThumb(art, clip.Id, width);
            Grid.SetRowSpan(art, 2); body.Children.Add(art);
            // The time is read on the picture: the block darkens toward its bottom edge.
            var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0)); shade.GradientStops.Add(new GradientStop(Color.FromArgb(200, 0, 0, 0), 1));
            var foot = new Border { Height = layout.BlockFoot + 12, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(0, 0, corner, corner), Background = shade, IsHitTestVisible = false };
            Grid.SetRowSpan(foot, 2); body.Children.Add(foot);
        }
        else if (clip.Kind == ClipKind.Color && Shelf.TryColor(clip.Text, out var c))
        {
            body.Children.Add(new Border { CornerRadius = new CornerRadius(Look.Corner(5)), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), Margin = new Thickness(side, layout.BlockPad, side, 2) });
            left = clip.Text.Trim();
        }
        else
        {
            var text = ClipLines(clip, layout.BlockText, layout.BlockLine, layout.BlockLines);
            text.Margin = new Thickness(side, layout.BlockPad, side, 0);
            body.Children.Add(text);
            int more = TextLines(clip) - layout.BlockLines;
            if (more > 0) left = "+" + more + Loc.T(" 行", more == 1 ? " line" : " lines");
        }
        // The bottom line has room for the note and the time together or for one of them: a color keeps its value, which is what
        // it is, and anything else keeps its time.
        string time = clip.Manual ? Loc.T("常用语", "Phrase") : Shelf.When(clip.Copied, DateTimeOffset.Now);
        var marked = ClipMarks(clip);
        if (left.Length > 0)
        {
            bool value = clip.Kind == ClipKind.Color;
            double note = TextWidth(left, layout.Aux, FontWeights.Normal, value ? new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI") : null);
            double marks = marked.Count == 0 ? 0 : 6 + Math.Min(3, marked.Count) * 9 + (Math.Min(3, marked.Count) - 1) * 3;
            if (note + 6 + marks + TextWidth(time, layout.Aux, FontWeights.Normal) > width - 2 * side) { if (value) time = ""; else left = ""; }
        }
        var bottom = new Grid { Margin = new Thickness(side, 0, side, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (left.Length > 0)
        {
            bool value = clip.Kind == ClipKind.Color;
            var note = Label(left, layout.Aux, value ? Look.Primary : Look.Tertiary, HorizontalAlignment.Left);
            if (value) note.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
            bottom.Children.Add(note);
        }
        FrameworkElement? mark = null;
        if (marked.Count > 0)
        {
            var marks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent, ToolTip = string.Join(Loc.T("、", ", "), marked.Select(Shelf.CategoryName)) };
            foreach (var category in marked.Take(3))
            {
                var glyph = MarkGlyph(category);
                if (marks.Children.Count > 0) glyph.Margin = new Thickness(3, 0, 0, 0);
                marks.Children.Add(glyph);
            }
            mark = marks;
            Grid.SetColumn(mark, 1); bottom.Children.Add(mark);
        }
        var when = Label(time, layout.Aux, picture ? "#EBFFFFFF" : Look.Tertiary, HorizontalAlignment.Right);
        Typography.SetNumeralAlignment(when, FontNumeralAlignment.Tabular);
        var strip = DeedStrip(ClipDeeds(clip), () => OpenDetail(ShelfSide.Clipboard, clip.Id), 20, Math.Min(layout.BlockFoot - 2, 18));
        strip.Margin = new Thickness(0, 0, -4, 0);
        var slot = DeedLine(when, strip, layout.BlockFoot, HorizontalAlignment.Right);
        slot.Margin = new Thickness(6, 0, 0, 0); slot.MinWidth = 22;
        ((RowEnd)slot.Tag).Mark = mark;
        Grid.SetColumn(slot, 2); bottom.Children.Add(slot);
        Grid.SetRow(bottom, 1); body.Children.Add(bottom);
        frame.Child = body;
        AutomationProperties.SetName(frame, picture ? Loc.T("图片", "Picture") : Shelf.FirstLine(clip.Text).First);
        _shelfRows[clip.Id] = (frame, slot);
        AttachRow(frame, slot, ShelfSide.Clipboard, clip.Id, picked: picked, click: () => ClickClip(clip), menu: () => ClipMenu(clip), drag: () => ClipDrag(clip), rest: rest);
        if (_flash is { } flash && flash.Id == clip.Id && flash.Until > Environment.TickCount64) ShowFlash(frame, slot, flash.Text);
        return frame;
    }
    // A row of blocks: equal columns with the blocks' gap between them and under the row; a last row short of blocks keeps them
    // at the left, the same size.
    private FrameworkElement BlockRow(ClipEntry[] clips, ShelfLayout layout)
    {
        int columns = layout.ClipColumns;
        var row = new Grid { Margin = new Thickness(8, 0, 0, layout.BlockGap) };
        double width = Math.Max(0, (layout.ClipColumn - (columns - 1) * layout.BlockGap) / columns);
        for (int i = 0; i < columns; i++)
        {
            if (i > 0) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.BlockGap) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (int i = 0; i < clips.Length; i++)
        {
            var block = ClipBlock(clips[i], layout, width);
            Grid.SetColumn(block, 2 * i); row.Children.Add(block);
        }
        return row;
    }

    // ---- Nothing to show ----
    // Three small cards over the page's words when a column is empty: a picture, a folder and a document for the file transfer;
    // a picture, text and a link for the clipboard. The one in the middle stands upright in front, the two beside it lean away.
    // Drawn on a 104 by 52 field and scaled to the page's step.
    private const string KindPicture = "M 5,4 H 19 A 1,1 0 0 1 20,5 V 19 A 1,1 0 0 1 19,20 H 5 A 1,1 0 0 1 4,19 V 5 A 1,1 0 0 1 5,4 Z M 4,16 L 9,11 L 13,15 L 16,12 L 20,16 M 15.4,8.5 H 15.6";
    private const string KindDocument = "M 7,3 H 14 L 19,8 V 20 A 1,1 0 0 1 18,21 H 7 A 1,1 0 0 1 6,20 V 4 A 1,1 0 0 1 7,3 Z M 14,3 V 8 H 19 M 9,13 H 15 M 9,17 H 13";
    private const string KindFolder = "M 3,7 A 2,2 0 0 1 5,5 H 9 L 11,7.5 H 19 A 2,2 0 0 1 21,9.5 V 17 A 2,2 0 0 1 19,19 H 5 A 2,2 0 0 1 3,17 Z";
    private const string KindText = "M 5,6 H 19 M 5,10 H 19 M 5,14 H 19 M 5,18 H 13";
    private const string KindLink = "M 10,14 A 4,4 0 0 0 15.7,14 L 18.7,11 A 4,4 0 0 0 13,5.3 L 12,6.3 M 14,10 A 4,4 0 0 0 8.3,10 L 5.3,13 A 4,4 0 0 0 11,18.7 L 12,17.7";
    private FrameworkElement EmptyKinds(ShelfSide side, ShelfLayout layout)
    {
        double scale = layout.Tier switch { ShelfTier.Compact => .72, ShelfTier.Standard => 1, _ => 1.2 };
        var field = new Canvas { Width = 104, Height = 52, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false,
            LayoutTransform = new ScaleTransform(scale, scale), Margin = new Thickness(0, 0, 0, Math.Round(8 * scale)) };
        var surface = Look.ColorOf("surface"); var ink = Look.ColorOf("textPrimary");
        Color Mix(double share) => Color.FromRgb((byte)(surface.R + (ink.R - surface.R) * share), (byte)(surface.G + (ink.G - surface.G) * share), (byte)(surface.B + (ink.B - surface.B) * share));
        void Card(string data, string color, double left, double angle, bool front)
        {
            double width = front ? 38 : 32, height = front ? 46 : 38, icon = front ? 21 : 17;
            // The drawing on its 24 unit field, in the middle of the card whatever its outline measures.
            var glyph = new Path { Data = Geometry.Parse(data), Stroke = UI.Brush(color), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            var drawing = new Canvas { Width = 24, Height = 24, Children = { glyph }, LayoutTransform = new ScaleTransform(icon / 24, icon / 24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var card = new Border
            {
                Width = width, Height = height, CornerRadius = new CornerRadius(Look.Corner(5)), BorderThickness = new Thickness(1), BorderBrush = UI.Brush(Look.Hex("outline")),
                Background = new SolidColorBrush(Mix(front ? .12 : .08)), Child = drawing, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(angle)
            };
            Canvas.SetLeft(card, left); Canvas.SetTop(card, front ? 2 : 10);
            field.Children.Add(card);
        }
        // The two tints are those of the dark themes, darkened where the theme's surface is light.
        Card(KindPicture, Look.Readable("#D5AA7D"), 12, -14, false);
        Card(side == ShelfSide.Files ? KindDocument : KindLink, Look.Readable("#91AED5"), 60, 14, false);
        Card(side == ShelfSide.Files ? KindFolder : KindText, Look.Primary, 33, 0, true);
        return field;
    }
}

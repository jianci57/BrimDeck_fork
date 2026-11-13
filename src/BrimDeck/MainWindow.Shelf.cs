using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using Path = System.Windows.Shapes.Path;

namespace BrimDeck;

// The clipboard and file transfer page: the clipboard at the left, the file transfer at the right, both lists at once. A click on a
// row pastes it into the window the user is typing in (the panel takes no focus, so that window keeps it); "›" or the details show
// the whole entry in place of the page (MainWindow.ShelfDetail.cs). The lists, categories and files are shared by every panel
// (ShelfService); what each panel shows (the chosen category, the search, the details) is its own.
public partial class MainWindow
{
    private Guid? _clipTab, _fileTab;
    private string _shelfQuery = "";
    private bool _shelfSearching;
    private (ShelfSide Side, Guid Id)? _shelfDetail;
    // The text entry being edited in place of the list (MainWindow.ShelfDetail.cs).
    private Guid? _shelfEdit;
    private readonly HashSet<Guid> _picked = [];
    private Guid? _pickAnchor;
    // The row that was just pasted: its time reads "已粘贴" on a blue background for a moment.
    private (Guid Id, string Text, long Until)? _flash;
    private DispatcherTimer? _flashTimer;
    // Files dragged over the panel (ShelfDragEnter): the page shown before, to go back to if they are not dropped.
    private bool _dropping, _dropOpened, _draggingOut;
    private DeckPage? _dropReturn;
    private DispatcherTimer? _dropLeave;
    private ContextMenu? _shelfMenu;
    private TextBox? _searchBox, _editBox;
    private double _clipOffset, _fileOffset;
    private Rectangle? _fileFrame;
    private readonly List<(Border Tab, Guid? Id, ShelfSide Side)> _shelfTabs = [];
    private readonly Dictionary<Guid, (Border Row, Grid Aux)> _shelfRows = [];
    private ShelfLayout _shelfLayout = ShelfLayout.For(ShelfSizes.Default.Width, ShelfSizes.Default.Height);
    private int _transferShown = -1;

    // The details replace the page switch and the clock with their own header.
    internal bool ShelfHidesSwitch => _shelfDetail is not null;
    // The panel stays open while a menu of the page is open, while the user types in it, and while files are dragged over it.
    internal bool ShelfHolds => _shelfMenu?.IsOpen == true || _shelfEdit is not null || _dropping || _draggingOut || _searchBox?.IsKeyboardFocusWithin == true;
    private ShelfData ShelfData => _app.Shelf.Data;

    private void InitializeShelf()
    {
        Action changed = ShelfChanged;
        _app.Shelf.Changed += changed;
        ShelfFiles.Changed += changed;
        Closed += (_, _) => { _shelfClosed = true; _movingOut.Clear(); _app.Shelf.Changed -= changed; ShelfFiles.Changed -= changed; _movingTimer?.Stop(); };
        HoverZone.AllowDrop = true;
        HoverZone.PreviewDragEnter += ShelfDragEnter;
        HoverZone.PreviewDragOver += ShelfDragOver;
        HoverZone.PreviewDragLeave += ShelfDragLeave;
        HoverZone.PreviewDrop += ShelfDrop;
        // A press anywhere else on the panel lets go of the search box: the keyboard goes back to the window it came from, and the
        // search and its results stay.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_searchBox is not { IsKeyboardFocusWithin: true } box || box.Parent is not Visual host) return;
            if (e.OriginalSource is Visual pressed && (host == pressed || host.IsAncestorOf(pressed))) return;
            EndTyping(); Keyboard.ClearFocus();
        };
        // Typing ends when the user goes to another window; the panel then closes as usual once the pointer is away.
        Deactivated += (_, _) =>
        {
            if (_shelfEdit is not null && !_editCommitting) { _shelfEdit = null; if (IsShelfPage) RenderShelf(); }
            if (_expanded && !_pointerInside && !ShelfHolds) PointerChanged(false);
        };
    }

    // A copy or a dropped file while the panel is closed is shown when it opens (ApplyDefaultMusicPage), not built at every copy;
    // one made while another page shows, when the page is next shown (RenderShelf).
    private bool _shelfStale;
    private void ShelfChanged()
    {
        if (IsShelfPage && _expanded && !_preview) RenderShelf(); else _shelfStale = true;
        int count = Settings.ShowsTransfer(EffectiveStyle) ? ShelfData.Files.Count : 0;
        if (count != _transferShown && !_expanded) RenderCompact();
    }

    // ---- The collapsed count ----
    // The width of the fixed marks of the row: the clock, the count and the devices (CompactMusicLayout).
    // The count shows the whole number, and its width follows the number's digits.
    private static double FixedFace(double scale, params double[] widths)
    {
        var shown = widths.Where(width => width > 0).ToArray();
        return shown.Sum() + Math.Max(0, shown.Length - 1) * CompactMusicLayout.Gap(scale);
    }
    private FrameworkElement? _compactTransfer;
    private FrameworkElement? TransferBadge(double scale)
    {
        int count = Settings.ShowsTransfer(EffectiveStyle) ? ShelfData.Files.Count : 0;
        _transferShown = count; _compactTransfer = null;
        if (count == 0) return null;
        double s = CompactMusicLayout.SafeScale(scale), icon = CompactMusicLayout.Whole(14 * s), gap = CompactMusicLayout.Whole(3 * s);
        // The number starts on a whole device pixel and the badge ends on one. Layout rounding moves a margin that lies between two
        // pixels onto the next (17 DIPs are 25.5 pixels at 150% and become 26), which took the room from the number: a count of two
        // digits no longer fit and, wrapping as every TextBlock does by default (App.xaml), stood on two lines cut by the badge.
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX, start = DevicePixels.Up(icon + gap, dpi);
        var number = new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture), FontFamily = UI.PanelFont, FontSize = CompactMusicLayout.Half(11.5 * s), FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(UI.Primary), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap
        };
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        number.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double digits = number.DesiredSize.Width;
        number.Margin = new Thickness(start, 0, 0, 0);
        var badge = new Grid { Width = DevicePixels.Up(start + digits, dpi), Height = CompactMusicLayout.Whole(16 * s), Background = Brushes.Transparent };
        // The tray on the 16 unit grid of the row's other marks (PowerIcons), scaled to the icon's size with the same 1.35 DIP line,
        // rather than a larger drawing stretched to fit, which thickened and blurred it.
        double grid = icon / 16;
        var tray = new Path
        {
            Data = Geometry.Parse("M 1.5,9 L 3.5,3.5 H 12.5 L 14.5,9 V 12.5 A 1,1 0 0 1 13.5,13.5 H 2.5 A 1,1 0 0 1 1.5,12.5 Z M 1.5,9 H 5.5 L 6.5,10.5 H 9.5 L 10.5,9 H 14.5"),
            Stroke = UI.Brush(UI.Secondary), StrokeThickness = 1.35 / grid, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 16, Height = 16, LayoutTransform = new ScaleTransform(grid, grid), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
        };
        badge.Children.Add(tray); badge.Children.Add(number);
        string tip = Loc.T($"文件中转：{count} 个文件", count == 1 ? "File transfer: 1 file" : $"File transfer: {count} files");
        badge.ToolTip = tip; AutomationProperties.SetName(badge, tip);
        _compactTransfer = badge;
        return badge;
    }
    // Opened while the pointer is on the count, the panel opens on this page.
    private bool PointerOnTransfer()
    {
        if (_compactTransfer is not { IsVisible: true } badge || PresentationSource.FromVisual(badge) is null) return false;
        var rect = new Rect(badge.PointToScreen(new Point()), badge.PointToScreen(new Point(badge.ActualWidth, badge.ActualHeight)));
        rect.Inflate(4, 6);
        return rect.Contains(WindowsHost.Cursor());
    }

    // ---- The page ----
    // What the page's rows were last built for, besides the entries and what the page itself has chosen (the categories, the
    // search, the details), each change of which builds it again (ShelfChanged, and the page's own actions). The minute is part of
    // it, as the rows show how long ago each copy was made.
    private sealed record ShelfBuild(double Width, double Height, object Settings, int Theme, string Language, long Minute, double Dpi, double Scale);
    private ShelfBuild? _shelfBuilt;

    // lazy: asked for with the data or the settings (RenderUsage) rather than by a change of the page itself. The usage is read every
    // minute, and each read used to build this page again, twice, whether or not the panel was open. A lazy render keeps the rows
    // while what they were built for is the same; while the panel is closed it builds nothing, and the page is built as the panel
    // opens (ApplyDefaultMusicPage), as it is after a copy made meanwhile.
    private void RenderShelf(bool lazy = false)
    {
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed; DevicesContent.Visibility = Visibility.Collapsed;
        ShelfContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        var layout = _shelfLayout = ShelfLayout.For(width, height);
        var header = PanelHeader.For(ToolbarFor(height), layout.Side);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        // In the lowest panels the toolbar leaves 2 DIP under the page switch (PanelHeader); the page keeps 4 more below it.
        ShelfContent.Margin = new Thickness(layout.Side, height < 150 ? 4 : 0, layout.Side, layout.Tier == ShelfTier.Compact ? 8 : 12);
        var build = new ShelfBuild(width, height, Settings, PanelTheme.Generation, Loc.Language, DateTime.Now.Ticks / TimeSpan.TicksPerMinute, VisualTreeHelper.GetDpi(this).DpiScaleY, _scale);
        bool keep = lazy && !_shelfStale && build == _shelfBuilt && ShelfContent.Children.Count > 0;
        if (lazy && !keep && !_expanded && !_preview) { _shelfStale = true; return; }
        if (!keep)
        {
            // Built while the panel is closed as well, when the page itself asks: the panel opens on what was built last.
            _shelfStale = false; _shelfBuilt = build;
            // The pointer may have left a row that is about to be replaced; its scroll positions are kept.
            RememberShelfScroll();
            ShelfContent.Children.Clear(); _shelfRows.Clear(); _shelfTabs.Clear(); _fileFrame = null;
        }
        // The header's own parts are set again either way: another page shown meanwhile has hidden them.
        RenderShelfToolbar(width);
        if (_shelfDetail is { } detail && Find(detail.Side, detail.Id) is not null) { if (!keep) ShelfContent.Children.Add(DetailView(detail.Side, detail.Id, width - 2 * layout.Side, layout)); return; }
        _shelfDetail = null;
        RenderPageSwitch();
        if (keep) return;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.FileColumn) });
        var clips = ClipColumn(layout);
        var divider = new Border { Width = 1, Background = UI.Brush(UI.Hairline), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        Grid.SetColumn(divider, 1);
        var files = FileColumn(layout);
        Grid.SetColumn(files, 2);
        grid.Children.Add(clips); grid.Children.Add(divider); grid.Children.Add(files);
        ShelfContent.Children.Add(grid);
        RefocusTyping();
    }
    // During a resize the page is built again only where its sizes step (row height, type size, what the file rows show); in between
    // the file column takes the width the new size gives it and the clipboard column the rest. The details keep the width they were
    // built for until the drag ends.
    private void StretchShelf()
    {
        if (_shelfDetail is not null) return;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        var layout = ShelfLayout.For(width, height);
        if (layout with { FileColumn = 0, ClipColumn = 0 } != _shelfLayout with { FileColumn = 0, ClipColumn = 0 }) { RenderShelf(); return; }
        if (ShelfContent.Children.Count == 1 && ShelfContent.Children[0] is Grid { ColumnDefinitions.Count: 3 } grid)
            grid.ColumnDefinitions[2].Width = new GridLength(layout.FileColumn);
    }
    private void RememberShelfScroll()
    {
        foreach (var viewer in ShelfContent.Children.OfType<Grid>().SelectMany(grid => FindScrolls(grid)))
        {
            if (Equals(viewer.Tag, ShelfSide.Clipboard)) _clipOffset = viewer.VerticalOffset;
            else if (Equals(viewer.Tag, ShelfSide.Files)) _fileOffset = viewer.VerticalOffset;
        }
        static IEnumerable<ScrollViewer> FindScrolls(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer viewer && viewer.Tag is ShelfSide) yield return viewer;
                else foreach (var inner in FindScrolls(child)) yield return inner;
            }
        }
    }

    private object? Find(ShelfSide side, Guid id) => side == ShelfSide.Clipboard ? ShelfData.Clips.FirstOrDefault(clip => clip.Id == id) : ShelfData.Files.FirstOrDefault(file => file.Id == id);
    private bool Searching => _shelfSearching && _shelfQuery.Trim().Length > 0;
    private List<ClipEntry> VisibleClips() => Searching
        ? ShelfData.Clips.Where(clip => clip.Kind != ClipKind.Image && Shelf.Matches(clip.Text, _shelfQuery)).ToList()
        : ShelfData.Clips.Where(clip => _clipTab is not { } id || clip.Categories.Contains(id)).ToList();
    private List<TransferFile> VisibleFiles() => Searching
        ? ShelfData.Files.Where(file => Shelf.Matches(file.Name, _shelfQuery)).ToList()
        : ShelfData.Files.Where(file => _fileTab is not { } id || file.Categories.Contains(id)).ToList();

    // ---- Columns ----
    // The space between a column's header (its filters' underline) and its list.
    private const double ListGap = 4;
    private Grid ClipColumn(ShelfLayout layout)
    {
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.Header + ListGap) });
        column.RowDefinitions.Add(new RowDefinition());
        column.Children.Add(ColumnHeader(ShelfSide.Clipboard, layout.ClipColumn, layout));
        FrameworkElement body;
        if (_shelfEdit is not null) body = TextEditor(layout);
        else
        {
            var clips = VisibleClips();
            if (clips.Count == 0) body = ClipEmpty(layout);
            else
            {
                // Only the rows in view are built (ShelfRowList); a row reaches from the list's left edge, its text 8 DIP in.
                var list = new ShelfRowList(
                    item => { var row = ClipRow((ClipEntry)item, layout); row.Margin = new Thickness(0); return row; },
                    (item, row) => { if (_shelfRows.TryGetValue(((ClipEntry)item).Id, out var kept) && kept.Row == row) _shelfRows.Remove(((ClipEntry)item).Id); })
                { ItemsSource = clips, Margin = new Thickness(-8, 0, 0, 0) };
                Fade(PassWheel(list.Viewer), ShelfSide.Clipboard, _clipOffset);
                body = list;
            }
        }
        Grid.SetRow(body, 1); column.Children.Add(body);
        return column;
    }
    private Grid FileColumn(ShelfLayout layout)
    {
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.Header + ListGap) });
        column.RowDefinitions.Add(new RowDefinition());
        // The drop highlight: over the list, below the header: a faint fill and a thin dashed outline in the theme's accent, corner 8.
        // The list stays visible. It stands clear of the rows: up into the gap under the filters, and out to the left past a row's
        // background (8 DIP left of the text) to 12 DIP from the icons, stopping 4 DIP short of the divider in the gap between the
        // columns; on the right, the same distance from the text, whose row ends 4 DIP past it.
        double corner = PanelTheme.Corner(8), left = Math.Min(12, layout.Gap / 2 - 4);
        var frame = new Rectangle { RadiusX = corner, RadiusY = corner, StrokeThickness = 1, StrokeDashArray = [4, 3], Margin = new Thickness(-left, -ListGap, -(left - 4), -4), IsHitTestVisible = false };
        Grid.SetRow(frame, 1); column.Children.Add(frame); _fileFrame = frame; ShowDropFrame();
        column.Children.Add(ColumnHeader(ShelfSide.Files, layout.FileColumn, layout));
        var files = VisibleFiles();
        FrameworkElement body;
        if (files.Count == 0) body = FileEmpty(layout);
        else
        {
            // As the clipboard's list: only the rows in view are built (ShelfRowList). What is picked is kept by the page (_picked),
            // so a row built as it scrolls into view shows it.
            var list = new ShelfRowList(
                item => { var row = FileRow((TransferFile)item, layout); row.Margin = new Thickness(0); return row; },
                (item, row) => { if (_shelfRows.TryGetValue(((TransferFile)item).Id, out var kept) && kept.Row == row) _shelfRows.Remove(((TransferFile)item).Id); })
            { ItemsSource = files, Margin = new Thickness(-8, 0, 0, 0) };
            Fade(PassWheel(list.Viewer), ShelfSide.Files, _fileOffset);
            body = list;
        }
        Grid.SetRow(body, 1); column.Children.Add(body);
        return column;
    }
    private void ShowDropFrame()
    {
        if (_fileFrame is null) return;
        var accent = PanelTheme.ColorOf("accent");
        _fileFrame.Fill = _dropping ? new SolidColorBrush(Color.FromArgb(18, accent.R, accent.G, accent.B)) : Brushes.Transparent;
        _fileFrame.Stroke = _dropping ? new SolidColorBrush(Color.FromArgb(170, accent.R, accent.G, accent.B)) : Brushes.Transparent;
    }
    // A list or text with nothing to scroll lets the wheel through to the panel (PanelWheel: the volume or the pages); one that
    // scrolls keeps it. A scroll viewer marks every wheel handled, so this takes the mark back when there is nothing to scroll.
    private static T PassWheel<T>(T viewer) where T : ScrollViewer
    {
        viewer.AddHandler(MouseWheelEvent, new MouseWheelEventHandler((_, e) => { if (viewer.ScrollableHeight < 1) e.Handled = false; }), true);
        return viewer;
    }
    // A list scrolls inside its column (ShelfRowList, which starts 8 DIP left of the column so a row's background reaches past its
    // text); the scroll bar is only a thin handle (App.xaml). This marks the list's side (RememberShelfScroll), goes back to its
    // scroll position, and fades the list out at the bottom while more of it lies below.
    private static ScrollViewer Fade(ScrollViewer viewer, ShelfSide side, double offset)
    {
        viewer.Tag = side;
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0)); fade.GradientStops.Add(new GradientStop(Colors.Black, .9)); fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        void Mask() => viewer.OpacityMask = viewer.ScrollableHeight > 1 && viewer.VerticalOffset < viewer.ScrollableHeight - 1 ? fade : null;
        viewer.ScrollChanged += (_, _) => Mask();
        viewer.Loaded += (_, _) => { if (offset > 0) viewer.ScrollToVerticalOffset(offset); Mask(); };
        return viewer;
    }

    private FrameworkElement ClipEmpty(ShelfLayout layout)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, layout.Header / 2) };
        bool favorites = _clipTab == ShelfData.Favorites;
        string text = Searching ? Loc.T("没有匹配的内容", "Nothing matches")
            : favorites ? Loc.T("常用的内容放在这里，一直保留", "Favorites are kept here until you delete them")
            : _clipTab is not null ? Loc.T("这个分类还没有内容", "This category is empty")
            : !Settings.ClipboardRecord ? Loc.T("剪贴板记录尚未开启", "Clipboard history is off")
            : Loc.T("复制的内容会显示在这里", "What you copy appears here");
        panel.Children.Add(Label(text, layout.Body, UI.Secondary, HorizontalAlignment.Center));
        if (!Searching && favorites)
        {
            panel.Children.Add(Label(Loc.T("右键任意一条，选择“加入常用”", "Right-click an entry and choose Add to favorites"), layout.Aux, UI.Tertiary, HorizontalAlignment.Center, top: 4));
            var add = PillButton(Loc.T("在设置中添加常用语", "Add phrases in settings"), primary: false, () => _app.OpenSettingsPage(SettingsWindow.ClipboardSettingsPage));
            add.Margin = new Thickness(0, 10, 0, 0); add.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(add);
        }
        else if (!Searching && _clipTab is null && !Settings.ClipboardRecord)
        {
            panel.Children.Add(Label(Loc.T("开启后保存在本机，加密存放", "Kept on this computer, encrypted"), layout.Aux, UI.Tertiary, HorizontalAlignment.Center, top: 4));
            var on = PillButton(Loc.T("开启记录", "Turn on"), primary: true, () => _app.UpdateSettings(SettingsWith(s => s.ClipboardRecord = true)));
            on.Margin = new Thickness(0, 10, 0, 0); on.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(on);
        }
        return panel;
    }
    private FrameworkElement FileEmpty(ShelfLayout layout)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, layout.Header / 2) };
        if (Searching) { panel.Children.Add(Label(Loc.T("没有匹配的文件", "No files match"), layout.Body, UI.Secondary, HorizontalAlignment.Center)); return panel; }
        panel.Children.Add(Label(_fileTab is null ? Loc.T("把文件拖到这里", "Drag files here") : Loc.T("这个分类还没有文件", "This category is empty"), layout.Body, UI.Secondary, HorizontalAlignment.Center));
        return panel;
    }

    // ---- The column's header: its name, then "全部" and the categories to show, and its buttons ----
    // The name is a plain title. The filters are text: the one shown is bright and underlined. The clipboard always has "全部" and
    // "常用"; the file transfer shows its filters only once the user has made a category. Categories are made and changed in the
    // settings, not here.
    private FrameworkElement ColumnHeader(ShelfSide side, double width, ShelfLayout layout)
    {
        var row = new Grid { Height = layout.Header, VerticalAlignment = VerticalAlignment.Top };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string name = side == ShelfSide.Clipboard ? Loc.T("剪贴板", "Clipboard") : Loc.T("文件中转", "File transfer");
        // The name and the filters, left to right; "分类 ⌄" last takes what room is left, its word trimmed to it.
        var tabs = new DockPanel { LastChildFill = false, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, -4, 0) };
        Grid.SetColumn(buttons, 1);
        row.Children.Add(tabs); row.Children.Add(buttons);
        var title = Label(name, layout.Title, UI.Primary, HorizontalAlignment.Left); title.FontWeight = FontWeights.SemiBold;
        tabs.Children.Add(title);
        if (Searching)
        {
            int found = side == ShelfSide.Clipboard ? VisibleClips().Count : VisibleFiles().Count;
            tabs.Children.Add(Label(Loc.T($"{found} 个结果", found == 1 ? "1 result" : $"{found} results"), layout.Aux, UI.Tertiary, HorizontalAlignment.Left, left: 10));
            return row;
        }
        double buttonsWidth = 0;
        if (side == ShelfSide.Clipboard)
        {
            // "清除" takes away at once what the filter shown holds (Shelf.Clear): in "全部" all that is not kept; in a category of the
            // user's, its entries, those also in another category only leaving this one. "常用" has none. The button stays while the
            // list shows anything, even when all of it is kept and a press takes nothing away.
            if (VisibleClips().Count > 0)
            {
                string tip = _clipTab == ShelfData.Favorites ? Loc.T("“常用”中的内容不会被清除", "What is in Favorites is not cleared")
                    : _clipTab is { } id && ShelfData.ClipCategories.FirstOrDefault(c => c.Id == id) is { } category
                    ? Loc.T($"清除“{category.Name}”中的全部内容", $"Clear everything in {category.Name}") : Loc.T("清除全部内容，“常用”和一直保留的分类不受影响", "Clear everything but Favorites and the categories kept");
                buttons.Children.Add(ClearButton(tip, () =>
                {
                    if (Shelf.Clearable(ShelfData, _clipTab).Count == 0) return;
                    var gone = Shelf.Clear(ShelfData, _clipTab);
                    if (gone.Count > 0) DeleteClips(gone); else _app.Shelf.Edited();
                }));
                buttonsWidth += ClearWidth;
            }
        }
        var categories = ShelfData.Categories(side);
        var own = ShelfData.OwnCategories(side).ToList();
        var selected = side == ShelfSide.Clipboard ? _clipTab : _fileTab;
        double titleWidth = TextWidth(name, layout.Title, FontWeights.SemiBold);
        // "全部" and "常用" always show. The user's categories follow them when they all fit; else they go into one filter with "⌄"
        // in the third place.
        bool favorites = categories.Any(category => category.Id == ShelfData.Favorites);
        double fixedWidth = categories.Count == 0 ? 0 : BarWidth + TabWidth(Loc.T("全部", "All"), layout, false) + (favorites ? TabWidth(Loc.T("常用", "Favorites"), layout, false) : 0);
        double ownWidth = own.Sum(category => TabWidth(category.Name, layout, true));
        if (side == ShelfSide.Files)
        {
            // "清除" as on the clipboard: every file in "全部", a category's files in it (those also in another category only leave
            // it); the files themselves stay where they are.
            bool clearable = Shelf.ClearableFiles(ShelfData, _fileTab).Count > 0;
            double clear = clearable ? ClearWidth : 0;
            buttonsWidth = clear;
            // "拖出全部" (its icon) shows only while there is a file to drag out, and gives way to the filters in a column too narrow for
            // both "全部 ⌄" and it (each file can still be dragged out on its own).
            if (VisibleFiles().Any(file => ShelfFiles.Exists(file.Path)))
            {
                double free = width - clear - ButtonsMargin - titleWidth - 8;
                double least = fixedWidth + (own.Count == 0 ? 0 : Math.Min(ownWidth, PickerChrome));
                if (least + DragAllWidth <= free) { buttons.Children.Add(DragAllButton()); buttonsWidth += DragAllWidth; }
            }
            if (clearable)
            {
                string tip = _fileTab is { } id && ShelfData.FileCategories.FirstOrDefault(c => c.Id == id) is { } category
                    ? Loc.T($"清除“{category.Name}”中的文件记录", $"Clear the files listed in {category.Name}") : Loc.T("清除全部文件记录", "Clear every file listed");
                buttons.Children.Add(ClearButton(tip, () =>
                {
                    var gone = Shelf.ClearFiles(ShelfData, _fileTab);
                    if (gone.Count > 0) RemoveFiles(gone); else _app.Shelf.Edited();
                }));
            }
        }
        double room = width - buttonsWidth - ButtonsMargin - 4;
        bool fits = titleWidth + fixedWidth + ownWidth <= room;
        if (categories.Count > 0)
        {
            tabs.Children.Add(new Border { Width = 1, Height = 12, Background = UI.Brush(UI.Hairline), Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            tabs.Children.Add(Tab(side, null, Loc.T("全部", "All"), layout, selected is null));
            if (favorites) tabs.Children.Add(Tab(side, ShelfData.Favorites, Loc.T("常用", "Favorites"), layout, selected == ShelfData.Favorites));
            if (fits)
                foreach (var category in own) tabs.Children.Add(Tab(side, category.Id, category.Name, layout, selected == category.Id, category.Color));
            else if (own.Count > 0)
            {
                tabs.LastChildFill = true;
                tabs.Children.Add(FilterPicker(side, own.FirstOrDefault(category => category.Id == selected), layout, width - buttonsWidth - ButtonsMargin - titleWidth - fixedWidth - PickerChrome));
            }
        }
        // The size of everything on the file transfer, where there is room for it beside the filters.
        if (side == ShelfSide.Files && fits && layout.FileColumn >= 260 && ShelfData.Files.Count > 0 && room - titleWidth - fixedWidth - ownWidth > 70)
        {
            long total = ShelfData.Files.Sum(file => ShelfFiles.FileSize(file.Path) ?? (ShelfFiles.IsFolder(file.Path) ? ShelfFiles.Folder(file.Path).Size ?? 0 : 0));
            var size = Label(Shelf.Size(total), layout.Aux, UI.Tertiary, HorizontalAlignment.Right);
            size.Margin = new Thickness(0, 0, 4, 0);
            buttons.Children.Insert(0, size);
        }
        return row;
    }
    // The divider between the column's name and its filters, with its margins; the buttons' own margin (4 at the left, -4 at the
    // right); and "⌄" with the picker's padding, what is left of "分类 ⌄" when its word has no room.
    private const double BarWidth = 13, ButtonsMargin = 0, PickerChrome = 25;
    private double TabWidth(string text, ShelfLayout layout, bool dot) => Math.Min(140, TextWidth(text, layout.Body, FontWeights.Normal)) + 12 + (dot ? 11 : 0);
    // The user's categories when they do not all fit: "分类 ⌄", or the one shown, underlined, with "⌄". A click lists them.
    private Border FilterPicker(ShelfSide side, ShelfCategory? shown, ShelfLayout layout, double labelRoom)
    {
        // The dot at the left, "⌄" at the right, the word between them, trimmed when the header is short of room.
        var content = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        if (shown is not null)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(shown.Color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dot, System.Windows.Controls.Dock.Left); content.Children.Add(dot);
        }
        // Without the settings' 1 DIP drop: beside the panel's filters that put the arrow below their middle (measured on screen).
        var chevron = SettingsWindow.Chevron(); chevron.FontSize = 11; chevron.Foreground = UI.Brush(UI.Secondary);
        chevron.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(chevron, System.Windows.Controls.Dock.Right); content.Children.Add(chevron);
        string text = shown?.Name ?? Loc.T("分类", "Categories");
        // In the narrowest file column only "⌄" (and the dot of the category shown) is left.
        var label = Label(text, layout.Body, shown is null ? UI.Tertiary : UI.Primary, HorizontalAlignment.Left); label.MaxWidth = 110;
        if (labelRoom >= 16) content.Children.Add(label);
        // "分类" shows whole or not at all (never "分…"); a category's own long name is trimmed.
        if (shown is null)
        {
            double whole = TextWidth(text, layout.Body, FontWeights.Normal);
            label.SizeChanged += (_, e) => { if (e.NewSize.Width + .5 < whole) label.Visibility = Visibility.Collapsed; };
        }
        var face = new Grid();
        face.Children.Add(content);
        if (shown is not null) face.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = UI.Brush(UI.Accent), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 6, 0) });
        var picker = new Border { Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Child = face, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(picker, Loc.T("分类：", "Categories: ") + text);
        picker.MouseEnter += (_, _) => { label.Foreground = UI.Brush(UI.Primary); chevron.Foreground = UI.Brush(UI.Primary); };
        picker.MouseLeave += (_, _) => { label.Foreground = UI.Brush(shown is null ? UI.Tertiary : UI.Primary); chevron.Foreground = UI.Brush(UI.Secondary); };
        picker.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenMenu(CategoryListMenu(side), picker); };
        picker.MouseRightButtonUp += (_, e) => e.Handled = true;
        return picker;
    }
    // An icon button's note: shown as soon as the pointer is on the button and gone as soon as it leaves. WPF's own note waits a
    // moment, and stays open while the pointer moves toward it, over the button beside.
    private static ToolTip HoverTip(FrameworkElement owner, string text)
    {
        var tip = new ToolTip { Content = text, PlacementTarget = owner, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, VerticalOffset = 4 };
        owner.MouseEnter += (_, _) => tip.IsOpen = true;
        owner.MouseLeave += (_, _) => tip.IsOpen = false;
        owner.Unloaded += (_, _) => tip.IsOpen = false;
        return tip;
    }
    // "清除": a bin, 22 DIP square with 2 at its right; what it clears is said when the pointer rests on it.
    private const double ClearWidth = 24;
    private const string BinIcon = "M 2.5,4.5 H 13.5 M 6,4.5 V 3 H 10 V 4.5 M 4,4.5 L 4.7,13.5 H 11.3 L 12,4.5 M 6.8,7 V 11 M 9.2,7 V 11";
    private Border ClearButton(string tip, Action click)
    {
        var glyph = MenuGlyph(BinIcon, UI.Secondary); glyph.Margin = new Thickness(0); glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var button = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Child = glyph, Background = Brushes.Transparent,
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
        HoverTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.MouseEnter += (_, _) => { button.Background = UI.Brush(PanelTheme.Hex("hover")); glyph.Stroke = UI.Brush(UI.Primary); };
        button.MouseLeave += (_, _) => { button.Background = Brushes.Transparent; glyph.Stroke = UI.Brush(UI.Secondary); };
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        return button;
    }
    // "拖出全部": held and dragged, it takes every file shown here, as copies, to a folder or another application. Its icon is the
    // four arrows of moving, as the pointer shows over it; what it does is said when the pointer rests on it.
    private const string DragOutIcon = "M 8,1.5 V 14.5 M 1.5,8 H 14.5 M 6,3.5 L 8,1.5 L 10,3.5 M 6,12.5 L 8,14.5 L 10,12.5 M 3.5,6 L 1.5,8 L 3.5,10 M 12.5,6 L 14.5,8 L 12.5,10";
    private const double DragAllWidth = 24;
    private Border DragAllButton()
    {
        var glyph = MenuGlyph(DragOutIcon, UI.Secondary); glyph.Margin = new Thickness(0); glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var button = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Child = glyph, Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
        var tip = HoverTip(button, Loc.T("拖出全部文件", "Drag out all files"));
        AutomationProperties.SetName(button, Loc.T("拖出全部文件", "Drag out all files"));
        button.MouseEnter += (_, _) => { button.Background = UI.Brush(PanelTheme.Hex("hover")); glyph.Stroke = UI.Brush(UI.Primary); };
        button.MouseLeave += (_, _) => { button.Background = Brushes.Transparent; glyph.Stroke = UI.Brush(UI.Secondary); };
        // A click without a drag keeps saying what it does.
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; tip.IsOpen = true; };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        AttachFileDrag(button, () => VisibleFiles().Where(file => ShelfFiles.Exists(file.Path)).ToList());
        return button;
    }
    // A filter: its text, a dot in the category's color, and the underline when it is the one shown.
    private Border Tab(ShelfSide side, Guid? id, string text, ShelfLayout layout, bool selected, string? color = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        if (color is not null) content.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = layout.Body, Foreground = UI.Brush(selected ? UI.Primary : UI.Tertiary),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 };
        content.Children.Add(label);
        var face = new Grid();
        face.Children.Add(content);
        // The underline is in the panel theme's accent, so it follows the theme.
        if (selected) face.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = UI.Brush(UI.Accent), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 6, 0) });
        var tab = new Border
        {
            Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Child = face, Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, AllowDrop = true
        };
        AutomationProperties.SetName(tab, text);
        if (selected) AutomationProperties.SetItemStatus(tab, Loc.T("已选中", "Selected"));
        _shelfTabs.Add((tab, id, side));
        tab.MouseEnter += (_, _) => label.Foreground = UI.Brush(UI.Primary);
        tab.MouseLeave += (_, _) => { if (!selected) label.Foreground = UI.Brush(UI.Tertiary); };
        tab.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (side == ShelfSide.Clipboard) _clipTab = id; else { _fileTab = id; _picked.Clear(); }
            if (side == ShelfSide.Clipboard) _clipOffset = 0; else _fileOffset = 0;
            RenderShelf();
        };
        tab.MouseRightButtonUp += (_, e) => e.Handled = true;
        // Entries dragged from the list onto a category, and files dragged in from the computer onto a file category, go into that
        // category, staying in the others they are in. "全部" takes only files from the computer.
        tab.DragOver += (_, e) => { e.Effects = TabAccepts(side, id, e.Data) ? DragDropEffects.Copy : DragDropEffects.None; HighlightTab(tab, e.Effects != DragDropEffects.None); e.Handled = true; };
        tab.DragLeave += (_, _) => HighlightTab(tab, false);
        tab.Drop += (_, e) => { e.Handled = true; HighlightTab(tab, false); DropOnTab(side, id, e.Data); };
        return tab;
    }
    private void HighlightTab(Border tab, bool on)
    {
        var accent = PanelTheme.ColorOf("accent");
        tab.BorderThickness = new Thickness(on ? 1 : 0);
        tab.BorderBrush = on ? new SolidColorBrush(accent) : null;
        if (on) tab.Background = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B));
        else tab.Background = Brushes.Transparent;
    }
    private const string ShelfFormat = "BrimDeck.ShelfItems";
    private static bool TabAccepts(ShelfSide side, Guid? id, IDataObject data)
    {
        if (data.GetDataPresent(ShelfFormat)) return id is not null && data.GetData(ShelfFormat) is string text && text.StartsWith(side + "|", StringComparison.Ordinal);
        return side == ShelfSide.Files && data.GetDataPresent(DataFormats.FileDrop);
    }
    private void DropOnTab(ShelfSide side, Guid? id, IDataObject data)
    {
        if (data.GetDataPresent(ShelfFormat) && data.GetData(ShelfFormat) is string text && text.StartsWith(side + "|", StringComparison.Ordinal))
        {
            var ids = text[(side.ToString().Length + 1)..].Split(',').Select(part => Guid.TryParse(part, out var g) ? g : Guid.Empty).ToHashSet();
            if (id is { } category) SetCategory(side, ids, category, add: true);
            return;
        }
        if (side == ShelfSide.Files && data.GetData(DataFormats.FileDrop) is string[] paths) { EndDrop(dropped: true); AddDropped(paths, id); }
    }
    // Puts entries in a category or takes them out of it; their other categories stay.
    private void SetCategory(ShelfSide side, IEnumerable<Guid> ids, Guid category, bool add)
    {
        var set = ids.ToHashSet();
        Shelf.SetCategory(side == ShelfSide.Clipboard ? ShelfData.Clips.Where(clip => set.Contains(clip.Id)).Select(clip => clip.Categories)
            : ShelfData.Files.Where(file => set.Contains(file.Id)).Select(file => file.Categories), category, add);
        _app.Shelf.Edited();
    }
    // The categories of the entries given, one list each.
    private List<List<Guid>> CategoriesOf(ShelfSide side, IReadOnlyList<Guid> ids) => side == ShelfSide.Clipboard
        ? [.. ShelfData.Clips.Where(clip => ids.Contains(clip.Id)).Select(clip => clip.Categories)]
        : [.. ShelfData.Files.Where(file => ids.Contains(file.Id)).Select(file => file.Categories)];

    // ---- Rows ----
    private Border RowFrame(ShelfLayout layout, double height, bool picked)
    {
        var frame = new Border
        {
            Height = height, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Padding = new Thickness(8, 0, 4, 0), Margin = new Thickness(-8, 0, 0, 0),
            Background = picked ? UI.Brush(UI.FillSelected) : Brushes.Transparent, Cursor = Cursors.Hand
        };
        return frame;
    }
    private Border ClipRow(ClipEntry clip, ShelfLayout layout)
    {
        bool two = layout.TwoLines && clip.Kind is ClipKind.Text or ClipKind.Code && Shelf.LineCount(clip.Text.Trim()) > 1 || layout.TwoLines && clip.Kind == ClipKind.Image;
        double height = two ? layout.Row + (clip.Kind == ClipKind.Image ? Math.Max(0, layout.ThumbHeight + 6 - layout.Row) : layout.Body * 1.45) : layout.Row;
        var frame = RowFrame(layout, height, false);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var content = ClipContent(clip, layout, two);
        grid.Children.Add(content);
        // In "全部", the categories an entry is in, in their order: a dot in each one's color, a star for "常用", at most three (the
        // tip names them all). Under the pointer they give way, as the time does (ShowMore).
        FrameworkElement? mark = null;
        var marked = _clipTab is null ? ShelfData.ClipCategories.Where(c => clip.Categories.Contains(c.Id)).ToList() : [];
        if (marked.Count > 0)
        {
            var marks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent, ToolTip = string.Join(Loc.T("、", ", "), marked.Select(Shelf.CategoryName)) };
            foreach (var category in marked.Take(3))
            {
                FrameworkElement glyph = category.Id == ShelfData.Favorites ? StarGlyph(9, UI.Tertiary, filled: true) : new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(category.Color) };
                glyph.VerticalAlignment = VerticalAlignment.Center;
                if (marks.Children.Count > 0) glyph.Margin = new Thickness(3, 0, 0, 0);
                marks.Children.Add(glyph);
            }
            mark = marks;
            Grid.SetColumn(mark, 1); grid.Children.Add(mark);
        }
        var aux = AuxSlot(clip.Manual ? Loc.T("常用语", "Phrase") : Shelf.When(clip.Copied, DateTimeOffset.Now), layout, () => OpenDetail(ShelfSide.Clipboard, clip.Id),
            extra: clip.Kind == ClipKind.Image ? null : (MenuIcons.PlainText, Loc.T("粘贴为纯文本", "Paste as plain text"), () => PasteClip(clip, plain: true)));
        if (aux.Tag is RowEnd end) end.Mark = mark;
        Grid.SetColumn(aux, 2); grid.Children.Add(aux);
        frame.Child = grid;
        AutomationProperties.SetName(frame, clip.Kind == ClipKind.Image ? Loc.T("图片", "Picture") : Shelf.FirstLine(clip.Text).First);
        _shelfRows[clip.Id] = (frame, aux);
        AttachRow(frame, aux, ShelfSide.Clipboard, clip.Id, picked: false,
            click: () => PasteClip(clip),
            menu: () => ClipMenu(clip),
            drag: () => ClipDrag(clip));
        if (_flash is { } flash && flash.Id == clip.Id && flash.Until > Environment.TickCount64) ShowFlash(frame, aux, flash.Text);
        return frame;
    }
    private FrameworkElement ClipContent(ClipEntry clip, ShelfLayout layout, bool two)
    {
        switch (clip.Kind)
        {
            case ClipKind.Image:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var thumb = new Border { Width = layout.Thumb, Height = layout.ThumbHeight, CornerRadius = new CornerRadius(PanelTheme.Corner(3)), Background = UI.Brush(UI.Fill), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
                if (_app.Shelf.Picture(clip.Id, (int)Math.Ceiling(layout.Thumb * 2)) is { } picture)
                    thumb.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
                panel.Children.Add(thumb);
                panel.Children.Add(Label(Loc.T("图片", "Picture") + $"  {clip.PixelWidth}×{clip.PixelHeight}", layout.Aux, UI.Secondary, HorizontalAlignment.Left, left: 8));
                return panel;
            }
            case ClipKind.Color when Shelf.TryColor(clip.Text, out var c):
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                panel.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(PanelTheme.Corner(3)), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                    BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
                var text = Highlighted(clip.Text.Trim(), layout.Body, UI.Primary, mono: true); text.Margin = new Thickness(8, 0, 0, 0);
                panel.Children.Add(text);
                return panel;
            }
            case ClipKind.Link:
            {
                var (host, rest) = Shelf.SplitLink(clip.Text);
                var block = new TextBlock { FontFamily = UI.PanelFont, FontSize = layout.Body, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
                if (Searching) AddHighlighted(block.Inlines, clip.Text.Trim(), UI.Primary);
                else { block.Inlines.Add(new Run(host) { Foreground = UI.Brush(UI.Primary) }); block.Inlines.Add(new Run(rest) { Foreground = UI.Brush(UI.Tertiary) }); }
                return block;
            }
            default:
            {
                bool code = clip.Kind == ClipKind.Code;
                var (first, more) = Shelf.FirstLine(clip.Text);
                if (two)
                {
                    var lines = clip.Text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Trim().Length > 0).ToList();
                    string shown = string.Join("\n", lines.Take(2).Select(line => code ? line.Replace("\t", "  ") : line.Trim()));
                    var block = Highlighted(shown, layout.Body, UI.Primary, mono: code);
                    block.TextWrapping = TextWrapping.NoWrap;
                    if (lines.Count > 2) { block.Inlines.Add(new Run("  +" + (lines.Count - 2) + Loc.T(" 行", " lines")) { Foreground = UI.Brush(UI.Tertiary), FontSize = layout.Aux }); }
                    return block;
                }
                var line = Highlighted(code ? first : first, layout.Body, UI.Primary, mono: code);
                if (more > 0) line.Inlines.Add(new Run("  +" + more + Loc.T(" 行", more == 1 ? " line" : " lines")) { Foreground = UI.Brush(UI.Tertiary), FontSize = layout.Aux, FontFamily = UI.PanelFont });
                return line;
            }
        }
    }
    private TextBlock Highlighted(string text, double size, string color, bool mono = false)
    {
        var block = new TextBlock { FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI") : UI.PanelFont, FontSize = mono ? size - .5 : size, Foreground = UI.Brush(color),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        AddHighlighted(block.Inlines, text, color);
        return block;
    }
    // The search's matches on a blue background.
    private void AddHighlighted(InlineCollection inlines, string text, string color)
    {
        if (!Searching) { inlines.Add(new Run(text)); return; }
        var accent = PanelTheme.ColorOf("accent");
        var mark = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B));
        int at = 0;
        foreach (var (start, length) in Shelf.MatchesIn(text, _shelfQuery))
        {
            if (start > at) inlines.Add(new Run(text[at..start]));
            inlines.Add(new Run(text.Substring(start, length)) { Background = mark, Foreground = UI.Brush(UI.Primary) });
            at = start + length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
    }

    private Border FileRow(TransferFile file, ShelfLayout layout)
    {
        bool exists = ShelfFiles.Exists(file.Path), folder = exists && ShelfFiles.IsFolder(file.Path);
        var frame = RowFrame(layout, layout.Row, _picked.Contains(file.Id));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Icon + 8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Source = ShelfFiles.Icon(file.Path), Width = layout.Icon, Height = layout.Icon, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Opacity = exists ? 1 : .45 };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);
        // The facts at the right: size (or the folder's items, then its size), and the date where there is room.
        string facts = !exists ? Loc.T("已不存在", "Missing") : folder ? FolderFacts(file.Path) : ShelfFiles.FileSize(file.Path) is { } bytes ? Shelf.Size(bytes) : "";
        if (layout.FileDate && exists && ShelfFiles.Modified(file.Path) is { } modified) facts += "   " + modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool showFacts = !exists || layout.FileSize;
        double factsWidth = showFacts ? TextWidth(facts, layout.Aux, FontWeights.Normal) + 10 : 0;
        double nameRoom = Math.Max(20, layout.FileColumn - layout.Icon - 8 - 12 - Math.Max(factsWidth, 22));
        // Short of room, the name is cut at the end of its stem; an extension of up to four letters (.docx) always shows after it.
        string extension = System.IO.Path.GetExtension(file.Name);
        if (extension.Length is < 2 or > 5 || extension.Length == file.Name.Length) extension = "";
        var stem = Label(file.Name[..^extension.Length], layout.Body, UI.Primary, HorizontalAlignment.Left);
        var ext = Label(extension, layout.Body, UI.Primary, HorizontalAlignment.Left);
        if (Searching)
        {
            stem.Inlines.Clear(); AddHighlighted(stem.Inlines, file.Name[..^extension.Length], UI.Primary);
            ext.Inlines.Clear(); AddHighlighted(ext.Inlines, extension, UI.Primary);
        }
        var name = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Opacity = exists ? 1 : .45 };
        DockPanel.SetDock(ext, System.Windows.Controls.Dock.Right);
        name.Children.Add(ext); name.Children.Add(stem);
        // The whole name, after a moment, when the row shows only part of it.
        if (TextWidth(file.Name, layout.Body, FontWeights.Normal) > nameRoom) { name.ToolTip = file.Name; ToolTipService.SetInitialShowDelay(name, 600); }
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        var aux = AuxSlot(showFacts ? facts : "", layout, () => OpenDetail(ShelfSide.Files, file.Id), warn: !exists,
            extra: (BinIcon, Loc.T("从文件中转中移除", "Remove from the file transfer"), () => RemoveFiles([file])));
        Grid.SetColumn(aux, 2); grid.Children.Add(aux);
        frame.Child = grid;
        AutomationProperties.SetName(frame, file.Name);
        _shelfRows[file.Id] = (frame, aux);
        AttachRow(frame, aux, ShelfSide.Files, file.Id, picked: _picked.Contains(file.Id),
            click: () => ClickFile(file),
            menu: () => FileMenu(file),
            drag: null);
        AttachFileDrag(frame, () => _picked.Contains(file.Id) && _picked.Count > 1 ? ShelfData.Files.Where(f => _picked.Contains(f.Id) && ShelfFiles.Exists(f.Path)).ToList() : exists ? [file] : []);
        if (_flash is { } flash && flash.Id == file.Id && flash.Until > Environment.TickCount64) ShowFlash(frame, aux, flash.Text);
        return frame;
    }
    private static string FolderFacts(string path)
    {
        var stats = ShelfFiles.Folder(path);
        if (stats.Size is { } size) return Shelf.Size(size);
        return stats.Items is { } items ? Loc.T($"{items} 项", items == 1 ? "1 item" : $"{items} items") : "…";
    }

    // The time or size at the end of a row; under the pointer it gives way to "›", which shows the whole entry, and to one more
    // button at its left (extra): "粘贴为纯文本" on a text, "从文件中转中移除" on a file.
    private Grid AuxSlot(string text, ShelfLayout layout, Action open, bool warn = false, (string Icon, string Note, Action Act)? extra = null)
    {
        var slot = new Grid { VerticalAlignment = VerticalAlignment.Center, MinWidth = 22, Margin = new Thickness(8, 0, 0, 0) };
        var label = Label(text, layout.Aux, warn ? "#E5C890" : UI.Tertiary, HorizontalAlignment.Right);
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        // The settings' chevron (the icon font's, as the expandable rows there), turned to point right; no hover note.
        var chevron = SettingsWindow.Chevron(); chevron.FontSize = 11; chevron.Margin = new Thickness(0); chevron.Foreground = UI.Brush(UI.Secondary);
        chevron.RenderTransform = new RotateTransform(-90);
        // A 22 DIP face; the press is taken over the row's whole height and 36 DIP across, reaching into the row's right padding.
        // Beside "粘贴为纯文本" both press areas narrow, so the two faces stand 4 DIP apart.
        var detail = RowAction(chevron, extra is null ? 36 : 28, new Thickness(0, 0, 4, 0), layout, open);
        AutomationProperties.SetName(detail, Loc.T("查看详情", "Details"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        // The extra button, its menu icon taking the press over 26 DIP across; it shows with "›", and takes no room before.
        if (extra is { } one)
        {
            var glyph = MenuGlyph(one.Icon, UI.Secondary); glyph.Margin = new Thickness(0); glyph.HorizontalAlignment = HorizontalAlignment.Center;
            var button = RowAction(glyph, 26, new Thickness(2, 0, 2, 0), layout, one.Act);
            HoverTip(button, one.Note);
            AutomationProperties.SetName(button, one.Note);
            button.Visibility = Visibility.Collapsed;
            actions.Children.Add(button);
        }
        actions.Children.Add(detail);
        var more = new Border { Child = actions, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -4, 0), Visibility = Visibility.Hidden };
        slot.Children.Add(label); slot.Children.Add(more);
        slot.Tag = new RowEnd(label, more);
        return slot;
    }
    // A button at a row's end: a 22 DIP face (its icon centred, hover lights it) inside a press area as tall as the row.
    private static Border RowAction(FrameworkElement icon, double width, Thickness faceMargin, ShelfLayout layout, Action act)
    {
        var face = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = faceMargin, Child = icon };
        var button = new Border { Width = width, Height = layout.Row, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = face };
        void Paint(string color) { if (icon is Shape shape) shape.Stroke = UI.Brush(color); else if (icon is TextBlock text) text.Foreground = UI.Brush(color); }
        button.MouseEnter += (_, _) => { face.Background = UI.Brush(PanelTheme.Hex("pressed")); Paint(UI.Primary); };
        button.MouseLeave += (_, _) => { face.Background = Brushes.Transparent; Paint(UI.Secondary); };
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
        return button;
    }
    // A row's end: the time or size, the buttons that take its place under the pointer, and the category's mark beside it.
    private sealed class RowEnd(TextBlock label, Border more)
    {
        public TextBlock Label { get; } = label;
        public Border More { get; } = more;
        public FrameworkElement? Mark { get; set; }
    }
    private static void ShowMore(Grid aux, bool on)
    {
        if (aux.Tag is not RowEnd end) return;
        end.Label.Visibility = on ? Visibility.Hidden : Visibility.Visible;
        end.More.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        if (end.Mark is not null) end.Mark.Visibility = on ? Visibility.Hidden : Visibility.Visible;
        // The extra button takes its room only while it shows, so the row keeps its text's width the rest of the time.
        if (end.More.Child is StackPanel { Children.Count: > 1 } actions) actions.Children[0].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // Hover lights the row and shows "›"; press darkens it; a click (a press and release without moving away) does the row's action.
    private void AttachRow(Border frame, Grid aux, ShelfSide side, Guid id, bool picked, Action click, Func<ContextMenu> menu, Func<DataObject>? drag)
    {
        Brush rest = picked ? UI.Brush(UI.FillSelected) : Brushes.Transparent;
        Point? pressed = null;
        bool flashing() => _flash is { } flash && flash.Id == id && flash.Until > Environment.TickCount64;
        // While the row's own menu is open the row stays lit, though the pointer is on the menu.
        bool held = false;
        frame.MouseEnter += (_, _) => { if (!flashing()) frame.Background = picked ? rest : UI.Brush(PanelTheme.Hex("hover")); if (!flashing()) ShowMore(aux, true); };
        frame.MouseLeave += (_, _) => { pressed = null; if (!flashing() && !held) { frame.Background = rest; ShowMore(aux, false); } };
        frame.MouseLeftButtonDown += (_, e) => { pressed = e.GetPosition(frame); if (!flashing()) frame.Background = UI.Brush(PanelTheme.Hex("pressed")); e.Handled = true; };
        frame.MouseLeftButtonUp += (_, e) =>
        {
            if (pressed is null) return;
            pressed = null; e.Handled = true;
            if (!flashing()) frame.Background = picked ? rest : UI.Brush(PanelTheme.Hex("hover"));
            click();
        };
        frame.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var opened = menu();
            held = true;
            opened.Closed += (_, _) => { held = false; if (!frame.IsMouseOver && !flashing()) { frame.Background = rest; ShowMore(aux, false); } };
            OpenMenu(opened, frame, atPointer: true);
        };
        if (drag is not null)
            frame.MouseMove += (_, e) =>
            {
                if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
                var now = e.GetPosition(frame);
                if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressed = null; frame.Background = rest;
                var data = drag();
                data.SetData(ShelfFormat, side + "|" + id);
                DragOut(frame, data);
            };
    }
    private void AttachFileDrag(FrameworkElement source, Func<List<TransferFile>> files)
    {
        Point? start = null;
        source.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(source);
        source.PreviewMouseLeftButtonUp += (_, _) => start = null;
        source.MouseLeave += (_, e) => { if (e.LeftButton != MouseButtonState.Pressed) start = null; };
        source.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(source);
            if (Math.Abs(now.X - from.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            var list = files();
            if (list.Count == 0) return;
            bool move = Settings.TransferDragOut == TransferDragOut.Move;
            var data = ShelfService.FileData(list.Select(file => file.Path), move);
            data.SetData(ShelfFormat, ShelfSide.Files + "|" + string.Join(",", list.Select(file => file.Id)));
            if (source is Border row) row.Background = _picked.Count > 0 && list.Count > 1 ? UI.Brush(UI.FillSelected) : Brushes.Transparent;
            var effect = DragOut(source, data, move ? DragDropEffects.Copy | DragDropEffects.Move : DragDropEffects.Copy);
            if (move && effect == DragDropEffects.Move) WatchMoved(list);
            e.Handled = true;
        };
    }
    // Dragged out of the panel, clipboard entries are copied; files are copied, or, when the drag is set to move, moved where the
    // target takes moves (a chat window or a browser takes only copies).
    private DragDropEffects DragOut(DependencyObject source, DataObject data, DragDropEffects allowed = DragDropEffects.Copy)
    {
        _draggingOut = true;
        try { return DragDrop.DoDragDrop(source, data, allowed); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { return DragDropEffects.None; }
        finally
        {
            _draggingOut = false;
            if (_expanded && !_pointerInside) PointerChanged(false);
        }
    }
    // Missing is not evidence of a move by itself. Only an explicit Move result is watched; targets such as Explorer that
    // report no result keep their records. A disconnected share or unreadable parent also keeps its record and categories.
    private sealed record MovingFile(string Path, long Until, DateTimeOffset Added);
    private readonly Dictionary<Guid, MovingFile> _movingOut = [];
    private DispatcherTimer? _movingTimer;
    private bool _shelfClosed, _checkingMoved;
    private int _movingCursor;
    private void WatchMoved(IReadOnlyList<TransferFile> files)
    {
        if (_shelfClosed) return;
        long until = Environment.TickCount64 + 5 * 60_000;
        foreach (var file in files) _movingOut[file.Id] = new(file.Path, until, file.Added);
        CheckMoved(null, EventArgs.Empty);
        if (_movingOut.Count == 0) return;
        _movingTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _movingTimer.Tick -= CheckMoved; _movingTimer.Tick += CheckMoved;
        _movingTimer.Start();
    }
    private async void CheckMoved(object? sender, EventArgs e)
    {
        if (_shelfClosed) return;
        long now = Environment.TickCount64;
        var current = ShelfData.Files.ToDictionary(file => file.Id);
        foreach (var (id, watch) in _movingOut.ToArray())
            if (now >= watch.Until || !current.TryGetValue(id, out var file) || file.Path != watch.Path || file.Added != watch.Added)
                _movingOut.Remove(id);
        if (_movingOut.Count == 0) { _movingTimer?.Stop(); return; }
        // One batch per window and one filesystem operation process-wide. A blocked network call keeps its slot, so later
        // ticks (and other panels) never queue more tasks behind it. Batches are bounded and rotated for fairness.
        if (_checkingMoved) return;
        var pending = _movingOut.ToArray();
        int start = _movingCursor % pending.Length, count = Math.Min(32, pending.Length);
        var batch = Enumerable.Range(0, count).Select(i => pending[(start + i) % pending.Length]).ToArray();
        var probe = ShelfFiles.TryProbeMoved(batch.Select(pair => pair.Value.Path).ToArray());
        if (probe is null) return;
        _movingCursor = (start + count) % pending.Length;
        _checkingMoved = true;
        try
        {
            var states = await probe;
            if (_shelfClosed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            now = Environment.TickCount64;
            current = ShelfData.Files.ToDictionary(file => file.Id);
            var gone = new List<TransferFile>();
            for (int i = 0; i < batch.Length; i++)
            {
                var (id, watch) = batch[i];
                if (!_movingOut.TryGetValue(id, out var live) || !ReferenceEquals(live, watch) || now >= watch.Until
                    || !current.TryGetValue(id, out var file) || file.Path != watch.Path || file.Added != watch.Added) continue;
                if (states[i] != ShelfFiles.PathState.ConfirmedMissing) continue;
                _movingOut.Remove(id);
                ShelfFiles.Forget(file.Path);
                gone.Add(file);
            }
            if (gone.Count > 0) RemoveFiles(gone);
            if (_movingOut.Count == 0) _movingTimer?.Stop();
        }
        catch (Exception) { /* A failed probe retains its records; async timer callbacks must not fault the dispatcher. */ }
        finally { _checkingMoved = false; }
    }
    // A picture is dragged as a file as well (ShelfService.PictureFile), which is how browsers and Electron applications take one.
    private DataObject ClipDrag(ClipEntry clip)
    {
        var data = _app.Shelf.ClipData(clip);
        if (_app.Shelf.PictureFile(clip) is { } path) data.SetFileDropList([path]);
        return data;
    }

    // ---- Actions ----
    private void PasteClip(ClipEntry clip, bool plain = false)
        => _app.Shelf.Paste(_app.Shelf.ClipData(clip, plain), done => Flash(clip.Id, done ? Loc.T("已粘贴", "Pasted") : Loc.T("已复制", "Copied")));
    // Copied again, the entry moves to the top as any new copy does, and the list shows it there.
    private void CopyClip(ClipEntry clip)
    {
        if (!_app.Shelf.Put(_app.Shelf.ClipData(clip))) return;
        Shelf.Recopied(ShelfData, clip, DateTimeOffset.Now);
        _clipOffset = 0;
        Flash(clip.Id, Loc.T("已复制", "Copied"));
        _app.Shelf.Edited();
    }
    private void ClickFile(TransferFile file)
    {
        bool ctrl = PasteInput.KeyDown(0x11), shift = PasteInput.KeyDown(0x10);
        if (ctrl || shift)
        {
            // Ctrl adds or removes one file; Shift takes every file from the last one chosen to this one.
            var visible = VisibleFiles();
            if (shift && _pickAnchor is { } anchor && visible.FindIndex(f => f.Id == anchor) is var from and >= 0)
            {
                int to = visible.FindIndex(f => f.Id == file.Id);
                if (!ctrl) _picked.Clear();
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) _picked.Add(visible[i].Id);
            }
            else if (!_picked.Remove(file.Id)) { _picked.Add(file.Id); _pickAnchor = file.Id; }
            RenderShelf();
            return;
        }
        if (_picked.Count > 0) { _picked.Clear(); RenderShelf(); }
        _pickAnchor = file.Id;
        if (!ShelfFiles.Exists(file.Path)) return;
        PasteFiles([file]);
    }
    private void PasteFiles(IReadOnlyList<TransferFile> files)
    {
        var present = files.Where(file => ShelfFiles.Exists(file.Path)).ToList();
        if (present.Count == 0) return;
        _app.Shelf.Paste(ShelfService.FileData(present.Select(file => file.Path)), done => { foreach (var file in present) Flash(file.Id, done ? Loc.T("已粘贴", "Pasted") : Loc.T("已复制", "Copied")); });
    }
    private void Flash(Guid id, string text)
    {
        _flash = (id, text, Environment.TickCount64 + 1200);
        if (_shelfRows.TryGetValue(id, out var row)) ShowFlash(row.Row, row.Aux, text);
        _flashTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(1250) };
        _flashTimer.Tick -= FlashEnded; _flashTimer.Tick += FlashEnded;
        _flashTimer.Stop(); _flashTimer.Start();
    }
    private void FlashEnded(object? sender, EventArgs e)
    {
        _flashTimer?.Stop();
        if (_flash is not { } flash) return;
        _flash = null;
        if (_shelfRows.TryGetValue(flash.Id, out var row))
        {
            bool over = row.Row.IsMouseOver;
            row.Row.Background = _picked.Contains(flash.Id) ? UI.Brush(UI.FillSelected) : over ? UI.Brush(PanelTheme.Hex("hover")) : Brushes.Transparent;
            if (row.Aux.Tag is RowEnd { Label: var label } && label.Tag is string original) { label.Text = original; label.Tag = null; label.Foreground = UI.Brush(UI.Tertiary); label.Margin = new Thickness(0); }
            ShowMore(row.Aux, over);
        }
    }
    private void ShowFlash(Border row, Grid aux, string text)
    {
        var accent = PanelTheme.ColorOf("accent");
        row.Background = new SolidColorBrush(Color.FromArgb(51, accent.R, accent.G, accent.B));
        if (aux.Tag is RowEnd end)
        {
            end.Label.Tag ??= end.Label.Text;
            // On the tinted row the word keeps as far from the right edge as the entry does from the left (8 DIP: the row's right
            // padding is 4).
            end.Label.Text = text; end.Label.Foreground = UI.Brush(UI.Primary); end.Label.Margin = new Thickness(0, 0, 4, 0);
            end.Label.Visibility = Visibility.Visible; end.More.Visibility = Visibility.Hidden;
        }
    }

    private static void OpenFile(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException) { }
    }
    private static void ShowInFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ---- Files dragged in ----
    // Into the collapsed island the panel opens on this page; into the panel on another page it turns to this page. The file transfer
    // column lights up; over a file category, that category does too. Taken away without dropping, the panel goes back.
    private void ShelfDragEnter(object sender, DragEventArgs e)
    {
        _dropLeave?.Stop();
        if (_draggingOut || !Settings.ClipboardPage || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy; e.Handled = true;
        if (_dropping) return;
        _dropping = true;
        _shelfDetail = null; _shelfEdit = null; _shelfSearching = false; _shelfQuery = "";
        if (!_expanded)
        {
            _dropOpened = true; _dropReturn = null;
            _hover.Stop();
            if (!IsShelfPage) SelectPage(DeckPage.Clipboard, remember: false);
            SetExpanded(true);
        }
        else
        {
            _dropOpened = false;
            _dropReturn = IsShelfPage ? null : CurrentPage;
            if (!IsShelfPage) SelectPage(DeckPage.Clipboard, remember: false);
        }
        RenderShelf();
    }
    private void ShelfDragOver(object sender, DragEventArgs e)
    {
        _dropLeave?.Stop();
        if (_draggingOut || !_dropping) return;
        e.Effects = DragDropEffects.Copy;
        // A file category under the pointer lights up as well (Tab's own DragOver); elsewhere the whole column takes the files.
        if (e.OriginalSource is DependencyObject over && _shelfTabs.Any(tab => tab.Side == ShelfSide.Files && tab.Tab.IsAncestorOf(over))) return;
        e.Handled = true;
    }
    private void ShelfDragLeave(object sender, DragEventArgs e)
    {
        if (!_dropping) return;
        // Leave also fires when the pointer moves between parts of the panel; it counts only if no part takes the drag right after.
        _dropLeave ??= new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _dropLeave.Tick -= DropLeft; _dropLeave.Tick += DropLeft;
        _dropLeave.Stop(); _dropLeave.Start();
    }
    private void DropLeft(object? sender, EventArgs e) => EndDrop(dropped: false);
    private void ShelfDrop(object sender, DragEventArgs e)
    {
        _dropLeave?.Stop();
        if (_draggingOut || !_dropping) return;
        if (e.OriginalSource is DependencyObject over && _shelfTabs.FirstOrDefault(tab => tab.Side == ShelfSide.Files && tab.Tab.IsAncestorOf(over)) is { Tab: not null } tab)
        { HighlightTab(tab.Tab, false); EndDrop(dropped: true); AddDropped(e.Data.GetData(DataFormats.FileDrop) as string[] ?? [], tab.Id); e.Handled = true; return; }
        e.Handled = true;
        EndDrop(dropped: true);
        AddDropped(e.Data.GetData(DataFormats.FileDrop) as string[] ?? [], null);
    }
    private void AddDropped(string[] paths, Guid? category)
    {
        foreach (var path in paths) ShelfFiles.Forget(path);
        if (paths.Length == 0) return;
        _fileTab = category; _fileOffset = 0; _picked.Clear();
        _app.Shelf.AddFiles(paths, category);
    }
    private void EndDrop(bool dropped)
    {
        _dropLeave?.Stop();
        if (!_dropping) return;
        _dropping = false;
        if (!dropped)
        {
            if (_dropOpened) { _dropOpened = false; if (!_pointerInside) SetExpanded(false); }
            else if (_dropReturn is { } back) SelectPage(back, remember: false);
            _dropReturn = null;
        }
        _dropOpened = false; _dropReturn = null;
        if (IsShelfPage && _expanded) RenderShelf();
    }

    // ---- Small parts ----
    private static TextBlock Label(string text, double size, string color, HorizontalAlignment align, double top = 0, double left = 0) => new()
    {
        Text = text, FontFamily = UI.PanelFont, FontSize = size, Foreground = UI.Brush(color), HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(left, top, 0, 0)
    };
    private double TextWidth(string text, double size, FontWeight weight, FontFamily? font = null)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(font ?? UI.PanelFont, FontStyles.Normal, weight, FontStretches.Normal),
            size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }
    // "常用"'s star: outlined in the menus, filled where it marks an entry.
    private const string StarData = "M 8,1.8 L 9.9,5.7 L 14.2,6.3 L 11.1,9.3 L 11.8,13.6 L 8,11.6 L 4.2,13.6 L 4.9,9.3 L 1.8,6.3 L 6.1,5.7 Z";
    private static Path StarGlyph(double size, string color, bool filled) => new()
    {
        Data = Geometry.Parse(StarData), Stroke = UI.Brush(color), StrokeThickness = 1.3, StrokeLineJoin = PenLineJoin.Round, Fill = filled ? UI.Brush(color) : null,
        Stretch = Stretch.Uniform, Width = size, Height = size
    };
    // A capsule button: 26 high, corner 13, 12 px text; the main action is white with black text, the others 12 % white.
    private Button PillButton(string text, bool primary, Action click)
    {
        var label = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = 12, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = primary ? UI.Brush(PanelTheme.Hex("surface")) : UI.Brush(UI.Primary), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var face = new Border { Height = 26, CornerRadius = new CornerRadius(PanelTheme.Corner(13)), Padding = new Thickness(12, 0, 12, 0), Child = label,
            Background = primary ? UI.Brush(UI.Primary) : UI.Brush(UI.FillSelected) };
        var button = new Button { Content = face, Cursor = Cursors.Hand, Focusable = false, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        button.MouseEnter += (_, _) => face.Opacity = .86;
        button.MouseLeave += (_, _) => face.Opacity = 1;
        button.Click += (_, e) => { e.Handled = true; click(); };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        AutomationProperties.SetName(button, text);
        return button;
    }
    private DeckSettings SettingsWith(Action<DeckSettings> change)
    {
        var copy = Settings.Copy();
        change(copy);
        return copy;
    }
}

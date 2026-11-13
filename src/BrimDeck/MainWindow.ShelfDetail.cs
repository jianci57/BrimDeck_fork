using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Native;
using Path = System.Windows.Shapes.Path;

namespace BrimDeck;

// The clipboard page's full-content view, its header (back, position, previous and next; or the search box), typing (the search,
// editing a text entry) and its menus. Typing needs the keyboard, so the panel takes the focus while the user types and gives it
// back to the window it came from (ShelfService.TypingTarget), where a paste then goes.
public partial class MainWindow
{
    private bool _editCommitting;

    // ---- Header ----
    private void RenderShelfToolbar(double width)
    {
        ShelfToolbar.Children.Clear();
        ShelfToolbar.Visibility = Visibility.Visible;
        double right = _header.RightMargin + PanelHeader.SettingsWidth + 6;
        if (_shelfDetail is { } detail)
        {
            ClockLabel.Visibility = Visibility.Collapsed; ShelfSearchButton.Visibility = Visibility.Collapsed;
            var list = DetailList(detail.Side);
            int at = list.IndexOf(detail.Id);
            var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(_header.Side - 6, 0, 0, 0) };
            var back = ToolbarGlyph("M 5,0 L 0,5 L 5,10", Loc.T("返回", "Back"), CloseDetail);
            left.Children.Add(back);
            left.Children.Add(Label(DetailKind(detail.Side, detail.Id), 13, UI.Primary, HorizontalAlignment.Left, left: 4));
            if (at >= 0) left.Children.Add(Label($"{at + 1} / {list.Count}", 11.5, UI.Tertiary, HorizontalAlignment.Left, left: 8));
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, right, 0) };
            var up = ToolbarGlyph("M 0,5 L 5,0 L 10,5", Loc.T("上一条", "Previous"), () => StepDetail(-1));
            var down = ToolbarGlyph("M 0,0 L 5,5 L 10,0", Loc.T("下一条", "Next"), () => StepDetail(1));
            up.IsEnabled = at > 0; down.IsEnabled = at >= 0 && at < list.Count - 1;
            arrows.Children.Add(up); arrows.Children.Add(down);
            ShelfToolbar.Children.Add(left); ShelfToolbar.Children.Add(arrows);
            return;
        }
        if (_shelfSearching)
        {
            ShelfSearchButton.Visibility = Visibility.Collapsed;
            // The box takes the room right of the clock, up to its usual width (ShelfLayout.SearchWidth); only when that room is under
            // 140 does it keep 140 and cover the clock.
            double clock = ClockLabel.Text.Length > 0 ? TextWidth(ClockLabel.Text, 12, FontWeights.Normal) : 34;
            double room = width - right - (width + clock) / 2 - 12;
            double boxWidth = Math.Min(ShelfLayout.SearchWidth(width), Math.Max(room, 140)), height = _header.SwitchHeight;
            var box = _searchBox ??= NewSearchBox();
            (box.Parent as Panel)?.Children.Remove(box);
            var host = new Grid { Width = boxWidth, Height = height, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, right, 0) };
            box.FontSize = 12.5; box.Padding = new Thickness(26, 0, 28, 0); box.VerticalContentAlignment = VerticalAlignment.Center;
            host.Children.Add(box);
            // "×" at the right: it empties the box, or closes the search when the box is already empty.
            // A plain cross, drawn in a 20 DIP square about its middle (10, 10) so it sits centered; a cross laid out by its own bounds
            // is pushed off by its pen. The square is what takes the press.
            var cross = new Path { Data = Geometry.Parse("M 6.5,6.5 L 13.5,13.5 M 13.5,6.5 L 6.5,13.5"), Stroke = UI.Brush(UI.Tertiary), StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            var clear = new Border { Width = 20, Height = 20, Background = Brushes.Transparent, Child = cross, Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            AutomationProperties.SetName(clear, Loc.T("清空搜索", "Clear search"));
            clear.MouseEnter += (_, _) => cross.Stroke = UI.Brush(UI.Primary);
            clear.MouseLeave += (_, _) => cross.Stroke = UI.Brush(UI.Tertiary);
            clear.MouseLeftButtonDown += (_, e) => e.Handled = true;
            clear.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (box.Text.Length == 0) { CloseSearch(); return; }
                box.Text = ""; box.Focus();
            };
            host.Children.Add(clear);
            host.Children.Add(new Path { Data = Geometry.Parse("M 10.5,3.5 A 7,7 0 1 1 10.5,17.5 A 7,7 0 1 1 10.5,3.5 Z M 15.6,15.6 L 21,21"), Stroke = UI.Brush(UI.Tertiary), StrokeThickness = 2,
                Stretch = Stretch.Uniform, Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0), IsHitTestVisible = false });
            // The text starts 28 in (the box's padding 26 and the text box's own 2), where the caret blinks; the hint starts just after
            // it, so the caret does not cross its first letter.
            var hint = Label(Loc.T("搜索剪贴板与文件", "Search clipboard and files"), 12.5, UI.Tertiary, HorizontalAlignment.Left, left: 30);
            hint.IsHitTestVisible = false; hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; hint.Tag = "hint";
            host.Children.Add(hint);
            ShelfToolbar.Children.Add(host);
            // The clock is hidden only when the box reaches it.
            ClockLabel.Visibility = boxWidth > room ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        ShelfSearchButton.Visibility = Visibility.Visible;
        ClockLabel.Visibility = Visibility.Visible;
    }
    private Button ToolbarGlyph(string data, string name, Action click)
    {
        var path = new Path { Data = Geometry.Parse(data), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var button = new Button { Style = (Style)FindResource("PanelIconButton"), Width = 28, Height = _header.ButtonHeight, Content = path, ToolTip = name, Margin = new Thickness(1, 0, 0, 0) };
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : .35;
        AutomationProperties.SetName(button, name);
        button.Click += (_, e) => { e.Handled = true; click(); };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        return button;
    }
    private TextBox NewSearchBox()
    {
        var box = new TextBox { Style = (Style)FindResource("ShelfTextBox") };
        AutomationProperties.SetName(box, Loc.T("搜索剪贴板与文件", "Search clipboard and files"));
        TakeKeyboardOnClick(box);
        box.TextChanged += (_, _) =>
        {
            _shelfQuery = box.Text;
            if (box.Parent is Grid host) foreach (var hint in host.Children.OfType<TextBlock>().Where(t => Equals(t.Tag, "hint"))) hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_shelfSearching && IsShelfPage) RenderShelf();
        };
        return box;
    }
    private void ShelfSearch_Click(object sender, RoutedEventArgs e)
    {
        _shelfSearching = true; _shelfDetail = null; _shelfQuery = _searchBox?.Text ?? "";
        RenderShelf();
        BeginTyping(_searchBox);
    }
    private void CloseSearch()
    {
        _shelfSearching = false; _shelfQuery = "";
        if (_searchBox is not null) _searchBox.Text = "";
        EndTyping();
        RenderShelf();
    }

    // Esc leaves the menu, the typing, the search or the details, in that order, before it closes the panel.
    internal bool ShelfEscape()
    {
        if (!_expanded || !IsShelfPage) return false;
        if (_shelfMenu?.IsOpen == true) { _shelfMenu.IsOpen = false; return true; }
        if (_shelfEdit is not null) { _shelfEdit = null; EndTyping(); RenderShelf(); return true; }
        if (_shelfSearching) { CloseSearch(); return true; }
        if (_shelfDetail is not null) { CloseDetail(); return true; }
        return false;
    }
    // Closing the panel ends the search, the typing and the details; the chosen categories stay. The page is built again at the next
    // opening only when one of them was ended: a page closed as it was built opens as it is.
    private void ResetShelf()
    {
        bool ended = _shelfEdit is not null || _shelfDetail is not null || _shelfSearching || _shelfQuery.Length > 0 || _picked.Count > 0;
        if (_shelfEdit is not null || _shelfSearching) EndTyping();
        _shelfEdit = null; _shelfDetail = null; _shelfSearching = false; _shelfQuery = ""; _picked.Clear();
        if (_searchBox is not null) _searchBox.Text = "";
        _shelfMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        if (ended && IsShelfPage) _shelfStale = true;
    }

    // ---- Typing ----
    private void BeginTyping(Control? box)
    {
        if (box is null) return;
        var foreground = PasteInput.Foreground;
        if (PasteInput.IsOther(foreground)) _app.Shelf.TypingTarget = foreground;
        Activate();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { box.Focus(); Keyboard.Focus(box); if (box is TextBox text) text.CaretIndex = text.Text.Length; });
    }
    // The panel opens without taking the keyboard, so a box that lost it (the panel closed and opened again, or another window was
    // used) takes it back when it is clicked.
    private void TakeKeyboardOnClick(TextBox box) => box.PreviewMouseLeftButtonDown += (_, _) => { if (!IsActive || !box.IsKeyboardFocusWithin) BeginTyping(box); };
    private void EndTyping()
    {
        if (IsActive && PasteInput.IsOther(_app.Shelf.TypingTarget)) PasteInput.Activate(_app.Shelf.TypingTarget);
    }
    private void RefocusTyping()
    {
        if (!IsActive) return;
        Control? box = _shelfEdit is not null ? _editBox : _shelfSearching ? _searchBox : null;
        if (box is not null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { if (box.IsVisible) { box.Focus(); Keyboard.Focus(box); } });
    }
    private void BeginEdit(Guid id)
    {
        _shelfEdit = id; _shelfDetail = null;
        _editBox = NewEditBox();
        _editBox.Text = ShelfData.Clips.FirstOrDefault(c => c.Id == id)?.Text ?? "";
        RenderShelf();
        BeginTyping(_editBox);
    }
    private TextBox NewEditBox()
    {
        var box = new TextBox { Style = (Style)FindResource("ShelfTextBox"), AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6), VerticalContentAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(box, Loc.T("文字内容", "Text"));
        TakeKeyboardOnClick(box);
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; CommitText(); } };
        return box;
    }
    // A text entry edited in place of the list. Ctrl+Enter saves, Esc cancels. New phrases are written in the settings.
    private FrameworkElement TextEditor(ShelfLayout layout)
    {
        var box = _editBox ??= NewEditBox();
        (box.Parent as Panel)?.Children.Remove(box);
        box.FontSize = layout.Body;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(box);
        var bar = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(Label(Loc.T("编辑 · Ctrl+Enter 保存 · Esc 取消", "Edit · Ctrl+Enter saves · Esc cancels"), layout.Aux, UI.Tertiary, HorizontalAlignment.Left));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = PillButton(Loc.T("取消", "Cancel"), false, () => { _shelfEdit = null; EndTyping(); RenderShelf(); });
        var save = PillButton(Loc.T("保存", "Save"), true, CommitText); save.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        Grid.SetColumn(buttons, 1); bar.Children.Add(buttons);
        Grid.SetRow(bar, 1); grid.Children.Add(bar);
        return grid;
    }
    private void CommitText()
    {
        if (_shelfEdit is not { } id || _editBox is null) return;
        string text = _editBox.Text;
        _editCommitting = true;
        _shelfEdit = null;
        if (text.Trim().Length > 0 && ShelfData.Clips.FirstOrDefault(c => c.Id == id) is { } clip) { clip.Text = text; clip.Kind = Shelf.Classify(text); clip.Html = ""; clip.Rtf = ""; }
        EndTyping();
        _editCommitting = false;
        _app.Shelf.Edited();
        RenderShelf();
    }

    // ---- The full-content view ----
    private List<Guid> DetailList(ShelfSide side) => side == ShelfSide.Clipboard ? VisibleClips().Select(c => c.Id).ToList() : VisibleFiles().Select(f => f.Id).ToList();
    private void OpenDetail(ShelfSide side, Guid id) { CloseMenu(); _shelfDetail = (side, id); RenderShelf(); }
    private void CloseDetail() { _shelfDetail = null; RenderShelf(); }
    private void StepDetail(int step)
    {
        if (_shelfDetail is not { } detail) return;
        var list = DetailList(detail.Side);
        int at = list.IndexOf(detail.Id) + step;
        if (at < 0 || at >= list.Count) return;
        _shelfDetail = (detail.Side, list[at]); RenderShelf();
    }
    private string DetailKind(ShelfSide side, Guid id)
    {
        if (side == ShelfSide.Files) return Loc.T("文件详情", "File details");
        return (Find(side, id) as ClipEntry)?.Kind switch
        {
            ClipKind.Link => Loc.T("链接", "Link"), ClipKind.Image => Loc.T("图片", "Picture"), ClipKind.Code => Loc.T("代码", "Code"), ClipKind.Color => Loc.T("颜色", "Color"), _ => Loc.T("文本", "Text")
        };
    }

    private FrameworkElement DetailView(ShelfSide side, Guid id, double width, ShelfLayout layout)
    {
        bool wide = ShelfLayout.DetailSide(ContentCanvas.Width);
        var grid = new Grid();
        FrameworkElement content = side == ShelfSide.Clipboard ? ClipDetail((ClipEntry)Find(side, id)!, layout) : FileDetail((TransferFile)Find(side, id)!, layout);
        var facts = side == ShelfSide.Clipboard ? ClipFacts((ClipEntry)Find(side, id)!) : FileFacts((TransferFile)Find(side, id)!);
        var actions = side == ShelfSide.Clipboard ? ClipActions((ClipEntry)Find(side, id)!) : FileActions((TransferFile)Find(side, id)!);
        if (wide)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Gap) });
            // 200 wide, or as wide as its buttons when they need more (a file's three), so the last is not cut off.
            actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(200, Math.Ceiling(actions.DesiredSize.Width) + 2)) });
            grid.Children.Add(content);
            // The page's own divider between the content and its facts, as between the two columns.
            var divider = new Border { Width = 1, Background = UI.Brush(UI.Hairline), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetColumn(divider, 1); grid.Children.Add(divider);
            var info = new DockPanel { LastChildFill = false };
            actions.Margin = new Thickness(0, 8, 0, 0);
            DockPanel.SetDock(actions, System.Windows.Controls.Dock.Bottom); info.Children.Add(actions);
            var list = new StackPanel();
            foreach (var (label, value, change) in facts)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, layout.Tier == ShelfTier.Compact ? 2 : 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(Label(label, layout.Aux, UI.Tertiary, HorizontalAlignment.Left));
                FrameworkElement shown = Label(value, layout.Aux, UI.Secondary, HorizontalAlignment.Left);
                if (change is not null) shown = LinkText(value, layout.Aux, change);
                Grid.SetColumn(shown, 1); row.Children.Add(shown);
                list.Children.Add(row);
            }
            DockPanel.SetDock(list, System.Windows.Controls.Dock.Top); info.Children.Add(list);
            Grid.SetColumn(info, 2); grid.Children.Add(info);
            return grid;
        }
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(content);
        var bottom = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition()); bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bool first = true;
        foreach (var (_, value, change) in facts)
        {
            if (value.Length == 0) continue;
            if (!first) line.Children.Add(Label("  ·  ", layout.Aux, UI.Tertiary, HorizontalAlignment.Left));
            line.Children.Add(change is not null ? LinkText(value, layout.Aux, change) : Label(value, layout.Aux, UI.Tertiary, HorizontalAlignment.Left));
            first = false;
        }
        bottom.Children.Add(line);
        Grid.SetColumn(actions, 1); bottom.Children.Add(actions);
        Grid.SetRow(bottom, 1); grid.Children.Add(bottom);
        return grid;
    }
    // The value and the settings window's chevron, which is centered on the text where the "⌄" character sat low.
    private FrameworkElement LinkText(string text, double size, Action<FrameworkElement> click)
    {
        var label = Label(text, size, UI.Secondary, HorizontalAlignment.Left);
        var chevron = SettingsWindow.Chevron();
        chevron.FontSize = size; chevron.Foreground = UI.Brush(UI.Secondary);
        chevron.Margin = new Thickness(4, SettingsWindow.ChevronDrop.Top, 0, SettingsWindow.ChevronDrop.Bottom);
        var link = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        link.Children.Add(label); link.Children.Add(chevron);
        void Paint(string color) { label.Foreground = UI.Brush(color); chevron.Foreground = UI.Brush(color); }
        link.MouseEnter += (_, _) => Paint(UI.Primary);
        link.MouseLeave += (_, _) => Paint(UI.Secondary);
        link.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(link); };
        return link;
    }
    // A read-only text box, so the text can be selected; the whole entry is pasted with the button.
    private static TextBox ReadOnlyText(string text, double size, FontFamily font) => new()
    {
        Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = UI.Brush(UI.Primary), CaretBrush = Brushes.Transparent,
        FontFamily = font, FontSize = size, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0), SelectionBrush = UI.Brush(UI.Accent), FocusVisualStyle = null,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private FrameworkElement ClipDetail(ClipEntry clip, ShelfLayout layout)
    {
        double size = layout.DetailBody;
        switch (clip.Kind)
        {
            case ClipKind.Image:
            {
                var image = new Image { Source = _app.Shelf.Picture(clip.Id), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                return new Border { Child = image, Margin = new Thickness(0, 2, 0, 2) };
            }
            case ClipKind.Color when Shelf.TryColor(clip.Text, out var c):
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                double swatch = Math.Min(96, Math.Max(48, ContentCanvas.Height - _header.Row - 60));
                panel.Children.Add(new Border { Width = swatch, Height = swatch, CornerRadius = new CornerRadius(PanelTheme.Corner(10)), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                    BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(1) });
                var forms = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var (hex, rgb, hsl) = Shelf.ColorForms(c);
                foreach (var (name, value) in new[] { ("HEX", hex), ("RGB", rgb), ("HSL", hsl) })
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = Loc.T("单击粘贴这种写法", "Click to paste this form") };
                    row.Children.Add(new TextBlock { Text = name, Width = 40, FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = UI.Brush(UI.Tertiary), VerticalAlignment = VerticalAlignment.Center });
                    var text = new TextBlock { Text = value, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = size - 1, Foreground = UI.Brush(UI.Primary), VerticalAlignment = VerticalAlignment.Center };
                    row.Children.Add(text);
                    row.MouseEnter += (_, _) => text.Foreground = UI.Brush(UI.Accent);
                    row.MouseLeave += (_, _) => text.Foreground = UI.Brush(UI.Primary);
                    row.MouseLeftButtonUp += (_, e) => { e.Handled = true; _app.Shelf.Paste(ShelfService.TextData(value), done => text.Text = value + (done ? Loc.T("  已粘贴", "  pasted") : Loc.T("  已复制", "  copied"))); };
                    forms.Children.Add(row);
                }
                panel.Children.Add(forms);
                return panel;
            }
            case ClipKind.Link:
            {
                var (host, _) = Shelf.SplitLink(clip.Text);
                var panel = new StackPanel();
                panel.Children.Add(Label(host, size + 2, UI.Primary, HorizontalAlignment.Left));
                var url = ReadOnlyText(clip.Text.Trim(), size - 1, UI.PanelFont); url.Foreground = UI.Brush(UI.Secondary); url.Margin = new Thickness(0, 6, 0, 10);
                panel.Children.Add(url);
                var open = PillButton(Loc.T("在浏览器中打开", "Open in browser"), false, () => { if (Shelf.IsLink(clip.Text.Trim())) OpenFile(clip.Text.Trim()); });
                open.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(open);
                return PassWheel(new ScrollViewer { Content = panel, Focusable = false });
            }
            case ClipKind.Code:
            {
                var lines = clip.Text.Replace("\r\n", "\n").Split('\n');
                var font = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
                var numbers = new TextBlock { Text = string.Join("\n", Enumerable.Range(1, lines.Length)), FontFamily = font, FontSize = size - 1.5, Foreground = UI.Brush(UI.Tertiary),
                    TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 12, 0) };
                var code = ReadOnlyText(string.Join("\n", lines), size - 1.5, font); code.TextWrapping = TextWrapping.NoWrap;
                Grid.SetColumn(code, 1); grid.Children.Add(numbers); grid.Children.Add(code);
                return PassWheel(new ScrollViewer { Content = grid, Focusable = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            }
            default:
                return PassWheel(new ScrollViewer { Content = ReadOnlyText(clip.Text, size, UI.PanelFont), Focusable = false });
        }
    }
    private FrameworkElement FileDetail(TransferFile file, ShelfLayout layout)
    {
        bool exists = ShelfFiles.Exists(file.Path);
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Top };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        double icon = layout.Tier == ShelfTier.Compact ? 32 : 48;
        var image = new Image { Source = ShelfFiles.Icon(file.Path), Width = icon, Height = icon, VerticalAlignment = VerticalAlignment.Top, Opacity = exists ? 1 : .45, Margin = new Thickness(0, 2, 14, 0) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        grid.Children.Add(image);
        var text = new StackPanel();
        var name = ReadOnlyText(file.Name, layout.DetailBody + 1, UI.PanelFont); name.FontWeight = FontWeights.SemiBold; name.Opacity = exists ? 1 : .55;
        text.Children.Add(name);
        string type = ShelfFiles.TypeName(file.Path);
        text.Children.Add(Label(exists ? type : Loc.T("原文件已不存在（被移动或删除）", "The original is gone (moved or deleted)"), layout.Aux, exists ? UI.Secondary : "#E5C890", HorizontalAlignment.Left, top: 4));
        var folder = ReadOnlyText(System.IO.Path.GetDirectoryName(file.Path) ?? "", layout.Aux, UI.PanelFont); folder.Foreground = UI.Brush(UI.Tertiary); folder.Margin = new Thickness(0, 4, 0, 0);
        text.Children.Add(folder);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        return PassWheel(new ScrollViewer { Content = grid, Focusable = false });
    }

    private List<(string Label, string Value, Action<FrameworkElement>? Change)> ClipFacts(ClipEntry clip)
    {
        var facts = new List<(string, string, Action<FrameworkElement>?)>();
        facts.Add((Loc.T("来源", "Source"), clip.Manual ? Loc.T("常用语", "Phrase") : clip.Source.Length > 0 ? clip.Source : Loc.T("未知", "Unknown"), null));
        facts.Add((Loc.T("时间", "Time"), clip.Copied.ToLocalTime().ToString(Loc.T("M月d日 HH:mm", "MMM d, HH:mm"), CultureInfo.CurrentCulture), null));
        facts.Add(clip.Kind == ClipKind.Image ? (Loc.T("尺寸", "Size"), $"{clip.PixelWidth} × {clip.PixelHeight}", null)
            : (Loc.T("长度", "Length"), Loc.T($"{clip.Text.Length} 字 · {Shelf.LineCount(clip.Text)} 行", $"{clip.Text.Length} chars · {Shelf.LineCount(clip.Text)} lines"), null));
        facts.Add((Loc.T("分类", "Category"), CategoryNames(ShelfSide.Clipboard, clip.Categories), target => OpenMenu(CategoryChoices(ShelfSide.Clipboard, [clip.Id]), target)));
        return facts;
    }
    private List<(string Label, string Value, Action<FrameworkElement>? Change)> FileFacts(TransferFile file)
    {
        bool exists = ShelfFiles.Exists(file.Path);
        string size = !exists ? Loc.T("已不存在", "Missing") : ShelfFiles.IsFolder(file.Path) ? FolderFacts(file.Path) : ShelfFiles.FileSize(file.Path) is { } bytes ? Shelf.Size(bytes) : "";
        var facts = new List<(string, string, Action<FrameworkElement>?)> { (Loc.T("大小", "Size"), size, null) };
        if (exists && ShelfFiles.Modified(file.Path) is { } modified) facts.Add((Loc.T("修改", "Modified"), modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), null));
        facts.Add((Loc.T("加入", "Added"), Shelf.When(file.Added, DateTimeOffset.Now), null));
        facts.Add((Loc.T("分类", "Category"), CategoryNames(ShelfSide.Files, file.Categories), target => OpenMenu(CategoryChoices(ShelfSide.Files, [file.Id]), target)));
        return facts;
    }
    // The categories an entry is in, in their order, or "无".
    private string CategoryNames(ShelfSide side, List<Guid> ids) => ShelfData.Categories(side).Where(c => ids.Contains(c.Id)).Select(Shelf.CategoryName).ToList() is { Count: > 0 } names
        ? string.Join(Loc.T("、", ", "), names) : Loc.T("无", "None");

    private StackPanel ClipActions(ClipEntry clip)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        panel.Children.Add(PillButton(Loc.T("粘贴", "Paste"), true, () => PasteClip(clip)));
        // Text pastes as plain text from a button of its own beside "粘贴".
        if (clip.Kind != ClipKind.Image)
        {
            var plain = PillButton(Loc.T("粘贴为纯文本", "Paste as plain text"), false, () => PasteClip(clip, plain: true)); plain.Margin = new Thickness(6, 0, 0, 0);
            panel.Children.Add(plain);
        }
        Button more = null!;
        more = PillButton("⋯", false, () =>
        {
            var menu = NewMenu();
            menu.Items.Add(Item(Loc.T("只复制，不粘贴", "Copy only"), () => CopyClip(clip), MenuIcons.Copy));
            if (clip.Kind != ClipKind.Image) menu.Items.Add(Item(Loc.T("编辑…", "Edit…"), () => BeginEdit(clip.Id), MenuIcons.Edit));
            AddFiling(menu, clip);
            menu.Items.Add(Separator());
            menu.Items.Add(Item(Loc.T("删除", "Delete"), () => DeleteClips([clip]), MenuIcons.Delete, danger: true));
            OpenMenu(menu, more);
        });
        more.Margin = new Thickness(6, 0, 0, 0);
        panel.Children.Add(more);
        return panel;
    }
    private StackPanel FileActions(TransferFile file)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        if (!ShelfFiles.Exists(file.Path)) { panel.Children.Add(PillButton(Loc.T("移除", "Remove"), false, () => RemoveFiles([file]))); return panel; }
        panel.Children.Add(PillButton(Loc.T("打开", "Open"), true, () => OpenFile(file.Path)));
        var show = PillButton(Loc.T("在文件夹中显示", "Show in folder"), false, () => ShowInFolder(file.Path)); show.Margin = new Thickness(6, 0, 0, 0);
        panel.Children.Add(show);
        Button more = null!;
        more = PillButton("⋯", false, () =>
        {
            var menu = NewMenu();
            menu.Items.Add(Item(Loc.T("粘贴", "Paste"), () => PasteFiles([file]), MenuIcons.Paste));
            menu.Items.Add(Item(Loc.T("复制路径", "Copy path"), () => CopyPath(file), MenuIcons.FilePath));
            if (FilingMenu(ShelfSide.Files, [file.Id]) is { } filing) { menu.Items.Add(Separator()); menu.Items.Add(filing); }
            menu.Items.Add(Separator());
            AddRemoval(menu, [file]);
            OpenMenu(menu, more);
        });
        more.Margin = new Thickness(6, 0, 0, 0);
        panel.Children.Add(more);
        return panel;
    }

    private void DeleteClips(IReadOnlyList<ClipEntry> clips)
    {
        if (_shelfDetail is { Side: ShelfSide.Clipboard } detail && clips.Any(c => c.Id == detail.Id)) _shelfDetail = null;
        _app.Shelf.Remove(clips);
    }
    private void RemoveFiles(IReadOnlyList<TransferFile> files)
    {
        if (_shelfDetail is { Side: ShelfSide.Files } detail && files.Any(f => f.Id == detail.Id)) _shelfDetail = null;
        foreach (var file in files) _picked.Remove(file.Id);
        _app.Shelf.RemoveFiles(files);
    }
    // Taking files off the list: "移除" only forgets them; "删除原文件", in red, sends the files themselves to the Recycle Bin
    // (so they can be brought back) and forgets them too.
    private void AddRemoval(ContextMenu menu, IReadOnlyList<TransferFile> files)
    {
        bool many = files.Count > 1;
        menu.Items.Add(Item(many ? Loc.T($"移除 {files.Count} 项", $"Remove {files.Count} files") : Loc.T("移除", "Remove"), () => RemoveFiles(files), MenuIcons.Remove));
        menu.Items.Add(Item(many ? Loc.T($"删除 {files.Count} 个原文件", $"Delete {files.Count} original files") : Loc.T("删除原文件", "Delete the original"), () => DeleteOriginals(files), MenuIcons.Delete, danger: true));
    }
    // A file the shell will not delete (in use, or no permission) stays on the list and says so.
    private void DeleteOriginals(IReadOnlyList<TransferFile> files)
    {
        var gone = new List<TransferFile>();
        foreach (var file in files)
        {
            if (ShelfFiles.Exists(file.Path) && !Native.RecycleBin.Send(file.Path)) { Flash(file.Id, Loc.T("无法删除", "Not deleted")); continue; }
            ShelfFiles.Forget(file.Path); gone.Add(file);
        }
        if (gone.Count > 0) RemoveFiles(gone);
    }
    private void CopyPath(TransferFile file) { if (_app.Shelf.Put(ShelfService.TextData(file.Path))) Flash(file.Id, Loc.T("已复制路径", "Path copied")); }

    // ---- Menus ----
    // The panel's menus only use and file what is here; categories and phrases are made and changed in the settings. A row has an
    // icon at the left; a choice (a category) has a check column instead. Only a row that opens more rows has an arrow.
    private static class MenuIcons
    {
        public const string Paste = "M 4.5,2.5 H 11.5 A 1.5,1.5 0 0 1 13,4 V 13 A 1.5,1.5 0 0 1 11.5,14.5 H 4.5 A 1.5,1.5 0 0 1 3,13 V 4 A 1.5,1.5 0 0 1 4.5,2.5 Z M 6,2.5 H 10 V 4.5 H 6 Z";
        public const string PlainText = "M 3,3.5 H 13 M 8,3.5 V 13.5 M 6,13.5 H 10";
        public const string Copy = "M 6.5,5 H 12 A 1.5,1.5 0 0 1 13.5,6.5 V 12 A 1.5,1.5 0 0 1 12,13.5 H 6.5 A 1.5,1.5 0 0 1 5,12 V 6.5 A 1.5,1.5 0 0 1 6.5,5 Z M 11,5 V 3.5 A 1,1 0 0 0 10,2.5 H 3.5 A 1,1 0 0 0 2.5,3.5 V 10 A 1,1 0 0 0 3.5,11 H 5";
        public const string Details = "M 4.5,2.5 H 11.5 A 2,2 0 0 1 13.5,4.5 V 11.5 A 2,2 0 0 1 11.5,13.5 H 4.5 A 2,2 0 0 1 2.5,11.5 V 4.5 A 2,2 0 0 1 4.5,2.5 Z M 5.5,6 H 10.5 M 5.5,8.5 H 10.5 M 5.5,11 H 8.5";
        public const string Edit = "M 10.5,2.5 L 13.5,5.5 L 6,13 H 3 V 10 Z";
        public const string Category = "M 2.5,2.5 H 7.7 L 13.7,8.5 L 8.5,13.7 L 2.5,7.7 Z M 4.3,5.3 A 1,1 0 1 1 6.3,5.3 A 1,1 0 1 1 4.3,5.3";
        public const string Delete = "M 2.5,4.5 H 13.5 M 6,4.5 V 3 H 10 V 4.5 M 4,4.5 L 4.7,13.5 H 11.3 L 12,4.5";
        public const string Open = "M 9,2.5 H 13.5 V 7 M 13.5,2.5 L 7.5,8.5 M 11.5,9.5 V 12.5 A 1,1 0 0 1 10.5,13.5 H 3.5 A 1,1 0 0 1 2.5,12.5 V 5.5 A 1,1 0 0 1 3.5,4.5 H 6.5";
        public const string Folder = "M 2,4.5 A 1,1 0 0 1 3,3.5 H 6.2 L 7.7,5 H 13 A 1,1 0 0 1 14,6 V 12 A 1,1 0 0 1 13,13 H 3 A 1,1 0 0 1 2,12 Z";
        public const string FilePath = "M 6.5,9.5 L 9.5,6.5 M 7,4.5 L 8,3.5 A 2.8,2.8 0 0 1 12,7.5 L 11,8.5 M 9,11.5 L 8,12.5 A 2.8,2.8 0 0 1 4,8.5 L 5,7.5";
        public const string Remove = "M 2.5,8 A 5.5,5.5 0 1 1 13.5,8 A 5.5,5.5 0 1 1 2.5,8 Z M 5.5,8 H 10.5";
    }
    private ContextMenu NewMenu() => new() { Style = (Style)FindResource("ShelfMenu") };
    // check: null for an action; false or true for a choice, whose check column shows the one chosen.
    private MenuItem Item(string label, Action act, string? icon = null, string? hint = null, bool danger = false, bool? check = null, bool enabled = true, string? dot = null, bool star = false)
    {
        string color = danger ? PanelTheme.Hex("danger") : UI.Primary;
        var header = new DockPanel { LastChildFill = true };
        void Lead(FrameworkElement part) { DockPanel.SetDock(part, System.Windows.Controls.Dock.Left); header.Children.Add(part); }
        if (check is { } chosen)
            Lead(new Path { Data = Geometry.Parse("M 1.5,5.2 L 4,7.7 L 8.5,2.5"), Stroke = UI.Brush(UI.Primary), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Width = 10, Height = 10, Margin = new Thickness(1, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = chosen ? Visibility.Visible : Visibility.Hidden });
        if (icon is not null) Lead(MenuGlyph(icon, danger ? color : UI.Secondary));
        if (star) { var mark = StarGlyph(10, UI.Secondary, filled: false); mark.Margin = new Thickness(0, 0, 7, 0); mark.VerticalAlignment = VerticalAlignment.Center; Lead(mark); }
        if (dot is not null) Lead(new Ellipse { Width = 7, Height = 7, Fill = UI.Brush(dot), Margin = new Thickness(1, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = label, Foreground = UI.Brush(color), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var item = new MenuItem { Style = (Style)FindResource("ShelfMenuItem"), Header = header, InputGestureText = hint ?? "", IsEnabled = enabled };
        AutomationProperties.SetName(item, label);
        if (check == true) AutomationProperties.SetItemStatus(item, Loc.T("已选中", "Selected"));
        item.Click += (_, e) => { e.Handled = true; CloseMenu(); act(); };
        // Another row's menu beside it closes as soon as the pointer comes onto this row.
        item.MouseEnter += (_, _) =>
        {
            if (item.Parent is ItemsControl owner)
                foreach (var other in owner.Items.OfType<MenuItem>().Where(other => other != item && other.IsSubmenuOpen)) other.IsSubmenuOpen = false;
        };
        return item;
    }
    private MenuItem Submenu(string label, string icon, IEnumerable<object> items)
    {
        var header = new DockPanel { LastChildFill = true };
        var glyph = MenuGlyph(icon, UI.Secondary); DockPanel.SetDock(glyph, System.Windows.Controls.Dock.Left); header.Children.Add(glyph);
        header.Children.Add(new TextBlock { Text = label, Foreground = UI.Brush(UI.Primary), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var item = new MenuItem { Style = (Style)FindResource("ShelfMenuItem"), Header = header };
        AutomationProperties.SetName(item, label);
        foreach (var child in items) item.Items.Add(child);
        // Opens as soon as the pointer is on it, without the system's menu delay (400 ms by default).
        item.MouseEnter += (_, _) => { if (!item.IsSubmenuOpen) item.IsSubmenuOpen = true; };
        return item;
    }
    // A 14 DIP line icon drawn on a 16 grid.
    private static Path MenuGlyph(string data, string color) => new()
    {
        Data = Geometry.Parse(data), Stroke = UI.Brush(color), StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        Width = 16, Height = 16, LayoutTransform = new ScaleTransform(.875, .875), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
    };
    private Separator Separator() => new() { Style = (Style)FindResource("ShelfMenuSeparator") };
    private void CloseMenu() { if (_shelfMenu is { IsOpen: true } menu) menu.IsOpen = false; }
    // The menu's frame keeps 12 DIP around it for its shadow (ShelfResources.xaml); the offsets take that back.
    private void OpenMenu(ContextMenu menu, FrameworkElement target, bool atPointer = false)
    {
        CloseMenu();
        _shelfMenu = menu;
        menu.PlacementTarget = target;
        menu.Placement = atPointer ? PlacementMode.MousePoint : PlacementMode.Bottom;
        menu.HorizontalOffset = -12 * _scale;
        menu.VerticalOffset = (atPointer ? -12 : 4 - 12) * _scale;
        UI.Scale(menu, _scale);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_shelfMenu, menu)) _shelfMenu = null;
            Dispatcher.BeginInvoke(() => { if (_expanded && !_pointerInside && !ShelfHolds) PointerChanged(false); });
        };
        menu.IsOpen = true;
    }

    private ContextMenu ClipMenu(ClipEntry clip)
    {
        var menu = NewMenu();
        bool textual = clip.Kind != ClipKind.Image;
        menu.Items.Add(Item(Loc.T("粘贴", "Paste"), () => PasteClip(clip), MenuIcons.Paste));
        // Every text entry, as on its row and in its details.
        if (textual) menu.Items.Add(Item(Loc.T("粘贴为纯文本", "Paste as plain text"), () => PasteClip(clip, plain: true), MenuIcons.PlainText));
        menu.Items.Add(Item(Loc.T("只复制，不粘贴", "Copy only"), () => CopyClip(clip), MenuIcons.Copy));
        menu.Items.Add(Separator());
        menu.Items.Add(Item(Loc.T("查看详情", "Details"), () => OpenDetail(ShelfSide.Clipboard, clip.Id), MenuIcons.Details));
        if (textual) menu.Items.Add(Item(Loc.T("编辑…", "Edit…"), () => BeginEdit(clip.Id), MenuIcons.Edit));
        AddFiling(menu, clip);
        menu.Items.Add(Separator());
        menu.Items.Add(Item(Loc.T("删除", "Delete"), () => DeleteClips([clip]), MenuIcons.Delete, danger: true));
        return menu;
    }
    // "加入常用" (or "移出常用") on its own row, and the user's categories, when there are any, in a menu beside it.
    private void AddFiling(ContextMenu menu, ClipEntry clip)
    {
        menu.Items.Add(Separator());
        bool favorite = clip.Categories.Contains(ShelfData.Favorites);
        menu.Items.Add(Item(favorite ? Loc.T("移出常用", "Remove from favorites") : Loc.T("加入常用", "Add to favorites"),
            () => SetCategory(ShelfSide.Clipboard, [clip.Id], ShelfData.Favorites, add: !favorite), StarData));
        if (FilingMenu(ShelfSide.Clipboard, [clip.Id]) is { } filing) menu.Items.Add(filing);
    }
    // "加入分类" with the user's categories, or nothing when the user has made none. An entry can be in several: a category all the
    // entries are in is checked, and choosing it takes them out of it; choosing another puts them in it as well.
    private MenuItem? FilingMenu(ShelfSide side, IReadOnlyList<Guid> ids)
    {
        var own = ShelfData.OwnCategories(side).ToList();
        if (own.Count == 0) return null;
        var lists = CategoriesOf(side, ids);
        bool All(Guid id) => lists.Count > 0 && lists.All(list => list.Contains(id));
        // The check column only when the entries are in one of these categories.
        bool inOwn = own.Any(category => All(category.Id));
        var items = own.Select(category => (object)Item(category.Name, () => SetCategory(side, ids, category.Id, add: !All(category.Id)),
            check: inOwn ? All(category.Id) : null, dot: category.Color)).ToList();
        return Submenu(Loc.T("加入分类", "Add to category"), MenuIcons.Category, items);
    }
    private ContextMenu FileMenu(TransferFile file)
    {
        var menu = NewMenu();
        if (!ShelfFiles.Exists(file.Path))
        {
            menu.Items.Add(Item(Loc.T("查看详情", "Details"), () => OpenDetail(ShelfSide.Files, file.Id), MenuIcons.Details));
            if (FilingMenu(ShelfSide.Files, [file.Id]) is { } lost) menu.Items.Add(lost);
            menu.Items.Add(Separator());
            menu.Items.Add(Item(Loc.T("移除", "Remove"), () => RemoveFiles([file]), MenuIcons.Remove));
            return menu;
        }
        var files = _picked.Contains(file.Id) && _picked.Count > 1 ? ShelfData.Files.Where(f => _picked.Contains(f.Id)).ToList() : [file];
        bool many = files.Count > 1;
        menu.Items.Add(Item(many ? Loc.T($"粘贴所选 {files.Count} 项", $"Paste {files.Count} files") : Loc.T("粘贴", "Paste"),
            () => PasteFiles(files), MenuIcons.Paste));
        if (!many)
        {
            menu.Items.Add(Item(Loc.T("打开", "Open"), () => OpenFile(file.Path), MenuIcons.Open));
            menu.Items.Add(Item(Loc.T("在文件夹中显示", "Show in folder"), () => ShowInFolder(file.Path), MenuIcons.Folder));
            menu.Items.Add(Item(Loc.T("复制路径", "Copy path"), () => CopyPath(file), MenuIcons.FilePath));
            menu.Items.Add(Separator());
            menu.Items.Add(Item(Loc.T("查看详情", "Details"), () => OpenDetail(ShelfSide.Files, file.Id), MenuIcons.Details));
        }
        if (FilingMenu(ShelfSide.Files, files.Select(f => f.Id).ToList()) is { } filing) { if (many) menu.Items.Add(Separator()); menu.Items.Add(filing); }
        menu.Items.Add(Separator());
        AddRemoval(menu, files);
        return menu;
    }
    // The details' "分类": every category, "常用" too, those the entry is in checked; choosing one puts it in or takes it out.
    // "无" takes it out of all of them.
    private ContextMenu CategoryChoices(ShelfSide side, IReadOnlyList<Guid> ids)
    {
        var menu = NewMenu();
        var lists = CategoriesOf(side, ids);
        bool In(Guid id) => lists.Count > 0 && lists.All(list => list.Contains(id));
        menu.Items.Add(Item(Loc.T("无", "None"), () => { foreach (var list in lists) list.Clear(); _app.Shelf.Edited(); }, check: lists.All(list => list.Count == 0)));
        foreach (var category in ShelfData.Categories(side))
            menu.Items.Add(category.Id == ShelfData.Favorites
                ? Item(Shelf.CategoryName(category), () => SetCategory(side, ids, category.Id, add: !In(category.Id)), check: In(category.Id), star: true)
                : Item(category.Name, () => SetCategory(side, ids, category.Id, add: !In(category.Id)), check: In(category.Id), dot: category.Color));
        return menu;
    }
    // The user's categories, with their counts, when they do not fit beside "全部" and "常用".
    private ContextMenu CategoryListMenu(ShelfSide side)
    {
        var menu = NewMenu();
        var selected = side == ShelfSide.Clipboard ? _clipTab : _fileTab;
        string Number(Guid id) => (side == ShelfSide.Clipboard ? ShelfData.Clips.Count(c => c.Categories.Contains(id)) : ShelfData.Files.Count(f => f.Categories.Contains(id))).ToString(CultureInfo.InvariantCulture);
        void Choose(Guid id) { if (side == ShelfSide.Clipboard) _clipTab = id; else { _fileTab = id; _picked.Clear(); } RenderShelf(); }
        // The check column only when one of them is the one shown, so the menu has no empty column at its left.
        var own = ShelfData.OwnCategories(side).ToList();
        bool chosen = own.Any(category => category.Id == selected);
        foreach (var category in own)
            menu.Items.Add(Item(category.Name, () => Choose(category.Id), hint: Number(category.Id), check: chosen ? selected == category.Id : null, dot: category.Color));
        return menu;
    }
}

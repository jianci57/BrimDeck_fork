using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The clipboard and file transfer's settings, in their own group ("中转站"). The page's size is set with the other pages under
// "展开外观". "剪贴板": whether the clipboard is recorded (the history stays on this computer, encrypted for the current Windows
// user, until BrimDeck exits), its categories and the phrases; "文件中转": its categories. The panel only uses the categories and
// phrases; they are made and changed here, each category with what goes in by itself (SettingsShelfRules) and, on the clipboard,
// whether what is in it is kept. "文件中转" also sets whether files dragged out are copied or moved.
public partial class SettingsWindow
{
    internal const int ClipboardSettingsPage = 13, TransferSettingsPage = 14;

    private void ShelfSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.ClipboardWidth, 440, 1200, value => Resize(s => s.ClipboardWidth = value, DeckPage.Clipboard)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.ClipboardHeight, 140, 400, value => Resize(s => s.ClipboardHeight = value, DeckPage.Clipboard)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var sizes = KeepPreview(new UniformGrid { Columns = 3, Width = 292 });
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (w, h) = ShelfSizes.For(size);
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = PanelSizes.Name(size), GroupName = "ShelfSize", IsChecked = (S.ClipboardWidth, S.ClipboardHeight) == (w, h) };
            AutomationProperties.SetName(choice, Loc.T("剪贴板与文件中转快捷尺寸 ", "Clipboard and file transfer quick size ") + PanelSizes.Name(size));
            choice.Checked += (_, _) => { Resize(s => { s.ClipboardWidth = w; s.ClipboardHeight = h; }, DeckPage.Clipboard); ShowPage(2); };
            sizes.Children.Add(choice);
        }
        QuickSizeRow(sizes);
    }

    // ---- 剪贴板 ----
    private void ClipboardSettings()
    {
        WatchShelf();
        var record = Toggle(Loc.T("记录剪贴板", "Record the clipboard"), S.ClipboardRecord, value => { Change(s => s.ClipboardRecord = value); ShowPage(_page); },
            help: Loc.T("记录复制的文字和图片，保存在本机并加密存放，退出 BrimDeck 时清除。放进“常用”或设为一直保留的分类的内容不会清除。",
                "Keeps the text and pictures you copy, encrypted on this computer, until BrimDeck exits. What is in Favorites or in a category set to keep stays."));
        Group(record, _excludedHost = new Border { Child = ExcludedAppsContent() });
        SectionTitle(Loc.T("分类", "Categories"), help: () => Loc.T("一条内容可以放进多个分类。关闭“一直保留”的分类，其中的内容在退出 BrimDeck 时清除。单击筛选按钮设置自动放入的规则。拖动调整顺序。",
            "An entry can be in several categories. What is in a category with Keep off is cleared when BrimDeck exits. The filter button sets what goes in by itself. Drag to reorder."));
        CategoryList(ShelfSide.Clipboard);
        SectionTitle(Loc.T("常用", "Favorites"));
        FavoritesList();
    }

    // Whether the apps not recorded are shown under their row, and the row itself, rebuilt alone when an app is added or removed.
    private bool _excludedOpen;
    private Border? _excludedHost;
    private void RefreshExcludedApps() { if (_excludedHost is not null) _excludedHost.Child = ExcludedAppsContent(); }

    // An app picked from the menu or a file is kept by its path, for its icon and name; one typed in earlier versions by its
    // process name. Either way every process of that executable is left out.
    private FrameworkElement ExcludedAppsContent() => AppListRow(Loc.T("不记录的应用", "Apps not recorded"),
        () => Loc.T("从这些应用复制的内容不会记录。“添加应用”列出有窗口的应用，也可以浏览可执行文件。",
            "What you copy from these apps is not recorded. \"Add app\" lists apps with windows, or browse for an executable."),
        Loc.T("展开不记录的应用", "Show apps not recorded"), Loc.T("添加不记录的应用", "Add an app not to record"),
        [.. S.ClipboardExcluded.Select(ExcludedAppLine)], ExcludedAppMenu, _excludedOpen, open => _excludedOpen = open, S.ClipboardRecord);

    private FrameworkElement ExcludedAppLine(string entry)
    {
        string process = ClipboardExclusions.ProcessName(entry), path = ClipboardExclusions.IsPath(entry) ? entry : "";
        string name = AppName(process, path);
        return AppLine(name, FileName(process, path), path, path, () =>
        {
            Change(s => s.ClipboardExcluded.RemoveAll(other => string.Equals(ClipboardExclusions.ProcessName(other), process, StringComparison.OrdinalIgnoreCase)));
            RefreshExcludedApps();
        });
    }

    private void ExcludedAppMenu(Button anchor)
    {
        var menu = AppMenu(anchor);
        var apps = RunningApps.WithWindows()
            .Where(app => !ClipboardExclusions.Excludes(S.ClipboardExcluded, app.Name))
            .Select(app => (App: app, Name: AppName(app.Name, app.Path)))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var (app, name) in apps)
            menu.Items.Add(AppMenuItem(name, FileName(app.Name, app.Path), app.Path, () => AddExcludedApp(app.Path.Length > 0 ? app.Path : app.Name)));
        if (apps.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("没有可添加的应用", "No apps to add"), IsEnabled = false });
        AddBrowseItem(menu, Loc.T("选择不记录的应用", "Choose an app not to record"), AddExcludedApp);
        menu.IsOpen = true;
    }

    private void AddExcludedApp(string entry)
    {
        if (ClipboardExclusions.Excludes(S.ClipboardExcluded, ClipboardExclusions.ProcessName(entry))) return;
        Change(s => s.ClipboardExcluded.Add(entry));
        RefreshExcludedApps();
    }

    // ---- 文件中转 ----
    private void TransferSettings()
    {
        WatchShelf();
        Group(MusicChoice(Loc.T("拖出文件", "Drag files out"), S.TransferDragOut,
            [(TransferDragOut.Copy, Loc.T("复制", "Copy")), (TransferDragOut.Move, Loc.T("移动", "Move"))],
            value => Change(s => s.TransferDragOut = value), width: 216,
            help: Loc.T("选择“移动”时，文件移到新位置后从列表中移除。聊天软件、浏览器等只接受复制的地方仍会复制。",
                "With Move, files moved to a new place leave the list. Chat apps, browsers and other places that only take copies still get copies.")));
        SectionTitle(Loc.T("分类", "Categories"), help: () => Loc.T("一个文件可以放进多个分类。单击筛选按钮设置自动放入的规则。拖动调整顺序。原文件不受影响。",
            "A file can be in several categories. The filter button sets what goes in by itself. Drag to reorder. The original files are not touched."));
        CategoryList(ShelfSide.Files);
    }

    // ---- Categories and phrases ----
    private const double CategoryRowHeight = 44;
    // A phrase being written (Guid.Empty for a new one), and a category just made, whose name is ready to type.
    private Guid? _phraseEdit;
    private Guid? _namingCategory;
    private bool _shelfWatching, _shelfOwnEdit;
    private ShelfData ShelfData => _app.Shelf.Data;

    // Copies and changes made on the panel show on these pages while one is open; what a page changes it rebuilds itself.
    private void WatchShelf()
    {
        if (_shelfWatching) return;
        _shelfWatching = true;
        _app.Shelf.Changed += ShelfDataChanged;
        Closed += (_, _) => _app.Shelf.Changed -= ShelfDataChanged;
    }
    private void ShelfDataChanged()
    {
        if (_page is not (ClipboardSettingsPage or TransferSettingsPage) || _shelfOwnEdit || _phraseEdit is not null || _appDrag is not null || Keyboard.FocusedElement is TextBox) return;
        ShowPage(_page);
    }
    // A change made here: saved, and the panels told, without this page rebuilding under the user.
    private void ShelfEdited()
    {
        _shelfOwnEdit = true;
        try { _app.Shelf.Edited(); } finally { _shelfOwnEdit = false; }
    }

    private void CategoryList(ShelfSide side)
    {
        var list = new StackPanel();
        if (side == ShelfSide.Clipboard)
        {
            list.Children.Add(FavoritesRow());
            list.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
        }
        var rows = new StackPanel();
        foreach (var category in ShelfData.OwnCategories(side)) rows.Children.Add(CategoryRow(side, category, rows));
        UpdateDividers(rows);
        list.Children.Add(rows);
        if (side == ShelfSide.Files && rows.Children.Count == 0)
        {
            var empty = TextLine(Loc.T("还没有分类。", "No categories yet."), 12.5, TextTertiary); empty.Margin = new Thickness(0, 13, 0, 13);
            list.Children.Add(empty);
        }
        Target.Children.Add(new Border { Child = list, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0, 1, 0, 0) });
        var add = AddLink(Loc.T("新建分类", "New category"), () => AddCategory(side));
        add.Margin = new Thickness(0, 6, 0, 26);
        AutomationProperties.SetName(add, side == ShelfSide.Clipboard ? Loc.T("新建剪贴板分类", "New clipboard category") : Loc.T("新建文件中转分类", "New file transfer category"));
        Target.Children.Add(add);
    }
    private string CountText(ShelfSide side, Guid id)
    {
        int count = side == ShelfSide.Clipboard ? ShelfData.Clips.Count(clip => clip.Categories.Contains(id)) : ShelfData.Files.Count(file => file.Categories.Contains(id));
        return side == ShelfSide.Clipboard ? Loc.T($"{count} 条", count == 1 ? "1 entry" : $"{count} entries") : Loc.T($"{count} 个", count == 1 ? "1 file" : $"{count} files");
    }
    // "常用": no handle, no color and no delete; it is always first.
    private Grid FavoritesRow()
    {
        var row = CategoryColumns();
        var star = Stroke("M 8,1.3 L 10,5.4 L 14.5,6 L 11.2,9.2 L 12,13.7 L 8,11.6 L 4,13.7 L 4.8,9.2 L 1.5,6 L 6,5.4 Z", 14, 14, TextSecondary);
        star.Fill = UI.Brush(TextSecondary); star.HorizontalAlignment = HorizontalAlignment.Center; star.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(star, 1); row.Children.Add(star);
        var name = TextLine(Loc.T("常用", "Favorites"), 13, TextPrimary); name.VerticalAlignment = VerticalAlignment.Center; name.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(name, 2); Grid.SetColumnSpan(name, 2); row.Children.Add(name);
        var always = AlwaysKept();
        Grid.SetColumn(always, 4); row.Children.Add(always);
        var count = TextLine(CountText(ShelfSide.Clipboard, ShelfData.Favorites), 12, TextTertiary); count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(count, 5); row.Children.Add(count);
        return row;
    }
    // Handle, color, name (180 wide), what the category does by itself, "一直保留" (the clipboard's), count, the filter button, delete.
    private const double CategoryNameWidth = 180, KeepColumnWidth = 116;
    private static Grid CategoryColumns()
    {
        var row = new Grid { Height = CategoryRowHeight, Background = Brushes.Transparent };
        foreach (var width in new[] { new GridLength(22), new GridLength(44), new GridLength(CategoryNameWidth), new GridLength(1, GridUnitType.Star), new GridLength(KeepColumnWidth), GridLength.Auto, new GridLength(36), new GridLength(36) })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        return row;
    }
    private Border CategoryRow(ShelfSide side, ShelfCategory category, StackPanel rows)
    {
        var row = CategoryColumns();
        var handle = new GripHandle { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        // The color: the settings' color button and picker, as the apps and the monitor use. A category has no default color.
        var color = new Button { Style = (Style)FindResource("SettingsColorButton"), Foreground = UI.Brush(category.Color) };
        AutomationProperties.SetName(color, Loc.T($"{category.Name}颜色", $"{category.Name} color"));
        var popup = new Popup { PlacementTarget = color, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        popup.Child = ColorPicker(category.Color, true, value =>
        {
            popup.IsOpen = false;
            PopupMotion.AfterClose(() => { if (value.Length == 0) return; category.Color = value; ShelfEdited(); ShowPage(_page); });
        });
        TogglePopup(color, popup);
        Grid.SetColumn(color, 1); row.Children.Add(color);
        var name = new TextBox { Style = (Style)FindResource("SettingsField"), Height = 30, Padding = new Thickness(10, 0, 10, 0), FontSize = 12.5, MaxLength = 24, Text = category.Name, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(name, Loc.T("分类名称", "Category name"));
        void Commit()
        {
            string value = name.Text.Trim();
            if (value.Length == 0) { name.Text = category.Name; return; }
            if (value == category.Name) return;
            category.Name = value; ShelfEdited();
        }
        name.LostKeyboardFocus += (_, _) => Commit();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { name.Text = category.Name; Keyboard.ClearFocus(); e.Handled = true; }
        };
        if (_namingCategory == category.Id)
        {
            _namingCategory = null;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { name.Focus(); name.SelectAll(); name.BringIntoView(); });
        }
        Grid.SetColumn(name, 2); row.Children.Add(name);
        var summary = CategorySummary(side, category);
        Grid.SetColumn(summary, 3); row.Children.Add(summary);
        if (side == ShelfSide.Clipboard)
        {
            var keep = KeepSwitch(category);
            Grid.SetColumn(keep, 4); row.Children.Add(keep);
        }
        var count = TextLine(CountText(side, category.Id), 12, TextTertiary); count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(count, 5); row.Children.Add(count);
        bool open = _openCategory == category.Id;
        var options = CategoryOptionsButton(side, category, open);
        Grid.SetColumn(options, 6); row.Children.Add(options);
        // Deleting puts the entries only in this category back in "全部", where they go with the history, so it asks first. Entries
        // also in another category stay there.
        var remove = GlyphButton("M 0,0 L 8,8 M 8,0 L 0,8", 8, 8, Loc.T("删除 ", "Delete ") + category.Name, () =>
        {
            bool Alone(List<Guid> ids) => ids.Count == 1 && ids[0] == category.Id;
            int alone = side == ShelfSide.Clipboard ? ShelfData.Clips.Count(clip => Alone(clip.Categories)) : ShelfData.Files.Count(file => Alone(file.Categories));
            string what = side == ShelfSide.Clipboard
                ? Loc.T($"将删除分类“{category.Name}”。只在这个分类中的 {alone} 条内容回到“全部”，退出 BrimDeck 时清除。", $"This deletes {category.Name}. The {alone} entries only in it go back to All and are cleared when BrimDeck exits.")
                : Loc.T($"将删除分类“{category.Name}”。只在这个分类中的 {alone} 个文件回到“全部”，原文件不受影响。", $"This deletes {category.Name}. The {alone} files only in it go back to All; the original files are not touched.");
            if (alone > 0 && !Confirm(Loc.T("删除分类", "Delete category"), what, Loc.T("删除", "Delete"))) return;
            Shelf.DeleteCategory(ShelfData, side, category.Id);
            ShelfEdited(); ShowPage(_page);
        });
        remove.ToolTip = null;
        remove.Width = 24; remove.Height = 26; remove.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(remove, 7); row.Children.Add(remove);
        // The category's options open under its row, as the apps' quota configuration does; a drag closes them first.
        var body = new StackPanel(); body.Children.Add(row);
        if (open) body.Children.Add(CategoryOptions(side, category));
        var border = new Border { Child = body, Height = open ? double.NaN : CategoryRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = category.Id, RenderTransform = new TranslateTransform() };
        AttachDrag(handle, category.Name, () => rows, CategoryRowHeight, list =>
        {
            Shelf.Reorder(ShelfData, side, [.. list.Children.OfType<Border>().Where(b => b.Tag is Guid).Select(b => (Guid)b.Tag)]);
            ShelfEdited();
        }, () => CloseCategoryOptions(rows));
        return border;
    }
    private void AddCategory(ShelfSide side)
    {
        string baseName = Loc.T("新分类", "New category"), name = baseName;
        for (int n = 2; ShelfData.Categories(side).Any(category => category.Name == name); n++) name = baseName + " " + n.ToString(CultureInfo.InvariantCulture);
        var colors = PanelTheme.Current.Swatches.Take(8).Select(swatch => swatch.WithAlpha(255).ToString()).ToList();
        _namingCategory = Shelf.AddCategory(ShelfData, side, name, colors).Id;
        ShelfEdited(); ShowPage(_page);
    }

    // Everything in "常用", newest first: what was added from the panel and the phrases written here. Text can be changed in place;
    // the cross deletes a phrase (it exists only here) and takes anything else out of "常用", back to its other categories or the
    // history.
    private void FavoritesList()
    {
        var list = new StackPanel();
        void Add(FrameworkElement row)
        {
            if (list.Children.Count > 0) list.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
            list.Children.Add(row);
        }
        if (_phraseEdit == Guid.Empty) Add(PhraseEditor(null));
        foreach (var entry in ShelfData.Clips.Where(clip => clip.Categories.Contains(ShelfData.Favorites)))
            Add(_phraseEdit == entry.Id ? PhraseEditor(entry) : FavoriteRow(entry));
        if (list.Children.Count == 0)
        {
            var empty = TextLine(Loc.T("还没有内容。", "Nothing here yet."), 12.5, TextTertiary); empty.Margin = new Thickness(0, 13, 0, 13);
            list.Children.Add(empty);
        }
        Target.Children.Add(new Border { Child = list, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0, 1, 0, 0) });
        var add = AddLink(Loc.T("新建常用语", "New phrase"), () => { _phraseEdit = Guid.Empty; ShowPage(_page); });
        add.Margin = new Thickness(0, 6, 0, 26); add.IsEnabled = _phraseEdit is null;
        Target.Children.Add(add);
    }
    private Grid FavoriteRow(ClipEntry entry)
    {
        var row = new Grid { Height = 40, Background = Brushes.Transparent };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(36), new GridLength(36) })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        string label;
        if (entry.Kind == ClipKind.Image)
        {
            label = Loc.T("图片", "Picture");
            // A 48 x 28 window on the picture, filled and cropped, so a long thin one does not shrink to a line.
            var thumb = new Border { Width = 48, Height = 28, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = UI.Brush(_palette.Inset),
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new Image { Source = _app.Shelf.Picture(entry.Id, 96), Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            row.Children.Add(thumb);
            var size = TextLine($"{entry.PixelWidth} × {entry.PixelHeight}", 13, TextPrimary); size.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(size, 1); row.Children.Add(size);
        }
        else
        {
            var (first, more) = Shelf.FirstLine(entry.Text);
            label = first;
            var text = TextLine(more > 0 ? first + Loc.T($"  +{more} 行", $"  +{more} lines") : first, 13, TextPrimary);
            text.VerticalAlignment = VerticalAlignment.Center; text.TextTrimming = TextTrimming.CharacterEllipsis; text.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(text, 1); row.Children.Add(text);
        }
        // Where it came from: a phrase written here, or the time it was copied.
        var from = TextLine(entry.Manual ? Loc.T("常用语", "Phrase") : Shelf.When(entry.Copied, DateTimeOffset.Now), 12, TextTertiary);
        from.VerticalAlignment = VerticalAlignment.Center; from.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(from, 2); row.Children.Add(from);
        if (entry.Kind != ClipKind.Image)
        {
            var edit = GlyphButton("M 10.5,2.5 L 13.5,5.5 L 6,13 H 3 V 10 Z", 12, 12, Loc.T("编辑", "Edit"), () => { _phraseEdit = entry.Id; ShowPage(_page); });
            edit.ToolTip = null; edit.Width = 28; edit.Height = 28; edit.IsEnabled = _phraseEdit is null; edit.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(edit, 3); row.Children.Add(edit);
        }
        var remove = GlyphButton("M 0,0 L 8,8 M 8,0 L 0,8", 8, 8, entry.Manual ? Loc.T("删除常用语", "Delete phrase") : Loc.T("移出常用", "Remove from favorites"), () =>
        {
            if (entry.Manual)
            {
                if (!Confirm(Loc.T("删除常用语", "Delete phrase"), Loc.T($"将删除常用语“{label}”。", $"This deletes the phrase \"{label}\"."), Loc.T("删除", "Delete"))) return;
                _shelfOwnEdit = true;
                try { _app.Shelf.Remove([entry]); } finally { _shelfOwnEdit = false; }
            }
            else { entry.Categories.Remove(ShelfData.Favorites); ShelfEdited(); }
            ShowPage(_page);
        });
        remove.ToolTip = null; remove.Width = 24; remove.Height = 26; remove.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(remove, 4); row.Children.Add(remove);
        return row;
    }
    // A phrase written or changed in place of its row; Ctrl+Enter saves, Esc cancels.
    private StackPanel PhraseEditor(ClipEntry? phrase)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        var box = new TextBox { Style = (Style)FindResource("SettingsField"), Height = double.NaN, MinHeight = 72, MaxHeight = 200, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(10, 7, 10, 7), FontSize = 12.5, Text = phrase?.Text ?? "" };
        box.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(box, Loc.T("常用语内容", "Phrase text"));
        panel.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        void Close() { _phraseEdit = null; ShowPage(_page); }
        void Save()
        {
            string text = box.Text;
            if (text.Trim().Length > 0)
            {
                if (phrase is null) Shelf.AddPhrase(ShelfData, text, DateTimeOffset.Now);
                else { phrase.Text = text; phrase.Kind = Shelf.Classify(text); phrase.Html = ""; phrase.Rtf = ""; }
                ShelfEdited();
            }
            Close();
        }
        var cancel = ActionButton(Loc.T("取消", "Cancel"), Close);
        var save = ActionButton(Loc.T("保存", "Save"), Save, primary: true); save.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        panel.Children.Add(buttons);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Save(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { box.Focus(); box.CaretIndex = box.Text.Length; box.BringIntoView(); });
        return panel;
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The clipboard and file transfer's settings, in their own group ("中转站"). The page's size is set with the other pages under
// "展开外观". "剪贴板": whether and for how long the clipboard is recorded (the history stays on this computer, encrypted for the
// current Windows user), its categories and the phrases; "文件中转": its categories. The panel only uses the categories and
// phrases; they are made and changed here. Entries in a category and phrases are kept whatever the history rule.
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
            help: Loc.T("记录复制的文字和图片，保存在本机并加密存放。密码管理器标记为不记录的内容不会保存。",
                "Keeps the text and pictures you copy, encrypted on this computer. What password managers mark as private is never kept."));
        var retention = new ComboBox { Width = 150, Style = (Style)FindResource("SettingsCombo") };
        var choices = new[] { (ClipRetention.Week, Loc.T("7 天", "7 days")), (ClipRetention.Month, Loc.T("30 天", "30 days")),
            (ClipRetention.Recent500, Loc.T("最近 500 条", "Latest 500")), (ClipRetention.UntilExit, Loc.T("退出时清除", "Until exit")) };
        retention.ItemsSource = choices.Select(choice => choice.Item2).ToList();
        retention.SelectedIndex = Math.Max(0, Array.FindIndex(choices, choice => choice.Item1 == S.ClipboardRetention));
        retention.IsEnabled = S.ClipboardRecord;
        AutomationProperties.SetName(retention, Loc.T("保留历史记录", "Keep history"));
        PopupMotion.Attach(retention);
        retention.SelectionChanged += (_, _) => { if (retention.SelectedIndex >= 0) Change(s => s.ClipboardRetention = choices[retention.SelectedIndex].Item1); };
        var excluded = new TextBox { Style = (Style)FindResource("SettingsField"), Width = 220, Text = string.Join(", ", S.ClipboardExcluded), IsEnabled = S.ClipboardRecord };
        AutomationProperties.SetName(excluded, Loc.T("不记录的应用", "Apps not recorded"));
        excluded.LostKeyboardFocus += (_, _) =>
        {
            var names = excluded.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries).Select(name => name.Trim()).ToList();
            if (!names.SequenceEqual(S.ClipboardExcluded)) Change(s => s.ClipboardExcluded = names);
        };
        Group(record,
            Row(Loc.T("保留历史记录", "Keep history"), retention, help: () => Loc.T("放进分类的内容和常用语一直保留，只在删除时移除。", "Entries in a category and phrases are kept until you delete them.")),
            Row(Loc.T("不记录的应用", "Apps not recorded"), excluded, help: () => Loc.T("填写进程名，用逗号分隔，例如 KeePass, 1Password。", "Process names separated by commas, for example KeePass, 1Password.")));
        SectionTitle(Loc.T("分类", "Categories"), help: () => Loc.T("放进分类的内容一直保留，不按保留规则清理。拖动调整顺序，面板中按这个顺序显示。“常用”是内置分类，排在最前。",
            "Entries in a category are kept, whatever the history rule. Drag to reorder; the panel shows them in this order. Favorites is built in and comes first."));
        CategoryList(ShelfSide.Clipboard);
        SectionTitle(Loc.T("常用语", "Phrases"), help: () => Loc.T("手写的文字，放在“常用”中，在面板中单击即可粘贴。", "Text you write here goes to Favorites; click it on the panel to paste it."));
        PhraseList();
    }

    // ---- 文件中转 ----
    private void TransferSettings()
    {
        WatchShelf();
        SectionTitle(Loc.T("分类", "Categories"), help: () => Loc.T("拖动调整顺序。删除分类时，其中的文件回到“全部”，原文件不受影响。",
            "Drag to reorder. Deleting a category puts its files back in All; the original files are not touched."));
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
            var empty = TextLine(Loc.T("还没有分类。", "No categories yet."), 12.5, TextSecondary); empty.Margin = new Thickness(0, 12, 0, 12);
            list.Children.Add(empty);
        }
        Target.Children.Add(new Border { Child = list, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0, 1, 0, 0) });
        var add = ActionButton(Loc.T("新建分类", "New category"), () => AddCategory(side));
        add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new Thickness(0, 10, 0, 28);
        AutomationProperties.SetName(add, side == ShelfSide.Clipboard ? Loc.T("新建剪贴板分类", "New clipboard category") : Loc.T("新建文件中转分类", "New file transfer category"));
        Target.Children.Add(add);
    }
    private string CountText(ShelfSide side, Guid id)
    {
        int count = side == ShelfSide.Clipboard ? ShelfData.Clips.Count(clip => clip.Category == id) : ShelfData.Files.Count(file => file.Category == id);
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
        Grid.SetColumn(name, 2); row.Children.Add(name);
        var note = TextLine(Loc.T("内置", "Built in"), 12, TextTertiary); note.VerticalAlignment = VerticalAlignment.Center; note.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(note, 2); row.Children.Add(note);
        var count = TextLine(CountText(ShelfSide.Clipboard, ShelfData.Favorites), 12, TextTertiary); count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(count, 3); row.Children.Add(count);
        return row;
    }
    private static Grid CategoryColumns()
    {
        var row = new Grid { Height = CategoryRowHeight, Background = Brushes.Transparent };
        foreach (var width in new[] { new GridLength(22), new GridLength(44), new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(36) })
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
        var count = TextLine(CountText(side, category.Id), 12, TextTertiary); count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(count, 3); row.Children.Add(count);
        // Deleting puts the entries back in "全部", where the history rule may then clear them, so it asks first.
        var remove = GlyphButton("M 0,0 L 8,8 M 8,0 L 0,8", 8, 8, Loc.T("删除 ", "Delete ") + category.Name, () =>
        {
            int inside = side == ShelfSide.Clipboard ? ShelfData.Clips.Count(clip => clip.Category == category.Id) : ShelfData.Files.Count(file => file.Category == category.Id);
            string what = side == ShelfSide.Clipboard
                ? Loc.T($"将删除分类“{category.Name}”。其中的 {inside} 条内容回到“全部”，之后按保留规则清理。", $"This deletes {category.Name}. Its {inside} entries go back to All, where the history rule applies to them.")
                : Loc.T($"将删除分类“{category.Name}”。其中的 {inside} 个文件回到“全部”，原文件不受影响。", $"This deletes {category.Name}. Its {inside} files go back to All; the original files are not touched.");
            if (inside > 0 && !Confirm(Loc.T("删除分类", "Delete category"), what, Loc.T("删除", "Delete"))) return;
            Shelf.DeleteCategory(ShelfData, side, category.Id);
            ShelfEdited(); ShowPage(_page);
        });
        remove.Width = 24; remove.Height = 26; remove.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(remove, 4); row.Children.Add(remove);
        var border = new Border { Child = row, Height = CategoryRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = category.Id, RenderTransform = new TranslateTransform() };
        AttachDrag(handle, category.Name, () => rows, CategoryRowHeight, list =>
        {
            Shelf.Reorder(ShelfData, side, [.. list.Children.OfType<Border>().Where(b => b.Tag is Guid).Select(b => (Guid)b.Tag)]);
            ShelfEdited();
        });
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

    private void PhraseList()
    {
        var list = new StackPanel();
        void Add(FrameworkElement row)
        {
            if (list.Children.Count > 0) list.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
            list.Children.Add(row);
        }
        if (_phraseEdit == Guid.Empty) Add(PhraseEditor(null));
        foreach (var phrase in ShelfData.Clips.Where(clip => clip.Manual))
            Add(_phraseEdit == phrase.Id ? PhraseEditor(phrase) : PhraseRow(phrase));
        if (list.Children.Count == 0)
        {
            var empty = TextLine(Loc.T("还没有常用语。", "No phrases yet."), 12.5, TextSecondary); empty.Margin = new Thickness(0, 12, 0, 12);
            list.Children.Add(empty);
        }
        Target.Children.Add(new Border { Child = list, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0, 1, 0, 0) });
        var add = ActionButton(Loc.T("新建常用语", "New phrase"), () => { _phraseEdit = Guid.Empty; ShowPage(_page); });
        add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new Thickness(0, 10, 0, 28); add.IsEnabled = _phraseEdit is null;
        Target.Children.Add(add);
    }
    private Grid PhraseRow(ClipEntry phrase)
    {
        var row = new Grid { Height = 40, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var (first, more) = Shelf.FirstLine(phrase.Text);
        var text = TextLine(more > 0 ? first + Loc.T($"  +{more} 行", $"  +{more} lines") : first, 13, TextPrimary);
        text.VerticalAlignment = VerticalAlignment.Center; text.TextTrimming = TextTrimming.CharacterEllipsis; text.Margin = new Thickness(2, 0, 12, 0);
        row.Children.Add(text);
        var edit = GlyphButton("M 10.5,2.5 L 13.5,5.5 L 6,13 H 3 V 10 Z", 12, 12, Loc.T("编辑常用语", "Edit phrase"), () => { _phraseEdit = phrase.Id; ShowPage(_page); });
        edit.Width = 28; edit.Height = 28; edit.IsEnabled = _phraseEdit is null;
        Grid.SetColumn(edit, 1); row.Children.Add(edit);
        var remove = GlyphButton("M 0,0 L 8,8 M 8,0 L 0,8", 8, 8, Loc.T("删除常用语", "Delete phrase"), () =>
        {
            if (!Confirm(Loc.T("删除常用语", "Delete phrase"), Loc.T($"将删除常用语“{first}”。", $"This deletes the phrase \"{first}\"."), Loc.T("删除", "Delete"))) return;
            _shelfOwnEdit = true;
            try { _app.Shelf.Remove([phrase]); } finally { _shelfOwnEdit = false; }
            ShowPage(_page);
        });
        remove.Width = 28; remove.Height = 28; remove.Margin = new Thickness(2, 0, 4, 0);
        Grid.SetColumn(remove, 2); row.Children.Add(remove);
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

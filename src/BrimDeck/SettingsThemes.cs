using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// The "面板主题" page: the basic themes, the built-in extensions, and the user's own themes (the files in the themes folder),
// which can be opened or deleted here. Each card shows the collapsed capsule in its theme, as the collapsed view's style cards
// do: a fixed miniature that does not follow later changes to the panel. Choosing a card applies it at once and holds the panel
// open as a preview; what the user changed under the theme being left is kept for it (ThemeChoices). While the page is shown,
// saving a file in the themes folder reloads it, so a theme can be edited with the panel beside it.
public sealed partial class SettingsWindow
{
    internal const int ThemePage = 8, MonitorSettingsPage = 9;
    private FileSystemWatcher? _themeWatcher;
    private DispatcherTimer? _themeReload;
    // The themes folder as last read (file names, sizes and times), so a watcher event that changed nothing reloads nothing.
    private string _themeStamp = "";
    // A stamp no folder has, so the next watcher tick reloads.
    private const string UnknownStamp = "?";
    // Set when the folder needs no new read for this rebuild: the theme changed without the page, or the page just read it.
    // Saved files are covered by the watcher, and the Reload button reads it again.
    private bool _themesFresh;
    // The "当前主题" section, replaced alone when a card is chosen.
    private StackPanel? _themeDetails;
    // Each card's miniature, kept while its palette stays the same; a file theme gets a new palette each time it is read.
    private readonly Dictionary<(string Card, string Language), (PanelPalette Palette, FrameworkElement Preview)> _themePreviews = [];

    private void ThemesPage()
    {
        WatchThemeFolder(true);
        // The stamp is taken just before the read, so a write during the read still differs from it.
        if (!_themesFresh) { _themeStamp = ThemeFolderStamp(); _app.ApplyPanelTheme(reload: true); }
        _themesFresh = false;
        var chosen = PanelThemes.NormalizeId(S.PanelTheme);
        var users = PanelTheme.UserThemes;
        ThemeSection(Loc.T("基础", "Basics"));
        ThemeCards([(PanelThemes.SystemId, Loc.T("跟随系统", "System"), PanelTheme.Find(PanelThemes.SystemId)),
            .. PanelThemes.BuiltIns.Where(t => t.Kind == PanelThemeKind.Basic).Select(Card)], chosen);
        ThemeSection(Loc.T("主题扩展", "Theme extensions"));
        ThemeCards([.. PanelThemes.BuiltIns.Where(t => t.Kind == PanelThemeKind.Extension).Select(Card)], chosen);
        CustomSection(users, chosen);
        _themeDetails = Collect(() => ThemeDetails(chosen, users)); _content.Children.Add(_themeDetails);
        // Miniatures of cards no longer on the page (a theme file renamed or removed) are let go.
        var cards = new[] { PanelThemes.SystemId }.Concat(PanelThemes.BuiltIns.Select(t => t.Id)).Concat(users.Where(t => t.Usable).Select(t => t.Id)).ToHashSet();
        foreach (var key in _themePreviews.Keys.Where(key => !cards.Contains(key.Card) || key.Language != Loc.Language).ToList()) _themePreviews.Remove(key);
        static (string Id, string Name, PanelThemeInfo Theme) Card(PanelThemeInfo theme) => (theme.Id, theme.DisplayName, theme);
    }

    // A section title with a divider under it, as the other pages' groups start.
    private void ThemeSection(string title)
    {
        SectionTitle(title);
        Target.Children.Add(new Border { Height = 1, Background = UI.Brush(Divider), Margin = new Thickness(0, 0, 0, 12) });
    }

    private void Note(string text, double size, string color, Thickness margin)
    {
        var note = TextLine(text, size, color);
        note.TextWrapping = TextWrapping.Wrap; note.Margin = margin; _content.Children.Add(note);
    }

    // Three cards to a row, each the collapsed capsule in that theme. A user's own theme can be renamed by clicking its name and
    // carries a delete button on its preview (OwnThemeCard), and the custom section ends with the new-theme card (tile).
    private void ThemeCards(IReadOnlyList<(string Id, string Name, PanelThemeInfo Theme)> themes, string chosen, FrameworkElement? tile = null)
    {
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(-6, 0, -6, 18) };
        foreach (var (id, name, theme) in themes)
        {
            bool own = theme.Path is not null;
            // The chosen theme of the user's with unsaved changes offers to save them over it in place of its card.
            if (own && id == chosen && !PanelTheme.KeptLastGood && PanelTheme.DraftOf(id) is not null) { grid.Children.Add(OverwriteCard(theme)); continue; }
            var card = KeepPreview(new RadioButton { Style = (Style)FindResource("SettingsChoice"), Content = ThemePreview(id, theme),
                Margin = new Thickness(6, 3, 6, 6), Tag = own ? "" : name, GroupName = "PanelTheme", IsChecked = id == chosen });
            AutomationProperties.SetName(card, Loc.T("面板主题 ", "Panel theme ") + name);
            AutomationProperties.SetAutomationId(card, "panel-theme-" + id);
            card.Checked += (_, _) =>
            {
                if (PanelThemes.NormalizeId(S.PanelTheme) == id) return;
                Change(s => ChooseTheme(s, id));
                _preview = true; _app.Deck.Preview(true);
                // The cards stay as they are, the radio group moving the check; only the details change. A choice that did not
                // take (the settings could not be updated) rebuilds the page to show what is saved.
                if (PanelThemes.NormalizeId(S.PanelTheme) != id || _themeDetails is null || !_content.Children.Contains(_themeDetails)) { _themesFresh = true; ShowPage(ThemePage); return; }
                int at = _content.Children.IndexOf(_themeDetails);
                _themeDetails = Collect(() => ThemeDetails(id, PanelTheme.UserThemes));
                // A panel's slot cannot take a new child while the old one is still in it, so the old one leaves first.
                _content.Children.RemoveAt(at); _content.Children.Insert(at, _themeDetails);
            };
            grid.Children.Add(own ? OwnThemeCard(card, theme) : card);
        }
        if (tile is not null) grid.Children.Add(tile);
        _content.Children.Add(grid);
    }

    // A theme and what goes with it: what the user changed under the theme being left is kept for it, and the theme chosen brings
    // back what was changed under it, or else its own app colors and suggested clock (PanelThemes.SwitchTheme). Restore defaults
    // on this page goes through here too.
    private static void ChooseTheme(DeckSettings settings, string id)
        => PanelThemes.SwitchTheme(settings, settings.PanelTheme, PanelTheme.Current, id, PanelThemes.Resolve(PanelTheme.Find(id)));

    private FrameworkElement ThemePreview(string card, PanelThemeInfo theme)
    {
        var palette = PanelThemes.Resolve(theme);
        var key = (card, Loc.Language);
        if (_themePreviews.TryGetValue(key, out var kept) && ReferenceEquals(kept.Palette, palette))
        {
            // The miniature may still belong to a card of the page this one replaces.
            if (kept.Preview.Parent is ContentControl previous) previous.Content = null;
            return kept.Preview;
        }
        // The collapsed capsule with its quota rings and clock, at its real size, in this theme's colors.
        var preview = CompactStylePreview.Theme(palette);
        _themePreviews[key] = (palette, preview);
        return preview;
    }

    private void ThemeDetails(string chosen, IReadOnlyList<PanelThemeInfo> users)
    {
        bool kept = PanelTheme.KeptLastGood;
        var theme = kept ? PanelTheme.ShownTheme : PanelTheme.Find(chosen);
        bool missing = !kept && chosen != PanelThemes.SystemId && !PanelThemes.IsBuiltIn(chosen) && users.All(t => !t.Usable || t.Id != chosen);
        SectionTitle(Loc.T("当前主题", "Current theme"));
        var rows = new List<FrameworkElement>();
        string title = chosen == PanelThemes.SystemId ? Loc.T("跟随系统（当前：", "System (now: ") + theme.DisplayName + Loc.T("）", ")") : theme.DisplayName;
        rows.Add(Row(Loc.T("名称", "Name"), TextLine(title, 12, TextSecondary)));
        string description = chosen == PanelThemes.SystemId ? Loc.T("跟随 Windows 的应用主题，在深色与浅色之间切换。", "Follows the Windows app theme, switching between dark and light.") : theme.DisplayDescription;
        if (description.Length > 0) rows.Add(Row(Loc.T("说明", "About"), Wrapped(description, false)));
        if (theme.File.Night is not null) rows.Add(DaylightRow(theme.File));
        // A file that carries the chosen id (or is named after it) but cannot be used is there, with its error listed below.
        bool broken = missing && users.Any(t => !t.Usable && (PanelThemes.NormalizeId(t.File.Id) == chosen || t.Path is { } file && PanelThemes.NormalizeId(PanelThemes.FileName(file)) == chosen));
        if (missing)
            rows.Add(Row(Loc.T("提示", "Note"), Wrapped(broken
                ? Loc.T("主题文件有错误（见下方），暂时显示为深色；修正并保存后即可恢复。", "The theme file has an error (see below) and the dark theme is shown meanwhile. Fix and save the file to restore it.")
                : Loc.T($"找不到主题“{chosen}”，暂时显示为深色。放回主题文件并重新加载后即可恢复。",
                    $"The theme \"{chosen}\" was not found and the dark theme is shown meanwhile. Put the theme file back and reload to restore it."), true)));
        if (kept)
            rows.Add(Row(Loc.T("提示", "Note"), Wrapped(Loc.T("主题文件现在有错误（见下方），面板暂时保留它上次正确的版本；修正并保存后即恢复同步。",
                "The theme file has an error now (see below). The panel keeps its last good version until the file is fixed and saved."), true)));
        // A user's own theme file says who made it and where it comes from; the built-in themes' notices are in ThirdPartyNotices.txt.
        if (!theme.BuiltIn)
        {
            if (theme.File.Author.Length > 0) rows.Add(Row(Loc.T("作者", "Author"), TextLine(theme.File.Author, 12, TextSecondary)));
            if (theme.File.Homepage.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) rows.Add(Row(Loc.T("来源", "Source"), Link(Loc.T("主页 ↗", "Homepage ↗"), theme.File.Homepage)));
        }
        if (theme.Path is { } path)
        {
            var open = ActionButton(Path.GetFileName(path) + " ↗", () => OpenThemeFile(path)); open.Style = (Style)FindResource("SettingsLink"); open.ToolTip = path;
            rows.Add(Row(Loc.T("文件", "File"), open));
        }
        // The colors moved to stay readable, counted here and listed once opened; with unsaved changes, those of the theme as
        // it would be saved.
        rows.AddRange(ReadabilityRows(PanelTheme.PaletteOf(theme).Adjustments));
        Group([.. rows]);
        // How opaque the panel's background is. It belongs to the panel, not to a theme file, so it stays even while a broken
        // theme is shown in place of the chosen one.
        SectionTitle(Loc.T("透明度", "Transparency"));
        Group(SliderRow(Loc.T("背景不透明度", "Background opacity"), S.PanelOpacity, 0, 100, 10, "%",
            value => Change(s => s.PanelOpacity = (int)value),
            help: Loc.T("面板背景的不透明度，每 10% 一档；文字与图标不受影响。",
                "How opaque the panel's background is, in 10% steps; text and icons stay as they are.")));
        // A theme shown in place of a missing or broken one is not the one chosen, so it is not offered for editing.
        if (!missing && !kept) ThemeEditor(theme);
    }

    // A theme with a night look: when its day begins and ends, in half hours. The theme's own hours are saved as no setting,
    // so a later change to the theme's hours still reaches those who never moved them. A time between the half hours (a theme
    // file's 06:45) is listed as it is, so moving the other box does not round it.
    private FrameworkElement DaylightRow(PanelThemeFile file)
    {
        var (day, night) = PanelThemes.DaylightHours(file, S.ThemeDaylight);
        var steps = Enumerable.Range(0, 48).Select(i => TimeSpan.FromMinutes(30 * i)).ToList();
        (ComboBox Box, List<TimeSpan> Times) Pick(TimeSpan value, string name)
        {
            var times = steps.Contains(value) ? steps : [.. steps.Append(value).Order()];
            // The time stands in the middle of its box and of the list; the list opens on the chosen time, a few rows down.
            var box = new ComboBox { Width = 84, Style = (Style)FindResource("SettingsCombo"), ItemsSource = times.Select(PanelThemes.FormatTimeOfDay).ToList(),
                SelectedIndex = times.IndexOf(value) is var at and >= 0 ? at : 0, HorizontalContentAlignment = HorizontalAlignment.Center };
            box.DropDownOpened += (_, _) => box.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ShowChosen(box));
            AutomationProperties.SetName(box, name); PopupMotion.Attach(box);
            return (box, times);
        }
        var (from, fromTimes) = Pick(day, Loc.T("白天开始", "Day begins"));
        var (to, toTimes) = Pick(night, Loc.T("夜晚开始", "Night begins"));
        // A day of no length would never end, so it is no choice: the box just changed goes back to what it showed, and the row
        // never shows unsaved hours. Going back changes the selection again, which saves nothing.
        int shownFrom = from.SelectedIndex, shownTo = to.SelectedIndex;
        bool reverting = false;
        void Save(ComboBox changed)
        {
            if (reverting) return;
            var (d, n) = (fromTimes[Math.Max(0, from.SelectedIndex)], toTimes[Math.Max(0, to.SelectedIndex)]);
            if (d == n)
            {
                reverting = true;
                changed.SelectedIndex = ReferenceEquals(changed, from) ? shownFrom : shownTo;
                reverting = false;
                return;
            }
            shownFrom = from.SelectedIndex; shownTo = to.SelectedIndex;
            Change(s => s.ThemeDaylight = (d, n) == PanelThemes.DaylightHours(file) ? "" : PanelThemes.FormatDaylight(d, n));
        }
        from.SelectionChanged += (_, _) => Save(from); to.SelectionChanged += (_, _) => Save(to);
        var dash = TextLine("–", 12, TextSecondary);
        dash.Margin = new Thickness(8, 0, 8, 0); dash.VerticalAlignment = VerticalAlignment.Center;
        var pair = new StackPanel { Orientation = Orientation.Horizontal };
        pair.Children.Add(from); pair.Children.Add(dash); pair.Children.Add(to);
        return Row(Loc.T("白天时段", "Daytime"), pair, help: () => Loc.T("在这段时间内使用白天外观，其余时间使用夜间外观；到点时自动渐变切换。",
            "The day look is shown between these times and the night look the rest of the day; it changes over by itself, with a slow fade."));
    }

    // Scrolls an open list so its chosen item stands about a third of the way down, with the times before it in view.
    private static void ShowChosen(ComboBox box)
    {
        if (box.ItemContainerGenerator.ContainerFromIndex(box.SelectedIndex) is not FrameworkElement item
            || FindParent<ScrollViewer>(item) is not { } scroll) return;
        double top = item.TransformToAncestor(scroll).Transform(new Point()).Y + scroll.VerticalOffset;
        scroll.ScrollToVerticalOffset(Math.Max(0, top - (scroll.ViewportHeight - item.ActualHeight) / 3));
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var node = System.Windows.Media.VisualTreeHelper.GetParent(child); node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is T found) return found;
        return null;
    }

    private TextBlock Wrapped(string text, bool error)
    {
        var block = TextLine(text, 11.5, error ? (_palette.Dark ? "#F0A6AA" : "#B12E3A") : TextSecondary);
        block.TextWrapping = TextWrapping.Wrap; block.TextTrimming = TextTrimming.None; block.MaxWidth = 460; block.TextAlignment = TextAlignment.Right;
        return block;
    }

    // The custom section: the title with a reload button, and "Open themes folder" and the theme guide at the right; the user's
    // themes as cards, each renamed by clicking its name and deleted from its preview; and a card with a plus that makes a new
    // theme from the current one. Files that cannot be used are listed under it with their errors.
    private void CustomSection(IReadOnlyList<PanelThemeInfo> users, string chosen)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var guide = Link(Loc.T("主题制作指南 ↗", "Theme guide ↗"), Updates.GitHubUpdates.RepositoryUrl + Loc.T("/blob/main/docs/zh-CN/themes.md", "/blob/main/docs/en-US/themes.md"));
        guide.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(guide, Dock.Right); head.Children.Add(guide);
        // The folder holds the theme files, so it is opened from the section's title row, beside the guide to writing them.
        var folder = ActionButton(Loc.T("打开主题文件夹", "Open themes folder"), () =>
        {
            try { Directory.CreateDirectory(PanelTheme.Folder); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + PanelTheme.Folder + "\"") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { SaveStatus(ex.Message, true); }
        });
        folder.Style = (Style)FindResource("SettingsLink");
        folder.ToolTip = Loc.T("主题文件放在这里：*.json，或子文件夹中的 theme.json。", "Theme files live here: *.json, or theme.json in a subfolder.");
        folder.VerticalAlignment = VerticalAlignment.Center; folder.Margin = new Thickness(0, 0, 16, 0);
        DockPanel.SetDock(folder, Dock.Right); head.Children.Add(folder);
        var title = TextLine(Loc.T("自定义", "Custom"), 11, TextSecondary); title.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(title);
        var reload = GlyphButton("M 12.5,4.5 A 5.5,5.5 0 1 0 13,9 M 12.5,1.5 V 4.5 H 9.5", 14, 14, Loc.T("重新加载主题文件", "Reload theme files"), () => ShowPage(ThemePage));
        reload.Width = 22; reload.Height = 22; reload.Margin = new Thickness(4, 0, 0, 0); reload.VerticalAlignment = VerticalAlignment.Center;
        reload.HorizontalAlignment = HorizontalAlignment.Left; // the last child fills the rest of the row; the icon stays beside the title
        // Measured on the rendered row in both settings themes, the arrow's ink centre sat 1 DIP above the title's.
        reload.RenderTransform = new System.Windows.Media.TranslateTransform(0, 1);
        head.Children.Add(reload);
        Target.Children.Add(head);
        Target.Children.Add(new Border { Height = 1, Background = UI.Brush(Divider), Margin = new Thickness(0, 0, 0, 12) });
        // In the order they were added, so a new theme takes the place of the card that made it, at the end.
        var own = PanelThemes.InAddedOrder(users.Where(t => t.Usable)).ToList();
        ThemeCards([.. own.Select(t => (t.Id, t.DisplayName, t))], chosen, CustomTile());
        var broken = users.Where(t => !t.Usable).ToList();
        if (broken.Count > 0)
            Group([.. broken.Select(t => (FrameworkElement)Row(t.Path is { } file ? PanelThemes.DisplayFileName(file) : t.Id, Wrapped(Loc.T("无法使用：", "Cannot be used: ") + t.Error, true)))]);
    }

    // The new-theme card: a theme card's frame with a plus in place of the preview. The new theme starts as a copy of the current
    // one and is chosen at once. While the current theme has unsaved changes, the card saves the theme as changed as a new theme
    // instead (SaveCard).
    private FrameworkElement CustomTile()
    {
        if (!PanelTheme.KeptLastGood && PanelTheme.DraftOf(PanelTheme.ShownTheme.Id) is not null)
        {
            var save = SaveCard(Loc.T("保存当前主题", "Save current theme"), null, SaveThemeDraftAsNew);
            save.ToolTip = Loc.T("把当前主题连同尚未保存的修改保存为一个新的自定义主题，并切换到它。",
                "Saves the current theme with its unsaved changes as a new custom theme and switches to it.");
            AutomationProperties.SetName(save, Loc.T("保存当前主题为新主题", "Save current theme as a new theme"));
            return save;
        }
        var create = ActionButton(Loc.T("以当前主题新建", "New from current theme"), () =>
        {
            try
            {
                // The theme on screen, which is the last good version while the chosen file has an error.
                var current = PanelTheme.ShownTheme;
                // A theme with a night look starts from its day look and carries its night look along, whichever is on screen.
                var path = PanelThemes.CreateFromTemplate(PanelTheme.Folder, current.File.Night is null ? PanelTheme.Current : PanelThemes.Resolve(current), current.BuiltIn ? current.Id : null, current.File);
                PanelTheme.Reload();
                // The new theme copies the current one, so it starts with what was changed under the current one.
                ChooseOwnTheme(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SaveStatus(Loc.T("无法创建主题文件：", "Could not create the theme file: ") + ex.Message, true); }
            _themesFresh = true; ShowPage(ThemePage);
        });
        create.ToolTip = Loc.T("把当前主题的全部颜色写入一个新的自定义主题，并切换到它。", "Writes every color of the current theme into a new custom theme and switches to it.");
        create.Style = (Style)FindResource("SettingsAddCard"); create.Content = null;
        create.Tag = Loc.T("以当前主题新建", "New from current theme"); create.Margin = new Thickness(6, 3, 6, 6);
        AutomationProperties.SetName(create, Loc.T("以当前主题新建主题", "New theme from current theme"));
        return create;
    }

    // A user theme's card with what only a user's theme has. Its name (the card's own is left empty) renames the theme when
    // clicked: a text box takes its place, Enter or leaving it renames the file's theme (its name only, PanelThemes.RenameFile),
    // Esc keeps the name. Each card renames its own file, so choosing another card meanwhile still renames this one. On the
    // preview's top right corner, shown while the pointer is on the card or the button has focus, a delete button asks first.
    private FrameworkElement OwnThemeCard(RadioButton card, PanelThemeInfo theme)
    {
        var path = theme.Path!;
        var cell = new Grid(); cell.Children.Add(card);
        // The name row is the card's: 30 high under the preview, the check 14 wide at its right; the name keeps clear of the check
        // on both sides so it stays centred.
        var row = new Grid { Height = 30, Margin = new Thickness(6 + 8 + 18, 0, 6 + 8 + 18, 6), VerticalAlignment = VerticalAlignment.Bottom };
        var name = new Button { Content = theme.DisplayName, Style = (Style)FindResource("SettingsCardName"), HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = Loc.T("单击重命名", "Click to rename") };
        // The name looks as the card's own name would: brighter and bolder once chosen or under the pointer.
        name.SetBinding(ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = card });
        name.SetBinding(FontWeightProperty, new System.Windows.Data.Binding(nameof(FontWeight)) { Source = card });
        name.SetBinding(FontSizeProperty, new System.Windows.Data.Binding(nameof(FontSize)) { Source = card });
        AutomationProperties.SetName(name, Loc.T("重命名主题 ", "Rename theme ") + theme.DisplayName);
        var box = new TextBox { Style = (Style)FindResource("SettingsField"), Height = 26, FontSize = 12, Padding = new Thickness(6, 0, 6, 0),
            TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed, MaxLength = 60 };
        AutomationProperties.SetName(box, Loc.T("主题名称", "Theme name"));
        bool editing = false;
        void Finish(bool keep)
        {
            if (!editing) return;
            editing = false;
            string text = box.Text.Trim();
            box.Visibility = Visibility.Collapsed; name.Visibility = Visibility.Visible;
            if (!keep || text.Length == 0 || text == theme.DisplayName) return;
            try { PanelThemes.RenameFile(path, text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SaveStatus(Loc.T("无法重命名主题：", "Could not rename the theme: ") + ex.Message, true); return; }
            name.Content = text;
            // The page is read again once the click that ended the edit (on another card, say) has been handled, not under it.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (IsLoaded && _page == ThemePage) { _themesFresh = false; ShowPage(ThemePage); } });
        }
        name.Click += (_, _) =>
        {
            editing = true; box.Text = theme.DisplayName;
            name.Visibility = Visibility.Collapsed; box.Visibility = Visibility.Visible;
            box.Focus(); box.SelectAll();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { Finish(true); card.Focus(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape) { Finish(false); card.Focus(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        row.Children.Add(name); row.Children.Add(box);
        cell.Children.Add(row);

        var delete = new Button { Style = (Style)FindResource("SettingsCardDelete"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3 + 8, 6 + 8, 0), Opacity = 0, ToolTip = Loc.T("删除主题", "Delete theme") };
        AutomationProperties.SetName(delete, Loc.T("删除主题 ", "Delete theme ") + theme.DisplayName);
        void Show() => delete.Opacity = cell.IsMouseOver || delete.IsKeyboardFocused ? 1 : 0;
        cell.MouseEnter += (_, _) => Show(); cell.MouseLeave += (_, _) => Show();
        delete.GotKeyboardFocus += (_, _) => Show(); delete.LostKeyboardFocus += (_, _) => Show();
        delete.Click += (_, _) =>
        {
            if (Confirm(Loc.T("删除主题", "Delete theme"),
                Loc.T($"确定删除“{theme.DisplayName}”吗？主题文件会移到回收站，为它记住的配色也会一并清除。",
                    $"Delete \"{theme.DisplayName}\"? The theme file goes to the Recycle Bin, and the colors remembered for it are cleared."),
                Loc.T("删除", "Delete")))
                DeleteOwnTheme(theme);
        };
        cell.Children.Add(delete);
        return cell;
    }

    // A theme in its own folder (theme.json) takes the folder with it; a single file goes alone. The theme in use gives way to
    // the dark theme first, and the theme's saved choices go with it.
    private void DeleteOwnTheme(PanelThemeInfo theme)
    {
        var path = theme.Path!;
        var target = string.Equals(Path.GetFileName(path), "theme.json", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path)! : path;
        if (!Native.RecycleBin.Send(target)) { SaveStatus(Loc.T("无法删除主题文件：", "Could not delete the theme file: ") + target, true); return; }
        string id = theme.Id;
        PanelTheme.Discard(id);
        Change(s =>
        {
            if (PanelThemes.NormalizeId(s.PanelTheme) == id) ChooseTheme(s, PanelThemes.Dark);
            PanelThemes.ForgetTheme(s, id);
        });
        _themesFresh = false; ShowPage(ThemePage);
    }

    private void OpenThemeFile(string path)
    {
        if (!File.Exists(path)) { SaveStatus(Loc.T("主题文件不存在：", "The theme file does not exist: ") + path, true); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No program is set for .json files.
            try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            catch (Win32Exception error) { SaveStatus(error.Message, true); }
        }
    }

    // The panel theme changed without this page, or Windows switched between light and dark (the "System" card shows it).
    // The collapsed view (style cards, clock tiles) and the quotas page (default color swatches) show theme colors too.
    internal void PanelThemeChanged(bool onlyThemePage = false)
    {
        if (!IsLoaded || (onlyThemePage ? _page != ThemePage : _page is not (1 or 3 or ThemePage or MonitorSettingsPage))) return;
        _themesFresh = _page == ThemePage; ShowPage(_page);
    }

    // Names, sizes and times of the files LoadFolder reads: a file's content cannot change without one of them changing.
    private static string ThemeFolderStamp()
    {
        try
        {
            var folder = new DirectoryInfo(PanelTheme.Folder);
            if (!folder.Exists) return "";
            return string.Join("|", folder.EnumerateFiles("*.json").Concat(folder.EnumerateDirectories().Select(dir => new FileInfo(Path.Combine(dir.FullName, "theme.json"))).Where(file => file.Exists))
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).Select(file => $"{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Guid.NewGuid().ToString(); }
    }

    // Saving a theme file while the page is shown reloads the themes; editors write a file in several steps, so changes are
    // gathered for a moment first.
    private void WatchThemeFolder(bool on)
    {
        if (!on) { _themeWatcher?.Dispose(); _themeWatcher = null; _themeReload?.Stop(); return; }
        if (_themeWatcher is not null) return;
        try
        {
            Directory.CreateDirectory(PanelTheme.Folder);
            if (_themeReload is null)
            {
                _themeReload = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
                _themeReload.Tick += (_, _) =>
                {
                    _themeReload.Stop();
                    if (IsLoaded && _page == ThemePage && ThemeFolderStamp() != _themeStamp) ShowPage(ThemePage);
                };
            }
            void Restart() { _themeReload.Stop(); _themeReload.Start(); }
            var watcher = new FileSystemWatcher(PanelTheme.Folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Changed(object? _, FileSystemEventArgs e)
            {
                if (!e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && e.ChangeType != WatcherChangeTypes.Deleted && e.ChangeType != WatcherChangeTypes.Renamed) return;
                Dispatcher.BeginInvoke(Restart);
            }
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
            // A burst of changes can overflow the watcher's buffer, and a removed folder stops it; the rebuild that follows makes a
            // new one and reads whatever was missed.
            watcher.Error += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_themeWatcher, watcher)) return;
                _themeWatcher = null; watcher.Dispose(); _themeStamp = UnknownStamp;
                Restart();
            });
            watcher.EnableRaisingEvents = true;
            _themeWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { _themeWatcher = null; }
    }
}

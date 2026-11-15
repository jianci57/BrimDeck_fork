using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The displays and profiles page: the named profiles (DeckProfile), where the panels show and the profile each display shows
// (DisplayProfiles), and the rules that switch profiles while an app runs or is in front (ProfileRule). What it changes is shared by
// every profile, so it edits the app's settings directly (ChangeShared) rather than the profile being edited.
public sealed partial class SettingsWindow
{
    internal const int ProfilesPage = 16;

    private void ChangeShared(Action<DeckSettings> edit, bool rebuild = false)
    {
        var settings = _app.Settings.Copy(); edit(settings); _app.UpdateSettings(settings);
        if (rebuild && IsLoaded && _page == ProfilesPage) ShowPage(ProfilesPage);
    }

    // A rule switched profiles, the user chose another one, or the list changed: the page and the choice of profile to edit follow.
    internal void ProfilesChanged()
    {
        if (!IsLoaded) return;
        if (_preview) EndPreview();
        ShowPage(_page);
    }

    private void ProfilesSettings()
    {
        var shared = _app.Settings;
        // While a rule holds, which one; what it does is in the rule below.
        if (_app.HoldingRule is { } holding)
        {
            string app = AppName(holding.Name, holding.Path);
            ProfileNotice(_palette.Accent, holding.Trigger == ProfileTrigger.Foreground
                ? Loc.T($"自动切换规则生效中：{app} 在前台。", $"An automatic switch rule applies: {app} is in front.")
                : Loc.T($"自动切换规则生效中：{app} 正在运行。", $"An automatic switch rule applies: {app} is running."));
        }
        // Two joined buttons make a new profile, made current at once with its name ready to change: a copy of the one in use, or
        // every setting at its default.
        var create = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), IsEnabled = shared.Profiles.Count < Profiles.Maximum,
            Opacity = shared.Profiles.Count < Profiles.Maximum ? 1 : .4 };
        create.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        create.SetResourceReference(Border.BackgroundProperty, "SettingsInput");
        var pair = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, blank, corners) in new[] { (Loc.T("以当前配置新建", "New from current"), false, new CornerRadius(5, 0, 0, 5)),
            (Loc.T("以默认设置新建", "New with defaults"), true, new CornerRadius(0, 5, 5, 0)) })
        {
            if (pair.Children.Count > 0)
            {
                var rule = new Border { Width = 1 };
                rule.SetResourceReference(Border.BackgroundProperty, "SettingsBorder");
                pair.Children.Add(rule);
            }
            var button = new Button { Content = label, Style = (Style)FindResource("SettingsJoinedButton"), Tag = corners };
            button.Click += (_, _) => { var id = Guid.NewGuid(); _renamingProfile = id; _app.CreateProfile(id, blank); };
            pair.Children.Add(button);
        }
        create.Child = pair;
        SectionTitle(Loc.T("配置", "Profiles"), create);
        Group([.. shared.Profiles.Select(profile => ProfileRow(shared, profile))]);
        DisplayProfiles(shared);
        ProfileRules(shared);
    }

    // A line above the profiles: a colored dot and what it is about.
    private void ProfileNotice(string color, string text)
    {
        var words = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, MinHeight = 36 };
        words.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = UI.Brush(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 10, 0) });
        var label = TextLine(text, 12.5, TextPrimary); label.VerticalAlignment = VerticalAlignment.Center; label.TextTrimming = TextTrimming.CharacterEllipsis;
        words.Children.Add(label);
        var frame = new Border { Child = words, CornerRadius = new CornerRadius(10), BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 0, 20) };
        frame.SetResourceReference(Border.BackgroundProperty, "SettingsInset");
        _content.Children.Add(frame);
    }

    // The profile made last, whose name is edited as soon as its row shows (the new-profile button).
    private Guid? _renamingProfile;

    // One profile: its name, and "使用中" on the one in use. At the right, "使用" for
    // the others, and a menu to rename, copy or delete it. A click on the name renames it too.
    private FrameworkElement ProfileRow(DeckSettings shared, DeckProfile profile)
    {
        string name = Profiles.Name(profile);
        var id = profile.Id;
        bool current = id == shared.CurrentProfile;
        var row = new Grid { MinHeight = 52 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 12, 6) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new Button { Content = name, Style = (Style)FindResource("SettingsCardName"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(TextPrimary), Margin = new Thickness(-4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("单击重命名", "Click to rename") };
        AutomationProperties.SetName(title, Loc.T("重命名 ", "Rename ") + name);
        var box = new TextBox { Style = (Style)FindResource("SettingsField"), Height = 26, FontSize = 12.5, Padding = new Thickness(6, 0, 6, 0), Width = 220,
            Visibility = Visibility.Collapsed, MaxLength = Profiles.MaximumName };
        AutomationProperties.SetName(box, Loc.T("配置名称", "Profile name"));
        heading.Children.Add(title); heading.Children.Add(box);
        if (current)
        {
            var mark = TextLine(Loc.T("使用中", "In use"), 11, _palette.Accent);
            heading.Children.Add(new Border { Child = mark, BorderBrush = UI.Brush(_palette.Accent), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        labels.Children.Add(heading);
        row.Children.Add(labels);
        bool editing = false;
        void Edit() { editing = true; box.Text = name; title.Visibility = Visibility.Collapsed; box.Visibility = Visibility.Visible; box.Focus(); box.SelectAll(); }
        void Finish(bool keep)
        {
            if (!editing) return;
            editing = false;
            string text = Profiles.Clean(box.Text);
            box.Visibility = Visibility.Collapsed; title.Visibility = Visibility.Visible;
            if (!keep || text.Length == 0 || text == name) return;
            // Once the click that ended the edit has been handled, not under it.
            Dispatcher.BeginInvoke(() => ChangeShared(s => { if (Profiles.Find(s, id) is { } found) found.Name = Profiles.FreeName(s, text, id); }));
        }
        title.Click += (_, _) => Edit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        if (_renamingProfile == id) { _renamingProfile = null; Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, Edit); }

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!current)
        {
            var use = ActionButton(Loc.T("使用", "Use"), () => _app.ChooseProfile(id)); use.Style = (Style)FindResource("SettingsOutlineButton");
            AutomationProperties.SetName(use, Loc.T("使用 ", "Use ") + name); use.Margin = new Thickness(0, 0, 4, 0);
            controls.Children.Add(use);
        }
        // Three dots in a 14 by 4 box, centred as the box is.
        Button? more = null;
        more = GlyphButton("M 1.9,2 L 2.1,2 M 6.9,2 L 7.1,2 M 11.9,2 L 12.1,2", 14, 4, Loc.T("更多操作：", "More: ") + name, () =>
        {
            var menu = AppMenu(more!);
            var rename = new MenuItem { Header = Loc.T("重命名", "Rename") }; rename.Click += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, Edit);
            var copy = new MenuItem { Header = Loc.T("复制", "Copy"), IsEnabled = _app.Settings.Profiles.Count < Profiles.Maximum };
            copy.Click += (_, _) => ChangeShared(s => s.Profiles.Add(Profiles.Duplicate(s, id, Profiles.NextName(s))));
            var delete = new MenuItem { Header = Loc.T("删除", "Delete"), IsEnabled = !current };
            delete.Click += (_, _) =>
            {
                // What changes is said only for what uses the profile, the rules by their apps' names.
                var now = _app.Settings;
                var lines = new List<string> { Loc.T($"确定删除“{name}”吗？", $"Delete “{name}”?") };
                if (now.Display == DisplayPlacement.Chosen && now.DisplayMonitors.Any(d => d.Profile == id))
                    lines.Add(Loc.T("使用它的显示器会自动改为当前正在使用的配置。", "Displays that use it will change to the profile in use."));
                string Apps(IEnumerable<ProfileRule> rules) => string.Join(Loc.T("、", ", "), rules.Select(r => AppName(r.Name, r.Path)).Distinct());
                if (now.ProfileRules.Where(r => r.Profile == id).ToList() is { Count: > 0 } switching)
                    lines.Add(Loc.T($"删除后，{Apps(switching)}的自动切换规则不会再切换配置。", $"The automatic switch rules for {Apps(switching)} will no longer switch profiles."));
                if (now.ProfileRules.Where(r => r.Displays.Any(d => d.Profile == id)).ToList() is { Count: > 0 } showing)
                    lines.Add(Loc.T($"{Apps(showing)}的规则中使用它的显示器，会自动改为跟随当前正在使用的配置。", $"In the rules for {Apps(showing)}, displays that use it will follow the profile in use."));
                string text = string.Join(Environment.NewLine, lines);
                if (!Confirm(Loc.T("删除配置", "Delete profile"), text, Loc.T("删除", "Delete"))) return;
                ChangeShared(s => s.Profiles.RemoveAll(p => p.Id == id));
            };
            menu.Items.Add(rename); menu.Items.Add(copy); menu.Items.Add(new Separator()); menu.Items.Add(delete);
            menu.IsOpen = true;
        });
        if (more.Content is System.Windows.Shapes.Path dots) dots.StrokeThickness = 2.2;
        more.Width = 28; more.Height = 28;
        controls.Children.Add(more);
        Grid.SetColumn(controls, 1); row.Children.Add(controls);
        return row;
    }

    private ComboBox ProfileCombo(string name, IReadOnlyList<string> options, int selected, Action<int> chosen, double width = 200)
    {
        var combo = new ComboBox { Width = width, Style = (Style)FindResource("SettingsCombo"), ItemsSource = options.ToList(), SelectedIndex = Math.Clamp(selected, 0, options.Count - 1) };
        AutomationProperties.SetName(combo, name); PopupMotion.Attach(combo);
        int last = combo.SelectedIndex;
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex < 0 || combo.SelectedIndex == last) return; last = combo.SelectedIndex; chosen(last); };
        return combo;
    }

    private static string InUseLabel => Loc.T("跟随使用中的配置", "Follow the profile in use");
    private static string NoPanelLabel => Loc.T("不显示面板", "No panel");

    // What a display shows outside rules, for the rules' "跟随显示器设置": the profile in use, a profile's name, or no panel.
    private static string DisplayUsually(DeckSettings shared, DisplayInfo display, IReadOnlyList<DisplayInfo> displays)
    {
        if (shared.Display != DisplayPlacement.Chosen)
            return display.Primary || (!displays.Any(d => d.Primary) && displays[0] == display) ? Loc.T("使用中的配置", "profile in use") : Loc.T("不显示面板", "no panel");
        if (shared.DisplayMonitors.FirstOrDefault(c => c.Id == display.Id) is not { } own) return Loc.T("不显示面板", "no panel");
        return own.Profile is { } id && Profiles.Find(shared, id) is { } profile ? Profiles.Name(profile) : Loc.T("使用中的配置", "profile in use");
    }

    // Where the panels show: the primary display only, or chosen displays, each showing the profile in use (following a switch), a
    // profile of its own, or no panel. A chosen display that is not connected is listed, marked, so that it can be removed. Shown
    // with two or more displays, or while a chosen one is not connected.
    private void DisplayProfiles(DeckSettings shared)
    {
        var displays = Displays.Current();
        bool chosenMode = shared.Display == DisplayPlacement.Chosen;
        var missing = chosenMode ? shared.DisplayMonitors.Where(c => displays.All(d => d.Id != c.Id)).ToList() : [];
        if (displays.Count < 2 && missing.Count == 0) return;
        SectionTitle(Loc.T("显示器", "Displays"));
        var modes = new UniformGrid { Columns = 2, Width = 240 };
        foreach (var (placement, name) in new[] { (DisplayPlacement.Primary, Loc.T("仅主显示器", "Primary only")), (DisplayPlacement.Chosen, Loc.T("自选显示器", "Choose displays")) })
        {
            var option = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = name, IsChecked = shared.Display == placement, GroupName = "Display" };
            AutomationProperties.SetName(option, Loc.T("显示在 · ", "Show on · ") + name);
            option.Checked += (_, _) =>
            {
                if (_app.Settings.Display == placement) return;
                // Choosing displays for the first time starts from the primary one, showing the profile in use.
                var start = displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault();
                ChangeShared(s =>
                {
                    s.Display = placement;
                    if (placement == DisplayPlacement.Chosen && s.DisplayMonitors.Count == 0 && start is not null) s.DisplayMonitors.Add(new ChosenDisplay(start.Id, start.Name));
                });
                ShowPage(ProfilesPage);
            };
            modes.Children.Add(option);
        }
        var rows = new List<FrameworkElement> { Row(Loc.T("显示在", "Show on"), SegmentBorder(modes)) };
        if (chosenMode)
        {
            var options = new List<string> { InUseLabel };
            options.AddRange(shared.Profiles.Select(Profiles.Name));
            options.Add(NoPanelLabel);
            int hidden = options.Count - 1;
            foreach (var display in displays)
            {
                var own = shared.DisplayMonitors.FirstOrDefault(c => c.Id == display.Id);
                int selected = own is null ? hidden : own.Profile is { } id && shared.Profiles.FindIndex(p => p.Id == id) is >= 0 and var at ? at + 1 : 0;
                string label = Displays.Label(display, displays) + (display.Primary ? Loc.T(" · 主显示器", " · primary") : "");
                var combo = ProfileCombo(label, options, selected, index =>
                {
                    // The last display that shows a panel keeps one.
                    if (index == hidden && _app.Settings.DisplayMonitors.All(c => c.Id == display.Id))
                    { SaveStatus(Loc.T("至少要有一台显示器显示面板。", "At least one display must show the panel."), true); ShowPage(ProfilesPage); return; }
                    SaveStatus("");
                    Guid? profile = index > 0 && index < hidden ? _app.Settings.Profiles[index - 1].Id : null;
                    ChangeShared(s =>
                    {
                        s.DisplayMonitors.RemoveAll(c => c.Id == display.Id);
                        if (index != hidden) s.DisplayMonitors.Add(new ChosenDisplay(display.Id, display.Name) { Profile = profile });
                    });
                });
                rows.Add(DisplayLine(Row(label, combo)));
            }
            foreach (var gone in missing)
            {
                string label = (gone.Name.Length > 0 ? gone.Name : Loc.T("显示器", "Display")) + Loc.T("（未连接）", " (not connected)");
                rows.Add(DisplayLine(Row(label, RemoveButton(label, () => ChangeShared(s => s.DisplayMonitors.RemoveAll(c => c.Id == gone.Id))))));
            }
        }
        Group([.. rows]);
    }

    // The displays are items under "显示在", so their names are not set as headings.
    private static Grid DisplayLine(Grid line)
    {
        if (line.Children[0] is StackPanel { Children: [HelpHeading or TextBlock] } labels && labels.Children[0] is TextBlock plain) plain.FontWeight = FontWeights.Normal;
        return line;
    }

    // The rules, first to last; the first that holds applies. A row with the count and the add button heads them, as the app lists
    // on the other pages do. Each rule is one line that drags to another place by its handle and opens to its choices, as the
    // collapsed content's parts do (OptionsPartRow).
    private void ProfileRules(DeckSettings shared)
    {
        SectionTitle(Loc.T("自动切换", "Switch automatically"));
        PlayerNote(Loc.T("指定的应用运行或在前台时，自动切换到所选的配置；也可以为每台显示器单独指定配置或不显示面板。条件结束后恢复原样。多条规则同时满足时，排在上面的生效，拖动左侧的手柄可以调整顺序。规则对所有配置都有效。",
            "While the app runs or is in front, switches to the chosen profile; each display can also be given its own profile or no panel. When that ends, everything goes back. When several rules hold, the one higher up applies; drag the handle at the left to reorder. The rules apply whatever profile is in use."));
        var head = new Grid { MinHeight = 44 };
        head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = TextLine(Loc.T("规则", "Rules"), 13, TextPrimary, FontWeights.SemiBold); label.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(label);
        int n = shared.ProfileRules.Count;
        var count = TextLine(n == 0 ? Loc.T("未添加", "None") : Loc.T($"{n} 条", n == 1 ? "1 rule" : $"{n} rules"), 12, TextTertiary);
        count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(0, 0, 12, 0);
        var add = AddAppButton(Loc.T("添加自动切换规则", "Add an automatic switch rule"), RuleMenu);
        add.IsEnabled = n < Profiles.MaximumRules;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(count); controls.Children.Add(add);
        Grid.SetColumn(controls, 1); head.Children.Add(controls);
        var displays = Displays.Current();
        _ruleOptions.Clear();
        if (n == 0) { Group(head); return; }
        var rows = new StackPanel();
        foreach (var rule in shared.ProfileRules) rows.Children.Add(RuleRow(shared, rule, rows, displays));
        UpdateDividers(rows);
        Group(head, rows);
    }

    private const double RuleRowHeight = 44;
    // The rules whose choices are open, kept while the page is built again.
    private readonly HashSet<Guid> _openRules = [];
    private readonly List<(Border Row, FrameworkElement Picks, Button Arrow, Guid Id)> _ruleOptions = [];
    private void CloseRuleOptions()
    {
        _openRules.Clear();
        foreach (var (row, picks, arrow, _) in _ruleOptions) { picks.Visibility = Visibility.Collapsed; PointArrow(arrow, false); row.Height = RuleRowHeight; }
    }

    // One rule: the handle, the app with its arrow and whether the rule holds now, its switch and its removal; opened, when it
    // applies, the profile it switches to and each display's panel while it does.
    private Border RuleRow(DeckSettings shared, ProfileRule rule, StackPanel rows, IReadOnlyList<DisplayInfo> displays)
    {
        var id = rule.Id;
        void Edit(Action<ProfileRule> edit) => ChangeShared(s => { if (s.ProfileRules.FirstOrDefault(r => r.Id == id) is { } found) edit(found); });
        string name = AppName(rule.Name, rule.Path);

        var picks = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Enable(bool on) { picks.IsEnabled = on; picks.Opacity = on ? 1 : .5; }
        Enable(rule.Enabled);
        // When the rule holds.
        var triggers = new UniformGrid { Columns = 2, Width = 240 };
        foreach (var (trigger, text) in new[] { (ProfileTrigger.Running, Loc.T("应用运行时", "While it runs")), (ProfileTrigger.Foreground, Loc.T("应用在前台时", "While in front")) })
        {
            var option = new RadioButton { Content = text, Style = (Style)FindResource("SettingsSegment"), GroupName = "Trigger" + id.ToString("N"), IsChecked = rule.Trigger == trigger };
            AutomationProperties.SetName(option, name + " · " + text);
            option.Checked += (_, _) => Edit(r => r.Trigger = trigger);
            triggers.Children.Add(option);
        }
        picks.Children.Add(Row(Loc.T("生效时机", "When"), SegmentBorder(triggers)));
        // The profile it makes current.
        var profiles = new List<string> { Loc.T("不切换配置", "Keep the profile in use") };
        profiles.AddRange(shared.Profiles.Select(Profiles.Name));
        int selected = rule.Profile is { } wanted && shared.Profiles.FindIndex(p => p.Id == wanted) is >= 0 and var at ? at + 1 : 0;
        picks.Children.Add(Row(Loc.T("切换到", "Switch to"), ProfileCombo(name + " · " + Loc.T("切换到", "Switch to"), profiles, selected,
            choice => Edit(r => r.Profile = choice == 0 ? null : shared.Profiles[choice - 1].Id))));
        // Each display while the rule holds: as the displays section sets it (what that is, in brackets), the profile in use, a
        // chosen profile, or no panel; in the order the displays section lists them.
        foreach (var display in displays)
        {
            string label = displays.Count == 1 ? Loc.T("面板", "Panel") : Displays.Label(display, displays) + (display.Primary ? Loc.T(" · 主显示器", " · primary") : "");
            var options = new List<string> { Loc.T($"跟随显示器设置（{DisplayUsually(shared, display, displays)}）", $"As the display is set ({DisplayUsually(shared, display, displays)})"), InUseLabel };
            options.AddRange(shared.Profiles.Select(Profiles.Name));
            options.Add(NoPanelLabel);
            int hidden = options.Count - 1;
            var own = rule.Displays.FirstOrDefault(d => d.Id == display.Id);
            int pick = own is null ? 0 : own.Hidden ? hidden : own.Profile is { } shown && shared.Profiles.FindIndex(p => p.Id == shown) is >= 0 and var place ? 2 + place : 1;
            picks.Children.Add(DisplayLine(Row(label, ProfileCombo(name + " · " + label, options, pick, choice => Edit(r =>
            {
                r.Displays.RemoveAll(d => d.Id == display.Id);
                if (choice == 0) return;
                r.Displays.Add(new ProfileRuleDisplay { Id = display.Id, Name = display.Name, Hidden = choice == hidden, Profile = choice >= 2 && choice < hidden ? shared.Profiles[choice - 2].Id : null });
            }), 260))));
        }

        Button arrow = null!;
        Border item = null!;
        void Show(bool open)
        {
            if (open) _openRules.Add(id); else _openRules.Remove(id);
            picks.Visibility = open ? Visibility.Visible : Visibility.Collapsed; PointArrow(arrow, open);
            item.Height = open ? double.NaN : RuleRowHeight;
        }
        arrow = ChevronButton(Loc.T($"展开 {name} 规则", $"Show the {name} rule"), () => Show(!_openRules.Contains(id)));
        arrow.Margin = new Thickness(2, 0, 0, 0);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        heading.Children.Add(AppIcon(rule.Path));
        var title = TextLine(name, 13, TextPrimary, FontWeights.SemiBold); title.TextTrimming = TextTrimming.CharacterEllipsis; title.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(title);
        var file = TextLine(FileName(rule.Name, rule.Path), 11.5, TextTertiary); file.Margin = new Thickness(8, 1, 0, 0); file.VerticalAlignment = VerticalAlignment.Center;
        // The full path only on the file's name, which it spells out.
        if (rule.Path.Length > 0) file.ToolTip = rule.Path;
        heading.Children.Add(file);
        heading.Children.Add(arrow);
        if (_app.HoldingRule?.Id == id)
        {
            var on = TextLine(Loc.T("生效中", "Active"), 11.5, _palette.Accent); on.Margin = new Thickness(8, 1, 0, 0); on.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(on);
        }
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var enabled = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        AutomationProperties.SetName(enabled, Loc.T($"启用 {name} 规则", $"Enable the {name} rule"));
        enabled.Checked += (_, _) => { Edit(r => r.Enabled = true); Enable(true); };
        enabled.Unchecked += (_, _) => { Edit(r => r.Enabled = false); Enable(false); };
        controls.Children.Add(enabled);
        controls.Children.Add(RemoveButton(name, () => { _openRules.Remove(id); ChangeShared(s => s.ProfileRules.RemoveAll(r => r.Id == id), rebuild: true); }));

        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RuleRowHeight) }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var handle = new GripHandle { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        Grid.SetColumn(heading, 1); row.Children.Add(heading);
        Grid.SetColumn(controls, 2); row.Children.Add(controls);
        Grid.SetRow(picks, 1); Grid.SetColumn(picks, 1); Grid.SetColumnSpan(picks, 2); row.Children.Add(picks);
        // The handle, the switch, the arrow and the cross take their own clicks; a click anywhere else on the title line opens or
        // closes the choices.
        row.MouseLeftButtonDown += (_, e) => { if (e.GetPosition(row).Y >= RuleRowHeight) return; Show(!_openRules.Contains(id)); e.Handled = true; };
        // Dropped in a new place, the rules take the new order at once; open choices close when a drag begins, so every rule is one
        // line high while they move.
        AttachDrag(handle, name, () => rows, RuleRowHeight, list =>
        {
            var order = list.Children.OfType<Border>().Where(b => b.Tag is Guid).Select(b => (Guid)b.Tag).ToList();
            ChangeShared(s => s.ProfileRules = [.. order.Select(rid => s.ProfileRules.FirstOrDefault(r => r.Id == rid)).OfType<ProfileRule>()]);
        }, CloseRuleOptions);
        item = new Border { Child = row, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = id, RenderTransform = new TranslateTransform() };
        _ruleOptions.Add((item, picks, arrow, id));
        Show(_openRules.Contains(id));
        return item;
    }

    // Adds a rule for an app with a window open, or one chosen from its executable.
    private void RuleMenu(Button anchor)
    {
        var menu = AppMenu(anchor);
        var apps = RunningApps.WithWindows()
            .Where(app => !string.Equals(app.Name, "BrimDeck", StringComparison.OrdinalIgnoreCase))
            .Select(app => (App: app, Name: AppName(app.Name, app.Path)))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var (app, name) in apps)
            menu.Items.Add(AppMenuItem(name, FileName(app.Name, app.Path), app.Path, () => AddRule(app.Name, app.Path)));
        if (apps.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("没有可添加的应用", "No apps to add"), IsEnabled = false });
        AddBrowseItem(menu, Loc.T("选择应用", "Choose an app"), path => AddRule(System.IO.Path.GetFileNameWithoutExtension(path), path));
        menu.IsOpen = true;
    }

    // A new rule switches to the first profile that is not the current one, when there is one.
    private void AddRule(string name, string path) => ChangeShared(s =>
    {
        var other = s.Profiles.FirstOrDefault(p => p.Id != s.CurrentProfile);
        var made = new ProfileRule { Name = name, Path = path, Profile = other?.Id };
        s.ProfileRules.Add(made);
        // A new rule shows its choices at once.
        _openRules.Add(made.Id);
    }, rebuild: true);
}

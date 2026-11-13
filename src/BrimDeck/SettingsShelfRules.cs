using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// A category's options: in its row, on the clipboard, whether what is in it is kept; under its row, what goes in by itself
// (ShelfRules). Changes are saved as they are made; a text box is saved when it is left, and the row's summary and the count of
// what is already on the page follow it while typing.
public partial class SettingsWindow
{
    private Guid? _openCategory;
    private readonly Dictionary<Guid, TextBlock> _categorySummaries = [];

    // The filter's funnel, drawn in a 12 DIP box: in the accent while the category has a rule that works.
    private const string FunnelIcon = "M 1,1.5 H 11 L 7,6.3 V 10.3 L 5,11.3 V 6.3 Z";

    private TextBlock CategorySummary(ShelfSide side, ShelfCategory category)
    {
        var block = TextLine("", 12, TextTertiary);
        block.VerticalAlignment = VerticalAlignment.Center; block.TextTrimming = TextTrimming.CharacterEllipsis; block.Margin = new Thickness(14, 0, 4, 0);
        _categorySummaries[category.Id] = block;
        // The whole line on hover only when it is cut short.
        block.ToolTipOpening += (_, e) =>
        {
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (block.DesiredSize.Width <= block.ActualWidth + 0.5) e.Handled = true;
        };
        UpdateSummary(side, category);
        return block;
    }
    private void UpdateSummary(ShelfSide side, ShelfCategory category)
    {
        if (!_categorySummaries.TryGetValue(category.Id, out var block)) return;
        bool wrong = ShelfRules.Problem(category.Rule, side) is not null;
        var parts = new List<string>();
        if (wrong) parts.Add(Loc.T("规则有误", "The rule has a mistake"));
        else if (ShelfRules.Active(category.Rule, side)) parts.Add(Loc.T("自动：", "Adds: ") + ShelfRules.Summary(category.Rule!, side));
        block.Text = string.Join(" · ", parts);
        block.ToolTip = block.Text.Length > 0 ? block.Text : null;
        block.Foreground = UI.Brush(wrong ? _palette.Negative : TextTertiary);
    }

    // In the row, a small label that says what happens to what is in the category and changes it when clicked: "一直保留" (on)
    // or "退出时清除" (off, it goes with the history when BrimDeck exits). Both words are laid out, the other hidden, so the label
    // keeps its width.
    private ToggleButton KeepSwitch(ShelfCategory category)
    {
        TextBlock Word(string text) => new() { Text = text, FontFamily = Fonts.PlainOf(UiFontRole.Panel), HorizontalAlignment = HorizontalAlignment.Center };
        var kept = Word(Loc.T("一直保留", "Kept"));
        var cleared = Word(Loc.T("退出时清除", "Cleared on exit"));
        void Show(bool on) { kept.Visibility = on ? Visibility.Visible : Visibility.Hidden; cleared.Visibility = on ? Visibility.Hidden : Visibility.Visible; }
        Show(category.Keep);
        var keep = new ToggleButton { Style = (Style)FindResource("SettingsStateChip"), IsChecked = category.Keep, Content = new Grid { Children = { kept, cleared } },
            Width = KeepChipWidth, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(keep, Loc.T($"{category.Name}一直保留", $"Keep {category.Name}"));
        keep.Checked += (_, _) => { category.Keep = true; Show(true); ShelfEdited(); };
        keep.Unchecked += (_, _) => { category.Keep = false; Show(false); ShelfEdited(); };
        return keep;
    }

    // Every label in that column is this wide and centred in it, so they line up whichever words they show.
    private static double KeepChipWidth => Loc.IsEnglish ? 108 : 86;

    // "常用"'s place in that column: the same label, on, with a lock before the words and paler, and not to be clicked: "常用" is
    // always kept.
    private FrameworkElement AlwaysKept()
    {
        var lockIcon = Stroke("M 2,5 H 8 V 9.5 H 2 Z M 3.2,5 V 3.4 A 1.8,1.8 0 0 1 6.8,3.4 V 5", 10, 10, _palette.Accent); lockIcon.StrokeThickness = 1.2;
        lockIcon.Margin = new Thickness(0, 0, 4, 0);
        var words = new TextBlock { Text = Loc.T("一直保留", "Kept"), FontFamily = Fonts.PlainOf(UiFontRole.Panel), VerticalAlignment = VerticalAlignment.Center };
        var mark = new ToggleButton { Style = (Style)FindResource("SettingsStateChip"), IsChecked = true, IsHitTestVisible = false, Focusable = false, Opacity = 0.6,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { lockIcon, words } },
            Width = KeepChipWidth, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(mark, Loc.T("常用一直保留", "Favorites are always kept"));
        return mark;
    }

    // A button that adds something to the list above it: a plus and the words in the accent, starting at the list's left edge. It
    // has no fill to overhang that edge; under the pointer the words are underlined.
    private Button AddLink(string text, Action action)
    {
        var button = ActionButton(text, action);
        button.Style = (Style)FindResource("SettingsGlyphButton");
        var plus = Stroke("M 5,1 V 9 M 1,5 H 9", 10, 10, _palette.Accent); plus.StrokeThickness = 1.5; plus.Margin = new Thickness(0, 0, 6, 0);
        var words = TextLine(text, 12.5, _palette.Accent); words.VerticalAlignment = VerticalAlignment.Center;
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { plus, words }, Margin = new Thickness(0, 5, 2, 5) };
        button.MouseEnter += (_, _) => words.TextDecorations = TextDecorations.Underline;
        button.MouseLeave += (_, _) => words.TextDecorations = null;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(button, text);
        return button;
    }

    private Button CategoryOptionsButton(ShelfSide side, ShelfCategory category, bool open)
    {
        var button = GlyphButton(FunnelIcon, 12, 12, Loc.T($"{category.Name}的自动规则", $"{category.Name}: what goes in by itself"), () =>
        {
            _openCategory = _openCategory == category.Id ? null : category.Id;
            ShowPage(_page);
        });
        if (ShelfRules.Active(category.Rule, side) || ShelfRules.Problem(category.Rule, side) is not null) button.Foreground = UI.Brush(_palette.Accent);
        // No hover tip: the name stays for screen readers.
        button.ToolTip = null;
        button.Width = 28; button.Height = 28; button.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetItemStatus(button, open ? Loc.T("已展开", "Expanded") : Loc.T("已收起", "Collapsed"));
        return button;
    }

    // Before a row is dragged: every row closed, so all are the same height.
    private void CloseCategoryOptions(Panel rows)
    {
        _openCategory = null;
        foreach (var row in rows.Children.OfType<Border>())
            if (row.Child is StackPanel { Children.Count: > 1 } body) { body.Children.RemoveRange(1, body.Children.Count - 1); row.Height = CategoryRowHeight; }
    }

    private FrameworkElement CategoryOptions(ShelfSide side, ShelfCategory category)
    {
        var box = new StackPanel();
        var frame = new Border { Child = box, Background = UI.Brush(_palette.Inset), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 2, 0, 14) };

        var rule = category.Rule;
        var conditions = rule?.Conditions ?? [];
        TextBlock? footText = null; Button? putIn = null;
        void Refresh()
        {
            UpdateSummary(side, category);
            if (footText is null || putIn is null) return;
            if (ShelfRules.Problem(category.Rule, side) is not null)
            {
                footText.Text = Loc.T("现有记录暂不计算：请先改正正则表达式。", "Not counted until the regular expression is corrected."); putIn.IsEnabled = false; return;
            }
            int outside = side == ShelfSide.Clipboard ? ShelfRules.Outside(ShelfData, category).Count : ShelfRules.OutsideFiles(ShelfData, category).Count;
            footText.Text = side == ShelfSide.Clipboard
                ? Loc.T($"现有记录中有 {outside} 条内容符合，尚未放进这个分类", outside == 1 ? "1 entry already here meets it and is not in this category" : $"{outside} entries already here meet it and are not in this category")
                : Loc.T($"现有文件中有 {outside} 个符合，尚未放进这个分类", outside == 1 ? "1 file already here meets it and is not in this category" : $"{outside} files already here meet it and are not in this category");
            putIn.IsEnabled = outside > 0;
        }

        // ---- The first line: how the conditions combine ----
        var lead = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        TextBlock LeadText(string text) { var t = TextLine(text, 12.5, TextSecondary); t.VerticalAlignment = VerticalAlignment.Center; return t; }
        string into = Loc.T($"自动放入“{category.Name}”", $"go into {category.Name} by themselves");
        if (conditions.Count == 0) lead.Children.Add(LeadText(side == ShelfSide.Clipboard
            ? Loc.T("没有规则时，只收入手动加入的内容。", "Without a rule, only what you put in by hand goes in.")
            : Loc.T("没有规则时，只收入手动加入的文件。", "Without a rule, only the files you put in by hand go in.")));
        else if (conditions.Count == 1) lead.Children.Add(LeadText(Loc.T($"符合以下条件时，{into}", $"Entries that meet this condition {into}")));
        else
        {
            lead.Children.Add(LeadText(Loc.T("符合", "Entries that meet ")));
            var match = RuleCombo([Loc.T("任一", "any"), Loc.T("全部", "all")], rule!.Match == RuleMatch.All ? 1 : 0, 76, Loc.T("条件组合", "How the conditions combine"),
                index => { rule.Match = index == 1 ? RuleMatch.All : RuleMatch.Any; ShelfEdited(); Refresh(); });
            match.Margin = new Thickness(8, 0, 8, 0); lead.Children.Add(match);
            lead.Children.Add(LeadText(Loc.T($"条件时，{into}", $"of the conditions {into}")));
        }
        box.Children.Add(lead);

        foreach (var condition in conditions.ToList())
            box.Children.Add(ConditionRow(side, category, condition, Refresh));

        var add = AddLink(Loc.T("添加条件", "Add a condition"), () =>
        {
            (category.Rule ??= new ShelfRule()).Conditions.Add(ShelfRules.NewCondition(side));
            ShelfEdited(); ShowPage(_page);
        });
        add.Margin = new Thickness(0, conditions.Count == 0 ? 2 : 0, 0, 0);
        AutomationProperties.SetName(add, Loc.T($"给{category.Name}添加条件", $"Add a condition to {category.Name}"));
        box.Children.Add(add);

        // ---- What is already here and meets the rule ----
        if (conditions.Count > 0)
        {
            var foot = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            putIn = ActionButton(Loc.T("放进这个分类", "Put them in"), () =>
            {
                if (side == ShelfSide.Clipboard) foreach (var clip in ShelfRules.Outside(ShelfData, category)) clip.Categories.Add(category.Id);
                else foreach (var file in ShelfRules.OutsideFiles(ShelfData, category)) file.Categories.Add(category.Id);
                ShelfEdited(); ShowPage(_page);
            });
            putIn.Style = (Style)FindResource("SettingsOutlineButton"); putIn.Padding = new Thickness(12, 4, 12, 4);
            DockPanel.SetDock(putIn, Dock.Right); foot.Children.Add(putIn);
            footText = TextLine("", 12, TextSecondary); footText.VerticalAlignment = VerticalAlignment.Center; footText.TextWrapping = TextWrapping.Wrap; footText.Margin = new Thickness(0, 0, 12, 0);
            foot.Children.Add(footText);
            box.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.Border), Margin = new Thickness(0, 10, 0, 0) });
            box.Children.Add(foot);
        }
        Refresh();
        return frame;
    }

    // One condition: what it looks at, how it compares, the value, and its delete button.
    private FrameworkElement ConditionRow(ShelfSide side, ShelfCategory category, RuleCondition condition, Action refresh)
    {
        var line = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var width in new[] { new GridLength(116), new GridLength(112), new GridLength(1, GridUnitType.Star), new GridLength(32) })
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Rebuild() { ShelfEdited(); Dispatcher.BeginInvoke(() => ShowPage(_page)); }
        void Saved() { ShelfEdited(); refresh(); }

        var fields = ShelfRules.Fields(side);
        var field = RuleCombo([.. fields.Select(ShelfRules.Name)], Array.IndexOf(fields, condition.Field), 108, Loc.T("条件", "Condition"), index =>
        {
            if (fields[index] == condition.Field) return;
            condition.Field = fields[index]; condition.Test = RuleTest.Contains; condition.Values = []; condition.Value = "";
            Rebuild();
        });
        field.VerticalAlignment = VerticalAlignment.Top;
        line.Children.Add(field);

        FrameworkElement test;
        if (condition.Field is RuleField.Text or RuleField.FileName)
        {
            RuleTest[] tests = condition.Field == RuleField.Text
                ? [RuleTest.Contains, RuleTest.StartsWith, RuleTest.EndsWith, RuleTest.Format, RuleTest.Regex]
                : [RuleTest.Contains, RuleTest.StartsWith, RuleTest.EndsWith, RuleTest.Regex];
            test = RuleCombo([.. tests.Select(ShelfRules.Name)], Math.Max(0, Array.IndexOf(tests, condition.Test)), 104, Loc.T("比较方式", "Comparison"), index =>
            {
                if (tests[index] == condition.Test) return;
                bool format = tests[index] == RuleTest.Format || condition.Test == RuleTest.Format;
                condition.Test = tests[index];
                if (format) condition.Value = condition.Test == RuleTest.Format ? nameof(TextFormat.Email) : "";
                Rebuild();
            });
        }
        else
        {
            var word = TextLine(condition.Field == RuleField.Folder ? Loc.T("在其中", "contains it") : Loc.T("是", "is"), 12.5, TextSecondary);
            word.Margin = new Thickness(10, 0, 0, 0); word.VerticalAlignment = VerticalAlignment.Center;
            test = new Border { Height = 31, Child = word };
        }
        test.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(test, 1); line.Children.Add(test);

        FrameworkElement value;
        TextBlock? hint = null;
        switch (condition.Field)
        {
            case RuleField.Kind or RuleField.FileType:
                var chips = new WrapPanel { Margin = new Thickness(0, 2, 0, -6) };
                var names = condition.Field == RuleField.Kind
                    ? Enum.GetValues<ClipKind>().Select(kind => (kind.ToString(), ShelfRules.Name(kind)))
                    : Enum.GetValues<FileKind>().Select(kind => (kind.ToString(), ShelfRules.Name(kind)));
                foreach (var (key, name) in names)
                {
                    var chip = new ToggleButton { Style = (Style)FindResource("SettingsChip"), Content = name, IsChecked = condition.Values.Contains(key) };
                    AutomationProperties.SetName(chip, name);
                    if (condition.Field == RuleField.FileType && Enum.TryParse<FileKind>(key, out var kind) && ShelfRules.ExtensionsOf(kind) is { Count: > 0 } extensions)
                        chip.ToolTip = string.Join(" ", extensions.Select(e => "." + e));
                    chip.Checked += (_, _) => { if (!condition.Values.Contains(key)) condition.Values.Add(key); Saved(); };
                    chip.Unchecked += (_, _) => { condition.Values.Remove(key); Saved(); };
                    chips.Children.Add(chip);
                }
                value = chips;
                break;
            case RuleField.Text when condition.Test == RuleTest.Format:
                var formats = Enum.GetValues<TextFormat>();
                var chosen = Array.FindIndex(formats, f => f.ToString() == condition.Value);
                value = RuleCombo([.. formats.Select(f => $"{ShelfRules.Name(f)}　{ShelfRules.Example(f)}")], Math.Max(0, chosen), double.NaN, Loc.T("内置格式", "Format"),
                    index => { condition.Value = formats[index].ToString(); Saved(); });
                if (chosen < 0) condition.Value = formats[0].ToString();
                break;
            default:
                var box = new TextBox { Style = (Style)FindResource("SettingsField"), Height = 31, Padding = new Thickness(10, 0, 10, 0), FontSize = 12.5, Text = condition.Value };
                if (condition.Test == RuleTest.Regex && condition.Field is RuleField.Text or RuleField.FileName) box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                AutomationProperties.SetName(box, ShelfRules.Name(condition.Field));
                box.ToolTip = condition.Field switch
                {
                    RuleField.Site => Loc.T("域名，多个用逗号分隔，例如 github.com", "Domains separated by commas, e.g. github.com"),
                    RuleField.Source => Loc.T("进程名，多个用逗号分隔，例如 chrome, msedge", "Process names separated by commas, e.g. chrome, msedge"),
                    RuleField.Extension => Loc.T("多个用逗号分隔，例如 .psd, .fig", "Separated by commas, e.g. .psd, .fig"),
                    _ => null
                };
                hint = TextLine("", 11.5, _palette.Negative); hint.Margin = new Thickness(0, 4, 0, 0); hint.TextWrapping = TextWrapping.Wrap;
                void Check()
                {
                    string? problem = ShelfRules.Problem(condition);
                    hint.Text = problem ?? ""; hint.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
                    box.BorderBrush = problem is null ? (Brush)FindResource("SettingsInputBorder") : UI.Brush(_palette.Negative);
                }
                box.TextChanged += (_, _) => { condition.Value = box.Text; Check(); refresh(); };
                box.LostKeyboardFocus += (_, _) => ShelfEdited();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Keyboard.ClearFocus(); e.Handled = true; } };
                Check();
                if (condition.Field == RuleField.Folder)
                {
                    var pick = new DockPanel();
                    var browse = ActionButton(Loc.T("选择…", "Choose…"), () =>
                    {
                        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("选择文件夹", "Choose a folder") };
                        if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
                        ShelfEdited();
                    });
                    browse.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right);
                    pick.Children.Add(browse); pick.Children.Add(box);
                    value = pick;
                }
                else value = box;
                break;
        }
        value.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(value, 2); line.Children.Add(value);
        if (hint is not null) { Grid.SetColumn(hint, 2); Grid.SetRow(hint, 1); line.Children.Add(hint); }

        var remove = GlyphButton("M 0,0 L 8,8 M 8,0 L 0,8", 8, 8, Loc.T("删除条件", "Delete the condition"), () =>
        {
            if (category.Rule is { } rule)
            {
                rule.Conditions.Remove(condition);
                if (rule.Conditions.Count == 0) category.Rule = null;
            }
            ShelfEdited(); ShowPage(_page);
        });
        remove.ToolTip = null;
        remove.Width = 28; remove.Height = 31; remove.HorizontalAlignment = HorizontalAlignment.Right; remove.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(remove, 3); line.Children.Add(remove);
        return line;
    }

    private ComboBox RuleCombo(IReadOnlyList<string> items, int selected, double width, string name, Action<int> chosen)
    {
        var combo = new ComboBox { Style = (Style)FindResource("SettingsCombo"), ItemsSource = items, SelectedIndex = selected, FontSize = 12.5 };
        if (!double.IsNaN(width)) combo.Width = width;
        AutomationProperties.SetName(combo, name);
        PopupMotion.Attach(combo);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) chosen(combo.SelectedIndex); };
        return combo;
    }
}

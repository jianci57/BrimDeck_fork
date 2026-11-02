using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// Editing the current theme on the theme page. The background, foreground and accent come first, and choosing one of them makes
// the colors that follow from it (PanelThemes.Derive); "Advanced" lists every color, app color and effect of the file. A change
// takes effect only once saved: until then it is kept in memory (PanelTheme.Edit), shown on this page as "Unsaved", and neither
// the panel nor the theme cards show it. Saving goes through the custom section's cards: the new-theme card turns into "Save
// current theme" (a new theme of the user's, chosen at once), and a chosen theme of the user's offers "Overwrite current theme"
// in place of its own card (the changed values written into its file).
public sealed partial class SettingsWindow
{
    private bool _themeAdvancedOpen, _themeReadabilityOpen, _themeRebuildQueued;

    // Every token, in groups as the panel uses them.
    private static (string Chinese, string English, string[] Keys)[] TokenGroups =>
    [
        ("文字", "Text", ["textPrimary", "textSecondary", "textTertiary", "menuDetail"]),
        ("强调与状态", "Accent and states", ["accent", "onAccent", "danger", "liked", "rangeFill"]),
        ("时间与律动条", "Clock and equalizer", ["clock", "equalizer"]),
        ("面板与收起形态", "Panel and collapsed view", ["surface", "rim", "shadow", "indicator", "indicatorProgress", "coverPlaceholder", "coverOutline"]),
        ("控件", "Controls", ["hairline", "track", "ringTrack", "fill", "fillSelected", "hover", "pressed", "outline", "input", "scrollThumb", "switchFill", "seekRail", "rowDivider", "rowHover"]),
        ("弹出层与提示", "Pop-ups and tooltips", ["popup", "popupBorder", "tooltip", "tooltipBorder", "bubble", "bubbleBorder"]),
    ];

    private static string AppColorName(string key) => key switch
    {
        "claude" => "Claude", "codex" => "Codex", "antigravity" => "Antigravity", "cursor" => "Cursor", "zcode" => "ZCode", "dsh" => "DeepSeek Harness",
        "grokBot" => "Grok Bot", "kimi" => "Kimi Code", "opencode" => "OpenCode", "manual" => Loc.T("手动填写的来源", "Sources entered by hand"), "other" => Loc.T("其他应用", "Other apps"),
        "warning" => Loc.T("预警", "Warning"), "critical" => Loc.T("告警", "Critical"), _ => key
    };

    private void ThemeEditor(PanelThemeInfo theme)
    {
        var palette = PanelTheme.PaletteOf(theme);
        var draft = PanelTheme.DraftOf(theme.Id);
        Button? undoAll = null;
        if (draft is not null)
        {
            undoAll = ActionButton(Loc.T("撤销全部修改", "Undo all changes"), () => { PanelTheme.Discard(theme.Id); RebuildThemePage(); });
            undoAll.Style = (Style)FindResource("SettingsLink");
        }
        SectionTitle(Loc.T("颜色", "Colors"), undoAll, () => Loc.T("修改只在保存后生效：保存前，面板和主题卡片都保持原样，下方“自定义”中会出现保存卡片。修改背景、前景或强调色时，与它相关的颜色会按它重新生成，可在“高级设置”中逐项调整。",
            "Changes take effect only once saved: until then the panel and the theme cards stay as they are, and save cards appear under Custom below. Changing the background, foreground or accent makes the colors that follow from it again; adjust them one by one under Advanced."));
        Group(
            ColorRow(theme, palette, draft, PanelThemes.BackgroundKey, Loc.T("背景", "Background"), Loc.T("刘海、胶囊与展开面板的底色", "The surface of the notch, capsule and panel"), seed: true),
            ColorRow(theme, palette, draft, PanelThemes.ForegroundKey, Loc.T("前景", "Foreground"), Loc.T("主要文字；次要文字、分隔线与控件底色由它生成", "Primary text; secondary text, dividers and fills follow from it"), seed: true),
            ColorRow(theme, palette, draft, PanelThemes.AccentKey, Loc.T("强调色", "Accent"), Loc.T("键盘焦点与选中的日期", "Keyboard focus and chosen dates"), seed: true));

        var toggle = ActionButton((_themeAdvancedOpen ? Loc.T("收起高级设置", "Hide advanced") : Loc.T("高级设置", "Advanced")) + (_themeAdvancedOpen ? " ▴" : " ▾"),
            () => { _themeAdvancedOpen = !_themeAdvancedOpen; RebuildThemePage(); });
        toggle.Style = (Style)FindResource("SettingsLink"); toggle.HorizontalAlignment = HorizontalAlignment.Left; toggle.Margin = new Thickness(0, -12, 0, 20);
        AutomationProperties.SetName(toggle, Loc.T("高级颜色设置", "Advanced color settings"));
        Target.Children.Add(toggle);
        if (!_themeAdvancedOpen) return;

        foreach (var (chinese, english, keys) in TokenGroups)
        {
            SectionTitle(Loc.T(chinese, english));
            Group([.. keys.Select(key => PanelThemes.Tokens.First(token => token.Key == key)).Select(token => ColorRow(theme, palette, draft, token.Key, Loc.T(token.Chinese, token.English), TokenNote(token)))]);
        }
        SectionTitle(Loc.T("应用默认颜色", "Default app colors"), help: () => Loc.T("在“配额与统计”中没有单独选择颜色的应用列使用这些颜色。",
            "Application columns without a color chosen under Quotas and usage use these colors."));
        Group([.. PanelThemes.AppKeys.Select(key => ColorRow(theme, palette, draft, key, AppColorName(key), "apps." + key, app: true))]);
        SectionTitle(Loc.T("效果", "Effects"));
        string Percent(double value) => (value * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
        string Units(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        Group(
            EffectRow(theme, draft, palette, "rimWidth", Loc.T("边缘描线宽度", "Rim width"), .25, value => Units(value) + " DIP"),
            EffectRow(theme, draft, palette, "shadowBlur", Loc.T("阴影模糊", "Shadow blur"), 1, Units),
            EffectRow(theme, draft, palette, "shadowDepth", Loc.T("阴影偏移", "Shadow depth"), 1, Units),
            EffectRow(theme, draft, palette, "shadowOpacity", Loc.T("阴影不透明度", "Shadow opacity"), .01, Percent),
            EffectRow(theme, draft, palette, "scanlines", Loc.T("扫描线", "Scan lines"), .01, Percent));
        var rest = TextLine(Loc.T("表面渐变、边缘渐变、时间的预设颜色与渐变，以及颜色选择器的预设色，仍需在主题文件中修改。",
            "The surface and rim gradients, the clock's preset colors and gradients, and the color picker's presets are still set in the theme file."), 11.5, TextTertiary);
        rest.TextWrapping = TextWrapping.Wrap; rest.Margin = new Thickness(0, -16, 0, 28);
        Target.Children.Add(rest);
    }

    // The key as the file writes it, and the contrast it is held to.
    private static string TokenNote(PanelThemeToken token)
        => "colors." + token.Key + (token.Minimum > 0 ? Loc.T($" · 至少 {token.Minimum:0.#}:1", $" · at least {token.Minimum:0.#}:1") : "");

    // One color: its name and note at the left; at the right whether it has an unsaved change (with a button that takes the change
    // back), and the color as written, which opens the color picker. A color that would be shown otherwise, to stay readable, says so.
    private Grid ColorRow(PanelThemeInfo theme, PanelPalette palette, PanelThemeDraft? draft, string key, string title, string note, bool seed = false, bool app = false)
    {
        var written = app ? palette.WrittenApps[key] : palette.WrittenColors[key];
        bool changed = draft is not null && (app ? draft.Apps.ContainsKey(key) : draft.Colors.ContainsKey(key));
        if (!app && palette.Adjustments.FirstOrDefault(adjustment => adjustment.Key == key) is { } adjusted)
            note += Loc.T($"；为保证可读，将显示为 {adjusted.After}", $"; shown as {adjusted.After} to stay readable");
        var subtitle = TextLine(note, 11, TextTertiary); subtitle.TextWrapping = TextWrapping.Wrap;

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        if (changed)
        {
            var mark = TextLine(Loc.T("修改未保存", "Unsaved"), 11.5, TextSecondary); mark.VerticalAlignment = VerticalAlignment.Center; mark.Margin = new Thickness(0, 0, 10, 0);
            mark.SetResourceReference(TextBlock.ForegroundProperty, "SettingsAccent");
            controls.Children.Add(mark);
            if (!seed)
            {
                var undo = ActionButton(Loc.T("撤销", "Undo"), () => EditTheme(theme, (d, file) => { if (app) d.SetApp(file, key, null); else d.SetColor(file, key, null); }));
                undo.Style = (Style)FindResource("SettingsLink"); undo.VerticalAlignment = VerticalAlignment.Center; undo.Margin = new Thickness(0, 0, 12, 0);
                AutomationProperties.SetName(undo, Loc.T("撤销修改 ", "Undo change to ") + title);
                controls.Children.Add(undo);
            }
        }
        controls.Children.Add(ColorChip(title, written, palette.Surface, changed && !seed, value =>
        {
            if (seed) { if (value is { } picked) EditTheme(theme, (d, file) => { foreach (var (token, color) in PanelThemes.Derive(PanelTheme.PaletteOf(PanelTheme.ShownTheme), key, picked)) d.SetColor(file, token, color); }); }
            // A translucent color keeps its opacity: the picker chooses the hue, the file keeps the rest.
            else EditTheme(theme, (d, file) => { if (value is not { } color) { if (app) d.SetApp(file, key, null); else d.SetColor(file, key, null); }
                else if (app) d.SetApp(file, key, color.WithAlpha(255)); else d.SetColor(file, key, color.WithAlpha(written.A)); });
        }));
        return Row(title, controls, subtitle);
    }

    // The color as written, in a chip with its swatch; the color picker opens under it. choose receives the color picked, or
    // null for "Undo" in the picker, which is offered only for a changed color.
    private Button ColorChip(string title, ThemeColor color, ThemeColor surface, bool canUndo, Action<ThemeColor?> choose)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = UI.Brush(color.Over(surface).ToString()), Stroke = UI.Brush(_palette.Border), StrokeThickness = 1,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var text = TextLine(color.ToString(), 12, TextPrimary); text.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(text);
        var chip = new Button { Style = (Style)FindResource("SettingsButton"), Content = line, MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = UI.Brush(_palette.Input), BorderBrush = UI.Brush(_palette.InputBorder), Padding = new Thickness(10, 5, 12, 5) };
        AutomationProperties.SetName(chip, title + " " + color); chip.ToolTip = Loc.T("选择颜色", "Choose a color");
        var popup = new Popup { PlacementTarget = chip, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        popup.Child = ColorPicker(color.WithAlpha(255).ToString(), !canUndo, value =>
        {
            popup.IsOpen = false;
            PopupMotion.AfterClose(() => choose(value.Length == 0 ? null : ThemeColor.Parse(value)));
        }, Loc.T("撤销", "Undo"));
        TogglePopup(chip, popup);
        return chip;
    }

    // A numeric effect: a slider and its value, with a button that takes a change back. The value follows the slider; the page is
    // rebuilt to show the change as unsaved once the slider is let go, so a drag is never cut short.
    private Grid EffectRow(PanelThemeInfo theme, PanelThemeDraft? draft, PanelPalette palette, string key, string title, double tick, Func<double, string> format)
    {
        var (min, max, _) = PanelThemes.EffectRange(key);
        double value = PanelThemes.Effect(palette.Effects, key);
        bool changed = draft?.Effects.ContainsKey(key) == true;
        var row = new Grid { Height = 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(174) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(SettingLabel(title));
        var slider = new Slider { Style = (Style)FindResource("SettingsSlider"), Minimum = min, Maximum = max, Value = value, SmallChange = tick, LargeChange = tick * 4,
            TickFrequency = tick, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        AutomationProperties.SetName(slider, title);
        Grid.SetColumn(slider, 1); row.Children.Add(slider);
        var shown = TextLine(format(value), 12, TextSecondary); shown.Width = 64; shown.TextAlignment = TextAlignment.Right; shown.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(shown, 2); row.Children.Add(shown);
        var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), MinWidth = 104 };
        if (changed)
        {
            var mark = TextLine(Loc.T("修改未保存", "Unsaved"), 11.5, TextSecondary); mark.VerticalAlignment = VerticalAlignment.Center; mark.Margin = new Thickness(0, 0, 10, 0);
            mark.SetResourceReference(TextBlock.ForegroundProperty, "SettingsAccent");
            var undo = ActionButton(Loc.T("撤销", "Undo"), () => EditTheme(theme, (d, file) => d.SetEffect(file, key, null)));
            undo.Style = (Style)FindResource("SettingsLink"); undo.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(undo, Loc.T("撤销修改 ", "Undo change to ") + title);
            state.Children.Add(mark); state.Children.Add(undo);
        }
        Grid.SetColumn(state, 3); row.Children.Add(state);
        slider.ValueChanged += (_, _) =>
        {
            double next = Math.Round(Math.Round(slider.Value / tick) * tick, 4);
            shown.Text = format(next);
            var current = PanelTheme.ShownTheme;
            if (current.Id != theme.Id) return;
            PanelTheme.Edit(current, d => d.SetEffect(current.File, key, next));
            // A key press or a click on the track ends at once; a drag ends when the thumb is let go.
            if (Mouse.LeftButton == MouseButtonState.Released) QueueThemeRebuild();
        };
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => QueueThemeRebuild()));
        slider.PreviewMouseLeftButtonUp += (_, _) => QueueThemeRebuild();
        return row;
    }

    private void QueueThemeRebuild()
    {
        if (_themeRebuildQueued) return;
        _themeRebuildQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _themeRebuildQueued = false;
            if (IsLoaded && _page == ThemePage) RebuildThemePage();
        });
    }
    // The page again, without reading the themes folder: the change is in memory only.
    private void RebuildThemePage() { _themesFresh = true; ShowPage(ThemePage); }

    // Changes the shown theme's draft and shows the result on this page.
    private void EditTheme(PanelThemeInfo theme, Action<PanelThemeDraft, PanelThemeFile> edit)
    {
        // The page may have been built from an earlier reading of the theme's file.
        var current = PanelTheme.ShownTheme;
        if (current.Id == theme.Id) PanelTheme.Edit(current, draft => edit(draft, current.File));
        if (current.Id == theme.Id) RebuildThemePage(); else { _themesFresh = false; ShowPage(ThemePage); }
    }

    // The readability adjustments: one row with their count, which opens the list of them underneath.
    private List<FrameworkElement> ReadabilityRows(IReadOnlyList<PanelThemeAdjustment> adjustments)
    {
        if (adjustments.Count == 0) return [];
        int count = adjustments.Count;
        var toggle = ActionButton(Loc.T($"已调整 {count} 项颜色", $"{count} color{(count == 1 ? "" : "s")} adjusted") + (_themeReadabilityOpen ? " ▴" : " ▾"),
            () => { _themeReadabilityOpen = !_themeReadabilityOpen; RebuildThemePage(); });
        toggle.Style = (Style)FindResource("SettingsLink");
        AutomationProperties.SetName(toggle, _themeReadabilityOpen ? Loc.T("收起可读性调整", "Hide readability adjustments") : Loc.T("查看可读性调整", "Show readability adjustments"));
        var rows = new List<FrameworkElement>
        {
            Row(Loc.T("可读性调整", "Readability"), toggle, help: () => Loc.T("为保证文字与信息清晰可读，低于对比度下限的颜色会沿原色相自动调亮或调暗到刚好达标，不影响主题文件本身。",
                "To keep text and figures readable, a color below its contrast minimum is lightened or darkened along its own hue just enough to pass. The theme file is not changed."))
        };
        if (_themeReadabilityOpen) rows.AddRange(adjustments.Select(AdjustmentRow));
        return rows;
    }

    // One adjustment: the color's name at the left; its contrast, and the color as written and as shown, at the right.
    private Grid AdjustmentRow(PanelThemeAdjustment adjustment)
    {
        var token = PanelThemes.Tokens.FirstOrDefault(token => token.Key == adjustment.Key);
        string name = token is not null ? Loc.T(token.Chinese, token.English) : Loc.T("应用颜色 · ", "App color · ") + AppColorName(adjustment.Key);
        var row = new Grid { MinHeight = 34, Margin = new Thickness(16, 0, 0, 0), ToolTip = adjustment.Describe() };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = TextLine(name, 12, TextSecondary); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
        var values = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        string ratio = adjustment.Ratio.ToString("0.0", CultureInfo.InvariantCulture), minimum = adjustment.Minimum.ToString("0.#", CultureInfo.InvariantCulture);
        var contrast = TextLine(Loc.T($"对比度 {ratio}:1，需要 {minimum}:1", $"Contrast {ratio}:1, needs {minimum}:1"), 11.5, TextTertiary);
        contrast.VerticalAlignment = VerticalAlignment.Center; contrast.Margin = new Thickness(0, 0, 16, 0);
        values.Children.Add(contrast);
        void Color(string hex)
        {
            values.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = UI.Brush(hex), Stroke = UI.Brush(_palette.Border), StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var text = TextLine(hex, 11.5, TextSecondary); text.VerticalAlignment = VerticalAlignment.Center; values.Children.Add(text);
        }
        Color(adjustment.Before);
        var arrow = TextLine("→", 11.5, TextTertiary); arrow.VerticalAlignment = VerticalAlignment.Center; arrow.Margin = new Thickness(8, 0, 8, 0); values.Children.Add(arrow);
        Color(adjustment.After);
        if (!adjustment.Reached)
        {
            var note = TextLine(Loc.T("无法完全达标", "Not fully reached"), 11.5, _palette.Dark ? "#F0A6AA" : "#B12E3A"); note.VerticalAlignment = VerticalAlignment.Center; note.Margin = new Thickness(10, 0, 0, 0);
            values.Children.Add(note);
        }
        Grid.SetColumn(values, 1); row.Children.Add(values);
        return row;
    }

    // A card that saves the unsaved changes, in a theme card's frame: blue to save them as a new theme, amber to overwrite the
    // current one. color null is the settings accent.
    private Button SaveCard(string label, string? color, Action save)
    {
        var card = ActionButton("", save);
        card.Style = (Style)FindResource("SettingsSaveCard"); card.Content = null; card.Tag = label; card.Margin = new Thickness(6, 3, 6, 6);
        if (color is null) card.SetResourceReference(ForegroundProperty, "SettingsAccent"); else card.Foreground = UI.Brush(color);
        return card;
    }
    private string OverwriteColor => CautionColor;
    // Amber, for what asks for care: overwriting a theme, a reading that was stopped.
    private string CautionColor => _palette.Dark ? "#E8B04B" : "#B7791F";

    // The chosen theme of the user's while it has unsaved changes. Its frame stays the chosen card's color, so it is still seen as
    // the theme in use.
    private Button OverwriteCard(PanelThemeInfo theme)
    {
        var card = SaveCard(Loc.T("覆盖当前主题", "Overwrite current theme"), OverwriteColor, OverwriteThemeDraft);
        card.BorderBrush = UI.Brush("#529DFF");
        card.ToolTip = Loc.T($"把未保存的修改写入“{theme.DisplayName}”的主题文件。", $"Writes the unsaved changes into the theme file of \"{theme.DisplayName}\".");
        AutomationProperties.SetName(card, Loc.T("覆盖当前主题 ", "Overwrite current theme ") + theme.DisplayName);
        return card;
    }

    // Writes the changed values into the current theme's file, which takes effect at once as the theme is in use.
    private void OverwriteThemeDraft()
    {
        var theme = PanelTheme.ShownTheme;
        if (theme.Path is not { } path || PanelTheme.DraftOf(theme.Id) is not { } draft) return;
        try { File.WriteAllText(path, PanelThemes.WriteDraft(File.ReadAllText(path), draft)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { SaveStatus(Loc.T("无法保存主题：", "Could not save the theme: ") + ex.Message, true); return; }
        PanelTheme.Discard(theme.Id);
        SaveStatus("");
        ShowSavedTheme(reload: true);
    }

    // Saves the current theme with its changes as a new theme of the user's and switches to it.
    private void SaveThemeDraftAsNew()
    {
        var theme = PanelTheme.ShownTheme;
        if (PanelTheme.DraftOf(theme.Id) is not { } draft) return;
        try
        {
            var created = PanelThemes.SaveDraftAsNew(PanelTheme.Folder, theme, draft);
            PanelTheme.Discard(theme.Id);
            PanelTheme.Reload();
            ChooseOwnTheme(created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { SaveStatus(Loc.T("无法保存主题：", "Could not save the theme: ") + ex.Message, true); return; }
        SaveStatus("");
        ShowSavedTheme(reload: false);
    }

    // A saved theme is the one in use, so the panel opens to show it, as choosing a card does.
    private void ShowSavedTheme(bool reload)
    {
        _themesFresh = !reload; ShowPage(ThemePage);
        _preview = true; _previewPage = S.UsagePage ? DeckPage.Usage : DeckPage.Music; _app.Deck.Preview(true);
    }

    // Chooses a theme file just written to the folder (read again by the caller), keeping what was changed under the theme being left.
    private void ChooseOwnTheme(string path)
    {
        // The folder as just read, including this new file, so the watcher's report of the write reloads nothing.
        _themeStamp = ThemeFolderStamp();
        var id = PanelTheme.UserThemes.FirstOrDefault(t => t.Usable && string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))?.Id;
        if (id is not null) Change(s => { var kept = ThemeChoices.From(s); ChooseTheme(s, id); kept.ApplyTo(s); });
    }
}

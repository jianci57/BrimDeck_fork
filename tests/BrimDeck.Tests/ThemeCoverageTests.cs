using BrimDeck.Core;

// Theme coverage across every built-in theme (and "system", which resolves to dark or light): every token the app reads resolves,
// and the color pairs the panel, its pop-ups and the notice window actually draw keep their contrast.
internal static class ThemeCoverageTests
{
    // Where a pair is drawn: the foreground token and the backdrop it sits on, with the layers under that backdrop.
    internal sealed record Pair(string Where, string Foreground, string[] Backdrop, double Minimum);

    // Text needs 4.5:1, icons, focus rings and graphics 3:1 (WCAG 1.4.3 and 1.4.11).
    internal static readonly Pair[] Pairs =
    [
        // Body text on the panel and its pop-ups.
        new("Primary text on the surface", "textPrimary", ["surface"], 4.5),
        new("Primary text in pop-ups", "textPrimary", ["popup", "surface"], 4.5),
        new("Primary text on a hovered menu row", "textPrimary", ["hover", "popup", "surface"], 4.5),
        new("Primary text on a pressed menu row", "textPrimary", ["pressed", "popup", "surface"], 4.5),
        new("Primary text on a hovered panel button", "textPrimary", ["hover", "surface"], 4.5),
        new("Primary text on the selected segment", "textPrimary", ["fillSelected", "surface"], 4.5),
        new("Primary text on a notice button", "textPrimary", ["fill", "surface"], 4.5),
        new("Primary text on a pressed notice button", "textPrimary", ["pressed", "surface"], 4.5),
        new("Primary text in input fields", "textPrimary", ["input", "popup", "surface"], 4.5),
        new("Primary text in tooltips", "textPrimary", ["tooltip", "surface"], 4.5),
        new("Primary text in the seek bubble", "textPrimary", ["bubble", "surface"], 4.5),
        new("Secondary text on the surface", "textSecondary", ["surface"], 4.5),
        new("Secondary text in pop-ups", "textSecondary", ["popup", "surface"], 4.5),
        new("Secondary text on hover", "textSecondary", ["hover", "surface"], 4.5),
        new("Secondary text on a hovered menu row", "textSecondary", ["hover", "popup", "surface"], 4.5),
        new("Secondary text on the segment strip", "textSecondary", ["fill", "surface"], 4.5),
        new("Secondary text on a hovered page button", "textSecondary", ["fillSelected", "surface"], 4.5),
        new("Secondary text on a page button in an open notice", "textSecondary", ["fill", "fill", "surface"], 4.5),
        new("Secondary text on a hovered page button in an open notice", "textSecondary", ["fillSelected", "fill", "surface"], 4.5),
        new("Error text on the surface", "danger", ["surface"], 4.5),
        new("Error text in pop-ups", "danger", ["popup", "surface"], 4.5),
        // Text on the accent: the chosen date and a notice's main button.
        new("Text on the accent", "onAccent", ["accent", "popup", "surface"], 4.5),
        new("Text on the accent over the surface", "onAccent", ["accent", "surface"], 4.5),
        // Graphics: keyboard focus, marks and the default clock.
        new("Focus ring on the surface", "accent", ["surface"], 3),
        new("Focus ring in pop-ups", "accent", ["popup", "surface"], 3),
        new("Focus ring on a hovered button", "accent", ["hover", "surface"], 3),
        new("Icons in tertiary text on the surface", "textTertiary", ["surface"], 3),
        new("Icons in tertiary text in pop-ups", "textTertiary", ["popup", "surface"], 3),
        new("Tertiary text in an open notice", "textTertiary", ["fill", "surface"], 3),
        new("Days inside a date range", "textPrimary", ["rangeFill", "popup", "surface"], 4.5),
        new("Progress on its track", "accent", ["track", "surface"], 3),
        new("Song titles in menus", "menuDetail", ["popup", "surface"], 3),
        new("Song titles on a hovered menu row", "menuDetail", ["hover", "popup", "surface"], 3),
        new("Liked mark", "liked", ["surface"], 3),
        new("Equalizer", "equalizer", ["surface"], 3),
        new("Default clock", "clock", ["surface"], 3),
        new("Playback progress on the indicator", "indicatorProgress", ["indicator"], 2),
    ];

    // Decorative pairs measured but not held to 3:1, with the reason. They separate areas whose content (text, values) already
    // carries the meaning, as the native Windows and macOS controls these are modelled on do.
    internal static readonly (string Foreground, string Backdrop, string Reason)[] Decorative =
    [
        ("hairline", "surface", "dividers between labelled groups"),
        ("track", "surface", "the empty part of a bar whose filled part and value are shown"),
        ("ringTrack", "surface", "the empty part of a ring whose value is shown"),
        ("seekRail", "surface", "the empty part of the seek bar; the times are shown"),
        ("scrollThumb", "popup", "an overlay scroll bar; content can also be scrolled with the wheel and keys"),
        ("popupBorder", "surface", "a pop-up also stands apart by its own surface and shadow"),
        ("rim", "surface", "the island's edge; the surface itself stands apart from the desktop"),
    ];

    // The settings window's own light and dark palette (SettingsPalette.cs in the app, read as source since the tests do not load
    // WPF): its text levels on the surfaces they sit on, white text on the accent buttons and the focus ring.
    private static void SettingsPalette(Action<string, bool> check, bool report)
    {
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        if (source is null) { check("The source tree is found for the settings palette", false); return; }
        var text = File.ReadAllText(Path.Combine(source.FullName, "src", "BrimDeck", "SettingsPalette.cs"));
        var colors = new Dictionary<(string, bool), ThemeColor>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text,
            @"(?:public string (\w+) => |Brush\(""(\w+)"", )(?:Dark \? ""(#\w+)"" : ""(#\w+)""|""(#\w+)"")"))
        {
            var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            var dark = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[5].Value;
            var light = match.Groups[4].Success ? match.Groups[4].Value : match.Groups[5].Value;
            colors[(key, true)] = ThemeColor.Parse(dark); colors[(key, false)] = ThemeColor.Parse(light);
        }
        (string Foreground, string Background, double Minimum)[] pairs =
        [
            ("Primary", "Surface", 4.5), ("Primary", "Sidebar", 4.5), ("Primary", "Inset", 4.5), ("Primary", "Popup", 4.5),
            ("Secondary", "Surface", 4.5), ("Secondary", "Sidebar", 4.5), ("Secondary", "Inset", 4.5), ("Secondary", "SettingsFillHover", 4.5),
            ("Tertiary", "Surface", 4.5), ("Tertiary", "Sidebar", 4.5), ("Tertiary", "Inset", 4.5),
            ("SettingsError", "Surface", 4.5), ("SettingsError", "Inset", 4.5),
            ("White", "SettingsAccentFill", 4.5), ("White", "SettingsAccentHover", 4.5),
            ("SettingsAccent", "Surface", 3), ("SettingsAccent", "Popup", 3), ("SettingsAccent", "Inset", 3),
            // The edge of an input (fields, drop-downs, a switch's off track) stands out from everything it can sit on (WCAG 1.4.11).
            ("InputBorder", "Surface", 3), ("InputBorder", "Inset", 3), ("InputBorder", "Input", 3), ("InputBorder", "Popup", 3),
        ];
        colors[("White", true)] = colors[("White", false)] = ThemeColor.White;
        var failures = new List<string>();
        foreach (var dark in new[] { true, false })
            foreach (var (fg, bg, minimum) in pairs)
            {
                if (!colors.TryGetValue((fg, dark), out var f) || !colors.TryGetValue((bg, dark), out var b)) { failures.Add($"{fg} or {bg} not found"); continue; }
                double ratio = ThemeColor.Contrast(f.Over(b), b);
                if (report) Console.WriteLine($"  settings-{(dark ? "dark" : "light"),-13} {fg} on {bg}: {ratio:0.00} (min {minimum})");
                if (ratio < minimum - .005) failures.Add($"{(dark ? "dark" : "light")}: {fg} on {bg} {ratio:0.00} < {minimum}");
            }
        check("The settings window keeps contrast in light and dark" + (failures.Count > 0 ? ": " + string.Join("; ", failures) : ""), failures.Count == 0);
    }

    internal static IEnumerable<(string Id, PanelPalette Palette)> Palettes()
    {
        foreach (var theme in PanelThemes.BuiltIns) yield return (theme.Id, PanelThemes.Resolve(theme));
        // "system" resolves to one of these; both of its sides are covered above, but it is named so a change to Find shows here.
        yield return ("system-dark", PanelThemes.Resolve(PanelThemes.Find(PanelThemes.SystemId, true, [])));
        yield return ("system-light", PanelThemes.Resolve(PanelThemes.Find(PanelThemes.SystemId, false, [])));
        // Windows' high contrast in its black and white schemes, as the panel shows it whatever theme is chosen.
        yield return ("high-contrast-black", PanelThemes.HighContrast(ThemeColor.Parse("#000000"), ThemeColor.Parse("#FFFFFF"), ThemeColor.Parse("#3FF23F"), ThemeColor.Parse("#1AEBFF"), ThemeColor.Parse("#000000")));
        yield return ("high-contrast-white", PanelThemes.HighContrast(ThemeColor.Parse("#FFFFFF"), ThemeColor.Parse("#000000"), ThemeColor.Parse("#600000"), ThemeColor.Parse("#37006E"), ThemeColor.Parse("#FFFFFF")));
    }

    // A backdrop as drawn: its layers composited bottom up over the bottom layer, which is opaque. A surface with a gradient
    // gives one backdrop per stop, as text must read on all of them.
    internal static IReadOnlyList<ThemeColor> Backdrops(PanelPalette palette, string[] layers)
    {
        IEnumerable<ThemeColor> bottoms = layers[^1] == "surface" ? palette.SurfaceStops : [palette[layers[^1]].Over(ThemeColor.Black)];
        return [.. bottoms.Select(bottom =>
        {
            var color = bottom;
            for (int i = layers.Length - 2; i >= 0; i--) color = palette[layers[i]].Over(color);
            return color;
        })];
    }

    internal static double Contrast(PanelPalette palette, string foreground, string[] backdrop)
        => Backdrops(palette, backdrop).Min(back => ThemeColor.Contrast(palette[foreground].Over(back), back));

    internal static void Run(Action<string, bool> check, bool report = false)
    {
        var palettes = Palettes().ToList();
        // Every token the app reads is set, by the theme or its base chain (the dark theme sets them all).
        var dark = PanelThemes.BuiltIn(PanelThemes.Dark)!.File;
        check("The dark theme, which every theme inherits from, sets every token", PanelThemes.Tokens.All(token => dark.Colors!.ContainsKey(token.Key)));
        check("Every theme resolves every token", palettes.All(p => PanelThemes.Tokens.All(token => p.Palette.Colors.ContainsKey(token.Key))));
        check("Every theme resolves the warning and critical colors and every app", palettes.All(p => PanelThemes.AppKeys.All(key => p.Palette.Apps.ContainsKey(key))));
        check("Every built-in theme names an existing base", PanelThemes.BuiltIns.All(theme => theme.File.Base.Length == 0 || PanelThemes.IsBuiltIn(PanelThemes.NormalizeId(theme.File.Base))));

        var failures = new List<string>();
        foreach (var (id, palette) in palettes)
            foreach (var pair in Pairs)
            {
                double ratio = Contrast(palette, pair.Foreground, pair.Backdrop);
                if (report) Console.WriteLine($"  {id,-22} {pair.Where,-44} {ratio,5:0.00} (min {pair.Minimum})");
                if (ratio < pair.Minimum - .005) failures.Add($"{id}: {pair.Where} {ratio:0.00} < {pair.Minimum}");
            }
        check("Every theme keeps contrast on every pair the panel draws" + (failures.Count > 0 ? ": " + string.Join("; ", failures) : ""), failures.Count == 0);
        // The text roles, the warning and critical colors (also text) and the scroll thumb, each at its worst over everything the
        // surface can show (gradient stops, texture squares, glass extremes, motifs) and, for text, the pop-ups.
        var roles = new List<string>();
        foreach (var (id, palette) in palettes)
        {
            var popup = palette["popup"].Over(palette.Surface);
            foreach (var (name, color, minimum, backgrounds) in new (string, ThemeColor, double, IReadOnlyList<ThemeColor>)[]
            {
                ("textPrimary", palette["textPrimary"], PanelThemes.StrongTextContrast, [.. palette.SurfaceStops, popup]),
                ("textSecondary", palette["textSecondary"], PanelThemes.TextContrast, [.. palette.SurfaceStops, popup]),
                ("textTertiary", palette["textTertiary"], PanelThemes.TextContrast, [.. palette.SurfaceStops, popup]),
                ("warning", palette.Apps["warning"], PanelThemes.TextContrast, palette.SurfaceStops),
                ("critical", palette.Apps["critical"], PanelThemes.TextContrast, palette.SurfaceStops),
                ("scrollThumb", palette["scrollThumb"], PanelThemes.ThumbContrast, palette.SurfaceStops),
                // The handle as it rests while the pointer is elsewhere (App.xaml).
                ("scrollThumb at rest", palette["scrollThumb"].WithAlpha((byte)Math.Round(palette["scrollThumb"].A * palette.ThumbRestOpacity)), PanelThemes.ThumbContrast, palette.SurfaceStops)
            })
            {
                double ratio = color.MinimumContrast(backgrounds);
                if (report) Console.WriteLine($"  {id,-22} {"role " + name,-44} {ratio,5:0.00} (min {minimum})");
                if (ratio < minimum - .005) roles.Add($"{id}: {name} {ratio:0.00} < {minimum}");
            }
        }
        check("Every theme's text roles, warning and critical reach 4.5:1 (primary 7:1) and its scroll thumb 2:1, at rest too" + (roles.Count > 0 ? ": " + string.Join("; ", roles) : ""), roles.Count == 0);
        SettingsPalette(check, report);
        if (report)
            foreach (var (id, palette) in palettes)
                foreach (var (fg, bg, _) in Decorative)
                    Console.WriteLine($"  {id,-22} decorative {fg} on {bg}: {Contrast(palette, fg, [bg]):0.00}");
    }
}

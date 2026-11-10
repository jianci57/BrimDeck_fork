using System.Text.Json;
using BrimDeck.Core;

// Theme edits made on the settings page: drafts laid over a file, written into its text, the three seed colors, and a draft
// saved as a new theme.
internal static class PanelThemeEditorTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        var dark = PanelThemes.BuiltIn(PanelThemes.Dark)!;
        var dracula = PanelThemes.BuiltIn("dracula")!;
        ThemeColor C(string text) => ThemeColor.Parse(text);
        string Folder(string name) { var folder = Path.Combine(root, name); Directory.CreateDirectory(folder); return folder; }

        // Drafts.
        var draft = new PanelThemeDraft("dracula");
        draft.SetColor(dracula.File, "surface", C("#101010"));
        draft.SetApp(dracula.File, "claude", C("#AA5500"));
        draft.SetEffect(dracula.File, "shadowBlur", 30);
        var applied = PanelThemes.ApplyDraft(dracula.File, draft);
        check("A draft is laid over a copy of the file", applied.Colors!["surface"] == "#101010" && applied.Apps!["claude"] == "#AA5500" && applied.Effects!.ShadowBlur == 30
            && dracula.File.Colors!["surface"] == "#21222C" && dracula.File.Effects!.ShadowBlur is null);
        var built = PanelThemes.Build("dracula", applied);
        check("A palette built from a draft shows its changes", built.Surface == C("#101010") && built.Effects.ShadowBlur == 30 && built.WrittenApps["claude"] == C("#AA5500"));
        draft.SetColor(dracula.File, "surface", C("#21222C"));
        draft.SetEffect(dracula.File, "shadowOpacity", .4);
        check("A change back to the file's own value is dropped", !draft.Colors.ContainsKey("surface") && !draft.Effects.ContainsKey("shadowOpacity") && draft.Count == 2);
        int version = draft.Version;
        draft.SetApp(dracula.File, "claude", null);
        check("Taking a change back drops it and counts as a change", !draft.Apps.ContainsKey("claude") && draft.Version > version);
        var cased = PanelThemes.Parse("""{ "id": "x", "colors": { "Surface": "#202020" } }""");
        var casedDraft = new PanelThemeDraft("x");
        casedDraft.SetColor(cased, "surface", C("#303030"));
        var casedApplied = PanelThemes.ApplyDraft(cased, casedDraft);
        check("A key the file writes in another case is replaced, not kept beside", casedApplied.Colors!.Count == 1 && casedApplied.Colors["surface"] == "#303030");

        // Writing a draft into a file's text.
        var template = PanelThemes.FillTemplate("edited", PanelThemes.Resolve(dark));
        var edits = new PanelThemeDraft("edited");
        var templateFile = PanelThemes.Parse(template);
        edits.SetColor(templateFile, "accent", C("#FF00AA"));
        edits.SetColor(templateFile, "hairline", C("#30FFFFFF"));
        edits.SetApp(templateFile, "codex", C("#00FF00"));
        edits.SetEffect(templateFile, "scanlines", .05);
        var written = PanelThemes.WriteDraft(template, edits);
        var lines = template.Split('\n'); var writtenLines = written.Split('\n');
        int changedLines = lines.Zip(writtenLines).Count(pair => pair.First != pair.Second);
        check("Writing a draft changes only the lines of the changed values and keeps the comments",
            lines.Length == writtenLines.Length && changedLines == 4 && written.Contains("\"accent\": \"#FF00AA\",            // Keyboard focus", StringComparison.Ordinal)
            && written.Contains("\"scanlines\": 0.05", StringComparison.Ordinal) && written.Contains("// BrimDeck panel theme template", StringComparison.Ordinal));
        var reread = PanelThemes.Build("edited", PanelThemes.Parse(written));
        var expected = PanelThemes.Build("edited", PanelThemes.ApplyDraft(templateFile, edits));
        check("The written file reads back as the draft laid over the file",
            reread.WrittenColors.All(pair => expected.WrittenColors[pair.Key] == pair.Value) && reread.WrittenApps.All(pair => expected.WrittenApps[pair.Key] == pair.Value)
            && PanelThemes.EffectKeys.All(key => PanelThemes.Effect(reread.Effects, key) == PanelThemes.Effect(expected.Effects, key))
            && reread["accent"] == C("#FF00AA") && reread.Effects.Scanlines == .05);

        var sparse = "{\n  // Just a name.\n  \"id\": \"sparse\",\n  \"name\": \"Sparse\",\n  \"apps\": { \"claude\": \"#111111\" },\n  \"effects\": {}\n}\n";
        var sparseFile = PanelThemes.Parse(sparse);
        var additions = new PanelThemeDraft("sparse");
        additions.SetColor(sparseFile, "surface", C("#123456"));
        additions.SetApp(sparseFile, "codex", C("#222222"));
        additions.SetEffect(sparseFile, "rimWidth", 2);
        var grown = PanelThemes.WriteDraft(sparse, additions);
        var grownFile = PanelThemes.Parse(grown);
        check("Missing objects and keys are added: on their own line, on the same line, and into an empty object",
            grownFile.Colors!["surface"] == "#123456" && grownFile.Apps!["codex"] == "#222222" && grownFile.Apps["claude"] == "#111111" && grownFile.Effects!.RimWidth == 2
            && grown.Contains("  \"colors\": { \"surface\": \"#123456\" },\n  \"id\": \"sparse\"", StringComparison.Ordinal)
            && grown.Contains("\"apps\": { \"codex\": \"#222222\", \"claude\"", StringComparison.Ordinal) && grown.Contains("// Just a name.", StringComparison.Ordinal));
        var crlf = sparse.Replace("\n", "\r\n", StringComparison.Ordinal);
        var crlfGrown = PanelThemes.WriteDraft(crlf, additions);
        check("A new line follows the file's own line endings", !crlfGrown.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n'));
        var nulled = PanelThemes.WriteDraft("{ \"id\": \"n\", \"colors\": null }", additions);
        check("A member that holds null where an object belongs is replaced", PanelThemes.Parse(nulled).Colors!["surface"] == "#123456");
        bool refused = false;
        try { PanelThemes.WriteDraft("{ \"id\": \"broken\", \"colors\": { \"surface\" \"#000000\" } }", additions); }
        catch (JsonException) { refused = true; }
        check("A file that is not well formed is not written over", refused);

        // The three seed colors.
        var darkPalette = PanelThemes.Resolve(dark);
        var light = PanelThemes.Derive(darkPalette, PanelThemes.BackgroundKey, C("#F4F4F6"));
        check("A light background on a dark theme brings a dark foreground and light-theme shades",
            light["surface"] == C("#F4F4F6") && light["textPrimary"] == C("#1D1D1F") && light["textSecondary"] == C("#1D1D1F").WithAlpha(0xB8)
            && light["popup"].Luminance > C("#F4F4F6").Luminance && light.ContainsKey("onAccent") && light["rangeFill"].A == 0x2E);
        var lightDraft = new PanelThemeDraft("dark");
        foreach (var (key, color) in light) lightDraft.SetColor(dark.File, key, color);
        var lightPalette = PanelThemes.Build("dark", PanelThemes.ApplyDraft(dark.File, lightDraft));
        check("A theme made from a light background keeps its text as derived", !lightPalette.IsDark
            && lightPalette.Adjustments.All(adjustment => adjustment.Key is not ("textPrimary" or "textSecondary" or "textTertiary")));
        var navy = PanelThemes.Derive(darkPalette, PanelThemes.BackgroundKey, C("#0B1530"));
        check("A dark background keeps the foreground and mixes the pop-ups toward it",
            !navy.ContainsKey("textPrimary") && navy["popup"] == ThemeColor.Mix(C("#0B1530"), C("#F5F5F7"), .10) && navy["hairline"] == C("#F5F5F7").WithAlpha(0x17) && !navy.ContainsKey("onAccent"));
        var lightTheme = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Light)!);
        check("Derived tracks are as strong as the dark and light themes' own",
            navy["track"].A == darkPalette["track"].A && navy["ringTrack"].A == darkPalette["ringTrack"].A
            && light["track"].A == lightTheme["track"].A && light["ringTrack"].A == lightTheme["ringTrack"].A);
        var warm = PanelThemes.Derive(darkPalette, PanelThemes.ForegroundKey, C("#FFE8C0"));
        check("A new foreground brings the text, clock and lines in its color",
            warm["clock"] == C("#FFE8C0") && warm["textTertiary"] == C("#FFE8C0").WithAlpha(0x8C) && !warm.ContainsKey("surface") && !warm.ContainsKey("accent"));
        var yellow = PanelThemes.Derive(darkPalette, PanelThemes.AccentKey, C("#FFD60A"));
        var blue = PanelThemes.Derive(darkPalette, PanelThemes.AccentKey, C("#0A3DFF"));
        check("A new accent brings readable text on it and the date range, and nothing else",
            yellow.Count == 3 && yellow["onAccent"] == ThemeColor.Black && blue["onAccent"] == ThemeColor.White && yellow["rangeFill"] == C("#FFD60A").WithAlpha(0x38));

        // Saving a draft as a new theme.
        var folder = Folder("theme-editor");
        // Themes in the order they were added: a new theme goes last, even under a file name that sorts first or one just deleted.
        var order = Folder("theme-order");
        var first = PanelThemes.CreateFromTemplate(order, darkPalette);
        var second = PanelThemes.CreateFromTemplate(order, darkPalette);
        var third = PanelThemes.CreateFromTemplate(order, darkPalette);
        File.SetCreationTimeUtc(first, DateTime.UtcNow.AddMinutes(-3)); File.SetCreationTimeUtc(second, DateTime.UtcNow.AddMinutes(-2)); File.SetCreationTimeUtc(third, DateTime.UtcNow.AddMinutes(-1));
        File.Delete(first);
        var again = PanelThemes.CreateFromTemplate(order, darkPalette);
        var listed = PanelThemes.InAddedOrder(PanelThemes.LoadFolder(order)).Select(theme => theme.Path).ToList();
        check("A new theme is listed after the others, though it takes the name of one just deleted",
            again == first && listed.SequenceEqual([second, third, first]));

        var saved = PanelThemes.SaveDraftAsNew(folder, dracula, draft);
        var savedTheme = PanelThemes.LoadFolder(folder).Single(theme => theme.Path == saved);
        var savedPalette = PanelThemes.Resolve(savedTheme);
        var draculaPalette = PanelThemes.Resolve(dracula);
        check("A built-in theme's draft is saved as a new theme that looks like the built-in one with the changes",
            savedTheme.Usable && savedTheme.File.Base == "dracula" && savedPalette.Effects.ShadowBlur == 30
            && PanelThemes.Tokens.All(token => savedPalette[token.Key] == draculaPalette[token.Key]));
    }
}

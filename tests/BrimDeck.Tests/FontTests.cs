using BrimDeck.Core;

// The shared font entry (UiFonts here, Fonts in the app) and the setting behind it: with no font chosen every role is the chain the
// app always used; a chosen font goes in front of it; the setting reads back, defaults for old files and survives hand edits. That
// the app's windows really take the chosen font is checked on real windows in BrimDeck.WindowsTests (FontFollowTests).
internal static class FontTests
{
    // The strings that were written at every use before the entry existed. A change here is a change of how the app looks with no font chosen.
    private static readonly (UiFontRole Role, string Chain)[] Original =
    [
        (UiFontRole.Text, "Microsoft YaHei"),
        (UiFontRole.Panel, "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
        (UiFontRole.Display, "Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI"),
        (UiFontRole.Menu, "Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI"),
        (UiFontRole.Label, "Segoe UI")
    ];

    public static void Run(string root, Action<string, bool> check)
    {
        // Defaults.
        check("with no font chosen every role is the chain the app always used",
            Original.All(o => UiFonts.Chain(o.Role, "") == o.Chain && UiFonts.Chain(o.Role, null) == o.Chain && UiFonts.DefaultChain(o.Role) == o.Chain)
            && Enum.GetValues<UiFontRole>().Length == Original.Length);
        check("a name that is only blanks is no font", Original.All(o => UiFonts.Chain(o.Role, "   \t ") == o.Chain));

        // A chosen font.
        check("a chosen font goes in front of each role's own chain",
            Original.All(o => UiFonts.Chain(o.Role, "LXGW WenKai") == "LXGW WenKai, " + o.Chain));
        check("the chosen font is trimmed and its blanks made one", UiFonts.Chain(UiFontRole.Panel, "  Source   Han  Sans ").StartsWith("Source Han Sans, Segoe UI Variable Text"));
        check("a font the chain already names is not repeated, whatever its case",
            UiFonts.Chain(UiFontRole.Panel, "segoe ui") == "segoe ui, Segoe UI Variable Text, Microsoft YaHei UI"
            && UiFonts.Chain(UiFontRole.Label, "Segoe UI") == "Segoe UI");
        check("the Chinese fallback survives in every text role of a custom font",
            new[] { UiFontRole.Panel, UiFontRole.Display, UiFontRole.Menu, UiFontRole.Text }.All(role => UiFonts.Chain(role, "Inter").Contains("YaHei")));
        check("the icon and code chains are fixed and never take a font",
            UiFonts.IconChain == "Segoe Fluent Icons, Segoe MDL2 Assets" && UiFonts.IconMdl2Chain == "Segoe MDL2 Assets" && UiFonts.MonoChain == "Cascadia Mono, Consolas");

        // Normalization keeps a hand-edited file from passing extra families, a path or a URI into the chain.
        string[] bad = ["Inter, Arial", "a;b", "\"Inter\"", "'Inter'", "pack://application:,,,/x#Inter", "./Fonts/#Inter", "C:\\Fonts\\Inter", "a/b", "Inter\u0001",
            "<b>", "x|y", "wild*", "what?", new string('A', UiFonts.MaxNameLength + 1)];
        check("a name that could not be one family is no font", bad.All(name => UiFonts.Normalize(name) == "" && UiFonts.Chain(UiFontRole.Panel, name) == Original[1].Chain));
        check("ordinary family names, Chinese ones and digits among them, are kept",
            UiFonts.Normalize("Segoe UI Variable") == "Segoe UI Variable" && UiFonts.Normalize("微软雅黑") == "微软雅黑" && UiFonts.Normalize("Noto Sans SC Medium") == "Noto Sans SC Medium"
            && UiFonts.Normalize("Source Han Sans CN-Bold") == "Source Han Sans CN-Bold" && UiFonts.Normalize(new string('A', UiFonts.MaxNameLength)).Length == UiFonts.MaxNameLength);

        check("icon and symbol families are not offered for text, ordinary ones are",
            new[] { "Wingdings", "Webdings 2", "Segoe MDL2 Assets", "Segoe Fluent Icons", "Symbol" }.All(UiFonts.IsSymbolFamily)
            && !new[] { "Segoe UI", "Verdana", "微软雅黑", "Symbolic Sans", "" }.Any(UiFonts.IsSymbolFamily));

        // The setting.
        check("the font setting starts empty", new DeckSettings().UiFont == "");
        var messy = new DeckSettings { UiFont = "  Inter   Display " }; messy.Normalize();
        var hostile = new DeckSettings { UiFont = "Inter, Evil" }; hostile.Normalize();
        var missing = new DeckSettings { UiFont = null! }; missing.Normalize();
        check("the font setting is normalized with the others", messy.UiFont == "Inter Display" && hostile.UiFont == "" && missing.UiFont == "");
        check("a copy of the settings keeps the font", new DeckSettings { UiFont = "Inter" }.Copy().UiFont == "Inter");

        var store = new SettingsStore(Path.Combine(root, "font-setting"), "0.1.0");
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","Width":900}""");
        check("a file saved before the setting reads as no font", store.Load().UiFont == "" && store.Load().Width == 900);
        var chosen = store.Load(); chosen.UiFont = "Inter"; store.Save(chosen);
        check("a chosen font is saved and read back", File.ReadAllText(store.FilePath).Contains("\"UiFont\": \"Inter\"") && new SettingsStore(store.DirectoryPath, "0.1.0").Load().UiFont == "Inter");
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","UiFont":"Inter, Arial"}""");
        check("a hand-edited list of families in the file reads as no font", store.Load().UiFont == "");
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","UiFont":null}""");
        check("a null font in the file reads as no font", store.Load().UiFont == "");
        var reset = new DeckSettings { UiFont = "Inter" }; reset.UiFont = new DeckSettings().UiFont;
        check("restoring the default is the empty name", reset.UiFont == "" && UiFonts.Chain(UiFontRole.Text, reset.UiFont) == Original[0].Chain);
    }
}

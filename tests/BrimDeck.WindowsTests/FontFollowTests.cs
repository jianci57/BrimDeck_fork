using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;
using Fonts = BrimDeck.Fonts;

// Changing the interface font while windows are open, on real windows made the ways the app makes its own: a resource reference
// (what XAML's DynamicResource is), a family set once in code, a window default, a menu and a tip hung on an element, and a window
// that keeps the app's own fonts (the settings window). Fonts.Use is the call the app makes when the setting changes. Nothing here
// reads source files: the windows are built, the font is changed, and the elements are looked at. Take Retarget out of Fonts.Use and
// the once-set families stay in the old font; take the resource publishing out and the resource-bound ones do.
internal static class FontFollowTests
{
    private sealed class OwnFontsWindow : Window, BrimDeck.IKeepsAppFonts { }

    public static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Fonts.Use("");
        Fonts.Publish(app.Resources);
        int start = Fonts.Version;
        string[] preferred = ["Verdana", "Tahoma", "Arial", "Calibri"];
        string installed = preferred.FirstOrDefault(name => System.Windows.Media.Fonts.SystemFontFamilies.Any(f => f.FamilyNames.Values.Contains(name))) ?? "Arial";

        const string sentence = "Quota used 1,234 of 5,000 中文混排";
        var bound = new TextBlock { Text = sentence }; bound.SetResourceReference(TextBlock.FontFamilyProperty, "FontPanel");
        var once = new TextBlock { Text = sentence, FontFamily = Fonts.Panel };
        var label = new TextBlock { Text = "12:30", FontFamily = Fonts.Label };
        var text = new TextBlock { Text = sentence, FontFamily = Fonts.Text };
        var tipped = new TextBlock { Text = "hover", FontFamily = Fonts.Panel, ToolTip = new TextBlock { Text = "tip " + sentence, FontFamily = Fonts.Panel } };
        var item = new MenuItem { Header = "Quit", FontFamily = Fonts.Menu };
        var menued = new TextBlock { Text = "menu", ContextMenu = new ContextMenu { Items = { item } }, FontFamily = Fonts.Panel };
        var icon = new TextBlock { Text = "", FontFamily = Fonts.Icon };
        var iconOld = new TextBlock { Text = "", FontFamily = Fonts.IconMdl2 };
        var mono = new TextBlock { Text = "0123456789", FontFamily = Fonts.Mono };
        var code = new TextBox { Text = "var x = 1; // 注释", FontFamily = Fonts.Code };
        var bundled = new TextBlock { Text = "12:30", FontFamily = Fonts.Bundled("Orbitron") };
        var stack = new StackPanel();
        foreach (var child in new UIElement[] { bound, once, label, text, tipped, menued, icon, iconOld, mono, code, bundled }) stack.Children.Add(child);
        var main = new Window { Left = -20000, Top = -20000, Width = 520, Height = 520, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, FontFamily = Fonts.Panel, Content = stack };
        // A window made the way the settings window is: while the app's own fonts are asked for, whatever is chosen.
        OwnFontsWindow own; TextBlock ownText, ownResource;
        using (Fonts.Plain())
        {
            ownText = new TextBlock { Text = sentence, FontFamily = Fonts.Panel };
            ownResource = new TextBlock { Text = sentence };
            own = new OwnFontsWindow { Left = -20000, Top = -20000, Width = 400, Height = 100, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, FontFamily = Fonts.Panel };
            own.Resources["FontPanel"] = Fonts.Panel; ownResource.SetResourceReference(TextBlock.FontFamilyProperty, "FontPanel");
            own.Content = new StackPanel { Children = { ownText, ownResource } };
        }
        try
        {
            main.Show(); own.Show(); main.UpdateLayout(); own.UpdateLayout();
            string panelDefault = UiFonts.DefaultChain(UiFontRole.Panel), menuDefault = UiFonts.DefaultChain(UiFontRole.Menu);
            string Source(TextBlock block) => block.FontFamily.Source ?? "";
            double Width(TextBlock block) => new FormattedText(block.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(block.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), block.FontSize, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
            check("with no font chosen the windows are in the shipped chains",
                Source(bound) == panelDefault && Source(once) == panelDefault && Source(label) == UiFonts.DefaultChain(UiFontRole.Label) && Source(text) == UiFonts.DefaultChain(UiFontRole.Text)
                && item.FontFamily.Source == menuDefault && main.FontFamily.Source == panelDefault);
            double narrow = once.DesiredSize.Width;
            check("the one-line text is as wide as the font it is set in", Math.Abs(narrow - Width(once)) < 1.5 && narrow > 40);

            // Another font.
            bool changed = Fonts.Use(installed);
            main.UpdateLayout(); own.UpdateLayout();
            string chosenPanel = UiFonts.Chain(UiFontRole.Panel, installed);
            check("choosing a font changes the current family and counts a change", changed && Fonts.Custom == installed && Fonts.Version == start + 1 && Fonts.Panel.Source == chosenPanel);
            check("a resource reference follows the font", Source(bound) == chosenPanel);
            check("a family set once in code follows the font", Source(once) == chosenPanel && Source(tipped) == chosenPanel && Source(text) == UiFonts.Chain(UiFontRole.Text, installed));
            check("the window's own default follows the font", main.FontFamily.Source == chosenPanel);
            check("a menu item and a tip hung on elements follow the font", item.FontFamily.Source == UiFonts.Chain(UiFontRole.Menu, installed) && ((TextBlock)tipped.ToolTip).FontFamily.Source == chosenPanel);
            check("the small label follows the font", Source(label) == UiFonts.Chain(UiFontRole.Label, installed));
            check("icons, monospaced figures, code and the clock's own face do not change",
                Source(icon) == UiFonts.IconChain && Source(iconOld) == UiFonts.IconMdl2Chain && Source(mono) == UiFonts.MonoChain
                && code.FontFamily == Fonts.Code && bundled.FontFamily.Source!.Contains("Orbitron"));
            check("a window that keeps the app's own fonts is left alone",
                Source(ownText) == panelDefault && Source(ownResource) == panelDefault && own.FontFamily.Source == panelDefault);
            double wide = once.DesiredSize.Width;
            check("the one-line text is measured again in the new font", Math.Abs(wide - Width(once)) < 1.5 && Math.Abs(wide - narrow) > 1);
            check("a family asked for after the change is the chosen one from the start",
                new TextBlock { FontFamily = Fonts.Panel }.FontFamily.Source == chosenPanel && Fonts.Face(UiFontRole.Panel).FontFamily.Source == chosenPanel);
            Typeface faceNow = Fonts.Face(UiFontRole.Panel);
            check("a typeface is kept until the font changes", ReferenceEquals(faceNow, Fonts.Face(UiFontRole.Panel)));
            using (Fonts.Plain())
                check("while the app's own fonts are asked for the chosen font is not used", Fonts.Panel.Source == panelDefault && Fonts.Face(UiFontRole.Panel).FontFamily.Source == panelDefault);
            check("outside that scope the chosen font is back", Fonts.Panel.Source == chosenPanel);
            int version = Fonts.Version;
            check("choosing the font already in use changes nothing", !Fonts.Use(installed) && !Fonts.Use("  " + installed + " ") && Fonts.Version == version);
            int raised = 0; void count() => raised++; Fonts.Changed += count;

            // A font the machine does not have, then a name that is no font, then none.
            const string missing = "Font That Is Not Installed 4711";
            Fonts.Use(missing); main.UpdateLayout();
            check("a font that is not installed leads the chain and the text still lays out in the chain behind it",
                raised == 1 && Source(once).StartsWith(missing + ", ") && once.DesiredSize.Width > 40 && Fonts.Face(UiFontRole.Panel).TryGetGlyphTypeface(out _)
                && new FormattedText("中文", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Fonts.Face(UiFontRole.Panel), 13, Brushes.Black, 1).Width > 10);
            Fonts.Use("Inter, Evil;Font"); main.UpdateLayout();
            check("a name that could not be one family is the app's own fonts", raised == 2 && Fonts.Custom == "" && Source(once) == panelDefault && Source(bound) == panelDefault);
            Fonts.Changed -= count;
            check("with no font chosen again the windows are in the shipped chains, as at the start",
                Source(text) == UiFonts.DefaultChain(UiFontRole.Text) && item.FontFamily.Source == menuDefault && main.FontFamily.Source == panelDefault
                && Math.Abs(once.DesiredSize.Width - narrow) < .5 && Source(icon) == UiFonts.IconChain);
            check("the resources published are the shipped chains again", Equals(app.Resources["FontPanel"], Fonts.Panel) && ((FontFamily)app.Resources["FontPanel"]).Source == panelDefault);
        }
        finally { Fonts.Use(""); main.Close(); own.Close(); }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// A window built with the app's own fonts, which a change of the user's font leaves alone (the settings window).
internal interface IKeepsAppFonts { }

// The one entry for fonts: every family BrimDeck draws text in comes from here (UI.TextFont, UI.PanelFont and UI.DisplayFont are
// these, kept for the call sites that already use them), and nothing else names a font.
//
// Text roles (UiFontRole) take the user's font (DeckSettings.UiFont) in front of their usual chain, and are republished as
// resources so that XAML follows with DynamicResource. Symbols (Icon, IconMdl2), code (Mono, Code) and the clock styles' own
// faces (Bundled) are fixed: a font chosen for text never reaches them.
//
// Anything built from a font's metrics (a Typeface, a FormattedText, a measured width) is valid for one Version only; the cache
// beside it compares the version it was made at (MonitorDraw does), or is rebuilt when Changed is raised.
internal static class Fonts
{
    private static readonly object Sync = new();
    private static string _custom = "";
    private static readonly FontFamily?[] Families = new FontFamily?[Enum.GetValues<UiFontRole>().Length];
    private static readonly Dictionary<(UiFontRole, FontWeight), Typeface> Faces = [];
    // While a window that keeps the app's own fonts is being built (the settings window: Plain), the families asked for are the usual ones, not the user's.
    [ThreadStatic] private static bool _plain;
    private static readonly FontFamily?[] PlainFamilies = new FontFamily?[Enum.GetValues<UiFontRole>().Length];

    // Counts the changes of the chosen font, from 0; text laid out at another count is stale.
    public static int Version { get; private set; }
    // The family the user chose, empty for the app's own fonts.
    public static string Custom => _custom;
    // Raised after a different font is made current and published.
    public static event Action? Changed;

    public static FontFamily Text => Of(UiFontRole.Text);
    public static FontFamily Panel => Of(UiFontRole.Panel);
    public static FontFamily Display => Of(UiFontRole.Display);
    public static FontFamily Menu => Of(UiFontRole.Menu);
    // Plain Segoe UI for small fixed labels.
    public static FontFamily Label => Of(UiFontRole.Label);

    // Symbols and code do not change with the user's font.
    public static readonly FontFamily Icon = new(UiFonts.IconChain);
    public static readonly FontFamily IconMdl2 = new(UiFonts.IconMdl2Chain);
    public static readonly FontFamily Mono = new(UiFonts.MonoChain);

    // The resource each role is published under (App.xaml declares the defaults under the same keys).
    public static string ResourceKey(UiFontRole role) => role switch
    {
        UiFontRole.Text => "AppTextFont",
        UiFontRole.Display => "FontDisplay",
        UiFontRole.Menu => "FontMenu",
        UiFontRole.Label => "FontLabel",
        _ => "FontPanel"
    };

    // The app's own families whatever the user chose, for what the settings window builds or draws later (a dialog, a popup, a tick label), outside Plain().
    public static FontFamily PlainOf(UiFontRole role) { lock (Sync) return PlainFamilies[(int)role] ??= new FontFamily(UiFonts.Chain(role, "")); }

    public static FontFamily Of(UiFontRole role)
    {
        lock (Sync) return _plain ? PlainFamilies[(int)role] ??= new FontFamily(UiFonts.Chain(role, "")) : Families[(int)role] ??= new FontFamily(UiFonts.Chain(role, _custom));
    }

    // The role's family in a weight, kept until the font changes; for text drawn or measured with a Typeface.
    public static Typeface Face(UiFontRole role, FontWeight? weight = null)
    {
        var w = weight ?? FontWeights.Normal;
        if (_plain) return new Typeface(Of(role), FontStyles.Normal, w, FontStretches.Normal);
        lock (Sync)
        {
            if (!Faces.TryGetValue((role, w), out var face))
                Faces[(role, w)] = face = new Typeface(Of(role), FontStyles.Normal, w, FontStretches.Normal);
            return face;
        }
    }

    // The panel's text family with the given user font in front: what the settings page's preview line is set in (the page itself is built Plain).
    public static FontFamily Sample(string? name) => new(UiFonts.Chain(UiFontRole.Panel, name));

    // Builds with the app's own fonts whatever the user chose: the settings window does not change with the font it sets (Fonts.Plain).
    public static IDisposable Plain() { var was = _plain; _plain = true; return new PlainScope(was); }
    private sealed class PlainScope(bool was) : IDisposable { public void Dispose() => _plain = was; }

    // Makes the saved family current. False when it already was (nothing is published or raised then). Everything already built
    // follows: the resources are published again, and a control that was given a family of its own when it was made (settings
    // window, small windows, notices) gets the new one in its place (Retarget).
    public static bool Use(string? name)
    {
        var clean = UiFonts.Normalize(name);
        Dictionary<string, FontFamily> follow = [];
        lock (Sync)
        {
            if (clean == _custom) return false;
            var before = Enum.GetValues<UiFontRole>().ToDictionary(role => role, role => UiFonts.Chain(role, _custom));
            _custom = clean; Array.Clear(Families); Faces.Clear(); Version++;
            foreach (var (role, chain) in before)
                if (!string.Equals(chain, UiFonts.Chain(role, clean), StringComparison.Ordinal)) follow[chain] = Of(role);
        }
        if (Application.Current is { } app) { Publish(app.Resources); Retarget(app, follow); }
        Changed?.Invoke();
        return true;
    }

    // Walks every window (visual and logical trees, and the menus and tips hung on the elements) and puts the new family where the
    // old text family was set on the element itself; what is bound to a resource follows by itself. Layout and drawing are asked
    // again, since both were made with the old family's metrics.
    internal static void Retarget(Application app, IReadOnlyDictionary<string, FontFamily> follow)
    {
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<DependencyObject>();
        foreach (Window window in app.Windows) if (window is not IKeepsAppFonts) pending.Push(window);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node)) continue;
            if (node.ReadLocalValue(TextElement.FontFamilyProperty) is FontFamily { Source: { } source } && follow.TryGetValue(source, out var next))
                node.SetValue(TextElement.FontFamilyProperty, next);
            if (node is UIElement element) { element.InvalidateMeasure(); element.InvalidateVisual(); }
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(node, i));
            if (node is FrameworkElement or FrameworkContentElement)
                foreach (var child in LogicalTreeHelper.GetChildren(node)) if (child is DependencyObject logical) pending.Push(logical);
            if (node is FrameworkElement { ContextMenu: { } menu }) pending.Push(menu);
            if (node is FrameworkElement { ToolTip: DependencyObject tip }) pending.Push(tip);
        }
    }

    // The families installed on this machine, by the name each is called by (the English one, which any language finds), for the
    // font picker. A walk over the system's fonts: made when the picker opens, not kept. Symbol fonts are left out.
    public static List<string> Installed()
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            string? name = null;
            foreach (var (language, value) in family.FamilyNames)
                if (language == System.Windows.Markup.XmlLanguage.GetLanguage("en-us")) { name = value; break; }
            name ??= family.FamilyNames.Values.FirstOrDefault() ?? family.Source;
            if (UiFonts.Normalize(name).Length > 0 && !UiFonts.IsSymbolFamily(name)) names.Add(name);
        }
        return [.. names];
    }

    // Puts the current text families into a resource dictionary (the application's), where DynamicResource finds them.
    public static void Publish(ResourceDictionary resources)
    {
        foreach (var role in Enum.GetValues<UiFontRole>()) resources[ResourceKey(role)] = Of(role);
    }

    // A face shipped inside the app (Assets/Fonts): the clock styles' own letterforms, which the user's font does not replace.
    internal static FontFamily Bundled(string name) => new(new Uri("pack://application:,,,/BrimDeck;component/"), "./Assets/Fonts/#" + name);

    // The script editor's face. Consolas has no Chinese glyphs. Chinese and full-width punctuation come from Microsoft YaHei UI, whose
    // glyphs are 1 em wide, scaled so each takes exactly two Consolas columns (2 × 0.5498 em); trailing comments then line up.
    public static FontFamily Code => _code.Value;
    private static readonly Lazy<FontFamily> _code = new(() =>
    {
        var consolas = new FontFamily("Consolas");
        var family = new FontFamily { Baseline = consolas.Baseline, LineSpacing = consolas.LineSpacing };
        family.FamilyNames[System.Windows.Markup.XmlLanguage.GetLanguage("en-us")] = "BrimDeck Code";
        family.FamilyMaps.Add(new FontFamilyMap { Unicode = "2E80-9FFF,F900-FAFF,FE30-FE4F,FF00-FF60,FFE0-FFE6", Target = "Microsoft YaHei UI", Scale = 2 * 0.5498046875 });
        family.FamilyMaps.Add(new FontFamilyMap { Unicode = "0000-10FFFF", Target = "Consolas" });
        return family;
    });
}

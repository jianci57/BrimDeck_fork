namespace BrimDeck.Core;

// What a piece of text is for. Each role has its own default family chain (the one the app has always used there), and the
// user's own font, when one is chosen, goes in front of that chain, so that anything it lacks (Chinese, symbols, emoji) is drawn
// by the fonts the app already relied on.
public enum UiFontRole
{
    // Settings pages, dialogs and window defaults (UI.Text, UI.Line, App.xaml's window style).
    Text,
    // The panel, the collapsed row, the notices and the small windows: Latin text and digits in Segoe UI Variable, Chinese in YaHei.
    Panel,
    // Large figures on the panel (the usage numbers, a notice's initial).
    Display,
    // The tray menu.
    Menu,
    // Small fixed labels set in plain Segoe UI (the off-peak bar's hours, the music seek bubble).
    Label
}

// The one place that knows which font families BrimDeck draws text in. The app side (Fonts) turns these chains into WPF
// families and publishes them as resources; nothing else names a font. The default chains are exactly the strings that used to
// be written at every use, so with no font chosen the text is drawn as it always was.
public static class UiFonts
{
    public const int MaxNameLength = 64;

    public const string TextChain = "Microsoft YaHei";
    public const string PanelChain = "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI";
    public const string DisplayChain = "Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI";
    public const string MenuChain = "Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI";
    public const string LabelChain = "Segoe UI";

    // Symbols are drawn by the icon fonts and never by the user's font: Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets
    // where it is missing, and MDL2 alone where a glyph only exists there.
    public const string IconChain = "Segoe Fluent Icons, Segoe MDL2 Assets";
    public const string IconMdl2Chain = "Segoe MDL2 Assets";
    // Figures and code, drawn in a fixed-width face whatever the user's font is.
    public const string MonoChain = "Cascadia Mono, Consolas";

    public static string DefaultChain(UiFontRole role) => role switch
    {
        UiFontRole.Text => TextChain,
        UiFontRole.Display => DisplayChain,
        UiFontRole.Menu => MenuChain,
        UiFontRole.Label => LabelChain,
        _ => PanelChain
    };

    // A family name as saved: trimmed, runs of blanks made one, empty for "the app's own fonts". A name that could not be one
    // family (a list, a path, a pack URI, a control character, or longer than any real family) is empty too, so that a hand-edited
    // settings file can neither pass extra families into the chain nor reach a font by its location.
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var parts = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var clean = string.Join(' ', parts);
        if (clean.Length > MaxNameLength) return "";
        foreach (var c in clean)
            if (char.IsControl(c) || c is ',' or ';' or '"' or '\'' or '#' or '/' or '\\' or ':' or '<' or '>' or '|' or '*' or '?') return "";
        return clean;
    }

    // Families made of symbols and icons, not letters: text drawn in one would be boxes and pictures, so the picker does not list them.
    public static bool IsSymbolFamily(string? name)
    {
        var clean = Normalize(name);
        return clean.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("Webdings", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("Marlett", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("Segoe MDL2", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("HoloLens MDL2", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Symbol", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("Bookshelf Symbol", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("MT Extra", StringComparison.OrdinalIgnoreCase);
    }

    // The chain a role is drawn in: the default one with no font chosen, otherwise the chosen family followed by the default
    // chain (without a repeat of the chosen family if the chain already names it).
    public static string Chain(UiFontRole role, string? custom)
    {
        var name = Normalize(custom);
        var fallback = DefaultChain(role);
        if (name.Length == 0) return fallback;
        var rest = fallback.Split(", ").Where(family => !string.Equals(family, name, StringComparison.OrdinalIgnoreCase));
        return string.Join(", ", rest.Prepend(name));
    }
}

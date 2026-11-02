namespace BrimDeck.Core;

// Resize and Move are each one setting for four registrations: their modifiers with each arrow key (see Arrows). Page1 to Page3
// open the shown pages in the order of the page switch.
public enum HotkeyKind { Hide, Expand, Resize, Move, ClickThrough, OpenSettings, Page1, Page2, Page3, Refresh }

// Unset: no combination saved. Active: registered. InUse: another program holds the combination. Invalid: the text is not
// an allowed combination. Duplicate: a combination another hotkey has, which keeps it (see Plan and PlanArrows).
public enum HotkeyStatus { Unset, Active, InUse, Invalid, Duplicate }

[Flags]
public enum HotkeyModifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

// The saved text of a global hotkey: modifiers in the fixed order Ctrl+Alt+Shift+Win, then the main key's name (a WPF Key
// name such as "K", "D1" or "F9"), joined by "+". Only F1 to F24 may stand without a modifier. Left and right modifiers are
// not told apart. Whether the key name names a real key is decided by the caller (HotkeyGesture in the app).
public static class HotkeyText
{
    private static readonly (HotkeyModifiers Flag, string Name)[] Order =
        [(HotkeyModifiers.Ctrl, "Ctrl"), (HotkeyModifiers.Alt, "Alt"), (HotkeyModifiers.Shift, "Shift"), (HotkeyModifiers.Win, "Win")];

    // Accepts the modifiers in any order and letter case, and the aliases Control and Windows; rejects repeated modifiers,
    // a missing or repeated main key and spaces inside a part. An empty text is not a hotkey (see IsUnset).
    public static bool TryParse(string? text, out HotkeyModifiers modifiers, out string key)
    {
        modifiers = HotkeyModifiers.None; key = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 0 || part.Any(char.IsWhiteSpace)) return false;
            var flag = Modifier(part);
            if (i < parts.Length - 1)
            {
                if (flag == HotkeyModifiers.None || modifiers.HasFlag(flag)) return false;
                modifiers |= flag;
            }
            else
            {
                if (flag != HotkeyModifiers.None) return false;
                key = part;
            }
        }
        return IsAllowed(modifiers, key);
    }

    public static string Format(HotkeyModifiers modifiers, string key)
        => string.Join("+", Order.Where(o => modifiers.HasFlag(o.Flag)).Select(o => o.Name).Append(key));

    // The canonical form of a saved text, or "" when it is empty or not an allowed combination.
    public static string Normalize(string? text) => TryParse(text, out var modifiers, out var key) ? Format(modifiers, key) : "";

    public static bool IsUnset(string? text) => string.IsNullOrWhiteSpace(text);

    public static bool IsAllowed(HotkeyModifiers modifiers, string key)
        => key.Length > 0 && (modifiers & ~(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win)) == 0
           && (modifiers != HotkeyModifiers.None || IsFunctionKey(key));

    // F1 to F24, which may be used alone.
    public static bool IsFunctionKey(string key)
        => key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.None, null, out int n)
           && n is >= 1 and <= 24 && key[1] != '0';

    // Whether two saved texts name the same combination, whatever their spelling. Two unset or invalid texts are not the same.
    public static bool Same(string? first, string? second)
        => Normalize(first) is { Length: > 0 } a && string.Equals(a, Normalize(second), StringComparison.OrdinalIgnoreCase);

    // Whether a hotkey is saved as modifiers with the arrow keys (see Arrows).
    public static bool IsArrows(HotkeyKind kind) => kind is HotkeyKind.Resize or HotkeyKind.Move;

    // The order in which the hotkeys claim their combinations: the single combinations in the order of HotkeyKind, then the arrow
    // shortcuts. A hotkey that shares a combination with an earlier one that is valid yields to it (Duplicate).
    public static IReadOnlyList<HotkeyKind> Priority { get; } = [.. Enum.GetValues<HotkeyKind>().OrderBy(IsArrows)];

    // What every hotkey should do before registration: Unset, Invalid, Duplicate, or null to register its canonical text (for an
    // arrow shortcut, its modifiers with Arrows). saved gives each hotkey's saved text; isKey checks a main key's name.
    public static Dictionary<HotkeyKind, (HotkeyStatus? Status, string Text)> Plan(Func<HotkeyKind, string?> saved, Func<string, bool> isKey)
    {
        var plans = new Dictionary<HotkeyKind, (HotkeyStatus? Status, string Text)>();
        var claimed = new List<string>();
        foreach (var kind in Priority)
        {
            var plan = PlanOne(kind, saved(kind), isKey);
            if (plan.Status is null && claimed.Any(other => Overlap(plan.Text, other))) plan = (HotkeyStatus.Duplicate, plan.Text);
            else if (plan.Status is null) claimed.Add(plan.Text);
            plans[kind] = plan;
        }
        return plans;
    }

    private static (HotkeyStatus? Status, string Text) PlanOne(HotkeyKind kind, string? text, Func<string, bool> isKey)
    {
        if (IsUnset(text)) return (HotkeyStatus.Unset, "");
        if (IsArrows(kind)) return TryParseArrows(text, out var arrowModifiers) ? (null, Format(arrowModifiers, Arrows)) : (HotkeyStatus.Invalid, "");
        // "Arrows" names no key: only an arrow shortcut stands for the arrow keys.
        if (!TryParse(text, out var modifiers, out var key) || !isKey(key) || string.Equals(key, Arrows, StringComparison.OrdinalIgnoreCase)) return (HotkeyStatus.Invalid, "");
        return (null, Format(modifiers, key));
    }

    // An arrow shortcut is saved as its modifiers and "Arrows" ("Ctrl+Alt+Arrows"): the four arrow keys with those modifiers,
    // in the order Left, Right, Up, Down. Which modifiers are allowed is ArrowModifiersAllowed's.
    public const string Arrows = "Arrows";
    public static readonly IReadOnlyList<string> ArrowKeys = ["Left", "Right", "Up", "Down"];

    // The modifiers must include Alt and one more: the arrow keys with Shift, Ctrl or both select and move through text in every
    // program, and with Win they arrange windows, so taking those over would break them everywhere.
    public static bool ArrowModifiersAllowed(HotkeyModifiers modifiers)
        => modifiers.HasFlag(HotkeyModifiers.Alt) && modifiers != HotkeyModifiers.Alt;

    public static bool TryParseArrows(string? text, out HotkeyModifiers modifiers)
        => TryParse(text, out modifiers, out var key) && string.Equals(key, Arrows, StringComparison.OrdinalIgnoreCase) && ArrowModifiersAllowed(modifiers);

    // The settings show an arrow shortcut as "Alt + [box] + arrow keys": Alt and the arrows are fixed, and the box records
    // only the other modifiers. ArrowExtras is what the box shows ("Ctrl", "Ctrl+Shift"), or "" when the text is not an arrow
    // shortcut. FromArrowExtras is the text to save for the recorded modifiers (Alt among them changes nothing), or "" when no
    // other modifier was recorded.
    public static string ArrowExtras(string? text)
        => TryParseArrows(text, out var modifiers) ? FormatModifiers(modifiers & ~HotkeyModifiers.Alt) : "";

    public static string FromArrowExtras(HotkeyModifiers extras)
    {
        var modifiers = (extras & (HotkeyModifiers.Ctrl | HotkeyModifiers.Shift | HotkeyModifiers.Win)) | HotkeyModifiers.Alt;
        return ArrowModifiersAllowed(modifiers) ? Format(modifiers, Arrows) : "";
    }

    // Modifiers alone, in the fixed order ("Ctrl+Shift"), or "" for none.
    public static string FormatModifiers(HotkeyModifiers modifiers)
        => string.Join("+", Order.Where(o => modifiers.HasFlag(o.Flag)).Select(o => o.Name));

    // The combinations a saved text stands for: the four arrow keys for an arrow shortcut, otherwise the text itself.
    public static string[] Combinations(string? text)
        => TryParseArrows(text, out var modifiers) ? [.. ArrowKeys.Select(key => Format(modifiers, key))] : Normalize(text) is { Length: > 0 } one ? [one] : [];

    // Whether two saved texts share a combination (an arrow shortcut and Ctrl+Alt+Left do).
    public static bool Overlap(string? first, string? second)
    {
        var others = Combinations(second);
        return Combinations(first).Any(a => others.Contains(a, StringComparer.OrdinalIgnoreCase));
    }

    private static HotkeyModifiers Modifier(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Ctrl,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None
    };
}

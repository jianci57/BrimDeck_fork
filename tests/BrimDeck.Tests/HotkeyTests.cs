using BrimDeck.Core;

internal static class HotkeyTests
{
    // What one hotkey does when the others are saved as given (the rest unset).
    internal static (HotkeyStatus? Status, string Text) Plan(HotkeyKind kind, Func<string, bool> isKey, params (HotkeyKind Kind, string Text)[] saved)
        => HotkeyText.Plan(other => saved.FirstOrDefault(entry => entry.Kind == other).Text ?? "", isKey)[kind];

    internal static void Run(Action<string, bool> check)
    {
        static bool Key(string name) => name.Length > 0 && !name.All(char.IsDigit);
        check("Hotkey text keeps the fixed modifier order",
            HotkeyText.Normalize("Ctrl+Alt+K") == "Ctrl+Alt+K" && HotkeyText.Normalize("shift+control+D1") == "Ctrl+Shift+D1"
            && HotkeyText.Normalize("Win+Shift+Alt+Ctrl+Space") == "Ctrl+Alt+Shift+Win+Space" && HotkeyText.Normalize(" Ctrl + Alt + K ") == "Ctrl+Alt+K"
            && HotkeyText.Format(HotkeyModifiers.Win | HotkeyModifiers.Ctrl, "F5") == "Ctrl+Win+F5");
        check("Only F1 to F24 stand without a modifier",
            HotkeyText.Normalize("F9") == "F9" && HotkeyText.Normalize("F24") == "F24" && HotkeyText.Normalize("F1") == "F1"
            && HotkeyText.Normalize("F25") == "" && HotkeyText.Normalize("F0") == "" && HotkeyText.Normalize("F01") == "" && HotkeyText.Normalize("K") == ""
            && HotkeyText.Normalize("Escape") == "" && HotkeyText.Normalize("Shift+K") == "Shift+K");
        check("Malformed hotkey texts are rejected",
            HotkeyText.Normalize("Ctrl+Ctrl+K") == "" && HotkeyText.Normalize("Ctrl+") == "" && HotkeyText.Normalize("Ctrl+Alt") == ""
            && HotkeyText.Normalize("K+Ctrl") == "" && HotkeyText.Normalize("Ctrl+K+L") == "" && HotkeyText.Normalize("Ctrl+Page Up") == ""
            && HotkeyText.Normalize("Hyper+K") == "" && HotkeyText.Normalize(null) == "");
        check("An empty hotkey is unset, not invalid",
            HotkeyText.IsUnset("") && HotkeyText.IsUnset("  ") && HotkeyText.IsUnset(null) && !HotkeyText.IsUnset("Ctrl+K")
            && Plan(HotkeyKind.Hide, Key, (HotkeyKind.Hide, ""), (HotkeyKind.Expand, "")).Status == HotkeyStatus.Unset
            && Plan(HotkeyKind.Hide, Key, (HotkeyKind.Hide, "Ctrl+"), (HotkeyKind.Expand, "")).Status == HotkeyStatus.Invalid
            && Plan(HotkeyKind.Hide, Key, (HotkeyKind.Hide, "Ctrl+5"), (HotkeyKind.Expand, "")).Status == HotkeyStatus.Invalid);
        check("The same combination in any spelling is a duplicate; the expand hotkey yields",
            HotkeyText.Same("Ctrl+Alt+K", "alt+ctrl+k") && !HotkeyText.Same("", "") && !HotkeyText.Same("Ctrl+K", "Ctrl+Shift+K")
            && Plan(HotkeyKind.Hide, Key, (HotkeyKind.Hide, "Ctrl+Alt+K"), (HotkeyKind.Expand, "alt+ctrl+K")) == (null, "Ctrl+Alt+K")
            && Plan(HotkeyKind.Expand, Key, (HotkeyKind.Hide, "Ctrl+Alt+K"), (HotkeyKind.Expand, "alt+ctrl+K")) == (HotkeyStatus.Duplicate, "Ctrl+Alt+K")
            && Plan(HotkeyKind.Expand, Key, (HotkeyKind.Hide, "Ctrl+Alt+J"), (HotkeyKind.Expand, "Ctrl+Alt+K")) == (null, "Ctrl+Alt+K")
            && Plan(HotkeyKind.Expand, Key, (HotkeyKind.Hide, "Ctrl+"), (HotkeyKind.Expand, "Ctrl+Alt+K")) == (null, "Ctrl+Alt+K"));
        check("Every hotkey yields to an earlier one that is valid; the arrow shortcuts come after the single combinations",
            Plan(HotkeyKind.Page2, Key, (HotkeyKind.Page1, "Ctrl+Alt+P"), (HotkeyKind.Page2, "alt+ctrl+p")) == (HotkeyStatus.Duplicate, "Ctrl+Alt+p")
            && Plan(HotkeyKind.Page1, Key, (HotkeyKind.Page1, "Ctrl+Alt+P"), (HotkeyKind.Page2, "alt+ctrl+p")) == (null, "Ctrl+Alt+P")
            && Plan(HotkeyKind.Refresh, Key, (HotkeyKind.OpenSettings, "Ctrl+Alt+5"), (HotkeyKind.Refresh, "Ctrl+Alt+R")) == (null, "Ctrl+Alt+R")
            && Plan(HotkeyKind.Move, Key, (HotkeyKind.Resize, "Ctrl+Alt+Arrows"), (HotkeyKind.Move, "Ctrl+Alt+Arrows")) == (HotkeyStatus.Duplicate, "Ctrl+Alt+Arrows")
            && Plan(HotkeyKind.Move, Key, (HotkeyKind.Resize, "Ctrl+Alt+Arrows"), (HotkeyKind.Move, "Alt+Shift+Arrows")) == (null, "Alt+Shift+Arrows")
            && Plan(HotkeyKind.Resize, Key, (HotkeyKind.Refresh, "Ctrl+Alt+Right"), (HotkeyKind.Resize, "Ctrl+Alt+Arrows")) == (HotkeyStatus.Duplicate, "Ctrl+Alt+Arrows")
            && Plan(HotkeyKind.ClickThrough, Key, (HotkeyKind.ClickThrough, "Ctrl+Alt+Arrows")).Status == HotkeyStatus.Invalid
            && HotkeyText.IsArrows(HotkeyKind.Move) && HotkeyText.IsArrows(HotkeyKind.Resize) && !HotkeyText.IsArrows(HotkeyKind.Page1));
        var all = new DeckSettings();
        foreach (var kind in Enum.GetValues<HotkeyKind>()) all.SetHotkey(kind, kind.ToString());
        check("Every hotkey has its own setting, unset by default, and a missing one reads as unset",
            Enum.GetValues<HotkeyKind>().All(kind => all.Hotkey(kind) == kind.ToString() && new DeckSettings().Hotkey(kind) == "")
            && Normalized(new DeckSettings { Page10Hotkey = null!, MoveHotkey = null! }).Page10Hotkey == ""
            && Normalized(new DeckSettings { MoveHotkey = null! }).MoveHotkey == "");
    }

    private static DeckSettings Normalized(DeckSettings settings) { settings.Normalize(); return settings; }
}

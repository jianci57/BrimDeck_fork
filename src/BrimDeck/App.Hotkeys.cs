using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The global hotkeys (HotkeyKind) and the hidden state the hide hotkey toggles. All are unset by default and are taken from the
// settings (DeckSettings.Hotkey). The registrations live on a message-only window of the app (GlobalHotkeys), so panels closed
// and opened again for display changes do not lose them.
public partial class App
{
    private GlobalHotkeys? _hotkeys;
    private readonly Dictionary<HotkeyKind, HotkeyStatus> _hotkeyStatus = Enum.GetValues<HotkeyKind>().ToDictionary(kind => kind, _ => HotkeyStatus.Unset);
    private Dictionary<HotkeyKind, string> _hotkeyTexts = [];
    private int _hotkeySuspensions;

    // Raised on the UI thread after the hotkeys are registered again, with any status change.
    public event EventHandler? HotkeysChanged;
    // Raised when the panels are hidden or shown again by the hide hotkey or the tray menu.
    public event EventHandler? UserHiddenChanged;

    // Every panel is hidden: nothing is drawn, hovering does nothing and no click is caught. Not saved; a restart shows them.
    public bool UserHidden { get; private set; }

    // The status from the last registration. While SuspendHotkeys is in effect nothing is registered and this keeps the
    // status the combinations will have again.
    public HotkeyStatus StatusOf(HotkeyKind kind) => _hotkeyStatus[kind];

    // Creates the registrar and registers the saved hotkeys. Call once the main panel exists.
    internal void InitializeHotkeys()
    {
        if (_hotkeys is not null) return;
        _hotkeys = new GlobalHotkeys();
        _hotkeys.Pressed += HotkeyPressed;
        ApplyHotkeys();
    }

    // Registers the hotkeys saved in the settings. Unchanged texts that are already active are left alone; any other one is
    // tried again, so a combination another program has since released becomes active on the next save.
    public void ApplyHotkeys()
    {
        var texts = Enum.GetValues<HotkeyKind>().ToDictionary(kind => kind, Settings.Hotkey);
        bool unchanged = texts.All(pair => _hotkeyTexts.TryGetValue(pair.Key, out var text) && text == pair.Value)
            && _hotkeyStatus.Values.All(status => status is HotkeyStatus.Active or HotkeyStatus.Unset);
        _hotkeyTexts = texts;
        if (unchanged && _hotkeySuspensions == 0 && _hotkeys is not null) return;
        RegisterHotkeys();
    }

    // Releases the hotkeys, for example while the settings window records a new combination. Calls nest; each must be
    // matched by ResumeHotkeys, and the hotkeys come back with the last one.
    public void SuspendHotkeys()
    {
        if (_hotkeySuspensions++ > 0 || _hotkeys is null) return;
        foreach (var kind in Enum.GetValues<HotkeyKind>()) _hotkeys.Unregister(kind);
    }

    public void ResumeHotkeys()
    {
        if (_hotkeySuspensions == 0) return;
        if (--_hotkeySuspensions == 0) RegisterHotkeys();
    }

    internal void DisposeHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.Pressed -= HotkeyPressed; _hotkeys.Dispose(); _hotkeys = null;
    }

    // Hides every panel, or shows them again. An open panel closes first.
    public void ToggleHidden() => SetUserHidden(!UserHidden);
    public void SetUserHidden(bool hidden)
    {
        if (UserHidden == hidden) return;
        UserHidden = hidden;
        foreach (var deck in Decks.ToList()) deck.UserHiddenChanged();
        UserHiddenChanged?.Invoke(this, EventArgs.Empty);
    }

    // Turns mouse pass-through on or off (DeckSettings.ClickThrough), from its hotkey or the tray menu.
    public void ToggleClickThrough()
    {
        var settings = Settings.Copy();
        settings.ClickThrough = !settings.ClickThrough;
        UpdateSettings(settings);
        _settingsWindow?.ClickThroughChanged();
    }

    // Opens the settings window, or closes it when it is open and not minimized.
    private void ToggleSettingsFromHotkey()
    {
        if (_settingsWindow is { WindowState: not System.Windows.WindowState.Minimized } open) open.Close();
        else OpenSettings();
    }

    // The panel the hotkeys act on when none is open: the one on the monitor under the pointer, or the main panel.
    private MainWindow DeckUnderPointer()
    {
        var monitor = GlobalHotkeys.CursorMonitor();
        return Decks.FirstOrDefault(deck => deck.HotkeyMonitor == monitor) ?? Deck;
    }

    // Opens the panel on the monitor under the pointer, or closes the one that is open.
    public void ToggleExpandFromHotkey()
    {
        if (Decks.FirstOrDefault(deck => deck.IsOpenForHotkey) is { } open) { open.CollapseFromHotkey(); return; }
        DeckUnderPointer().OpenFromHotkey();
    }

    // The page hotkeys: the shown page at this place in the page switch. An open panel turns to it, or closes when it already
    // shows it; otherwise the panel under the pointer opens on it.
    public void OpenPageFromHotkey(int place)
    {
        var pages = DeckPages.Visible(Settings);
        if (place < 0 || place >= pages.Count) return;
        var page = pages[place];
        if (Decks.FirstOrDefault(deck => deck.IsOpenForHotkey) is { } open)
        {
            if (open.CurrentPage == page) open.CollapseFromHotkey(); else open.SelectPage(page);
            return;
        }
        DeckUnderPointer().OpenFromHotkey(page);
    }

    // Resizes the open panel, or the collapsed surface on the monitor under the pointer (KeyboardResize).
    public void ResizeFromHotkey(int arrow)
    {
        var target = Decks.FirstOrDefault(deck => deck.IsOpenForHotkey) ?? DeckUnderPointer();
        target.ResizeFromKeyboard(KeyboardResize.Of(arrow));
    }

    private void RegisterHotkeys()
    {
        // Everything is released first, so a combination one hotkey gives up is free for another this time round.
        foreach (var kind in Enum.GetValues<HotkeyKind>()) _hotkeys?.Unregister(kind);
        var plans = HotkeyText.Plan(kind => _hotkeyTexts.GetValueOrDefault(kind), HotkeyGesture.IsKeyName);
        foreach (var kind in HotkeyText.Priority)
        {
            var (status, text) = plans[kind];
            _hotkeyStatus[kind] = status ?? (HotkeyText.IsArrows(kind) ? RegisterArrows(kind, text) : Register(kind, text, 0, repeat: false));
        }
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    private HotkeyStatus Register(HotkeyKind kind, string text, int slot, bool repeat)
    {
        HotkeyGesture.TryParse(text, out var modifiers, out var key);
        return !HotkeyGesture.ToNative(modifiers, key, out uint fs, out uint vk, repeat) ? HotkeyStatus.Invalid
            : _hotkeys is null || _hotkeySuspensions > 0 ? HotkeyStatus.Active : _hotkeys.Register(kind, fs, vk, slot);
    }

    // An arrow shortcut's four combinations, each repeating while its key is held. It is active only when all four are; one
    // another program holds leaves none registered, so the arrows never work in some directions only.
    private HotkeyStatus RegisterArrows(HotkeyKind kind, string text)
    {
        if (!HotkeyText.TryParseArrows(text, out var modifiers)) return HotkeyStatus.Invalid;
        var status = HotkeyStatus.Active;
        for (int arrow = 0; arrow < HotkeyText.ArrowKeys.Count && status == HotkeyStatus.Active; arrow++)
            status = Register(kind, HotkeyText.Format(modifiers, HotkeyText.ArrowKeys[arrow]), arrow, repeat: true);
        if (status != HotkeyStatus.Active) _hotkeys?.Unregister(kind);
        return status;
    }

    private void HotkeyPressed(HotkeyKind kind, int slot)
    {
        if (Exiting) return;
        switch (kind)
        {
            case HotkeyKind.Hide: ToggleHidden(); break;
            case HotkeyKind.Expand: ToggleExpandFromHotkey(); break;
            case HotkeyKind.Resize: ResizeFromHotkey(slot); break;
            case HotkeyKind.Move: DeckUnderPointer().MoveFromKeyboard(slot); break;
            case HotkeyKind.ClickThrough: ToggleClickThrough(); break;
            case HotkeyKind.OpenSettings: ToggleSettingsFromHotkey(); break;
            case HotkeyKind.Page1: OpenPageFromHotkey(0); break;
            case HotkeyKind.Page2: OpenPageFromHotkey(1); break;
            case HotkeyKind.Page3: OpenPageFromHotkey(2); break;
            case HotkeyKind.Refresh: Deck.SyncNow(); break;
        }
    }
}

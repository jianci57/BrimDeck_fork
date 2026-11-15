using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// How a panel follows the global hotkeys (see App.Hotkeys.cs).
// Opened by the expand hotkey, the panel stays open until the pointer has been inside it once; after that the usual leave
// rules close it (under mouse pass-through, a panel that ignores the pointer stays open until a hotkey closes it). While
// the panels are hidden, the expand hotkey still opens this panel; once it closes, it hides again.
// Opened by a hotkey, the panel also takes the keyboard (Tab, arrows, Enter and Space work its pages) and gives it back to the
// window that had it when it closes (PanelKeyboard); under mouse pass-through only with ClickThroughKeyboard on. Opened by the
// pointer, it leaves the keyboard where it is.
public partial class MainWindow
{
    // Shown for the expand hotkey while the panels are hidden.
    private bool _hotkeyOpened;
    // Opened by the expand hotkey and not entered by the pointer yet.
    private bool _hotkeyPinned;
    private PanelKeyboard? _panelKeyboard;
    private PanelKeyboard PanelKeys => _panelKeyboard ??= new PanelKeyboard(this, () => Settings.WindowCapture);

    // Hidden by the hide hotkey; part of IsSuppressed, so it combines with the full-screen rule the same way.
    // A profile rule that hides every panel (App.RuleHidesAll) hides the main one, which then has no other panel to hand over to.
    internal bool HiddenByUser => (_app.UserHidden || _app.RuleHidesAll && _lead is null) && !_hotkeyOpened;
    internal IntPtr HotkeyMonitor => MonitorHandle;
    internal bool IsOpenForHotkey => (_noticeDeferredExpand ?? _expanded) && !_preview;
    internal bool IsHotkeyPinned => _hotkeyPinned;

    // page: the page to open on (a page hotkey), in place of the page chosen for opening (ApplyDefaultMusicPage).
    internal void OpenFromHotkey(DeckPage? page = null)
    {
        if (_expanded && _noticeDeferredExpand != false) return;
        _hotkeyPage = page;
        try { Open(); } finally { _hotkeyPage = null; }
    }

    private DeckPage? _hotkeyPage;

    private void Open()
    {
        // A collapsed preview of the settings ends as hovering would end it; a preview of the open panel is left alone.
        if (_preview) { if (_previewExpanded) return; _app.EndPreviewFromDeck(); }
        // The full-screen rule hides the panel; the hotkey does not override it.
        if (Settings.Behavior(_context, _host.App.Name, _host.App.Path, _host.BackgroundKind) == WindowBehavior.Hide) return;
        if (_app.UserHidden && !_hotkeyOpened) { _hotkeyOpened = true; ApplySettings(); }
        _hover.Stop(); _leave.Stop();
        _hotkeyPinned = true;
        SetExpanded(true, keyboard: true);
        if (!_expanded && _noticeDeferredExpand != true) { _hotkeyPinned = false; if (_hotkeyOpened) { _hotkeyOpened = false; ApplySettings(); } }
        else if (_expanded && PanelTakesKeyboard) TakeKeyboard();
    }

    // The panel becomes a window that can be active, takes the foreground and puts the focus on the button of the page shown.
    private void TakeKeyboard()
    {
        if (!PanelKeys.Take()) return;
        // A paste from the panel goes to the window that had the keyboard before it, not to one typed in earlier or to none.
        _app.Shelf.TypingTarget = PanelKeys.Previous;
        Dispatcher.BeginInvoke(() => { if (PanelKeys.Holding && _expanded) FocusPanelStart(); }, DispatcherPriority.Input);
    }

    private void FocusPanelStart()
    {
        Keyboard.Focus(PageButton(CurrentPage));
    }

    // The panel closed or was hidden: it cannot be active again until the next hotkey, and the window that had the keyboard gets
    // it back, unless the user has moved on to another one meanwhile (giveBack false: the user's own move, from Deactivated).
    private void ReleaseKeyboard(bool giveBack) => PanelKeys.Release(giveBack);

    internal void CollapseFromHotkey() => SetExpanded(false, keyboard: true);

    // For TrackPointer while open: the pointer inside ends the pin. True while the pin holds the panel open.
    internal bool HotkeyPinned(bool inside)
    {
        if (inside) _hotkeyPinned = false;
        return _hotkeyPinned;
    }

    // For PointerChanged: any entry ends the pin.
    internal void ReleaseHotkeyPin() => _hotkeyPinned = false;

    // For SetExpanded, on the change from open to closed.
    internal void HotkeyPanelCollapsed()
    {
        ReleaseKeyboard(giveBack: true);
        _hotkeyPinned = false;
        if (!_hotkeyOpened) return;
        _hotkeyOpened = false;
        if (!_app.UserHidden) return;
        // The panels are hidden: closing goes straight back to hidden, as hiding always does (ApplySettings), instead of
        // playing the close into the collapsed island and showing that for a moment first. Not from inside SetExpanded,
        // which ApplySettings calls, but before the next frame is drawn.
        Dispatcher.BeginInvoke(DispatcherPriority.Send, Rehide);
    }

    // Called by App for every panel when the hidden state changes.
    internal void UserHiddenChanged()
    {
        _hotkeyOpened = false; _hotkeyPinned = false;
        ReleaseKeyboard(giveBack: true);
        if (_app.UserHidden || _app.RuleHidesAll && _lead is null)
        {
            _hover.Stop(); _leave.Stop(); _pointerInside = false; _cursorInCompact = false;
            if (_alert is not null) ClearAlert();
        }
        // Hiding and showing again are immediate (MainWindow.ApplySettings).
        ApplySettings(true);
    }

    // A panel retired meanwhile (its monitor was removed) is closed and left alone.
    private void Rehide()
    {
        if (IsLoaded && HiddenByUser && !_expanded) ApplySettings();
    }
}

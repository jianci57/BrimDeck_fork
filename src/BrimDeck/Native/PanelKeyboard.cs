using System.Windows;
using System.Windows.Interop;

namespace BrimDeck.Native;

// The keyboard of a panel opened by a hotkey. The panel never activates otherwise (WS_EX_NOACTIVATE, WindowsHost.SetCapturable): a
// panel the pointer opens leaves the keyboard where it is. Opened by a hotkey it is a window that can be active for as long as it
// is open: it takes the foreground, so Tab, the arrows, Enter and Space work its pages, and gives the keyboard back to the window
// that had it when it closes.
internal sealed class PanelKeyboard
{
    private readonly Window _window;
    private readonly Func<bool> _capturable;
    private IntPtr _previous;

    // capturable: whether the user lets recording apps capture the panel (DeckSettings.WindowCapture). That setting already leaves
    // the window without WS_EX_NOACTIVATE, and it must stay so when the keyboard is given up.
    public PanelKeyboard(Window window, Func<bool> capturable) { _window = window; _capturable = capturable; }

    // True from Take until the keyboard is given up, by the panel closing or by the user going to another window.
    public bool Holding { get; private set; }

    // The window that had the keyboard before the panel took it (zero when there was none): where a paste from the panel goes.
    // A window of this process is no target; Paste leaves those out.
    public IntPtr Previous => Holding ? _previous : IntPtr.Zero;

    public bool Take()
    {
        var own = new WindowInteropHelper(_window).Handle;
        if (own == IntPtr.Zero) return false;
        var foreground = PasteInput.Foreground;
        if (!Holding && foreground != own) _previous = foreground;
        Holding = true;
        WindowsHost.KeyboardPanel = own;
        WindowsHost.SetCapturable(_window, true);
        _window.Activate();
        return true;
    }

    // giveBack: the panel closed. False when the user went to another window, which then keeps the keyboard.
    public void Release(bool giveBack)
    {
        if (!Holding) return;
        Holding = false;
        var previous = _previous; _previous = IntPtr.Zero;
        var own = new WindowInteropHelper(_window).Handle;
        if (own == IntPtr.Zero) return;
        WindowsHost.SetCapturable(_window, _capturable());
        if (giveBack && ShouldReturn(own, previous, PasteInput.Foreground)) PasteInput.Activate(previous);
        // Only once the foreground has moved on: until then the panel being in front must not read as a change of context.
        if (WindowsHost.KeyboardPanel == own) WindowsHost.KeyboardPanel = IntPtr.Zero;
    }

    // The window that had the keyboard before gets it back if it still exists, belongs to another process, and the panel still has
    // the foreground (a user who has moved on to a third window is left there).
    internal static bool ShouldReturn(IntPtr own, IntPtr previous, IntPtr foreground)
        => previous != own && foreground == own && PasteInput.IsOther(previous);
}

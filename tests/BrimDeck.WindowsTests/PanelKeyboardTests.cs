using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BrimDeck.Native;

// What a panel opened by a hotkey does to its window: it can be active while it holds the keyboard, and goes back to the style it
// had (never activated, or not when the user lets recording apps capture it) when the keyboard is given up. Real windows and real
// window styles; the windows are off the screen and unactivated, and no keyboard input is sent.
internal static class PanelKeyboardTests
{
    private const long NoActivate = 0x08000000;

    public static void Run(Action<string, bool> check)
    {
        foreach (bool capturable in new[] { false, true })
        {
            var window = new Window { Width = 200, Height = 100, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
            try
            {
                window.Show();
                var handle = new WindowInteropHelper(window).Handle;
                WindowsHost.ConfigureOverlay(window, capturable);
                var keys = new PanelKeyboard(window, () => capturable);
                string setting = capturable ? "capturable panel" : "default panel";
                check($"{setting}: before the hotkey the window is {(capturable ? "" : "not ")}activatable", NoActivateSet(handle) == !capturable);
                var before = PasteInput.Foreground;
                check($"{setting}: Take reports the keyboard taken", keys.Take() && keys.Holding);
                check($"{setting}: a paste from the panel goes to the window that had the keyboard before it (or nowhere)",
                    keys.Previous == (before == handle ? IntPtr.Zero : before));
                check($"{setting}: holding the keyboard, the window can be active", !NoActivateSet(handle));
                check($"{setting}: holding the keyboard, a foreground panel does not read as a change of application", WindowsHost.KeyboardPanel == handle);
                keys.Release(giveBack: false);
                check($"{setting}: after Release the window is back to its style ({(capturable ? "activatable" : "never activates")})", NoActivateSet(handle) == !capturable);
                check($"{setting}: after Release the keyboard is not held and the panel no longer counts as the user's foreground", !keys.Holding && WindowsHost.KeyboardPanel == IntPtr.Zero);
                check($"{setting}: after Release no paste target is kept", keys.Previous == IntPtr.Zero);
                keys.Release(giveBack: true);
                check($"{setting}: a second Release changes nothing", NoActivateSet(handle) == !capturable);
            }
            finally { window.Close(); }
        }

        var panel = new Window { Width = 200, Height = 100, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
        var neighbour = new Window { Width = 200, Height = 100, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
        try
        {
            panel.Show(); neighbour.Show();
            var own = new WindowInteropHelper(panel).Handle; var sameProcess = new WindowInteropHelper(neighbour).Handle;
            var other = GetShellWindow(); var third = GetDesktopWindow();
            check("The window that had the keyboard gets it back while the panel is still the foreground", PanelKeyboard.ShouldReturn(own, other, own));
            check("A user who went to a third window is left there", !PanelKeyboard.ShouldReturn(own, other, third));
            check("Nothing is given back when no window had the keyboard", !PanelKeyboard.ShouldReturn(own, IntPtr.Zero, own));
            check("The panel is never given the keyboard back to itself", !PanelKeyboard.ShouldReturn(own, own, own));
            check("A window of this process is not the user's other window", !PanelKeyboard.ShouldReturn(own, sameProcess, own));
            check("A window that no longer exists is not given the keyboard", !PanelKeyboard.ShouldReturn(own, new IntPtr(0x7FFF0), own));
        }
        finally { panel.Close(); neighbour.Close(); }
    }

    private static bool NoActivateSet(IntPtr handle) => (GetWindowLongPtr(handle, -20).ToInt64() & NoActivate) != 0;

    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32")] private static extern IntPtr GetShellWindow();
    [DllImport("user32")] private static extern IntPtr GetDesktopWindow();
}

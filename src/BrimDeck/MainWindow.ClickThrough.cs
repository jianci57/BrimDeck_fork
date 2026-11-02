using System.Runtime.InteropServices;
using System.Windows.Interop;
using BrimDeck.Core;

namespace BrimDeck;

// Mouse pass-through (DeckSettings.ClickThrough). The window lets every click through to the window beneath (WS_EX_TRANSPARENT)
// and hovering does not open the panel; only the shortcuts do. Open, the panel takes clicks only with ClickThroughPanel on, and
// then closes by the usual rules once the pointer has been inside. Otherwise it ignores the pointer and stays open until a
// shortcut or Esc closes it. A preview from the settings window behaves as usual.
public partial class MainWindow
{
    private bool _passingClicks;

    internal bool ClickThrough => Settings.ClickThrough && !_preview;
    // The open panel neither takes clicks nor closes when the pointer leaves.
    private bool PanelIgnoresPointer => ClickThrough && _expanded && !Settings.ClickThroughPanel;

    // From TrackPointer: true while the pointer is to be ignored. Whatever hovering had started is dropped, and the checks slow
    // down, as nothing they find is used.
    private bool IgnorePointer()
    {
        if (!(ClickThrough && !_expanded || PanelIgnoresPointer)) return false;
        _hover.Stop(); _leave.Stop();
        _pointerInside = false; _cursorInCompact = false;
        SetHoverEdge(ResizeEdges.None);
        _pointerTrack.Interval = TimeSpan.FromMilliseconds(TrackFar);
        return true;
    }

    // From SetExpanded: the window lets clicks through while pass-through is on, unless the open panel takes them.
    private void ApplyClickThrough()
    {
        bool passing = ClickThrough && !(_expanded && Settings.ClickThroughPanel);
        if (passing == _passingClicks) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        long style = GetWindowLongPtr(handle, ExStyle).ToInt64();
        SetWindowLongPtr(handle, ExStyle, new IntPtr(passing ? style | Transparent : style & ~Transparent));
        _passingClicks = passing;
    }

    private const int ExStyle = -20;
    private const long Transparent = 0x20; // WS_EX_TRANSPARENT
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BrimDeck.Native;

// While Start, search or another shell flyout is open, Windows moves the taskbar out of band 1, where every application
// window lives (topmost ones included), into a higher band of the shell's, and moves it back a second or two after the flyout
// closes. No ordinary window can follow it there, so a collapsed island over the taskbar is hidden behind it all that while.
// An ordinary process can still create a window in band 16, the band of explorer's task switcher, which lies above the
// lifted taskbar. While the taskbar under the island is lifted, such a window ("lens") lies over the part of the island the
// taskbar covers and shows it through a DWM thumbnail of the panel's window: the desktop compositor draws the panel's live
// picture there, at the panel's own frame rate and with its transparency, and the panel draws nothing more. The lens covers
// the island alone, with room for the resize grips beside it while the pointer is near (_reach), and follows the island's
// layout as it changes; the rest of the taskbar stays the taskbar's. (A lens across the whole taskbar did not let clicks
// through its clear parts: a window answering HTTRANSPARENT hands the mouse only to windows of its own thread.) The panel's
// own pop-ups over that part (menus, tooltips) get a lens of their own above it. The lenses exist only while they are
// needed. Both band calls are undocumented; where either is missing or refused, no lens is shown and the island is covered
// as before.
//
// The same lenses keep the panel above other apps' topmost windows when the user asks for it (DeckSettings.ForceTopmost):
// the part they cover is shown above them, and the panel no longer raises itself over them again and again.
//
// The mouse over a lens reaches the window it shows: a clear spot lets the mouse through to what lies beneath (the taskbar);
// moves are handed on so hover states follow (Hover), and presses, releases and wheel turns are raised in the window at the
// same place (Pointer). After a flyout closes, the taskbar can stay lifted for a few seconds while the pointer is over it, and
// the lenses stay with it.
internal sealed class TaskbarMirror : IDisposable
{
    private const uint DesktopBand = 1, SwitcherBand = 16;
    private const string ClassName = "BrimDeckTaskbarMirror";
    // The island in screen physical pixels: whether it reaches into a lifted taskbar decides whether a lens is needed. The
    // reach is the island with room for its resize grips while the pointer is near or a resize runs: the lens shows that much.
    private readonly Func<Rect?> _strip, _reach;
    private readonly Func<bool> _wanted;
    // Puts the panel back above a taskbar that has come down to band 1, before the lenses leave.
    private readonly Action _raise;
    private readonly Func<IntPtr> _panel;
    // The part of an area other topmost windows cover where they reach the island, when the panel is to stay above them.
    private readonly Func<Rect, Rect, Rect?> _topmost;
    // Whether a window of the panel's draws at a place on screen, and the press, release or wheel turn there (window, message,
    // place on screen in physical pixels, wheel turn); true when handled.
    private readonly Func<IntPtr, Point, bool> _hit;
    private readonly Func<IntPtr, uint, Point, int, bool> _pointer;
    // The lenses' places follow the panel's window and the taskbar; their pictures follow on their own.
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };
    // The taskbar is lifted a moment after the flyout takes the foreground, so a foreground change is followed by a few
    // quick checks; the order watch alone looks only every 400 ms.
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private int _followTicks;
    private readonly Lens _panelLens;
    private readonly Dictionary<IntPtr, Lens> _popupLenses = [];
    private bool _shown, _unavailable;
    // False once a band call is found missing or refused.
    public bool Available => !_unavailable;

    public TaskbarMirror(Func<Rect?> strip, Func<Rect?> reach, Func<bool> wanted, Action raise, Func<IntPtr> panel, Func<Rect, Rect, Rect?> topmost,
        Func<IntPtr, Point, bool> hit, Func<IntPtr, uint, Point, int, bool> pointer)
    {
        _strip = strip; _reach = reach; _wanted = wanted; _raise = raise; _panel = panel; _topmost = topmost; _hit = hit; _pointer = pointer;
        _panelLens = new Lens(this);
        _collectPopup = (window, _) => { _popups.Add(window); return true; };
        _frame.Tick += (_, _) => Frame();
        _follow.Tick += (_, _) => { Check(); if (_shown || --_followTicks <= 0) _follow.Stop(); };
    }

    // Called after each check of the panel's order.
    public void Check()
    {
        if (_unavailable) return;
        if (_wanted() && Covered() is { } area) Show(area);
        else if (_shown) Hide();
    }

    // Called after each layout of the panel: a shown lens follows the island as it opens, closes or is resized.
    public void Track()
    {
        if (_shown) Check();
    }

    // Called when another window takes the foreground.
    public void Follow()
    {
        if (_unavailable || _shown) return;
        _followTicks = 15;
        _follow.Start();
    }

    private void Frame()
    {
        if (_wanted() && Covered() is { } area) { Show(area); CheckHover(); return; }
        _raise();
        Hide();
    }

    // The part of the island's reach to show above, in screen physical pixels: where a taskbar that is out of band 1 covers it,
    // when that taskbar reaches the island, and where other topmost windows cover it when the panel is to stay above them
    // (_topmost, DeckSettings.ForceTopmost); null when none.
    private Rect? Covered()
    {
        if (_strip() is not { } strip || _reach() is not { } window) return null;
        Rect? area = _topmost(strip, window);
        try
        {
            foreach (var name in (ReadOnlySpan<string>)["Shell_TrayWnd", "Shell_SecondaryTrayWnd"])
                for (var bar = FindWindowEx(IntPtr.Zero, IntPtr.Zero, name, null); bar != IntPtr.Zero; bar = FindWindowEx(IntPtr.Zero, bar, name, null))
                {
                    if (!IsWindowVisible(bar) || !GetWindowRect(bar, out var r)) continue;
                    var taskbar = ToRect(r);
                    if (!Overlaps(strip, taskbar) || !GetWindowBand(bar, out var band) || band == DesktopBand || band == 0) continue;
                    var part = Rect.Intersect(window, taskbar);
                    if (part.IsEmpty) continue;
                    area = area is { } so ? Rect.Union(so, part) : part;
                }
        }
        catch (EntryPointNotFoundException) { _unavailable = true; return null; }
        return area;
    }
    private static bool Overlaps(Rect a, Rect b) { var c = Rect.Intersect(a, b); return !c.IsEmpty && c.Width >= 1 && c.Height >= 1; }
    private static Rect ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    private void Show(Rect area)
    {
        if (!Place(_panelLens, _panel(), area)) { Hide(); return; }
        ShowPopups(area);
        if (_shown) return;
        _shown = true;
        _frame.Start();
    }

    private void Hide()
    {
        _frame.Stop();
        EndPress();
        CheckHover(end: true);
        Conceal(_panelLens);
        foreach (var lens in _popupLenses.Values) Conceal(lens);
        _popupLenses.Clear();
        _shown = false;
    }

    // The panel's own topmost pop-ups — its menus, tooltips and detail windows — lie above the panel but in band 1, so one that
    // reaches into the covered part is covered too; it gets a lens of its own, above the panel's.
    private void ShowPopups(Rect area)
    {
        var panel = _panel();
        _popups.Clear();
        EnumThreadWindows(GetCurrentThreadId(), _collectPopup, IntPtr.Zero);
        var seen = new HashSet<IntPtr>();
        foreach (var popup in _popups)
        {
            if (popup == panel || Lenses.ContainsKey(popup) || !IsWindowVisible(popup) || (GetWindowLongPtr(popup, -20).ToInt64() & 0x8) == 0) continue;
            if (!GetWindowRect(popup, out var r) || !Overlaps(area, ToRect(r))) continue;
            seen.Add(popup);
            if (!_popupLenses.TryGetValue(popup, out var lens)) _popupLenses[popup] = lens = new Lens(this);
            Place(lens, popup, ToRect(r), above: true);
        }
        foreach (var gone in _popupLenses.Keys.Where(popup => !seen.Contains(popup)).ToList())
        {
            Conceal(_popupLenses[gone]);
            _popupLenses.Remove(gone);
        }
    }

    // A band-16 window showing a part of another window of this app through a DWM thumbnail.
    private sealed class Lens(TaskbarMirror mirror)
    {
        public readonly TaskbarMirror Mirror = mirror;
        public IntPtr Window, Target, Thumbnail;
        public Rect Area = Rect.Empty;
    }
    private static readonly Dictionary<IntPtr, Lens> Lenses = [];

    // Shows the given area of the target window at its own place on screen. above puts the lens over the others.
    private bool Place(Lens lens, IntPtr target, Rect area, bool above = false)
    {
        if (target == IntPtr.Zero || !GetWindowRect(target, out var t)) return false;
        if (lens.Window == IntPtr.Zero && !Create(lens)) return false;
        bool fresh = lens.Target != target || lens.Thumbnail == IntPtr.Zero;
        if (fresh)
        {
            if (lens.Thumbnail != IntPtr.Zero) DwmUnregisterThumbnail(lens.Thumbnail);
            lens.Thumbnail = IntPtr.Zero; lens.Target = target;
            if (DwmRegisterThumbnail(lens.Window, target, out lens.Thumbnail) != 0) { lens.Thumbnail = IntPtr.Zero; return false; }
        }
        int left = (int)Math.Round(area.X), top = (int)Math.Round(area.Y);
        int width = (int)Math.Round(area.Right) - left, height = (int)Math.Round(area.Bottom) - top;
        if (width <= 0 || height <= 0) return false;
        var place = new Rect(left, top, width, height);
        if (!fresh && place == lens.Area) return true;
        lens.Area = place;
        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            Flags = 0x1 | 0x2 | 0x4 | 0x8, // DWM_TNP_RECTDESTINATION | RECTSOURCE | OPACITY | VISIBLE
            Destination = new RECT { Left = 0, Top = 0, Right = width, Bottom = height },
            Source = new RECT { Left = left - t.Left, Top = top - t.Top, Right = left - t.Left + width, Bottom = top - t.Top + height },
            Opacity = 255, Visible = true,
        };
        DwmUpdateThumbnailProperties(lens.Thumbnail, ref properties);
        // SWP_NOACTIVATE | SWP_SHOWWINDOW, and SWP_NOZORDER unless the lens goes on top.
        SetWindowPos(lens.Window, IntPtr.Zero, left, top, width, height, 0x0010 | 0x0040 | (above && fresh ? 0u : 0x0004));
        return true;
    }

    private static void Conceal(Lens lens)
    {
        if (lens.Window == IntPtr.Zero) return;
        ShowWindow(lens.Window, 0); // SW_HIDE
        if (lens.Thumbnail != IntPtr.Zero) DwmUnregisterThumbnail(lens.Thumbnail);
        lens.Thumbnail = IntPtr.Zero; lens.Target = IntPtr.Zero; lens.Area = Rect.Empty;
        // A pop-up's lens is not kept: the pop-up is gone or will be another.
        if (lens != lens.Mirror._panelLens) { Lenses.Remove(lens.Window); DestroyWindow(lens.Window); lens.Window = IntPtr.Zero; }
    }

    private bool Create(Lens lens)
    {
        try
        {
            // WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST; WS_POPUP. Without a surface of its own the
            // lens shows nothing but the thumbnail, so the panel's clear parts stay clear.
            if (Register())
                lens.Window = CreateWindowInBand(0x00200000 | 0x08000000 | 0x80 | 0x08, ClassName, "BrimDeck", 0x80000000, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, GetModuleHandle(IntPtr.Zero), IntPtr.Zero, SwitcherBand);
        }
        catch (EntryPointNotFoundException) { }
        if (lens.Window == IntPtr.Zero) { _unavailable = true; return false; }
        Lenses[lens.Window] = lens;
        return true;
    }

    // One window class for every lens; the procedure finds its lens by window.
    private static readonly WndProc Procedure = LensProcedure;
    private static bool _registered;
    private static bool Register()
    {
        if (_registered) return true;
        var type = new WNDCLASSEX
        {
            Size = Marshal.SizeOf<WNDCLASSEX>(), Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = GetModuleHandle(IntPtr.Zero), Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)), ClassName = ClassName, // IDC_ARROW
        };
        return _registered = RegisterClassEx(ref type) != 0;
    }
    private static IntPtr LensProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Lenses.TryGetValue(window, out var lens) && lens.Target != IntPtr.Zero)
            switch (message)
            {
                // WM_NCHITTEST: where the window shown draws nothing, the mouse goes on to what lies beneath.
                case 0x0084:
                    var at = new Point(unchecked((short)(long)lParam), unchecked((short)((long)lParam >> 16)));
                    return new IntPtr(lens.Mirror._hit(lens.Target, at) ? 1 : -1); // HTCLIENT, HTTRANSPARENT
                case 0x0021: return new IntPtr(3); // WM_MOUSEACTIVATE: MA_NOACTIVATE, the click is delivered without taking focus.
                // WM_SETCURSOR over the window shown: the pointer keeps the shape that window gives it.
                case 0x0020 when ((long)lParam & 0xFFFF) == 1: return new IntPtr(1);
                case 0x0200 when lens.Mirror.Hover(lens, wParam, lParam): return IntPtr.Zero;
                case >= 0x0201 and <= 0x020E when lens.Mirror.Pointer(lens, message, wParam, lParam): return IntPtr.Zero;
            }
        return DefWindowProc(window, message, wParam, lParam);
    }

    // Passes a press, a release or a wheel turn over a lens to the window it shows, with its place on screen in physical pixels;
    // that window raises them as its own mouse events at the same place (MainWindow.MirrorPointer). Pressed messages handed to
    // the window itself were seen to raise no press in it. From a press to its release the lens holds the capture, so the drag
    // and the release come back to it wherever the pointer goes.
    private Lens? _pressed;
    private bool Pointer(Lens lens, uint message, IntPtr wParam, IntPtr lParam)
    {
        var point = new POINT { X = unchecked((short)(long)lParam), Y = unchecked((short)((long)lParam >> 16)) };
        // The wheel messages carry screen coordinates, the others the window's own.
        bool wheel = message is 0x020A or 0x020E;
        if (!wheel) ClientToScreen(lens.Window, ref point);
        int delta = wheel ? unchecked((short)((long)wParam >> 16)) : 0;
        var target = _pressed?.Target ?? lens.Target;
        bool handled = _pointer(target, message, new Point(point.X, point.Y), delta);
        // After the window has acted on it: a control that takes the capture on a press would otherwise keep the drag and the
        // release from the lens.
        if (message is 0x0201 or 0x0204 or 0x0207) { _pressed ??= lens; SetCapture(_pressed.Window); } // WM_LBUTTONDOWN, WM_RBUTTONDOWN, WM_MBUTTONDOWN
        else if (message is 0x0202 or 0x0205 or 0x0208) { EndPress(); Uncapture(target); }
        return handled;
    }
    // A control that held the capture through the press lets go of it on the release: WPF asks Windows to release it and keeps the
    // control as its capture until Windows reports the change. The lens held it, so the report goes to the lens, and the window's
    // hook would hold it back anyway; the control stayed the capture, its pointer shape on screen (a grip's two-way arrow), until
    // the pointer left the lenses. The window is told itself.
    private void Uncapture(IntPtr target)
    {
        if (GetCapture() == target || Mouse.Captured is not DependencyObject held || PresentationSource.FromDependencyObject(held) is not HwndSource source || source.Handle != target) return;
        _forwarding = true;
        try { SendMessage(target, 0x0215, IntPtr.Zero, IntPtr.Zero); } // WM_CAPTURECHANGED
        finally { _forwarding = false; }
    }
    private void EndPress()
    {
        if (_pressed is { } lens && GetCapture() == lens.Window) ReleaseCapture();
        _pressed = null;
    }

    // The pointer's moves over a lens are handed to the window it shows as mouse moves at the same place, so its hover states
    // (a row's highlight, a button's) and a drag follow the pointer there.
    //
    // WPF drops mouse input as spurious while its window is inactive and the pointer is over another window, unless the window
    // holds the mouse capture, so the window holds it for the move. Once active, WPF would go inactive again on the capture going
    // back and on the leave its own mouse tracking reports (the pointer is not over its window), so the window's hook keeps both
    // from it while the pointer is over a lens (TargetMessages); a capture held between moves would keep the pointer's own
    // clicks from the lens, for a window not in the foreground gets the mouse only where it can be seen. During a press the lens
    // takes the capture back after each move.
    private bool _hovering, _forwarding, _inputHooked;
    private IntPtr _hovered;
    private readonly HashSet<IntPtr> _hooked = [];
    private IntPtr _lastMove = new(-1), _lastKeys = new(-1);
    private bool Hover(Lens lens, IntPtr wParam, IntPtr lParam)
    {
        var target = _pressed?.Target ?? lens.Target;
        // An open menu of the panel's holds the capture; taking it for a move would close the menu.
        if (GetCapture() is var held && held != IntPtr.Zero && held != target && !Lenses.ContainsKey(held)) return true;
        if (!_hooked.Contains(target) && HwndSource.FromHwnd(target) is { } source) { source.AddHook(TargetMessages); _hooked.Add(target); }
        if (!_inputHooked) { InputManager.Current.PreProcessInput += KeepForwarded; _inputHooked = true; }
        // Giving and taking back the capture makes Windows send the window under the pointer a move of its own; a move to the
        // same place with the same buttons is not handed on again.
        if (_hovering && lParam == _lastMove && wParam == _lastKeys) return true;
        _lastMove = lParam; _lastKeys = wParam;
        if (_hovering && _hovered != target) Leave(_hovered);
        _hovering = true; _hovered = target;
        var point = new POINT { X = unchecked((short)(long)lParam), Y = unchecked((short)((long)lParam >> 16)) };
        ClientToScreen(lens.Window, ref point);
        ScreenToClient(target, ref point);
        bool own = GetCapture() == target;
        if (!own) SetCapture(target);
        _forwarding = true;
        try { SendMessage(target, 0x0200, wParam, new IntPtr((point.Y << 16) | (point.X & 0xFFFF))); }
        finally { _forwarding = false; }
        if (_pressed is { } pressed) SetCapture(pressed.Window);
        else if (!own && GetCapture() == target) ReleaseCapture();
        return true;
    }
    // While the pointer is over a lens, the panel's windows take no mouse input but the moves handed on. WPF looks again at what is
    // under the pointer after a layout (a "synchronize"), asks Windows which window is there, finds the lens, and put the pointer
    // over nothing: the panel redraws its readings all the time, and a hover highlight did not stay.
    private void KeepForwarded(object sender, PreProcessInputEventArgs e)
    {
        // The raw report's type is internal to WPF and is known by name; under another name nothing is held back.
        var input = e.StagingItem.Input;
        if (_hovering && !_forwarding && input.Device is MouseDevice && input.GetType().Name == "InputReportEventArgs") e.Cancel();
    }
    private IntPtr TargetMessages(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hovering && !_forwarding && message is 0x02A3 or 0x0215) handled = true; // WM_MOUSELEAVE, WM_CAPTURECHANGED
        return IntPtr.Zero;
    }
    // Each frame, and when the lenses leave: once the pointer has left the lenses with no button held, the window it was over
    // gets the pointer as usual again.
    private void CheckHover(bool end = false)
    {
        if (!_hovering) return;
        GetCursorPos(out var cursor);
        if (!end && (_pressed is not null || Lenses.ContainsKey(WindowFromPoint(cursor)))) return;
        _hovering = false;
        _lastMove = _lastKeys = new IntPtr(-1);
        Leave(_hovered);
        _hovered = IntPtr.Zero;
    }
    // Over the window itself, its mouse tracking is started again so that it learns when the pointer leaves; elsewhere it is
    // told the pointer has left.
    private static void Leave(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        GetCursorPos(out var cursor);
        if (WindowFromPoint(cursor) == window)
        {
            var track = new TRACKMOUSEEVENT { Size = Marshal.SizeOf<TRACKMOUSEEVENT>(), Flags = 2, Window = window }; // TME_LEAVE
            TrackMouseEvent(ref track);
        }
        else SendMessage(window, 0x02A3, IntPtr.Zero, IntPtr.Zero); // WM_MOUSELEAVE
    }

    public void Dispose()
    {
        _frame.Stop(); _follow.Stop();
        Hide();
        if (_inputHooked) InputManager.Current.PreProcessInput -= KeepForwarded;
        if (_panelLens.Window != IntPtr.Zero) { Lenses.Remove(_panelLens.Window); DestroyWindow(_panelLens.Window); _panelLens.Window = IntPtr.Zero; }
    }

    private readonly List<IntPtr> _popups = [];
    private readonly EnumWindowsProc _collectPopup;
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint Flags; public RECT Destination, Source; public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible; [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int Size; public uint Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public int Size; public uint Flags; public IntPtr Window; public uint HoverTime; }
    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi")] private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);
    [DllImport("dwmapi")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")] private static extern ushort RegisterClassEx(ref WNDCLASSEX type);
    [DllImport("user32", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", EntryPoint = "LoadCursorW")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowInBand(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter, uint band);
    [DllImport("user32")] private static extern bool GetWindowBand(IntPtr window, out uint band);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32")] private static extern bool EnumThreadWindows(uint thread, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32", EntryPoint = "GetModuleHandleW")] private static extern IntPtr GetModuleHandle(IntPtr name);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32")] private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32")] private static extern IntPtr GetCapture();
    [DllImport("user32")] private static extern bool ReleaseCapture();
    [DllImport("user32")] private static extern bool ClientToScreen(IntPtr window, ref POINT point);
    [DllImport("user32")] private static extern bool ScreenToClient(IntPtr window, ref POINT point);
    [DllImport("user32")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
}

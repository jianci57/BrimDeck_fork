using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BrimDeck.Core;

namespace BrimDeck.Native;

// The frame rate of the program in front, for the collapsed row and the system monitor page: one figure for every panel, with the
// program's name. The frames each program presents are read from Windows' event tracing, as PresentMon reads them (GameTechDev/
// PresentMon, MIT License): the DXGI runtime's Present start (DirectX 10 to 12, event 42), the D3D9 runtime's (DirectX 9 and WPF,
// event 1), and the graphics kernel's present history record (event 171), which also covers OpenGL and Vulkan. The runtimes' are
// counted by swap chain (the event's first field), and a DXGI present with DXGI_PRESENT_TEST (flag 1, which only asks whether the
// window shows) is not counted. A program's figure is its busiest swap chain or its kernel count, whichever is larger: a program
// with two windows is not counted twice, nor a frame counted by both the runtime and the kernel. These are the frames a program
// submits, as Steam's and other games' counters show them: one that does not wait for the display (sync interval 0, as Chromium
// and Electron present; Claude submitted 101 to 115 frames a second on a 60 Hz display while it redrew) submits more frames than the
// display shows, and the compositor drops the rest, so it may read above the refresh rate, as an uncapped game does. The frames the
// display itself takes are not counted: they include everything that moves on it (a chat writing, BrimDeck's own panel), not one
// program.
// The desktop and the taskbar in front show the desktop's own frames: a live wallpaper (Wallpaper Engine, Lively) draws in a
// window of its own inside the desktop's (a child of Progman or WorkerW not owned by explorer), so that program's frame rate and
// name show; behind a still wallpaper, the desktop (explorer's own frames, near none).
// The session subscribes to the selected present events and the DxgKrnl device, context, hardware-queue and packet events used to
// reconstruct GPU work. Each provider is filtered to those event IDs; the GPU figure is unknown when the trace cannot establish work.
// Capturing the screen (Windows Graphics Capture) was measured separately at about 39 ms here and is not used.
// Reading the events takes membership of the Performance Log Users group (or an elevated process); without it the session is
// refused and there is no frame rate (State None): the collapsed row leaves the reading out and the page's line shows a dash.
// Which program: the one whose window is in front. A browser or an Electron app draws in a child process of the same executable,
// and an app built on WebView2 in msedgewebview2.exe children, so a window whose own process presents nothing takes the frames of
// such descendants; any other descendant is a program of its own (everything started from the desktop descends from explorer).
// A UWP app's window (Settings) is ApplicationFrameHost's frame around the app's own, so the app's process counts (Owner).
// While the program in front presents nothing (a chat beside a game on another display), the figure stays with the last program
// that did, so it does not drop to 0 at every click; once that one has stopped too, the program in front shows, at 0.
// The session is named for this process ("BrimDeck Frames 1234"): a test or a second copy has a session of its own, and a session
// stopped by anything else is started again (Sample, at most every few seconds). Sessions left by a BrimDeck that has ended are
// stopped when a session starts, as Windows keeps a session until it is stopped and has few of them.
// Want is called on the UI thread, Sample on the sampler's; the events arrive on a thread of their own.
internal sealed class FrameRates : IDisposable
{
    private const string SessionPrefix = "BrimDeck Frames";
    private static readonly string Session = $"{SessionPrefix} {Environment.ProcessId}";
    private double _restarted = double.NegativeInfinity;
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9"), D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9"),
        DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const uint DxgiData1 = 0xCA11C036, D3D9Data1 = 0x783ACA0A, DxgKrnlData1 = 0x802EC45A;
    private readonly object _lock = new();
    // The frames not yet counted, each at the time it was presented (the performance counter, as Sample's clock): runtime presents
    // by process and swap chain, kernel present records by process (Kernel as the chain). Windows hands the events over in
    // batches, about once a second (the session's flush timer, at least a second, and a buffer as it fills), so counting them as
    // they arrive gave a second with two batches (twice the rate) after one with none; Sample counts by when they were presented
    // instead, up to Lag seconds ago. The session is flushed every FlushEvery as well, so by then they have arrived (here within
    // 0.2 s, 0.3 s at most); one that comes later still counts, in the next reading.
    private const long Kernel = -1;
    private const double Lag = 0.4;
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMilliseconds(100);
    private Timer? _flusher;
    private const int MostEvents = 200_000;
    // The raw events of the live session, as they arrive on the reader's callback: present starts, and
    // the scheduler's packet and mapping events. Sample is the single consumer: it takes the list, feeds
    // the timeline (a new one for each session generation) and reads one window out of it. A generation
    // that has been stopped or replaced loses its events at the door, so nothing of an old session can
    // reach the new session's reading.
    private List<FrameTraceEvent> _pending = [], _spare = [];
    private bool _overflow;
    private int _generation;
    private const int KeptCapacity = 16_384;
    // The sampler's own: the window logic of one session, dropped when the session is (reset, restart,
    // stop) and rebuilt from the next events. Never touched by the reader's thread.
    private FrameTimeline? _timeline;
    private ulong _session, _trace;
    private Thread? _reader;
    private bool _wanted, _refused;
    // Set when the frame rate is wanted anew: the sampler's thread starts its count and its history over (Sample).
    private bool _reset = true;
    // The program shown (Desktop for the desktop) and its readings so far; the last that presented anything, for while the
    // program in front presents nothing.
    private const int Desktop = -1;
    private int _shown, _lastDrawing;
    // The readings the page's curve draws, as many as the network's (SystemSampler.HistoryLength): a minute at one a second.
    private const int HistoryLength = 60;
    private readonly Queue<double> _history = new(HistoryLength);
    private readonly Dictionary<int, (string Name, string? Path)> _names = [];
    private IReadOnlyList<DisplayInfo> _displays = [];
    // Each process's parent and executable (Processes), kept between readings, as taking it costs about 5 ms with 280 processes
    // and a browser in front needs it at every reading. Taken again when the program in front changes, when a process that
    // presented frames (a child just started, or started again) or the program asked about is not in it, and every
    // ProcessesSeconds, since Windows gives an ended process's number to a new one.
    private Dictionary<int, (int Parent, string File)>? _processes;
    private double _processesAt = double.NegativeInfinity;
    private int _processesFront;
    private const double ProcessesSeconds = 10;


    // Found while the events are read, None once the session was refused (no permission); Unknown before it is asked for. Kept
    // while not wanted, so a refusal is not asked again until BrimDeck starts again.
    public SensorState State { get; private set; } = SensorState.Unknown;

    // Reads frame rates from now on, or stops (the session ends, nothing is counted).
    public void Want(bool on)
    {
        lock (_lock)
        {
            if (on == _wanted) return;
            _wanted = on;
            _reset = true;
            if (!on) { StopTrace(); return; }
            if (_refused) return;
            try { StartTrace(); State = SensorState.Found; }
            catch (TraceRefused ex)
            {
                Debug.WriteLine(ex.Message);
                StopTrace();
                _refused = true; State = SensorState.None;
            }
        }
    }

    // The frame rate since the last call, of the program in front (or the last that presented anything); none while not wanted,
    // without permission, or on the first call, which starts the count.
    public Reading<FrameSample> Sample(double now)
    {
        List<FrameTraceEvent> events;
        bool reset; int generation;
        lock (_lock)
        {
            if (!_wanted) return Reading<FrameSample>.Unavailable(Why.Waiting);
            if (_refused) return Reading<FrameSample>.Unavailable(Loc.T("无权读取帧事件", "No access to frame events"));
            if ((_reader is null || !_reader.IsAlive) && now - _restarted >= 5)
            {
                _restarted = now; StopTrace();
                try { StartTrace(); _reset = true; }
                catch (TraceRefused ex) { Debug.WriteLine(ex.Message); StopTrace(); }
            }
            generation = _generation;
            events = _pending; _pending = _spare; _spare = [];
            bool overflow = _overflow;
            reset = _reset || overflow;
            _reset = false; _overflow = false;
            // An overflowed batch is thrown away whole, never fed to the new timeline: a batch whose stops
            // were lost would leave packets open forever and paint busy time into future windows.
            if (overflow) events.Clear();
            // The mappings that died with the batch are asked for again (capture state). If Windows
            // refuses, the reading stays unknown rather than inventing work from a timeline missing them.
            if (overflow)
                try { Enable(DxgKrnl, 0x841, 0, EVENT_CONTROL_CODE_CAPTURE_STATE, DxgKrnlIds); }
                catch (TraceRefused ex) { Debug.WriteLine(ex.Message); }
        }
        if (reset || _timeline is null)
        {
            _timeline = new FrameTimeline(Stopwatch.Frequency);
            _history.Clear(); _shown = 0; _lastDrawing = 0;
            _processes = null; _processesAt = double.NegativeInfinity; _wallpaperAt = _mainAt = double.NegativeInfinity;
            _names.Clear(); _displays = []; // a new session's names and displays are read again, not carried over
        }
        foreach (var value in events) _timeline.Add(value);
        events.Clear();
        lock (_lock)
        {
            if (!_wanted || generation != _generation) return Reading<FrameSample>.Unavailable(Why.Waiting);
            if (events.Capacity <= KeptCapacity) _spare = events;
        }
        var interval = _timeline.Read((long)((now - Lag) * Stopwatch.Frequency));
        if (interval is null || !double.IsFinite(interval.Seconds) || interval.Seconds <= 0)
            return Reading<FrameSample>.Unavailable(Why.Waiting);
        var front = GetForegroundWindow(); int frontPid = Owner(front);
        if (IsDesktop(front))
        {
            var desktop = DesktopRate(front, frontPid, interval, now);
            lock (_lock) return _wanted && generation == _generation ? desktop : Reading<FrameSample>.Unavailable(Why.Waiting);
        }
        Dictionary<int, (int Parent, string File)> Known(int pid)
        {
            bool stale = _processes is null || _processesFront != frontPid || now - _processesAt >= ProcessesSeconds
                || !_processes.ContainsKey(pid) || interval.Counts.Keys.Any(key => !_processes.ContainsKey(key.Pid));
            if (stale && _processesAt != now) { _processes = ProcessSnapshot.Read(); _processesAt = now; _processesFront = frontPid; }
            return _processes!;
        }
        FrameSelection? Select(int owner)
        {
            if (owner == 0) return null;
            bool Belongs(int pid)
            {
                var processes = Known(owner);
                return processes.TryGetValue(owner, out var self) && processes.TryGetValue(pid, out var child)
                    && (Same(child.File, self.File) || Same(child.File, "msedgewebview2.exe")) && Descends(pid, owner, processes);
            }
            return interval.Select(owner, Belongs);
        }
        int shown = frontPid; var chosen = Select(frontPid);
        if (chosen is null && _lastDrawing != 0 && _lastDrawing != frontPid)
        {
            var last = Select(_lastDrawing);
            if (last is not null) { shown = _lastDrawing; chosen = last; }
        }
        long count = chosen?.Count ?? 0;
        if (count > 0) _lastDrawing = shown;
        if (_shown != shown) { _shown = shown; _history.Clear(); }
        var window = shown == frontPid ? front : MainWindow(shown, front, now);
        var display = Display(window); double rate = count / interval.Seconds;
        if (_history.Count == HistoryLength) _history.Dequeue();
        _history.Enqueue(rate);
        var (name, path) = Describe(shown);
        var result = new FrameSample(rate, name)
        {
            Path = path, Refresh = display?.Refresh, History = _history.ToArray(),
            Low1 = count == 0 ? 0 : FrameSample.Low(chosen!.Gaps, 0.01),
            Low01 = count == 0 ? 0 : FrameSample.Low(chosen!.Gaps, 0.001),
            GpuMs = interval.GpuMilliseconds(chosen),
        };
        lock (_lock) return _wanted && generation == _generation ? result : Reading<FrameSample>.Unavailable(Why.Waiting);
    }

    private Reading<FrameSample> DesktopRate(IntPtr front, int explorerPid, FrameWindow interval, double now)
    {
        var display = Display(front);
        if (_wallpaperFront != front || now - _wallpaperAt >= LookAgainSeconds) { _wallpaper = Wallpaper(explorerPid); _wallpaperFront = front; _wallpaperAt = now; }
        int wallpaper = _wallpaper; int key = wallpaper != 0 ? wallpaper : Desktop;
        if (_shown != key) { _shown = key; _history.Clear(); }
        int drawing = wallpaper != 0 ? wallpaper : explorerPid;
        var chosen = interval.Select(drawing); long count = chosen?.Count ?? 0;
        double rate = count / interval.Seconds;
        if (_history.Count == HistoryLength) _history.Dequeue();
        _history.Enqueue(rate);
        var (name, path) = wallpaper != 0 ? Describe(wallpaper) : (Loc.T("桌面", "Desktop"), null);
        return new FrameSample(rate, name)
        {
            Path = path, Refresh = display?.Refresh, History = _history.ToArray(),
            Low1 = count == 0 ? 0 : FrameSample.Low(chosen!.Gaps, 0.01),
            Low01 = count == 0 ? 0 : FrameSample.Low(chosen!.Gaps, 0.001),
            GpuMs = interval.GpuMilliseconds(chosen),
        };
    }
    // The windows looked for each reading (a live wallpaper's program, a program's largest window) are kept until the window in front
    // changes, or for LookAgainSeconds: listing every window each second cost the most of a reading.
    private const double LookAgainSeconds = 5;
    private IntPtr _wallpaperFront, _mainFront, _mainWindow;
    private int _wallpaper, _mainPid;
    private double _wallpaperAt = double.NegativeInfinity, _mainAt = double.NegativeInfinity;

    // The program of a live wallpaper: the owner of a window inside the desktop's (Progman, or a WorkerW behind the icons) that is
    // not explorer's; 0 behind a still wallpaper.
    private static int Wallpaper(int explorerPid)
    {
        int found = 0;
        EnumWindows((window, _) =>
        {
            if (ClassName(window, stackalloc char[64]) is not ("Progman" or "WorkerW")) return true;
            EnumChildWindows(window, (child, _) =>
            {
                GetWindowThreadProcessId(child, out int owner);
                if (owner == explorerPid || owner == 0 || !IsWindowVisible(child)) return true;
                found = owner;
                return false;
            }, IntPtr.Zero);
            return found == 0;
        }, IntPtr.Zero);
        return found;
    }

    // The desktop (the shell's window, or the windows behind the icons) and the taskbars, whose process (explorer) presents only a
    // few frames of its own.
    private static bool IsDesktop(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        if (window == GetShellWindow()) return true;
        return ClassName(window, stackalloc char[64]) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }
    // The window's class name, read into the caller's buffer.
    private static ReadOnlySpan<char> ClassName(IntPtr window, Span<char> buffer) =>
        buffer[..Math.Max(0, GetClassNameW(window, ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(buffer)), buffer.Length))];

    // The process that draws a window: its own, except for a UWP app (Settings, Calculator), whose window is ApplicationFrameHost's
    // frame around the app's own window (a CoreWindow child of another process); the frame draws only its title bar.
    private static int Owner(IntPtr window)
    {
        GetWindowThreadProcessId(window, out int pid);
        if (window == IntPtr.Zero || ClassName(window, stackalloc char[64]) is not "ApplicationFrameWindow") return pid;
        int app = 0;
        EnumChildWindows(window, (child, _) =>
        {
            if (ClassName(child, stackalloc char[64]) is not "Windows.UI.Core.CoreWindow") return true;
            GetWindowThreadProcessId(child, out int owner);
            if (owner == pid || owner == 0) return true;
            app = owner;
            return false;
        }, IntPtr.Zero);
        return app != 0 ? app : pid;
    }
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // The display a window is on; the list is read again only when a display it has not seen turns up.
    private DisplayInfo? Display(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        var monitor = MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        var known = _displays.FirstOrDefault(display => display.Handle == monitor);
        if (known is not null) return known;
        _displays = Displays.Current();
        return _displays.FirstOrDefault(display => display.Handle == monitor);
    }

    // A program's name as it gives it (the executable's description, "Google Chrome"), else its file's name, and its executable's
    // path; kept per process.
    private (string Name, string? Path) Describe(int pid)
    {
        if (_names.TryGetValue(pid, out var known)) return known;
        string name = pid == 0 ? "—" : $"PID {pid}";
        string? file = null;
        if (AppIdentity.ImagePath(pid) is { Length: > 0 } path)
        {
            file = path;
            string? described = null;
            try { described = FileVersionInfo.GetVersionInfo(file).FileDescription?.Trim(); } catch (FileNotFoundException) { }
            name = described is { Length: > 0 } ? described : Path.GetFileNameWithoutExtension(file);
        }
        if (_names.Count > 256) _names.Clear();
        return _names[pid] = (name, file);
    }

    // The largest visible window of a program (a UWP app's frame), for the display it is on; kept as LookAgainSeconds says.
    private IntPtr MainWindow(int pid, IntPtr front, double now)
    {
        if (pid != _mainPid || front != _mainFront || now - _mainAt >= LookAgainSeconds || !IsWindow(_mainWindow))
        { _mainWindow = LargestWindow(pid); _mainPid = pid; _mainFront = front; _mainAt = now; }
        return _mainWindow;
    }
    private static IntPtr LargestWindow(int pid)
    {
        IntPtr best = IntPtr.Zero; long area = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || Owner(window) != pid || !GetWindowRect(window, out var r)) return true;
            long size = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (size > area) { area = size; best = window; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static bool Descends(int process, int ancestor, Dictionary<int, (int Parent, string File)> processes)
    {
        for (int depth = 0; depth < 8 && processes.TryGetValue(process, out var entry) && entry.Parent != 0 && entry.Parent != process; depth++)
        {
            if (entry.Parent == ancestor) return true;
            process = entry.Parent;
        }
        return false;
    }

    // ---- the event tracing session ----

    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1, EVENT_CONTROL_CODE_CAPTURE_STATE = 2;
    private static readonly ushort[] DxgKrnlIds = [27,28,29,30,31,32,171,175,177,178,180,244,422,424];
    private TraceSession? _activeSession;
    private sealed class TraceSession : IDisposable
    {
        public readonly int Generation;
        public IntPtr LoggerName = Marshal.StringToHGlobalUni(Session);
        public IntPtr Buffer = Marshal.AllocHGlobal(8);
        public EventRecordCallback Callback { get; }
        public TraceSession(FrameRates owner, int generation) { Generation = generation; Callback = record => owner.OnEvent(record, this); }
        private long _handle;
        public void SetHandle(ulong handle) => Interlocked.Exchange(ref _handle, unchecked((long)handle));
        public void Close() { long handle = Interlocked.Exchange(ref _handle, 0); if (handle != 0 && handle != -1) CloseTrace(unchecked((ulong)handle)); }
        public void Dispose() { Close(); if (LoggerName != IntPtr.Zero) { Marshal.FreeHGlobal(LoggerName); LoggerName = IntPtr.Zero; } if (Buffer != IntPtr.Zero) { Marshal.FreeHGlobal(Buffer); Buffer = IntPtr.Zero; } GC.KeepAlive(Callback); }
    }

    private void StartTrace()
    {
        StopLeftSessions();
        var state = new TraceSession(this, ++_generation);
        _activeSession = state;
        bool readerStarted = false;
        try
        {
            IntPtr properties = Properties(); uint error;
            try { error = StartTraceW(out _session, Session, properties); } finally { Marshal.FreeHGlobal(properties); }
            if (error != 0) throw new TraceRefused("StartTrace", error);
            Enable(DxgKrnl, 0x841, 0, EVENT_CONTROL_CODE_ENABLE_PROVIDER, DxgKrnlIds);
            Enable(Dxgi, 0x8000000000000002, 0x8000000000000002, EVENT_CONTROL_CODE_ENABLE_PROVIDER, [42]);
            Enable(D3D9, 0x8000000000000002, 0x8000000000000002, EVENT_CONTROL_CODE_ENABLE_PROVIDER, [1]);
            IntPtr logfile = Zeroed(448);
            try
            {
                Marshal.WriteIntPtr(logfile, 8, state.LoggerName);
                Marshal.WriteInt32(logfile, 28, 0x100 | 0x10000000 | 0x1000);
                Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(state.Callback));
                _trace = OpenTraceW(logfile);
                if (_trace == ulong.MaxValue) throw new TraceRefused("OpenTrace", (uint)Marshal.GetLastWin32Error());
                state.SetHandle(_trace);
            }
            finally { Marshal.FreeHGlobal(logfile); }
            ulong trace = _trace;
            _reader = new Thread(() => { try { ProcessTrace([trace], 1, IntPtr.Zero, IntPtr.Zero); } finally { state.Dispose(); } }) { IsBackground = true, Name = "BrimDeck frame events" };
            _reader.Start(); readerStarted = true;
            Enable(DxgKrnl, 0x841, 0, EVENT_CONTROL_CODE_CAPTURE_STATE, DxgKrnlIds);
            int generation = state.Generation;
            _flusher = new Timer(_ => { lock (_lock) if (_wanted && generation == _generation) Control(0, Session, 3); }, null, FlushEvery, FlushEvery);
        }
        catch
        {
            if (_session != 0) { Control(0, Session, 1); _session = 0; }
            state.Close(); _trace = 0; _activeSession = null;
            if (!readerStarted) state.Dispose();
            throw;
        }
    }
    private void StopTrace()
    {
        _generation++; _reset = true;
        _flusher?.Dispose(); _flusher = null;
        if (_session != 0) { Control(0, Session, 1); _session = 0; }
        _activeSession?.Close(); _activeSession = null; _trace = 0; _reader = null;
        _pending = []; _overflow = false;
    }

    // Stops the sessions of BrimDecks no longer running (and the one this process may have left), found among all the machine's
    // sessions (QueryAllTracesW, at most 64).
    private static void StopLeftSessions()
    {
        const int Most = 64, Size = 120 + 2048;
        var properties = new IntPtr[Most];
        try
        {
            for (int i = 0; i < Most; i++)
            {
                properties[i] = Zeroed(Size);
                Marshal.WriteInt32(properties[i], 0, Size);
                Marshal.WriteInt32(properties[i], 112, 120 + 1024); // LogFileNameOffset
                Marshal.WriteInt32(properties[i], 116, 120); // LoggerNameOffset
            }
            if (QueryAllTracesW(properties, Most, out uint count) != 0) return;
            for (int i = 0; i < count && i < Most; i++)
            {
                string? name = Marshal.PtrToStringUni(properties[i] + Marshal.ReadInt32(properties[i], 116));
                if (name is null || !name.StartsWith(SessionPrefix, StringComparison.Ordinal)) continue;
                bool alive = int.TryParse(name.AsSpan(SessionPrefix.Length).Trim(), out int pid) && pid != Environment.ProcessId && Running(pid);
                if (!alive) Control(0, name, 1);
            }
        }
        finally { foreach (var p in properties) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
    }
    private static bool Running(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static void Control(ulong session, string? name, uint code)
    {
        IntPtr properties = Properties();
        try { ControlTraceW(session, name, properties, code); }
        finally { Marshal.FreeHGlobal(properties); }
    }

    // EVENT_TRACE_PROPERTIES (120 bytes on x64) and room for the session's name after it: Wnode.BufferSize at 0,
    // Wnode.ClientContext at 40 (1: the performance counter), Wnode.Flags at 44 (WNODE_FLAG_TRACED_GUID), BufferSize in KB at 48,
    // MinimumBuffers at 52, MaximumBuffers at 56, LogFileMode at 64 (EVENT_TRACE_REAL_TIME_MODE), FlushTimer in seconds at 68,
    // LoggerNameOffset at 116.
    private static IntPtr Properties()
    {
        int size = 120 + 512;
        IntPtr p = Zeroed(size);
        Marshal.WriteInt32(p, 0, size);
        Marshal.WriteInt32(p, 40, 1);
        Marshal.WriteInt32(p, 44, 0x20000);
        Marshal.WriteInt32(p, 48, 16);
        Marshal.WriteInt32(p, 52, 2);
        Marshal.WriteInt32(p, 56, 8);
        Marshal.WriteInt32(p, 64, 0x100);
        Marshal.WriteInt32(p, 68, 1);
        Marshal.WriteInt32(p, 116, 120);
        return p;
    }

    // A provider with only these events (EVENT_FILTER_TYPE_EVENT_ID, filtered in) at the information level, through
    // ENABLE_TRACE_PARAMETERS (48 bytes: Version 2 at 0, EnableFilterDesc at 32, FilterDescCount at 40) and one
    // EVENT_FILTER_DESCRIPTOR (Ptr at 0, Size at 8, Type at 12) over an EVENT_FILTER_EVENT_ID (FilterIn, Reserved, Count, the IDs).
    private void Enable(Guid provider, ulong any, ulong all, uint code, params ushort[] ids)
    {
        int size = 4 + 2 * ids.Length;
        IntPtr filter = Zeroed(size), descriptor = Zeroed(16), parameters = Zeroed(48);
        try
        {
            Marshal.WriteByte(filter, 0, 1); Marshal.WriteInt16(filter, 2, (short)ids.Length);
            for (int i = 0; i < ids.Length; i++) Marshal.WriteInt16(filter, 4 + 2 * i, (short)ids[i]);
            Marshal.WriteIntPtr(descriptor, 0, filter); Marshal.WriteInt32(descriptor, 8, size); Marshal.WriteInt32(descriptor, 12, unchecked((int)0x80000200));
            Marshal.WriteInt32(parameters, 0, 2);
            Marshal.WriteIntPtr(parameters, 32, descriptor);
            Marshal.WriteInt32(parameters, 40, 1);
            uint error = EnableTraceEx2(_session, provider, code, 4, any, all, 0, parameters);
            if (error != 0) throw new TraceRefused($"EnableTraceEx2 {provider}", error);
        }
        finally { Marshal.FreeHGlobal(filter); Marshal.FreeHGlobal(descriptor); Marshal.FreeHGlobal(parameters); }
    }

    // EVENT_RECORD (112 bytes on x64): EventHeader.Flags at 4 (0x40: pointers in the data are 64 bits), ProcessId at 12, TimeStamp at
    // 16 (the performance counter, as the session's ClientContext asks), ProviderId at 24 (its first four bytes tell the three apart), UserDataLength at 86, UserData at 96. The data:
    // DXGI's Present start pIDXGISwapChain then Flags, D3D9's pSwapchain then Flags.
    // ---- event decoding: runtime presents, kernel present records, scheduler metadata and packets ----
    // Normalized into FrameTraceEvents and handed to the sampler's timeline via Count. Every event here
    // carries its own timestamp; a rundown (DCStart) that arrives after a window was already paid is the
    // timeline's late path to decide about, never a rewrite of paid history. The payloads are read by name (TdhGetProperty), so the layout comes from
    // the manifest at runtime rather than guessed offsets.
    private const ushort
        KernelPresentId = 171,          // PresentHistoryInfo (0xAB)
        DeviceStartId = 27, DeviceStopId = 28, DeviceDCStartId = 29,
        ContextStartId = 30, ContextStopId = 31, ContextDCStartId = 32,
        DmaPacketStartId = 175, DmaPacketInfoId = 177,
        QueuePacketStartId = 178, QueuePacketWaitStartId = 244, QueuePacketStopId = 180,
        HwQueueStartId = 422, HwQueueDCStartId = 424;

    private static readonly UIntPtr PDxgAdapterName = (UIntPtr)Marshal.StringToHGlobalUni("pDxgAdapter"),
        HDeviceName = (UIntPtr)Marshal.StringToHGlobalUni("hDevice"),
        HContextName = (UIntPtr)Marshal.StringToHGlobalUni("hContext"),
        NodeOrdinalName = (UIntPtr)Marshal.StringToHGlobalUni("NodeOrdinal"),
        SubmitSequenceName = (UIntPtr)Marshal.StringToHGlobalUni("SubmitSequence"),
        DmaSequenceName = (UIntPtr)Marshal.StringToHGlobalUni("ulQueueSubmitSequence"),
        PacketTypeName = (UIntPtr)Marshal.StringToHGlobalUni("PacketType"),
        ParentHwQueueName = (UIntPtr)Marshal.StringToHGlobalUni("ParentDxgHwQueue");

    private void OnEvent(IntPtr record, TraceSession session)
    {
        int pid = Marshal.ReadInt32(record, 12);
        uint provider = (uint)Marshal.ReadInt32(record, 24);
        if (provider is not (DxgiData1 or D3D9Data1 or DxgKrnlData1)) return;
        long time = Marshal.ReadInt64(record, 16);
        ushort id = (ushort)Marshal.ReadInt16(record, 40);
        int length = (ushort)Marshal.ReadInt16(record, 86);   // UserData.Length
        IntPtr data = Marshal.ReadIntPtr(record, 96);          // UserData.Ptr

        if (provider == DxgKrnlData1)
        {
            switch (id)
            {
                case KernelPresentId:
                    Count(session, new(time, FrameTraceKind.Present, Pid: pid, Chain: Kernel));
                    return;
                // ---- metadata: device, context and hardware queue mappings ----
                case DeviceDCStartId or DeviceStartId:
                    if (U64(session, record, PDxgAdapterName, out ulong adapter) && U64(session, record, HDeviceName, out ulong device))
                        Count(session, new(time, FrameTraceKind.DeviceStart, Device: device, Adapter: adapter));
                    return;
                case DeviceStopId:
                    if (U64(session, record, HDeviceName, out ulong deadDevice))
                        Count(session, new(time, FrameTraceKind.DeviceStop, Device: deadDevice));
                    return;
                case ContextDCStartId or ContextStartId:
                    // A DCStart comes from the rundown (capture state), not the context's process: Pid 0,
                    // claimed later by the context's first QueueStart.
                    if (U64(session, record, HContextName, out ulong context) && U64(session, record, HDeviceName, out ulong onDevice)
                        && U32(session, record, NodeOrdinalName, out uint node))
                        Count(session, new(time, FrameTraceKind.ContextStart, Pid: id == ContextDCStartId ? 0 : pid,
                            Context: context, Device: onDevice, Node: node));
                    return;
                case ContextStopId:
                    if (U64(session, record, HContextName, out ulong stopped))
                        Count(session, new(time, FrameTraceKind.ContextStop, Context: stopped));
                    return;
                case HwQueueStartId or HwQueueDCStartId:
                    // The payload's ParentDxgHwQueue is the hContext later QueuePacket events carry, so it
                    // becomes the packet target (Device); the queue's own hContext is the parent side. The
                    // event's pid is the capture requester's, never the queue owner's: Pid 0, claimed later
                    // through the parent context's QueueStart.
                    if (U64(session, record, HContextName, out ulong parent) && U64(session, record, ParentHwQueueName, out ulong hwQueue))
                        Count(session, new(time, FrameTraceKind.HwQueueStart, Pid: 0,
                            Context: parent /* hContext */, Device: hwQueue /* ParentDxgHwQueue */));
                    return;
                // ---- packets: legacy DMA and HAGS queue packets ----
                case DmaPacketStartId:
                    if (U64(session, record, HContextName, out ulong dmaContext) && U32(session, record, DmaSequenceName, out uint dmaSeq))
                        Count(session, new(time, FrameTraceKind.DmaStart, Pid: pid, Context: dmaContext, Sequence: dmaSeq));
                    return;
                case DmaPacketInfoId:
                    if (U64(session, record, HContextName, out ulong dmaDone) && U32(session, record, DmaSequenceName, out uint dmaDoneSeq))
                        Count(session, new(time, FrameTraceKind.DmaStop, Pid: pid, Context: dmaDone, Sequence: dmaDoneSeq));
                    return;
                case QueuePacketStartId:
                    // PacketType 4 is DXGKETW_WAIT_COMMAND_BUFFER: it holds the FIFO's head but is not work.
                    // A payload whose PacketType cannot be read is dropped rather than guessed as a wait.
                    if (!U32(session, record, PacketTypeName, out uint packetType)) return;
                    if (U64(session, record, HContextName, out ulong queueContext) && U32(session, record, SubmitSequenceName, out uint queueSeq))
                        Count(session, new(time, FrameTraceKind.QueueStart, Pid: pid, Context: queueContext, Sequence: queueSeq, Wait: packetType == 4));
                    return;
                case QueuePacketWaitStartId:
                    // Start_2 (a monitor wait) has no PacketType in its schema: always a wait.
                    if (U64(session, record, HContextName, out ulong waitContext) && U32(session, record, SubmitSequenceName, out uint waitSeq))
                        Count(session, new(time, FrameTraceKind.QueueStart, Pid: pid, Context: waitContext, Sequence: waitSeq, Wait: true));
                    return;
                case QueuePacketStopId:
                    if (U64(session, record, HContextName, out ulong doneContext) && U32(session, record, SubmitSequenceName, out uint doneSeq))
                        Count(session, new(time, FrameTraceKind.QueueStop, Pid: pid, Context: doneContext, Sequence: doneSeq));
                    return;
            }
            return;
        }

        // ---- runtime presents: DXGI 42 / D3D9 1, the swap chain is the payload's first field ----
        // Validate the id as well as the provider: the session's filter decides what arrives live, but a
        // synthetic record (or a future manifest change) must not decode as a present.
        if ((provider == DxgiData1) != (id == 42) || (provider == D3D9Data1) != (id == 1)) return;
        // A 64-bit header (header flag 0x40, EVENT_HEADER_FLAG_64_BIT_HEADER) aligns its payload at 8 bytes. A DXGI present with
        // DXGI_PRESENT_TEST (flag bit 1) only asks whether the window shows and presents no frame.
        int pointer = (Marshal.ReadInt16(record, 4) & 0x40) != 0 ? 8 : 4;
        if (data == IntPtr.Zero || length < pointer + 4) return; // a short payload has no chain to read
        long chain = pointer == 8 ? Marshal.ReadInt64(data, 0) : Marshal.ReadInt32(data, 0);
        if (provider == DxgiData1 && (Marshal.ReadInt32(data, pointer) & 1) != 0) return;
        Count(session, new(time, FrameTraceKind.Present, Pid: pid, Chain: chain));
    }

    // TDH reads a scalar by property name; Index = uint.MaxValue asks for the first instance by name, so
    // no version's layout is guessed. The buffer is this call's own, zeroed before the read and freed
    // after: no static buffer is shared across (possibly re-entrant) callbacks.
    private bool U64(TraceSession session, IntPtr record, UIntPtr name, out ulong value)
    {
        value = 0;
        // The session's own 8-byte scratch buffer, zeroed before each read: one reader thread owns one
        // session, so no locking, and no buffer is shared between instances.
        IntPtr buffer = session.Buffer;
        for (int i = 0; i < 8; i++) Marshal.WriteByte(buffer, i, 0);
        var descriptor = new[] { new PropertyDataDescriptor { Name = name, Index = uint.MaxValue } };
        if (TdhGetProperty(record, 0, IntPtr.Zero, 1, descriptor, 8, buffer) != 0) return false;
        value = (ulong)Marshal.ReadInt64(buffer); return true;
    }
    private bool U32(TraceSession session, IntPtr record, UIntPtr name, out uint value)
    {
        value = 0;
        IntPtr buffer = session.Buffer;
        for (int i = 0; i < 8; i++) Marshal.WriteByte(buffer, i, 0);
        var descriptor = new[] { new PropertyDataDescriptor { Name = name, Index = uint.MaxValue } };
        if (TdhGetProperty(record, 0, IntPtr.Zero, 1, descriptor, 8, buffer) != 0) return false;
        value = (uint)Marshal.ReadInt32(buffer); return true;
    }

    // The sampler's single consumer path: only this generation's events are kept, and an overflow marks
    // the whole batch a reset rather than a partial reading.
    private void Count(TraceSession session, FrameTraceEvent value)
    {
        lock (_lock)
        {
            if (!_wanted || session.Generation != _generation) return;
            if (_pending.Count >= MostEvents) { _overflow = true; return; }
            _pending.Add(value);
        }
    }

    // As large as the largest block asked for (StopLeftSessions).
    private static readonly byte[] Zeros = new byte[120 + 2048];
    private static IntPtr Zeroed(int size)
    {
        IntPtr p = Marshal.AllocHGlobal(size);
        Marshal.Copy(Zeros, 0, p, size);
        return p;
    }

    public void Dispose()
    {
        lock (_lock) { _wanted = false; StopTrace(); }
    }

    private sealed class TraceRefused(string call, uint error) : Exception($"{call} failed: {error}");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void EventRecordCallback(IntPtr record);
    private delegate bool WindowProc(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint StartTraceW(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint ControlTraceW(ulong handle, string? name, IntPtr properties, uint code);
    [DllImport("advapi32.dll")] private static extern uint EnableTraceEx2(ulong handle, in Guid provider, uint code, byte level, ulong any, ulong all, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")] private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    // TDH: the event payload's properties by name (PROPERTY_DATA_DESCRIPTOR: the name's pointer, then the array index).
    [StructLayout(LayoutKind.Sequential)] private struct PropertyDataDescriptor { public UIntPtr Name; public uint Index; }
    [DllImport("tdh.dll")] private static extern uint TdhGetProperty(IntPtr eventRecord, uint contextCount, IntPtr contexts, uint propertyCount, PropertyDataDescriptor[] descriptors, uint size, IntPtr buffer);
    [DllImport("advapi32.dll")] private static extern uint CloseTrace(ulong handle);
    [DllImport("advapi32.dll")] private static extern uint QueryAllTracesW([In] IntPtr[] properties, uint count, out uint loggers);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    // ushort: a char is not blittable to DllImport, which would copy a single ANSI byte.
    [DllImport("user32.dll")] private static extern int GetClassNameW(IntPtr window, ref ushort name, int length);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
}

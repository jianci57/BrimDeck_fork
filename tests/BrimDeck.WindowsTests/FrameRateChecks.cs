using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

// The frame rate of the program in front, from Windows' frame events: where the user may read them (Performance Log Users, or
// elevated) the session starts and a reading after the first names a program; elsewhere the session is refused and there is no
// reading. Either way the first reading only starts the count, and nothing is read once not wanted. Where the events are read,
// the session is this process's own, a session left by a BrimDeck that has ended is stopped, and a session stopped from outside
// is started again.
internal static class FrameRateChecks
{
    private const string Prefix = "BrimDeck Frames";

    public static void Run(Action<string, bool> check)
    {
        static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        // A session as a BrimDeck that ended without stopping it leaves one (no process has this number).
        string left = $"{Prefix} {int.MaxValue}";
        bool leftStarted = Start(left);
        using var rates = new FrameRates();
        rates.Want(true);
        bool traced = rates.State == SensorState.Found;
        Console.WriteLine(traced ? "  frame events: read" : "  frame events: refused here (not in Performance Log Users and not elevated)");
        check("frame rates: the events are read, or refused", traced || rates.State == SensorState.None);
        if (leftStarted) check("frame rates: a session left by a BrimDeck that has ended is stopped", !Exists(left));
        else Stop(left);
        string own = $"{Prefix} {Environment.ProcessId}";
        if (traced) check("frame rates: the session is named for this process", Exists(own));
        check("frame rates: the first reading only starts the count", !rates.Sample(Now()).HasValue);
        Thread.Sleep(1300);
        var reading = rates.Sample(Now());
        check(traced ? "frame rates: a reading names a program and its rate" : "frame rates: refused, there is no reading",
            traced ? reading.TryGet(out var frames) && frames.Program.Length > 0 && frames.PerSecond is >= 0 and < 5000 && frames.History.Count == 1 : !reading.HasValue);
        if (reading.TryGet(out var shown)) Console.WriteLine($"  in front: {shown.Program}, {shown.PerSecond:0.#} frames a second, display {shown.Refresh:0.#} Hz");
        if (traced)
        {
            // Stopped from outside (as another tool, or a session handle reused, might): the next reading starts it again.
            Stop(own);
            Thread.Sleep(300);
            rates.Sample(Now());
            check("frame rates: a session stopped from outside is started again", Exists(own));
        }
        rates.Want(false);
        check("frame rates: nothing is read once not wanted, and the session is gone", !rates.Sample(Now()).HasValue && !Exists(own));
        if (!traced) return;
        rates.Want(true);
        check("frame rates: wanted again, the session starts again", rates.State == SensorState.Found && Exists(own) && !rates.Sample(Now()).HasValue);
    }

    // A live probe: this process's own animated window in front, sampled through the normal Sample
    // foreground path — no reflection, no second implementation of the sampling. The probe asks for a
    // real reading of its own drawing: frames above 0, the GPU's time above 0, and low percentiles along
    // with them. Where frame events cannot be read at all the probe reports that and does not fake a pass;
    // where they are read but the GPU stays unknown it fails, as the acceptance asks.
    public static void Probe(Action<string, bool> check)
    {
        static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var window = ProbeWindow();
        using var rates = new FrameRates();
        try
        {
            rates.Want(true);
            bool traced = rates.State == SensorState.Found;
            if (!traced)
            {
                // Without permission nothing can be probed here: say so and fail the probe, never fake a pass.
                Console.WriteLine($"  probe: frame events refused here — UNKNOWN ({rates.State}); the probe cannot pass on this machine");
                check("frame probe: frame events must be readable to run the probe", false);
                return;
            }
            check("frame probe: the first reading only starts the count", !rates.Sample(Now()).HasValue);
            BringToFront(window);
            // A few seconds of pumping the animation while sampling through the normal path once a second.
            var readings = new List<FrameSample>();
            for (int second = 0; second < 5; second++)
            {
                Pump(1000);
                if (rates.Sample(Now()).TryGet(out var frame)) { readings.Add(frame); Console.WriteLine(
                    $"  probe t+{second + 1}s: {frame.PerSecond,6:0.#} fps, low1 {(frame.Low1 is { } l1 ? l1.ToString("0.#") : "-")}, " +
                    $"low0.1 {(frame.Low01 is { } l01 ? l01.ToString("0.#") : "-")}, gpu {(frame.GpuMs is { } g ? $"{g:0.00} ms" : "unknown")}, {frame.Program}, path {frame.Path ?? "-"}"); }
                else Console.WriteLine($"  probe t+{second + 1}s: no reading yet");
                BringToFront(window);
            }
            // The reading must name this process's own window (its executable): only then does a GPU figure
            // belong to the probe's own drawing and not to some other program that was in front.
            string own = Environment.ProcessPath ?? "";
            var ownReadings = readings.Where(r => string.Equals(r.Path, own, StringComparison.OrdinalIgnoreCase)).ToList();
            check("frame probe: a reading names this process's own animated window", ownReadings.Any(r => r.PerSecond > 0));
            var best = ownReadings.Where(r => r.PerSecond > 0)
                .OrderByDescending(r => r.GpuMs is not null).ThenByDescending(r => r.PerSecond).FirstOrDefault();
            check("frame probe: the GPU's busy time is read for the probe's own frames (>0, not faked)",
                best is { GpuMs: > 0 } && best.Low1 is > 0 && best.Low01 is > 0);
            // Stop and restart: the first reading after restart waits, then the numbers come back for the probe's own window.
            rates.Want(false);
            check("frame probe: nothing is read once not wanted", !rates.Sample(Now()).HasValue);
            rates.Want(true);
            BringToFront(window);
            Pump(1400);
            _ = rates.Sample(Now()); // the first reading after a restart only starts the count again.
            Pump(1100);
            check("frame probe: after stop and restart the readings recover",
                rates.Sample(Now()).TryGet(out var after) && after.PerSecond > 0 && string.Equals(after.Path, own, StringComparison.OrdinalIgnoreCase) && after.GpuMs > 0);
        }
        finally
        {
            rates.Want(false);
            window.Close();
        }
    }

    // A small always-animated window: a rectangle glides forever, so WPF presents a continuous stream of
    // frames and submits real GPU packets for them.
    private static Window ProbeWindow()
    {
        var rectangle = new Rectangle { Width = 40, Height = 40, Fill = Brushes.DodgerBlue };
        var glide = new TranslateTransform();
        glide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 160, new Duration(TimeSpan.FromMilliseconds(400)))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        rectangle.RenderTransform = glide;
        var window = new Window { Content = rectangle, Width = 220, Height = 120, Left = 40, Top = 40, ShowInTaskbar = false, Title = "BrimDeck Frame Probe" };
        window.Show();
        return window;
    }

    // The probe's window must be the program in front for Sample's foreground path to report it.
    private static void BringToFront(Window window)
    {
        window.Topmost = true;
        window.Activate();
        SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        window.Topmost = false;
        if (GetForegroundWindow() is { } front && GetWindowThreadProcessId(front, out int pid) != 0 && pid == Environment.ProcessId) return;
        window.Topmost = true; window.Activate(); window.Topmost = false;
        Console.WriteLine("  probe: foreground pid " + (GetForegroundWindow() is { } f && GetWindowThreadProcessId(f, out int p) != 0 ? p : 0) + ", own " + Environment.ProcessId);
    }

    // Pump the WPF message loop (the animation needs it); Sleep would stall the UI thread and the frames with it.
    private static void Pump(double milliseconds)
    {
        var stop = Stopwatch.GetTimestamp() + (long)(milliseconds / 1000.0 * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < stop)
        {
            var frame = new DispatcherFrame(true);
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr window, out int pid);

    // A real-ETW probe against a running target process (the parent names the pid; nothing here starts or
    // touches it). The session and its callback are the production FrameRates' own; each second the raw
    // pending events are taken atomically from under the lock (replaced with a fresh list, so no event is
    // lost between the probe and any consumer) and replayed into a separate FrameTimeline of this probe,
    // read at the cutoff Sample would use (Now − Lag). Select(targetPid) and GpuMilliseconds then show
    // what the Core window logic concludes for the target: this verifies the ETW → normalized event →
    // window pipeline, not the foreground path, and the numbers below say so honestly.
    public static void TraceProbe(Action<string, bool> check, int targetPid)
    {
        static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(FrameRates);
        double lag = (double)(type.GetField("Lag", flags | BindingFlags.Static)!.GetValue(null)!);
        using var rates = new FrameRates();
        var lockObject = type.GetField("_lock", flags)!.GetValue(rates)!;
        var pendingField = type.GetField("_pending", flags)!;

        rates.Want(true);
        if (rates.State != SensorState.Found)
        {
            Console.WriteLine($"  trace probe: frame events refused here — UNKNOWN ({rates.State}); not a pass");
            check($"trace probe (pid {targetPid}): frame events must be readable", false);
            return;
        }
        // Optional offline replay: when BRIMDECK_TRACE_FILE is set, every window's raw events are written
        // as one JSONL line (the events as they were taken, plus the window's cutoff) so the exact moment
        // the GPU figure goes unknown can be replayed elsewhere. Without the variable nothing is written.
        var tracePath = Environment.GetEnvironmentVariable("BRIMDECK_TRACE_FILE");
        StreamWriter? trace = tracePath is null ? null : new StreamWriter(new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write), new System.Text.UTF8Encoding(false));
        int session = 0;

        void TraceLine(long through, FrameTraceEvent[] events, int second)
        {
            if (trace is null) return;
            trace.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Session = session + 1, Second = second, Frequency = Stopwatch.Frequency, Through = through, Events = events }));
            trace.Flush();
        }

        var timeline = new FrameTimeline(Stopwatch.Frequency);
        int clean = 0;

        void Window(int second)
        {
            Thread.Sleep(1000);
            double now = Now();
            // The test-specific consumer: the session's pending events are taken atomically and owned by
            // this probe's timeline; nothing else consumes them (the production Sample is not called).
            FrameTraceEvent[] events;
            lock (lockObject)
            {
                events = ((IEnumerable<FrameTraceEvent>)(pendingField.GetValue(rates) ?? Array.Empty<FrameTraceEvent>())).ToArray();
                pendingField.SetValue(rates, new List<FrameTraceEvent>());
            }
            foreach (var value in events) timeline.Add(value);
            var kinds = events.GroupBy(e => e.Kind).Select(g => $"{g.Key}:{g.Count()}").OrderBy(t => t).ToList();
            int metadata = events.Count(e => e.Kind is FrameTraceKind.DeviceStart or FrameTraceKind.DeviceStop or FrameTraceKind.ContextStart or FrameTraceKind.ContextStop or FrameTraceKind.HwQueueStart);
            int dma = events.Count(e => e.Kind is FrameTraceKind.DmaStart or FrameTraceKind.DmaStop);
            int hags = events.Count(e => e.Kind is FrameTraceKind.QueueStart or FrameTraceKind.QueueStop);
            long through = (long)((now - lag) * Stopwatch.Frequency);
            TraceLine(through, events, second);
            var window = timeline.Read(through);
            var selection = window?.Select(targetPid);
            double seconds = window is { } w ? w.Seconds : 0;
            double? gpu = window?.GpuMilliseconds(selection);
            double? low = selection is { Count: > 0 } s ? FrameSample.Low(s.Gaps, 0.01) : null;
            // Diagnostics for "no presents for the target": the window's own counts per pid (the busiest
            // chain each) with their GPU figure, and the raw present records per pid, so a wrong pid, a
            // lag miss or an over-eager filter shows up directly.
            var byPid = window?.Counts.GroupBy(p => p.Key.Pid)
                .Select(g => (Pid: g.Key, Count: g.Max(p => p.Value))).OrderByDescending(p => p.Count).Take(8)
                .Select(p => $"{p.Pid}:{p.Count}/{(window!.GpuMilliseconds(window.Select(p.Pid)) is { } ms ? $"{ms:0.0}ms" : "-")}") ?? [];
            var rawPresents = events.Where(e => e.Kind == FrameTraceKind.Present).GroupBy(e => e.Pid)
                .Select(g => $"{g.Key}:{g.Count()}").OrderByDescending(s => s.Length).Take(8);
            Console.WriteLine($"  trace probe t+{second}s: pid {targetPid} frames {selection?.Count ?? 0} in {seconds:0.00}s " +
                $"(fps {(selection is { Count: > 0 } c ? c.Count / seconds : 0):0.#}), low1 {(low is { } l ? l.ToString("0.#") : "-")}, " +
                $"gpu {(gpu is { } g ? $"{g:0.00} ms" : "unknown")}, metadata {metadata}, dma {dma}, hags {hags}, input [{string.Join(" ", kinds)}] | " +
                $"window [{string.Join(" ", byPid)}] | raw present [{string.Join(" ", rawPresents)}]");
            if (window is not null && (selection?.Count ?? 0) > 0 && gpu is > 0) clean++;
        }

        for (int second = 1; second <= 8; second++) Window(second);
        check($"trace probe (pid {targetPid}): at least one clean window with frames and positive GPU over the first pass", clean > 0);

        // Stop: nothing is read once not wanted. Restart: the probe's timeline starts over (its first read
        // only primes), and the target's frames and GPU must come back within a few windows.
        rates.Want(false);
        check("trace probe: nothing is read once not wanted", !rates.Sample(Now()).HasValue);
        rates.Want(true);
        timeline = new FrameTimeline(Stopwatch.Frequency);
        clean = 0;
        session = 1; // the restart's events are marked so an offline replay can tell the sessions apart.
        for (int second = 1; second <= 4; second++) Window(second);
        check($"trace probe (pid {targetPid}): after stop and restart a clean window returns with positive GPU", clean > 0);
        trace?.Dispose();
    }

    // A synthetic regression for the overflow path: a batch that overflows must be dropped whole — the
    // new timeline must hold none of it, and the next ordinary batches must read normally. No real ETW
    // session is started: the fields are set through reflection and the restart condition is disarmed
    // (_restarted = now), so Sample only clears and primes.
    public static void OverflowRecovery(Action<string, bool> check)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(FrameRates);
        using var rates = new FrameRates();
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        type.GetField("_wanted", flags)!.SetValue(rates, true);
        type.GetField("_overflow", flags)!.SetValue(rates, true);
        type.GetField("_restarted", flags)!.SetValue(rates, now); // disarms the automatic restart
        type.GetField("_pending", flags)!.SetValue(rates, new List<FrameTraceEvent>
        {
            new(Stopwatch.GetTimestamp() + Stopwatch.Frequency, FrameTraceKind.Present, Pid: 1, Chain: 2), // a future present
            new(Stopwatch.GetTimestamp(), FrameTraceKind.DeviceStart, Device: 9, Adapter: 1),
            new(Stopwatch.GetTimestamp(), FrameTraceKind.ContextStart, Pid: 7, Context: 8, Device: 9, Node: 0),
            new(Stopwatch.GetTimestamp(), FrameTraceKind.QueueStart, Pid: 7, Context: 8, Sequence: 1),
        });
        check("overflow: the batch is dropped and the reading only primes", !rates.Sample(now).HasValue);
        var timeline = type.GetField("_timeline", flags)!.GetValue(rates)!;
        check("overflow: the new timeline holds nothing of the dropped batch",
            ((System.Collections.ICollection)(timeline.GetType().GetField("_pending", flags)!.GetValue(timeline) ?? throw new MissingMemberException("FrameTimeline._pending"))).Count == 0);
        check("overflow: the sampler's pending list is empty after the drop",
            ((System.Collections.ICollection)(type.GetField("_pending", flags)!.GetValue(rates) ?? throw new MissingMemberException("_pending"))).Count == 0);
        // The next ordinary batches read normally again: the first primes, the second reads.
        type.GetField("_pending", flags)!.SetValue(rates, new List<FrameTraceEvent>
        { new(Stopwatch.GetTimestamp(), FrameTraceKind.Present, Pid: Environment.ProcessId, Chain: 1) });
        _ = rates.Sample(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        type.GetField("_pending", flags)!.SetValue(rates, new List<FrameTraceEvent>
        { new(Stopwatch.GetTimestamp(), FrameTraceKind.Present, Pid: Environment.ProcessId, Chain: 1) });
        check("overflow: the next ordinary batches read normally", rates.Sample(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency).HasValue);
    }

    // A synthetic regression for the event decoder: hand-made EVENT_RECORDs are fed to OnEvent through
    // reflection (runtime events only — they need no TDH), and the sampler's pending list is read back.
    // Nothing here touches the timeline or Sample, so the normal probe's reading is never disturbed.
    public static void Decode(Action<string, bool> check)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var rates = new FrameRates();
        try
        {
            var type = typeof(FrameRates);
            var sessionType = type.GetNestedType("TraceSession", flags) ?? throw new MissingMemberException("TraceSession");
            int generation = (int)(type.GetField("_generation", flags)!.GetValue(rates) ?? 0);
            object Session(int g) => Activator.CreateInstance(sessionType, flags, null, [rates, g], null)!;
            using var session = (IDisposable)Session(generation);
            using var stale = (IDisposable)Session(generation - 1);
            type.GetField("_wanted", flags)!.SetValue(rates, true);
            var pending = (List<FrameTraceEvent>)(type.GetField("_pending", flags)!.GetValue(rates)
                ?? throw new MissingMemberException("_pending"));
            var onEvent = type.GetMethod("OnEvent", flags) ?? throw new MissingMemberException("OnEvent");
            type.GetField("_pending", flags)!.SetValue(rates, pending = []);

            void Feed(object traceSession, Guid provider, ushort id, byte[] payload, bool header64 = false)
            {
                // EVENT_HEADER, x64: flags@4, pid@12, time@16, provider@24, id@40, UserDataLength@86,
                // UserData.Ptr@96; the payload follows the 112-byte record.
                byte[] buffer = new byte[112 + payload.Length];
                BitConverter.GetBytes((short)(header64 ? 0x40 : 0)).CopyTo(buffer, 4);
                BitConverter.GetBytes(Environment.ProcessId).CopyTo(buffer, 12);
                BitConverter.GetBytes(1234L).CopyTo(buffer, 16);
                provider.ToByteArray().CopyTo(buffer, 24);
                BitConverter.GetBytes(id).CopyTo(buffer, 40);
                BitConverter.GetBytes((short)payload.Length).CopyTo(buffer, 86);
                payload.CopyTo(buffer, 112);
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var record = handle.AddrOfPinnedObject();
                    Marshal.WriteIntPtr(record, 96, record + 112); // UserData.Ptr is the payload's real address
                    onEvent.Invoke(rates, [record, traceSession]); // OnEvent(IntPtr record, TraceSession session)
                }
                finally { handle.Free(); }
            }
            int Events() => pending.Count;
            var dxgi = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
            var d3d9 = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");

            // DXGI 42, 32-bit header: chain is the payload's first dword.
            Feed(session, dxgi, 42, [5, 0, 0, 0, 0, 0, 0, 0]);
            check("decode: a DXGI present with a 32-bit header counts its chain", Events() == 1 && pending[^1].Chain == 5 && pending[^1].Kind == FrameTraceKind.Present);
            // DXGI 42, 64-bit header: the chain is a qword and the payload starts 8-byte aligned.
            Feed(session, dxgi, 42, [9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], header64: true);
            check("decode: a DXGI present with a 64-bit header reads the chain as a qword", Events() == 2 && pending[^1].Chain == 9);
            // DXGI_PRESENT_TEST (flag bit 1) asks, it does not present.
            Feed(session, dxgi, 42, [5, 0, 0, 0, 1, 0, 0, 0]);
            check("decode: a DXGI present with the TEST flag is not counted", Events() == 2);
            // A payload too short to hold chain and flags is dropped.
            Feed(session, dxgi, 42, [5, 0]);
            check("decode: a short payload is dropped", Events() == 2);
            // Only id 42 is a DXGI present.
            Feed(session, dxgi, 43, [5, 0, 0, 0, 0, 0, 0, 0]);
            check("decode: another DXGI id is not a present", Events() == 2);
            // D3D9 id 1 counts the chain, and its flag bits are not DXGI's TEST.
            Feed(session, d3d9, 1, [7, 0, 0, 0, 1, 0, 0, 0]);
            check("decode: a D3D9 present counts even with the bit-1 flag set", Events() == 3 && pending[^1].Chain == 7);
            Feed(session, d3d9, 2, [7, 0, 0, 0, 0, 0, 0, 0]);
            check("decode: another D3D9 id is not a present", Events() == 3);
            // A session of another generation is dropped at the door.
            Feed(stale, dxgi, 42, [5, 0, 0, 0, 0, 0, 0, 0]);
            check("decode: a stale generation's events are not counted", Events() == 3);
            Feed(session, dxgi, 42, [6, 0, 0, 0, 0, 0, 0, 0]);
            check("decode: the live generation still counts afterwards", Events() == 4 && pending[^1].Chain == 6);
        }
        finally { rates.Dispose(); }
    }

    // EVENT_TRACE_PROPERTIES (120 bytes on x64) with room for the name: Wnode.BufferSize at 0, Wnode.ClientContext at 40, Wnode.Flags
    // at 44, LogFileMode at 64 (real time), LoggerNameOffset at 116.
    private static IntPtr Properties()
    {
        const int Size = 120 + 512;
        IntPtr p = Marshal.AllocHGlobal(Size);
        for (int i = 0; i < Size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, Size); Marshal.WriteInt32(p, 40, 1); Marshal.WriteInt32(p, 44, 0x20000); Marshal.WriteInt32(p, 64, 0x100); Marshal.WriteInt32(p, 116, 120);
        return p;
    }
    private static bool Start(string name) { IntPtr p = Properties(); try { return StartTraceW(out _, name, p) == 0; } finally { Marshal.FreeHGlobal(p); } }
    private static bool Exists(string name) { IntPtr p = Properties(); try { return ControlTraceW(0, name, p, 0 /* query */) == 0; } finally { Marshal.FreeHGlobal(p); } }
    private static void Stop(string name) { IntPtr p = Properties(); try { ControlTraceW(0, name, p, 1); } finally { Marshal.FreeHGlobal(p); } }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint StartTraceW(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint ControlTraceW(ulong handle, string? name, IntPtr properties, uint code);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
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

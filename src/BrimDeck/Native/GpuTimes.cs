using System.Runtime.InteropServices;

namespace BrimDeck.Native;

// The GPU's running time per process, from the performance counters Task Manager's GPU page reads ("\GPU Engine(*)\RunningTime"):
// a wildcard PDH query collected on the sampler's thread, the cumulative engine time of every instance kept between two
// collections, so each reading takes the delta per process over the interval. Only the 3D engines count: the time a frame's draw
// calls execute. Nothing here throws out: a machine whose driver gives no counters reads nothing (a null GPU time), and the rest
// of the frame reading carries on.
internal sealed class GpuTimes : IDisposable
{
    private IntPtr _query;
    private IntPtr _counter;
    // The last collection's cumulative time per instance name (100 ns units), and the moment it was taken, so the first delta
    // waits for a second collection and a changed instance list does not invent time.
    private readonly Dictionary<string, long> _previous = [];
    private long _collectedAt;
    private bool _broken;

    // Collect now, and hand back each process's GPU running time in milliseconds since the previous collection. The first call
    // primes the query and returns nothing (a delta needs two collections); a collection that fails stops the counter quietly.
    public Dictionary<int, double> Collect()
    {
        if (_broken) return [];
        try
        {
            if (_query == IntPtr.Zero)
            {
                if (PdhOpenQueryW(null, 0, out _query) != 0) throw new GpuUnavailable("open");
                if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\RunningTime", 0, out _counter) != 0) throw new GpuUnavailable("add");
            }
            if (PdhCollectQueryData(_query) != 0) throw new GpuUnavailable("collect");
            long now = Environment.TickCount64;
            var current = Raw();
            var times = new Dictionary<int, double>();
            if (_collectedAt != 0 && now - _collectedAt >= 200)
                foreach (var (instance, time) in current)
                    if (_previous.TryGetValue(instance, out var before) && time > before && Pid(instance) is { } id)
                        times[id] = times.GetValueOrDefault(id) + (time - before) * 0.0001;
            _previous.Clear();
            foreach (var (instance, time) in current) _previous[instance] = time;
            _collectedAt = now;
            return times;
        }
        catch (GpuUnavailable)
        {
            _broken = true;
            Dispose();
            return [];
        }
    }

    // The raw values of the wildcard counter, one per instance: "pid_1234_luid_0x00000000_0x0000C3B7_phys_0_eng_0_engtype_3D".
    // Only the 3D engines: video decode and copy engines are not the time a frame's draw calls take. The buffer is a plain array
    // of PDH_FMT_COUNTERVALUE_ITEM_W (the name's pointer at 0, the counter's status at 8, its long long value at 16), grown when
    // PDH asks for more room.
    private Dictionary<string, long> Raw()
    {
        int size = 65_536;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint error = PdhGetRawCounterArrayW(_counter, ref size, out uint count, buffer);
                if (error == 0x800007D2 /* PDH_MORE_DATA */) continue;
                if (error != 0) throw new GpuUnavailable("array");
                var times = new Dictionary<string, long>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    IntPtr item = buffer + (IntPtr)(i * 24);
                    var text = Marshal.ReadIntPtr(item);
                    if (text == IntPtr.Zero) continue;
                    string instance = Marshal.PtrToStringUni(text) ?? "";
                    // The item's status: 0 valid, 1 new since the last collect; anything else is not a reading.
                    if ((uint)Marshal.ReadInt32(item, 8) > 1) continue;
                    if (instance.Contains("engtype_3D")) times[instance] = Marshal.ReadInt64(item, 16);
                }
                return times;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    // The process a counter instance belongs to, from "pid_1234_..."; null for the odd instance without one.
    private static int? Pid(string instance)
    {
        int start = instance.IndexOf("pid_") + 4;
        if (start < 4) return null;
        int end = instance.IndexOf('_', start);
        return end > start && int.TryParse(instance.AsSpan(start, end - start), out var pid) ? pid : null;
    }

    public void Dispose()
    {
        if (_counter != IntPtr.Zero) { PdhRemoveCounter(_counter); _counter = IntPtr.Zero; }
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    private sealed class GpuUnavailable(string step) : Exception(step);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, uint userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, uint userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetRawCounterArrayW(IntPtr counter, ref int size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhRemoveCounter(IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}

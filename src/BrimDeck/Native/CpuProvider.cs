using System.Diagnostics;
using System.Runtime.InteropServices;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Per logical processor: usage, and the current frequency against the highest seen so far. Usage is the share of time
// the processor was busy, which Task Manager shows on Windows 11 since 24H2; the processor utility it showed before
// scales that by the frequency and reads well above it while the cores boost. Without performance counters the same
// busy time comes from the kernel.
internal sealed class CpuProvider
{
    private const string BusyPath = @"\Processor Information(*)\% Processor Time";
    private const string PerformancePath = @"\Processor Information(*)\% Processor Performance";
    private const string BasePath = @"\Processor Information(*)\Processor Frequency";
    private readonly ICounterSource? _pdh;
    private readonly Func<(long Idle, long Kernel, long User)[]?> _kernelTimes;
    // The counters join the query in Attach, later than the constructor: the first counter a process adds takes half a second of CPU,
    // which the first readings need not wait for while the kernel's times give the same busy time.
    private bool _busy, _frequency, _attached;
    // Whether the busy-time counter gave readings last time; until it does, or once it stops, the kernel's times stand in.
    private bool _busyReads;
    private readonly Dictionary<(int Group, int Number), (int Group, CoreTier Tier)> _kinds = CpuTopology.Read();
    private readonly Dictionary<int, double> _highest = [];
    private ProcessorShares _shares = new();
    private (long Idle, long Kernel, long User)[] _previousTimes = [];
    private Reading<CpuSample> _last = Reading<CpuSample>.Unavailable(Why.Waiting);

    // kernelTimes: where the kernel's times come from (the tests give their own).
    public CpuProvider(ICounterSource? pdh, Func<(long Idle, long Kernel, long User)[]?>? kernelTimes = null)
    {
        _pdh = pdh;
        _kernelTimes = kernelTimes ?? KernelTimes;
    }

    // Whether the counters have joined the query (Attach); until then the readings come from the kernel's times alone.
    public bool Attached => _attached;

    // Adds the counters to the query and collects it once, so the next collection has a window to read. The readings go on from the
    // kernel's times until the busy-time counter gives its own: both are the share of time the processors were busy.
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _busy = _pdh?.Add(BusyPath) == true;
        _frequency = _pdh?.Add(PerformancePath) == true & _pdh?.Add(BasePath) == true;
        _pdh?.Collect();
    }

    public Reading<CpuSample> Sample(bool pdhCollected)
    {
        // The kernel's times are only the fallback; while the busy-time counter gives readings they are not read at all.
        var times = _busyReads ? null : _kernelTimes();
        var timeUsage = new Dictionary<int, double>();
        if (times is not null)
        {
            if (_previousTimes.Length == times.Length)
                for (int i = 0; i < times.Length; i++) if (MonitorMetrics.Usage(_previousTimes[i], times[i]) is { } usage) timeUsage[i] = usage;
            _previousTimes = times;
        }
        var busy = _shares.Fill(pdhCollected && _busy ? Processors(_pdh!.Read(BusyPath)) : []);
        _busyReads = busy.Count > 0;
        // While the counters give the readings the kernel's times are not read, so the baseline would grow old: it is dropped, and the
        // times that stand in once the counters stop start a window of their own (the last reading is kept until it has one).
        if (_busyReads) _previousTimes = [];
        IReadOnlyList<(string, double)> performanceValues = pdhCollected && _frequency ? _pdh!.Read(PerformancePath) : [];
        IReadOnlyList<(string, double)> nominalValues = pdhCollected && _frequency ? _pdh!.Read(BasePath) : [];
        var (performance, nominal) = (Processors(performanceValues), Processors(nominalValues));

        // The processors in their logical order. The kernel's times cover the first processor group only, so the counters
        // decide the list when they are there.
        var processors = busy.Count > 0 ? busy.Keys.OrderBy(key => key).ToList()
            : Enumerable.Range(0, times?.Length ?? 0).Select(index => (Group: 0, Number: index)).ToList();
        // A counter that gives nothing this once keeps the last reading, so the temperature that rides on it does not drop to a dash.
        if (processors.Count == 0 || busy.Count == 0 && timeUsage.Count == 0) return _last;
        var counters = new Dictionary<int, int>();
        var cores = new List<CoreSample>(processors.Count);
        for (int logical = 0; logical < processors.Count; logical++)
        {
            var key = processors[logical];
            var (group, tier) = _kinds.TryGetValue(key, out var kind) ? kind : (0, CoreTier.Unknown);
            double usage = busy.TryGetValue(key, out var percent) ? Math.Clamp(percent / 100, 0, 1)
                : timeUsage.TryGetValue(key.Number, out var kernelBusy) && key.Group == 0 ? kernelBusy : 0;
            Reading<double> frequency = Reading<double>.Unavailable(Missing);
            double? top = null;
            if (performance.TryGetValue(key, out var share) && nominal.TryGetValue(key, out var baseMhz) && baseMhz > 0)
            {
                double mhz = baseMhz * share / 100;
                frequency = mhz;
                // Turbo has no reported ceiling; the tick is drawn against the highest frequency this kind of core has reached.
                _highest[group] = Math.Max(_highest.GetValueOrDefault(group, baseMhz), mhz);
                top = _highest[group];
            }
            int number = counters[group] = counters.GetValueOrDefault(group) + 1;
            cores.Add(new CoreSample(logical, group, tier, number, usage, frequency, top));
        }
        cores.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Id.CompareTo(b.Id));
        var overall = Total(performanceValues) is { } p && Total(nominalValues) is { } n && n > 0 ? Reading<double>.Of(n * p / 100) : Reading<double>.Unavailable(Missing);
        return _last = new CpuSample(cores, overall);
    }

    // Why there is no frequency: still waiting for the counters to join, or they gave none.
    private string Missing => _frequency ? Why.NoFrequency : _attached ? Why.NoCounters : Why.Waiting;

    // The readings paused: the last reading is old and is not given again.
    public void Forget() => _last = Reading<CpuSample>.Unavailable(Why.Waiting);

    // The readings start again after the processor was not read for a while (the page stopped showing it): what the counters and the
    // kernel's times hold from before covers the whole time off, so one reading is made to set the baselines and is not given. The
    // next Sample then covers only the time since this one. pdhCollected: the query was collected for this reading.
    public void Prime(bool pdhCollected)
    {
        _previousTimes = [];
        // The kernel's times were not read while the counters gave readings; this one sets their baseline whatever the counters say now.
        _busyReads = false;
        Forget();
        Sample(pdhCollected);
        _shares = new ProcessorShares();
        Forget();
    }

    // "0,3" is processor 3 of group 0; "_Total" and "0,_Total" are sums.
    private static Dictionary<(int Group, int Number), double> Processors(IReadOnlyList<(string Instance, double Value)> values)
    {
        var result = new Dictionary<(int, int), double>();
        foreach (var (instance, value) in values)
        {
            int comma = instance.IndexOf(',');
            if (comma > 0 && int.TryParse(instance.AsSpan(0, comma), out int group) && int.TryParse(instance.AsSpan(comma + 1), out int number))
                result[(group, number)] = value;
        }
        return result;
    }
    private static double? Total(IReadOnlyList<(string Instance, double Value)> values)
        => values.Where(value => value.Instance == "_Total").Select(value => (double?)value.Value).FirstOrDefault();

    // Idle, kernel and user time of each processor of the current group.
    private static (long Idle, long Kernel, long User)[]? KernelTimes()
    {
        // The first processor group only (at most 64 logical processors); the counters, once they give readings, cover every group.
        int count = Math.Min(Environment.ProcessorCount, 64);
        const int Entry = 48;
        var buffer = Marshal.AllocHGlobal(count * Entry);
        try
        {
            if (NtQuerySystemInformation(8, buffer, count * Entry, out int length) != 0) return null;
            var times = new (long, long, long)[Math.Min(count, length / Entry)];
            for (int i = 0; i < times.Length; i++)
                times[i] = (Marshal.ReadInt64(buffer, i * Entry), Marshal.ReadInt64(buffer, i * Entry + 8), Marshal.ReadInt64(buffer, i * Entry + 16));
            return times;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returnLength);
}

// The kind of each logical processor from its efficiency class: a higher class is a faster core. Group 0 holds the
// fastest kind, as on the page; with one class every core is Unknown.
internal static class CpuTopology
{
    public static Dictionary<(int Group, int Number), (int Group, CoreTier Tier)> Read()
    {
        var classes = new Dictionary<(int, int), int>();
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint length, IntPtr.Zero, 0);
            if (length == 0) return [];
            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return [];
                for (int offset = 0; offset + 20 <= length;)
                {
                    int size = Marshal.ReadInt32(buffer, offset);
                    if (size <= 0) break;
                    // SYSTEM_CPU_SET_INFORMATION: Size, Type, then CpuSet { Id, Group (WORD), LogicalProcessorIndex, CoreIndex,
                    // LastLevelCacheIndex, NumaNodeIndex, EfficiencyClass, ... }.
                    if (Marshal.ReadInt32(buffer, offset + 4) == 0)
                        classes[(Marshal.ReadInt16(buffer, offset + 12), Marshal.ReadByte(buffer, offset + 14))] = Marshal.ReadByte(buffer, offset + 18);
                    offset += size;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        // Without the topology every core is drawn as one kind; it is never worth failing the page for.
        catch (Exception ex) when (ex is not OutOfMemoryException) { MonitorLog.Note("cpu topology", ex); return []; }
        var ranks = classes.Values.Distinct().OrderByDescending(value => value).ToList();
        return classes.ToDictionary(pair => pair.Key, pair =>
        {
            int group = ranks.IndexOf(pair.Value);
            var tier = ranks.Count < 2 ? CoreTier.Unknown : group == 0 ? CoreTier.Performance : group == ranks.Count - 1 ? CoreTier.Efficiency : CoreTier.Unknown;
            return (group, tier);
        });
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);
}

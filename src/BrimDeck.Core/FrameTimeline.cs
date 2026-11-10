namespace BrimDeck.Core;

// Normalized ETW records. Native decoding and trace generations stay outside the sampler's single-threaded timeline.
public enum FrameTraceKind { Present, DeviceStart, DeviceStop, ContextStart, ContextStop, HwQueueStart, QueueStart, QueueStop, DmaStart, DmaStop }
public readonly record struct FrameTraceEvent(long Time, FrameTraceKind Kind, int Pid = 0, long Chain = 0,
    ulong Context = 0, ulong Device = 0, ulong Adapter = 0, uint Node = 0, uint Sequence = 0, bool Wait = false);

public sealed record FrameSelection(int Pid, long Chain, long Count, IReadOnlyList<double> Gaps);

public sealed class FrameWindow(long from, long through, long frequency,
    Dictionary<(int Pid, long Chain), long> counts, Dictionary<(int Pid, long Chain), List<double>> gaps,
    Dictionary<int, long> busy)
{
    public long From { get; } = from;
    public long Through { get; } = through;
    public double Seconds => (Through - From) / (double)frequency;
    public IReadOnlyDictionary<(int Pid, long Chain), long> Counts => counts;

    // Parent-first matches the existing foreground policy; every consumer uses the one winning chain, even without gaps.
    public FrameSelection? Select(int owner, Func<int, bool>? descendant = null)
    {
        var own = SelectWhere(pid => pid == owner);
        return own ?? (descendant is null ? null : SelectWhere(descendant));
    }

    private FrameSelection? SelectWhere(Func<int, bool> accepts)
    {
        (int Pid, long Chain)? best = null;
        long most = 0;
        foreach (var (key, count) in counts)
            if (accepts(key.Pid) && (count > most || count == most && best is { } previous
                && (key.Pid < previous.Pid || key.Pid == previous.Pid && key.Chain < previous.Chain)))
            { best = key; most = count; }
        return best is { } picked
            ? new(picked.Pid, picked.Chain, most, gaps.TryGetValue(picked, out var values) ? values : Array.Empty<double>())
            : null;
    }

    // The scheduler measures a process, not a swap chain: use that PID once and the same frame denominator as FPS/lows.
    public double? GpuMilliseconds(FrameSelection? selection) => selection is { Count: > 0 } chosen
        && busy.TryGetValue(chosen.Pid, out long ticks)
        ? ticks * 1000.0 / frequency / chosen.Count : null;
}

public sealed class FrameTimeline
{
    private readonly long _frequency;
    private readonly GpuWork _gpu;
    private readonly List<(FrameTraceEvent Event, long Order)> _pending = [];
    private readonly Dictionary<(int Pid, long Chain), long> _last = [];
    private long _order;
    private long? _through;

    public FrameTimeline(long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _frequency = frequency; _gpu = new(frequency);
    }

    public void Add(FrameTraceEvent value) => _pending.Add((value, _order++));

    public void Reset()
    {
        _pending.Clear(); _last.Clear(); _gpu.Reset(); _through = null; _order = 0;
    }

    // A window is (previous cutoff, cutoff]. Truly late presents are dropped, never moved into the next interval.
    // Equal timestamps retain arrival order, so stop/start at one timestamp cannot swap their lifecycle.
    public FrameWindow? Read(long through)
    {
        if (_through is { } committed && through <= committed) return null;
        _pending.Sort((a, b) => { int time = a.Event.Time.CompareTo(b.Event.Time); return time != 0 ? time : a.Order.CompareTo(b.Order); });
        long from = _through ?? through;
        bool priming = _through is null;
        var counts = new Dictionary<(int Pid, long Chain), long>();
        var gaps = new Dictionary<(int Pid, long Chain), List<double>>();
        int consumed = 0;
        foreach (var (value, _) in _pending)
        {
            if (value.Time > through) break;
            consumed++;
            bool late = !priming && value.Time <= from;
            if (value.Kind != FrameTraceKind.Present)
            {
                // Scheduler metadata and packets: the priming window's events still build the device,
                // context and queue mappings (a rundown that arrives before the first reading must not
                // be thrown away); once a window is paid, a late event goes through Late instead.
                if (late) _gpu.Late(value); else _gpu.Apply(value);
                continue;
            }
            if (late || priming) continue;
            var key = (value.Pid, value.Chain);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (_last.TryGetValue(key, out long last))
            {
                double ms = (value.Time - last) * 1000.0 / _frequency;
                if (ms > 0 && ms < 1000)
                {
                    if (!gaps.TryGetValue(key, out var list)) gaps[key] = list = [];
                    list.Add(ms);
                }
            }
            _last[key] = value.Time;
        }
        _pending.RemoveRange(0, consumed);
        var busy = _gpu.Read(from, through);
        _through = through;
        // Older predecessors could only produce discarded suspend gaps. Retire them without touching active chains.
        foreach (var key in _last.Where(pair => through - pair.Value >= _frequency).Select(pair => pair.Key).ToArray()) _last.Remove(key);
        return priming ? null : new(from, through, _frequency, counts, gaps, busy);
    }
}

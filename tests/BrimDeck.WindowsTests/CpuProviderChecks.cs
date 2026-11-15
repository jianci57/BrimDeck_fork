using BrimDeck.Core;
using BrimDeck.Native;

// The processor's readings before and after the performance counters join (CpuProvider.Attach), with counters and kernel times that are
// given, so each state is the same on every run: the kernel's times give the first figures alone, the counters take over after Attach, and
// the times that stand in once the counters stop start a window of their own.
internal static class CpuProviderChecks
{
    private const string BusyPath = @"\Processor Information(*)\% Processor Time";

    private sealed class Counters : ICounterSource
    {
        public bool Collects = true;
        public Dictionary<string, List<(string, double)>> Values = [];
        public bool Add(string path) => true;
        public bool Collect() => Collects;
        public IReadOnlyList<(string Instance, double Value)> Read(string path) => Values.TryGetValue(path, out var list) ? list : [];
    }

    public static void Run(Action<string, bool> check)
    {
        var counters = new Counters();
        // Four processors; the kernel's times grow by the steps given (idle, kernel, user), as Windows counts them (idle is inside kernel).
        long idle = 0, kernel = 0, user = 0;
        int reads = 0;
        (long, long, long)[]? Times() { reads++; return [(idle, kernel, user), (idle, kernel, user), (idle, kernel, user), (idle, kernel, user)]; }
        void Advance(long busy, long free) { idle += free; kernel += busy + free; }
        var cpu = new CpuProvider(counters, Times);

        check("cpu: the counters are not in the query at construction", !cpu.Attached);
        Advance(50, 50);
        var first = cpu.Sample(false);
        Advance(25, 75);
        var second = cpu.Sample(false);
        check("cpu: the kernel's times give no figure at first and one after a window, without the counters",
            !first.HasValue && second.TryGet(out var fallback) && fallback.Cores.Count == 4 && fallback.Cores.All(core => Math.Abs(core.Usage - .25) < 1e-9));
        check("cpu: before the counters join the frequency is waiting, not missing", second.TryGet(out var waiting) && waiting.Cores.All(core => core.FrequencyMhz.Reason == Why.Waiting));

        cpu.Attach();
        counters.Values[BusyPath] = [("0,0", 40), ("0,1", 60)];
        int before = reads;
        var read = cpu.Sample(true);
        check("cpu: after the counters join the readings come from them (two processors), and the kernel's times are not read",
            cpu.Attached && read.TryGet(out var fromCounters) && fromCounters.Cores.Count == 2 && Math.Abs(fromCounters.Cores[0].Usage - .4) < 1e-9
            && Math.Abs(fromCounters.Cores[1].Usage - .6) < 1e-9 && reads == before + 1);
        // The first sample after Attach still read the times (the counters had given nothing before it); from here on they are left alone.
        before = reads;
        cpu.Sample(true); cpu.Sample(true);
        check("cpu: while the counters give readings the kernel's times are not read", reads == before);

        // The counters stop: the last reading is kept while the times take a baseline, and the first figure after covers only the time
        // since that baseline, not the stretch the counters had.
        Advance(900, 100);
        var kept = cpu.Sample(false);
        var keptAgain = cpu.Sample(false);
        check("cpu: when the counters stop the last reading is kept until the kernel's times have a baseline of their own",
            kept.TryGet(out var a) && keptAgain.TryGet(out var b) && ReferenceEquals(a, b) && a.Cores.Count == 2);
        Advance(20, 80);
        var resumed = cpu.Sample(false);
        check("cpu: the first figure from the kernel's times covers a fresh window, not the time the counters were giving the readings",
            resumed.TryGet(out var c) && c.Cores.Count == 4 && c.Cores.All(core => Math.Abs(core.Usage - .2) < 1e-9));

        // The processor was not read for a while and is read again (CpuProvider.Prime): the counters' and the times' windows from before
        // cover the whole time off, so the reading that sets the baselines is not given and the next one covers only the time since.
        var restartCounters = new Counters();
        var restart = new CpuProvider(restartCounters, Times);
        restart.Attach();
        restartCounters.Values[BusyPath] = [("0,0", 40), ("0,1", 60)];
        restart.Sample(true);
        // Over the time off the counters read an average of the whole stretch.
        restartCounters.Values[BusyPath] = [("0,0", 10), ("0,1", 10)];
        restart.Prime(true);
        check("cpu: after a restart the last reading is waiting, not the average over the time off", !restart.Sample(false).HasValue);
        restartCounters.Values[BusyPath] = [("0,0", 80), ("0,1", 90)];
        check("cpu: the first figure after a restart comes from the new window alone",
            restart.Sample(true).TryGet(out var fresh) && Math.Abs(fresh.Cores[0].Usage - .8) < 1e-9 && Math.Abs(fresh.Cores[1].Usage - .9) < 1e-9);
        restartCounters.Values[BusyPath] = [("0,0", 10), ("0,1", 10)];
        restart.Prime(true);
        restartCounters.Values.Remove(BusyPath);
        check("cpu: after a restart a counter that gives nothing does not bring back the shares read over the time off", !restart.Sample(true).HasValue);

        // The counters gave readings, stop, and the kernel's times come back: the baseline is set from the times themselves (a stale
        // "the counters are reading" does not make the next reading wait another interval).
        var swapCounters = new Counters();
        var swap = new CpuProvider(swapCounters, Times);
        swap.Attach();
        swapCounters.Values[BusyPath] = [("0,0", 40), ("0,1", 60)];
        swap.Sample(true);
        swapCounters.Values.Remove(BusyPath);
        Advance(100, 100);
        swap.Prime(true);
        Advance(20, 80);
        check("cpu: after counters that gave readings, a restart on the kernel's times covers the new window at the next reading",
            swap.Sample(true).TryGet(out var swapped) && swapped.Cores.All(core => Math.Abs(core.Usage - .2) < 1e-9));

        var offTimes = new CpuProvider(new Counters(), Times);
        Advance(50, 50); offTimes.Sample(false);
        Advance(25, 75); offTimes.Sample(false);
        // A long time off: the times moved far since the baseline.
        Advance(900, 100);
        offTimes.Prime(false);
        Advance(20, 80);
        check("cpu: kernel times: the first figure after a restart covers only the time since it, not the time off",
            offTimes.Sample(false).TryGet(out var window) && window.Cores.All(core => Math.Abs(core.Usage - .2) < 1e-9));

        // The real counters and times on this machine: only what holds whatever the load (structure, never values).
        using var pdh = new PdhQuery();
        var real = new CpuProvider(pdh);
        real.Sample(false);
        Thread.Sleep(250);
        var kernelRead = real.Sample(false);
        real.Attach();
        check("cpu: on this machine the kernel's times give one figure per processor with valid shares, and the counters can join",
            real.Attached && kernelRead.TryGet(out var live) && live.Cores.Count > 0 && live.Cores.All(core => core.Usage is >= 0 and <= 1));
    }
}

using System.Diagnostics;
using BrimDeck.Core;
using BrimDeck.Native;

// Reads this machine's power and battery the way the monitor page does, without a driver or administrator rights (read only). A
// machine may lack any of it (a desktop has no battery, an AMD or virtual machine no Energy Meter, no NVIDIA adapter), so the checks
// hold either way: whatever is read is plausible, an estimate is marked as one, nothing reads as zero, and a missing source stays
// missing. What was read is printed.
internal static class PowerChecks
{
    public static void Run(Action<string, bool> check)
    {
        Battery(check);
        Power(check);
    }

    private static void Battery(Action<string, bool> check)
    {
        var provider = new BatteryProvider();
        check("battery: unknown until read", provider.State == SensorState.Unknown);
        var (inputs, sample) = provider.Sample();
        bool system = BatteryProvider.SystemHasBattery;
        Console.WriteLine(sample.TryGet(out var b)
            ? $"  battery {b.Percent:0.0}% {b.State}, rate {b.Watts:0.0} W{(b.HasRate ? "" : " (none)")}, left {b.TimeLeft}, to full {b.TimeToFull}, health {(b.Health is { } h ? h.ToString("0.00") : "?")}, {b.Count} battery(ies), mode {b.Mode}, saver {b.BatterySaver}, mains {b.AcOnline}"
            : $"  battery: none ({sample.Reason}); system says battery: {system}");
        check("battery: the system and the batteries agree on whether there is one", sample.HasValue == system || !system && provider.State == SensorState.None);
        check("battery: the state is known after a read", provider.State is SensorState.Found or SensorState.None);
        check("battery: a desktop has none and says why", sample.HasValue || sample.Reason.Length > 0);
        if (b is null) return;
        check("battery: the share is a percentage", b.Percent is >= 0 and <= 100);
        check("battery: health is a share or none", b.Health is null or (>= 0 and <= 1));
        check("battery: capacities are plausible or none", b.FullWh is null or (> 0 and < 2000) && b.RemainingWh is null or (>= 0 and < 2000));
        check("battery: a time is set only for the state it belongs to", (b.TimeLeft is null || b.State == BatteryState.Discharging) && (b.TimeToFull is null || b.State == BatteryState.Charging));
        check("battery: the cells were handed over", inputs.Cells.Count > 0);
        // Reading again changes nothing but the readings; the batteries are not listed again every time.
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) provider.Sample();
        Console.WriteLine($"  battery: {watch.Elapsed.TotalMilliseconds / 20:0.00} ms a reading");
        check("battery: a reading is cheap", watch.Elapsed.TotalMilliseconds / 20 < 50);
    }

    // The NVIDIA helper (nvidia-smi) runs only while an adapter works and power is wanted, and ends otherwise.
    private static void Helper(List<GpuSample> adapters, Reading<double> discharge, bool battery, Action<string, bool> check)
    {
        // Only the helpers this process started: a BrimDeck running meanwhile has one of its own.
        int self = Environment.ProcessId;
        List<int> Own() => [.. BrimDeck.Native.ProcessSnapshot.Read().Where(entry => entry.Value.Parent == self
            && string.Equals(entry.Value.File, "nvidia-smi.exe", StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key)];
        int Running() => Own().Count;
        using var pdh = new PdhQuery();
        using var provider = new PowerProvider(pdh, () => { });
        var idle = adapters.Select(adapter => adapter with { Usage = 0, EngineUsage = 0 }).ToList();
        double clock = 200;
        for (int i = 0; i < 3; i++) { provider.Sample(provider.Prepare(true), true, clock++, idle, discharge, battery); Thread.Sleep(300); }
        check("power: no helper while the NVIDIA adapter is idle", Running() == 0);
        // nvidia-smi takes a second or more to start on some machines: the readings go on until it shows, for at most a few seconds.
        bool Started()
        {
            var waited = Stopwatch.StartNew();
            do { provider.Sample(provider.Prepare(true), true, clock++, adapters, discharge, battery); Thread.Sleep(500); }
            while (Running() != 1 && waited.ElapsedMilliseconds < 8000);
            return Running() == 1;
        }
        check("power: the helper runs while the NVIDIA adapter works", Started());
        // Ended from outside while the adapter still works: it is started again once the interval has passed, and not before.
        foreach (var id in Own()) { using var process = Process.GetProcessById(id); process.Kill(); process.WaitForExit(2000); }
        Thread.Sleep(300);
        check("power: the helper is gone when it is ended from outside", Running() == 0);
        provider.Sample(provider.Prepare(true), true, clock + 5, adapters, discharge, battery);
        Thread.Sleep(300);
        check("power: an ended helper is not started again at once", Running() == 0);
        clock += PowerResolver.RestartSeconds + 5;
        check("power: ...but again after the interval, while the adapter works", Started());
        provider.Sample(provider.Prepare(false), false, clock++, adapters, discharge, battery);
        Thread.Sleep(500);
        check("power: the helper ends when power is no longer wanted", Running() == 0);
        for (int i = 0; i < 2; i++) { provider.Sample(provider.Prepare(true), true, clock++, adapters, discharge, battery); Thread.Sleep(300); }
        provider.Sample(provider.Prepare(true), true, clock += 20, idle, discharge, battery);
        Thread.Sleep(500);
        check("power: the helper ends once the adapter has been idle for a while", Running() == 0);
    }

    private static void Power(Action<string, bool> check)
    {
        // The meter in a query of its own, as the sampler keeps it.
        using var pdh = new PdhQuery();
        using var provider = new PowerProvider(pdh, () => { });
        check("power: unknown until asked", provider.State == SensorState.Unknown);
        var battery = new BatteryProvider();
        var (inputs, sample) = battery.Sample();
        var discharge = BatteryModel.Discharge(inputs, sample);
        // The adapters the monitor would hand over: the first NVIDIA adapter, busy by Windows' own figure (EngineUsage, which decides
        // whether the helper runs), so it is read.
        var adapters = GpuAdapters.Read().Adapters.Values.Where(adapter => !adapter.Software && adapter.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            .Select(adapter => new GpuSample(.5, Reading<ulong>.Unavailable("")) { Name = adapter.Name, EngineUsage = .5 }).ToList();
        check("power: nothing is read while it is not wanted", !provider.Sample(provider.Prepare(false), false, 0, adapters, discharge, sample.HasValue).HasValue);
        // The counters join the query on the first ask and give readings from the second collection (a rate needs two).
        provider.Sample(provider.Prepare(true), true, 1, adapters, discharge, sample.HasValue);
        Thread.Sleep(1100);
        provider.Sample(provider.Prepare(true), true, 2, adapters, discharge, sample.HasValue);
        Thread.Sleep(1100);
        var read = provider.Sample(provider.Prepare(true), true, 3, adapters, discharge, sample.HasValue);
        Console.WriteLine(read.TryGet(out var power)
            ? $"  power {power.Watts:0.0} W {power.Origin} from {power.Source}: {string.Join(", ", power.Parts.Select(part => $"{part.Name} {part.Watts:0.0} W ({part.Source})"))}{(power.Missing.Count > 0 ? "; missing: " + string.Join("; ", power.Missing) : "")}"
            : $"  power: none ({read.Reason})");
        check("power: a reading is plausible or has a reason", read.TryGet(out var p) ? PowerResolver.Plausible(p.Watts) && p.Watts > 0 : read.Reason.Length > 0);
        check("power: an estimate is marked and is never a measurement of a source it is not", p is null || (p.Estimated == (p.Source == PowerSource.Components)));
        check("power: parts are plausible", p is null || p.Parts.All(part => PowerResolver.Plausible(part.Watts) && part.Name.Length > 0 && part.Source.Length > 0));
        check("power: its state follows the reading", read.HasValue ? provider.State == SensorState.Found : provider.State != SensorState.Found);
        if (adapters.Count > 0)
            check("power: an NVIDIA adapter in use gives its board power, or says why not", p is null || p.Parts.Any(part => part.Source == PowerResolver.GpuSource) || p.Missing.Count > 0 || !read.HasValue);
        check("power: nothing stays read once it is not wanted", !provider.Sample(provider.Prepare(false), false, 4, [], discharge, false).HasValue);
        // After the reading above has ended its own helper, so the helper checks count only theirs.
        if (adapters.Count > 0) Helper(adapters, discharge, sample.HasValue, check);
        // Idle adapters are not asked: a sleeping discrete adapter would wake up.
        var idle = adapters.Select(adapter => adapter with { Usage = 0, EngineUsage = 0 }).ToList();
        using var coldPdh = new PdhQuery();
        using var cold = new PowerProvider(coldPdh, () => { });
        cold.Sample(cold.Prepare(true), true, 100, idle, discharge, false);
        Thread.Sleep(1100);
        var quiet = cold.Sample(cold.Prepare(true), true, 101, idle, discharge, false);
        check("power: an idle adapter is not read", !quiet.TryGet(out var q) || q.Parts.All(part => part.Source != PowerResolver.GpuSource));
    }
}

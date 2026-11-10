using System.Diagnostics;
using System.Security.Principal;
using BrimDeck.Core;
using BrimDeck.Native;

// Reads this machine's temperature sensors the way the system monitor does, without a driver or administrator rights. A machine
// may lack any of them (no ACPI thermal zone, an adapter without a sensor, no solid-state drive), so the checks hold either way:
// whatever is read is plausible, nothing reads as zero, and a missing sensor stays missing. What was read is printed.
internal static class TemperatureChecks
{
    public static void Run(Action<string, bool> check)
    {
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Console.WriteLine($"Temperatures read {(elevated ? "with" : "without")} administrator rights.");
        Zones(check);
        using var pdh = new PdhQuery();
        Adapters(pdh, check);
        Drives(check, elevated);
    }

    // The zones in a query of their own, as the sampler keeps them.
    private static void Zones(Action<string, bool> check)
    {
        using var pdh = new PdhQuery();
        var zones = new ThermalZones(pdh, () => { });
        check("thermal zones: unknown until asked", zones.State == SensorState.Unknown);
        var watch = Stopwatch.StartNew();
        var cpu = zones.Sample(true, 0);
        Console.WriteLine($"  thermal zones: added and collected in {watch.Elapsed.TotalMilliseconds:0.0} ms");
        Thread.Sleep(1100);
        cpu = zones.Sample(true, 1) ?? cpu;
        Console.WriteLine(cpu is { } zone ? $"  CPU {zone.Celsius:0.0} °C from {zone.Zone}" : $"  CPU: no ACPI thermal zone gives a temperature on this machine ({zones.State})");
        check("thermal zones: the processor's temperature is plausible or none", cpu is null || Temperatures.Plausible(cpu.Value.Celsius) is not null);
        // A zone counts once it has moved (SteadyZones): until then, or for good on a machine whose zones hold still, it may stay Unknown.
        check("thermal zones: found once a temperature is read", cpu is null ? zones.State != SensorState.Found : zones.State == SensorState.Found);
        check("thermal zones: none once not wanted, and the counters leave the query", zones.Sample(false, 3) is null && pdh.Read(@"\Thermal Zone Information(*)\Temperature").Count == 0);
    }

    private static void Adapters(PdhQuery pdh, Action<string, bool> check)
    {
        // The adapters by the key the counters name them with, as GpuProvider finds them.
        const string Memory = @"\GPU Adapter Memory(*)\Dedicated Usage";
        pdh.Add(Memory); pdh.Collect();
        var keys = pdh.Read(Memory).Select(item => item.Instance).Select(instance => instance.IndexOf("_phys", StringComparison.Ordinal) is var end and > 0 ? instance[..end] : instance)
            .Where(key => AdapterLuid.Parse(key) is not null).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        using var temperatures = new GpuTemperatures(() => { });
        // Named by their keys here; the sampler names them by the adapters' names.
        var names = keys.ToDictionary(key => key, key => key, StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        foreach (var key in keys)
        {
            var first = temperatures.Read(key, key, 0);
            var soon = temperatures.Read(key, key, 1);
            Console.WriteLine(first is { } celsius ? $"  GPU {key}: {celsius:0.0} °C" : $"  GPU {key}: no temperature");
            check($"graphics adapter {key}: plausible or none", first is null || Temperatures.Plausible(first.Value) is not null);
            check($"graphics adapter {key}: one without a temperature is not asked again at once", first is not null || soon is null);
            if (first is not null) found.Add(key);
        }
        if (keys.Count > 0)
        {
            // Whichever adapter is the busiest, the temperature comes from one with a sensor and stays with it.
            var readings = keys.Select(shown => temperatures.ReadFor(shown, keys, names, 2)).ToList();
            check("graphics adapters: an adapter without a sensor does not take the temperature away", found.Count == 0
                ? readings.All(reading => reading is null) && temperatures.State != SensorState.Found
                : readings.All(reading => reading is { } r && found.Contains(r.Adapter)) && temperatures.State == SensorState.Found);
            var without = keys.Except(found).ToList();
            if (found.Count > 0 && without.Count > 0)
                check("graphics adapters: the adapter that last gave one keeps it", without.All(shown => temperatures.ReadFor(shown, keys, names, 3)?.Adapter == temperatures.ReadFor(without[0], keys, names, 3)?.Adapter));
        }
        // An adapter not in use is never asked for another's sake: shown alone, an adapter without a sensor reads nothing even
        // after another gave a temperature, so a powered-down dedicated adapter is left asleep.
        foreach (var without in keys.Except(found))
            check($"graphics adapter {without}: an idle adapter is not asked for its sake", temperatures.ReadFor(without, [without], names, 10) is null);
        check("graphics adapters: an unknown key reads nothing", temperatures.Read("luid_0x7FFFFFFF_0x7FFFFFFF", "unknown", 0) is null && temperatures.Read("not an adapter", "unknown", 0) is null);
        Console.WriteLine($"  {keys.Count} adapter(s) listed, {found.Count} with a temperature");
    }

    private static void Drives(Action<string, bool> check, bool elevated)
    {
        var watch = Stopwatch.StartNew();
        var volumes = StorageReader.Volumes();
        Console.WriteLine($"  {volumes.Count} fixed volume(s), listed in {watch.Elapsed.TotalMilliseconds:0} ms");
        foreach (var (volume, disk) in volumes)
            Console.WriteLine($"  Volume {volume.Letter} \"{volume.Label}\": {volume.Used / 1073741824d:0.0} / {volume.Total / 1073741824d:0.0} GB on drive {disk?.ToString() ?? "?"}");
        check("volumes: each has a letter and a size, its free space within it", volumes.All(item => item.Volume.Letter.EndsWith(':') && item.Volume.Total > 0 && item.Volume.Free <= item.Volume.Total));

        // The sampler's reader never waits for the drives: its first sample is empty, and the readings follow on the thread pool.
        // The checks wait for that reading itself, however long the drives take.
        var reader = new StorageReader();
        check("drives: unknown until read", reader.Drives == SensorState.Unknown && reader.Temperatures == SensorState.Unknown && reader.Count == -1);
        check("drives: the first sample does not wait for the drives", reader.Sample(0, true).Count == 0);
        reader.Pending.Wait();
        var drives = reader.Sample(1, true);
        foreach (var drive in drives)
            Console.WriteLine($"  Drive {drive.Number} {drive.Model}: {drive.Letters}, {drive.Used / 1073741824d:0.0} / {drive.Total / 1073741824d:0.0} GB"
                + (drive.Temperature is { } heat ? $", {heat.Celsius:0} °C (warning {heat.Warning?.ToString() ?? "-"}, critical {heat.Critical?.ToString() ?? "-"})" : ", no temperature"));
        check("drives: counted and known", reader.Count == Storage.VolumeCount(drives) && reader.Drives == (drives.Count > 0 ? SensorState.Found : SensorState.None));
        check("drives: each volume on one drive only, the drive's volumes in letter order",
            drives.SelectMany(drive => drive.Volumes).Select(volume => volume.Letter).Distinct(StringComparer.OrdinalIgnoreCase).Count() == drives.Sum(drive => drive.Volumes.Count)
            && drives.All(drive => drive.Volumes.Select(volume => volume.Letter).SequenceEqual(drive.Volumes.Select(volume => volume.Letter).Order(StringComparer.OrdinalIgnoreCase))));
        var placed = volumes.Where(item => item.Disk is not null && item.Volume.Total >= Storage.SmallestVolume).ToList();
        check("drives: a listed volume sits on the drive its extents name", drives.All(drive => drive.Number < 0
            || drive.Volumes.All(volume => placed.Any(item => item.Volume.Letter == volume.Letter && item.Disk == drive.Number))));
        check("drives: small volumes are left out", drives.SelectMany(drive => drive.Volumes).All(volume => volume.Total >= Storage.SmallestVolume));
        check("drives: the Windows drive is among them", drives.Count == 0 || Storage.System(drives, Storage.WindowsLetter)?.Volumes.Any(volume => volume.Letter.Equals(Storage.WindowsLetter, StringComparison.OrdinalIgnoreCase)) == true);
        check("drives: temperatures plausible, in order, and their drive's own", drives.All(drive => drive.Temperature is not { } heat
            || Temperatures.Plausible(heat.Celsius) is not null && heat.Number == drive.Number && (heat.Warning is not { } w || heat.Critical is not { } c || w <= c)));
        // The health log stands in where the temperature property gives none; where both give one, they agree.
        foreach (var drive in drives.Where(drive => drive.Number >= 0 && drive.Temperature is not null))
        {
            var logged = StorageReader.HealthLog(drive.Number);
            Console.WriteLine($"  Drive {drive.Number} health log: {logged?.ToString("0") ?? "none"} °C");
            check($"drives: drive {drive.Number}'s health log agrees with its temperature property", logged is not { } c || Math.Abs(c - drive.Temperature!.Celsius) <= 3);
        }
        check("drives: temperatures known once read",reader.Temperatures == (drives.Any(drive => drive.Temperature is not null) ? SensorState.Found : SensorState.None));
        if (drives.Count > 0 && !elevated) check("drives: read without administrator rights", true);
        check("drives: without temperatures asked for, the same drives without them", reader.Sample(2, false) is var plain
            && plain.Count == drives.Count && plain.All(drive => drive.Temperature is null));
        check("drives: temperatures older than twice their interval are not shown", reader.Sample(1e9, true).All(drive => drive.Temperature is null));
        reader.Pending.Wait();
        reader.Rest();
        check("drives: the readings stay while the sampler pauses", reader.Sample(3, true).Count == drives.Count);
        reader.Pending.Wait();
        check("drives: read again on the next sample after a pause", reader.Sample(4, true).Count == drives.Count);
        var early = new StorageReader();
        early.Start(true); early.Start(true);
        early.Pending.Wait();
        check("drives: started early, one reading at a time", early.Sample(0, true).Count == drives.Count);
    }
}

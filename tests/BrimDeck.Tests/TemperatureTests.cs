using BrimDeck.Core;

// The system monitor's temperatures: unit conversions, sensors that report nothing or nonsense, the thermal zone chosen for the
// processor, the adapters' LUIDs, the levels, and the temperature reading of the collapsed row.
static class TemperatureTests
{
    public static void Run(Action<string, bool> check)
    {
        Conversions(check);
        Zones(check);
        Drives(check);
        Luids(check);
        Levels(check);
        Snapshots(check);
        Strip(check);
    }

    private static bool Near(double? value, double expected) => value is { } v && Math.Abs(v - expected) < 1e-9;

    private static void Conversions(Action<string, bool> check)
    {
        check("Temperatures: kelvin to Celsius", Near(Temperatures.FromKelvin(320.15), 47));
        check("Temperatures: tenths of a kelvin to Celsius", Near(Temperatures.FromDeciKelvin(3231.5), 50));
        check("Temperatures: the graphics adapter's tenths of a degree", Near(Temperatures.FromDeciCelsius(473), 47.3));
        check("Temperatures: an adapter reporting zero has no sensor", Temperatures.FromDeciCelsius(0) is null);
        check("Temperatures: a zone at 0 K or below freezing is no reading", Temperatures.FromKelvin(0) is null && Temperatures.FromKelvin(273.15) is null
            && Temperatures.FromDeciKelvin(0) is null);
        check("Temperatures: nonsense is no reading", Temperatures.Plausible(-273) is null && Temperatures.Plausible(200) is null
            && Temperatures.Plausible(double.NaN) is null && Temperatures.Plausible(double.PositiveInfinity) is null && Temperatures.FromDeciCelsius(uint.MaxValue) is null);
        check("Temperatures: the range's ends are readings", Near(Temperatures.Plausible(Temperatures.Lowest), 1) && Near(Temperatures.Plausible(Temperatures.Highest), 125));
        check("Temperatures: whole degrees, halves away from zero", Temperatures.Format(47.3) == "47" && Temperatures.Format(47.5) == "48" && Temperatures.Format(99.96) == "100");
    }

    private static void Zones(Action<string, bool> check)
    {
        check("Thermal zones: none gives no processor temperature", Temperatures.Cpu([], []) is null);
        check("Thermal zones: zones reporting 0 K give none", Temperatures.Cpu([(@"\_TZ.TZ00", 0)], [(@"\_TZ.TZ00", 0), (@"\_TZ.TZ01", 0)]) is null);
        var hottest = Temperatures.Cpu([], [(@"\_TZ.TZ00", 300.15), (@"\_TZ.TZ01", 335.15)]);
        check("Thermal zones: without a processor zone the hottest wins", hottest is { Zone: @"\_TZ.TZ01" } && Near(hottest?.Celsius, 62));
        var named = Temperatures.Cpu([], [(@"\_TZ.TZ00", 345.15), (@"\_TZ.CPUZ", 330.15)]);
        check("Thermal zones: a zone named for the processor wins over a hotter one", named is { Zone: @"\_TZ.CPUZ" } && Near(named?.Celsius, 57));
        var precise = Temperatures.Cpu([(@"\_TZ.TZ00", 3254.5)], [(@"\_TZ.TZ00", 325)]);
        check("Thermal zones: the high precision counter wins over the whole kelvin one", Near(precise?.Celsius, 52.3));
        var mixed = Temperatures.Cpu([(@"\_TZ.TZ00", 0)], [(@"\_TZ.TZ00", 320.15)]);
        check("Thermal zones: a broken precise counter falls back to the whole kelvin one", Near(mixed?.Celsius, 47));
        var ties = Temperatures.Cpu([], [(@"\_TZ.TZ01", 320.15), (@"\_TZ.TZ00", 320.15)]);
        check("Thermal zones: equal zones choose the same one every time", ties is { Zone: @"\_TZ.TZ00" });

        // A zone the firmware fills with a fixed value (27.8 °C here) never shows; a zone shows once its value has changed, and
        // drops out again after ten minutes without change.
        var steady = new SteadyZones();
        Dictionary<string, double> Read(double fixedZone, double moving) => new() { [@"\_TZ.TZ00"] = fixedZone, [@"\_TZ.TZ01"] = moving };
        check("Steady zones: nothing shows before a zone has moved", steady.Choose(Read(27.8, 25), 0) is null);
        check("Steady zones: a moving zone shows, the fixed one never does", steady.Choose(Read(27.8, 25.1), 1) is { Zone: @"\_TZ.TZ01", Celsius: 25.1 }
            && steady.Choose(Read(27.8, 25.2), SteadyZones.SteadySeconds + 5) is { Zone: @"\_TZ.TZ01", Celsius: 25.2 });
        check("Steady zones: the fixed zone shows once it moves, and wins as the hotter", steady.Choose(Read(28.1, 25.3), SteadyZones.SteadySeconds + 6) is { Zone: @"\_TZ.TZ00", Celsius: 28.1 });
        check("Steady zones: a zone still for ten minutes after moving drops out again", steady.Choose(Read(28.1, 25.4), 2 * SteadyZones.SteadySeconds + 6) is { Zone: @"\_TZ.TZ01", Celsius: 25.4 });
        var only = new SteadyZones();
        only.Choose(new Dictionary<string, double> { [@"\_TZ.TZ00"] = 27.8 }, 0);
        check("Steady zones: with only a fixed zone the processor never shows a temperature", only.Choose(new Dictionary<string, double> { [@"\_TZ.TZ00"] = 27.8 }, 1) is null
            && only.Choose(new Dictionary<string, double> { [@"\_TZ.TZ00"] = 27.8 }, 10 * SteadyZones.SteadySeconds) is null);
        check("Steady zones: a zone that comes back starts again, unmoved", only.Choose(new Dictionary<string, double>(), 11 * SteadyZones.SteadySeconds) is null
            && only.Choose(new Dictionary<string, double> { [@"\_TZ.TZ00"] = 27.8 }, 11 * SteadyZones.SteadySeconds + 1) is null
            && only.Choose(new Dictionary<string, double> { [@"\_TZ.TZ00"] = 28 }, 11 * SteadyZones.SteadySeconds + 2) is { Celsius: 28 });
        check("Zones: none gives no pick", Temperatures.Pick([]) is null && Temperatures.Zones([], []).Count == 0);
    }

    private static void Drives(Action<string, bool> check)
    {
        var nvme = Temperatures.Disk(" YMTC YMSS2ED06D25MC ", 44, 80, 85);
        check("Drives: a drive's reading with its own thresholds, its name trimmed", nvme is { Name: "YMTC YMSS2ED06D25MC", Celsius: 44, Warning: 80, Critical: 85 });
        check("Drives: a drive reporting 0 °C or −273 °C has no reading", Temperatures.Disk("SSD", 0, 80, 85) is null && Temperatures.Disk("SSD", -273, 0, 0) is null);
        var none = Temperatures.Disk("SSD", 40, 0, -274);
        check("Drives: thresholds the drive does not give are left out", none is { Warning: null, Critical: null });
        var swapped = Temperatures.Disk("SSD", 40, 90, 70);
        check("Drives: thresholds out of order are left out", swapped is { Warning: null, Critical: null });
    }

    private static void Luids(Action<string, bool> check)
    {
        check("Adapter LUID: from the counters' key", AdapterLuid.Parse("luid_0x00000000_0x00017550") == (0x17550u, 0));
        check("Adapter LUID: the high part comes first", AdapterLuid.Parse("luid_0x00000001_0x0000C5E2") == (0xC5E2u, 1));
        check("Adapter LUID: a high part with the top bit set", AdapterLuid.Parse("luid_0xFFFFFFFF_0x00000001") == (1u, -1));
        check("Adapter LUID: anything else is none", AdapterLuid.Parse(null) is null && AdapterLuid.Parse("") is null && AdapterLuid.Parse("luid_0x12") is null
            && AdapterLuid.Parse("pid_12_luid_0x0_0x1") is null && AdapterLuid.Parse("luid_0xZZ_0x01") is null && AdapterLuid.Parse("luid_00000000_00017550") is null);
    }

    private static void Levels(Action<string, bool> check)
    {
        check("Temperature levels: the processor", Temperatures.OfCpu(84.9) == MemoryLevel.Normal && Temperatures.OfCpu(85) == MemoryLevel.Elevated && Temperatures.OfCpu(95) == MemoryLevel.Critical);
        check("Temperature levels: the graphics adapter", Temperatures.OfGpu(79) == MemoryLevel.Normal && Temperatures.OfGpu(80) == MemoryLevel.Elevated && Temperatures.OfGpu(90) == MemoryLevel.Critical);
        check("Temperature levels: a drive by its own thresholds", Temperatures.OfDisk(new("SSD", 79, 80, 85)) == MemoryLevel.Normal
            && Temperatures.OfDisk(new("SSD", 80, 80, 85)) == MemoryLevel.Elevated && Temperatures.OfDisk(new("SSD", 85, 80, 85)) == MemoryLevel.Critical);
        check("Temperature levels: a drive without thresholds by the defaults", Temperatures.OfDisk(new("SSD", 59)) == MemoryLevel.Normal
            && Temperatures.OfDisk(new("SSD", 60)) == MemoryLevel.Elevated && Temperatures.OfDisk(new("SSD", 70)) == MemoryLevel.Critical);
        check("Temperature levels: one threshold sets the other", Temperatures.OfDisk(new("SSD", 85, Warning: 80)) == MemoryLevel.Elevated
            && Temperatures.OfDisk(new("SSD", 90, Warning: 80)) == MemoryLevel.Critical && Temperatures.OfDisk(new("SSD", 75, Critical: 80)) == MemoryLevel.Elevated);
    }

    private static MonitorSnapshot Snapshot(double? cpu, double? gpu, bool gpuRead = true) =>
        MonitorSnapshot.Empty(8, "test") with
        {
            Cpu = new CpuSample([new CoreSample(0, 0, CoreTier.Unknown, 1, .2, Reading<double>.Unavailable(""), null)], Reading<double>.Unavailable("")) { Temperature = cpu },
            Gpu = gpuRead ? new GpuSample(.1, Reading<ulong>.Unavailable("")) { Temperature = gpu } : Reading<GpuSample>.Unavailable("no counters")
        };

    private static void Snapshots(Action<string, bool> check)
    {
        check("Ring temperatures: none while there is none", CompactFigures.Heat(CompactMonitorItems.Cpu, null) is null && CompactFigures.Heat(CompactMonitorItems.Cpu, Snapshot(null, null)) is null
            && CompactFigures.Heat(CompactMonitorItems.Gpu, MonitorSnapshot.Empty(4, "waiting")) is null && CompactFigures.Heat(CompactMonitorItems.Memory, Snapshot(60, 60)) is null);
        check("Ring temperatures: each its own, at its own level", CompactFigures.Heat(CompactMonitorItems.Cpu, Snapshot(61, null, gpuRead: false)) is { Celsius: 61, Level: MemoryLevel.Normal }
            && CompactFigures.Heat(CompactMonitorItems.Gpu, Snapshot(null, 47.3)) is { Celsius: 47.3 } && CompactFigures.Heat(CompactMonitorItems.Cpu, Snapshot(96, 40)) is { Level: MemoryLevel.Critical }
            && CompactFigures.Heat(CompactMonitorItems.Gpu, Snapshot(96, 82)) is { Level: MemoryLevel.Elevated });
    }

    // The temperatures show with CPU, GPU and the drives, as their second figure (CompactFigures), not as a reading of their own.
    private static void Strip(Action<string, bool> check)
    {
        const CompactMonitorItems Temperature = CompactMonitorItems.Temperature, Cpu = CompactMonitorItems.Cpu, Gpu = CompactMonitorItems.Gpu, Storage_ = CompactMonitorItems.Storage;
        var settings = SettingsMigrations.Read("""{"AppVersion":"1.2.0","CompactReadings":[{"Item":64,"Visible":true},{"Item":2,"Visible":true}]}""");
        check("Compact temperature: a table saved with it drops it and keeps the rest in order", settings.CompactReadings.All(r => r.Item != Temperature)
            && settings.CompactReadings[0] is { Item: Cpu, Visible: true } && CompactMonitor.Visible(settings.CompactReadings) == Cpu);

        check("Compact figures: CPU, GPU and the drives have them, the others not", CompactFigures.Has(Cpu) && CompactFigures.Has(Gpu) && CompactFigures.Has(Storage_)
            && !CompactFigures.Has(CompactMonitorItems.Memory) && !CompactFigures.Has(CompactMonitorItems.Download));
        check("Compact figures: the share in use by default", CompactFigures.Of(new CompactReading { Item = Cpu }).SequenceEqual([CompactFigures.Usage]));
        check("Compact figures: a choice in order, each once, without unknown ones", CompactFigures.Normalize(Gpu, ["temperature", "Usage", "x", null, "Temperature"])!.SequenceEqual([CompactFigures.Usage, CompactFigures.Temperature])
            && CompactFigures.Normalize(Cpu, ["Temperature"])!.SequenceEqual([CompactFigures.Temperature]) && CompactFigures.Normalize(Cpu, ["x"]) is null
            && CompactFigures.Normalize(CompactMonitorItems.Memory, ["Temperature"]) is null);
        var saved = SettingsMigrations.Read("""{"AppVersion":"1.2.0","CompactReadings":[{"Item":2,"Figures":["Temperature","Usage"]},{"Item":4,"Figures":["Temperature"]}]}""");
        check("Compact figures: a saved choice is kept, and dropped where a reading has none", saved.CompactReadings.Single(r => r.Item == Cpu).Figures!.SequenceEqual([CompactFigures.Usage, CompactFigures.Temperature])
            && saved.CompactReadings.Single(r => r.Item == CompactMonitorItems.Memory).Figures is null);
        var copy = saved.CompactReadings.Single(r => r.Item == Cpu).Copy();
        copy.Figures!.Clear();
        check("Compact figures: a copied reading has its own choice", saved.CompactReadings.Single(r => r.Item == Cpu).Figures!.Count == 2);
        var inLine = SettingsMigrations.Read("""{"AppVersion":"1.2.0","CompactReadings":[{"Item":2,"FiguresInLine":true},{"Item":4,"FiguresInLine":true},{"Item":128}]}""");
        check("Compact figures: one above the other by default, side by side kept where a reading has figures", saved.CompactReadings.All(r => !r.FiguresInLine)
            && inLine.CompactReadings.Single(r => r.Item == Cpu).FiguresInLine && !inLine.CompactReadings.Single(r => r.Item == CompactMonitorItems.Memory).FiguresInLine
            && !inLine.CompactReadings.Single(r => r.Item == Storage_).FiguresInLine);
        var on = SettingsMigrations.Read("""{"AppVersion":"1.2.0","MonitorTemperatures":true}""");
        check("Temperatures setting: saved on stays on", on.MonitorTemperatures);
    }
}

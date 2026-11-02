using BrimDeck.Core;

// Every graphics adapter read on its own, and the one the monitor shows (issue #80). The instance names and ids are those of a
// laptop with an Intel integrated GPU and an NVIDIA RTX 4060 Laptop GPU.
internal static class GpuAdapterTests
{
    private const ulong Nvidia = 0x1795E, Intel = 0xC5E2, Warp = 0x1A557, Npu = 0x2B001;

    public static void Run(string root, Action<string, bool> check)
    {
        Parsing(check);
        Totals(check);
        Drivers(check);
        Choice(check);
        Settings(root, check);
    }

    private static void Parsing(Action<string, bool> check)
    {
        check("GPU LUID: from an engine instance", GpuReadings.Luid("pid_11904_luid_0x00000000_0x0001795E_phys_0_eng_0_engtype_3D") == Nvidia);
        check("GPU LUID: from an adapter memory instance", GpuReadings.Luid("luid_0x00000000_0x0000C5E2_phys_0") == Intel);
        check("GPU LUID: the high part comes first", GpuReadings.Luid("luid_0x00000001_0x00000002_phys_0") == (1UL << 32 | 2));
        check("GPU LUID: without the low part's end", GpuReadings.Luid("luid_0x00000000_0x0000C5E2") == Intel);
        check("GPU LUID: none in a name without one, or a broken one", GpuReadings.Luid("_Total") is null && GpuReadings.Luid("luid_0xZZ_0x01") is null
            && GpuReadings.Luid("luid_0x00000000") is null);
        check("GPU LUID: written back as the counters write it", GpuReadings.LuidText(Nvidia) == "luid_0x00000000_0x0001795E" && GpuReadings.Luid(GpuReadings.LuidText(1UL << 40 | 7)) == (1UL << 40 | 7));
        check("GPU kind: the system's word first, else the memory carve-out", GpuReadings.KindOf(true, 8UL << 30) == GpuKind.Integrated
            && GpuReadings.KindOf(false, 128UL << 20) == GpuKind.Discrete && GpuReadings.KindOf(null, 128UL << 20) == GpuKind.Integrated
            && GpuReadings.KindOf(null, 512UL << 20) == GpuKind.Integrated && GpuReadings.KindOf(null, 8UL << 30) == GpuKind.Discrete);
        var keyed = GpuReadings.Keyed([
            new GpuAdapter(9, "NVIDIA GeForce RTX 4090", 0x10DE, 0x2684, 0x16F310DE, 24UL << 30, GpuKind.Discrete),
            new GpuAdapter(5, "NVIDIA GeForce RTX 4090", 0x10DE, 0x2684, 0x16F310DE, 24UL << 30, GpuKind.Discrete),
            new GpuAdapter(7, "Virtual GPU", 0, 0, 0, 0, GpuKind.Integrated), new GpuAdapter(8, "", 0, 0, 0, 0, GpuKind.Unknown)]);
        check("GPU key: the ids outlive the LUID; identical cards by their order", keyed.First(a => a.Luid == 5).Key == "10DE:2684:16F310DE"
            && keyed.First(a => a.Luid == 9).Key == "10DE:2684:16F310DE#2");
        check("GPU key: the name, or the LUID, without ids", keyed.First(a => a.Luid == 7).Key == "Virtual GPU" && keyed.First(a => a.Luid == 8).Key == GpuReadings.LuidText(8));
    }

    // The counters of the laptop: two processes on the NVIDIA adapter, the desktop on the Intel one, the software rasterizer, and
    // an NPU, which has counters but is no graphics adapter.
    private static Dictionary<ulong, GpuTotals> Counters(GpuCounters counters, double nvidia3D = 30, double intel3D = 55) => counters.Sum(
        [
            ("pid_100_luid_0x00000000_0x0001795E_phys_0_eng_0_engtype_3D", nvidia3D / 2), ("pid_200_luid_0x00000000_0x0001795E_phys_0_eng_0_engtype_3D", nvidia3D / 2),
            ("pid_100_luid_0x00000000_0x0001795E_phys_0_eng_2_engtype_VideoDecode", 40), ("pid_100_luid_0x00000000_0x0001795E_phys_0_eng_3_engtype_Compute_0", 5),
            ("pid_100_luid_0x00000000_0x0001795E_phys_0_eng_4_engtype_Compute_1", 10), ("pid_300_luid_0x00000000_0x0000C5E2_phys_0_eng_0_engtype_3D", intel3D),
            ("pid_300_luid_0x00000000_0x0000C5E2_phys_0_eng_1_engtype_Copy", 3), ("pid_300_luid_0x00000000_0x0001A557_phys_0_eng_0_engtype_3D", 90),
            ("pid_400_luid_0x00000000_0x0002B001_phys_0_eng_0_engtype_Compute", 70), ("_Total", 12)
        ],
        [("luid_0x00000000_0x0001795E_phys_0", 1_180_934_144), ("luid_0x00000000_0x0000C5E2_phys_0", 0), ("luid_0x00000000_0x0001A557_phys_0", 0)],
        [("luid_0x00000000_0x0001795E_phys_0", 142_532_608), ("luid_0x00000000_0x0000C5E2_phys_0", 600_000_000)]);

    private static IReadOnlyDictionary<ulong, GpuAdapter> Described(ulong nvidia = Nvidia, ulong intel = Intel) => GpuReadings.Keyed([
        new GpuAdapter(nvidia, "NVIDIA GeForce RTX 4060 Laptop GPU", 0x10DE, 0x28E0, 0x3D6217AA, 7956UL << 20, GpuKind.Discrete),
        new GpuAdapter(intel, "Intel(R) UHD Graphics", 0x8086, 0xA788, 0x3D6217AA, 128UL << 20, GpuKind.Integrated),
        new GpuAdapter(Warp, "Microsoft Basic Render Driver", 0x1414, 0x008C, 0, 0, GpuKind.Integrated, Software: true)]).ToDictionary(a => a.Luid);

    private static void Totals(Action<string, bool> check)
    {
        var counters = new GpuCounters();
        var totals = Counters(counters);
        check("GPU totals: each adapter on its own, processes summed", totals[Nvidia].Engines["3D"] == 30 && totals[Intel].Engines["3D"] == 55 && totals.Count == 4);
        check("GPU totals: an adapter's usage is its busiest engine type", totals[Nvidia].Usage == 40 && totals[Intel].Usage == 55);
        check("GPU totals: memory per adapter, unlisted is unknown", totals[Nvidia].Dedicated == 1_180_934_144 && totals[Intel].Shared == 600_000_000 && totals[Warp].Shared is null);
        var again = Counters(counters, nvidia3D: 80);
        check("GPU totals: a new reading starts from nothing", again[Nvidia].Engines["3D"] == 80 && again[Nvidia].Usage == 80);
        var copies = counters.Sum([("pid_1_luid_0x00000000_0x0001795E_phys_0_eng_3_engtype_Copy", 35), ("pid_2_luid_0x00000000_0x0001795E_phys_0_eng_3_engtype_Copy", 25),
            ("pid_1_luid_0x00000000_0x0001795E_phys_0_eng_4_engtype_Copy", 50), ("pid_1_luid_0x00000000_0x0001795E_phys_1_eng_3_engtype_Copy", 20)], [], []);
        check("GPU totals: engines of one type are read apart, the busiest kept (Task Manager)", copies[Nvidia].Engines["Copy"] == 60 && copies[Nvidia].Usage == 60);

        var samples = GpuReadings.Samples(totals, Described(), described: true, "no memory");
        check("GPU samples: the software rasterizer and the NPU are left out", samples.Count == 2 && samples.All(s => s.Luid is Nvidia or Intel));
        check("GPU samples: discrete first, then integrated", samples[0].Kind == GpuKind.Discrete && samples[1].Kind == GpuKind.Integrated);
        var nvidia = samples[0];
        check("GPU samples: named, sized and keyed from the description", nvidia.Name == "NVIDIA GeForce RTX 4060 Laptop GPU" && nvidia.DedicatedTotal == 7956UL << 20
            && nvidia.Key == "10DE:28E0:3D6217AA" && nvidia.Usage == .4 && nvidia.EngineUsage == .4 && nvidia.UsageSource is null && nvidia.VendorId == 0x10DE);
        check("GPU samples: engines folded by kind, as Task Manager", nvidia.Engines.Select(e => e.Kind).SequenceEqual(["3D", "Compute", "VideoDecode"])
            && nvidia.Engines[1].Usage == .1);
        check("GPU samples: each adapter's own memory", nvidia.DedicatedBytes.OrDefault == 1_180_934_144 && samples[1].SharedBytes.OrDefault == 600_000_000
            && samples[1].DedicatedBytes.TryGet(out var none) && none == 0);
        var unnamed = GpuReadings.Samples(totals, new Dictionary<ulong, GpuAdapter>(), described: false, "no memory");
        check("GPU samples: without a description every adapter with counters is kept, unnamed", unnamed.Count == 4 && unnamed.All(s => s.Name is null && s.Kind == GpuKind.Unknown)
            && unnamed.Any(s => s.Key == GpuReadings.LuidText(Npu)));
        var partial = Counters(new GpuCounters());
        partial[Nvidia].Dedicated = null;
        check("GPU samples: memory the driver does not report is unavailable, not zero",
            !GpuReadings.Samples(partial, Described(), true, "no memory")[0].DedicatedBytes.HasValue);
    }

    private static void Drivers(Action<string, bool> check)
    {
        var discrete = new GpuSample(.05, 0UL) { EngineUsage = .05, VendorId = GpuDriverReadings.Nvidia, Kind = GpuKind.Discrete, Luid = Nvidia };
        var read = GpuDriverReadings.Apply(discrete, 31, 42.5);
        check("GPU driver: the driver's usage is shown, Windows' kept beside it", read.Usage == .31 && read.EngineUsage == .05 && read.UsageSource == "NVIDIA 驱动" && read.DriverWatts == 42.5);
        check("GPU driver: without a reading the busiest engine stays", GpuDriverReadings.Apply(discrete, null, null) == discrete
            && GpuDriverReadings.Apply(discrete, 250, null).Usage == 1);
        var integrated = discrete with { VendorId = GpuDriverReadings.Amd, Kind = GpuKind.Integrated };
        check("GPU driver: an integrated adapter's power is the processor's, never added again", GpuDriverReadings.Apply(integrated, 12, 8).DriverWatts is null
            && GpuDriverReadings.Apply(integrated, 12, 8).UsageSource == "AMD 驱动");
        check("GPU driver: a temperature is kept, a zero or absurd one is none", GpuDriverReadings.Apply(discrete, null, null, 61).DriverTemperature == 61
            && GpuDriverReadings.Apply(discrete, null, null, 0).DriverTemperature is null && GpuDriverReadings.Apply(discrete, null, null, 900).DriverTemperature is null);
        check("GPU driver: rates from growing counters", GpuDriverReadings.Rate(10, 100, 10.5, 101) == .5 && GpuDriverReadings.Rate(10, 100, 9, 101) is null
            && GpuDriverReadings.Rate(10, 100, 11, 100) is null);
        check("GPU driver: the bus of a location", GpuDriverReadings.Bus("1:0.0") == 1 && GpuDriverReadings.Bus("10:2.1") == 10 && GpuDriverReadings.Bus(null) is null && GpuDriverReadings.Bus("x") is null);

        var gate = new GpuDriverGate();
        var idle = discrete with { EngineUsage = 0 };
        check("GPU driver: an adapter never seen working is not asked (it may be asleep)", !gate.Awake(idle, 0));
        check("GPU driver: one working is asked, and for a while after", gate.Awake(discrete, 1) && gate.Awake(idle, 1 + GpuDriverGate.AwakeSeconds)
            && !gate.Awake(idle, 2 + GpuDriverGate.AwakeSeconds));
        check("GPU driver: an adapter without readings is not asked", !gate.Awake(discrete with { NoReadings = true }, 0));
        gate.Awake(discrete, 50);
        gate.Keep([]);
        check("GPU driver: adapters that went away are forgotten", !gate.Awake(idle, 51));
    }

    private static List<GpuSample> Laptop(double nvidia3D = 30, double intel3D = 55, ulong nvidia = Nvidia, ulong intel = Intel)
    {
        var totals = new GpuCounters().Sum(
            [($"pid_1_{GpuReadings.LuidText(nvidia)}_phys_0_eng_0_engtype_3D", nvidia3D), ($"pid_2_{GpuReadings.LuidText(intel)}_phys_0_eng_0_engtype_3D", intel3D)], [], []);
        return GpuReadings.Samples(totals, Described(nvidia, intel), true, "");
    }

    private static void Choice(Action<string, bool> check)
    {
        var busyIntel = Laptop(nvidia3D: 2, intel3D: 60);
        check("GPU auto: the discrete adapter, though the integrated one is busier", GpuChoice.Auto(busyIntel, 0)?.Luid == Nvidia);
        bool steady = true;
        ulong shown = 0;
        foreach (var (n, i) in new[] { (0.0, 80.0), (90, 5), (1, 99), (50, 50), (0, 0) })
        {
            var gpu = GpuChoice.Auto(Laptop(n, i), shown)!;
            steady &= gpu.Luid == Nvidia; shown = gpu.Luid;
        }
        check("GPU auto: stays on one adapter whatever the load", steady);

        var integrated = GpuReadings.Samples(new GpuCounters().Sum([("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 10),
            ("pid_1_luid_0x00000000_0x00000002_phys_0_eng_0_engtype_3D", 50)], [], []), new Dictionary<ulong, GpuAdapter>
        {
            [1] = new(1, "Intel(R) Arc(TM) Graphics", 0x8086, 1, 0, 128UL << 20, GpuKind.Integrated) { Key = "a" },
            [2] = new(2, "Microsoft Hyper-V Video", 0x1414, 2, 0, 0, GpuKind.Integrated) { Key = "b" }
        }, true, "");
        var first = GpuChoice.Auto(integrated, 0);
        check("GPU auto: without a discrete adapter, the busiest", first?.Luid == 2);
        var later = GpuReadings.Samples(new GpuCounters().Sum([("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 90),
            ("pid_1_luid_0x00000000_0x00000002_phys_0_eng_0_engtype_3D", 1)], [], []), integrated.ToDictionary(s => s.Luid, s => new GpuAdapter(s.Luid, s.Name!, 0, 0, 0, 0, s.Kind) { Key = s.Key }), true, "");
        check("GPU auto: ... and then the same one, though another became busier", GpuChoice.Auto(later, first!.Luid)?.Luid == 2);
        check("GPU auto: the largest discrete adapter", GpuChoice.Auto([new GpuSample(0, 0UL) { Luid = 1, Kind = GpuKind.Discrete, DedicatedTotal = 8 },
            new GpuSample(.9, 0UL) { Luid = 2, Kind = GpuKind.Discrete, DedicatedTotal = 24 }, new GpuSample(1, 0UL) { Luid = 3, Kind = GpuKind.Integrated }], 1)?.Luid == 2);
        check("GPU auto: nothing to show without adapters", GpuChoice.Auto([], 0) is null && GpuChoice.Resolve([], "10DE:28E0:3D6217AA", null, 0, out _) is null);

        const string IntelKey = "8086:A788:3D6217AA";
        check("GPU choice: the saved adapter, though it is idle", GpuChoice.Resolve(Laptop(90, 0), IntelKey, "Intel(R) UHD Graphics", Nvidia, out bool kept)?.Luid == Intel && !kept);
        var rebooted = Laptop(nvidia: 0x2A001, intel: 0x2A002);
        check("GPU choice: found again by its key after the LUIDs changed", GpuChoice.Saved(rebooted, IntelKey, null)?.Luid == 0x2A002);
        check("GPU choice: found by the LUID it was saved under, for an adapter without ids",
            GpuChoice.Saved(Laptop(), GpuReadings.LuidText(Intel), null)?.Luid == Intel);
        check("GPU choice: found by name when its ids changed", GpuChoice.Saved(Laptop(), "8086:FFFF:00000000", "Intel(R) UHD Graphics")?.Luid == Intel);
        check("GPU choice: 自动 while the saved adapter is not there", GpuChoice.Saved(Laptop(), "1002:73BF:00000000", "AMD Radeon RX 6800 XT") is null
            && GpuChoice.Resolve(Laptop(0, 99), "1002:73BF:00000000", "AMD Radeon RX 6800 XT", 0, out bool missing)?.Luid == Nvidia && missing);
        check("GPU choice: none is 自动", GpuChoice.Resolve(Laptop(0, 99), null, null, 0, out bool automatic)?.Luid == Nvidia && automatic);
        var both = Laptop();
        check("GPU choice: the next adapter, round to the first", GpuChoice.Next(both, both[0])?.Luid == Intel && GpuChoice.Next(both, both[1])?.Luid == Nvidia
            && GpuChoice.Next(both, new GpuSample(0, 0UL) { Luid = 42 })?.Luid == Nvidia);
    }

    private static void Settings(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "gpu-choice");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "settings.json");
        File.WriteAllText(file, "{ \"AppVersion\": \"1.2.0\", \"MonitorMain\": 1 }");
        var old = new SettingsStore(folder, "1.2.0").Load();
        check("GPU settings: a file from before the choice reads as 自动", old.MonitorGpu is null && old.MonitorGpuName is null && old.MonitorMain == MonitorMain.Gpu);
        var store = new SettingsStore(folder, "1.2.0");
        var settings = store.Load();
        settings.MonitorGpu = "8086:A788:3D6217AA"; settings.MonitorGpuName = "Intel(R) UHD Graphics";
        store.Save(settings);
        var read = new SettingsStore(folder, "1.2.0").Load();
        check("GPU settings: the choice is saved and read back", read.MonitorGpu == "8086:A788:3D6217AA" && read.MonitorGpuName == "Intel(R) UHD Graphics");
        check("GPU settings: Copy keeps the choice", read.Copy() is { MonitorGpu: "8086:A788:3D6217AA", MonitorGpuName: "Intel(R) UHD Graphics" });
        var blank = new DeckSettings { MonitorGpu = "  ", MonitorGpuName = "Intel(R) UHD Graphics" };
        blank.Normalize();
        check("GPU settings: a blank choice is 自动, without a name", blank.MonitorGpu is null && blank.MonitorGpuName is null);
    }
}

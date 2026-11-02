using BrimDeck.Core;

// The system monitor's pure logic. The SteadyRanking cases are ported from ftop's tests by Nongfsq
// (https://github.com/Nongfsq/ftop, MIT License; see src/BrimDeck/ThirdPartyNotices.txt).
static class SystemMonitorTests
{
    public static void Run(Action<string, bool> check)
    {
        Readings(check);
        Metrics(check);
        Folding(check);
        Ranking(check);
        Formatting(check);
        Layouts(check);
        Compact(check);
        Pages(check);
        Colors(check);
        Motion(check);
        Grips(check);
    }

    // A build that does not know a setting (another build of the same version) keeps it when it saves; an older version's
    // unknown settings are left to the migrations. Saving in two steps writes what Save writes.
    public static void Store(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "store-unknown");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "settings.json");
        File.WriteAllText(file, "{ \"AppVersion\": \"1.1.1\", \"PanelTheme\": \"dracula\", \"Width\": 600 }");
        var store = new SettingsStore(folder, "1.1.1");
        var settings = store.Load();
        store.Save(settings);
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        check("Settings: a key this build does not know survives a save", saved["PanelTheme"]?.GetValue<string>() == "dracula" && saved["Width"]?.GetValue<double>() == 600);
        File.WriteAllText(file, "{ \"AppVersion\": \"1.0.0\", \"RetiredSetting\": true }");
        store = new SettingsStore(folder, "1.1.1");
        store.Save(store.Load());
        saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        check("Settings: an older version's unknown key is not carried forward", !saved.ContainsKey("RetiredSetting"));
        var text = store.Serialize(new DeckSettings { Width = 700 });
        store.Write(text);
        check("Settings: Serialize then Write saves the settings", File.ReadAllText(file) == text && new SettingsStore(folder, "1.1.1").Load().Width == 700);
    }

    private static void Readings(Action<string, bool> check)
    {
        Reading<double> known = 2.5, missing = Reading<double>.Unavailable("no sensor");
        check("Monitor reading keeps its value", known.TryGet(out var value) && value == 2.5 && known.Reason == "");
        check("Monitor unavailable reading keeps its reason and no value", !missing.TryGet(out _) && missing.Reason == "no sensor" && missing.OrDefault == 0);
        check("Monitor default reading is unavailable, not zero", !default(Reading<double>).HasValue && default(Reading<double>).Reason.Length > 0);
        check("Monitor reading maps its value or keeps the reason", known.Map(x => x * 2).OrDefault == 5 && missing.Map(x => x * 2).Reason == "no sensor");
    }

    private static void Metrics(Action<string, bool> check)
    {
        check("Monitor processor usage from idle, kernel and user time", MonitorMetrics.Usage((0, 0, 0), (25, 50, 50)) is { } usage && Math.Abs(usage - .75) < 1e-9);
        check("Monitor processor usage skips an interval without time", MonitorMetrics.Usage((10, 10, 10), (10, 10, 10)) is null);
        check("Monitor processor usage skips counters that went back", MonitorMetrics.Usage((50, 100, 100), (10, 20, 20)) is null);
        check("Monitor rate per second", MonitorMetrics.Rate(1000, 3000, 2) == 1000);
        check("Monitor rate skips a counter reset", MonitorMetrics.Rate(3000, 1000, 1) is null && MonitorMetrics.Rate(1, 2, 0) is null);
        check("Monitor process CPU in percent of one core", MonitorMetrics.CpuPercent(0, 25_000_000, 1) == 250);
        var memory = new MemorySample(100, 50, 40, 100);
        check("Monitor memory level normal", memory.Level == MemoryLevel.Normal && memory.UsedFraction == .5);
        check("Monitor memory level elevated by use or commit", (memory with { Used = 85 }).Level == MemoryLevel.Elevated && (memory with { CommitUsed = 82 }).Level == MemoryLevel.Elevated);
        check("Monitor memory level critical", (memory with { Used = 95 }).Level == MemoryLevel.Critical && (memory with { CommitUsed = 95 }).Level == MemoryLevel.Critical);
        check("Monitor memory without a total reads zero shares", new MemorySample(0, 0, 0, 0) is { UsedFraction: 0, CommitFraction: 0 });
        var core = new CoreSample(0, 0, CoreTier.Performance, 1, .5, 2000d, 4000);
        check("Monitor frequency fraction", core.FrequencyFraction == .5 && (core with { FrequencyMhz = Reading<double>.Unavailable("x") }).FrequencyFraction is null
            && (core with { MaxFrequencyMhz = 0 }).FrequencyFraction is null && (core with { FrequencyMhz = 9000d }).FrequencyFraction == 1);
        var cpu = new CpuSample([core, core with { Id = 1, Group = 1, Usage = .1 }, core with { Id = 2, Group = 1, Usage = .3 }], 3000d);
        check("Monitor CPU usage is the cores' mean", Math.Abs(cpu.Usage - .3) < 1e-9 && cpu.GroupCount == 2 && cpu.Groups.Select(g => g.Count).SequenceEqual([1, 2]));
        check("Monitor CPU without cores", new CpuSample([], 0d) is { Usage: 0, GroupCount: 0, Groups.Count: 0 });
    }

    private static void Folding(Action<string, bool> check)
    {
        ProcessSample[] list =
        [
            new(1, "Chrome", 10, 300, "chrome.exe"), new(2, "Chrome", 30, 100, "CHROME.EXE"), new(3, "Code", 25, 900, "code.exe"),
            new(4, "System", 1, 50), new(5, "Chrome", 5, 200, "chrome.exe")
        ];
        var byCpu = ProcessSample.Folded(list, ProcessSort.Cpu);
        check("Monitor folding sums an app's processes", byCpu[0] is { Pid: 2, CpuPercent: 45, MemoryBytes: 600, Members.Count: 3 });
        check("Monitor folding ranks by CPU", byCpu.Select(p => p.Pid).SequenceEqual([2, 3, 4]));
        check("Monitor folded members are heaviest first", byCpu[0].Members.Select(m => m.CpuPercent).SequenceEqual([30d, 10, 5]));
        check("Monitor a lone process keeps no members", byCpu[1].Members.Count == 0 && byCpu[2].Id == "pid 4");
        var byMemory = ProcessSample.Folded(list, ProcessSort.Memory);
        check("Monitor folding ranks by memory", byMemory.Select(p => p.Pid).SequenceEqual([3, 1, 4]) && byMemory[1].MemoryBytes == 600);
        check("Monitor folding an empty list", ProcessSample.Folded([], ProcessSort.Cpu).Count == 0);
        var lists = new ProcessList(byCpu, byMemory);
        check("Monitor process list picks the ranking", lists.Ranked(ProcessSort.Memory) == byMemory && lists.Ranked(ProcessSort.Cpu) == byCpu);
    }

    private static void Ranking(Action<string, bool> check)
    {
        static IReadOnlyList<(string, double)> E(params (string, double)[] entries) => entries;
        var ranking = SteadyRanking.Cpu();
        bool steady = ranking.Step(E(("a", 20), ("b", 19), ("c", 5)), 0).SequenceEqual(["a", "b", "c"]);
        for (int turn = 0; turn < 20; turn++)
        {
            double first = turn % 2 == 0 ? 18 : 21;
            steady &= ranking.Step(E(("a", first), ("b", 39 - first), ("c", 5)), 1).SequenceEqual(["a", "b", "c"]);
        }
        check("Monitor ranking: close readings do not swap places", steady);

        ranking = SteadyRanking.Cpu();
        ranking.Step(E(("a", 20), ("b", 10), ("c", 5)), 0);
        bool lead = ranking.Step(E(("a", 20), ("b", 10), ("c", 60)), 1).SequenceEqual(["c", "a", "b"]);
        lead &= ranking.Step(E(("a", 20), ("b", 10), ("c", 5)), 1).SequenceEqual(["c", "a", "b"]);
        IReadOnlyList<string> order = [];
        for (int i = 0; i < 20; i++) order = ranking.Step(E(("a", 20), ("b", 10), ("c", 5)), 1);
        check("Monitor ranking: a clear lead takes its place at once and leaves slowly", lead && order.SequenceEqual(["a", "b", "c"]));

        ranking = SteadyRanking.Cpu();
        ranking.Step(E(("a", 20), ("b", 10)), 0);
        bool held = ranking.Step(E(("c", 90), ("b", 80), ("a", 1)), 1, hold: true).SequenceEqual(["a", "b", "c"]);
        held &= ranking.Step(E(("c", 90), ("b", 80)), 1, hold: true).SequenceEqual(["b", "c"]);
        held &= ranking.Step(E(("c", 90), ("b", 40)), 1).SequenceEqual(["c", "b"]);
        check("Monitor ranking: a held ranking keeps its order and puts arrivals last", held);

        ranking = SteadyRanking.Cpu();
        ulong seed = 7;
        double Next() { seed = unchecked(seed * 6_364_136_223_846_793_005 + 1_442_695_040_888_963_407); return (seed >> 40) % 60; }
        bool complete = true;
        for (int round = 0; round < 50; round++)
        {
            var entries = Enumerable.Range(0, 24).Select(i => ("p" + i, Next())).ToList();
            var result = ranking.Step(entries, 1);
            complete &= result.Count == entries.Count && result.ToHashSet().SetEquals(entries.Select(e => e.Item1));
        }
        var still = Enumerable.Range(0, 24).Select(i => ("p" + i, i * 4d)).ToList();
        for (int i = 0; i < 60; i++) order = ranking.Step(still, 1);
        var values = order.Select(id => double.Parse(id[1..]) * 4).ToList();
        bool settled = values.Select((value, index) => values.Take(index).All(above => value <= above * (1 + ranking.Relative) + ranking.Margin)).All(ok => ok);
        check("Monitor ranking: no entry stays below one it clearly beats", complete && settled);

        var samples = new List<ProcessSample> { new(1, "a", 5, 0, "a"), new(2, "b", 50, 0, "b") };
        var arranged = SteadyRanking.Cpu().Arrange(samples, s => s.CpuPercent, 0, false);
        check("Monitor ranking arranges samples", arranged.Select(s => s.Pid).SequenceEqual([2, 1]));
    }

    private static void Formatting(Action<string, bool> check)
    {
        check("Monitor percent", MonitorFormat.Percent(.374) == "37" && MonitorFormat.Percent(1.4) == "100" && MonitorFormat.Percent(double.NaN) == "0");
        check("Monitor process share", MonitorFormat.ProcessShare(250, 10) == "25" && MonitorFormat.ProcessShare(12, 8) == "1.5"
            && MonitorFormat.ProcessShare(5000, 8) == "100" && MonitorFormat.ProcessShare(5, 0) == "5.0");
        check("Monitor rates", MonitorFormat.Rate(84_200) == new MonitorFormat.Quantity("84", "KB/s") && MonitorFormat.Rate(1_240_000) == new MonitorFormat.Quantity("1.2", "MB/s")
            && MonitorFormat.Rate(2_500_000_000) == new MonitorFormat.Quantity("2.5", "GB/s") && MonitorFormat.Rate(999_600).Unit == "MB/s" && MonitorFormat.Rate(-5).Number == "0");
        check("Monitor gigabytes", MonitorFormat.Gigabytes(13_314_398_618) == "12.4" && MonitorFormat.GigabytesCompact(34_359_738_368) == "32"
            && MonitorFormat.GigabytesCompact(16_965_120_000) == "15.8");
        check("Monitor process memory", MonitorFormat.ProcessMemory(536_870_912) == "512M" && MonitorFormat.ProcessMemory(4_402_341_478) == "4.1G");
        check("Monitor gigahertz", MonitorFormat.Gigahertz(3210) == "3.21");
    }

    private static void Layouts(Action<string, bool> check)
    {
        check("Monitor layout: a standard page lists processes beside the readings", MonitorLayout.For(580, 160, true) is { List: ListPlacement.Side, Processes: true, Metrics: MetricsMode.Lines });
        check("Monitor layout: narrow pages drop the list", MonitorLayout.For(440, 160, true) is { List: ListPlacement.None, Processes: false });
        check("Monitor layout: tall narrow pages stack the list", MonitorLayout.For(440, 360, true) is { List: ListPlacement.Below, Metrics: MetricsMode.Lines });
        check("Monitor layout: short pages put the figures on one line", MonitorLayout.For(560, 100, true) is { Metrics: MetricsMode.Line, CompactMetrics: true, Figure: 16, List: ListPlacement.Side });
        check("Monitor layout: a switched-off list never shows", MonitorLayout.For(1200, 400, false) is { Processes: false });
        check("Monitor layout: tall pages give memory its block", MonitorLayout.For(1200, 300, true) is { Metrics: MetricsMode.Blocks });
        check("Monitor layout: nonsense sizes", MonitorLayout.For(double.NaN, -5, true) is { Processes: false });
        check("Monitor layout: steps, not sizes, so dragging within a step keeps the page", MonitorLayout.For(600, 220, true) == MonitorLayout.For(780, 240, true) && MonitorLayout.For(600, 220, true) != MonitorLayout.For(600, 260, true));
        check("Monitor list grid: rows fit the height, a wide list has two columns", MonitorLayout.ListGrid(300, 200, 22) == (7, 1) && MonitorLayout.ListGrid(500, 400, 22) == (14, 2));
        check("Monitor list grid: too short for two rows shows none", MonitorLayout.ListGrid(300, 60, 22) == (0, 0) && MonitorLayout.ListGrid(double.NaN, 300, 22) == (0, 0));
        check("Monitor side list width: its share within bounds, readings keep 260", MonitorLayout.SideListWidth(620) == 260 && MonitorLayout.SideListWidth(1200) == 504 && MonitorLayout.SideListWidth(500) == 220);
    }

    private static void Compact(Action<string, bool> check)
    {
        check("Compact monitor: modes choose the readings", CompactMonitor.Items(CompactMonitorMode.Full, CompactMonitorItems.None) == CompactMonitorItems.All
            && CompactMonitor.Items(CompactMonitorMode.Brief, CompactMonitorItems.All) == (CompactMonitorItems.Cpu | CompactMonitorItems.Memory | CompactMonitorItems.Gpu)
            && CompactMonitor.Items(CompactMonitorMode.Custom, CompactMonitorItems.None) == CompactMonitorItems.Cpu
            && CompactMonitor.Items(CompactMonitorMode.Custom, CompactMonitorItems.Memory | CompactMonitorItems.Upload) == (CompactMonitorItems.Memory | CompactMonitorItems.Upload));
        static double Ten(CompactMonitorItems item) => 10;
        check("Compact monitor: width of the readings with gaps", CompactMonitor.Width(CompactMonitorItems.All, Ten, 2) == 70 && CompactMonitor.Width(CompactMonitorItems.None, Ten, 2) == 0);
        check("Compact monitor: everything fits", CompactMonitor.Fit(CompactMonitorItems.All, Ten, 2, 70) == CompactMonitorItems.All);
        check("Compact monitor: upload leaves first", CompactMonitor.Fit(CompactMonitorItems.All, Ten, 2, 69) == (CompactMonitorItems.All & ~CompactMonitorItems.Upload));
        check("Compact monitor: CPU and memory stay longest", CompactMonitor.Fit(CompactMonitorItems.All, Ten, 2, 33) == (CompactMonitorItems.Cpu | CompactMonitorItems.Memory));
        check("Compact monitor: nothing fits", CompactMonitor.Fit(CompactMonitorItems.All, Ten, 2, 5) == CompactMonitorItems.None && CompactMonitor.Fit(CompactMonitorItems.All, Ten, 2, double.NaN) == CompactMonitorItems.None);
        // At 125 % and 150 % the row rounds the readings' region down to whole device pixels; the readings ask for their width
        // rounded up, so CPU, memory, GPU, download and upload (3 × 43 + 2 × 50 + 4 × 10 = 269 DIP) all stay, alone or beside
        // the song and the clock, while a row set too short still lets upload go first.
        var chosen = CompactMonitorItems.Cpu | CompactMonitorItems.Memory | CompactMonitorItems.Gpu | CompactMonitorItems.Download | CompactMonitorItems.Upload;
        static double Strip(CompactMonitorItems item) => item is CompactMonitorItems.Download or CompactMonitorItems.Upload ? 50 : 43;
        double content = CompactMonitor.Width(chosen, Strip, 10);
        check("Compact monitor: rounding the region down alone dropped upload", content == 269 && CompactMonitor.Fit(chosen, Strip, 10, DevicePixels.Down(269, 1.5)) == (chosen & ~CompactMonitorItems.Upload));
        foreach (double dpi in new[] { 1.25, 1.5 })
        {
            double wanted = DevicePixels.Up(content, dpi);
            bool fits = wanted >= content && wanted < content + 1 / dpi + 1e-9 && Math.Abs(wanted * dpi - Math.Round(wanted * dpi)) < 1e-6;
            foreach (var (media, text, clock) in new[] { (false, false, 0d), (true, true, 0d), (true, true, 60d), (false, false, 60d) })
            {
                var row = CompactMusicLayout.Calculate(CompactMusicLayout.NaturalWidth(1, media, 0, text, clock, wanted), 1, media, 0, text, clock, wanted);
                double region = DevicePixels.Down(Math.Max(0, row.MonitorWidth - (row.MonitorAlone ? 0 : 8)), dpi);
                fits &= CompactMonitor.Fit(chosen, Strip, 10, region) == chosen;
            }
            check($"Compact monitor: every reading stays after snapping at {dpi:P0}", fits);
            check($"Compact monitor: a short row still drops upload at {dpi:P0}", CompactMonitor.Fit(chosen, Strip, 10, DevicePixels.Down(260, dpi)) == (chosen & ~CompactMonitorItems.Upload));
        }
        check("Device pixels: whole widths stay, others round", DevicePixels.Up(268, 1.5) == 268 && DevicePixels.Up(269, 1.25) == 269.6 && DevicePixels.Down(269, 1.25) == 268.8
            && DevicePixels.Up(double.NaN, 1.5) == 0 && DevicePixels.Up(10, 0) == 10 && DevicePixels.Down(10, 0) == 0);
        check("Compact rates", MonitorFormat.CompactRate(5_120) == new MonitorFormat.Quantity("5.12", "K") && MonitorFormat.CompactRate(84_200) == new MonitorFormat.Quantity("84.2", "K")
            && MonitorFormat.CompactRate(512_000) == new MonitorFormat.Quantity("512", "K") && MonitorFormat.CompactRate(1_240_000) == new MonitorFormat.Quantity("1.24", "M")
            && MonitorFormat.CompactRate(120_000_000) == new MonitorFormat.Quantity("120", "M") && MonitorFormat.CompactRate(2_500_000_000) == new MonitorFormat.Quantity("2.50", "G")
            && MonitorFormat.CompactRate(12_340_000) == new MonitorFormat.Quantity("12.3", "M") && MonitorFormat.CompactRate(9_996_000) == new MonitorFormat.Quantity("10.0", "M")
            && MonitorFormat.CompactRate(99_960_000) == new MonitorFormat.Quantity("100", "M") && MonitorFormat.CompactRate(0) == new MonitorFormat.Quantity("0.00", "K")
            && MonitorFormat.CompactRate(999_600).Unit == "M");
        // The strip reserves room for "99.9M": every rate below a terabyte a second has three digits, a point below 100, and a letter.
        bool three = true;
        for (double rate = 1; rate < 999_000_000_000; rate *= 1.003)
        {
            var q = MonitorFormat.CompactRate(rate);
            three &= q.Number.Count(char.IsAsciiDigit) == 3 && q.Number.Length == (q.Number.Contains('.') ? 4 : 3) && q.Unit.Length == 1;
        }
        check("Compact rates: three digits and a letter", three);
        var alone = CompactMusicLayout.Calculate(300, 1, false, 0, false, 0, 120);
        check("Compact row: readings alone take the row, centred", alone is { MonitorAlone: true, MonitorWidth: 120 });
        var withRings = CompactMusicLayout.Calculate(300, 1, false, 2, false, 0, 120);
        check("Compact row: readings after the rings, with a gap", withRings is { MonitorAlone: false, MonitorWidth: 128 } && withRings.MiddleWidth == 172);
        var tight = CompactMusicLayout.Calculate(100, 1, false, 2, true, 0, 400);
        // The readings keep half of the middle; one ring and the shortest lyric (30 DIP) stay in any case.
        check("Compact row: one ring and the shortest lyric stay", tight.MonitorWidth == 50 && tight.MiddleWidth == 50
            && CompactMusicLayout.Calculate(40, 1, false, 2, true, 0, 400) is { MonitorWidth: 10, MiddleWidth: 30 });
        check("Compact row: no readings, no region", CompactMusicLayout.Calculate(300, 1, true, 2, true, 40).MonitorWidth == 0);
        bool steady = true;
        for (double width = 60; width < 400; width++)
            steady &= Math.Abs(CompactMusicLayout.Calculate(width + 1, 1, true, 3, true, 30, 150).MonitorWidth - CompactMusicLayout.Calculate(width, 1, true, 3, true, 30, 150).MonitorWidth) <= 1 + 1e-9;
        check("Compact row: a DIP of width moves the readings by at most a DIP", steady);
        // 269 DIP of readings and a 160 DIP lyric slot: the readings narrow first, then both share the middle evenly.
        var lyricWhole = CompactMusicLayout.Calculate(400, 1, true, 0, true, 0, content);
        var shared = CompactMusicLayout.Calculate(330, 1, true, 0, true, 0, content);
        check("Compact row: a short middle narrows the readings before the lyric", lyricWhole.TextWidth == CompactMusicLayout.TextSlot && lyricWhole.MonitorWidth == 186);
        check("Compact row: a shorter middle splits evenly between the readings and the lyric", shared.MonitorWidth == 138 && shared.TextWidth == 138);
        check("Compact row: a middle wide enough gives the readings all they want", CompactMusicLayout.Calculate(600, 1, true, 0, true, 0, content).MonitorWidth == 277);
        var screen = new DipRect(0, 0, 2560, 1440); var area = new DipRect(0, 0, 2560, 1392);
        var capsule = new DeckSettings { CapsuleWidth = 196, CapsuleHeight = 32 };
        check("Compact fit: a capsule grows to its content plus its insets", CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, 300).Surface.Width == 332);
        check("Compact fit: narrower content narrows the surface, down to the style's smallest width",
            CollapsedGeometry.Resolve(new DeckSettings { CapsuleWidth = 500 }, CompactStyle.Capsule, screen, area, 1, 150).Surface.Width == 182
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, 50).Surface.Width == 82
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, 10).Surface.Width == CollapsedLimits.CapsuleWidthMin
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, 0).Surface.Width == CollapsedLimits.CapsuleWidthMin
            && CollapsedGeometry.Resolve(new DeckSettings(), CompactStyle.Notch, screen, area, 1, 0).Surface.Width == CollapsedLimits.NotchWidthMin);
        check("Compact fit: without auto width the saved width stays", CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area).Surface.Width == 196);
        check("Compact fit: up to the screen", CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, 5000).Surface.Width == 2560
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, new DipRect(0, 0, 500, 400), new DipRect(0, 0, 500, 400), 1, 5000).Surface.Width == 500);
        check("Compact fit: the notch grows centred, the line keeps its length", CollapsedGeometry.Resolve(new DeckSettings(), CompactStyle.Notch, screen, area, 1, 400) is { Surface: { Width: 460, X: 1050 } }
            && CollapsedGeometry.Resolve(new DeckSettings(), CompactStyle.Line, screen, area, 1, 400).Surface.Width == CollapsedLimits.LineLengthDefault);
        var clockAndReadings = CompactMusicLayout.Calculate(300, 1, false, 0, false, 40, 120);
        check("Compact row: beside readings the clock is not alone", clockAndReadings is { ClockAlone: false, MonitorAlone: false } && clockAndReadings.MonitorWidth == 128 && clockAndReadings.ClockWidth == 48);
        var low = new DeckSettings { NotchHeight = 24 };
        check("Compact fit: a low notch keeps the content clear of its shoulders", CollapsedGeometry.Resolve(low, CompactStyle.Notch, screen, area, 1, 300).Surface.Width == 356);
        double natural = CompactMusicLayout.NaturalWidth(1, true, 3, true, 40, 150);
        var fitted = CompactMusicLayout.Calculate(natural, 1, true, 3, true, 40, 150);
        check("Compact fit: at its natural width the row shows every reading and every ring", fitted.MonitorWidth == 158 && !fitted.ScrollRings && fitted.TextWidth >= CompactMusicLayout.TextSlot);
        check("Compact fit: the lyric keeps a fixed slot, and each piece of content adds its width",
            CompactMusicLayout.NaturalWidth(1, true, 0, true, 0, 0) - CompactMusicLayout.NaturalWidth(1, true, 0, false, 0, 0) == CompactMusicLayout.TextSlot
            && CompactMusicLayout.NaturalWidth(1, true, 2, true, 0, 0) > CompactMusicLayout.NaturalWidth(1, true, 1, true, 0, 0)
            && CompactMusicLayout.NaturalWidth(1, true, 2, true, 40, 0) > CompactMusicLayout.NaturalWidth(1, true, 2, true, 0, 0)
            && CompactMusicLayout.NaturalWidth(1, true, 2, true, 40, 150) > CompactMusicLayout.NaturalWidth(1, true, 2, true, 40, 0));
        // An auto-width row asks for a fraction of a DIP (a clock face that is not a whole number of DIPs) and is laid out in whole
        // device pixels, rounded down: unrounded, the readings were a fraction short of their room and scrolled, cutting the last
        // one. The row's own arithmetic (MainWindow): the room the readings share is the row less the cover, the equalizer, the
        // clock, the gaps between the parts and the lyric's slot.
        double[] stripItems = [56, 47, 47, 47, 94, 41];
        const double readingsGap = 6, clockFace = 31.83, partGap = 8;
        bool Roomy(double dpi, bool rounded)
        {
            double asks = stripItems.Sum() + (stripItems.Length - 1) * readingsGap;
            double row = DevicePixels.Down(rounded ? CompactMusicLayout.NaturalWidth(1, true, 0, true, clockFace, DevicePixels.Up(asks, dpi), dpi)
                : CompactMusicLayout.NaturalWidth(1, true, 0, true, clockFace, DevicePixels.Up(asks, dpi)), dpi);
            double pool = row - (20 + 18 + clockFace + 4 * partGap) - CompactMusicLayout.TextSlot;
            var (_, readings) = CompactSpacing.Share([], 0, stripItems, readingsGap, pool);
            return readings.Count == stripItems.Length && readings.Travel == 0;
        }
        check("Compact fit: an auto-width row rounded up to whole device pixels gives the readings all their room at every display scale",
            new[] { 1, 1.25, 1.5, 1.75, 2 }.All(dpi => Roomy(dpi, rounded: true)));
        check("Compact fit: the width unrounded left the readings short at 100 % (the cut-off last reading)", !Roomy(1, rounded: false));
        var engines = GpuEngines.Order([new("Copy", .1), new("3D", .4), new("Compute_0", .2), new("Compute_1", .5), new("Foo", .3)]);
        check("GPU engines: kinds folded and ordered as Task Manager", engines.Select(e => e.Kind).SequenceEqual(["3D", "Compute", "Copy", "Foo"]) && engines[1].Usage == .5);
        check("GPU engine kinds", GpuEngines.Kind("Compute_1") == "Compute" && GpuEngines.Kind("VideoDecode") == "VideoDecode" && GpuEngines.Kind("Engine_x") == "Engine_x"
            && GpuEngines.Short("VideoEncode") == "ENC" && GpuEngines.Label("Weird") == "Weird");
        check("Memory cache share is of what is not in use", new MemorySample(100, 60, 0, 0, 30).CachedFraction == .3 && new MemorySample(100, 60, 0, 0, 70).CachedFraction == .4);
        check("GPU memory share", new GpuSample(.5, 2UL) { DedicatedTotal = 8 }.DedicatedFraction == .25 && new GpuSample(.5, 2UL).DedicatedFraction is null);
    }

    private static void Pages(Action<string, bool> check)
    {
        var settings = new DeckSettings();
        var sized = new DeckSettings();
        DeckPages.SetSize(sized, DeckPage.Music, 700, 300); DeckPages.SetSize(sized, DeckPage.Monitor, 800, 320); DeckPages.SetSize(sized, DeckPage.Usage, 900, 260);
        check("Pages: a drag sets the size of the page it is on and of no other",
            DeckPages.Size(sized, DeckPage.Music) == (700, 300) && DeckPages.Size(sized, DeckPage.Monitor) == (800, 320) && DeckPages.Size(sized, DeckPage.Usage) == (900, 260));
        DeckPages.SetSize(sized, DeckPage.Music, 5000, 10); DeckPages.SetSize(sized, DeckPage.Usage, 10, 5000);
        check("Pages: a size set by a drag stays within the page's limits",
            DeckPages.Size(sized, DeckPage.Music) == (DeckPages.MaxWidth, DeckPages.MinHeight) && DeckPages.Size(sized, DeckPage.Usage) == (DeckPages.Limits(sized, DeckPage.Usage).MinWidth, DeckPages.MaxHeight));
        check("Panel scale: 100 or 150 percent, any other value is 100; the widest page on a monitor shrinks by the scale",
            PanelScales.Fit(150) == 150 && PanelScales.Fit(125) == 100 && PanelScales.Fit(0) == 100
            && PanelScales.Factor(new DeckSettings()) == 1 && PanelScales.Factor(new DeckSettings { PanelScale = 150 }) == 1.5
            && DeckPages.MaxWidthOn(3840) == DeckPages.MaxWidth && DeckPages.MaxWidthOn(1280) == 1200 && DeckPages.MaxWidthOn(1366) == 1200 && DeckPages.MaxWidthOn(1366, 1.5) == 894
            && DeckPages.MaxWidthOn(800, 2) == DeckPages.MinWidth);
        check("Pages: the usage page is never narrower than its columns need",
            DeckPages.Limits(sized, DeckPage.Usage).MinWidth == Math.Max(DeckPages.MinWidth, sized.MinimumWidth) && DeckPages.Limits(sized, DeckPage.Music).MinWidth == DeckPages.MinWidth);
        check("Pages: a shown page resolves to itself", DeckPages.Resolve(settings, DeckPage.Monitor) == DeckPage.Monitor);
        settings.UsagePage = false;
        check("Pages: a hidden page falls back to the first shown one", DeckPages.Resolve(settings, DeckPage.Usage) == DeckPage.Music);
        settings.MonitorPage = false;
        check("Pages: the music page alone", DeckPages.Resolve(settings, DeckPage.Monitor) == DeckPage.Music);
        settings.MusicPage = false;
        check("Pages: with every page off the usage page says so", DeckPages.Resolve(settings, DeckPage.Music) == DeckPage.Usage && DeckPages.Visible(settings).Count == 0);
        var ordered = new DeckSettings { PageOrder = [DeckPage.Monitor, DeckPage.Usage, DeckPage.Music] };
        check("Pages: the switch follows the chosen order", DeckPages.Visible(ordered).SequenceEqual([DeckPage.Monitor, DeckPage.Usage, DeckPage.Music]));
        ordered.MonitorPage = false;
        check("Pages: a hidden page falls back to the first shown one in the chosen order", DeckPages.Resolve(ordered, DeckPage.Monitor) == DeckPage.Usage);
        var odd = System.Text.Json.JsonSerializer.Deserialize<DeckSettings>("""{"PageOrder":[2,9,2]}""")!;
        check("Pages: unknown and repeated pages are dropped and missing ones added", odd.PageOrder.SequenceEqual([DeckPage.Monitor, DeckPage.Usage, DeckPage.Music]));
        var copy = ordered.Copy(); copy.PageOrder = [.. DeckPages.Order];
        var orderRead = System.Text.Json.JsonSerializer.Deserialize<DeckSettings>(System.Text.Json.JsonSerializer.Serialize(ordered))!;
        check("Pages: the order is copied deeply and survives saving and reading", ordered.PageOrder[0] == DeckPage.Monitor && orderRead.PageOrder.SequenceEqual([DeckPage.Monitor, DeckPage.Usage, DeckPage.Music])
            && new DeckSettings().PageOrder.SequenceEqual(DeckPages.Order));
        var wheelOff = System.Text.Json.JsonSerializer.Deserialize<DeckSettings>("""{"MouseWheelPages":false}""")!; wheelOff.Normalize();
        var wheelOn = System.Text.Json.JsonSerializer.Deserialize<DeckSettings>("""{"MouseWheelPages":true}""")!; wheelOn.Normalize();
        check("The wheel: an older file's switch is carried across and then left out of the file",
            wheelOff.MouseWheel == MouseWheelAction.None && wheelOn.MouseWheel == MouseWheelAction.Pages
            && !System.Text.Json.JsonSerializer.Serialize(wheelOff).Contains("MouseWheelPages"));
        check("The wheel: enum values stay saved as before", (int)MouseWheelAction.None == 0 && (int)MouseWheelAction.Pages == 1 && (int)MouseWheelAction.Volume == 2);
        check("Pages: opening choices", DeckPages.Opening(DefaultDeckPage.Monitor) == DeckPage.Monitor && DeckPages.Opening(DefaultDeckPage.Last) is null
            && DeckPages.Opening(DefaultDeckPage.Usage) == DeckPage.Usage && DeckPages.Opening(DefaultDeckPage.Music) == DeckPage.Music);
        var sizes = new DeckSettings { Width = 500, Height = 150, MusicWidth = 700, MusicHeight = 180, MonitorWidth = 600, MonitorHeight = 300 };
        check("Pages: the window holds the largest shown page", DeckPages.Largest(sizes) == (700, 300));
        sizes.MusicPage = false; sizes.MonitorPage = false;
        check("Pages: only shown pages size the window", DeckPages.Largest(sizes) == (Math.Max(500, sizes.MinimumWidth), 150));
        var saved = new DeckSettings { MonitorWidth = 5000, MonitorHeight = double.NaN, MonitorInterval = 9, MonitorPalette = (MonitorPalette)42, MonitorSort = (ProcessSort)7, LastPage = (DeckPage)9 };
        saved.Normalize();
        check("Pages: monitor settings are normalized", saved is { MonitorWidth: 1200, MonitorInterval: 2, MonitorPalette: MonitorPalette.Theme, MonitorSort: ProcessSort.Cpu, LastPage: DeckPage.Usage }
            && saved.MonitorHeight == MonitorSizes.Default.Height);
        check("Pages: enum values stay saved as before", (int)DeckPage.Usage == 0 && (int)DeckPage.Music == 1 && (int)DeckPage.Monitor == 2 && (int)DefaultDeckPage.Monitor == 3);
    }

    // The easing the monitor's gauges follow, and the spring the drag follows.
    private static void Motion(Action<string, bool> check)
    {
        const double t0 = 1000;
        var eased = new Eased(0);
        check("A value without motion takes its target at once", !eased.Set(.5, false, t0) && eased.At(t0) == .5);
        eased = new Eased(0);
        check("A move starts", eased.Set(1, true, t0) && eased.Moving(t0 + .1));
        var path = Enumerable.Range(0, 43).Select(i => eased.At(t0 + i * .01)).ToList();
        check("A move rises steadily and never passes its target", path.Zip(path.Skip(1)).All(p => p.Second >= p.First - 1e-9) && path.All(v => v <= 1 + 1e-9));
        check("A move starts gently: slower than an ease-out cubic", eased.At(t0 + .042) < 1 - Math.Pow(1 - .1, 3));
        check("A move ends on its target and rests", Math.Abs(eased.At(t0 + Eased.Seconds) - 1) < 1e-9 && !eased.Moving(t0 + Eased.Seconds + .01));

        // A new target mid-move: the value carries on from where it is, at the speed it has, without a jump.
        eased = new Eased(0);
        eased.Set(1, true, t0);
        double at = eased.At(t0 + .1), speed = eased.Speed(t0 + .1);
        eased.Set(.2, true, t0 + .1);
        check("A new target starts from the current value", Math.Abs(eased.At(t0 + .1) - at) < 1e-9);
        eased = new Eased(0);
        eased.Set(.5, true, t0);
        speed = eased.Speed(t0 + .1);
        eased.Set(1, true, t0 + .1);
        check("A further target in the same direction keeps the speed the value has", Math.Abs(eased.Speed(t0 + .1) - speed) < 1e-6 || eased.Speed(t0 + .1) > speed);
        var further = Enumerable.Range(0, 43).Select(i => eased.At(t0 + .1 + i * .01)).ToList();
        check("Nor does the new move pass its target", further.All(v => v <= 1 + 1e-9));

        eased = new Eased(.5);
        check("A change too small to see is taken at once", !eased.Set(.502, true, t0) && eased.At(t0) == .502);
        eased = new Eased(0);
        check("A target that is not a number counts as zero", !eased.Set(double.NaN, true, t0) && eased.At(t0) == 0);
        var hover = new Eased(0, Eased.Hover);
        hover.Set(1, true, t0);
        check("Hover feedback takes its own, shorter time", !hover.Moving(t0 + Eased.Hover + .001) && hover.Moving(t0 + Eased.Hover - .01));

        var spring = new Spring(100) { Target = 200 };
        double peak = 100; int frames = 0;
        while (spring.Step(1 / 144.0) && frames < 1000) { peak = Math.Max(peak, spring.Value); frames++; }
        check("The spring comes to rest on its target", spring.Value == 200 && spring.Velocity == 0);
        check("The spring gives a little past its target and settles back", peak > 200 && peak < 210);
        check("The spring takes its time: neither instant nor sluggish", frames > 144 * .2 && frames < 144 * 1.2);
        spring = new Spring(100) { Target = 200 };
        spring.Step(1 / 144.0);
        double small = spring.Value;
        spring = new Spring(100) { Target = 200 };
        spring.Step(5);
        check("A stalled frame does not throw the spring", double.IsFinite(spring.Value) && Math.Abs(spring.Value - 200) < 120 && small > 100);

        check("Within its range a drag is followed as is", Spring.Resist(300, 100, 500) == 300);
        check("Past a limit a drag meets growing resistance and never passes it",
            Spring.Resist(520, 100, 500) < 500 && Spring.Resist(900, 100, 500) < 500 && Spring.Resist(900, 100, 500) > Spring.Resist(520, 100, 500));
        check("Below the lower limit too", Spring.Resist(40, 100, 500) > 100 && Spring.Resist(-400, 100, 500) > 100);
        check("Resistance meets the plain drag without a step", Math.Abs(Spring.Resist(464, 100, 500) - 464) < 1e-9 && Math.Abs(Spring.Resist(464.001, 100, 500) - 464.001) < 1e-3);
        check("A range with no room holds its one size", Spring.Resist(300, 200, 200) == 200);
    }

    // Where a drag resizes the open panel and the collapsed surface.
    private static void Grips(Action<string, bool> check)
    {
        var panel = new DipRect(100, 0, 500, 260);
        const double header = 36;
        ResizeEdges At(double x, double y) => ResizeGrips.Panel(x, y, panel, header);
        check("A side of the panel takes the pointer just inside it", At(595, 120) == ResizeEdges.Right && At(104, 120) == ResizeEdges.Left);
        check("And just outside it, so a small overshoot still holds the edge", At(610, 120) == ResizeEdges.Right && At(90, 120) == ResizeEdges.Left);
        check("Beyond the outer reach it does not", At(600 + ResizeGrips.PanelOuter + 1, 120) == ResizeEdges.None);
        check("The bottom takes the pointer on both sides of it", At(350, 255) == ResizeEdges.Bottom && At(350, 270) == ResizeEdges.Bottom);
        check("A bottom corner takes both, in a larger square", At(590, 245) == (ResizeEdges.Bottom | ResizeEdges.Right) && At(108, 268) == (ResizeEdges.Bottom | ResizeEdges.Left));
        check("The middle of the panel is no grip", At(350, 120) == ResizeEdges.None);
        check("The header row is no grip, so its buttons keep working", At(598, 20) == ResizeEdges.None && At(102, 30) == ResizeEdges.None);
        check("Above the panel nothing is a grip", At(350, -5) == ResizeEdges.None);
        var narrow = new DipRect(0, 0, 30, 200);
        check("A narrow panel keeps a middle that is no grip", ResizeGrips.Panel(15, 100, narrow, 10) == ResizeEdges.None);
        check("An empty panel has no grips", ResizeGrips.Panel(0, 0, new DipRect(0, 0, 0, 0), 0) == ResizeEdges.None);
        var strips = ResizeGrips.PanelStrips(panel, header).ToList();
        check("The grips the panel paints cover every point that grips",
            new[] { (595.0, 120.0), (610.0, 120.0), (90.0, 120.0), (350.0, 270.0), (590.0, 245.0), (108.0, 268.0) }
                .All(p => strips.Any(r => p.Item1 >= r.X && p.Item1 <= r.Right && p.Item2 >= r.Y && p.Item2 <= r.Bottom)));
        var area = ResizeGrips.PanelArea(panel);
        check("The panel's area includes its grips outside it", area.X == 100 - ResizeGrips.PanelOuter && area.Bottom == 260 + ResizeGrips.PanelOuter && area.Y == 0);

        var capsule = new DipRect(1000, 5, 300, 32);
        check("An end of the capsule takes the pointer inside and outside it",
            ResizeGrips.Compact(1295, 20, capsule, false) == ResizeEdges.Right && ResizeGrips.Compact(1310, 20, capsule, false) == ResizeEdges.Right
            && ResizeGrips.Compact(990, 20, capsule, false) == ResizeEdges.Left && ResizeGrips.Compact(1010, 20, capsule, false) == ResizeEdges.Left);
        check("The middle of the capsule still opens the panel", ResizeGrips.Compact(1150, 20, capsule, false) == ResizeEdges.None && ResizeGrips.Compact(1030, 20, capsule, false) == ResizeEdges.None);
        check("Far beside or below the capsule is no grip", ResizeGrips.Compact(1320, 20, capsule, false) == ResizeEdges.None && ResizeGrips.Compact(1295, 60, capsule, false) == ResizeEdges.None);
        var line = new DipRect(1250, 4, 56, 4);
        check("The thin line gets room above and below its ends", ResizeGrips.Compact(1310, 14, line, true) == ResizeEdges.Right && ResizeGrips.Compact(1246, 0, line, true) == ResizeEdges.Left);
        check("A short line keeps a middle that opens the panel", ResizeGrips.Compact(1278, 6, line, true) == ResizeEdges.None);
        var zone = ResizeGrips.CompactZone(capsule, false, ResizeEdges.Right);
        check("An end's zone is what grips there", zone.X <= 1295 && zone.Right >= 1310 && zone.Y <= 5 && zone.Bottom >= 37);
    }

    private static void Colors(Action<string, bool> check)
    {
        check("Color mix ends and middle", ColorMath.Mix("#000000", "#FFFFFF", 0) == "#000000" && ColorMath.Mix("#000000", "#FFFFFF", 1) == "#FFFFFF"
            && ColorMath.Mix("#000000", "#FF8040", .5) == "#804020" && ColorMath.Mix("#102030", "#FFFFFF", double.NaN) == "#102030");
        var sea = MonitorColors.For(MonitorPalette.Sea);
        check("Monitor core colors run from performance to efficiency", sea.Core(0, 3) == sea.Performance && sea.Core(2, 3) == sea.Efficiency && sea.Core(0, 1) == sea.Performance && sea.Core(9, 2) == sea.Efficiency);
        check("Monitor palettes differ in light and dark", Enum.GetValues<MonitorPalette>().All(p => MonitorColors.For(p, true) != MonitorColors.For(p, false)));
        check("Monitor level colors", sea.Level(MemoryLevel.Critical, sea.Memory) == sea.Critical && sea.Level(MemoryLevel.Elevated, sea.Memory) == sea.Warning && sea.Level(MemoryLevel.Normal, sea.Memory) == sea.Memory);
        var themed = MonitorColors.FromTheme("#0A84FF", ["#5CE1E6", "#FF8FB1", "#E4FF6A", "#FF9F43"], "#FFB000", "#FF4D4D");
        check("Monitor theme scheme takes the accent, then cyan and pink", themed is { Performance: "#0A84FF", Efficiency: "#5CE1E6", Memory: "#5CE1E6", Gpu: "#FF8FB1",
            Download: "#0A84FF", Upload: "#5CE1E6", Warning: "#FFB000", Critical: "#FF4D4D" });
        var cyberpunk = MonitorColors.FromTheme("#00F0FF", ["#00F0FF", "#FF2A6D", "#B6FF3B", "#FF9E3D"], "#FFB000", "#FF4D4D");
        check("Monitor theme scheme skips a cyan that is the accent", cyberpunk is { Performance: "#00F0FF", Efficiency: "#FF2A6D", Gpu: "#B6FF3B" });
        var flat = MonitorColors.FromTheme("#808080", ["#808081", "#808082"], "#808080", "#808080");
        check("Monitor theme scheme without distinct colors still gives colors", flat.Efficiency.Length == 7 && flat.Gpu.Length == 7);
        check("Color distance", ColorMath.Distance("#000000", "#000000") == 0 && Math.Abs(ColorMath.Distance("#000000", "#FFFFFF") - Math.Sqrt(3) * 255) < 1e-9);
    }
}

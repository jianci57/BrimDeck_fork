using System.Text.Json;
using BrimDeck.Core;

// The collapsed readings' table: migration from the old modes, presets, order, drop priority, widths and copies.
internal static class CompactReadingTests
{
    private const CompactMonitorItems Cpu = CompactMonitorItems.Cpu, Memory = CompactMonitorItems.Memory, Gpu = CompactMonitorItems.Gpu,
        Columns = CompactMonitorItems.Columns, Download = CompactMonitorItems.Download, Upload = CompactMonitorItems.Upload, All = CompactMonitorItems.All,
        Temperature = CompactMonitorItems.Temperature;

    internal static void Run(Action<string, bool> check)
    {
        Migration(check);
        Presets(check);
        Highlight(check);
        Layout(check);
        LevelColors(check);
        ReplacedChildren(check);
        Copies(check);
        PartOrder(check);
    }

    private static DeckSettings Read(string json) => SettingsMigrations.Read(json);
    // Read by the version that gave the equalizer its own switch, whatever version the tests carry.
    private static DeckSettings Upgraded(string json) => SettingsMigrations.Read(json, new Version(1, 2, 3), out _);
    private static bool Same(IEnumerable<CompactReading> readings, params CompactMonitorItems[] order) => readings.Select(r => r.Item).SequenceEqual(order);

    private static void Migration(Action<string, bool> check)
    {
        var full = Read("""{"AppVersion":"0.1.0","CompactMonitor":0,"CompactMonitorItems":3}""");
        // The old Full mode had no drives, power, battery or frame rate, so it matches the Full preset no longer.
        check("Compact readings: old Full shows every usage reading with figures in the default order",
            Same(full.CompactReadings, [.. CompactMonitor.Order]) && full.CompactReadings.All(r => r.Visible == ((All & r.Item) != 0) && r.ShowValue && r.Color is null)
            && full.CompactMonitor == CompactMonitorMode.Custom && full.CompactMonitorItems == All);
        var brief = Read("""{"AppVersion":"0.1.0","CompactMonitor":1,"CompactMonitorItems":63}""");
        check("Compact readings: old Brief shows the three rings without figures",
            CompactMonitor.Visible(brief.CompactReadings) == CompactMonitor.Rings && CompactMonitor.Values(brief.CompactReadings) == CompactMonitorItems.None
            && brief.CompactMonitor == CompactMonitorMode.Brief && brief.CompactMonitorItems == CompactMonitor.Rings);
        var custom = Read("""{"AppVersion":"0.1.0","CompactMonitor":2,"CompactMonitorItems":62}""");
        check("Compact readings: old Custom keeps its readings with figures",
            CompactMonitor.Visible(custom.CompactReadings) == (All & ~Columns) && CompactMonitor.Values(custom.CompactReadings) == CompactMonitor.Rings
            && custom.CompactMonitor == CompactMonitorMode.Custom && custom.CompactMonitorItems == (All & ~Columns) && CompactMonitor.Preset(custom.CompactReadings) is null);
        var empty = Read("""{"AppVersion":"0.1.0","CompactMonitor":2,"CompactMonitorItems":0}""");
        check("Compact readings: old Custom with nothing chosen shows CPU", CompactMonitor.Visible(empty.CompactReadings) == Cpu && empty.CompactMonitorItems == Cpu);
        var unknownMode = Read("""{"AppVersion":"0.1.0","CompactMonitor":9}""");
        check("Compact readings: an unknown old mode reads as the old Full", CompactMonitor.Visible(unknownMode.CompactReadings) == All);
        var defaults = new DeckSettings(); defaults.Normalize();
        check("Compact readings: defaults show every usage reading", CompactMonitor.Visible(defaults.CompactReadings) == All && CompactMonitor.Values(defaults.CompactReadings) == CompactMonitor.Rings
            && Same(defaults.CompactReadings, [.. CompactMonitor.Order]));

        var odd = Read("""{"AppVersion":"0.1.0","CompactMonitor":0,"CompactReadings":[{"Item":256,"Visible":true},null,{"Item":16,"Visible":true,"ShowValue":false,"Color":"#12ab3c"},{"Item":2,"Visible":false,"Color":"red"},{"Item":16,"Visible":false}]}""");
        check("Compact readings: unknown and repeated readings are dropped, missing ones join hidden",
            Same(odd.CompactReadings, Download, Cpu, Columns, Memory, Gpu, Upload, CompactMonitorItems.Storage, CompactMonitorItems.Power, CompactMonitorItems.Charge, CompactMonitorItems.Frames) && odd.CompactReadings[0].Visible && !odd.CompactReadings[1].Visible
            && odd.CompactReadings.Skip(2).All(r => !r.Visible) && odd.CompactMonitorItems == Download && odd.CompactMonitor == CompactMonitorMode.Custom);
        check("Compact readings: an older own color moves into the custom scheme, normalized", odd.MonitorPalette == MonitorPalette.Custom
            && odd.MonitorCustomColors is { Base: MonitorPalette.Theme, Download: "#12AB3C", Cpu: null } && odd.CompactReadings.All(r => !r.HasOwnColors));
        var none = Read("""{"AppVersion":"0.1.0","CompactReadings":[{"Item":2,"Visible":false}]}""");
        check("Compact readings: with every reading off the strip shows CPU", none.CompactMonitorItems == Cpu && CompactMonitor.Visible(none.CompactReadings) == CompactMonitorItems.None);
        var broken = Read("""{"AppVersion":"0.1.0","CompactReadings":null}""");
        check("Compact readings: a missing table reads as the defaults", Same(broken.CompactReadings, [.. CompactMonitor.Order]));
    }

    private static void Presets(Action<string, bool> check)
    {
        var readings = CompactMonitor.Normalize([
            new() { Item = Upload, Color = "#112233" }, new() { Item = Gpu }, new() { Item = Columns, Visible = false },
            new() { Item = Cpu, ShowValue = false }, new() { Item = Download }, new() { Item = Memory, Color = "#445566" }]);
        var order = readings.Select(r => r.Item).ToArray();
        check("Compact readings: a reordered, partly chosen table matches no preset", CompactMonitor.Preset(readings) is null);
        CompactMonitor.Apply(readings, CompactMonitorMode.Brief);
        check("Compact readings: Brief keeps the order and the colors", readings.Select(r => r.Item).SequenceEqual(order)
            && readings[0].Color == "#112233" && readings[5].Color == "#445566");
        check("Compact readings: Brief shows the rings without figures", CompactMonitor.Preset(readings) == CompactMonitorMode.Brief
            && CompactMonitor.Visible(readings) == CompactMonitor.Rings && CompactMonitor.Values(readings) == CompactMonitorItems.None);
        CompactMonitor.Apply(readings, CompactMonitorMode.Full);
        check("Compact readings: Full keeps the order and the colors", readings.Select(r => r.Item).SequenceEqual(order) && readings[0].Color == "#112233");
        check("Compact readings: Full shows everything with figures", CompactMonitor.Preset(readings) == CompactMonitorMode.Full && CompactMonitor.Visible(readings) == CompactMonitor.Every
            && CompactMonitor.Values(readings) == CompactMonitor.Rings && readings.Where(r => CompactFigures.Has(r.Item)).All(r => CompactFigures.Of(r).SequenceEqual([CompactFigures.Usage, CompactFigures.Temperature])));
        var before = readings.Select(r => (r.Item, r.Visible, r.ShowValue, r.Color)).ToArray();
        CompactMonitor.Apply(readings, CompactMonitorMode.Custom);
        check("Compact readings: no preset (Custom) changes nothing", readings.Select(r => (r.Item, r.Visible, r.ShowValue, r.Color)).SequenceEqual(before));
        readings.First(r => r.Item == Gpu).ShowValue = false;
        check("Compact readings: a figure turned off matches no preset", CompactMonitor.Preset(readings) is null);
        CompactMonitor.Apply(readings, CompactMonitorMode.Brief);
        readings.First(r => r.Item == Download).Visible = true;
        check("Compact readings: a reading added to Brief matches no preset", CompactMonitor.Preset(readings) is null);
        readings.First(r => r.Item == Download).Visible = false;
        readings.First(r => r.Item == Columns).ShowValue = true;
        check("Compact readings: the hidden readings' figures do not change the preset", CompactMonitor.Preset(readings) == CompactMonitorMode.Brief);
        check("Compact readings: only the rings have a figure choice",
            CompactMonitor.HasValueChoice(Cpu) && CompactMonitor.HasValueChoice(Memory) && CompactMonitor.HasValueChoice(Gpu)
            && !CompactMonitor.HasValueChoice(Columns) && !CompactMonitor.HasValueChoice(Download) && !CompactMonitor.HasValueChoice(Upload));
    }

    private static void Layout(Action<string, bool> check)
    {
        CompactMonitorItems[] order = [Upload, Gpu, Cpu, Columns, Download, Memory];
        var width = CompactMonitor.Widths(CompactMonitor.Rings, 12, _ => 30, 14, 20, 50);
        var arranged = CompactMonitor.Arrange(order, All & ~Download, width, 10, _ => 1);
        check("Compact readings: the strip follows the table's order", arranged.Items.Select(i => i.Item).SequenceEqual([Upload, Gpu, Cpu, Columns, Memory]));
        check("Compact readings: each reading starts after the one before it", arranged.Items[0].X == 0 && arranged.Items[1].X == 60 && arranged.Items[2].X == 112
            && arranged.Items[3].X == 164 && arranged.Items[4].X == 194 && arranged.Total == 236);
        var half = CompactMonitor.Arrange(order, All, width, 10, item => item == Gpu ? .5 : 1);
        check("Compact readings: a leaving reading gives back its room in proportion", half.Items[2].X == 60 + 26);

        foreach (var space in new[] { 300.0, 200, 150, 100, 60, 40, 20 })
        {
            var fit = CompactMonitor.Fit(All, width, 10, space);
            check($"Compact readings: drop order at {space} is the fixed one", fit == Expected(space));
        }
        // Fit takes no order, so the table's order cannot change which readings leave; the arranged width matches Width.
        var fitted = CompactMonitor.Fit(All, width, 10, 200);
        check("Compact readings: the arranged width of what fits is its measured width in any order",
            Math.Abs(CompactMonitor.Arrange(order, fitted, width, 10, _ => 1).Total - CompactMonitor.Width(fitted, width, 10)) < 1e-9
            && Math.Abs(CompactMonitor.Arrange(CompactMonitor.Order, fitted, width, 10, _ => 1).Total - CompactMonitor.Width(fitted, width, 10)) < 1e-9);

        var bare = CompactMonitor.Widths(CompactMonitorItems.None, 12, _ => 30, 14, 20, 50);
        var some = CompactMonitor.Widths(Cpu, 12, _ => 30, 14, 20, 50);
        check("Compact readings: a ring without its figure is narrower", bare(Memory) == 14 && width(Memory) == 42 && some(Cpu) == 42 && some(Gpu) == 14
            && bare(Download) == 50 && bare(Columns) == 20);
        check("Compact readings: hiding figures shrinks the strip",
            CompactMonitor.Width(All, bare, 10) < CompactMonitor.Width(All, some, 10) && CompactMonitor.Width(All, some, 10) < CompactMonitor.Width(All, width, 10));
        check("Compact readings: hiding figures lets more readings fit", CompactMonitor.Fit(All, bare, 10, 200) == (All & ~Upload) && CompactMonitor.Fit(All, width, 10, 200) == (Cpu | Memory | Columns | Download));
        check("Compact readings: rings alone sit closer only without figures",
            CompactMonitor.RingsOnly(CompactMonitor.Rings, CompactMonitorItems.None) && !CompactMonitor.RingsOnly(CompactMonitor.Rings, Cpu) && !CompactMonitor.RingsOnly(All, CompactMonitorItems.None));

        // Upload, GPU, download, columns and memory leave in turn; widths 50, 42, 50, 20, 42 and CPU 42, gaps 10.
        static CompactMonitorItems Expected(double space) => space switch
        {
            >= 296 => All, >= 236 => All & ~Upload, >= 184 => Cpu | Memory | Columns | Download, >= 124 => Cpu | Memory | Columns,
            >= 94 => Cpu | Memory, >= 42 => Cpu, _ => CompactMonitorItems.None
        };
    }

    // Every reading takes its colors from the color scheme (ReadingLevels): in the normal state its own, the core columns one per
    // kind of core; at the warning and critical levels the scheme's. The custom scheme changes some colors of the one it starts from.
    private static void LevelColors(Action<string, bool> check)
    {
        const MemoryLevel Normal = MemoryLevel.Normal, Elevated = MemoryLevel.Elevated, Critical = MemoryLevel.Critical;
        var palette = MonitorColors.For(MonitorPalette.Sea);
        var schemes = Enum.GetValues<MonitorPalette>().SelectMany(scheme => new[] { MonitorColors.For(scheme, true), MonitorColors.For(scheme, false) })
            .Append(MonitorColors.FromTheme("#112233", ["#445566", "#778899", "#AABBCC"], "#FFFF00", "#FF0000")).ToList();
        check("Level colors: every reading takes its scheme color at each level", schemes.All(scheme =>
        {
            var strip = new StripColors(scheme);
            return strip.Normal(Cpu) == scheme.Performance && strip.Normal(Memory) == scheme.Memory && strip.Normal(Gpu) == scheme.Gpu
                && strip.Normal(Columns) == scheme.Performance && strip.Normal(Columns, 1) == scheme.Efficiency
                && strip.Normal(Download) == scheme.Download && strip.Normal(Upload) == scheme.Upload
                && CompactMonitor.Order.All(item => strip.At(item, Normal) == strip.Normal(item) && strip.At(item, Elevated) == scheme.Warning && strip.At(item, Critical) == scheme.Critical);
        }));
        // The drives follow memory, power and the battery the CPU color, the frame rate the GPU color, while they have none of their own.
        static MonitorRole? Follows(MonitorRole role) => role switch
        {
            MonitorRole.Storage => MonitorRole.Memory, MonitorRole.Power or MonitorRole.Battery => MonitorRole.Cpu, MonitorRole.Frames => MonitorRole.Gpu, _ => null
        };
        check("Level colors: each color of a scheme by its role", Enum.GetValues<MonitorRole>().All(role =>
            palette.With(role, "#010203")[role] == "#010203" && Enum.GetValues<MonitorRole>().Where(other => other != role)
                .All(other => palette.With(role, "#010203")[other] == (Follows(other) == role ? "#010203" : palette[other]))));
        check("Level colors: the drives, power and the battery keep their own color over the one they follow",
            palette.With(MonitorRole.Storage, "#0A0B0C").With(MonitorRole.Memory, "#010203") is { Storage: "#0A0B0C", Memory: "#010203" }
            && palette.With(MonitorRole.Power, "#0A0B0C").With(MonitorRole.Cpu, "#010203")[MonitorRole.Power] == "#0A0B0C"
            && palette.With(MonitorRole.Battery, "#0A0B0C").With(MonitorRole.Cpu, "#010203")[MonitorRole.Battery] == "#0A0B0C");
        check("Custom colors: the drives, power and the battery survive drawing", new MonitorCustomColors { Base = MonitorPalette.Sea, Storage = "#123456", Power = "#234567", Battery = "#345678" }
            .Over(palette, _ => "#000000") is { Drives: "#000000", Power: "#000000", Battery: "#000000" } && palette.Map(color => color) is { Drives: null, Power: null, Battery: null });
        var none = new StripColors(palette);
        check("Level colors: the core columns run from the CPU color to the efficiency color", none.Core(0, 3) == palette.Performance && none.Core(2, 3) == palette.Efficiency
            && none.Core(1, 3) == palette.Core(1, 3));
        check("Level colors: a busy core takes the level's color", none.Core(1, 3, Elevated) == palette.Warning && none.Core(0, 3, Critical) == palette.Critical);
        check("Level colors: only the core columns have two channels", CompactMonitor.Channels(Columns) == 2 && CompactMonitor.Order.Where(item => item != Columns).All(item => CompactMonitor.Channels(item) == 1));

        var warm = MonitorColors.For(MonitorPalette.Warm);
        var custom = new MonitorCustomColors { Base = MonitorPalette.Warm, Memory = "#123456", Upload = "#ABCDEF" };
        var over = custom.Over(warm);
        check("Custom colors: the changed colors over the base's", over.Memory == "#123456" && over.Upload == "#ABCDEF" && over.Performance == warm.Performance
            && over.Gpu == warm.Gpu && over.Warning == warm.Warning && custom.Changed && !new MonitorCustomColors().Changed);
        check("Custom colors: drawn as the caller draws them", custom.Over(warm, _ => "#000000") is { Memory: "#000000", Upload: "#000000" } drawn && drawn.Gpu == warm.Gpu);
        var strip = new StripColors(over with { Warning = "#111111" });
        check("Custom colors: the collapsed readings take them, and the level colors apart from them", strip.Memory(Normal) == "#123456" && strip.Normal(Upload) == "#ABCDEF"
            && strip.Memory(Elevated) == "#111111" && strip.At(Cpu, Elevated) == "#111111" && strip.Normal(Gpu) == warm.Gpu);
        var odd = new MonitorCustomColors { Base = MonitorPalette.Custom, Cpu = "12ab3c", Gpu = "red" }; odd.Normalize();
        var unknown = new MonitorCustomColors { Base = (MonitorPalette)42 }; unknown.Normalize();
        check("Custom colors: normalized, an invalid one follows the base, the base is never Custom",
            odd is { Base: MonitorPalette.Theme, Cpu: "#12AB3C", Gpu: null } && unknown.Base == MonitorPalette.Theme);

        check("Level thresholds: shares", ReadingLevels.OfShare(.79) == Normal && ReadingLevels.OfShare(.8) == Elevated && ReadingLevels.OfShare(.919) == Elevated
            && ReadingLevels.OfShare(.92) == Critical && ReadingLevels.OfShare(1) == Critical);
        check("Level thresholds: rates", ReadingLevels.OfRate(9_999_999) == Normal && ReadingLevels.OfRate(10_000_000) == Elevated
            && ReadingLevels.OfRate(49_999_999) == Elevated && ReadingLevels.OfRate(50_000_000) == Critical);
        check("Level thresholds: memory in use matches the other shares", new MemorySample(100, 80, 0, 100).Level == Elevated && new MemorySample(100, 92, 0, 100).Level == Critical
            && new MemorySample(100, 10, 85, 100).Level == Elevated && new MemorySample(100, 10, 90, 100).Level == Critical && new MemorySample(100, 79, 79, 100).Level == Normal);
        var core = new CoreSample(0, 0, CoreTier.Performance, 1, .95, 2000d, 4000);
        var snapshot = new MonitorSnapshot(DateTimeOffset.Now, 2, new CpuSample([core, core with { Id = 1, Usage = .1 }], 3000d), new MemorySample(100, 85, 10, 100),
            new NetworkSample(60_000_000, 1_000), new GpuSample(.5, 0UL), Reading<ProcessList>.Unavailable("test"));
        check("Level of each reading from a snapshot", ReadingLevels.Of(Cpu, snapshot) == Normal && ReadingLevels.Of(Columns, snapshot) == Critical
            && ReadingLevels.Of(Memory, snapshot) == Elevated && ReadingLevels.Of(Gpu, snapshot) == Normal && ReadingLevels.Of(Download, snapshot) == Critical
            && ReadingLevels.Of(Upload, snapshot) == Normal && ReadingLevels.Of(Cpu, null) == Normal && ReadingLevels.Of(Memory, MonitorSnapshot.Empty(2, "test")) == Normal);

        // Files saved before the custom scheme: the readings' own colors become the custom scheme over the scheme in use. The
        // CPU color falls back to the core columns' first; the level colors are the first a reading sets, in the table's order.
        var read = Read("""{"AppVersion":"0.1.0","MonitorPalette":2,"CompactReadings":[{"Item":4,"Color":"#7fd1c4","WarningColor":"#aabbcc","CriticalColor":"nope"},{"Item":2,"WarningColor":"#112233","CriticalColor":"#445566","ExtraColors":["#000000"]},{"Item":1,"Color":"#ff0000","ExtraColors":["#0000ff","#00ff00"],"Visible":false},{"Item":16,"Color":"#010101"}]}""");
        check("Older colors: the readings' own colors become the custom scheme", read.MonitorPalette == MonitorPalette.Custom
            && read.MonitorCustomColors is { Base: MonitorPalette.Warm, Memory: "#7FD1C4", Cpu: "#FF0000", Efficiency: "#0000FF", Download: "#010101", Gpu: null, Upload: null });
        check("Older colors: the level colors become the collapsed readings' own", read is { CompactWarningColor: "#AABBCC", CompactCriticalColor: "#445566" });
        var levelsOnly = Read("""{"AppVersion":"0.1.0","MonitorPalette":1,"CompactReadings":[{"Item":2,"WarningColor":"#112233"}]}""");
        check("Older colors: level colors alone keep the scheme", levelsOnly is { MonitorPalette: MonitorPalette.Graphite, CompactWarningColor: "#112233", CompactCriticalColor: null }
            && !levelsOnly.MonitorCustomColors.Changed);
        var levels = Read("""{"AppVersion":"0.1.0","CompactWarningColor":"abcdef","CompactCriticalColor":"nope"}""");
        check("Level colors: normalized, an invalid one follows the panel theme", levels is { CompactWarningColor: "#ABCDEF", CompactCriticalColor: null }
            && SettingsMigrations.Read(JsonSerializer.Serialize(levels)).CompactWarningColor == "#ABCDEF");
        check("Smoothing: older files ease at 10 frames a second, an unknown value too, and a choice is kept",
            levels.CompactSmoothing == CompactSmoothing.Ten
            && Read("""{"AppVersion":"0.1.0","CompactSmoothing":7}""").CompactSmoothing == CompactSmoothing.Ten
            && Read("""{"AppVersion":"0.1.0","CompactSmoothing":30}""").CompactSmoothing == CompactSmoothing.Thirty
            && Read("""{"AppVersion":"0.1.0","CompactSmoothing":0}""").CompactSmoothing == CompactSmoothing.Off);
        check("Older colors: they leave the readings, which keep the rest", read.CompactReadings.All(r => !r.HasOwnColors)
            && !read.CompactReadings.First(r => r.Item == Columns).Visible && Same(read.CompactReadings, Memory, Cpu, Columns, Download, Gpu, Upload, CompactMonitorItems.Storage, CompactMonitorItems.Power, CompactMonitorItems.Charge, CompactMonitorItems.Frames));
        var plain = Read("""{"AppVersion":"0.1.0","MonitorPalette":1,"CompactReadings":[{"Item":4}]}""");
        check("Older colors: without own colors the scheme stays", plain.MonitorPalette == MonitorPalette.Graphite && !plain.MonitorCustomColors.Changed);
        var readings = JsonSerializer.Serialize(read.CompactReadings);
        check("Older colors: not written", !readings.Contains("Color"));
        var again = SettingsMigrations.Read(JsonSerializer.Serialize(read));
        check("Custom colors: survive saving and reading", again.MonitorPalette == MonitorPalette.Custom && again.MonitorCustomColors.Key == read.MonitorCustomColors.Key);
        var copy = read.Copy(); copy.MonitorCustomColors.Memory = "#000000";
        check("Custom colors: Copy is deep", read.MonitorCustomColors.Memory == "#7FD1C4");
        var missing = Read("""{"AppVersion":"0.1.0","MonitorCustomColors":null}""");
        check("Custom colors: a missing one reads as unchanged", missing.MonitorCustomColors is { Base: MonitorPalette.Theme, Changed: false });
    }

    // WPF throws when a panel's child is replaced through its indexer ("指定的索引已经在使用"), which once left the collapsed
    // readings' table empty; the app's code removes the old child and inserts the new one instead.
    private static void ReplacedChildren(Action<string, bool> check)
    {
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        if (source is null) { check("Panel children: the source tree is found", false); return; }
        var replaced = Directory.EnumerateFiles(Path.Combine(source.FullName, "src", "BrimDeck"), "*.cs", SearchOption.AllDirectories)
            .Where(file => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(file), @"\.Children\[[^\]]+\]\s*=(?!=)")).Select(Path.GetFileName).ToList();
        check("Panel children: none is replaced through the indexer" + (replaced.Count > 0 ? " (" + string.Join(", ", replaced) + ")" : ""), replaced.Count == 0);
    }

    private static void Highlight(Action<string, bool> check)
    {
        var readings = CompactMonitor.Readings(CompactMonitorMode.Full, CompactMonitorItems.None);
        check("Compact readings: the defaults, without the drives, light no preset", CompactMonitor.Preset(readings) is null);
        CompactMonitor.Apply(readings, CompactMonitorMode.Full);
        readings.First(r => r.Item == Columns).Visible = false;
        check("Compact readings: one reading off lights no preset", CompactMonitor.Preset(readings) is null);
        CompactMonitor.Apply(readings, CompactMonitorMode.Brief);
        readings.First(r => r.Item == Memory).Visible = false;
        check("Compact readings: Brief less a ring lights no preset", CompactMonitor.Preset(readings) is null);
        readings.First(r => r.Item == Memory).Visible = true; readings.First(r => r.Item == Memory).ShowValue = true;
        check("Compact readings: Brief with a figure lights no preset", CompactMonitor.Preset(readings) is null);
        var settings = new DeckSettings { CompactReadings = readings }; settings.Normalize();
        check("Compact readings: no preset is saved as Custom in the old key", settings.CompactMonitor == CompactMonitorMode.Custom);
        CompactMonitor.Apply(settings.CompactReadings, CompactMonitorMode.Full); settings.Normalize();
        check("Compact readings: Full is saved as Full in the old key", settings.CompactMonitor == CompactMonitorMode.Full && settings.CompactMonitorItems == CompactMonitor.Every);
    }

    private static void Copies(Action<string, bool> check)
    {
        var settings = new DeckSettings();
        settings.CompactReadings = CompactMonitor.Normalize([new() { Item = Gpu, ShowValue = false }, new() { Item = Upload }, new() { Item = Cpu }]);
        settings.Normalize();
        var copy = settings.Copy();
        copy.CompactReadings[0].ShowValue = true; copy.CompactReadings.Reverse();
        check("Compact readings: Copy is deep", settings.CompactReadings[0].Item == Gpu && !settings.CompactReadings[0].ShowValue);
        var read = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("Compact readings: the table survives saving and reading",
            read.CompactReadings.Select(r => (r.Item, r.Visible, r.ShowValue)).SequenceEqual(settings.CompactReadings.Select(r => (r.Item, r.Visible, r.ShowValue)))
            && read.CompactMonitor == CompactMonitorMode.Custom && read.CompactMonitorItems == (Gpu | Upload | Cpu));
        var untouched = new DeckSettings { CompactMonitor = CompactMonitorMode.Brief }.Copy();
        check("Compact readings: Copy keeps a table not yet built", CompactMonitor.Preset(untouched.CompactReadings) == CompactMonitorMode.Brief);
    }

    // The order of the collapsed parts: one per style, each part once, kept by copies and by saving.
    private static void PartOrder(Action<string, bool> check)
    {
        CompactPart[] standard = [CompactPart.Rings, CompactPart.Music, CompactPart.Monitor, CompactPart.Equalizer, CompactPart.Clock];
        var odd = Read("""{"AppVersion":"0.1.0","NotchOrder":[2,9,2,0]}""");
        check("Compact order: unknown and repeated parts are dropped and missing ones added", odd.NotchOrder.SequenceEqual([CompactPart.Monitor, CompactPart.Rings, CompactPart.Music, CompactPart.Equalizer, CompactPart.Clock])
            && odd.CapsuleOrder.SequenceEqual(standard));
        var older = Read("""{"AppVersion":"1.2.2","NotchOrder":[1,2,0]}""");
        check("Compact order: an order saved before the equalizer and the clock had a place puts the equalizer before the clock, at the end",
            older.NotchOrder.SequenceEqual([CompactPart.Music, CompactPart.Monitor, CompactPart.Rings, CompactPart.Equalizer, CompactPart.Clock]));
        var moved = Read("""{"AppVersion":"1.2.3","NotchOrder":[4,0,3,1,2]}""");
        check("Compact order: the equalizer and the clock keep a place chosen for them",
            moved.NotchOrder.SequenceEqual([CompactPart.Clock, CompactPart.Rings, CompactPart.Equalizer, CompactPart.Music, CompactPart.Monitor]));
        var musicOff = Upgraded("""{"AppVersion":"1.2.2","NotchMusic":false}""");
        check("Compact equalizer: a file from before its own switch shows it as the music was shown",
            !musicOff.NotchEqualizer && musicOff.CapsuleEqualizer && !musicOff.EqualizerAlways && Upgraded("""{"AppVersion":"1.2.2"}""").NotchEqualizer);
        var split = Upgraded("""{"AppVersion":"0.0.9","CompactMusic":false}""");
        check("Compact equalizer: a file with one music switch for both styles turns both equalizers as it", !split.NotchEqualizer && !split.CapsuleEqualizer);
        check("Compact equalizer: its color was the cover's or the theme's, and is now a swatch",
            Upgraded("""{"AppVersion":"1.2.2","MusicCoverColor":false}""").EqualizerColor == ClockColor.White
            && Upgraded("""{"AppVersion":"1.2.2","MusicCoverColor":true}""").EqualizerColor == ClockColor.Cover
            && Upgraded("""{"AppVersion":"1.2.2"}""").EqualizerColor == ClockColor.Cover && new DeckSettings().EqualizerColor == ClockColor.Cover
            && Upgraded("""{"AppVersion":"1.2.3","EqualizerColor":2}""").EqualizerColor == ClockColor.Cyan);
        var chosen = Upgraded("""{"AppVersion":"1.2.2","NotchMusic":false,"NotchEqualizer":true}""");
        check("Compact equalizer: a switch already saved is kept", chosen.NotchEqualizer);
        check("Compact equalizer: media is read for the equalizer alone, and the indicator shows none",
            new DeckSettings { MusicPage = false, NotchMusic = false, MusicIndicatorProgress = false }.MediaWanted
            && !new DeckSettings { MusicPage = false, NotchMusic = false, NotchEqualizer = false, MusicIndicatorProgress = false }.MediaWanted
            && !new DeckSettings().ShowsEqualizer(CompactStyle.Line));
        var settings = new DeckSettings();
        settings.SetCompactOrder(CompactStyle.Capsule, [CompactPart.Music, CompactPart.Monitor, CompactPart.Rings]);
        var copy = settings.Copy(); copy.SetCompactOrder(CompactStyle.Capsule, standard);
        check("Compact order: each style keeps its own and Copy is deep", settings.CompactOrder(CompactStyle.Capsule)[0] == CompactPart.Music
            && settings.CompactOrder(CompactStyle.Notch).SequenceEqual(standard));
        var read = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("Compact order: survives saving and reading", read.CapsuleOrder.SequenceEqual([CompactPart.Music, CompactPart.Monitor, CompactPart.Rings, CompactPart.Equalizer, CompactPart.Clock]));
    }
}

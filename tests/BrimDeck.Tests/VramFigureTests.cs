using System.Text.Json;
using BrimDeck.Core;

// The graphics memory as a third figure of the collapsed row's GPU (CompactFigures.Vram): which readings offer it, how a saved choice
// is kept (older tables stay as they were), the share it shows, when it joins the row (StripAdmission), and what a screen reader says.
static class VramFigureTests
{
    private const CompactMonitorItems Cpu = CompactMonitorItems.Cpu, Gpu = CompactMonitorItems.Gpu, Storage_ = CompactMonitorItems.Storage;
    private const string Usage = CompactFigures.Usage, Temperature = CompactFigures.Temperature, Vram = CompactFigures.Vram;

    public static void Run(Action<string, bool> check)
    {
        Choice(check);
        Saved(check);
        Share(check);
        Admission(check);
        Speech(check);
    }

    private static void Choice(Action<string, bool> check)
    {
        check("VRAM figure: only the GPU offers it, between the share and the temperature", CompactFigures.Offered(Gpu).SequenceEqual([Usage, Vram, Temperature])
            && CompactFigures.Offered(Cpu).SequenceEqual([Usage, Temperature]) && CompactFigures.Offered(Storage_).SequenceEqual([Usage, Temperature]));
        check("VRAM figure: a GPU choice in order, each once, any case", CompactFigures.Normalize(Gpu, ["temperature", "vram", "Usage", "VRAM", "x", null])!.SequenceEqual([Usage, Vram, Temperature])
            && CompactFigures.Normalize(Gpu, ["VRAM"])!.SequenceEqual([Vram]));
        check("VRAM figure: CPU and the drives drop it, and keep the rest", CompactFigures.Normalize(Cpu, ["VRAM"]) is null && CompactFigures.Normalize(Storage_, ["Vram"]) is null
            && CompactFigures.Normalize(Cpu, ["VRAM", "Temperature"])!.SequenceEqual([Temperature]));
        var only = new CompactReading { Item = Gpu, Figures = [Vram] };
        check("VRAM figure: not part of the default, shown when chosen", !CompactFigures.Shows(new CompactReading { Item = Gpu }, Vram) && CompactFigures.Shows(only, Vram) && !CompactFigures.Shows(only, Usage));
    }

    // A table saved before the figure keeps what it showed; the figure survives a save and a load, on the GPU only.
    private static void Saved(Action<string, bool> check)
    {
        var old = SettingsMigrations.Read("""{"AppVersion":"1.3.1","CompactReadings":[{"Item":8,"Figures":["Temperature","Usage"],"FiguresInLine":true},{"Item":2,"Figures":["Temperature"]}]}""");
        var gpu = old.CompactReadings.Single(r => r.Item == Gpu);
        check("VRAM figure: a table saved without it is read as it was", gpu.Figures!.SequenceEqual([Usage, Temperature]) && gpu.FiguresInLine
            && !CompactFigures.Shows(gpu, Vram) && old.CompactReadings.Single(r => r.Item == Cpu).Figures!.SequenceEqual([Temperature]));

        var settings = SettingsMigrations.Read("""{"AppVersion":"1.3.1"}""");
        settings.CompactReadings.Single(r => r.Item == Gpu).Figures = [Vram, Usage];
        settings.CompactReadings.Single(r => r.Item == Cpu).Figures = [Vram];
        var again = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("VRAM figure: a choice with it round-trips in order, and is dropped where a reading has none", again.CompactReadings.Single(r => r.Item == Gpu).Figures!.SequenceEqual([Usage, Vram])
            && again.CompactReadings.Single(r => r.Item == Cpu).Figures is null);

        var table = CompactMonitor.Normalize(null);
        CompactMonitor.Apply(table, CompactMonitorMode.Full);
        check("VRAM figure: Full is as before, without it", CompactMonitor.Preset(table) == CompactMonitorMode.Full && !CompactFigures.Shows(table.Single(r => r.Item == Gpu), Vram));
        table.Single(r => r.Item == Gpu).Figures = [Usage, Vram, Temperature];
        check("VRAM figure: a table with it is custom, not Full", CompactMonitor.Preset(table) is null);
        table.Single(r => r.Item == Gpu).Figures = [Usage, Vram];
        check("VRAM figure: two figures with it are not Full either", CompactMonitor.Preset(table) is null);
        CompactMonitor.Apply(table, CompactMonitorMode.Full);
        check("VRAM figure: choosing Full again sets the usual figures", table.Single(r => r.Item == Gpu).Figures!.SequenceEqual([Usage, Temperature]));
    }

    private static MonitorSnapshot Snapshot(ulong? used, ulong? total, bool noReadings = false) => MonitorSnapshot.Empty(4, "test") with
    {
        Gpu = new GpuSample(.1, used is { } bytes ? bytes : Reading<ulong>.Unavailable("")) { DedicatedTotal = total, NoReadings = noReadings }
    };

    private static void Share(Action<string, bool> check)
    {
        check("VRAM share: the dedicated memory in use, at its level", CompactFigures.Memory(Gpu, Snapshot(2, 8)) is { Share: .25, Level: MemoryLevel.Normal }
            && CompactFigures.Memory(Gpu, Snapshot(8, 8)) is { Share: 1 } full && full.Level == ReadingLevels.OfShare(1) && full.Level != MemoryLevel.Normal);
        check("VRAM share: none without a reading, a size, or an adapter awake", CompactFigures.Memory(Gpu, null) is null && CompactFigures.Memory(Gpu, MonitorSnapshot.Empty(4, "waiting")) is null
            && CompactFigures.Memory(Gpu, Snapshot(null, 8)) is null && CompactFigures.Memory(Gpu, Snapshot(2, null)) is null && CompactFigures.Memory(Gpu, Snapshot(2, 0)) is null
            && CompactFigures.Memory(Gpu, Snapshot(2, 8, noReadings: true)) is null);
        check("VRAM share: only the GPU has one", CompactFigures.Memory(Cpu, Snapshot(2, 8)) is null && CompactFigures.Memory(CompactMonitorItems.Memory, Snapshot(2, 8)) is null);
    }

    private static List<CompactReading> Readings(string[]? figures, bool value = true)
    {
        var readings = CompactMonitor.Normalize(null);
        foreach (var reading in readings)
        {
            reading.Visible = reading.Item == Gpu;
            if (reading.Item == Gpu) { reading.Figures = figures is null ? null : [.. figures]; reading.ShowValue = value; }
        }
        return readings;
    }
    private static SensorStates States(SensorState gpu) => new(SensorState.None, gpu, SensorState.Unknown, SensorState.Unknown);

    // The memory joins once its first figure arrives, like a temperature: not before, not unless chosen with the ring's figure on,
    // whether or not the temperature can be read, it stays through a figure missing for a moment and while the adapter sleeps, and
    // leaves only when the adapter shown has no memory of its own.
    private static void Admission(Action<string, bool> check)
    {
        var readings = Readings([Usage, Vram]);
        var admission = new StripAdmission();
        admission.Update(MonitorSnapshot.Empty(4, "waiting"), readings, States(SensorState.Unknown));
        string before = admission.Layout;
        check("VRAM admission: not before its first figure", !admission.Shows(Gpu, StripAdmission.VideoMemory));
        admission.Update(Snapshot(2, 8), readings, States(SensorState.Found));
        string joined = admission.Layout;
        check("VRAM admission: joins with its first figure, and the row is built again", admission.Shows(Gpu, StripAdmission.VideoMemory) && joined != before && !admission.Shows(Gpu, StripAdmission.Heat));
        admission.Update(Snapshot(null, 8), readings, States(SensorState.Found));
        check("VRAM admission: a figure missing for a moment keeps its place", admission.Shows(Gpu, StripAdmission.VideoMemory) && admission.Layout == joined);

        var integrated = new StripAdmission();
        integrated.Update(Snapshot(null, null), readings, States(SensorState.Found));
        check("VRAM admission: an adapter that reports no memory never shows it", !integrated.Shows(Gpu, StripAdmission.VideoMemory) && integrated.Layout == before);
        var noHeat = new StripAdmission();
        noHeat.Update(Snapshot(2, 8), Readings([Usage, Vram, Temperature]), States(SensorState.None));
        check("VRAM admission: shown where the GPU temperature cannot be read", noHeat.Shows(Gpu, StripAdmission.VideoMemory) && !noHeat.Shows(Gpu, StripAdmission.Heat));
        noHeat.Update(Snapshot(2, 8, noReadings: true), Readings([Usage, Vram, Temperature]), States(SensorState.None));
        check("VRAM admission: an adapter asleep keeps its place", noHeat.Shows(Gpu, StripAdmission.VideoMemory));
        var gone = new StripAdmission();
        gone.Update(Snapshot(2, 8), readings, States(SensorState.Found));
        gone.Update(Snapshot(null, null), readings, States(SensorState.Found));
        check("VRAM admission: leaves once the adapter shown has no memory of its own", !gone.Shows(Gpu, StripAdmission.VideoMemory) && gone.Layout == before);
        check("VRAM missing: only an adapter without a memory size", CompactFigures.MemoryMissing(Snapshot(null, null)) && CompactFigures.MemoryMissing(Snapshot(null, 0))
            && !CompactFigures.MemoryMissing(Snapshot(null, 8)) && !CompactFigures.MemoryMissing(Snapshot(null, 8, noReadings: true))
            && !CompactFigures.MemoryMissing(null) && !CompactFigures.MemoryMissing(MonitorSnapshot.Empty(4, "waiting")));

        var plain = new StripAdmission();
        plain.Update(Snapshot(2, 8), Readings(null), States(SensorState.Found));
        check("VRAM admission: not shown unless chosen", !plain.Shows(Gpu, StripAdmission.VideoMemory));
        var hidden = new StripAdmission();
        hidden.Update(Snapshot(2, 8), Readings([Vram], value: false), States(SensorState.Found));
        check("VRAM admission: not shown while the ring shows no figure", !hidden.Shows(Gpu, StripAdmission.VideoMemory));
        var cpuOnly = Readings([Usage]);
        cpuOnly.Single(r => r.Item == Cpu).Figures = [Vram];
        var wrong = new StripAdmission();
        wrong.Update(Snapshot(2, 8), cpuOnly, States(SensorState.Found));
        check("VRAM admission: a CPU never has it", !wrong.Shows(Cpu, StripAdmission.VideoMemory));
    }

    private static void Speech(Action<string, bool> check)
    {
        var language = Loc.Language;
        try
        {
            Loc.Use(Loc.Chinese);
            check("VRAM speech: the row's GPU reads its memory when shown", MonitorSpeech.StripGpu(.12, .18) == "GPU 12%，显存 18%" && MonitorSpeech.StripGpu(.12, null) == "GPU 12%"
                && MonitorSpeech.StripGpu(0, 0) == "GPU 0%，显存 0%");
            Loc.Use(Loc.English);
            check("VRAM speech: in English too", MonitorSpeech.StripGpu(.12, .18) == "GPU 12%, VRAM 18%");
        }
        finally { Loc.Use(language); }
    }
}

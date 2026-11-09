using BrimDeck.Core;

// The first load of the collapsed row's later parts: the drives, and the temperatures shown with CPU, GPU and the drives. Before
// their first figures the row leaves them out and does not change; when the figures arrive the row is built once more and they
// come in; a machine without the sensor never shows them, and nothing is built again for it.
static class StripAdmissionTests
{
    private const CompactMonitorItems Cpu = CompactMonitorItems.Cpu, Gpu = CompactMonitorItems.Gpu, Storage_ = CompactMonitorItems.Storage,
        // The machine's power and battery and the frame rate join by their own sensors (PowerTests, FrameRateTests), the devices'
        // battery by the devices, not by the system readings (DevicesPageTests); these runs leave them aside.
        Later = StripAdmission.Later & ~CompactMonitorItems.Power & ~CompactMonitorItems.Charge & ~CompactMonitorItems.Battery & ~CompactMonitorItems.Frames;
    private const CompactMonitorItems EveryReading = CompactMonitor.Every & ~CompactMonitorItems.Power & ~CompactMonitorItems.Charge & ~CompactMonitorItems.Frames;
    private const SensorState Unknown = SensorState.Unknown, Found = SensorState.Found, None = SensorState.None;
    private const string Heat = StripAdmission.Heat;

    public static void Run(Action<string, bool> check)
    {
        FirstLoad(check);
        Missing(check);
        OnlyDrives(check);
        Temperatures_(check);
        Letters(check);
        Presence(check);
    }

    // The table as saved by default with only these readings shown; heat: CPU, GPU and the drives show their temperatures too.
    private static List<CompactReading> Readings(CompactMonitorItems shown, bool heat = false)
    {
        var readings = CompactMonitor.Normalize(null);
        foreach (var reading in readings)
        {
            reading.Visible = (shown & reading.Item) != 0;
            if (heat && CompactFigures.Has(reading.Item)) reading.Figures = [CompactFigures.Usage, CompactFigures.Temperature];
        }
        return readings;
    }
    private static SensorStates States(SensorState cpu, SensorState gpu, SensorState storage, SensorState disks = Unknown) => new(cpu, gpu, storage, disks);
    private static readonly string Windows = Storage.WindowsLetter?.ToUpperInvariant() ?? "C:";

    private static MonitorSnapshot Snapshot(double? gpuHeat, bool drive) => MonitorSnapshot.Empty(4, "test") with
    {
        Gpu = new GpuSample(.1, Reading<ulong>.Unavailable("")) { Temperature = gpuHeat },
        Storage = drive ? [new StorageDrive(0, "SSD", [new StorageVolume(Windows, "", 100UL << 30, 40UL << 30)])] : []
    };

    // Counts the times the row would be built again (the hidden readings or the parts showing changing) and what arrives each time.
    private static (int Rebuilds, List<CompactMonitorItems> Arrivals, CompactMonitorItems Hidden) Run(IReadOnlyList<CompactReading> readings,
        IEnumerable<(MonitorSnapshot? Snapshot, SensorStates States)> ticks)
    {
        var admission = new StripAdmission();
        var (built, layout) = (admission.Hidden & Later, admission.Layout);
        int rebuilds = 0; var arrivals = new List<CompactMonitorItems>();
        foreach (var (snapshot, states) in ticks)
        {
            var (all, arriving) = admission.Update(snapshot, readings, states);
            var hidden = all & Later; arriving &= Later;
            if (hidden == built && admission.Layout == layout) continue;
            rebuilds++; arrivals.Add(arriving); (built, layout) = (hidden, admission.Layout);
        }
        return (rebuilds, arrivals, built);
    }

    private static void FirstLoad(Action<string, bool> check)
    {
        check("Strip admission: the drives are left out before any figure", (new StripAdmission().Hidden & Later) == Later);
        var readings = Readings(EveryReading);
        var (rebuilds, arrivals, hidden) = Run(readings, [(null, States(Unknown, Unknown, Unknown)), (Snapshot(null, false), States(Unknown, Unknown, Unknown)),
            (Snapshot(null, false), States(None, Found, Unknown)), (Snapshot(null, true), States(None, Found, Found)), (Snapshot(null, true), States(None, Found, Found))]);
        check("Strip admission: the row stays as it is until the drives' first figure, then they join once", rebuilds == 1 && arrivals.Single() == Storage_ && hidden == CompactMonitorItems.None);
        var flicker = Run(readings, [(Snapshot(null, true), States(None, Found, Found)), (Snapshot(null, false), States(None, Found, Found)), (Snapshot(null, true), States(None, Found, Found))]);
        check("Strip admission: a figure missing for a moment does not take the drives out again", flicker.Rebuilds == 1 && (flicker.Hidden & Later) == CompactMonitorItems.None);
    }

    private static void Missing(Action<string, bool> check)
    {
        var readings = Readings(EveryReading, heat: true);
        var none = Run(readings, [(null, States(Unknown, Unknown, Unknown)), (Snapshot(null, false), States(None, Unknown, None)), (Snapshot(null, false), States(None, None, None, None))]);
        check("Strip admission: without sensors nothing joins, and the row is never built again for them", none.Rebuilds == 0 && (none.Hidden & Later) == Later);
        var lost = Run(readings, [(Snapshot(48, true), States(None, Found, Found)), (Snapshot(null, true), States(None, None, Found))]);
        check("Strip admission: a temperature leaves only once its sensor is known to be missing", lost.Rebuilds == 2 && (lost.Hidden & Later) == CompactMonitorItems.None);
    }

    // After a restart with only the drives chosen for the row: before their first figures the row shows nothing and has no width,
    // yet the system must be read for it (Awaits), or the first figure would never come. Once it has come the drives join; on a
    // machine without them the row stops asking.
    private static void OnlyDrives(Action<string, bool> check)
    {
        var readings = Readings(Storage_);
        var admission = new StripAdmission();
        check("Strip admission (drives only, after a restart): the row shows nothing yet", (Storage_ & ~admission.Hidden) == CompactMonitorItems.None);
        check("Strip admission (drives only): the system is still read for them", admission.Awaits(readings, null, States(Unknown, Unknown, Unknown)));
        var (hidden, arriving) = admission.Update(Snapshot(null, true), readings, States(None, None, Found));
        check("Strip admission (drives only): the first figure arrives and they join", (hidden & Later) == CompactMonitorItems.None && arriving == Storage_
            && !admission.Awaits(readings, Snapshot(null, true), States(None, None, Found)));
        var bare = new StripAdmission();
        bare.Update(Snapshot(null, false), readings, States(None, None, None));
        check("Strip admission: without drives the row stops asking for readings", !bare.Awaits(readings, null, States(None, None, None)));
        check("Strip admission: other readings do not wait", !new StripAdmission().Awaits(Readings(Cpu | CompactMonitorItems.Memory, heat: true), null, States(Unknown, Unknown, Unknown)));
    }

    // A ring's temperature shows once it gives its first figure, only when chosen and with the ring's figure on, and the row is
    // built again when it joins or leaves.
    private static void Temperatures_(Action<string, bool> check)
    {
        var readings = Readings(Cpu | Gpu, heat: true);
        var admission = new StripAdmission();
        admission.Update(Snapshot(null, false), readings, States(Unknown, Unknown, Unknown));
        string before = admission.Layout;
        check("Ring temperatures: none before their first figures", !admission.Shows(Cpu, Heat) && !admission.Shows(Gpu, Heat));
        admission.Update(Snapshot(48, false), readings, States(None, Found, Unknown));
        check("Ring temperatures: the GPU's joins, the CPU's never does without a sensor", admission.Shows(Gpu, Heat) && !admission.Shows(Cpu, Heat) && admission.Layout != before);
        string joined = admission.Layout;
        admission.Update(Snapshot(null, false), readings, States(None, Found, Unknown));
        check("Ring temperatures: a figure missing for a moment keeps its place", admission.Shows(Gpu, Heat) && admission.Layout == joined);
        var usageOnly = Readings(Cpu | Gpu);
        var plain = new StripAdmission();
        plain.Update(Snapshot(48, false), usageOnly, States(None, Found, Unknown));
        check("Ring temperatures: not shown unless chosen", !plain.Shows(Gpu, Heat));
        usageOnly.Single(r => r.Item == Gpu).Figures = [CompactFigures.Temperature];
        usageOnly.Single(r => r.Item == Gpu).ShowValue = false;
        plain.Update(Snapshot(48, false), usageOnly, States(None, Found, Unknown));
        check("Ring temperatures: not shown while the ring shows no figure", !plain.Shows(Gpu, Heat));
    }

    // Two drives: C: (40% used) and D: (90% used) on a solid-state drive at 41 °C, E: on a hard disk.
    private static MonitorSnapshot Drives() => MonitorSnapshot.Empty(4, "test") with
    {
        Storage =
        [
            new StorageDrive(0, "SSD", [new StorageVolume("C:", "", 100UL << 30, 60UL << 30), new StorageVolume("D:", "Data", 100UL << 30, 10UL << 30)])
                { SolidState = true, Temperature = new DiskTemperature("SSD", 41) },
            new StorageDrive(1, "HDD", [new StorageVolume("E:", "", 400UL << 30, 100UL << 30)])
        ]
    };

    // The drive letters: chosen in the table, each with its space and, when chosen, its drive's temperature, shown once they give
    // a figure.
    private static void Letters(Action<string, bool> check)
    {
        check("Letters: the volume holding Windows by default", CompactSources.Of(new CompactReading { Item = Storage_ }).SequenceEqual([Windows]));
        check("Letters: a saved choice is kept in order, each once, without what is not a letter",
            CompactSources.Normalize(Storage_, ["e:", "CPU", "C:", "e:", null, "x"])!.SequenceEqual(["C:", "E:"]) && CompactSources.Normalize(Storage_, ["GPU"]) is null
            && CompactSources.Normalize(Cpu, ["C:"]) is null);
        var saved = CompactMonitor.Normalize([new CompactReading { Item = Storage_, Sources = ["e:", "c:"] }]);
        check("Letters: the table keeps the choice", saved.Single(r => r.Item == Storage_).Sources!.SequenceEqual(["C:", "E:"]));
        var copied = saved.Single(r => r.Item == Storage_).Copy();
        copied.Sources!.Add("F:");
        check("Letters: a copied reading has its own choice", saved.Single(r => r.Item == Storage_).Sources!.Count == 2);
        check("Letters: the tag is the letter", CompactSources.Tag("D:") == "D");

        var snapshot = Drives();
        check("Letters: each volume's own share and level", CompactSources.Share("D:", snapshot) is { Share: .9, Level: MemoryLevel.Elevated }
            && CompactSources.Share("C:", snapshot) is { Share: .4 } && CompactSources.Share("F:", snapshot) is null && CompactSources.Share("C:", null) is null);
        check("Letters: the temperature of the drive holding the volume", CompactSources.Heat("D:", snapshot) is { Celsius: 41 } && CompactSources.Heat("E:", snapshot) is null);
        var states = States(Found, Found, Found, Found);
        check("Letters: a letter not found and a hard disk's temperature are missing once the drives are read",
            CompactSources.Missing("F:", snapshot, states) && !CompactSources.Missing("E:", snapshot, states) && CompactSources.HeatMissing("E:", snapshot, states)
            && !CompactSources.HeatMissing("C:", snapshot, states) && !CompactSources.Missing("F:", snapshot, States(Found, Found, Unknown))
            && CompactSources.HeatMissing("C:", snapshot, States(Found, Found, Found, None)));

        var readings = Readings(Storage_, heat: true);
        var drives = readings.Single(r => r.Item == Storage_);
        drives.Sources = ["C:", "E:", "F:"];
        var admission = new StripAdmission();
        admission.Update(snapshot, readings, states);
        check("Letters: only the letters with a figure show, in order", admission.Letters(drives).SequenceEqual(["C:", "E:"])
            && admission.Shows(Storage_, "C:" + Heat) && !admission.Shows(Storage_, "E:" + Heat));
        string layout = admission.Layout;
        drives.Figures = [CompactFigures.Temperature];
        check("Letters: with only the temperature chosen, a hard disk's letter does not show", admission.Letters(drives).SequenceEqual(["C:"]));
        admission.Update(snapshot, readings, states);
        check("Letters: a change of the figures builds the row again", admission.Layout != layout);
        drives.Sources = ["D:"];
        check("Letters: a letter taken out of the choice no longer shows", admission.Letters(drives).Count == 0);
    }

    // The row's presence: a reading that arrives comes in from nothing and eases to its place; one the settings choose shows at once;
    // with the animations off it is in place at once.
    private static void Presence(Action<string, bool> check)
    {
        var shown = Cpu | CompactMonitorItems.Memory;
        var presence = new StripPresence();
        presence.Set(shown, shown, motion: true);
        double now = Eased.Now;
        check("Strip presence: readings in place before anything arrives", presence.At(Cpu, now) == 1 && !presence.Moving(now) && presence.At(Storage_, now) == 0);
        presence.Arrive(Storage_);
        bool moving = presence.Set(shown | Storage_, shown | Storage_, motion: true);
        now = Eased.Now;
        check("Strip presence: an arriving reading eases in from nothing, the others stay", moving && presence.At(Storage_, now) < .5 && presence.Moving(now)
            && presence.At(Cpu, now) == 1);
        check("Strip presence: it reaches its place", presence.At(Storage_, now + Eased.Seconds * 2) == 1);
        presence.Set(shown | Storage_, shown | Storage_, motion: true);
        check("Strip presence: a later arrange does not start it again", presence.At(Storage_, Eased.Now + Eased.Seconds * 2) == 1);
        var chosen = new StripPresence();
        chosen.Set(shown, shown, motion: true);
        chosen.Set(shown | Storage_, shown | Storage_, motion: true);
        check("Strip presence: a reading the settings choose shows at once", chosen.At(Storage_, Eased.Now) == 1 && !chosen.Moving(Eased.Now));
        var still = new StripPresence();
        still.Set(shown, shown, motion: false);
        still.Arrive(Storage_);
        still.Set(shown | Storage_, shown | Storage_, motion: false);
        check("Strip presence: with animations off an arrival is in place at once", still.At(Storage_, Eased.Now) == 1 && !still.Moving(Eased.Now));
        var crowded = new StripPresence();
        crowded.Set(shown, shown, motion: true);
        crowded.Arrive(Storage_);
        crowded.Set(shown | Storage_, shown, motion: true);
        check("Strip presence: an arrival with no room stays out", crowded.At(Storage_, Eased.Now + Eased.Seconds * 2) == 0);
    }
}

using BrimDeck.Core;

// The whole machine's power and its own battery: which source wins and how it is labelled (PowerResolver), the counters' names
// (FromCounters), a battery's state, time and health from made-up reports (BatteryModel), the collapsed row's Power and Charge readings,
// the monitor page's lines for them, and every theme's look.
static class PowerTests
{
    private static Reading<double> W(double watts) => watts;
    private static Reading<double> None_(string why = "none") => Reading<double>.Unavailable(why);
    private static BatteryCell Cell(double remaining, double full, double design, double? rate = null, bool charging = false, bool discharging = false)
        => new("b", remaining, full, design, rate, charging, discharging);

    public static void Run(Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try
        {
            Resolver(check);
            Counters(check);
            Batteries(check);
            Formats(check);
            Admission(check);
            Readings(check);
            Layout(check);
            Looks(check);
            Texts(check);
        }
        finally { Loc.Use(language); }
    }

    private static void Resolver(Action<string, bool> check)
    {
        // A desktop without a battery: the processor and a graphics adapter in use are summed, and it is an estimate.
        var desktop = PowerResolver.Resolve(new PowerInputs { Cpu = W(42.5), Gpus = [new GpuPowerInput("NVIDIA GeForce RTX 4070", W(118.2))] });
        check("Power: a desktop sums the processor and the graphics board, marked an estimate",
            desktop.TryGet(out var sum) && Math.Abs(sum.Watts - 160.7) < 1e-9 && sum.Estimated && sum.Source == PowerSource.Components && sum.Parts.Count == 2);
        check("Power: the estimate names its parts and where each is read", sum!.Parts[0].Name == "CPU" && sum.Parts[0].Source == "RAPL" && sum.Parts[1].Source == PowerResolver.GpuSource && sum.Parts[1].Name == "GPU");

        // A desktop with nothing to read: unavailable with a reason, never a zero.
        var none = PowerResolver.Resolve(new PowerInputs());
        check("Power: no source at all is unavailable with a reason", !none.HasValue && none.Reason.Length > 10);
        check("Power: a stated reason is kept", !PowerResolver.Resolve(new PowerInputs { NoSourceReason = "无计数器" }).HasValue && PowerResolver.Resolve(new PowerInputs { NoSourceReason = "无计数器" }).Reason == "无计数器");

        // A whole-system meter wins over everything and is a measurement.
        var metered = PowerResolver.Resolve(new PowerInputs { SystemMeter = W(95.5), BatteryDischarge = W(30), Cpu = W(40) });
        check("Power: a system meter is measured and comes first", metered.TryGet(out var m) && m.Watts == 95.5 && !m.Estimated && m.Source == PowerSource.SystemMeter);

        // On battery the discharge is the whole machine, measured, with the parts kept for the breakdown.
        var discharging = PowerResolver.Resolve(new PowerInputs { BatteryDischarge = W(23.4), Cpu = W(9.1) });
        check("Power: on battery the discharge rate is the measured whole", discharging.TryGet(out var d) && d.Watts == 23.4 && d.Origin == PowerOrigin.Measured && d.Source == PowerSource.Battery && d.Parts.Count == 1);

        // Plugged in and charging: no discharge, so the parts again.
        var charging = PowerResolver.Resolve(new PowerInputs { BatteryDischarge = None_("not on battery"), Cpu = W(22) });
        check("Power: charging on mains falls back to the sum of parts", charging.TryGet(out var c) && c.Estimated && c.Watts == 22);

        // A battery at rest (under half a watt) or a meter at rest is not the machine's draw.
        check("Power: a meter at rest under a watt is passed over", PowerResolver.Resolve(new PowerInputs { SystemMeter = W(.2), Cpu = W(15) }).OrDefault is { Source: PowerSource.Components });
        check("Power: a discharge under half a watt is passed over", PowerResolver.Resolve(new PowerInputs { BatteryDischarge = W(.3), Cpu = W(15) }).OrDefault is { Source: PowerSource.Components });

        // Broken counters never show.
        check("Power: NaN, negative and absurd readings are ignored", !PowerResolver.Resolve(new PowerInputs { Cpu = W(double.NaN), SystemMeter = W(-4), BatteryDischarge = W(99999) }).HasValue);

        // An idle discrete adapter is listed as missing, not summed as zero.
        var idle = PowerResolver.Resolve(new PowerInputs { Cpu = W(18), Gpus = [new GpuPowerInput("NVIDIA RTX 4060", None_("空闲，未读取"))] });
        check("Power: a sleeping adapter is not summed, and says why", idle.TryGet(out var i) && i.Watts == 18 && i.Missing.Count == 1 && i.Missing[0].Contains("空闲"));

        // Memory from its own meter is added; a zero memory meter is left out.
        check("Power: memory with its own meter is added, a zero one is not", PowerResolver.Resolve(new PowerInputs { Cpu = W(10), Memory = W(3) }).OrDefault is { Watts: 13 }
            && PowerResolver.Resolve(new PowerInputs { Cpu = W(10), Memory = W(0) }).OrDefault is { Watts: 10 } z && z.Parts.Count == 1);

        // Two identical cards are two readings, each its own; one the driver does not list twice has none of its own.
        (string, double?)[] twins = [("NVIDIA RTX 4090", 210.5), ("NVIDIA RTX 4090", 198.2)];
        check("Power: identical adapters are read by their order, not one counted twice", PowerResolver.PickGpu(twins, "NVIDIA RTX 4090", 0).OrDefault == 210.5 && PowerResolver.PickGpu(twins, "NVIDIA RTX 4090", 1).OrDefault == 198.2
            && !PowerResolver.PickGpu(twins, "NVIDIA RTX 4090", 2).HasValue);
        // One line for two identical cards (the driver's first lines not all in yet) is the first card's, not both cards' (210 W, not 420 W).
        (string, double?)[] single = [("NVIDIA RTX 4090", 210.0)];
        check("Power: a single line is not read again for a second identical adapter", PowerResolver.PickGpu(single, "NVIDIA RTX 4090", 0).OrDefault == 210
            && !PowerResolver.PickGpu(single, "NVIDIA RTX 4090", 1).HasValue && !PowerResolver.PickGpu([("NVIDIA GPU", 9.0)], "NVIDIA RTX 3050 Laptop", 1).HasValue);
        (string, double?)[] mixed = [("NVIDIA GeForce RTX 4060 Laptop GPU", 14.5), ("NVIDIA T400", null)];
        check("Power: an adapter is found by its name, a figure that is not a number is none", PowerResolver.PickGpu(mixed, "NVIDIA GeForce RTX 4060 Laptop GPU", 0).OrDefault == 14.5 && !PowerResolver.PickGpu(mixed, "NVIDIA T400", 0).HasValue
            && !PowerResolver.PickGpu(mixed, "NVIDIA RTX A2000", 0).HasValue && PowerResolver.PickGpu([("NVIDIA GPU", 9.0)], "NVIDIA RTX 3050 Laptop", 0).OrDefault == 9.0);
        check("Power: an absurd driver figure is none", !PowerResolver.PickGpu([("NVIDIA X", 99999.0)], "NVIDIA X", 0).HasValue);

        var stats = new PowerStats(10);
        foreach (double watts in new[] { 20.0, 60, 40, double.NaN, -3 }) stats.Add(watts);
        check("Power: an ended helper is started again after the interval, not before, and a running one never", !PowerResolver.RestartDue(true, 10, 0) && PowerResolver.RestartDue(true, PowerResolver.RestartSeconds, 0)
            && !PowerResolver.RestartDue(true, 80, 60) && PowerResolver.RestartDue(true, 100, 60) && !PowerResolver.RestartDue(false, 1000, 0));
        check("Power: the peak and the average count plausible readings only", stats.Count == 3 && stats.Peak == 60 && Math.Abs(stats.Average - 40) < 1e-9);
        stats.Reset();
        check("Power: stats start over", stats.Count == 0 && stats.Peak == 0 && stats.Average == 0);
        stats.Add(double.NaN, 99); stats.Add(30, 100); stats.Add(40, 101);
        check("Power: the stats begin at their first counted reading", stats.Started == 100);
        // As many readings as the curve: a minute at the one-second interval, two at the two-second one.
        var everySecond = new PowerStats(60);
        for (int at = 0; at <= 100; at++) everySecond.Add(at == 20 ? 99 : 10 + at % 3, at);
        check("Power: the stats keep the curve's readings only", everySecond.Count == 60 && everySecond.Peak == 12 && everySecond.Summary(100) is { Count: 60, Seconds: 60 });
        var everyTwo = new PowerStats(60);
        for (int at = 0; at <= 200; at += 2) everyTwo.Add(10, at);
        check("Power: at two seconds they cover two minutes", everyTwo.Summary(200) is { Count: 60, Seconds: 120 }
            && PowerText.Table(Reading<PowerSample>.Of(new PowerSample(10, PowerOrigin.Estimated, PowerSource.Components, [])), everyTwo.Summary(200), PowerTrack.None, PowerTrack.None)!.Rows[1].Label == "2 分钟内峰值");
        var filling = new PowerStats(60);
        filling.Add(10, 5); filling.Add(20, 6); filling.Add(30, 7);
        check("Power: before they are full they cover from their first reading", filling.Summary(7) is { Count: 3, Seconds: 2, Peak: 30, Average: 20 });

        // The CPU's and the GPU's own power, for the page's power line: a part's watts, two adapters together, and none where it is not read.
        var twoCards = new PowerSample(80, PowerOrigin.Estimated, PowerSource.Components, [new("cpu", "CPU", 20, "RAPL"), new("gpu", "A", 25, "x"), new("gpu", "B", 35, "x")]);
        check("Power: a part's own watts, adapters summed, none where absent", PowerResolver.PartWatts(twoCards, "cpu") == 20 && PowerResolver.PartWatts(twoCards, "gpu") == 60
            && PowerResolver.PartWatts(twoCards with { Parts = [] }, "gpu") is null);
        var trail = new PowerTrail(3);
        foreach (var (watts, at) in new[] { (10.0, 1.0), (30, 2), (double.NaN, 3), (20, 4), (15, 5) }) trail.Add(watts, at);
        var track = trail.Track(9);
        check("Power: a series keeps its last readings, and its peak, mean and time over the same readings", track.History.SequenceEqual([30.0, 20, 15]) && track.Summary is { Peak: 30, Count: 3, Seconds: 8 }
            && Math.Abs(track.Summary.Average - 65.0 / 3) < 1e-9);
        trail.Clear();
        check("Power: a cleared series starts over", trail.Track(10) == PowerTrack.None);
    }

    private static void Counters(Action<string, bool> check)
    {
        var (system, cpu, memory) = PowerResolver.FromCounters(
            [("RAPL_Package0_PKG", 55_089.2), ("RAPL_Package0_PP0", 43_764.3), ("RAPL_Package0_PP1", 0), ("RAPL_Package0_DRAM", 0), ("_Total", 0)], []);
        check("Counters: the package power is the processor's, in watts", cpu.TryGet(out var package) && Math.Abs(package - 55.0892) < 1e-4);
        check("Counters: cores and graphics inside the package are not added again, a zero DRAM is a zero", memory.TryGet(out var dram) && dram == 0 && !system.HasValue);
        var two = PowerResolver.FromCounters([("RAPL_Package0_PKG", 100_000), ("RAPL_Package1_PKG", 90_000), ("RAPL_Package0_DRAM", 12_000), ("RAPL_Package1_DRAM", 11_000)], []);
        check("Counters: sockets add up", two.Cpu.OrDefault == 190 && two.Memory.OrDefault == 23);
        var platform = PowerResolver.FromCounters([("Battery rail", 12_000), ("Board", 8_000), ("_Total", 20_000), ("RAPL_Package0_PKG", 30_000)], [("Power Meter (0)", 5_000), ("_Total", 5_000)]);
        check("Counters: the ACPI meter stands for the system meter, not on top of other channels; _Total is not counted twice", platform.System.OrDefault == 5 && platform.Cpu.OrDefault == 30);
        check("Counters: channels that are not RAPL make the system meter without an ACPI one; the platform domain counts", PowerResolver.FromCounters([("Battery rail", 12_000), ("Board", 8_000), ("_Total", 20_000)], []).System.OrDefault == 20
            && PowerResolver.FromCounters([("RAPL_Package0_PSYS", 70_000), ("RAPL_Package0_PKG", 30_000)], []) is { } psys && psys.System.OrDefault == 70 && psys.Cpu.OrDefault == 30);
        var empty = PowerResolver.FromCounters([], []);
        check("Counters: none is unavailable, not zero", !empty.System.HasValue && !empty.Cpu.HasValue && !empty.Memory.HasValue);
        var broken = PowerResolver.FromCounters([("RAPL_Package0_PKG", double.NaN), ("RAPL_Package0_DRAM", -5)], []);
        check("Counters: a NaN or negative reading is skipped", !broken.Cpu.HasValue && !broken.Memory.HasValue);
    }

    private static void Batteries(Action<string, bool> check)
    {
        check("Battery: none listed is unavailable, with the reason a desktop shows", !BatteryModel.Build(new BatteryInputs([])).HasValue && BatteryModel.Build(new BatteryInputs([])).Reason == BatteryModel.NoBattery);

        // Discharging at 23.4 W with 40 Wh of 60 Wh left; Windows says 1 h 50 min.
        var onBattery = BatteryModel.Build(new BatteryInputs([Cell(40_000, 60_000, 66_000, -23_400, discharging: true)], AcOnline: false, WindowsRemaining: TimeSpan.FromMinutes(110)));
        check("Battery: discharging, the share, the rate and the state", onBattery.TryGet(out var b) && Math.Abs(b.Percent - 66.667) < .01 && b.State == BatteryState.Discharging && b.HasRate && Math.Abs(b.Watts - 23.4) < 1e-9);
        check("Battery: Windows' time left is used when it gives one", b!.TimeLeft == TimeSpan.FromMinutes(110) && b.TimeToFull is null);
        check("Battery: health is full charge over design capacity", b.Health is { } h && Math.Abs(h - 60_000.0 / 66_000) < 1e-9 && b.FullWh == 60 && b.DesignWh == 66 && b.RemainingWh == 40);
        var computed = BatteryModel.Build(new BatteryInputs([Cell(40_000, 60_000, 66_000, -20_000, discharging: true)], AcOnline: false)).OrDefault!;
        check("Battery: without Windows' estimate the time is the charge over the rate", computed.TimeLeft is { } left && Math.Abs(left.TotalHours - 2) < 1e-9);

        // Charging at 31 W.
        var plugged = BatteryModel.Build(new BatteryInputs([Cell(40_000, 60_000, 66_000, 31_000, charging: true)], AcOnline: true)).OrDefault!;
        check("Battery: charging, with the time to full", plugged.State == BatteryState.Charging && plugged.Charging && plugged.TimeLeft is null
            && plugged.TimeToFull is { } toFull && Math.Abs(toFull.TotalHours - 20.0 / 31) < 1e-9 && plugged.AcOnline);

        // Full on mains, and held below full by a charge limit.
        check("Battery: full on mains", BatteryModel.Build(new BatteryInputs([Cell(59_000, 60_000, 66_000, 0)], AcOnline: true)).OrDefault is { State: BatteryState.Full });
        var limited = BatteryModel.Build(new BatteryInputs([Cell(48_000, 60_000, 66_000, 0)], AcOnline: true)).OrDefault!;
        check("Battery: on mains below full and not charging is plugged in (a charge limit)", limited.State == BatteryState.PluggedIn && limited.TimeToFull is null && limited.TimeLeft is null && !limited.HasRate);
        check("Battery: a rate within the noise floor is idle, with no absurd time",
            BatteryModel.Build(new BatteryInputs([Cell(30_000, 60_000, 66_000, -300)], AcOnline: false)).OrDefault is { TimeLeft: null, HasRate: false, State: BatteryState.Discharging });
        check("Battery: a time past two days is left out", BatteryModel.Build(new BatteryInputs([Cell(50_000, 60_000, 66_000, -600, discharging: true)], AcOnline: false)).OrDefault is { TimeLeft: null });

        // Battery saver and the power mode pass through.
        var saver = BatteryModel.Build(new BatteryInputs([Cell(30_000, 60_000, 66_000, -8_000, discharging: true)], AcOnline: false, BatterySaver: true, Mode: PowerMode.BestEfficiency)).OrDefault!;
        check("Battery: Battery saver and the power mode are kept", saver.BatterySaver && saver.Mode == PowerMode.BestEfficiency && !plugged.BatterySaver);

        // Two batteries: capacities add up and the share is weighted by size (15/30 and 60/60 Wh is 83 %, not 75 %).
        var two = BatteryModel.Build(new BatteryInputs([Cell(15_000, 30_000, 33_000, -4_000, discharging: true), Cell(60_000, 60_000, 66_000, -6_000, discharging: true)], AcOnline: false)).OrDefault!;
        check("Battery: two batteries are one, weighted by size", two.Count == 2 && Math.Abs(two.Percent - 83.333) < .01 && Math.Abs(two.Watts - 10) < 1e-9 && two.FullWh == 90 && two.DesignWh == 99);
        check("Battery: two batteries' time is the sum of what is left over the sum of the rates", two.TimeLeft is { } both && Math.Abs(both.TotalHours - 7.5) < 1e-9);

        // Two batteries of 40 Wh where only the first says its design capacity and its rate: no total is made of the first alone (it would read
        // a health of 100 %, four hours left and 10 W measured for the whole machine).
        var partialInputs = new BatteryInputs([new BatteryCell("a", 20_000, 40_000, 50_000, -10_000, Discharging: true), new BatteryCell("b", 20_000, 40_000, null, null, Discharging: true)], AcOnline: false);
        var partial = BatteryModel.Build(partialInputs).OrDefault!;
        check("Battery: a capacity or rate one battery lacks makes no total", partial.Health is null && partial.DesignWh is null && !partial.HasRate && partial.TimeLeft is null
            && partial.FullWh == 80 && partial.RemainingWh == 40 && Math.Abs(partial.Percent - 50) < 1e-9 && partial.State == BatteryState.Discharging);
        check("Battery: ...and its discharge is not the machine's measured draw", !BatteryModel.Discharge(partialInputs, BatteryModel.Build(partialInputs)).HasValue);
        check("Battery: ...while Windows' own time is still used", BatteryModel.Build(partialInputs with { WindowsRemaining = TimeSpan.FromMinutes(200) }).OrDefault is { TimeLeft: { TotalMinutes: 200 } });

        // A system that reports only a percentage (no capacities, no rate).
        var bare = BatteryModel.Build(new BatteryInputs([new BatteryCell("system", null, null, null, null)], AcOnline: false, WindowsPercent: 55)).OrDefault!;
        check("Battery: without capacities the system's own share is used, and there is no health", Math.Abs(bare.Percent - 55) < 1e-9 && bare.Health is null && bare.FullWh is null && bare.State == BatteryState.Discharging);
        check("Battery: no share at all is unavailable", !BatteryModel.Build(new BatteryInputs([new BatteryCell("system", null, null, null, null)])).HasValue);
        check("Battery: a full charge above the design capacity reads 100 % health", BatteryModel.Build(new BatteryInputs([Cell(50_000, 70_000, 66_000, 0)], AcOnline: true)).OrDefault is { Health: 1 });

        // The whole machine's draw is the discharge only while it discharges.
        var inputs = new BatteryInputs([Cell(40_000, 60_000, 66_000, -23_400, discharging: true)], AcOnline: false);
        check("Battery: the discharge feeds the power reading on battery only", BatteryModel.Discharge(inputs, BatteryModel.Build(inputs)).OrDefault is { } w && Math.Abs(w - 23.4) < 1e-9
            && !BatteryModel.Discharge(new BatteryInputs([Cell(40_000, 60_000, 66_000, 31_000, charging: true)], AcOnline: true), plugged.AsReading()).HasValue
            && !BatteryModel.Discharge(inputs, Reading<BatterySample>.Unavailable("x")).HasValue);

        // On mains the battery covers only a shortfall (a weak charger): that is not the machine's draw.
        var weak = new BatteryInputs([Cell(40_000, 60_000, 66_000, -23_400, discharging: true)], AcOnline: true);
        check("Battery: on mains a discharge is not the machine's draw", BatteryModel.Build(weak).OrDefault is { State: BatteryState.Discharging } && !BatteryModel.Discharge(weak, BatteryModel.Build(weak)).HasValue);
        // The system's own fallback cell (the listing gave none): discharging off mains, with Windows' time.
        var flagged = BatteryModel.Build(new BatteryInputs([new BatteryCell("system", null, null, null, null, false, true)], AcOnline: false, WindowsPercent: 40, WindowsRemaining: TimeSpan.FromMinutes(90))).OrDefault!;
        check("Battery: the fallback cell discharges off mains with Windows' time", flagged.State == BatteryState.Discharging && flagged.TimeLeft == TimeSpan.FromMinutes(90) && Math.Abs(flagged.Percent - 40) < 1e-9);
        var charging = BatteryModel.Build(new BatteryInputs([new BatteryCell("system", null, null, null, null, true, false)], AcOnline: true, WindowsPercent: 40)).OrDefault!;
        check("Battery: the fallback cell charges when the system flags charging", charging.State == BatteryState.Charging);

        // Levels: low at or under the threshold, elevated within ten above, never when on mains.
        check("Battery: levels follow the devices' rule, charging is never low",
            BatteryModel.Level(new BatterySample(12, BatteryState.Discharging, 1), 15) == MemoryLevel.Critical && BatteryModel.Level(new BatterySample(22, BatteryState.Discharging, 1), 15) == MemoryLevel.Elevated
            && BatteryModel.Level(new BatterySample(40, BatteryState.Discharging, 1), 15) == MemoryLevel.Normal && BatteryModel.Level(new BatterySample(5, BatteryState.Charging, 1), 15) == MemoryLevel.Normal
            && BatteryModel.Level(null, 15) == MemoryLevel.Normal);

        check("Battery: Windows' power modes by their overlay GUID",
            BatteryModel.ModeOf(Guid.Parse("961cc777-2547-4f9d-8174-7d86181b8a7a")) == PowerMode.BestEfficiency && BatteryModel.ModeOf(Guid.Empty) == PowerMode.Balanced
            && BatteryModel.ModeOf(Guid.Parse("ded574b5-45a0-4f42-8737-46345c09c238")) == PowerMode.BestPerformance
            && BatteryModel.ModeOf(Guid.Parse("3af9b8d9-7c97-431d-ad78-34a8bfea439f")) == PowerMode.BetterPerformance && BatteryModel.ModeOf(Guid.NewGuid()) == PowerMode.Unknown);
    }

    private static Reading<BatterySample> AsReading(this BatterySample sample) => sample;

    private static void Formats(Action<string, bool> check)
    {
        check("Format: watts have a decimal under 100 and none from there", PowerFormat.Watts(38.24) == "38.2" && PowerFormat.Watts(112.4) == "112" && PowerFormat.Watts(99.97) == "100" && PowerFormat.Watts(0) == "0.0"
            && PowerFormat.Watts(double.NaN) == "0.0");
        check("Format: the collapsed row's watts are three characters at most", PowerFormat.Compact(8.43) == "8.4" && PowerFormat.Compact(38.4) == "38" && PowerFormat.Compact(112.4) == "112" && PowerFormat.Compact(5000) == "999");
        check("Format: durations in the two largest units", PowerFormat.Duration(TimeSpan.FromMinutes(192)) == "3 小时 12 分" && PowerFormat.Duration(TimeSpan.FromMinutes(12)) == "12 分钟"
            && PowerFormat.Duration(TimeSpan.FromHours(2)) == "2 小时" && PowerFormat.Duration(TimeSpan.FromSeconds(20)) == "不到 1 分钟");
    }

    // The collapsed row's Power and Charge readings join once they give a figure, stay away on a machine that has none, and keep the
    // sampler running until they are known to be absent.
    private static void Admission(Action<string, bool> check)
    {
        var all = CompactMonitor.Normalize(null);
        foreach (var reading in all) reading.Visible = reading.Item is CompactMonitorItems.Power or CompactMonitorItems.Charge;
        var admission = new StripAdmission();
        const CompactMonitorItems Both = CompactMonitorItems.Power | CompactMonitorItems.Charge;
        check("Strip: Power and Charge wait for their first figure", (admission.Hidden & Both) == Both);
        check("Strip: a row that chose them keeps the sampler running while their sensors are unknown", admission.Awaits(all, null, SensorStates.Unknown));
        check("Strip: ...and stops asking once both are known to be absent", !admission.Awaits(all, null, SensorStates.Unknown with { Power = SensorState.None, Charge = SensorState.None }));
        check("Strip: ...but keeps asking for the one that may still come", admission.Awaits(all, null, SensorStates.Unknown with { Power = SensorState.None }));

        var drawn = new PowerSample(30, PowerOrigin.Estimated, PowerSource.Components, []);
        var snapshot = MonitorSnapshot.Empty(4, "t") with { Power = drawn };
        var (hidden, arriving) = admission.Update(snapshot, all, SensorStates.Unknown);
        check("Strip: the power reading joins with its first figure, the charge reading (no battery figure) does not", (hidden & Both) == CompactMonitorItems.Charge && arriving == CompactMonitorItems.Power);
        check("Strip: a joined reading is part of the layout", admission.Layout.Contains(((int)CompactMonitorItems.Power).ToString()));

        var withBattery = snapshot with { Battery = new BatterySample(80, BatteryState.Discharging, 1) };
        var (hidden2, arriving2) = admission.Update(withBattery, all, SensorStates.Unknown);
        check("Strip: the charge reading joins when the battery gives a figure", (hidden2 & Both) == CompactMonitorItems.None && arriving2 == CompactMonitorItems.Charge);

        var (hidden3, _) = admission.Update(null, all, SensorStates.Unknown with { Power = SensorState.None, Charge = SensorState.None });
        check("Strip: both leave once the sampler finds the machine has neither", (hidden3 & Both) == Both);

        var desktop = new StripAdmission();
        var (desktopHidden, _) = desktop.Update(MonitorSnapshot.Empty(4, "t"), all, SensorStates.Unknown with { Charge = SensorState.None });
        check("Strip: a desktop never shows the charge reading", (desktopHidden & CompactMonitorItems.Charge) != 0);
    }

    private static void Readings(Action<string, bool> check)
    {
        var table = CompactMonitor.Normalize(null);
        check("Readings: Power and Charge are last in the table but for the frame rate, hidden",
            table.Select(reading => reading.Item).TakeLast(3).SequenceEqual([CompactMonitorItems.Power, CompactMonitorItems.Charge, CompactMonitorItems.Frames]) && table.Where(reading => reading.Item is CompactMonitorItems.Power or CompactMonitorItems.Charge).All(reading => !reading.Visible));
        check("Readings: they leave a short row early, after the frame rate, the machine's battery after the drives", CompactMonitor.LeaveOrder.Take(4).SequenceEqual([CompactMonitorItems.Frames, CompactMonitorItems.Power, CompactMonitorItems.Storage, CompactMonitorItems.Charge]) && CompactMonitor.LeaveOrder.Count == CompactMonitor.Order.Count);

        // Full turns the new readings on, Brief turns them off.
        var full = CompactMonitor.Normalize(null);
        CompactMonitor.Apply(full, CompactMonitorMode.Full);
        check("Readings: Full shows Power and Charge", full.Where(reading => reading.Item is CompactMonitorItems.Power or CompactMonitorItems.Charge).All(reading => reading.Visible));
        CompactMonitor.Apply(full, CompactMonitorMode.Brief);
        check("Readings: Brief hides Power and Charge", full.Where(reading => reading.Item is CompactMonitorItems.Power or CompactMonitorItems.Charge).All(reading => !reading.Visible));
        full.First(reading => reading.Item == CompactMonitorItems.Charge).Visible = true;
        check("Readings: a table with Charge on is no preset", CompactMonitor.Preset(full) is null);

        // A saved table from before them still loads, and one with them round-trips.
        var saved = CompactMonitor.Normalize(table.Where(reading => reading.Item != CompactMonitorItems.Power && reading.Item != CompactMonitorItems.Charge));
        check("Readings: a table saved before them gets them hidden", saved.Count == table.Count && saved.Where(reading => reading.Item is CompactMonitorItems.Power or CompactMonitorItems.Charge).All(reading => !reading.Visible));
        var settings = new DeckSettings();
        settings.CompactReadings.First(reading => reading.Item == CompactMonitorItems.Power).Visible = true;
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck-Tests-power-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        DeckSettings copy;
        try { var store = new SettingsStore(folder); store.Save(settings); copy = new SettingsStore(folder).Load(); }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        check("Readings: Power survives the settings file", copy.CompactReadings.First(reading => reading.Item == CompactMonitorItems.Power).Visible && (CompactMonitor.Shown(copy.CompactReadings) & CompactMonitorItems.Power) != 0 && copy.MonitorPower);

        // Their widths come in through the same table as the others.
        var width = CompactMonitor.Widths(CompactMonitorItems.None, 16, _ => 0, 16, 40, 30, power: 41, charge: 52);
        check("Readings: the widths of Power and Charge", width(CompactMonitorItems.Power) == 41 && width(CompactMonitorItems.Charge) == 52 && CompactMonitor.Width(CompactMonitorItems.Power | CompactMonitorItems.Charge, width, 6) == 41 + 6 + 52);
        const CompactMonitorItems Three = CompactMonitorItems.Cpu | CompactMonitorItems.Power | CompactMonitorItems.Charge;
        check("Readings: a short row drops Power first", CompactMonitor.Fit(Three, width, 6, 16 + 6 + 52) == (CompactMonitorItems.Cpu | CompactMonitorItems.Charge));
        check("Readings: a shorter row then drops Charge", CompactMonitor.Fit(Three, width, 6, 16 + 6 + 52 - 1) == CompactMonitorItems.Cpu);
        check("Readings: no level of their own in the strip's rule", ReadingLevels.Of(CompactMonitorItems.Power, MonitorSnapshot.Empty(4, "t")) == MemoryLevel.Normal);
    }

    private static void Layout(Action<string, bool> check)
    {
        // A page without power is laid out as before.
        var before = MonitorLayout.For(700, 290, true, 2);
        check("Layout: without power nothing changes", before == MonitorLayout.For(700, 290, true, 2, 0) && before.PowerLines == 0 && before.Parts(290)[4].Height == 0);
        // Room is what the drives leave while the core columns keep their minimum.
        var tall = MonitorLayout.For(700, 400, true, 2, power: 2);
        check("Layout: a tall page shows power and the battery on two lines", tall.PowerLines == 2 && tall.StorageLines == 2);
        var narrow = MonitorLayout.For(620, 260, true, 2, power: 1);
        check("Layout: a standard page with drives keeps the power line", narrow.PowerLines == 1 && narrow.StorageLines == 2);
        check("Layout: a short page has none", MonitorLayout.For(620, 100, true, 2, power: 2).PowerLines == 0);
        check("Layout: the first line of power comes before the drives, which give up room for it (at most two lines) while the graph keeps its minimum", Enumerable.Range(140, 260).All(height =>
        {
            var plain = MonitorLayout.For(700, height, true, 3);
            var with = MonitorLayout.For(700, height, true, 3, power: 2);
            return with.StorageLines >= plain.StorageLines - 2 && with.StorageLines <= plain.StorageLines && with.Parts(height)[1].Height >= MonitorLayout.MinimumGraph - 1e-9 && with.Metrics == plain.Metrics && with.PowerLines is >= 0 and <= 2
                && (with.PowerLines == 0) == (MonitorLayout.PowerRoom(height, with.Metrics, with.Figure, with.Body, 0) == 0);
        }));
        var parts = tall.Parts(400);
        check("Layout: the power lines sit under the drives and the graph keeps its minimum", parts[4].Top == parts[3].Top + parts[3].Height
            && parts[4].Height == (tall.Metrics switch { MetricsMode.Line => 2, MetricsMode.Lines => 4, _ => 8 }) + 2 * tall.StorageRow && parts[1].Height >= MonitorLayout.MinimumGraph - 1e-9);
    }

    // Every built-in theme has its own look, every color a look names resolves in every theme, and the colors that carry meaning hold
    // the contrast the themes hold (graphics 3:1 on the surface at every stop of its gradient, as the tests of the other marks).
    private static void Looks(Action<string, bool> check)
    {
        var palettes = ThemeCoverageTests.Palettes().ToList();
        check("Look: the shapes differ between the themes that have their own", new[] { "cyberpunk-edgerunners", PanelThemes.IslandHoliday, PanelThemes.PixelBlocks, PanelThemes.Dark }
            .Select(id => PowerLooks.For(PanelThemes.Resolve(PanelThemes.BuiltIn(id)!)).Gauge).Distinct().Count() == 4);
        check("Look: high contrast draws with text colors only", PowerLooks.HighContrast.References.All(name => name == "textPrimary") && PowerLooks.For(palettes[0].Palette, highContrast: true) == PowerLooks.HighContrast);

        var failures = new List<string>();
        foreach (var (id, palette) in palettes)
        {
            var look = PowerLooks.For(palette, id.StartsWith("high-contrast", StringComparison.Ordinal));
            foreach (var name in look.References)
            {
                bool token = PowerLook.Tokens.Contains(name);
                if (!token && !PowerLook.Roles.Contains(name)) { failures.Add($"{id}: unknown color {name}"); continue; }
                // A role comes from the monitor scheme, drawn readable on the theme by the app; only the tokens are the theme's own.
                if (!token) continue;
                if (!palette.Colors.ContainsKey(name)) { failures.Add($"{id}: no token {name}"); continue; }
                double ratio = ThemeCoverageTests.Contrast(palette, name, ["surface"]);
                if (ratio < 3 - .005) failures.Add($"{id}: {name} {ratio:0.00} < 3");
            }
        }
        // Orange and red belong to the low levels: a battery at a normal charge, charging or saving is never drawn in the theme's warning or
        // critical color, so "83 %" is never mistaken for a warning. A token is the theme's own; a role comes from the monitor's Theme color scheme
        // (the default), worked out as the app does (MonitorColors.FromTheme over the theme's accent and its cyan, pink, lime and amber), with
        // the theme's warning and critical beside it. Every theme, its night and dusk looks included.
        var warm = new List<string>();
        foreach (var (id, palette) in palettes.Where(item => !item.Id.StartsWith("high-contrast", StringComparison.Ordinal)))
        {
            var look = PowerLooks.For(palette);
            string warning = (palette.WarningDefault ?? ThemeColor.Parse(AppPresets.WarningColor)).ToString(), critical = (palette.CriticalDefault ?? ThemeColor.Parse(AppPresets.CriticalColor)).ToString();
            var scheme = MonitorColors.FromTheme(palette["accent"].ToString(), [.. new[] { ClockColor.Cyan, ClockColor.Pink, ClockColor.Lime, ClockColor.Amber }.Select(color => palette.ClockPreset(color).ToString())], warning, critical);
            foreach (var name in new[] { look.Normal, look.Charge, look.Saver, look.Draw }.Distinct())
            {
                string color = PowerLook.Tokens.Contains(name) ? palette[name].ToString() : name switch
                { "cpu" => scheme.Performance, "efficiency" => scheme.Efficiency, "memory" => scheme.Memory, "gpu" => scheme.Gpu, _ => "" };
                if (color.Length == 0 || name is "warning" or "critical") continue;
                if (ColorMath.Distance(color, warning) < 90 || ColorMath.Distance(color, critical) < 90) warm.Add($"{id}: {name} {color} is near {warning} or {critical}");
            }
        }
        check("Look: no state of a normal battery takes the warning or critical color" + (warm.Count > 0 ? ": " + string.Join("; ", warm) : ""), warm.Count == 0);
        check("Look: a normal battery's fill is the text color or a cool one in every look", PowerLooks.Default.Normal == "textPrimary" && PowerLooks.Ids.All(id => PowerLooks.For(PanelThemes.Resolve(PanelThemes.BuiltIn(id)!)).Normal is "textPrimary" or "accent"));
        check("Look: every color a look takes from a theme reads on that theme" + (failures.Count > 0 ? ": " + string.Join("; ", failures) : ""), failures.Count == 0);
    }

    // The tooltips: a title line and short "item  value" lines, in plain words, in both languages; no sentences, no implementation names.
    private static void Texts(Action<string, bool> check)
    {
        PowerPart[] parts = [new("cpu", "CPU", 9.1, "RAPL"), new("gpu", "GPU", 7.9, PowerResolver.GpuSource)];
        var estimate = new PowerSample(17.0, PowerOrigin.Estimated, PowerSource.Components, parts);
        var summary = new PowerSummary(78.5, 63.4, 40, 312.4);
        var cpuTrack = new PowerTrack([9.1], new PowerSummary(52, 38, 40, 312)); var gpuTrack = new PowerTrack([7.9], new PowerSummary(23.1, 8.2, 40, 312));
        var table = PowerText.Table(Reading<PowerSample>.Of(estimate), summary, cpuTrack, gpuTrack)!;
        check("Text: its rows are now, the peak and the mean over the time they cover", table.Rows.Select(row => row.Label).SequenceEqual(["当前", "5 分钟内峰值", "5 分钟内均值"])
            && table.Rows[0].Cells.SequenceEqual(["17.0 W", "9.1 W", "7.9 W"]) && table.Rows[1].Cells.SequenceEqual(["78.5 W", "52.0 W", "23.1 W"]) && table.Rows[2].Cells.SequenceEqual(["63.4 W", "38.0 W", "8.2 W"]));
        check("Text: an estimate made of these parts is their sum, and says what it adds and leaves out", table.Sum && table.Notes.SequenceEqual(["来源  CPU + GPU 估算，不含屏幕、硬盘、风扇等"]));
        check("Text: a short time is in seconds", PowerText.Table(Reading<PowerSample>.Of(estimate), summary with { Seconds = 45.2 }, cpuTrack, gpuTrack)!.Rows[1].Label == "45 秒内峰值");
        check("Text: from a minute on the time is in whole minutes", PowerFormat.Span(59.4) == "59 秒" && PowerFormat.Span(64) == "1 分钟" && PowerFormat.Span(89) == "1 分钟"
            && PowerFormat.Span(90) == "2 分钟" && PowerFormat.Span(124) == "2 分钟" && PowerFormat.Span(150) == "3 分钟");
        var measured = new PowerSample(23.4, PowerOrigin.Measured, PowerSource.Battery, [parts[0]]);
        var measuredTable = PowerText.Table(Reading<PowerSample>.Of(measured), null, cpuTrack, PowerTrack.None)!;
        check("Text: a measurement is no sum, says where it is read, and has no GPU column without a GPU", !measuredTable.Sum && measuredTable.Notes.SequenceEqual(["来源  电池放电（实测）"])
            && measuredTable.Columns.SequenceEqual(["整机", "CPU"]) && measuredTable.Rows.Count == 1 && measuredTable.Rows[0].Cells.SequenceEqual(["23.4 W", "9.1 W"]));
        var idle = PowerText.Table(Reading<PowerSample>.Of(new PowerSample(9.1, PowerOrigin.Estimated, PowerSource.Components, [parts[0]]) { Missing = ["GPU（空闲）"] }), null, cpuTrack, PowerTrack.None)!;
        check("Text: a GPU not read has its column with a dash, and says why", idle.Columns.SequenceEqual(["整机", "CPU", "GPU"]) && idle.Rows[0].Cells[2] == "—" && idle.Notes.SequenceEqual(["未读取  GPU（空闲）", "来源  CPU 估算，不含屏幕、硬盘、风扇等"]));
        var meter = PowerText.Table(Reading<PowerSample>.Of(new PowerSample(40, PowerOrigin.Measured, PowerSource.SystemMeter, parts)), null, cpuTrack, gpuTrack)!;
        check("Text: a system meter says so", !meter.Sum && meter.Notes.SequenceEqual(["来源  整机功率计（实测）"]));
        check("Text: the power line's mean, up to four digits", PowerText.Mean(65.9) == "均值 66 W" && PowerText.Mean(8.43) == "均值 8.4 W" && PowerText.Mean(1234.4) == "均值 1234 W"
            && PowerFormat.Compact(1234.4) == "999");
        check("Text: no reading is a dash and the reason", PowerText.Table(Reading<PowerSample>.Unavailable("无功率来源"), null, PowerTrack.None, PowerTrack.None) is null
            && PowerText.NoPower("无功率来源") == "整机功耗  —\n原因  无功率来源");

        var cell = BatteryModel.Build(new BatteryInputs([Cell(57_000, 59_400, 60_000, 0)], AcOnline: true, Mode: PowerMode.BestPerformance)).OrDefault!;
        var lines = PowerText.Battery(Reading<BatterySample>.Of(cell)).Split('\n');
        check("Text: the battery tooltip: charge, state, mode, health", lines[0] == "电池  96%（储存电能 57.0 Wh）" && lines[1] == "状态  已接通电源，未在充电" && lines[2] == "电源模式  最佳性能" && lines[3].StartsWith("健康度  ") && lines[3].Contains("满充") && lines.Length == 4);
        var discharging = BatteryModel.Build(new BatteryInputs([Cell(40_000, 60_000, 66_000, -23_400, discharging: true)], AcOnline: false, BatterySaver: true, WindowsRemaining: TimeSpan.FromMinutes(165))).OrDefault!;
        var more = PowerText.Battery(Reading<BatterySample>.Of(discharging));
        check("Text: discharging has its rate, the time left and the saver", more.Contains("状态  放电 23.4 W") && more.Contains("剩余  2 小时 45 分") && more.Contains("节电模式  开启"));
        var charging = BatteryModel.Build(new BatteryInputs([Cell(40_000, 60_000, 66_000, 31_000, charging: true)], AcOnline: true)).OrDefault!;
        check("Text: charging has its rate and the time to full", PowerText.Battery(Reading<BatterySample>.Of(charging)) is { } c && c.Contains("状态  充电 31.0 W") && c.Contains("充满需  "));

        // After the battery's charge on the page: the time where there is one, nothing otherwise (the charge and the drawing say the rest).
        var full = BatteryModel.Build(new BatteryInputs([Cell(59_000, 60_000, 66_000, 0)], AcOnline: true)).OrDefault!;
        var idleOnBattery = BatteryModel.Build(new BatteryInputs([Cell(30_000, 60_000, 66_000, -300)], AcOnline: false)).OrDefault!;
        check("Text: the battery's time on the page", PowerText.Brief(discharging) == "剩余 2:45" && PowerText.Brief(charging).StartsWith("充满 "));
        check("Text: no words where the charge and the drawing say it", PowerText.Brief(full) == "" && PowerText.Brief(cell) == "" && PowerText.Brief(idleOnBattery) == ""
            && PowerText.Brief(charging with { TimeToFull = null }) == "");

        Loc.Use(Loc.English);
        var estimateEn = new PowerSample(17.0, PowerOrigin.Estimated, PowerSource.Components, parts);
        var english = PowerText.Table(Reading<PowerSample>.Of(estimateEn), summary with { Seconds = 45 }, cpuTrack, gpuTrack)!;
        check("Text: the English table is the same form", english.Columns.SequenceEqual(["System", "CPU", "GPU"]) && english.Rows.Select(row => row.Label).SequenceEqual(["Now", "Peak, 45 s", "Mean, 45 s"])
            && english.Notes.SequenceEqual(["Source  CPU + GPU estimate; display, drives, fans and others not included"]));
        check("Text: the English reading's name", PowerText.Spoken(Reading<PowerSample>.Of(estimateEn), Reading<BatterySample>.Of(cell), true) == "System power 17.0 W, estimated; battery 96%, Plugged in, not charging; power mode Best performance");
        Loc.Use(Loc.Chinese);
        var all = new[] { string.Join("\n", table.Columns.Concat(table.Rows.SelectMany(row => row.Cells.Prepend(row.Label))).Concat(table.Notes)), PowerText.Battery(Reading<BatterySample>.Of(discharging)), PowerText.Spoken(Reading<PowerSample>.Of(estimate), Reading<BatterySample>.Of(cell), true) };
        check("Text: no tool name and no full stop in any of them", all.All(line => !line.Contains("nvidia-smi", StringComparison.OrdinalIgnoreCase) && !line.Contains("NVML") && !line.Contains('。')));
    }
}

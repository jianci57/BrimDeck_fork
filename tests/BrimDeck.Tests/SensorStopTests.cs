using BrimDeck.Core;

// The readings stopped because their driver did not answer (SensorStops): which device and which way of asking a stop holds for,
// a mark left behind becoming a stop, the files read back, and the line the settings show.
static class SensorStopTests
{
    public static void Run(Action<string, bool> check)
    {
        var at = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.FromHours(8));
        var drive = new GuardedDevice(GuardedReading.DriveTemperature, "Samsung SSD 980 | 17", "Samsung SSD 980");
        var gpu = new GuardedDevice(GuardedReading.GpuTemperature, "NVIDIA GeForce RTX 4060", "NVIDIA GeForce RTX 4060");
        int method = SensorStops.Method(GuardedReading.DriveTemperature);
        var stops = SensorStops.With([], drive, method, at);

        check("Sensor stops: a stop holds for its reading and its device only", SensorStops.Applies(stops[0], GuardedReading.DriveTemperature, drive.Device)
            && !SensorStops.Applies(stops[0], GuardedReading.DriveTemperature, "WD Blue | 11") && !SensorStops.Applies(stops[0], GuardedReading.GpuTemperature, drive.Device));
        var older = stops[0] with { Method = method - 1 };
        check("Sensor stops: a stop recorded for an older way of asking no longer holds, and is dropped",
            !SensorStops.Applies(older, GuardedReading.DriveTemperature, drive.Device) && SensorStops.Current([older, .. stops]).Count == 1);
        stops = SensorStops.With(stops, drive, method, at.AddHours(1));
        check("Sensor stops: a device stopped again keeps one stop, the latest", stops.Count == 1 && stops[0].At == at.AddHours(1));
        stops = SensorStops.With(stops, gpu, SensorStops.Method(gpu.Reading), at);
        check("Sensor stops: written and read back the same", SensorStops.Parse(SensorStops.Serialize(stops)).SequenceEqual(stops));
        check("Sensor stops: a damaged file stops nothing", SensorStops.Parse("{ not json").Count == 0 && SensorStops.Parse("null").Count == 0 && SensorStops.Parse("[{}]").Count == 0);

        var left = SensorStops.ParsePending(SensorStops.SerializePending([drive, gpu]), at);
        check("Sensor stops: a mark left behind becomes a stop, for the way it was asked", left.Count == 2
            && left[0] == new SensorStop(drive.Reading, drive.Device, drive.Name, method, at) && left[1].Reading == GuardedReading.GpuTemperature);
        check("Sensor stops: an empty mark stops nothing", SensorStops.ParsePending(SensorStops.SerializePending([]), at).Count == 0 && SensorStops.ParsePending("", at).Count == 0);
        check("Sensor stops: only the drives' temperatures are marked every time", SensorStops.AlwaysMarked(GuardedReading.DriveTemperature)
            && !SensorStops.AlwaysMarked(GuardedReading.GpuTemperature) && !SensorStops.AlwaysMarked(GuardedReading.CpuTemperature) && !SensorStops.AlwaysMarked(GuardedReading.PowerMeter));

        var language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try
        {
            check("Sensor stops: the notice names the drive", SensorStops.Notice(stops[0]) == "已停止读取“Samsung SSD 980”的温度，上次读取时电脑可能卡住了。");
            check("Sensor stops: the processor and the meter are named without a device",
                SensorStops.Notice(new SensorStop(GuardedReading.CpuTemperature, "board", "CPU", 1, at)) == "已停止读取 CPU 温度，上次读取时电脑可能卡住了。"
                && SensorStops.Notice(new SensorStop(GuardedReading.PowerMeter, "board", "功率计", 1, at)) == "已停止读取功率计与 CPU 功耗，上次读取时电脑可能卡住了。");
        }
        finally { Loc.Use(language); }
    }
}

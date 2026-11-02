using BrimDeck.Core;

// The processor's own sensors as the sensor service decodes them, the reply it sends BrimDeck, and the hardware monitors read
// when enhanced sensors are not set up.
internal static class CpuSensorTests
{
    public static void Run(Action<string, bool> check)
    {
        Report(check);
        Intel(check);
        Amd(check);
        Energy(check);
        Bridges(check);
    }

    private static void Report(Action<string, bool> check)
    {
        var report = new SensorReport(SensorStatus.Ok)
        {
            Processor = "AMD Ryzen 7 7800X3D 8-Core Processor", Package = 61.25, Cores = [58.5, 60.125], CoresAreCcds = true, PackageWatts = 44.8, MemoryWatts = 3.2
        };
        var read = SensorReport.Parse(report.Format());
        check("Sensors: a reply reads back as it was sent", read is { Status: SensorStatus.Ok, Version: SensorReport.CurrentVersion, Package: 61.25, PackageWatts: 44.8, MemoryWatts: 3.2,
            CoresAreCcds: true, CoresWatts: null } && read.Cores.SequenceEqual([58.5, 60.125]) && read.Processor == report.Processor);
        check("Sensors: a reply without readings says why", SensorReport.Parse(new SensorReport(SensorStatus.NoDriver).Format()) is { Status: SensorStatus.NoDriver, Package: null, Cores.Count: 0 });
        check("Sensors: unknown keys are skipped, a broken reply is none", SensorReport.Parse("version=2\nstatus=Ok\nfuture=1\npackage=50\n") is { Version: 2, Package: 50 }
            && SensorReport.Parse("") is null && SensorReport.Parse("status=Ok\n") is null && SensorReport.Parse("version=1\nstatus=Bogus\n") is null);
    }

    private static void Intel(Action<string, bool> check)
    {
        // TjMax 100 in bits 23:16 of IA32_TEMPERATURE_TARGET; 38 below it, valid.
        check("Sensors: Intel's TjMax, an absurd one is none", CpuSensorMath.IntelTjMax(100UL << 16) == 100 && CpuSensorMath.IntelTjMax(0) is null);
        check("Sensors: Intel's temperature is TjMax less the readout", CpuSensorMath.IntelTemperature(0x80000000UL | 38UL << 16, 100) == 62
            && CpuSensorMath.IntelTemperature(38UL << 16, 100) is null);
        check("Sensors: the energy unit, 1/2^n joules", CpuSensorMath.EnergyUnit(0xA0E03) == 1.0 / (1 << 14));
        check("Sensors: family and model from CPUID", CpuSensorMath.FamilyModel(0x906A3) == (6, 0x9A) && CpuSensorMath.FamilyModel(0xA60F12) == (0x19, 0x61)
            && CpuSensorMath.FamilyModel(0xB40F40) == (0x1A, 0x44));
    }

    private static void Amd(Action<string, bool> check)
    {
        // 61.25 °C is 490 eighths in bits 31:21; with range select (bit 19) it reads 49 lower.
        check("Sensors: Zen's Tctl", CpuSensorMath.ZenTctl(490u << 21) == 61.25 && CpuSensorMath.ZenTctl(490u << 21 | 0x80000) == 12.25
            && CpuSensorMath.ZenTctl(490u << 21 | 0x30000) == 12.25);
        check("Sensors: Tctl offsets of the early parts only", CpuSensorMath.TctlOffset(0x17, "AMD Ryzen 7 1800X Eight-Core Processor") == 20
            && CpuSensorMath.TctlOffset(0x17, "AMD Ryzen 7 2700X Eight-Core Processor") == 10 && CpuSensorMath.TctlOffset(0x19, "AMD Ryzen 7 1800X") == 0
            && CpuSensorMath.TctlOffset(0x17, "AMD Ryzen 5 3600 6-Core Processor") == 0);
        check("Sensors: where each part reports its CCDs", CpuSensorMath.ZenCcds(0x17, 0x71) == (0x154, 8) && CpuSensorMath.ZenCcds(0x19, 0x61) == (0x308, 8)
            && CpuSensorMath.ZenCcds(0x19, 0x11) == (0x300, 12) && CpuSensorMath.ZenCcds(0x1A, 0x44) == (0x308, 8) && CpuSensorMath.ZenCcds(0x1A, 0x02) == (0x1F0, 16)
            && CpuSensorMath.ZenCcds(0x19, 0x75) == (0x308, 8) && CpuSensorMath.ZenCcds(0x17, 0x90) is null && CpuSensorMath.ZenCcds(0x15, 0x60) is null);
        // A CCD at 58.5 °C: (58.5 + 49) * 8 = 860 eighths, valid bit 11.
        check("Sensors: a CCD's temperature, an invalid or unreadable one is none", CpuSensorMath.ZenCcd(860 | 0x800) == 58.5 && CpuSensorMath.ZenCcd(860) is null
            && CpuSensorMath.ZenCcd(uint.MaxValue) is null);
    }

    private static void Energy(Action<string, bool> check)
    {
        double unit = 1.0 / (1 << 14);
        check("Sensors: joules across a wrap of the 32-bit counter", CpuSensorMath.Joules(uint.MaxValue - 16383, 16384, unit) == 2);
        var meter = new EnergyMeter();
        check("Sensors: no watts from the first reading", meter.Watts(1000, unit, 10) is null);
        check("Sensors: watts over the time between readings", meter.Watts(1000 + 16384 * 30, unit, 12) == 15);
        check("Sensors: a clock that did not move gives none", meter.Watts(1000 + 16384 * 40, unit, 12) is null);
    }

    private static void Bridges(Action<string, bool> check)
    {
        var hwInfo = MonitorBridges.Cpu([
            new BridgeSensor("CPU [#0]: Intel Core i7-12700H: DTS", "Core Max", BridgeReading.Temperature, 71),
            new BridgeSensor("CPU [#0]: Intel Core i7-12700H: DTS", "CPU Package", BridgeReading.Temperature, 68),
            new BridgeSensor("CPU [#0]: Intel Core i7-12700H: Enhanced", "CPU Package Power", BridgeReading.Power, 42.5),
            new BridgeSensor("GPU [#0]: NVIDIA GeForce RTX 3070 Ti Laptop GPU", "GPU Temperature", BridgeReading.Temperature, 55)]);
        check("Sensors: HWiNFO's package temperature and power", hwInfo == (68, 42.5));
        var libre = MonitorBridges.Cpu([
            new BridgeSensor("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", BridgeReading.Temperature, 63.5),
            new BridgeSensor("/amdcpu/0/power/0", "Package", BridgeReading.Power, 88),
            new BridgeSensor("/gpu-nvidia/0/temperature/0", "GPU Core", BridgeReading.Temperature, 50)]);
        check("Sensors: LibreHardwareMonitor's, by its names", libre == (63.5, 88));
        check("Sensors: nothing from a monitor without the processor's readings", MonitorBridges.Cpu([new BridgeSensor("/gpu-nvidia/0/temperature/0", "GPU Core", BridgeReading.Temperature, 50),
            new BridgeSensor("CPU [#0]: Intel: DTS", "CPU Package", BridgeReading.Temperature, 0)]) == (null, null));
    }
}

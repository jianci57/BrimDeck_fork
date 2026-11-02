using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using BrimDeck.Core;

namespace BrimDeck.Sensors;

// The processor's temperatures and energy counters through PawnIO's official modules: IntelMSR for Intel, AMDFamily17 for AMD's
// Zen families (17h to 1Ah). Only registers are read, never written. A core's own registers are read on that core: the thread is
// moved onto one of its logical processors first. The modules lie beside this program (modules\*.bin), put there by BrimDeck's
// setup from PawnIO's signed release.
internal sealed class CpuReader : IDisposable
{
    // Intel's MSRs (SDM volume 4) and AMD's (processor programming reference, family 17h and later).
    private const uint IntelTarget = 0x1A2, IntelCoreStatus = 0x19C, IntelPackageStatus = 0x1B1, IntelUnits = 0x606,
        IntelPackageEnergy = 0x611, IntelDramEnergy = 0x619, IntelCoresEnergy = 0x639, IntelGraphicsEnergy = 0x641, IntelPlatformEnergy = 0x64D;
    private const uint AmdUnits = 0xC0010299, AmdCoreEnergy = 0xC001029A, AmdPackageEnergy = 0xC001029B;
    private readonly PawnIo? _io;
    private readonly bool _intel;
    private readonly int _family, _model;
    private readonly string _name;
    private readonly List<GroupAffinity> _cores;
    private readonly double _tjMax, _unit, _tctlOffset;
    private readonly (uint Offset, int Count)? _ccds;
    private readonly EnergyMeter _package = new(), _graphics = new(), _dram = new(), _platform = new(), _coresTotal = new();
    private readonly List<EnergyMeter> _coreMeters = [];
    // The Zen parts share the index and data registers of the SMN with other monitors; this named mutex is the convention.
    private readonly Mutex? _pci;
    public SensorStatus Status { get; }

    public CpuReader(string folder)
    {
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        string vendor = Ascii(ebx) + Ascii(edx) + Ascii(ecx);
        (_family, _model) = CpuSensorMath.FamilyModel((uint)X86Base.CpuId(1, 0).Eax);
        _name = Brand();
        _cores = Cores();
        _intel = vendor == "GenuineIntel";
        bool amd = vendor == "AuthenticAMD";
        if (!_intel && !amd) { Status = SensorStatus.Unsupported; return; }
        string file = Path.Combine(folder, "modules", _intel ? "IntelMSR.bin" : "AMDFamily17.bin");
        if (!File.Exists(file)) { Status = SensorStatus.NoModule; return; }
        _io = PawnIo.Load(File.ReadAllBytes(file), out bool driver);
        if (_io is null) { Status = driver ? SensorStatus.Unsupported : SensorStatus.NoDriver; return; }
        if (_intel)
        {
            _tjMax = _io.ReadMsr(IntelTarget) is { } target && CpuSensorMath.IntelTjMax(target) is { } tj ? tj : 100;
            _unit = _io.ReadMsr(IntelUnits) is { } units ? CpuSensorMath.EnergyUnit(units) : 0;
        }
        else
        {
            _pci = new Mutex(false, @"Global\Access_PCI");
            _unit = _io.ReadMsr(AmdUnits) is { } units ? CpuSensorMath.EnergyUnit(units) : 0;
            _tctlOffset = CpuSensorMath.TctlOffset(_family, _name);
            _ccds = CpuSensorMath.ZenCcds(_family, _model);
        }
        Status = SensorStatus.Ok;
    }

    public SensorReport Read(double now)
    {
        if (_io is null || Status != SensorStatus.Ok) return new SensorReport(Status) { Processor = _name };
        return _intel ? ReadIntel(now) : ReadAmd(now);
    }

    private SensorReport ReadIntel(double now)
    {
        double? package = _io!.ReadMsr(IntelPackageStatus) is { } status ? CpuSensorMath.IntelTemperature(status, _tjMax) : null;
        var cores = new List<double>(_cores.Count);
        foreach (var core in _cores)
            if (OnCore(core, () => _io.ReadMsr(IntelCoreStatus)) is { } coreStatus && CpuSensorMath.IntelTemperature(coreStatus, _tjMax) is { } celsius) cores.Add(celsius);
        // Older parts without the package register: the hottest core stands for it.
        package ??= cores.Count > 0 ? cores.Max() : null;
        return new SensorReport(SensorStatus.Ok)
        {
            Processor = _name, Package = package, Cores = cores,
            PackageWatts = Watts(_package, IntelPackageEnergy, now), CoresWatts = Watts(_coresTotal, IntelCoresEnergy, now),
            GraphicsWatts = Watts(_graphics, IntelGraphicsEnergy, now), MemoryWatts = Watts(_dram, IntelDramEnergy, now),
            PlatformWatts = Watts(_platform, IntelPlatformEnergy, now)
        };
    }

    private SensorReport ReadAmd(double now)
    {
        double? package = null;
        var ccds = new List<double>();
        if (Locked(() => _io!.ReadSmn(CpuSensorMath.ZenTemperatureAddress)) is { } register) package = CpuSensorMath.ZenTctl(register) - _tctlOffset;
        if (_ccds is { } layout)
            for (int i = 0; i < layout.Count; i++)
                if (Locked(() => _io!.ReadSmn(CpuSensorMath.ZenTemperatureAddress + layout.Offset + (uint)i * 4)) is { } ccd && CpuSensorMath.ZenCcd(ccd) is { } celsius) ccds.Add(celsius);
        // The cores' power: each core's own counter, read on that core, summed.
        double? coresWatts = null;
        while (_coreMeters.Count < _cores.Count) _coreMeters.Add(new EnergyMeter());
        for (int i = 0; i < _cores.Count; i++)
            if (OnCore(_cores[i], () => _io!.ReadMsr(AmdCoreEnergy)) is { } energy && _unit > 0 && _coreMeters[i].Watts((uint)energy, _unit, now) is { } watts)
                coresWatts = (coresWatts ?? 0) + watts;
        return new SensorReport(SensorStatus.Ok)
        {
            Processor = _name, Package = package, Cores = ccds, CoresAreCcds = true,
            PackageWatts = Watts(_package, AmdPackageEnergy, now), CoresWatts = coresWatts
        };
    }

    // A counter that reads zero is one this processor does not keep.
    private double? Watts(EnergyMeter meter, uint msr, double now) =>
        _unit > 0 && _io!.ReadMsr(msr) is { } energy && (uint)energy != 0 ? meter.Watts((uint)energy, _unit, now) : null;

    private T? Locked<T>(Func<T?> read) where T : struct
    {
        bool owned = false;
        try { owned = _pci?.WaitOne(100) ?? true; }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return null;
        try { return read(); }
        finally { if (_pci is not null) _pci.ReleaseMutex(); }
    }

    // Runs read on one of the core's logical processors, then puts the thread back where it was.
    private static T? OnCore<T>(GroupAffinity core, Func<T?> read) where T : struct
    {
        if (!SetThreadGroupAffinity(GetCurrentThread(), ref core, out var previous)) return null;
        try { return read(); }
        finally { SetThreadGroupAffinity(GetCurrentThread(), ref previous, out _); }
    }

    // One logical processor of each physical core (GetLogicalProcessorInformationEx, RelationProcessorCore): the lowest of its mask.
    private static List<GroupAffinity> Cores()
    {
        var cores = new List<GroupAffinity>();
        uint length = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
        if (length == 0) return cores;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref length)) return cores;
            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship, Size, then PROCESSOR_RELATIONSHIP (GroupCount at 30, GroupMask at 32).
            for (int at = 0, size; at + 48 <= length; at += size)
            {
                size = Marshal.ReadInt32(buffer, at + 4);
                if (size <= 0) break;
                ulong mask = (ulong)Marshal.ReadInt64(buffer, at + 32);
                ushort group = (ushort)Marshal.ReadInt16(buffer, at + 40);
                if (mask != 0) cores.Add(new GroupAffinity { Mask = (UIntPtr)(mask & (~mask + 1)), Group = group });
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return cores;
    }

    private static string Brand()
    {
        if ((uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax < 0x80000004) return "";
        var text = new System.Text.StringBuilder();
        for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
        {
            var (a, b, c, d) = X86Base.CpuId(unchecked((int)leaf), 0);
            text.Append(Ascii(a)).Append(Ascii(b)).Append(Ascii(c)).Append(Ascii(d));
        }
        return text.ToString().Trim('\0', ' ');
    }
    private static string Ascii(int register) => System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(register));

    public void Dispose() { _io?.Dispose(); _pci?.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity { public UIntPtr Mask; public ushort Group; public ushort Reserved0, Reserved1, Reserved2; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}

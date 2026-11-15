namespace BrimDeck.Core;

public static class SnapshotParts
{
    // The snapshot with only what needs no window kept (the memory, the drives, the battery, the frame rate): the readings that are
    // rates over the time since the last collection (the processor, the adapters, the network, the processes, the power) are waiting,
    // and so are the curves drawn from them. A first reading after a pause covers the whole pause and must not be shown as current.
    public static MonitorSnapshot WithoutWindowed(this MonitorSnapshot snapshot, string waiting) => snapshot with
    {
        Cpu = Reading<CpuSample>.Unavailable(waiting), Gpu = Reading<GpuSample>.Unavailable(waiting), Gpus = [],
        Network = Reading<NetworkSample>.Unavailable(waiting), Processes = Reading<ProcessList>.Unavailable(waiting),
        Power = Reading<PowerSample>.Unavailable(waiting),
        DownHistory = [], UpHistory = [], CpuHistory = [], GpuHistory = [], PowerHistory = [],
        PowerSummary = null, CpuPower = PowerTrack.None, GpuPower = PowerTrack.None
    };
}

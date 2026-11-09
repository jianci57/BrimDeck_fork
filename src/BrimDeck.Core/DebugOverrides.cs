namespace BrimDeck.Core;

// The debug page's overrides for the collapsed row: with the page's switch on, every reading the row shows takes the value its
// slider holds instead of the sampler's, so a layout can be studied with steady figures (a width at its widest, a zero, the
// dash of a missing reading) whatever the machine really does. The sampler itself is untouched, and the open pages keep their
// real readings: this is asked for the collapsed row's strips alone (MainWindow.UpdateCompactStrips).
public static class DebugOverrides
{
    // The memory a machine without a reading yet is assumed to have, so the share has something to be a share of.
    private const double FallbackMemory = 32L << 30, FallbackCommit = 48L << 30;

    public static MonitorSnapshot Apply(MonitorSnapshot? snapshot, DeckSettings s)
    {
        if (!s.DebugOverride) return snapshot!;
        var real = snapshot;
        double cpu = Share(s.DebugCpu), memory = Share(s.DebugMemory), gpu = Share(s.DebugGpu), storage = Share(s.DebugStorage), charge = Share(s.DebugCharge);
        // The core columns take the same share for every core; a machine without a reading yet gets one core per logical
        // processor, so the columns show.
        var previous = real?.Cpu.OrDefault;
        List<CoreSample> cores = previous?.Cores is { Count: > 0 } list
            ? [.. list.Select(core => core with { Usage = cpu })]
            : [.. Enumerable.Range(0, Math.Max(1, real?.LogicalProcessors ?? Environment.ProcessorCount)).Select(i =>
                new CoreSample(i, 0, CoreTier.Unknown, i + 1, cpu, Reading<double>.Unavailable("debug"), null))];
        var cpuSample = new CpuSample(cores, previous?.FrequencyMhz ?? Reading<double>.Unavailable("debug"))
        {
            Temperature = previous?.Temperature, TemperatureZone = previous?.TemperatureZone,
            TemperatureSource = previous?.TemperatureSource ?? default, CoreTemperatures = previous?.CoreTemperatures ?? [],
        };
        var memorySample = real?.Memory.OrDefault is { } known
            ? new MemorySample(known.Total, (ulong)(known.Total * memory), (ulong)(known.CommitLimit * memory), known.CommitLimit, known.Cached)
            : new MemorySample((ulong)FallbackMemory, (ulong)(FallbackMemory * memory), (ulong)(FallbackCommit * memory), (ulong)FallbackCommit);
        var gpuSample = real?.Gpu.OrDefault is { } adapter ? adapter with { Usage = gpu } : new GpuSample(gpu, Reading<ulong>.Unavailable("debug"));
        // The frame rate takes its values literally: the figure 0 with a low of 45 shows "0 FPS 1% 45", as the sliders say. The
        // GPU frame time follows the figure's slider (1000 over it), since a layout needs the pair to move together.
        var frames = new FrameSample(Clamp(s.DebugFrames, 999), Loc.T("调试", "Debug"))
        {
            Path = real?.Frames.OrDefault?.Path, Refresh = real?.Frames.OrDefault?.Refresh,
            Low1 = Clamp(s.DebugLow1, 999), Low01 = Clamp(s.DebugLow01, 999),
            GpuMs = s.DebugFrames > 0 ? Math.Min(1000.0 / Clamp(s.DebugFrames, 999), 999.9) : null,
        };
        var result = real ?? new MonitorSnapshot(DateTimeOffset.Now, Environment.ProcessorCount,
            Reading<CpuSample>.Unavailable("debug"), Reading<MemorySample>.Unavailable("debug"),
            Reading<NetworkSample>.Unavailable("debug"), Reading<GpuSample>.Unavailable("debug"), Reading<ProcessList>.Unavailable("debug"));
        // A power reading the machine does not give stays as it is: the row shows its dash, and a slider could not stand in for
        // a source the machine has none of. One it does give takes the slider's watts, as the battery takes its charge.
        return result with
        {
            Cpu = Reading<CpuSample>.Of(cpuSample),
            Memory = Reading<MemorySample>.Of(memorySample),
            Network = Reading<NetworkSample>.Of(new NetworkSample(Clamp(s.DebugDown, 1000) * 1_000_000, Clamp(s.DebugUp, 1000) * 1_000_000)),
            Gpu = Reading<GpuSample>.Of(gpuSample),
            Power = real?.Power.OrDefault is { } power ? Reading<PowerSample>.Of(power with { Watts = Clamp(s.DebugPower, 2000) }) : result.Power,
            Battery = real?.Battery.OrDefault is { } battery ? Reading<BatterySample>.Of(battery with { Percent = charge * 100 }) : result.Battery,
            Storage = real?.Storage is { Count: > 0 } drives
                ? [.. drives.Select(drive => drive with { Volumes = [.. drive.Volumes.Select(volume => volume with { Free = (ulong)(volume.Total * (1 - storage)) })] })]
                : result.Storage,
            Frames = Reading<FrameSample>.Of(frames),
        };
    }

    // A share of 0..1 from a slider's percent; something that is not a number counts as 0.
    private static double Share(double percent) => double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) / 100 : 0;
    private static double Clamp(double value, double max) => double.IsFinite(value) ? Math.Clamp(value, 0, max) : 0;
}

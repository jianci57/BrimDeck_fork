using BrimDeck.Core;

// DebugOverrides: with the page's switch on, the collapsed row's readings take the sliders' values in a snapshot built for
// them alone; with it off the snapshot goes through untouched.
internal static class DebugOverridesTests
{
    internal static void Run(Action<string, bool> check)
    {
        var settings = new DeckSettings { DebugOverride = true, DebugCpu = 40, DebugMemory = 50, DebugGpu = 60, DebugDown = 10, DebugUp = 5, DebugFrames = 60, DebugLow1 = 45, DebugLow01 = 38 };
        var empty = MonitorSnapshot.Empty(4, "test");
        var out_ = DebugOverrides.Apply(empty, settings);
        check("The switch off hands the snapshot back untouched",
            ReferenceEquals(DebugOverrides.Apply(empty, new DeckSettings()), empty));
        check("CPU, memory, network and GPU take the sliders' values",
            out_.Cpu.OrDefault!.Usage == 0.4 && out_.Memory.OrDefault!.UsedFraction == 0.5
            && out_.Network.OrDefault!.DownBytesPerSecond == 10_000_000 && out_.Gpu.OrDefault!.Usage == 0.6);
        check("The frame reading is always available, with the figure and the percentiles the sliders hold",
            out_.Frames.OrDefault is { } frames && frames.PerSecond == 60 && frames.Low1 == 45 && frames.Low01 == 38);
        check("A frame rate of 0 with a low of 45 shows both, as the sliders say",
            DebugOverrides.Apply(empty, new DeckSettings { DebugOverride = true, DebugFrames = 0, DebugLow1 = 45 }).Frames.OrDefault is { } zero && zero.PerSecond == 0 && zero.Low1 == 45);
        check("A snapshot that was not there yet still comes out whole",
            DebugOverrides.Apply(null, settings) is { } built && built.Cpu.HasValue && built.Memory.HasValue);
        check("The core columns share the CPU slider's value",
            DebugOverrides.Apply(empty, new DeckSettings { DebugOverride = true, DebugCpu = 80 }).Cpu.OrDefault!.Cores.All(core => core.Usage == 0.8));
    }
}

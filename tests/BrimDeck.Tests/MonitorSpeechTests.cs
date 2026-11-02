using BrimDeck.Core;

// What the GPU block says to a screen reader (MonitorSpeech.GpuBlock, which BlockGauge.Spoken returns): a usage while the adapter has
// a reading, and what the tooltip says, never a usage of zero, while it has none.
internal static class MonitorSpeechTests
{
    internal static void Run(Action<string, bool> check)
    {
        var language = Loc.Language;
        try
        {
            Loc.Use(Loc.Chinese);
            check("GPU block reads its usage and memory", MonitorSpeech.GpuBlock(.12, .18, "x", "切换为以 GPU 为主") == "GPU 占用块：占用 12% · 显存 18%，切换为以 GPU 为主");
            check("GPU block reads a real zero as zero", MonitorSpeech.GpuBlock(0, null, "x", "切换") == "GPU 占用块：占用 0%，切换");
            check("GPU block without a reading says why, not zero", MonitorSpeech.GpuBlock(null, null, "暂无读数：计数器不可用", "切换") == "GPU 占用块：暂无读数：计数器不可用，切换");
            check("GPU block without a reading ignores a stale memory figure", !MonitorSpeech.GpuBlock(null, .5, "暂无读数", "切换").Contains("显存"));
            check("GPU block without a reading or a note still says there is none", MonitorSpeech.GpuBlock(null, null, "", "切换") == "GPU 占用块：暂无读数，切换");
        }
        finally { Loc.Use(language); }
    }
}

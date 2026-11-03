using BrimDeck.Core;

internal static class MonitorRecorderTests
{
    internal static void Run(Action<string, bool> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "brimdeck-recorder-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new DeckSettings { MonitorRecording = true, MonitorRecordInterval = 5, MonitorRecordKeepDays = 30 };
            using var recorder = new MonitorRecorder(directory);

            // The pace: a sample inside the interval waits, the next one past it records, and the ring keeps both.
            recorder.OnSample(settings, Snapshot(-100, cpu: .25, down: 1000));
            recorder.OnSample(settings, Snapshot(-98, cpu: .5, down: 2000));
            recorder.OnSample(settings, Snapshot(-90, cpu: .75, down: 3000, up: 100, temp: 55, gpu: .4));
            var recent = recorder.Recent();
            check("The pace keeps faster samples out of the ring",
                recent.Count == 2 && recent[0].Cpu == 25 && recent[1].Cpu == 75 && recent[1].DownBps == 3000 && recent[1].UpBps == 100 && recent[1].Gpu == 40 && recent[1].GpuTemp == 55);

            // The day file: a header once, one line per point, values rounded and blanks empty.
            var today = Path.Combine(directory, $"monitor-{DateTime.Today:yyyy-MM-dd}.csv");
            var lines = Lines(today);
            check("The day file gets a header and one line per point",
                lines.Length == 3 && lines[0].StartsWith("time,cpu_percent,cpu_temp_c") && lines[1].Contains(",25,") && lines[2].Contains(",75,55,") && lines[2].Contains(",3000,100,"));

            // A fresh recorder restores today's ring from the file, curves survive a restart.
            using (var reloaded = new MonitorRecorder(directory))
                check("A fresh recorder restores today's ring", reloaded.Recent().Count == 2 && reloaded.Recent()[0].Cpu == 25);

            // Turning the recording off stops the ring and closes the writer.
            settings.MonitorRecording = false;
            recorder.OnSample(settings, Snapshot(-10, cpu: .9));
            check("Turning the recording off stops the ring", recorder.Recent().Count == 2);

            // The cleanup: a file older than the keep days goes, the recent days stay.
            var oldFile = Path.Combine(directory, $"monitor-{DateTime.Today.AddDays(-40):yyyy-MM-dd}.csv");
            File.WriteAllText(oldFile, "time,cpu_percent,cpu_temp_c,memory_percent,download_bps,upload_bps,gpu_percent,gpu_temp_c\n");
            recorder.Clean(30);
            check("Old days go and recent files stay", !File.Exists(oldFile) && File.Exists(today));

            // The export merges every day into one file and skips the headers; an empty folder exports nothing.
            var yesterday = Path.Combine(directory, $"monitor-{DateTime.Today.AddDays(-1):yyyy-MM-dd}.csv");
            File.WriteAllLines(yesterday,
                ["time,cpu_percent,cpu_temp_c,memory_percent,download_bps,upload_bps,gpu_percent,gpu_temp_c", "earlier-line"]);
            var export = Path.Combine(directory, "export.csv");
            var ok = recorder.Export(export, out var exported);
            check("The export merges the days and skips the headers", ok && exported == 3 && File.ReadAllLines(export).Length == 4);
            var empty = Path.Combine(directory, "empty");
            Directory.CreateDirectory(empty);
            check("An empty folder exports zero lines", new MonitorRecorder(empty).Export(Path.Combine(empty, "none.csv"), out var none) && none == 0);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* a locked file only fails the temp cleanup */ }
        }
    }

    // Reads a file the recorder may still be writing, sharing the handle the way the export does.
    private static string[] Lines(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return [.. lines];
    }

    // A controlled snapshot: the seconds set the time, the readings come as given (the CPU shares one temperature with the GPU).
    private static MonitorSnapshot Snapshot(double seconds, double? cpu = null, double? down = null, double? up = null, double? temp = null, double? gpu = null) =>
        new(DateTimeOffset.Now.AddSeconds(seconds), Environment.ProcessorCount,
            cpu is { } c
                ? new CpuSample([new CoreSample(0, 0, CoreTier.Performance, 1, c, Reading<double>.Unavailable("test"), null)], Reading<double>.Unavailable("test")) { Temperature = temp }
                : Reading<CpuSample>.Unavailable("test"),
            Reading<MemorySample>.Unavailable("test"),
            down is { } d ? new NetworkSample(d, up ?? 0) : Reading<NetworkSample>.Unavailable("test"),
            gpu is { } g ? new GpuSample(g, Reading<ulong>.Unavailable("test")) { Temperature = temp } : Reading<GpuSample>.Unavailable("test"),
            Reading<ProcessList>.Unavailable("test"));
}

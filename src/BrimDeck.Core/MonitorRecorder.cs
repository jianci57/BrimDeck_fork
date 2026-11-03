using System.Globalization;

namespace BrimDeck.Core;

// One recorded reading (MonitorRecorder): the machine-wide numbers only, the per-core and per-process detail is not recorded.
public record MonitorRecordPoint(DateTime Time, double? Cpu, double? CpuTemp, double? Memory, double? DownBps, double? UpBps, double? Gpu, double? GpuTemp);

// Writes the system monitor's readings to one CSV file per day under the data folder and keeps a ring of recent points for the
// history curves (MonitorRecordChart). SystemSampler raises its Updated event on the UI thread and App forwards it here, so the
// ring and the writer are touched from that thread only and need no lock.
public sealed class MonitorRecorder : IDisposable
{
    const int Capacity = 720; // at the default five-second pace one ring spans an hour
    const string Header = "time,cpu_percent,cpu_temp_c,memory_percent,download_bps,upload_bps,gpu_percent,gpu_temp_c";
    readonly Queue<MonitorRecordPoint> _points = new();
    readonly string _directory;
    StreamWriter? _writer;
    DateTime _day;
    DateTime _last = DateTime.MinValue;
    DateTime _lastClean = DateTime.MinValue;

    public MonitorRecorder(string directory)
    {
        _directory = directory;
        try { System.IO.Directory.CreateDirectory(_directory); } catch { /* the writes below fail on their own and are caught */ }
        LoadToday();
    }

    public string Folder => _directory;

    // Called for every sample; keeps its own pace (MonitorRecordInterval), so the panel may redraw faster than the file is written.
    public void OnSample(DeckSettings settings, MonitorSnapshot snapshot)
    {
        if (!settings.MonitorRecording) { Close(); return; }
        var time = snapshot.Time.LocalDateTime;
        if ((time - _last).TotalSeconds < settings.MonitorRecordInterval) return;
        _last = time;
        double? cpu = null, cpuTemp = null, memory = null, down = null, up = null, gpu = null, gpuTemp = null;
        if (snapshot.Cpu.TryGet(out var cpuSample))
        {
            cpu = One(cpuSample.Usage * 100);
            cpuTemp = One(cpuSample.Temperature);
        }
        if (snapshot.Memory.TryGet(out var memorySample)) memory = One(memorySample.UsedFraction * 100);
        if (snapshot.Network.TryGet(out var networkSample))
        {
            down = Math.Round(networkSample.DownBytesPerSecond);
            up = Math.Round(networkSample.UpBytesPerSecond);
        }
        if (snapshot.Gpu.TryGet(out var gpuSample))
        {
            gpu = One(gpuSample.Usage * 100);
            gpuTemp = One(gpuSample.Temperature);
        }
        var point = new MonitorRecordPoint(time, cpu, cpuTemp, memory, down, up, gpu, gpuTemp);
        _points.Enqueue(point);
        while (_points.Count > Capacity) _points.Dequeue();
        try { Write(point, settings.MonitorRecordKeepDays); } catch { Close(); }
    }

    // The recent ring for the history curves, oldest first.
    public IReadOnlyList<MonitorRecordPoint> Recent() => _points.ToArray();

    // Merges every recorded day into one file the user picked; the line count tells whether anything came out.
    public bool Export(string file, out int lines)
    {
        lines = 0;
        try
        {
            using var writer = new StreamWriter(file, false, new System.Text.UTF8Encoding(false));
            writer.WriteLine(Header);
            foreach (var path in System.IO.Directory.GetFiles(_directory, "monitor-*.csv").Order(StringComparer.OrdinalIgnoreCase))
                foreach (var line in Open(path))
                    if (!line.StartsWith("time,", StringComparison.Ordinal)) { writer.WriteLine(line); lines++; }
            return true;
        }
        catch { return false; }
    }

    // The export runs while the writer may still hold today's file, so every read shares the handle.
    private static IEnumerable<string> Open(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null) yield return line;
    }

    void Write(MonitorRecordPoint point, int keepDays)
    {
        var day = point.Time.Date;
        if (_writer is null || day != _day)
        {
            Close();
            _day = day;
            var path = Path.Combine(_directory, $"monitor-{day:yyyy-MM-dd}.csv");
            var fresh = !File.Exists(path);
            _writer = new StreamWriter(path, true, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            if (fresh) _writer.WriteLine(Header);
            if ((day - _lastClean).TotalDays >= 1) { _lastClean = day; Clean(keepDays); }
        }
        _writer.WriteLine(Format(point));
    }

    // Recorded files older than the keep days go, checked once a day when a new file starts.
    public void Clean(int keepDays)
    {
        try
        {
            var cutoff = DateTime.Today.AddDays(-keepDays);
            foreach (var path in System.IO.Directory.GetFiles(_directory, "monitor-*.csv"))
                if (DayOf(path) is not { } day || day < cutoff) File.Delete(path);
        }
        catch { /* a locked or unparseable file simply stays */ }
    }

    static DateTime? DayOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length == 18 && DateTime.TryParseExact(name.AsSpan(8), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
    }

    // Restores the ring from today's file, so the curves survive a restart (the read also re-arms the pace).
    void LoadToday()
    {
        try
        {
            var path = Path.Combine(_directory, $"monitor-{DateTime.Today:yyyy-MM-dd}.csv");
            if (!File.Exists(path)) return;
            foreach (var line in Open(path))
                if (Parse(line) is { } point) { _points.Enqueue(point); while (_points.Count > Capacity) _points.Dequeue(); }
            if (_points.Count > 0) _last = _points.Last().Time;
            _day = DateTime.Today;
        }
        catch { /* a broken file simply starts a fresh ring */ }
    }

    void Close()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    public void Dispose() => Close();

    static string Format(MonitorRecordPoint p) => string.Join(",",
        p.Time.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        Cell(p.Cpu), Cell(p.CpuTemp), Cell(p.Memory), Cell(p.DownBps), Cell(p.UpBps), Cell(p.Gpu), Cell(p.GpuTemp));

    static string Cell(double? value) => value is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) : "";

    static MonitorRecordPoint? Parse(string line)
    {
        var parts = line.Split(',');
        if (parts.Length != 8 || !DateTime.TryParseExact(parts[0], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        return new MonitorRecordPoint(time, Num(parts[1]), Num(parts[2]), Num(parts[3]), Num(parts[4]), Num(parts[5]), Num(parts[6]), Num(parts[7]));
    }

    static double? Num(string cell) => double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    static double? One(double? value) => value is { } v ? Math.Round(v, 1) : null;
}

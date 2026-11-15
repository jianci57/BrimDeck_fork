using System.Diagnostics;
using System.IO;
using BrimDeck.Core;
using BrimDeck.Native;

// The guard over readings that ask a driver (SensorGuard), in a folder of its own: the mark on disk while a reading is asked, a
// reading that never answers stopped and its thread told, a mark left behind stopping the reading at the next start, and turning
// a reading on again. Run last: the guard is static, and its stops would hold for the other checks.
internal static class SensorGuardChecks
{
    public static void Run(Action<string, bool> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck-SensorGuard-" + Guid.NewGuid().ToString("N"));
        string pending = Path.Combine(folder, SensorStops.PendingFile);
        try
        {
            SensorGuard.Start(folder);
            check("sensor guard: nothing stopped in a new folder", SensorGuard.Stops.Count == 0);
            var drive = new GuardedDevice(GuardedReading.DriveTemperature, "Test SSD | 17", "Test SSD");
            string during = SensorGuard.Ask(drive, () => File.ReadAllText(pending));
            check("sensor guard: the reading is marked on disk while it is asked, and the mark goes once it answers",
                SensorStops.ParsePending(during, DateTimeOffset.Now) is [{ Device: "Test SSD | 17" }] && SensorStops.ParsePending(File.ReadAllText(pending), DateTimeOffset.Now).Count == 0);

            // Only the first readings of a device are marked on disk (the drive's temperature is marked every time): the others are asked
            // without a flush, so the sampling thread is not held up by one each tick.
            var board = new GuardedDevice(GuardedReading.CpuTemperature, "Test board", "CPU");
            var marks = new List<bool>();
            for (int i = 0; i < SensorStops.MarkedReadings + 3; i++)
                marks.Add(SensorStops.ParsePending(SensorGuard.Ask(board, () => File.ReadAllText(pending)), DateTimeOffset.Now).Count == 1);
            check("sensor guard: only the first readings of a device are marked on disk, and none is left once they answer",
                marks.Take(SensorStops.MarkedReadings).All(marked => marked) && marks.Skip(SensorStops.MarkedReadings).All(marked => !marked)
                && SensorStops.ParsePending(File.ReadAllText(pending), DateTimeOffset.Now).Count == 0);

            // A slow disk is not a slow driver: a mark that takes longer than the watch to write does not stop a reading that has not been
            // asked yet, and the watch counts from the driver's own time.
            var slow = new GuardedDevice(GuardedReading.DriveTemperature, "Slow disk SSD | 1", "Slow disk SSD");
            int delays = 0;
            SensorGuard.BeforeWrite = () => { if (Interlocked.Increment(ref delays) == 1) Thread.Sleep((SensorStops.StuckSeconds + 2) * 1000); };
            bool slowAbandoned = false;
            try { SensorGuard.Ask(slow, () => 1); } catch (ReadingAbandoned) { slowAbandoned = true; }
            SensorGuard.BeforeWrite = null;
            check("sensor guard: a mark that is slow to reach the disk does not stop the reading", !slowAbandoned && !SensorGuard.Stopped(GuardedReading.DriveTemperature, slow.Device));

            // A reading that does not answer in time: stopped, its caller told, and its thread ends once it answers after all.
            var gpu = new GuardedDevice(GuardedReading.GpuTemperature, "Test GPU", "Test GPU");
            int told = 0, changed = 0;
            void Changed() => changed++;
            SensorGuard.Changed += Changed;
            bool abandoned = false;
            var noted = new List<string>();
            var previousSink = MonitorLog.Sink;
            MonitorLog.Sink = noted.Add;
            var watch = Stopwatch.StartNew();
            try { SensorGuard.Ask(gpu, () => { Thread.Sleep((SensorStops.StuckSeconds + 3) * 1000); return 0; }, () => Interlocked.Increment(ref told)); }
            catch (ReadingAbandoned) { abandoned = true; }
            Console.WriteLine($"  a stuck reading was given up after at most {watch.Elapsed.TotalSeconds:0.0} s");
            check("sensor guard: a reading that does not answer is stopped, its caller told once, and its thread ends", abandoned && told == 1 && changed == 1
                && SensorGuard.Stopped(GuardedReading.GpuTemperature, "Test GPU") && !SensorGuard.Stopped(GuardedReading.GpuTemperature, "Other GPU"));
            MonitorLog.Sink = previousSink;
            check("sensor guard: a reading given up on is noted in the log once", noted.Count == 1 && noted[0].Contains("Test GPU"));
            check("sensor guard: the stop is kept on disk", SensorStops.Parse(File.ReadAllText(Path.Combine(folder, SensorStops.StopsFile))) is [{ Device: "Test GPU" }]);

            // A mark left behind by a run that ended while a reading was asked (a frozen machine) stops that reading at the next start.
            File.WriteAllText(pending, SensorStops.SerializePending([drive]));
            SensorGuard.Start(folder);
            check("sensor guard: a mark left behind stops its reading at the next start", SensorGuard.Stopped(GuardedReading.DriveTemperature, "Test SSD | 17")
                && SensorGuard.Stopped(GuardedReading.GpuTemperature, "Test GPU") && SensorStops.ParsePending(File.ReadAllText(pending), DateTimeOffset.Now).Count == 0);

            foreach (var stop in SensorGuard.Stops) SensorGuard.Resume(stop);
            SensorGuard.Changed -= Changed;
            check("sensor guard: turned on again, nothing is stopped, now or at the next start", SensorGuard.Stops.Count == 0 && changed == 3
                && SensorStops.Parse(File.ReadAllText(Path.Combine(folder, SensorStops.StopsFile))).Count == 0);
            SensorGuard.Finish();
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}

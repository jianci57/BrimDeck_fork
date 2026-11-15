using System.Diagnostics;
using System.Runtime.InteropServices;
using BrimDeck.Core;
using BrimDeck.Native;

// The id Windows resolves for a process that set its own, the way QueMusic does ("BroNekoX.QueMusic" for QueMusic.exe):
// the shell's resolver is not a documented interface, so this is where a Windows update that changes it would show.
internal static class AppIdentityChecks
{
    public static void Run(Action<string, bool> check)
    {
        const string id = "BrimDeck.Tests.IdentityProbe";
        check("app identity: this process sets its own application id", SetCurrentProcessExplicitAppUserModelID(id) == 0);
        var self = AppIdentity.Describe(Environment.ProcessId);
        using var process = Process.GetCurrentProcess();
        check("app identity: the process is described by its executable and the id it set",
            self.AppId == id && string.Equals(self.ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase) && self.PackageFamily is null);
        check("app identity: the process plays for a media session of that id, and not for one of another",
            MediaAppTarget.Plays(new(null, MediaAppTarget.ProcessName(id), id), self)
            && !MediaAppTarget.Plays(new(null, MediaAppTarget.ProcessName("BrimDeck.Tests.Other"), "BrimDeck.Tests.Other"), self));
        check("app identity: an application Windows does not list has no registered name", AppIdentity.RegisteredName(id) is null);

        // The name read from a process's handle (NameAndPath) is compared with saved window rules and clipboard exclusions, which
        // were written with Process.ProcessName: the two must be the same for every process that can be opened.
        int compared = 0;
        var different = new List<string>();
        foreach (var other in Process.GetProcesses())
            using (other)
            {
                string expected;
                try { expected = other.ProcessName; } catch (InvalidOperationException) { continue; }
                var (name, path) = AppIdentity.NameAndPath(other.Id);
                // Not opened (it ended, or is protected): the name came from Process itself.
                if (path.Length == 0) continue;
                // A running program whose file was renamed under it (an updater or Defender moving it aside to "name.exe~RF1234.TMP") has
                // that name on disk while Process keeps the one it started with: not a mismatch of the two readings.
                if (!Comparable(path)) continue;
                compared++;
                if (name != expected) different.Add($"{expected} / {name}");
            }
        check($"app identity: the name read from a process's handle is the one Process gives ({compared} processes{(different.Count > 0 ? "; differs: " + string.Join(", ", different.Take(5)) : "")})",
            compared > 0 && different.Count == 0);
        check("app identity: a program file renamed aside (name.exe~RF1234.TMP) is not compared, plain .exe paths are in any case",
            !Comparable(@"C:\Users\x\AppData\Local\Tracing.EventSource.check.exe~RF253479.TMP") && !Comparable(@"C:\a\b.TMP") && !Comparable(@"C:\a\b.exe.bak")
            && Comparable(@"C:\a\b.exe") && Comparable(@"C:\a\B.EXE") && Comparable(@"C:\a\b.Exe"));
        check("app identity: this process's path is its executable's", string.Equals(AppIdentity.NameAndPath(Environment.ProcessId).Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
        // What reading from the handle saves, on this machine.
        const int rounds = 100;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < rounds; i++) AppIdentity.NameAndPath(Environment.ProcessId);
        double handle = watch.Elapsed.TotalMilliseconds / rounds;
        watch.Restart();
        for (int i = 0; i < rounds; i++) { using var again = Process.GetProcessById(Environment.ProcessId); _ = again.ProcessName; _ = again.MainModule?.FileName; }
        Console.WriteLine($"app identity: a process's name and path take {handle:0.000} ms from its handle, {watch.Elapsed.TotalMilliseconds / rounds:0.000} ms from Process");
    }

    // Whether a process's image path is a plain program file, so that the name on disk and the one Process keeps are the same.
    internal static bool Comparable(string path) => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
}

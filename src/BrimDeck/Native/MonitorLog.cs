using System.Diagnostics;
using System.IO;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Where the readings' swallowed failures go: monitor.log in the data folder (a file of its own, so these lines never push the unhandled
// errors of crash.log out through its size limit), and the debugger. Each kind of failure of each source is written once in ten minutes (FailureNotes), so a provider that fails every tick
// leaves one line.
internal static class MonitorLog
{
    private const long Limit = 1_048_576;
    private static readonly FailureNotes Notes = new(() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, Deliver);
    // Where the lines go instead of the log file (the tests).
    public static Action<string>? Sink { get; set; }

    public static void Note(string source, Exception error)
    {
        Debug.WriteLine(error);
        Notes.Note(source, error);
    }

    public static void Note(string source, string kind, string text)
    {
        Debug.WriteLine(source + ": " + text);
        Notes.Note(source, kind, text);
    }

    // Never throws: it is called from the handlers of the readings, and a logger that fails must not end the sampler.
    private static void Deliver(string line)
    {
        try { (Sink ?? Write)(line); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static readonly object Gate = new();
    private static void Write(string line)
    {
        lock (Gate)
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "monitor.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            if (File.Exists(log) && new FileInfo(log).Length > Limit) File.Delete(log);
            File.AppendAllText(log, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {ApplicationVersion.Current}{Environment.NewLine}{line}{Environment.NewLine}{Environment.NewLine}");
        }
    }
}

namespace BrimDeck.Core;

// The applications whose copies the clipboard leaves out (DeckSettings.ClipboardExcluded). An entry is the executable's full
// path when the app was picked in the settings, or a process name typed in earlier versions. Either matches every process
// with that executable name, wherever it is installed.
public static class ClipboardExclusions
{
    // The process name an entry names: the file name of a path, without ".exe".
    public static string ProcessName(string entry)
    {
        string name = IsPath(entry) ? Path.GetFileName(entry.Trim()) : entry.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static bool IsPath(string entry) => entry.Contains('\\') || entry.Contains('/');

    public static bool Excludes(IEnumerable<string> entries, string processName) =>
        processName.Length > 0 && entries.Any(entry => string.Equals(ProcessName(entry), processName, StringComparison.OrdinalIgnoreCase));

    // Paths kept whole, names without ".exe", one entry per process name.
    public static List<string> Normalize(IEnumerable<string?>? entries) =>
        [.. (entries ?? []).Select(entry => (entry ?? "").Trim()).Select(entry => IsPath(entry) ? entry : ProcessName(entry))
            .Where(entry => ProcessName(entry).Length > 0).DistinctBy(ProcessName, StringComparer.OrdinalIgnoreCase)];
}

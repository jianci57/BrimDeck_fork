using System.Runtime.InteropServices;

namespace BrimDeck.Native;

// The applications that currently have a visible top-level window, for a list that picks an app by process name. One entry
// per process: several windows of an app describe the same application.
internal static class RunningApps
{
    internal sealed record App(string Name, string Path);

    public static IReadOnlyList<App> WithWindows()
    {
        var apps = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<uint>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0) return true;
            GetWindowThreadProcessId(window, out var pid);
            // Each process is read once, however many windows it has.
            if (pid == 0 || !seen.Add(pid)) return true;
            var (name, path) = AppIdentity.NameAndPath((int)pid);
            if (name.Length > 0 && !apps.ContainsKey(name)) apps[name] = new App(name, path);
            return true;
        }, IntPtr.Zero);
        return apps.Values.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrimDeck;

// What the file transfer shows about a file: the icon and type name the Explorer shows (SHGetFileInfo, as the monitor page's
// process list does), whether the file is still there, its size, and for a folder the items in it and, counted in the background,
// its total size. A folder's figures arrive later; Changed then asks the page to show them.
internal static class ShelfFiles
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, FolderStats> Folders = new(StringComparer.OrdinalIgnoreCase);
    public static event Action? Changed;

    public sealed class FolderStats
    {
        private readonly object _gate = new();
        private int? _items; private long? _size; private bool _counting;
        public int? Items { get { lock (_gate) return _items; } set { lock (_gate) _items = value; } }
        public long? Size { get { lock (_gate) return _size; } set { lock (_gate) _size = value; } }
        public bool Counting { get { lock (_gate) return _counting; } set { lock (_gate) _counting = value; } }
    }
    private static readonly object FolderGate = new();
    private static int _probingMoved;
    internal enum PathState { Present, ConfirmedMissing, Unavailable }

    // Exists suppresses access/network errors. Only a completed, accessible parent listing establishes absence.
    internal static PathState ProbePath(string path)
    {
        try
        {
            try { _ = File.GetAttributes(path); return PathState.Present; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            string full = Path.GetFullPath(path), parent = Path.GetDirectoryName(full) ?? "";
            if (parent.Length == 0) return PathState.Unavailable;
            foreach (string entry in Directory.EnumerateFileSystemEntries(parent))
                if (string.Equals(entry, full, StringComparison.OrdinalIgnoreCase)) return PathState.Present;
            return PathState.ConfirmedMissing;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return PathState.Unavailable; }
    }

    internal static Task<PathState[]>? TryProbeMoved(string[] paths)
    {
        if (Interlocked.CompareExchange(ref _probingMoved, 1, 0) != 0) return null;
        return Task.Run(() =>
        {
            try { return paths.Select(ProbePath).ToArray(); }
            finally { Volatile.Write(ref _probingMoved, 0); }
        });
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public static bool IsFolder(string path) => Directory.Exists(path);

    public static long? FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    public static DateTime? Modified(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTime(path) : Directory.Exists(path) ? Directory.GetLastWriteTime(path) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // The folder's figures so far; the first ask starts counting: the items in the folder itself first, then the total size.
    public static FolderStats Folder(string path)
    {
        FolderStats stats;
        lock (FolderGate)
        {
            stats = Folders.GetOrAdd(path, _ => new FolderStats());
            if (stats.Items is not null || stats.Counting) return stats;
            stats.Counting = true;
        }
        Task.Run(() =>
            {
                try { stats.Items = Directory.EnumerateFileSystemEntries(path).Count(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { stats.Items = 0; }
                Raise();
                long total = 0;
                try
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options)) total += file.Length;
                    stats.Size = total;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
                lock (FolderGate) stats.Counting = false;
                Raise();
            });
        return stats;
    }
    // A running traversal keeps ownership until completion; another drop must not start a duplicate traversal.
    public static void Forget(string path)
    {
        lock (FolderGate)
            if (Folders.TryGetValue(path, out var stats) && !stats.Counting) Folders.TryRemove(path, out _);
    }
    private static void Raise() => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    // Lets go of the icons, type names and folder figures read so far (the page is switched off); false when there were none.
    public static bool Rest()
    {
        bool held = !Icons.IsEmpty || !Types.IsEmpty;
        Icons.Clear(); Types.Clear();
        lock (FolderGate)
            foreach (var (path, stats) in Folders)
                if (!stats.Counting) held |= Folders.TryRemove(path, out _);
        return held;
    }

    // The Explorer's icon for the file: 32 px, drawn at 16 to 48 DIP. Read on the UI thread, which is an STA thread as the shell needs.
    public static ImageSource? Icon(string path)
    {
        if (Icons.TryGetValue(path, out var known)) return known;
        ImageSource? icon = null;
        var info = new ShellFileInfo();
        // SHGFI_ICON (0x100); SHGFI_SYSICONINDEX is not needed. A missing file still gets the icon of its type (SHGFI_USEFILEATTRIBUTES, 0x10).
        uint flags = 0x100u | (Exists(path) ? 0 : 0x10u);
        if (SHGetFileInfoW(path, 0x80, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags) != IntPtr.Zero && info.Icon != IntPtr.Zero)
        {
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze(); icon = source;
            }
            catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException) { }
            finally { DestroyIcon(info.Icon); }
        }
        if (Icons.Count > 1000) Icons.Clear();
        Icons[path] = icon;
        return icon;
    }
    // The Explorer's name for the file's type, such as "PNG 文件" or "文件夹".
    public static string TypeName(string path)
    {
        if (Types.TryGetValue(path, out var known)) return known;
        var info = new ShellFileInfo();
        string name = SHGetFileInfoW(path, 0x80, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x400u | (Exists(path) ? 0 : 0x10u)) != IntPtr.Zero ? info.TypeName ?? "" : "";
        Types[path] = name;
        return name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}

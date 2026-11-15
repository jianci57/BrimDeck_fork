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
// its total size. A folder's figures and a file's icon arrive later; Changed then asks the page to show them.
internal static class ShelfFiles
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, FolderStats> Folders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(string Path, int Pixels), ImageSource?> Larges = new();
    public static event Action? Changed;

    // The shell takes its time over an icon the first time it is asked: 5 ms for the small one and 15 for a large one on average,
    // and over 100 now and then (measured on Windows 11 with the 48 files of a desktop). Read where the rows are built, that
    // held up the page each time a file came into view, and the list stuttered as it scrolled. So icons are read on a thread of
    // their own, an STA thread as the shell needs, one after another in the order asked; a row shows its icon when it arrives.
    private static readonly BlockingCollection<Action> Reads = [];
    private static readonly ConcurrentDictionary<object, bool> Asked = new();
    private static int _reader, _pending, _raised;
    // Icons asked for and not yet read; for the pictures of the README, which wait for them.
    public static int Pending => Volatile.Read(ref _pending);
    private static void Read(object key, Action read)
    {
        if (!Asked.TryAdd(key, true)) return;
        Interlocked.Increment(ref _pending);
        if (Interlocked.Exchange(ref _reader, 1) == 0)
        {
            var thread = new Thread(() =>
            {
                foreach (var next in Reads.GetConsumingEnumerable())
                {
                    try { next(); }
                    catch (Exception error) when (error is COMException or ExternalException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
                    Interlocked.Decrement(ref _pending);
                    Raise();
                }
            }) { IsBackground = true, Name = "BrimDeck file icons" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        Reads.Add(read);
    }

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
    // Several arrivals before the page has looked are one notice.
    private static void Raise()
    {
        if (Interlocked.Exchange(ref _raised, 1) != 0) return;
        if (Application.Current is not { } app) { Volatile.Write(ref _raised, 0); return; }
        app.Dispatcher.BeginInvoke(() => { Volatile.Write(ref _raised, 0); Changed?.Invoke(); });
    }

    // Lets go of the icons, type names and folder figures read so far (the page is switched off); false when there were none.
    public static bool Rest()
    {
        bool held = !Icons.IsEmpty || !Types.IsEmpty || !Larges.IsEmpty;
        Icons.Clear(); Types.Clear(); Larges.Clear(); Asked.Clear();
        lock (FolderGate)
            foreach (var (path, stats) in Folders)
                if (!stats.Counting) held |= Folders.TryRemove(path, out _);
        return held;
    }

    // The Explorer's icon for the file: 32 px, drawn at 16 to 48 DIP. Null until it has been read (Changed says when).
    public static ImageSource? Icon(string path)
    {
        if (Icons.TryGetValue(path, out var known)) return known;
        Read(path, () => Icons[path] = ReadIcon(path));
        return null;
    }
    private static ImageSource? ReadIcon(string path)
    {
        ImageSource? icon = null;
        var info = new ShellFileInfo();
        // SHGFI_ICON (0x100); SHGFI_SYSICONINDEX is not needed. A missing file still gets the icon of its type (SHGFI_USEFILEATTRIBUTES, 0x10).
        uint flags = 0x100u | (Exists(path) ? 0 : 0x10u);
        if (SHGetFileInfoW(path, 0x80, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags) != IntPtr.Zero && info.Icon != IntPtr.Zero)
        {
            try
            {
                // Copied out of the icon's own bitmap, which belongs to the thread that made it.
                var made = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                int stride = made.PixelWidth * 4;
                var bits = new byte[stride * made.PixelHeight];
                new FormatConvertedBitmap(made, PixelFormats.Pbgra32, null, 0).CopyPixels(bits, stride, 0);
                var source = BitmapSource.Create(made.PixelWidth, made.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, bits, stride);
                source.Freeze(); icon = source;
            }
            catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException) { }
            finally { DestroyIcon(info.Icon); }
        }
        if (Icons.Count > 1000) { Icons.Clear(); Asked.Clear(); }
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

    // ---- Large pictures ----
    // The file's picture for the ways of showing files with large icons (FileView): the Explorer's icon at the size asked, in
    // device pixels (IShellItemImageFactory; the icon above is 32 px, which blurs when drawn larger), and for a picture file its
    // own picture, decoded in the background and kept in the icon's place; Changed then asks the page to show it. Null until it
    // has been read, and when the shell has none: the caller shows the small icon. Sizes go in steps, so a panel scaled a little
    // keeps what was read.
    private static readonly HashSet<string> PictureTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico" };
    public static ImageSource? Large(string path, int pixels)
    {
        pixels = pixels <= 32 ? 32 : pixels <= 48 ? 48 : pixels <= 64 ? 64 : pixels <= 96 ? 96 : 128;
        var key = (path, pixels);
        if (Larges.TryGetValue(key, out var known)) return known;
        Read(key, () =>
        {
            if (Larges.Count > 600) { Larges.Clear(); Asked.Clear(); }
            Larges[key] = ShellImage(path, pixels);
            if (!File.Exists(path) || !PictureTypes.Contains(Path.GetExtension(path))) return;
            Interlocked.Increment(ref _pending);
            _ = Task.Run(() =>
            {
                if (Thumbnail(path, pixels) is { } picture) Larges[key] = picture;
                Interlocked.Decrement(ref _pending);
                Raise();
            });
        });
        return null;
    }
    private static ImageSource? ShellImage(string path, int pixels)
    {
        IntPtr bitmap = IntPtr.Zero, screen = IntPtr.Zero;
        try
        {
            var id = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref id, out var factory) != 0 || factory is null) return null;
            // SIIGBF_ICONONLY (4): the icon, never a thumbnail, which can take long to make.
            try { if (factory.GetImage(new ImageSize { Width = pixels, Height = pixels }, 4, out bitmap) != 0 || bitmap == IntPtr.Zero) return null; }
            finally { Marshal.ReleaseComObject(factory); }
            if (GetObjectW(bitmap, Marshal.SizeOf<BitmapShape>(), out var shape) == 0 || shape.Width <= 0 || shape.Height <= 0) return null;
            int width = shape.Width, height = shape.Height, stride = width * 4;
            var bits = new byte[stride * height];
            // Top to bottom, 32 bits a pixel. The colors are not multiplied by their alpha (measured on Windows 11: most of an
            // icon's half-clear pixels hold a color above their alpha); read as if they were, every soft edge showed bright and ragged.
            var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            screen = GetDC(IntPtr.Zero);
            if (GetDIBits(screen, bitmap, 0, (uint)height, bits, ref header, 0) == 0) return null;
            // An icon without an alpha channel comes with every alpha at zero: it is opaque.
            bool clear = true;
            for (int i = 3; i < bits.Length && clear; i += 4) clear = bits[i] == 0;
            if (clear) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bits, stride);
            source.Freeze();
            return source;
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException or InvalidCastException) { return null; }
        finally
        {
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }
    }
    // A picture file's own picture, no larger than asked along its longer side. Read on a pool thread and frozen, so the page's
    // thread can show it.
    private static ImageSource? Thumbnail(string path, int pixels)
    {
        try
        {
            if (new FileInfo(path).Length > 64L * 1024 * 1024) return null;
            int width, height;
            using (var stream = File.OpenRead(path))
            {
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                width = frame.PixelWidth; height = frame.PixelHeight;
            }
            if (width <= 0 || height <= 0) return null;
            using var file = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width >= height) image.DecodePixelWidth = Math.Min(width, pixels); else image.DecodePixelHeight = Math.Min(height, pixels);
            image.StreamSource = file;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException
            or COMException or FileFormatException or OutOfMemoryException or OverflowException) { return null; }
    }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(ImageSize size, int flags, out IntPtr bitmap);
    }
    [StructLayout(LayoutKind.Sequential)] private struct ImageSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapShape { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader { public int Size, Width, Height; public ushort Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr handle, int size, out BitmapShape shape);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr context, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapHeader header, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr context);

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

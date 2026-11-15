using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace BrimDeck;

// The files and folders in what is dragged onto the panel. The Explorer gives a list of paths (CF_HDROP) only when everything
// selected is a file or a folder; with "此电脑" or "回收站" among the desktop's icons it gives none (measured on Windows 11: a
// selection of files and such an item offers only "Shell IDList Array"), and the panel then took the drag for something it has no
// use for. So without the list of paths the shell's own list is read: the items that have a path are taken, the others passed over.
internal static class DroppedFiles
{
    private const string ShellItems = "Shell IDList Array";

    // Whether the drag may hold files; cheap, for the many calls while it is over the panel.
    public static bool May(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(ShellItems);

    public static string[] Paths(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) return files;
            return data.GetDataPresent(ShellItems) && data.GetData(ShellItems) is MemoryStream items ? FromShellItems(items.ToArray()) : [];
        }
        catch (Exception error) when (error is COMException or ExternalException or InvalidOperationException or OutOfMemoryException) { return []; }
    }

    // CIDA: the number of items, then that many and one more offsets from the start: the folder first, then each item in it.
    private static string[] FromShellItems(byte[] bytes)
    {
        if (bytes.Length < 8) return [];
        uint count = BitConverter.ToUInt32(bytes, 0);
        if (count == 0 || count > 10000 || bytes.Length < 4 + 4 * ((long)count + 1)) return [];
        var paths = new List<string>();
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            IntPtr start = pinned.AddrOfPinnedObject();
            uint folder = BitConverter.ToUInt32(bytes, 4);
            if (folder >= bytes.Length) return [];
            var text = new StringBuilder(32768);
            for (int i = 1; i <= count; i++)
            {
                uint item = BitConverter.ToUInt32(bytes, 4 + 4 * i);
                if (item >= bytes.Length) continue;
                IntPtr whole = ILCombine(start + (int)folder, start + (int)item);
                if (whole == IntPtr.Zero) continue;
                try
                {
                    text.Clear();
                    if (SHGetPathFromIDListEx(whole, text, text.Capacity, 0) && text.Length > 0 && (File.Exists(text.ToString()) || Directory.Exists(text.ToString()))) paths.Add(text.ToString());
                }
                finally { ILFree(whole); }
            }
        }
        finally { pinned.Free(); }
        return [.. paths];
    }

    [DllImport("shell32.dll")] private static extern IntPtr ILCombine(IntPtr folder, IntPtr item);
    [DllImport("shell32.dll")] private static extern void ILFree(IntPtr list);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SHGetPathFromIDListEx(IntPtr list, StringBuilder path, int size, int options);
}

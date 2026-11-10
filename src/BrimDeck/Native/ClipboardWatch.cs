using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

// What one copy held, as read from the clipboard: text with its rich forms, or a picture encoded as PNG.
internal sealed record ClipCopy(string Text, string Html, string Rtf, byte[]? Png, int PixelWidth, int PixelHeight, string Source);

// Tells when the clipboard changes (AddClipboardFormatListener on a message-only window) and reads what was copied. The change
// notice arrives while the copying application may still hold the clipboard open, so the read waits a moment and tries a few
// times. Copies marked as private by password managers and by Windows' own history settings are not read at all.
internal sealed class ClipboardWatch : IDisposable
{
    private readonly HwndSource _window;
    private readonly DispatcherTimer _read = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly Action<ClipCopy> _copied;
    private readonly Func<string, bool> _excluded;
    private uint _seen, _written;
    private int _tries;
    private bool _disposed;

    private static readonly uint ExcludeFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint HistoryFormat = RegisterClipboardFormat("CanIncludeInClipboardHistory");

    public ClipboardWatch(Action<ClipCopy> copied, Func<string, bool> excluded)
    {
        _copied = copied; _excluded = excluded;
        // HWND_MESSAGE: a window that only receives messages.
        _window = new HwndSource(new HwndSourceParameters("BrimDeck.ClipboardWatch") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(Hook);
        if (!AddClipboardFormatListener(_window.Handle)) { int error = Marshal.GetLastWin32Error(); _window.Dispose(); throw new Win32Exception(error); }
        _seen = GetClipboardSequenceNumber();
        _read.Tick += (_, _) => Read();
    }

    // The clipboard as BrimDeck itself just set it, to paste: not recorded as a new copy.
    public void IgnoreCurrent() => _written = _seen = GetClipboardSequenceNumber();

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x031D && !_disposed) { _tries = 0; _read.Stop(); _read.Start(); } // WM_CLIPBOARDUPDATE
        return IntPtr.Zero;
    }

    private void Read()
    {
        _read.Stop();
        if (_disposed) return;
        uint sequence = GetClipboardSequenceNumber();
        if (sequence == _seen || sequence == _written) return;
        try
        {
            if (Private()) { _seen = sequence; return; }
            string source = OwnerName();
            if (source.Length > 0 && _excluded(source)) { _seen = sequence; return; }
            var copy = ReadCopy(source);
            // A newer copy arrived while this one was read: read again for that one.
            if (GetClipboardSequenceNumber() != sequence) { Retry(); return; }
            _seen = sequence;
            if (copy is not null) _copied(copy);
        }
        catch (Exception error) when (error is ExternalException or IOException or ArgumentException or InvalidOperationException or NotSupportedException or OutOfMemoryException)
        { Retry(); }
    }
    private void Retry() { if (++_tries < 4) _read.Start(); else _seen = GetClipboardSequenceNumber(); }

    private static ClipCopy? ReadCopy(string source)
    {
        var data = Clipboard.GetDataObject();
        if (data is null) return null;
        // Files copied in the Explorer belong on the file transfer, not in the clipboard's history.
        if (data.GetDataPresent(DataFormats.FileDrop, false)) return null;
        string text = Text(data, DataFormats.UnicodeText) ?? "";
        if (text.Trim().Length == 0 && Picture(data) is { } picture)
            return new ClipCopy("", "", "", picture.Png, picture.Width, picture.Height, source);
        if (text.Trim().Length == 0 || text.Length > Shelf.MaxText) return null;
        return new ClipCopy(text, Text(data, DataFormats.Html) ?? "", Text(data, DataFormats.Rtf) ?? "", null, 0, 0, source);
    }
    private static string? Text(IDataObject data, string format)
    {
        if (!data.GetDataPresent(format, false)) return null;
        return data.GetData(format, false) switch
        {
            string text when text.Length <= Shelf.MaxText => text,
            MemoryStream stream when stream.Length <= Shelf.MaxText => System.Text.Encoding.UTF8.GetString(stream.ToArray()).TrimEnd('\0'),
            _ => null
        };
    }
    // The largest picture kept, in pixels and as PNG bytes.
    private const int MaxSide = 8192, MaxPixels = 16_777_216, MaxBytes = 32 * 1024 * 1024;
    private static (byte[] Png, int Width, int Height)? Picture(IDataObject data)
    {
        BitmapSource? image = null;
        foreach (var format in new[] { "PNG", "image/png" })
        {
            if (!data.GetDataPresent(format, false) || data.GetData(format, false) is not MemoryStream stream || stream.Length > MaxBytes) continue;
            try { var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); image = decoder.Frames[0]; break; }
            catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException) { }
        }
        if (image is null && data.GetDataPresent(DataFormats.Bitmap)) image = data.GetData(DataFormats.Bitmap) as BitmapSource;
        if (image is null || image.PixelWidth is <= 0 or > MaxSide || image.PixelHeight is <= 0 or > MaxSide || (long)image.PixelWidth * image.PixelHeight > MaxPixels) return null;
        image = Opaque(image);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream(); encoder.Save(output);
        return output.Length > MaxBytes ? null : (output.ToArray(), image.PixelWidth, image.PixelHeight);
    }
    // Screenshots copied only as a bitmap are read with every pixel's alpha at 0, which would show black and paste as fully
    // transparent: such a picture is taken as opaque.
    private static BitmapSource Opaque(BitmapSource image)
    {
        if (image.Format != PixelFormats.Bgra32) return image;
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) return image;
        return new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
    }

    // ExcludeClipboardContentFromMonitorProcessing present, or CanIncludeInClipboardHistory set to 0: the copy asks not to be kept.
    private static bool Private()
    {
        if (IsClipboardFormatAvailable(ExcludeFormat)) return true;
        if (!IsClipboardFormatAvailable(HistoryFormat)) return false;
        if (!OpenClipboard(IntPtr.Zero)) throw new ExternalException("The clipboard is busy.");
        try
        {
            var handle = GetClipboardData(HistoryFormat);
            if (handle == IntPtr.Zero || GlobalSize(handle).ToUInt64() < 4) return true;
            var memory = GlobalLock(handle);
            if (memory == IntPtr.Zero) return true;
            try { return Marshal.ReadInt32(memory) == 0; } finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }
    private static string OwnerName()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return "";
        GetWindowThreadProcessId(owner, out uint pid);
        if (pid == 0) return "";
        try { using var process = System.Diagnostics.Process.GetProcessById((int)pid); return process.ProcessName; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { return ""; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _read.Stop();
        RemoveClipboardFormatListener(_window.Handle); _window.RemoveHook(Hook); _window.Dispose();
    }

    [DllImport("user32", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32")] private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32")] private static extern bool CloseClipboard();
    [DllImport("user32")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32")] private static extern bool GlobalUnlock(IntPtr memory);
}

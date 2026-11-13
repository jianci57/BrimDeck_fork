using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The clipboard history and the file transfer, shared by every panel. Kept in the data folder beside settings.json, encrypted
// for the current Windows user (DPAPI), as the API keys are: shelf.dat holds the entries, shelf\<id>.dat each picture.
internal sealed class ShelfService : IDisposable
{
    private readonly string _folder, _file, _pictures;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _save;
    private ClipboardWatch? _watch;
    private bool _unreadable;
    public ShelfData Data { get; private set; } = new();
    // A copy was recorded, an entry changed, files were added: the panels show the page again.
    public event Action? Changed;
    private Func<DeckSettings> _settings = () => new();

    public ShelfService(string folder, Dispatcher dispatcher)
    {
        _folder = folder; _file = Path.Combine(folder, "shelf.dat"); _pictures = Path.Combine(folder, "shelf");
        _dispatcher = dispatcher;
        _save = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _save.Tick += (_, _) => { _save.Stop(); Save(); };
        Load();
        // The history goes when BrimDeck exits; after an exit that did not clear it (a crash, a forced end) it goes now.
        if (!_unreadable && Shelf.History(Data) is { Count: > 0 } left) Remove(left, raise: false);
    }

    // Starts or stops recording as the settings ask.
    public void Apply(DeckSettings settings)
    {
        _settings = () => settings;
        bool record = settings.ClipboardPage && settings.ClipboardRecord;
        if (record && _watch is null)
        {
            try { _watch = new ClipboardWatch(Copied, name => ClipboardExclusions.Excludes(_settings().ClipboardExcluded, name)); }
            catch (System.ComponentModel.Win32Exception) { _watch = null; }
        }
        else if (!record && _watch is not null) { _watch.Dispose(); _watch = null; }
    }

    private void Copied(ClipCopy copy)
    {
        var now = DateTimeOffset.Now;
        ClipEntry entry;
        if (copy.Png is { } png)
        {
            entry = new ClipEntry { Kind = ClipKind.Image, PixelWidth = copy.PixelWidth, PixelHeight = copy.PixelHeight, Copied = now, Source = copy.Source };
            if (!WritePicture(entry.Id, png)) return;
            Data.Clips.Insert(0, entry);
        }
        else entry = Shelf.Add(Data, new ClipEntry { Kind = Shelf.Classify(copy.Text), Text = copy.Text, Html = copy.Html, Rtf = copy.Rtf, Copied = now, Source = copy.Source });
        // The categories whose rules it meets take it, the same text copied again too.
        ShelfRules.Apply(Data, entry);
        Changed?.Invoke();
        // The history goes when BrimDeck exits and when it next starts, so a copy that is only history has nothing to keep on disk:
        // saving it serialized and encrypted the whole list, on this thread, after every copy. A picture is saved all the same: its
        // file is deleted with its entry, and a start after a forced end finds that entry only in the saved list.
        if (entry.Kind == ClipKind.Image || Data.Kept(entry)) QueueSave();
    }

    public void Raise() => Changed?.Invoke();
    public void Edited() { Changed?.Invoke(); QueueSave(); }

    public void Remove(IEnumerable<ClipEntry> entries, bool raise = true)
    {
        foreach (var entry in entries.ToList())
        {
            Data.Clips.Remove(entry);
            if (entry.Kind == ClipKind.Image) { DeletePicture(entry.Id); _thumbs.Remove(entry.Id); }
        }
        if (raise) Changed?.Invoke();
        QueueSave();
    }

    public void AddFiles(IEnumerable<string> paths, Guid? category)
    {
        var added = Shelf.AddFiles(Data, paths, category, DateTimeOffset.Now);
        foreach (var file in added) ShelfRules.Apply(Data, file);
        Edited();
    }
    public void RemoveFiles(IEnumerable<TransferFile> files)
    {
        foreach (var file in files.ToList()) Data.Files.Remove(file);
        Edited();
    }

    // ---- Pictures ----
    private readonly Dictionary<Guid, BitmapSource?> _thumbs = [];
    private string PicturePath(Guid id) => Path.Combine(_pictures, id.ToString("N") + ".dat");
    private bool WritePicture(Guid id, byte[] png)
    {
        if (_unreadable) return false;
        try
        {
            Directory.CreateDirectory(_pictures);
            File.WriteAllBytes(PicturePath(id), ProtectedData.Protect(png, null, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException) { return false; }
    }
    private void DeletePicture(Guid id)
    {
        try { File.Delete(PicturePath(id)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    public byte[]? PictureBytes(Guid id)
    {
        try { return File.Exists(PicturePath(id)) ? ProtectedData.Unprotect(File.ReadAllBytes(PicturePath(id)), null, DataProtectionScope.CurrentUser) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
    }
    // The picture decoded at most this many pixels wide; the row's thumbnails are kept, the details' full picture is not.
    public BitmapSource? Picture(Guid id, int decodeWidth = 0)
    {
        if (decodeWidth > 0 && _thumbs.TryGetValue(id, out var known)) return known;
        BitmapSource? image = null;
        if (PictureBytes(id) is { } bytes)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = new MemoryStream(bytes);
                if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
                bitmap.EndInit(); bitmap.Freeze(); image = bitmap;
            }
            catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException) { }
        }
        if (decodeWidth > 0) { if (_thumbs.Count > 300) _thumbs.Clear(); _thumbs[id] = image; }
        return image;
    }

    // The page is switched off: the thumbnails and the files' icons it showed are let go (App.ReleaseUnused); the entries stay, as
    // they are what is saved. False when there was nothing to let go.
    public bool Rest()
    {
        bool held = _thumbs.Count > 0 | ShelfFiles.Rest();
        _thumbs.Clear();
        return held;
    }

    // ---- Saving ----
    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            Data = ShelfData.Deserialize(ProtectedData.Unprotect(File.ReadAllBytes(_file), null, DataProtectionScope.CurrentUser));
        }
        // A file that cannot be read (another user's, damaged) is left as it is and nothing is written over it.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException)
        { _unreadable = true; Data = new(); }
    }
    public void QueueSave() { _save.Stop(); _save.Start(); }
    public void Save()
    {
        _save.Stop();
        if (_unreadable) return;
        try
        {
            Directory.CreateDirectory(_folder);
            var temp = _file + ".tmp";
            File.WriteAllBytes(temp, ProtectedData.Protect(Data.Serialize(), null, DataProtectionScope.CurrentUser));
            File.Move(temp, _file, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException) { }
    }
    // Leaving BrimDeck: the history goes; what is kept (ShelfData.Kept) stays. The pictures written for dragging go too.
    public void Exit()
    {
        Remove(Shelf.History(Data), raise: false);
        Save();
        try { if (Directory.Exists(DragFolder)) Directory.Delete(DragFolder, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // ---- Dragging a picture out ----
    // Browsers and Electron applications (Claude, chat clients) take a dropped picture only as a file, not as picture data, so a
    // picture dragged out is also written, unencrypted, to a file of its own under the temporary folder. Files older than ten
    // minutes are cleared at each drag and the folder at exit.
    private static string DragFolder => Path.Combine(Path.GetTempPath(), "BrimDeck", "Drag");
    public string? PictureFile(ClipEntry entry)
    {
        if (entry.Kind != ClipKind.Image || PictureBytes(entry.Id) is not { } png) return null;
        try
        {
            Directory.CreateDirectory(DragFolder);
            foreach (var old in Directory.EnumerateFiles(DragFolder).Where(file => DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(10)).ToList())
                try { File.Delete(old); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            string path = Path.Combine(DragFolder, Loc.T("图片 ", "Picture ") + entry.Copied.ToLocalTime().ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".png");
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- Putting on the clipboard ----
    public DataObject ClipData(ClipEntry entry, bool plain = false)
    {
        var data = new DataObject();
        if (entry.Kind == ClipKind.Image)
        {
            if (PictureBytes(entry.Id) is { } png)
            {
                var image = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                data.SetImage(image);
                data.SetData("PNG", new MemoryStream(png));
            }
        }
        else
        {
            data.SetText(entry.Text, TextDataFormat.UnicodeText);
            if (!plain && entry.Html.Length > 0) data.SetData(DataFormats.Html, entry.Html);
            if (!plain && entry.Rtf.Length > 0) data.SetData(DataFormats.Rtf, entry.Rtf);
        }
        return data;
    }
    public static DataObject TextData(string text) { var data = new DataObject(); data.SetText(text, TextDataFormat.UnicodeText); return data; }
    // Files to paste or drop. Preferred DropEffect tells the Explorer to copy them (1), or, for a drag set to move, to move them (2)
    // even to another drive.
    public static DataObject FileData(IEnumerable<string> paths, bool move = false)
    {
        var list = new StringCollection(); list.AddRange(paths.ToArray());
        var data = new DataObject(); data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(move ? 2 : 1)));
        return data;
    }

    // Puts data on the clipboard, without recording it as a new copy. The clipboard may be held by another application for a moment.
    public bool Put(DataObject data)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                _watch?.IgnoreCurrent();
                return true;
            }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        return false;
    }

    // The window to paste into after the panel took the focus for typing (MainWindow.ShelfTyping).
    public IntPtr TypingTarget { get; set; }

    // Puts data on the clipboard and pastes it into the window the user was typing in. done: true once Ctrl+V was sent, false when
    // it could only be copied (no other window to paste into, or keys still held after a second).
    public void Paste(DataObject data, Action<bool> done)
    {
        if (!Put(data)) { done(false); return; }
        var target = PasteInput.IsOther(PasteInput.Foreground) ? PasteInput.Foreground : TypingTarget;
        if (!PasteInput.IsOther(target)) { done(false); return; }
        long started = Environment.TickCount64;
        bool asked = false;
        var wait = new DispatcherTimer(DispatcherPriority.Input, _dispatcher) { Interval = TimeSpan.FromMilliseconds(20) };
        wait.Tick += (_, _) =>
        {
            bool late = Environment.TickCount64 - started > 1000;
            if (PasteInput.Foreground != target)
            {
                if (!asked) { asked = true; PasteInput.Activate(target); }
                if (late) { wait.Stop(); done(false); }
                return;
            }
            if (PasteInput.ModifiersDown) { if (late) { wait.Stop(); done(false); } return; }
            wait.Stop();
            done(PasteInput.SendPaste());
        };
        wait.Start();
    }

    public void Dispose() { _watch?.Dispose(); _watch = null; }
}

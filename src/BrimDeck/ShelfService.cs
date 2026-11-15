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

// The clipboard history and the file transfer, shared by every panel. Kept in the data folder beside settings.json: shelf.dat
// holds the entries as JSON, shelf\<id>.dat each picture as PNG and shelf\<id>.html and .rtf a text entry's rich forms, which
// are read only to paste. Only the list is held in memory; pictures are read to be shown. Earlier versions encrypted both for the current Windows user
// (DPAPI); such files are still read, and the list is written plain at its next save. Encrypting the whole list took from a
// tenth of a second to seconds once it held thousands of entries, on the panel's thread, at every save.
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
    // The category each column shows, null for "全部". It is one for every panel, as the entries are: chosen on one display, it
    // is the one shown on the others, so no display shows an empty category while another shows everything.
    public Guid? ClipTab { get; private set; }
    public Guid? FileTab { get; private set; }
    public event Action? TabChanged;
    public void ShowTab(ShelfSide side, Guid? category)
    {
        if ((side == ShelfSide.Clipboard ? ClipTab : FileTab) == category) return;
        if (side == ShelfSide.Clipboard) ClipTab = category; else FileTab = category;
        TabChanged?.Invoke();
    }
    private Func<DeckSettings> _settings = () => new();

    public ShelfService(string folder, Dispatcher dispatcher)
    {
        _folder = folder; _file = Path.Combine(folder, "shelf.dat"); _pictures = Path.Combine(folder, "shelf");
        _dispatcher = dispatcher;
        _save = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _save.Tick += (_, _) => { _save.Stop(); Save(); };
        // The history kept past an exit is looked over every ten minutes for what has grown older than its length.
        _expire = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMinutes(10) };
        _expire.Tick += (_, _) => Expire(raise: true);
        Load();
    }
    private readonly DispatcherTimer _expire;
    private bool _applied;

    // Starts or stops recording as the settings ask, and keeps the history or lets it go with BrimDeck as they ask.
    public void Apply(DeckSettings settings)
    {
        bool first = !_applied, kept = !first && _settings().ClipboardKeepHistory;
        _settings = () => settings; _applied = true;
        // At the first settings: the history goes when BrimDeck exits, so after an exit that did not clear it (a crash, a forced
        // end) it goes now, unless it is kept; then only what has grown too old goes.
        if (first && !settings.ClipboardKeepHistory && !_unreadable && Shelf.History(Data) is { Count: > 0 } left) Remove(left, raise: false);
        if (settings.ClipboardKeepHistory) { Expire(raise: !first); _expire.Start(); } else _expire.Stop();
        // Kept from now on: the history copied before, not saved until now (Copied), is saved.
        if (settings.ClipboardKeepHistory && !kept && !first) QueueSave();
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
        else
        {
            entry = Shelf.Add(Data, new ClipEntry { Kind = Shelf.Classify(copy.Text), Text = copy.Text, Copied = now, Source = copy.Source });
            // The same text copied again keeps the rich forms it had, unless this copy brings its own.
            if (copy.Html.Length > 0 || copy.Rtf.Length > 0) WriteRich(entry.Id, copy.Html, copy.Rtf);
        }
        // The categories whose rules it meets take it, the same text copied again too.
        ShelfRules.Apply(Data, entry);
        Changed?.Invoke();
        // Unless the history is kept past an exit, it goes when BrimDeck exits and when it next starts, so a copy that is only history
        // has nothing to keep on disk: saving it turned the whole list into bytes, on this thread, after every copy. A picture
        // is saved all the same: its file is deleted with its entry, and a start after a forced end finds that entry only in the
        // saved list.
        if (_settings().ClipboardKeepHistory || entry.Kind == ClipKind.Image || Data.Kept(entry)) QueueSave();
    }

    // The history kept past an exit that has grown older than its length goes (Shelf.Expired).
    private void Expire(bool raise)
    {
        var settings = _settings();
        if (!settings.ClipboardKeepHistory || _unreadable) return;
        if (Shelf.Expired(Data, settings.ClipboardKeepFor, DateTimeOffset.Now) is { Count: > 0 } gone) Remove(gone, raise);
    }

    public void Raise() => Changed?.Invoke();
    public void Edited() { Changed?.Invoke(); QueueSave(); }

    public void Remove(IEnumerable<ClipEntry> entries, bool raise = true)
    {
        foreach (var entry in entries.ToList())
        {
            Data.Clips.Remove(entry);
            if (entry.Kind == ClipKind.Image) { DeletePicture(entry.Id); _thumbs.Remove(entry.Id); }
            else DropRich(entry.Id);
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
    // The thumbnails read so far, each with the width it was decoded at: a wider one is read when a wider one is asked for.
    private readonly Dictionary<Guid, (BitmapSource? Image, int Width)> _thumbs = [];
    private readonly Dictionary<Guid, (int Width, List<Action<BitmapSource?>> Waiting)> _decoding = [];
    private static int _reading;
    // Thumbnails being decoded; for the pictures of the README, which wait for them.
    public static int Reading => Volatile.Read(ref _reading);
    private string PicturePath(Guid id) => Path.Combine(_pictures, id.ToString("N") + ".dat");
    private bool WritePicture(Guid id, byte[] png)
    {
        if (_unreadable) return false;
        try
        {
            Directory.CreateDirectory(_pictures);
            File.WriteAllBytes(PicturePath(id), png);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    private void DeletePicture(Guid id)
    {
        try { File.Delete(PicturePath(id)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    public byte[]? PictureBytes(Guid id)
    {
        try
        {
            if (!File.Exists(PicturePath(id))) return null;
            var bytes = File.ReadAllBytes(PicturePath(id));
            // A PNG starts with its signature; a picture saved encrypted by an earlier version does not.
            return bytes.AsSpan().StartsWith(PngSignature) ? bytes : ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
    }
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    // The picture decoded at most this many pixels wide; the row's thumbnails are kept, the details' full picture is not.
    public BitmapSource? Picture(Guid id, int decodeWidth = 0)
    {
        if (decodeWidth > 0 && _thumbs.TryGetValue(id, out var known) && known.Width >= decodeWidth) return known.Image;
        var image = Decode(id, decodeWidth);
        if (decodeWidth > 0) Keep(id, image, decodeWidth);
        return image;
    }
    private void Keep(Guid id, BitmapSource? image, int width)
    {
        if (_thumbs.Count > 300) _thumbs.Clear();
        _thumbs[id] = (image, width);
    }
    // An entry's thumbnail for a list. A picture the size of a screenshot takes some 60 ms to decode (measured: 2560 x 1440,
    // whatever the width asked), which held up the list each time such an entry scrolled into view. So it is decoded on a pool
    // thread: this gives the thumbnail when it has been read at that width or wider, else the narrower one read before, or
    // nothing, and `arrived` is called on this thread with the new one when it is there (null when the picture cannot be read).
    public BitmapSource? Thumb(Guid id, int width, Action<BitmapSource?> arrived)
    {
        _thumbs.TryGetValue(id, out var known);
        if (known.Width >= width) return known.Image;
        if (_decoding.TryGetValue(id, out var under) && under.Width >= width) { under.Waiting.Add(arrived); return known.Image; }
        var waiting = new List<Action<BitmapSource?>> { arrived };
        _decoding[id] = (width, waiting);
        var dispatcher = Dispatcher.CurrentDispatcher;
        Interlocked.Increment(ref _reading);
        Task.Run(() =>
        {
            var image = Decode(id, width);
            dispatcher.BeginInvoke(() =>
            {
                Interlocked.Decrement(ref _reading);
                if (_decoding.TryGetValue(id, out var now) && now.Waiting == waiting) _decoding.Remove(id);
                Keep(id, image, width);
                foreach (var each in waiting) each(image);
            });
        });
        return known.Image;
    }
    private BitmapSource? Decode(Guid id, int decodeWidth)
    {
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
        return image;
    }

    // ---- Rich forms ----
    // A text entry's HTML and RTF, beside its pictures' files: shelf\<id>.html and shelf\<id>.rtf, each only when the copy had it.
    private string RichPath(Guid id, string format) => Path.Combine(_pictures, id.ToString("N") + "." + format);
    private void WriteRich(Guid id, string html, string rtf)
    {
        if (_unreadable) return;
        try
        {
            Directory.CreateDirectory(_pictures);
            foreach (var (format, text) in new[] { ("html", html), ("rtf", rtf) })
                if (text.Length > 0) File.WriteAllText(RichPath(id, format), text);
                else File.Delete(RichPath(id, format));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private string ReadRich(Guid id, string format)
    {
        try { return File.Exists(RichPath(id, format)) ? File.ReadAllText(RichPath(id, format)) : ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }
    // The text was changed by hand: what is pasted is the text alone.
    public void DropRich(Guid id)
    {
        foreach (var format in new[] { "html", "rtf" })
            try { File.Delete(RichPath(id, format)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
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
            var bytes = File.ReadAllBytes(_file);
            // The JSON starts with "{"; a list saved encrypted by an earlier version does not.
            Data = ShelfData.Deserialize(bytes is [(byte)'{', ..] ? bytes : ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            // A history saved with the rich forms in it: they move to their own files, and the list is saved without them.
            if (Shelf.TakeRich(Data) is { Count: > 0 } rich)
            {
                foreach (var (clip, html, rtf) in rich) WriteRich(clip.Id, html, rtf);
                QueueSave();
            }
            RemoveStrays();
        }
        // A file that cannot be read (another user's, damaged) is left as it is and nothing is written over it.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException)
        { _unreadable = true; Data = new(); }
    }
    // A picture or rich form is written as it is copied, the list only a moment later; files whose entry is not in the list (BrimDeck
    // ended in between) are deleted when it starts.
    private void RemoveStrays()
    {
        try
        {
            if (!Directory.Exists(_pictures)) return;
            var ids = Data.Clips.Select(clip => clip.Id).ToHashSet();
            foreach (var file in Directory.EnumerateFiles(_pictures).ToList())
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) && !ids.Contains(id))
                    try { File.Delete(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    public void QueueSave() { _save.Stop(); _save.Start(); }
    // The list is turned into bytes here, on the thread that changes it, so what is written is whole; the writing itself is done
    // on a worker thread, one write after another, so a later save never lands before an earlier one. wait: written before this
    // returns (leaving BrimDeck).
    private Task _writing = Task.CompletedTask;
    public void Save(bool wait = false)
    {
        _save.Stop();
        if (_unreadable) return;
        var bytes = Data.Serialize();
        _writing = _writing.ContinueWith(_ => Write(bytes), TaskScheduler.Default);
        if (wait) _writing.Wait();
    }
    private void Write(byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var temp = _file + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _file, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    // Leaving BrimDeck: the history goes, unless it is kept past an exit (then only what has grown too old); what is kept
    // (ShelfData.Kept) stays. The pictures written for dragging go too.
    public void Exit()
    {
        _expire.Stop();
        if (_settings().ClipboardKeepHistory) Expire(raise: false);
        else Remove(Shelf.History(Data), raise: false);
        Save(wait: true);
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
            if (!plain && ReadRich(entry.Id, "html") is { Length: > 0 } html) data.SetData(DataFormats.Html, html);
            if (!plain && ReadRich(entry.Id, "rtf") is { Length: > 0 } rtf) data.SetData(DataFormats.Rtf, rtf);
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

    public void Dispose() { _expire.Stop(); _watch?.Dispose(); _watch = null; }
}

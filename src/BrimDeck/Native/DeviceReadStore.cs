using System.IO;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck.Native;

// The kept reads (DeviceReadBook) on disk, in the app's data folder (device-reads.json), and the guard around trying a read
// a device has not answered before: a mark (device-probe.json) is written through to the disk before the read is sent and
// emptied when it returns. A mark left at the next start means the app ended while the device was being asked (the read hung
// it, or the machine froze): that read is never sent to that device again (DeviceReadBook.Harmed). A clean exit (Finish)
// empties the mark and lets no first try start after it, so quitting the app, or an update restarting it, during a first try
// harms nothing. Reads already kept are not marked, so a device in use costs no disk write per reading.
internal static class DeviceReadStore
{
    private const string BookFile = "device-reads.json", ProbeFile = "device-probe.json";
    private static readonly object Gate = new();
    private static string? _folder;
    private static DeviceReadBook _book = new();
    private static bool _finished, _marked;

    public static DeviceReadBook Book { get { lock (Gate) return _book; } }

    // Once, before the devices are read.
    public static void Start(string folder)
    {
        lock (Gate)
        {
            _folder = folder;
            _book = DeviceReadBook.Parse(Read(BookFile));
            if (Read(ProbeFile) is { Length: > 0 } left)
            {
                try
                {
                    var mark = JsonSerializer.Deserialize<Mark>(left);
                    if (mark is { Device.Length: > 0, Way.Length: > 0 }) _book.Harmed(mark.Device, mark.Way);
                }
                catch (JsonException) { }
                _marked = true; Clear();
            }
            if (_book.Dirty) SaveLocked();
        }
    }

    // Writes the book when it changed. Called after a pass, on the worker, and after the user turns a device's reading on.
    public static void Save() { lock (Gate) if (_book.Dirty) SaveLocked(); }

    // A clean exit: no first try is under way any longer, and none starts.
    public static void Finish()
    {
        lock (Gate)
        {
            _finished = true;
            Clear();
            if (_book.Dirty) SaveLocked();
        }
    }

    private sealed record Mark(string Device, string Way);

    // Runs a first try of a read on a device with the mark on disk around it. After Finish the read is not sent (default).
    public static T? Probe<T>(string device, string way, Func<T> ask)
    {
        lock (Gate)
        {
            if (_finished) return default;
            WriteThrough(ProbeFile, JsonSerializer.Serialize(new Mark(device, way)));
            _marked = true;
        }
        try { return ask(); }
        finally { lock (Gate) Clear(); }
    }

    private static void SaveLocked()
    {
        if (_folder is null) return;
        string text = _book.Serialize();
        try
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, BookFile);
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // The mark is emptied rather than deleted, written through like the mark itself: a delete still in the cache when the
    // machine stops would bring back a mark the read had already cleared (SensorGuard does the same).
    private static void Clear() { if (!_marked) return; WriteThrough(ProbeFile, ""); _marked = false; }

    private static string? Read(string name)
    {
        if (_folder is null) return null;
        try { var path = Path.Combine(_folder, name); return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
    // Written in place and flushed to the disk (data and the file's own record) before it returns: a mark written to a
    // temporary file and renamed would leave the rename in the cache.
    private static void WriteThrough(string name, string text)
    {
        if (_folder is null) return;
        try
        {
            Directory.CreateDirectory(_folder);
            using var stream = new FileStream(Path.Combine(_folder, name), FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            stream.Write(Encoding.UTF8.GetBytes(text));
            stream.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

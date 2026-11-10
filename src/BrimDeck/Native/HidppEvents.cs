using BrimDeck.Core;

namespace BrimDeck.Native;

// Keeps the HID++ collections of the Logitech receivers and cabled devices open while the battery readers run, and hands on
// every report a receiver or device sends unasked: a battery event (0x1004 or 0x1000 event 0, sent when the level or the
// charging changes: a cable put in shows within a second) and a link made or lost (0x41). This is how Solaar, the Linux driver
// and OpenLogi follow Logitech devices; the readings themselves stay with the reader (HidBatteries), which takes the reports
// on its own thread. Handles of its own: a receiver sends every report to every handle open on it, so the reader's requests
// and this listening do not meet. A receiver whose wireless notices are off has them turned on (Hidpp.NotificationFlags), as
// OpenLogi and Solaar do, its other flags left as they were; nothing else is ever written.
internal sealed class HidppEvents(Action<string, byte[]> received) : IDisposable
{
    // One receiver or cabled device: its group key (HidBatteries.GroupKey), its long collection and its short one, if any.
    public sealed record Group(string Key, HidInfo Long, HidInfo? Short, bool Receiver);

    private readonly object _gate = new();
    private readonly AutoResetEvent _changed = new(false);
    private IReadOnlyList<Group> _wanted = [];
    private bool _dirty, _stopped = true;
    private Thread? _thread;

    // The groups to listen to, from the reader's latest pass; the same set again changes nothing.
    public void Watch(IReadOnlyList<Group> groups)
    {
        lock (_gate)
        {
            bool same = groups.Count == _wanted.Count && groups.Zip(_wanted).All(pair => pair.First.Long.Path == pair.Second.Long.Path && pair.First.Short?.Path == pair.Second.Short?.Path);
            _stopped = false;
            if (!same) { _wanted = groups; _dirty = true; }
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "BrimDeck HID++ events" };
                _thread.Start();
            }
        }
        _changed.Set();
    }

    public void Stop()
    {
        lock (_gate) { _stopped = true; _wanted = []; _dirty = true; }
        _changed.Set();
    }

    public void Dispose() => Stop();

    private void Run()
    {
        var open = new List<(HidDevice Device, string Group)>();
        var devices = new List<HidDevice>();
        try
        {
            while (true)
            {
                IReadOnlyList<Group>? reopen = null;
                lock (_gate)
                {
                    if (_stopped) { _thread = null; return; }
                    if (_dirty) { _dirty = false; reopen = _wanted; }
                }
                if (reopen is not null)
                {
                    Close(open, devices);
                    foreach (var each in reopen) Open(each, open);
                    devices.AddRange(open.Select(each => each.Device));
                }
                if (devices.Count == 0) { _changed.WaitOne(1000); continue; }
                HidDevice? from;
                byte[]? data;
                // A receiver pulled out fails its read: it is dropped until the reader's next pass lists the groups again.
                try { from = Hid.ReadAny(devices, 500, out data); }
                catch { Close(open, devices); lock (_gate) _dirty = true; _changed.WaitOne(1000); continue; }
                if (from is null || data is null) continue;
                string group = open.First(each => each.Device == from).Group;
                try { received(group, data); } catch { }
            }
        }
        finally { Close(open, devices); }
    }

    private static void Open(Group group, List<(HidDevice, string)> open)
    {
        HidDevice longDevice;
        try { longDevice = new HidDevice(group.Long); }
        catch { return; }
        open.Add((longDevice, group.Key));
        HidDevice? shortDevice = null;
        if (group.Short is not null) try { shortDevice = new HidDevice(group.Short); open.Add((shortDevice, group.Key)); } catch { }
        if (group.Receiver && shortDevice is not null) EnableNotices([longDevice, shortDevice], shortDevice);
    }

    // Register 0x00 read, and written back with the wireless bit only if it was off.
    private static void EnableNotices(IList<HidDevice> devices, HidDevice shortDevice)
    {
        try
        {
            var flags = Hid.Transact<byte[]>(devices, () => shortDevice.Write(Hidpp.RegisterRequest(0xFF, Hidpp.NotificationFlags)), Hidpp.Flags, 500);
            if (flags is null || (flags[1] & Hidpp.WirelessNotifications) != 0) return;
            Hid.Transact<object>(devices, () => shortDevice.Write(Hidpp.RegisterWrite(0xFF, Hidpp.NotificationFlags, flags[0], (byte)(flags[1] | Hidpp.WirelessNotifications), flags[2])),
                report => report.Length >= 4 && report[1] == 0xFF && report[2] is 0x80 or 0x8F ? report : null, 500);
        }
        catch { }
    }

    private static void Close(List<(HidDevice Device, string Group)> open, List<HidDevice> devices)
    {
        foreach (var (device, _) in open) { try { device.Dispose(); } catch { } }
        open.Clear(); devices.Clear();
    }
}

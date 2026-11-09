using BrimDeck.Core;

namespace BrimDeck.Native;

// Keeps the collections of the receivers that send their battery unasked open (Pushes: Attack Shark, DELUX, Inphic ZHEN,
// Rapoo, AULA G9, the Steam Controller puck, JBL Quantum 910, PULSE 3D, Cloud Flight 2) and reads every report they send, as
// HaloBattery's jbl.py listens to its headset: some of them speak only when something changes, and a report sent while the
// collection is closed is lost. Each report is applied to the device's last state (Pushes.Apply), and a state that changed
// is handed on as the device's reading, which then stands until the device says otherwise: a device that goes quiet (a Rapoo
// mouse at rest) keeps it. Nothing is ever written. Handles of its own, on a thread of its own at below normal priority; the
// battery worker (HidBatteries) hands it the collections after each pass and takes the readings on its own thread.
internal sealed class PushEvents(Action<string, DeviceBattery> received) : IDisposable
{
    public sealed record Collection(HidInfo Info, Pushes.Kind Kind);

    private readonly object _gate = new();
    private readonly AutoResetEvent _changed = new(false);
    private IReadOnlyList<Collection> _wanted = [];
    private bool _dirty, _stopped = true;
    private Thread? _thread;
    // The listener's own: each collection's last state and the name its reports gave, kept while the collection is wanted
    // (a collection reopened after a failed read goes on from where it was).
    private readonly Dictionary<string, (BatteryRead? State, string? Name)> _states = new(StringComparer.OrdinalIgnoreCase);

    // The collections to listen to, from the worker's latest pass; the same set again changes nothing.
    public void Watch(IReadOnlyList<Collection> collections)
    {
        lock (_gate)
        {
            bool same = collections.Count == _wanted.Count && collections.Zip(_wanted).All(pair => pair.First.Info.Path == pair.Second.Info.Path);
            if (collections.Count == 0 && _thread is null) return;
            _stopped = false;
            if (!same) { _wanted = collections; _dirty = true; }
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "BrimDeck pushed batteries" };
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
        var open = new List<(HidDevice Device, Collection Collection)>();
        var devices = new List<HidDevice>();
        try
        {
            while (true)
            {
                IReadOnlyList<Collection>? reopen = null;
                lock (_gate)
                {
                    if (_stopped) { _thread = null; _states.Clear(); return; }
                    if (_dirty) { _dirty = false; reopen = _wanted; }
                }
                if (reopen is not null)
                {
                    Close(open, devices);
                    foreach (var path in _states.Keys.Where(path => reopen.All(each => !string.Equals(each.Info.Path, path, StringComparison.OrdinalIgnoreCase))).ToList()) _states.Remove(path);
                    foreach (var each in reopen) try { open.Add((new HidDevice(each.Info), each)); } catch { }
                    devices.AddRange(open.Select(each => each.Device));
                }
                if (devices.Count == 0) { _changed.WaitOne(1000); continue; }
                HidDevice? from;
                byte[]? data;
                // A receiver pulled out fails its read: every collection is closed until the worker's next pass lists them again.
                try { from = Hid.ReadAny(devices, 500, out data); }
                catch { Close(open, devices); lock (_gate) _dirty = true; _changed.WaitOne(1000); continue; }
                if (from is null || data is null) continue;
                var collection = open.First(each => each.Device == from).Collection;
                _states.TryGetValue(collection.Info.Path, out var last);
                if (Pushes.Apply(collection.Kind, last.State, data) is not { } state) continue;
                string? name = Pushes.Names(collection.Kind) ? Pushes.Name(collection.Kind, data) : last.Name;
                if (state == last.State && name == last.Name) continue;
                _states[collection.Info.Path] = (state, name);
                try { received(collection.Info.Path, HidBatteries.Pushed(collection.Info, collection.Kind, state, name)); } catch { }
            }
        }
        finally { Close(open, devices); }
    }

    private static void Close(List<(HidDevice Device, Collection Collection)> open, List<HidDevice> devices)
    {
        foreach (var (device, _) in open) { try { device.Dispose(); } catch { } }
        open.Clear(); devices.Clear();
    }
}

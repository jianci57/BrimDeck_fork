using System.IO;
using System.Windows.Threading;
using BrimDeck.Core;
using Windows.Devices.Enumeration;

namespace BrimDeck.Native;

// The batteries that only the devices' own protocols tell, over HID: Logitech receivers and cables (HID++), Razer mice and
// keyboards, the 2.4 GHz mice and keyboards of ATK, VXE, VGN, Pulsar, MCHOSE, Endgame Gear, VAXEE, Inphic, AJAZZ, WLmouse,
// G-Wolves, LAMZU, Ninjutso, LINGBAO, NZXT, CHERRY and HECATE (those whose vendor and product are known), ASUS ROG mice,
// keyboards and headsets (the ROG Omni receiver's paired devices too), SteelSeries mice, Rapoo's first generation, SteelSeries,
// HyperX and MCHOSE headsets, Corsair's receivers (CorsairReader), the receivers that send their battery unasked (Attack Shark,
// DELUX, Inphic ZHEN, Rapoo, AULA G9, the Steam Controller puck, JBL Quantum 910, PULSE 3D, Cloud Flight 2: only listened to,
// PushEvents), PlayStation controllers, and any device that carries the standard HID battery usages. The protocols themselves
// are in Core (DeviceProtocols.cs); this sends them. Every request only reads: nothing is set on any device (a Logitech
// receiver's wireless notices aside, HidppEvents).
// One worker thread at below normal priority does the I/O, so a slow device never holds the panel, while the page or the
// collapsed reading shows (DeviceDemand.Watch), and not at all otherwise, when it lets every handle go and ends (the next start
// makes a new one). Each kind is read as often as DevicePolling has it: the brands that only answer when asked every 15
// seconds, the PlayStation controllers (only listened to) every 5, and the Logitech devices every 30, their own events
// (HidppEvents) bringing a change in between at once. Two threads of their own take what would hold that pass: the receivers
// that send their battery unasked are listened to all the time (PushEvents), each reading standing until the device sends
// another, and the Corsair receivers, whose answer takes seconds, are read once a minute (CorsairReader); both hand their
// readings back to the worker, which lists them at once. A device or a cable plugged in or pulled out has every device read
// again within a moment, and twice more a few seconds later (a watcher over the HID collections); so do the computer waking
// and the user coming back (ReadNow). On Bluetooth, only the collections a reader here speaks to are opened (Spoken).
internal sealed class HidBatteries(Dispatcher dispatcher, Action<DeviceSource, IReadOnlyList<DeviceBattery>> report) : IDeviceSource
{
    private static readonly DeviceSource[] Sources = [DeviceSource.Logitech, DeviceSource.Razer, DeviceSource.SteelSeries, DeviceSource.HyperX, DeviceSource.Sony, DeviceSource.Hid];
    private readonly AutoResetEvent _wake = new(false);
    // Guards _thread between Start and the worker ending itself.
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile DeviceDemand _demand;
    private volatile bool _disposed;
    private DeviceWatcher? _arrivals;
    private int _generation;
    // The worker's own: each collection's attributes by path, and what each Logitech device slot was found to be.
    private readonly Dictionary<string, HidInfo> _infos = [];
    private readonly LogitechReader _logitech = new();
    private readonly Dictionary<string, (DeviceBattery Device, int Missed)> _lastRead = [];
    // Set once a pass over the collections has finished since the start.
    private volatile bool _listed;
    public bool Listed => _listed;

    // Started, the worker reads at once whatever is due (DevicePolling).
    public void Start(DeviceDemand demand)
    {
        var was = _demand;
        lock (_gate)
        {
            _demand = demand;
            _events ??= new HidppEvents(Received);
            _pushes ??= new PushEvents(Side);
            _corsair ??= new CorsairReader(Side);
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "BrimDeck HID batteries" };
                _thread.Start();
            }
        }
        if (demand == DeviceDemand.Watch) WatchArrivals(); else StopArrivals();
        if (demand != was) Wake();
    }

    public void Stop()
    {
        _demand = DeviceDemand.None;
        // Started again before the worker has noticed, it lists afresh rather than from where it was.
        _listed = false; _restart = true;
        StopArrivals(); StopListeners();
        Wake();
        foreach (var source in Sources) report(source, []);
    }

    // The page opened: the worker reads what is due. ReadNow: every device at once (the computer woke, the user came back).
    public void Refresh() { if (_demand == DeviceDemand.Watch) Wake(); }
    public void ReadNow() { if (_demand == DeviceDemand.Watch) { _forced = true; _corsair?.ReadNow(); Wake(); } }

    public void Dispose()
    {
        _disposed = true; _demand = DeviceDemand.None;
        StopArrivals(); StopListeners();
        Wake();
    }
    private void StopListeners() { _events?.Stop(); _pushes?.Stop(); _corsair?.Stop(); }

    // The worker's event lives as long as the process: set from the UI thread and the watcher's threads, it is never disposed
    // under them.
    private void Wake() => _wake.Set();
    private const long Never = -1;
    // When each kind of reading was last taken (Environment.TickCount64), the worker's own: the Logitech devices, those only
    // asked (every other brand and the standard usages) and those only listened to (the PlayStation controllers).
    private long _logitechAt = Never, _askAt = Never, _listenAt = Never;
    private volatile bool _arrived, _removed, _forced;
    // The worker's own: whether it has made a full pass since it started (_listed waits for the UI thread to take the result).
    private bool _scanned;
    private volatile bool _restart;
    // The worker's own: each source's current list, as last reported.
    private readonly Dictionary<DeviceSource, List<DeviceBattery>> _current = [];

    // ----- Logitech events (HidppEvents) -----

    private HidppEvents? _events;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Group, byte[] Report)> _pending = new();
    // Each Logitech slot's battery feature index, from the worker's reads, for the listener's thread to tell a battery event
    // from the rest.
    private volatile Dictionary<(string Group, byte Index), (byte Feature, int Battery)> _indices = [];

    // On the listener's thread: a battery event or a link notice is queued for the worker, which is woken; every other report
    // (another program's reply, another feature's event) is dropped here.
    private void Received(string group, byte[] report)
    {
        if (Hidpp.Connection(report) is null
            && !(Hidpp.Event(report) is { Event: 0 } e && _indices.TryGetValue((group, e.Device), out var battery) && battery.Feature == e.Feature)) return;
        _pending.Enqueue((group, report));
        Wake();
    }

    // The worker applies what came: a battery event is the device's reading at once; a link lost leaves its last reading,
    // asleep; a link made has the Logitech devices read now. True when a reading changed.
    private bool ApplyEvents(ref bool linkUp)
    {
        bool changed = false;
        while (_pending.TryDequeue(out var item))
        {
            var (group, report) = item;
            DeviceBattery? device = null;
            if (Hidpp.Connection(report) is { } connection)
            {
                if (connection.Linked) { linkUp = true; continue; }
                device = _logitech.LinkLost(group, connection.Device);
            }
            else if (Hidpp.Event(report) is { } e && _indices.TryGetValue((group, e.Device), out var battery) && battery.Feature == e.Feature)
                device = _logitech.Event(group, e.Device, battery.Battery, e.Parameters);
            if (device is null) continue;
            if (!_current.TryGetValue(DeviceSource.Logitech, out var list)) _current[DeviceSource.Logitech] = list = [];
            int at = list.FindIndex(known => known.Key == device.Key);
            if (at >= 0 && list[at] == device) continue;
            if (at >= 0) list[at] = device; else list.Add(device);
            _lastRead[device.Key] = (device, 0);
            changed = true;
        }
        return changed;
    }

    // ----- Readings from the worker's two helpers (PushEvents, CorsairReader) -----

    private PushEvents? _pushes;
    private CorsairReader? _corsair;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Origin, DeviceBattery Device)> _sideQueue = new();
    // The worker's own: the helpers' current readings, by where each came from (a pushed collection's path, a Corsair device's
    // key). A reading stands until its device sends another or its collection goes (Scan).
    private readonly Dictionary<string, DeviceBattery> _side = new(StringComparer.OrdinalIgnoreCase);

    // On a helper's thread: queued for the worker, which is woken.
    private void Side(string origin, DeviceBattery device)
    {
        _sideQueue.Enqueue((origin, device));
        Wake();
    }

    // The worker takes what came into its current lists. True when a reading changed.
    private bool ApplySide()
    {
        bool changed = false;
        while (_sideQueue.TryDequeue(out var item))
        {
            var (origin, device) = item;
            if (_demand == DeviceDemand.None || !Attached(device)) continue;
            _side[origin] = device;
            if (!_current.TryGetValue(device.Source, out var list)) _current[device.Source] = list = [];
            int at = list.FindIndex(known => known.Key == device.Key);
            if (at >= 0 && list[at] == device) continue;
            if (at >= 0) list[at] = device; else list.Add(device);
            _lastRead[device.Key] = (device, 0);
            changed = true;
        }
        return changed;
    }
    // Whether the collection a helper's reading came through is still there (its vendor and product).
    private bool Attached(DeviceBattery device) => _infos.Values.Any(info => Devices.Hardware(info.Vendor, info.Product) == device.Hardware);

    // A pushed reading as the device it is (PushEvents): the Steam puck holds a controller per interface.
    internal static DeviceBattery Pushed(HidInfo info, Pushes.Kind kind, BatteryRead state, string? name)
    {
        string key = Key("push", info) + (kind == Pushes.Kind.SteamPuck ? ":" + info.Interface : "");
        return new DeviceBattery(key, name ?? Pushes.Name(kind, null), Pushes.KindOf(kind), Pushes.Source(kind), state.Percent)
            { Charging = state.Charging, Coarse = state.Coarse, Asleep = state.Asleep, Hardware = Devices.Hardware(info.Vendor, info.Product) };
    }

    // HID collections that come or go while the page shows wake the worker; the first listing (every collection there is) does not.
    private void WatchArrivals()
    {
        if (_arrivals is not null) return;
        int generation = Interlocked.Increment(ref _generation);
        try
        {
            var watcher = DeviceInformation.CreateWatcher("System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True",
                [], DeviceInformationKind.DeviceInterface);
            bool listed = false;
            // Set and read on the watcher's threads.
            watcher.EnumerationCompleted += (_, _) => Volatile.Write(ref listed, true);
            watcher.Added += (_, _) => { if (Volatile.Read(ref listed) && generation == Volatile.Read(ref _generation)) { _arrived = true; Wake(); } };
            watcher.Removed += (_, _) => { if (Volatile.Read(ref listed) && generation == Volatile.Read(ref _generation)) { _removed = true; Wake(); } };
            // A watcher that does not handle updates stops reporting arrivals after its first listing.
            watcher.Updated += (_, _) => { };
            watcher.Start();
            _arrivals = watcher;
        }
        catch { }
    }
    private void StopArrivals()
    {
        Interlocked.Increment(ref _generation);
        try { if (_arrivals?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _arrivals.Stop(); } catch { }
        _arrivals = null;
    }

    private void Run()
    {
        while (!_disposed)
        {
            try { if (!Pass()) return; }
            catch { _wake.WaitOne(DevicePolling.Ask); }
        }
    }

    // One round of the worker: the events that came, the readings that are due, then the wait for the next. False once nothing
    // is wanted: the thread ends.
    private bool Pass()
    {
        var demand = _demand;
        if (_restart) { _restart = false; _scanned = false; }
        if (demand != DeviceDemand.None)
        {
            // A collection that came or went (a cable put into or pulled out of a device that also has a receiver) reads every device
            // again at once, and twice more a few seconds apart: the device stays through its receiver, its charging changes with the
            // cable, and a collection that has just arrived often cannot be opened yet. A burst of arrivals (a receiver brings
            // several collections) is read once.
            bool changed = _arrived || _removed; _arrived = _removed = false;
            if (changed || !_scanned) Thread.Sleep(300);
            if (changed) _followUps = FollowUps;
            if (changed || _forced) _corsair?.ReadNow();
            bool followUp = !changed && _followUps > 0 && Due(_askAt, FollowUpDelay);
            if (followUp) _followUps--;
            bool forced = _forced; _forced = false;
            bool linkUp = false;
            bool moved = ApplyEvents(ref linkUp) | ApplySide();
            bool all = changed || followUp || forced || !_scanned;
            bool logitech = all || linkUp || Due(_logitechAt, DevicePolling.Logitech);
            bool ask = all || Due(_askAt, DevicePolling.Ask);
            // Listened to only while a PlayStation controller is there; a new one is found by the next full pass.
            bool listen = all || _current.TryGetValue(DeviceSource.Sony, out var sony) && sony.Count > 0 && Due(_listenAt, DevicePolling.Listen);
            if (logitech || ask || listen)
            {
                try { Scan(logitech, ask, listen); moved = true; }
                catch { }
                // A pass counts as done even when it failed, so a failing one is tried again at its next interval, not at once.
                long now = Environment.TickCount64;
                if (logitech) _logitechAt = now;
                if (ask) _askAt = now;
                if (listen) _listenAt = now;
                _scanned = true;
                if (logitech) { _indices = _logitech.BatteryIndices(); _events?.Watch([.. _logitech.Groups]); }
            }
            // A pass that failed as a whole reports nothing: the last readings stay.
            if (moved && _demand != DeviceDemand.None && !_disposed) Publish();
        }
        else
        {
            _infos.Clear(); _logitech.Clear(); _lastRead.Clear(); _slow.Clear(); _current.Clear(); _pending.Clear(); _indices = [];
            _side.Clear(); _sideQueue.Clear();
            _listed = false; _scanned = false; _followUps = 0; _logitechAt = _askAt = _listenAt = Never;
            StopListeners();
            // Nothing is wanted: the thread ends rather than wait, and the next Start makes a new one. Started again meanwhile,
            // it goes round at once.
            lock (_gate)
            {
                if (_demand != DeviceDemand.None) return true;
                _thread = null;
                return false;
            }
        }
        _wake.WaitOne(NextWait());
        return true;
    }
    // The passes still to make after a collection came or went, and the wait before each.
    private const int FollowUps = 2;
    private static readonly TimeSpan FollowUpDelay = TimeSpan.FromSeconds(2);
    private int _followUps;

    private static bool Due(long at, TimeSpan interval) => at == Never || Environment.TickCount64 - at >= (long)interval.TotalMilliseconds - 50;
    private static long Left(long at, TimeSpan interval) => at == Never ? 0 : (long)interval.TotalMilliseconds - (Environment.TickCount64 - at);
    private TimeSpan NextWait()
    {
        if (_followUps > 0) return FollowUpDelay;
        long wait = Math.Min(Left(_logitechAt, DevicePolling.Logitech), Left(_askAt, DevicePolling.Ask));
        if (_current.TryGetValue(DeviceSource.Sony, out var sony) && sony.Count > 0) wait = Math.Min(wait, Left(_listenAt, DevicePolling.Listen));
        return TimeSpan.FromMilliseconds(Math.Clamp(wait, 500, (long)DevicePolling.Ask.TotalMilliseconds));
    }

    // Every source's current list, to the UI thread.
    private void Publish()
    {
        var lists = Sources.Select(source => (source, (IReadOnlyList<DeviceBattery>)(_current.TryGetValue(source, out var list) ? [.. list] : []))).ToList();
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_demand == DeviceDemand.None || _disposed) return;
            foreach (var (source, list) in lists) report(source, list);
            _listed = true;
        });
    }

    // ----- One pass over the collections -----

    // Collections whose feature reports timed out, left alone until then (Environment.TickCount64).
    private readonly Dictionary<string, long> _slow = [];

    // The sources each kind of reading lists.
    private static readonly DeviceSource[] Asked = [DeviceSource.Razer, DeviceSource.SteelSeries, DeviceSource.HyperX, DeviceSource.Hid];

    // One pass over the collections, reading the kinds that are due (Pass) into the current lists; the others keep theirs.
    private void Scan(bool logitech, bool ask, bool listen)
    {
        var found = new Dictionary<DeviceSource, List<DeviceBattery>>();
        // A device read on two collections is one entry: a reading it answered beats one that found it asleep.
        void Add(DeviceBattery device)
        {
            if (!found.TryGetValue(device.Source, out var list)) found[device.Source] = list = [];
            int same = list.FindIndex(known => known.Key == device.Key);
            if (same < 0) list.Add(device);
            else if (list[same].Asleep && !device.Asleep) list[same] = device;
        }
        var paths = Hid.Paths();
        foreach (var gone in _infos.Keys.Except(paths).ToList()) _infos.Remove(gone);
        var infos = new List<HidInfo>();
        foreach (var path in paths)
        {
            if (!_infos.TryGetValue(path, out var info))
            {
                // A Bluetooth collection no reader here speaks to is not opened at all (its level comes from Windows). One that
                // is has its name and serial read as on USB: the serial keeps two pads of one model apart (a classic Bluetooth
                // path carries no address).
                bool bluetooth = Hid.IsBluetooth(path);
                if (bluetooth && !Spoken(Hid.Ids(path))) continue;
                // A collection that could not be opened (its driver still starting) is tried again next time.
                info = Hid.Query(path, batteries: !bluetooth);
                if (info is not null) _infos[path] = info;
            }
            if (info is not null) infos.Add(info);
        }
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Try(Func<IEnumerable<DeviceBattery>> read)
        {
            try { foreach (var device in read()) Add(device); }
            catch { }
        }

        // The brands whose request and reply are two steps another program can come between stand aside while that brand's own
        // software runs (VendorSoftware). The processes are looked up once, and only if such a device is here.
        HashSet<DeviceBrand>? software = null;
        bool Aside(int vendor)
        {
            if (VendorSoftware.BrandOf(vendor) is not { } brand) return false;
            software ??= RunningSoftware();
            return software.Contains(brand);
        }

        // A kind not due this pass still claims its collections, so no other reader takes them.
        var hidpp = infos.Where(info => info.Vendor == Hidpp.Vendor && info.UsagePage == 0xFF00).ToList();
        if (logitech) Try(() => _logitech.Read(hidpp, claimed));
        else foreach (var info in hidpp) claimed.Add(info.Path);
        if (ask)
            foreach (var group in infos.Where(info => info.Vendor == Razer.Vendor).GroupBy(info => info.Product))
                // Basilisk Ultimate, cable and receiver, measured answering battery queries with Synapse 4 running.
                if (Razer.Find(group.Key) is { } model && (model.Product is 0x0086 or 0x0088 || !Aside(Razer.Vendor))) Try(() => ReadRazer(model, [.. group], claimed));
        // The devices (GroupKey) a dongle protocol has read this pass.
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The ROG Omni receivers, each read as a whole (its pairing table, then its paired devices).
        foreach (var receiver in infos.Where(info => info.Vendor == Asus.Vendor && info.Product == Asus.OmniReceiver && !info.Bluetooth).GroupBy(info => GroupKey(info.Path)))
            if (ask) Try(() => ReadOmni([.. receiver], claimed)); else foreach (var each in receiver) claimed.Add(each.Path);
        foreach (var info in infos)
        {
            if (claimed.Contains(info.Path)) continue;
            if (info.Vendor == PlayStation.Vendor && (PlayStation.IsDualSense(info.Product) || PlayStation.IsDualShock(info.Product)) && info.UsagePage == 0x01 && info.Usage == 0x05)
            {
                if (listen) Try(() => ReadPlayStation(info, claimed)); else claimed.Add(info.Path);
                continue;
            }
            if (!ask) continue;
            if (Headsets.Find(info.Vendor, info.Product) is { } headset) { if (Matches(info, Headsets.Channel(headset))) Try(() => ReadHeadset(headset, info, claimed, infos)); continue; }
            // AJAZZ shares Inphic's vendor ids: it does not stand aside for Inphic's software. A receiver whose mouse has answered on
            // one collection is not asked again on its others (G-Wolves' receiver has more than one that would take the request).
            if (Dongle(info) is { } dongle && !answered.Contains(GroupKey(info.Path)) && (Dongles.IsAjazz(info.Vendor, info.Product) || !Aside(info.Vendor)))
            {
                Try(() => dongle(info, claimed));
                if (claimed.Contains(info.Path)) answered.Add(GroupKey(info.Path));
            }
        }
        // The receivers that send their battery unasked are listened to all the time (PushEvents), and Corsair's receivers are
        // read on a thread of their own (CorsairReader): this pass only hands them their collections, and lists the readings
        // they have given, while their collections are there.
        var pushes = new List<PushEvents.Collection>();
        var corsair = new List<HidInfo>();
        // A Corsair receiver without the 0xFF42 collection is tried on its other vendor collections (as HaloBattery does).
        var bragi = infos.Where(info => Corsair.Find(info.Vendor, info.Product) is not null && Corsair.Collection(info.UsagePage)).Select(info => GroupKey(info.Path)).ToHashSet();
        foreach (var info in infos.Where(info => !claimed.Contains(info.Path) && !info.Bluetooth))
        {
            if (Pushes.Find(info.Vendor, info.Product, info.UsagePage, info.Usage, info.Interface, info.InputLength) is { } kind) pushes.Add(new(info, kind));
            else if (Corsair.Find(info.Vendor, info.Product) is not null
                && (Corsair.Collection(info.UsagePage) || !bragi.Contains(GroupKey(info.Path)) && info.UsagePage >= 0xFF00)) corsair.Add(info);
            else continue;
            claimed.Add(info.Path);
        }
        _pushes?.Watch(pushes);
        _corsair?.Watch(corsair);
        foreach (var (origin, device) in _side.ToList())
        {
            if (!Attached(device)) { _side.Remove(origin); continue; }
            Add(device);
        }
        // The standard battery usages, on whatever no vendor protocol read. Bluetooth collections are left out (their level comes
        // from Windows, and a feature report over the air can wait on a sleeping device), and so is one that timed out lately.
        // So are power devices (a UPS on the Power Device or Battery System page, 0x84 and 0x85), which are not wireless
        // devices and often count their charge in capacity units rather than percent.
        long now = Environment.TickCount64;
        foreach (var info in infos.Where(info => ask && info.FeatureBatteries.Count > 0 && !claimed.Contains(info.Path) && !info.Bluetooth && info.UsagePage is not (0x84 or 0x85)
            && (!_slow.TryGetValue(info.Path, out var until) || now >= until)))
        {
            try { foreach (var device in ReadUsage(info)) Add(device); }
            catch (TimeoutException) { _slow[info.Path] = now + 10 * 60_000; }
            catch { }
        }
        // A mouse on its cable is not listed a second time through its receiver, which finds it asleep (Dongles.AjazzShadowed,
        // Dongles.Shadowed).
        void DropShadowed()
        {
            foreach (var source in new[] { DeviceSource.Hid, DeviceSource.SteelSeries })
                if (found.TryGetValue(source, out var list))
                {
                    var shadowed = list.Where(device => Dongles.AjazzShadowed(device, list) || Dongles.Shadowed(device, list)).ToList();
                    list.RemoveAll(shadowed.Contains);
                }
        }

        var read = new HashSet<DeviceSource>();
        if (logitech) read.Add(DeviceSource.Logitech);
        if (ask) read.UnionWith(Asked);
        if (listen) read.Add(DeviceSource.Sony);

        // A receiver that answers for a mouse asleep or switched off says 0 % and not charging (G-Helper, vigil-watt and HaloBattery
        // read it so): that is no reading, so the last one stands, asleep, rather than an empty battery. A reader that found the
        // mouse asleep without a level (the receiver said so) keeps its last reading the same way; one never read is not listed.
        foreach (var source in Asked)
        {
            if (!found.TryGetValue(source, out var list)) continue;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is { Percent: 0, Charging: false } placeholder)
                    list[i] = _lastRead.TryGetValue(placeholder.Key, out var last) && last.Device.Percent > 0
                        ? last.Device with { Charging = false, Asleep = true } : placeholder with { Percent = null, Asleep = true };
                else if (list[i] is { Percent: null, Asleep: true } asleep)
                {
                    if (_lastRead.TryGetValue(asleep.Key, out var last) && last.Device.Percent > 0) list[i] = last.Device with { Charging = false, Asleep = true };
                    else list.RemoveAt(i);
                }
            }
        }

        // A device that missed one reading (asleep for a moment, busy with its own software) keeps its last one, not charging,
        // until the next pass of its kind; missing twice, it is gone.
        foreach (var list in found.Values) foreach (var device in list) _lastRead[device.Key] = (device, 0);
        foreach (var (key, (device, missed)) in _lastRead.ToList())
        {
            if (!read.Contains(device.Source) || found.Values.SelectMany(list => list).Any(seen => seen.Key == key)) continue;
            if (missed >= 1 || !Present(key)) { _lastRead.Remove(key); continue; }
            _lastRead[key] = (device, missed + 1);
            Add(device with { Charging = false });
        }
        if (ask) DropShadowed();
        foreach (var source in read) _current[source] = found.TryGetValue(source, out var list) ? list : [];
    }
    // The brands whose software is running, from one snapshot of the process names (nothing is opened or allocated per process).
    private static HashSet<DeviceBrand> RunningSoftware()
    {
        var running = new HashSet<DeviceBrand>();
        ProcessSnapshot.Any(file => { VendorSoftware.Note(file, running); return false; });
        return running;
    }

    // Whether a collection still present belongs to the device a key names (its vendor and product are in the key; a Logitech
    // device's key names its unit, so any Logitech collection will do).
    private static bool KeyOwner(HidInfo info, string key) => key.Contains($"{info.Vendor:X4}:{info.Product:X4}", StringComparison.Ordinal);
    // A Logitech device's key names its unit, not its receiver: it is present while the receiver it was read through is.
    private bool Present(string key) => key.StartsWith("logitech:", StringComparison.Ordinal)
        ? _logitech.Receiver(key) is { } group && _infos.Keys.Any(path => GroupKey(path) == group)
        : _infos.Values.Any(info => KeyOwner(info, key));

    // Whether a reader here speaks to a vendor and product over Bluetooth (from the path, before it is opened): PlayStation
    // controllers, the headsets and Razer's models. Logitech's Bluetooth collections carry HID++ on a page the receiver reader
    // does not use, and the 2.4 GHz brands are read on their dongles only, so neither is opened over the air.
    internal static bool Spoken((int Vendor, int Product) ids) => ids.Vendor == PlayStation.Vendor
        || ids.Vendor == Razer.Vendor && Razer.Find(ids.Product) is not null || Headsets.Find(ids.Vendor, ids.Product) is not null;

    // Headsets.Channel: an interface, a usage page, or (VendorPages) any vendor-defined page.
    private static bool Matches(HidInfo info, (int Interface, int UsagePage) channel)
        => (channel.Interface < 0 || info.Interface == channel.Interface)
        && (channel.UsagePage == Headsets.VendorPages ? info.UsagePage >= 0xFF00 : channel.UsagePage < 0 || info.UsagePage == channel.UsagePage);

    // A stable key for a device on a collection: its vendor and product, and its serial where it has one, else its Bluetooth
    // address where the path carries one (LE). Not the path, which names the USB port: a receiver moved to another port is the same
    // device, still switched off. The price: two receivers of one model without serials are listed as one device.
    private static string Key(string prefix, HidInfo info)
        => $"{prefix}:{info.Vendor:X4}:{info.Product:X4}" + (info.Serial.Length > 0 ? ":" + info.Serial : AddressOf(info) is { } address ? ":" + address : "");
    internal static string GroupKey(string path)
    {
        var p = System.Text.RegularExpressions.Regex.Replace(path.ToLowerInvariant(), "&col[0-9a-f]+", "");
        p = System.Text.RegularExpressions.Regex.Replace(p, "&mi_[0-9a-f]+", "");
        return System.Text.RegularExpressions.Regex.Replace(p, @"&[0-9a-f]{4}#\{", "#{");
    }
    private static string NameOf(HidInfo info, string fallback) => info.ProductName.Length > 0 ? info.ProductName : fallback;
    private static string? AddressOf(HidInfo info) => info.Bluetooth ? Devices.AddressIn(info.Path) : null;

    // ----- Razer (razer-battery-report's flow: the battery, then charging where the model reports it) -----

    private IEnumerable<DeviceBattery> ReadRazer(Razer.Model model, List<HidInfo> collections, HashSet<string> claimed)
    {
        // On the model's interface: the mouse's (or keyboard's) own collection first, then the vendor one; the first that answers.
        int usage = model.Kind == DeviceKind.Keyboard ? 0x06 : 0x02;
        var candidates = collections.Where(info => info.Interface < 0 || info.Interface == model.Interface)
            .OrderBy(info => info.UsagePage == 0x01 && info.Usage == usage ? 0 : info.UsagePage >= 0xFF00 ? 1 : 2).ToList();
        foreach (var info in candidates)
        {
            HidDevice device;
            try { device = new HidDevice(info, readWrite: false); }
            catch { continue; }
            using (device)
            {
                var battery = RazerQuery(device, model, Razer.Battery);
                if (battery is null) continue;
                bool charging = model.Charging && RazerQuery(device, model, Razer.ChargingState) is { } state && state != 0;
                foreach (var each in collections) claimed.Add(each.Path);
                yield return new DeviceBattery(Key("razer", info), model.Name, model.Kind, DeviceSource.Razer, Razer.Percent(battery.Value))
                { Charging = charging, Hardware = Devices.Hardware(info.Vendor, info.Product) };
                yield break;
            }
        }
    }
    private static byte? RazerQuery(HidDevice device, Razer.Model model, (byte Class, byte Id, byte Size) command)
    {
        var request = new byte[Razer.ReportLength + 1];
        Razer.Request(model.Transaction, command).CopyTo(request, 1);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0) Thread.Sleep(10);
            if (!device.SetFeature(request)) return null;
            Thread.Sleep(Math.Max(1, model.WaitMicroseconds / 1000));
            var reply = device.GetFeature(0, Math.Max(device.FeatureLength, Razer.ReportLength + 1));
            if (reply is null || reply.Length < Razer.ReportLength + 1) return null;
            if (Razer.Reply(reply[1..(Razer.ReportLength + 1)], command) is not { } answer) continue;
            if (answer.Status == Razer.Success) return answer.Value;
            if (answer.Status is not (Razer.Busy or Razer.NoResponse)) return null;
        }
        return null;
    }

    // ----- Headsets on their own dongles -----

    private static IEnumerable<DeviceBattery> ReadHeadset(Headsets.Kind kind, HidInfo info, HashSet<string> claimed, List<HidInfo> infos)
    {
        using var device = new HidDevice(info) { StripZero = true };
        // Where the reply can come on another collection of the interface, those are read as well (Headsets.ReadsSiblings).
        var listeners = new List<HidDevice> { device };
        if (Headsets.ReadsSiblings(kind))
            foreach (var other in infos.Where(other => other.Path != info.Path && other.Interface == info.Interface && GroupKey(other.Path) == GroupKey(info.Path) && other.InputLength > 0))
                try { listeners.Add(new HidDevice(other) { StripZero = true }); } catch { }
        try
        {
            void Send(byte[] request)
            {
                try { device.Write(request); }
                catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1 && Headsets.FeatureFallback(kind)) { device.SetFeature(request); }
            }
            int wait = Headsets.Wait(kind);
            // A dongle whose headset is off is not read: its answer to the level could be a stale one.
            if (Headsets.ConnectionRequest(kind) is { } ask
                && Hid.Transact<object>(listeners, () => Send(ask), r => Headsets.Connected(kind, r) is { } connected ? connected : null, wait) is false) return [];
            var reading = Hid.Transact<object>(listeners, () => Send(Headsets.Request(kind)), r => Headsets.Battery(kind, r) is { } read ? read : null, wait);
            if (reading is not BatteryRead battery) return [];
            bool charging = battery.Charging;
            if (Headsets.ChargingRequest(kind) is { } second)
                charging |= Hid.Transact<object>(listeners, () => Send(second), r => Headsets.Charging(kind, r) is { } state ? state : null, wait) is true;
            claimed.Add(info.Path);
            return [new DeviceBattery(Key("headset", info), NameOf(info, Headsets.Name(kind)), DeviceKind.Headphones, Headsets.Source(kind), battery.Percent)
                { Charging = charging, Coarse = battery.Coarse, Hardware = Devices.Hardware(info.Vendor, info.Product) }];
        }
        finally { foreach (var other in listeners.Skip(1)) other.Dispose(); }
    }

    // ----- PlayStation controllers: a few input reports, read as they come -----

    private static IEnumerable<DeviceBattery> ReadPlayStation(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        claimed.Add(info.Path);
        long deadline = Environment.TickCount64 + 300;
        while (Environment.TickCount64 < deadline)
        {
            if (device.Read((int)Math.Max(1, deadline - Environment.TickCount64)) is not { } report) break;
            if (PlayStation.Battery(info.Product, report, info.Bluetooth) is not { } battery) continue;
            string name = PlayStation.IsDualSense(info.Product) ? "DualSense Wireless Controller" : "DUALSHOCK 4 Wireless Controller";
            return [new DeviceBattery(Key("ps", info), NameOf(info, name), DeviceKind.Controller, DeviceSource.Sony, battery.Percent)
                { Charging = battery.Charging, Hardware = Devices.Hardware(info.Vendor, info.Product), Address = AddressOf(info) }];
        }
        // A controller on Bluetooth in its short reports: connected, but without a reading until a game asks for full reports.
        return [new DeviceBattery(Key("ps", info), NameOf(info, "PlayStation Controller"), DeviceKind.Controller, DeviceSource.Sony, null)
            { Hardware = Devices.Hardware(info.Vendor, info.Product), Address = AddressOf(info) }];
    }

    // ----- The 2.4 GHz brands -----

    // On their dongles only: a request is never sent to a Bluetooth collection that happens to share a vendor and page. Which
    // collections get which protocol is Dongles.Find's decision (vendor and product pairs that are known, not vendor ids alone).
    private static Func<HidInfo, HashSet<string>, IEnumerable<DeviceBattery>>? Dongle(HidInfo info) => info.Bluetooth ? null
        : Dongles.Find(info.Vendor, info.Product, info.UsagePage, info.ProductName, info.FeatureLength, info.Interface, info.Usage,
            info.InputLength, info.OutputLength, info.Collection) switch
        {
            Dongles.Protocol.BitMouse => BitMouse,
            Dongles.Protocol.Compx => Compx,
            Dongles.Protocol.Vgn => Vgn,
            Dongles.Protocol.Endgame => Endgame,
            Dongles.Protocol.Vaxee => Vaxee,
            Dongles.Protocol.Inphic => Inphic,
            Dongles.Protocol.Mchose => Mchose,
            Dongles.Protocol.Ajazz => Ajazz,
            Dongles.Protocol.CompxFramed => CompxFramed,
            Dongles.Protocol.F83 => F83,
            Dongles.Protocol.Nordic => Nordic,
            Dongles.Protocol.MchoseRealtek => MchoseRealtek,
            Dongles.Protocol.Gmouse => Gmouse,
            Dongles.Protocol.Lingbao => Lingbao,
            Dongles.Protocol.Nzxt => Nzxt,
            Dongles.Protocol.Cherry => (info, claimed) => StatusBlock(info, claimed, Dongles.StatusBlock.Cherry),
            Dongles.Protocol.Hecate => (info, claimed) => StatusBlock(info, claimed, Dongles.StatusBlock.Hecate),
            Dongles.Protocol.Asus => ReadAsus,
            Dongles.Protocol.SteelSeries => SteelSeriesMouse,
            Dongles.Protocol.RapooGen1 => RapooGen1,
            Dongles.Protocol.Aula => Aula,
            _ => null
        };

    private static DeviceBattery Mouse(string prefix, HidInfo info, string fallback, BatteryRead battery, DeviceKind kind = DeviceKind.Mouse, string? preferredName = null)
        => new(Key(prefix, info), preferredName is { Length: > 0 } ? preferredName : NameOf(info, fallback),
            Devices.Named(info.ProductName) is { } named and not DeviceKind.Other ? named : kind, DeviceSource.Hid, battery.Percent)
        { Charging = battery.Charging, Coarse = battery.Coarse, Asleep = battery.Asleep, Hardware = Devices.Hardware(info.Vendor, info.Product) };

    private static IEnumerable<DeviceBattery> BitMouse(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        // The mouse behind a receiver first; a mouse on its cable answers for itself.
        foreach (byte target in new byte[] { 1, 0 })
        {
            var read = Hid.Transact<object>([device], () => device.Write(Dongles.BitMouseRequest(0x07, target)), r => Dongles.BitMouseBattery(r) is { } b ? b : null, 500);
            if (read is not BatteryRead battery) continue;
            bool charging = Hid.Transact<object>([device], () => device.Write(Dongles.BitMouseRequest(0x0F, target)), r => Dongles.BitMouseCharging(r) is { } c ? c : null, 500) is true;
            claimed.Add(info.Path);
            return [Mouse("atk", info, "ATK / VXE", battery with { Charging = charging })];
        }
        return [];
    }

    // The receivers Dongles.Find sends the Compx frame to (the ATK A9 Ultra). atkctl sends it as feature report 8, set then get,
    // which is tried first; only if that gives no reading is it written as an output report, with the overlapped write that has a
    // timeout (Write). Never HidD_SetOutputReport: it is synchronous and has no timeout, and Microsoft documents that some devices
    // do not support it and stop responding after it.
    private static IEnumerable<DeviceBattery> Compx(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        var request = Dongles.CompxRequest(0x04);
        object? read = null;
        if (device.SetFeature(request) && device.GetFeature(0x08, Math.Max(17, device.FeatureLength)) is { } feature) read = Dongles.CompxBattery(feature);
        if (read is null)
        {
            try { read = Hid.Transact<object>([device], () => device.Write(request), r => Dongles.CompxBattery(r) is { } b ? b : null, 500); }
            // A collection with no output report: no reading, and nothing more to try.
            catch (System.ComponentModel.Win32Exception) { }
        }
        if (read is not BatteryRead battery) return [];
        claimed.Add(info.Path);
        return [Mouse("compx", info, "VXE", battery)];
    }

    // VGN's keyboards, and ATK's, which answer the same frame (Dongles.AtkKeyboards). The Z87 Pro's dongle calls itself
    // "ATK Z87 Dongle", so the keyboard's own model name is given where the table has it rather than the dongle's.
    private static IEnumerable<DeviceBattery> Vgn(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        var read = Hid.Transact<object>([device], () => device.Write(Dongles.VgnRequest()), r => Dongles.VgnBattery(r) is { } b ? b : null, 500);
        if (read is not BatteryRead battery) return [];
        claimed.Add(info.Path);
        bool atk = info.Vendor == Dongles.AtkVendor;
        return [Mouse(atk ? "atkkb" : "vgn", info, atk ? "ATK" : "VGN", battery, DeviceKind.Keyboard, atk ? Dongles.AtkKeyboard(info.Product) : null)];
    }

    // The AULA F87 PRO's 2.4 GHz receiver (Dongles.AulaRequest): command 0x05 on its 0xFF02 collection. The receiver answers a
    // request with echoes of it, and the 0x0A report that carries the charge comes with the request after it. One request goes
    // out each pass, in the same exchange as the other keyboards (no wait between two requests that would hold up the devices
    // read after this one): the first pass after the receiver arrives finds only the echoes, and the passes after it the
    // charge. The keyboard is the only device behind the receiver, so no target goes in the frame.
    private static IEnumerable<DeviceBattery> Aula(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        var read = Hid.Transact<object>([device], () => device.Write(Dongles.AulaRequest(0x05)), r => Dongles.AulaBattery(r) is { } b ? b : null, 500);
        if (read is not BatteryRead battery) return [];
        claimed.Add(info.Path);
        return [Mouse("aula", info, "AULA", battery, DeviceKind.Keyboard, "AULA F87 PRO")];
    }

    // The feature-report brands: set the request, wait, get the reply.
    private static IEnumerable<DeviceBattery> Feature(HidInfo info, HashSet<string> claimed, string prefix, string fallback, byte[] request, int waitMs, Func<byte[], BatteryRead?> parse)
    {
        using var device = new HidDevice(info);
        if (!device.SetFeature(request)) return [];
        Thread.Sleep(waitMs);
        if (device.GetFeature(request[0], device.FeatureLength) is not { } reply || parse(reply) is not { } battery) return [];
        claimed.Add(info.Path);
        return [Mouse(prefix, info, fallback, battery)];
    }
    private static IEnumerable<DeviceBattery> Endgame(HidInfo info, HashSet<string> claimed) => Feature(info, claimed, "endgame", "Endgame Gear", Dongles.EndgameRequest(info.FeatureLength), 350, Dongles.EndgameBattery);
    private static IEnumerable<DeviceBattery> Vaxee(HidInfo info, HashSet<string> claimed) => Feature(info, claimed, "vaxee", "VAXEE", Dongles.VaxeeRequest(info.FeatureLength), 120, Dongles.VaxeeBattery);

    // Inphic IN9: the handshake, then the battery, with one sequence number (InphicBatteryTray's order).
    private static IEnumerable<DeviceBattery> Inphic(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        device.SetFeature(Dongles.InphicRequest(0xF0, 1, Dongles.InphicHandshake));
        Thread.Sleep(10);
        if (!device.SetFeature(Dongles.InphicRequest(0x20, 1))) return [];
        Thread.Sleep(10);
        if (device.GetFeature(0x0C, 33) is not { } reply || Dongles.InphicBattery(reply) is not { } battery) return [];
        claimed.Add(info.Path);
        return [Mouse("inphic", info, "Inphic", battery)];
    }

    private static IEnumerable<DeviceBattery> Mchose(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) Thread.Sleep(30);
            var read = Hid.Transact<object>([device], () => device.Write(Dongles.MchoseRequest()), r => Dongles.MchoseBattery(r) is { } b ? b : null, 400);
            if (read is not BatteryRead battery) continue;
            claimed.Add(info.Path);
            return [Mouse("mchose", info, "MCHOSE", battery)];
        }
        return [];
    }

    // AJAZZ AJ179: WriteFile interrupt OUT (report-id 0 + 32-byte 0x10), read until an info or online status frame.
    // The receiver answers 0x10 while the mouse sleeps and still carries the last battery — mark Asleep, never invent 0 %.
    private static IEnumerable<DeviceBattery> Ajazz(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info) { StripZero = true };
        var reading = Hid.Transact<object>([device], () => device.Write(Dongles.AjazzInfoRequest()), r =>
        {
            if (Dongles.AjazzParseInfo(r) is { } infoReply) return infoReply;
            // A passive 0xC0 while waiting: only take it when the mouse is online and the battery byte is valid.
            if (Dongles.AjazzParseStatus(r) is { Online: true, Percent: { } percent })
                return new Dongles.AjazzInfo(percent, Charging: false, Connected: true, DeviceId: "");
            return null;
        }, 1500);
        bool cable = info.Vendor == Dongles.AjazzWiredVendor && Dongles.IsAjazzWired(info.Product);
        if (reading is not Dongles.AjazzInfo parsed || Dongles.AjazzBattery(parsed, cable) is not { } battery) return [];
        claimed.Add(info.Path);
        string name = parsed.DeviceId.Length > 0 ? parsed.DeviceId : NameOf(info, "AJAZZ");
        return [Mouse("ajazz", info, "AJAZZ", battery, preferredName: name)];
    }

    // The Compx frame on its own 17-byte collection (Dongles.CompxFramed): written, and answered on the input report. A mouse
    // asleep behind its receiver does not answer at all.
    private static IEnumerable<DeviceBattery> CompxFramed(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        var read = Hid.Transact<object>([device], () => device.Write(Dongles.CompxRequest(0x04)), r => Dongles.CompxFrame(r) is { } b ? b : null, 600);
        if (read is not BatteryRead battery) return [];
        claimed.Add(info.Path);
        return [Mouse("compx", info, "Compx", battery)];
    }

    // The 0x83 exchange (Dongles.F83): the request set once, the reply got back until it comes (HaloBattery's 34 ms, then up to
    // fifteen reads 50 ms apart). A collection that refuses the request is not the one.
    private static IEnumerable<DeviceBattery> F83(HidInfo info, HashSet<string> claimed)
    {
        if (Dongles.F83Find(info.Vendor, info.Product) is not { } model) return [];
        using var device = new HidDevice(info);
        if (!device.SetFeature(Dongles.F83Request(model))) return [];
        Thread.Sleep(34);
        BatteryRead? read = null;
        for (int attempt = 0; attempt < 15 && read is null; attempt++)
        {
            if (attempt > 0) Thread.Sleep(50);
            if (device.GetFeature(0, Math.Max(65, device.FeatureLength)) is { } reply) read = Dongles.F83Battery(model, reply);
        }
        if (read is not { } battery) return [];
        claimed.Add(info.Path);
        return [Mouse("f83", info, model.Name, battery, preferredName: model.Name)];
    }

    private static IEnumerable<DeviceBattery> Nordic(HidInfo info, HashSet<string> claimed)
        => Feature(info, claimed, "nordic", "Ninjutso Sora V2", Dongles.NordicRequest(32), 90, Dongles.NordicBattery);
    private static IEnumerable<DeviceBattery> Lingbao(HidInfo info, HashSet<string> claimed)
        => Feature(info, claimed, "lingbao", "LINGBAO M5PRO Max", Dongles.LingbaoRequest(), 50, Dongles.LingbaoBattery);

    // MCHOSE on its RealTek receivers: the short report first, then the long one, each asked afresh before every read.
    private static IEnumerable<DeviceBattery> MchoseRealtek(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        foreach (bool longReport in new[] { false, true })
        {
            var request = Dongles.MchoseRealtekRequest(longReport);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (!device.SetFeature(request)) break;
                Thread.Sleep(120);
                if (device.GetFeature(request[0], Math.Max(request.Length, device.FeatureLength)) is not { } reply || Dongles.MchoseRealtekBattery(reply) is not { } answer) continue;
                claimed.Add(info.Path);
                return [Mouse("mchose", info, "MCHOSE", answer.Read, preferredName: Dongles.MchoseRealtekModel(answer.Model))];
            }
        }
        return [];
    }

    private static IEnumerable<DeviceBattery> Gmouse(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info) { StripZero = true };
        var read = Hid.Transact<object>([device], () => device.Write(Dongles.GmouseRequest()), r => Dongles.GmouseBattery(r) is { } b ? b : null, 800);
        if (read is not BatteryRead battery) return [];
        claimed.Add(info.Path);
        return [Mouse("mchose", info, "MCHOSE", battery)];
    }

    // The NZXT dongle acknowledges first (4E E5), then answers; the acknowledgement is passed over.
    private static IEnumerable<DeviceBattery> Nzxt(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        var read = Hid.Transact<object>([device], () => device.Write(Dongles.NzxtRequest()), r => Dongles.NzxtBattery(r) is { } b ? b : null, 1500);
        if (read is not BatteryRead battery) return [];
        claimed.Add(info.Path);
        return [Mouse("nzxt", info, "NZXT Lift Elite", battery, preferredName: "NZXT Lift Elite")];
    }

    private static IEnumerable<DeviceBattery> StatusBlock(HidInfo info, HashSet<string> claimed, Dongles.StatusBlock kind)
    {
        using var device = new HidDevice(info);
        var read = Hid.Transact<object>([device], () => device.Write(Dongles.StatusBlockRequest(kind)), r => Dongles.StatusBlockBattery(kind, r) is { } b ? b : null, 700);
        if (read is not BatteryRead battery) return [];
        claimed.Add(info.Path);
        return kind == Dongles.StatusBlock.Cherry
            ? [Mouse("cherry", info, "CHERRY PIXIU75", battery, DeviceKind.Keyboard)]
            : [Mouse("hecate", info, "HECATE G5M Pro", battery)];
    }

    // ASUS: up to three asks, 300 ms each (as G-Helper asks), stopping at a command the firmware refuses; a headset is asked
    // for its charging state second.
    private static IEnumerable<DeviceBattery> ReadAsus(HidInfo info, HashSet<string> claimed)
    {
        if (Asus.Find(info.Vendor, info.Product) is not { } model) return [];
        using var device = new HidDevice(info);
        if (AskAsus(device, model) is not { } battery) return [];
        claimed.Add(info.Path);
        return [Mouse("asus", info, model.Name, battery, Asus.Kind(model))];
    }
    private static BatteryRead? AskAsus(HidDevice device, Asus.Model model)
    {
        object? reply = null;
        for (int attempt = 0; attempt < 3 && reply is null; attempt++)
            reply = Hid.Transact<object>([device], () => device.Write(Sized(device, Asus.Request(model))), r => Asus.Reply(model, r), 300);
        if (reply is not BatteryRead battery) return null;
        if (Asus.ChargingRequest(model) is { } second
            && Hid.Transact<object>([device], () => device.Write(Sized(device, second)), r => Asus.Charging(model, r) is { } state ? state : null, 300) is true)
            battery = battery with { Charging = true };
        return battery;
    }
    // A request at the collection's own output length (65 bytes on the ASUS mice, 64 on a headset's or the Omni receiver's).
    private static byte[] Sized(HidDevice device, byte[] request) { Array.Resize(ref request, device.OutputLength); return request; }

    // The ROG Omni receiver: its pairing table on collection 1, then each paired model on its own channel (Asus.OmniTable).
    // Every collection of the receiver is claimed, whatever it answers.
    private static IEnumerable<DeviceBattery> ReadOmni(List<HidInfo> receiver, HashSet<string> claimed)
    {
        foreach (var each in receiver) claimed.Add(each.Path);
        HidInfo? Channel(int collection) => receiver.FirstOrDefault(info => info.Interface == Asus.OmniInterface && info.Collection == collection && info.OutputLength >= 64);
        if (Channel(Asus.OmniTableCollection) is not { } tableInfo) return [];
        IReadOnlyList<Asus.Model>? paired;
        using (var table = new HidDevice(tableInfo))
            paired = Hid.Transact<IReadOnlyList<Asus.Model>>([table], () => table.Write(Sized(table, Asus.OmniTableRequest())), Asus.OmniTable, 700);
        var found = new List<DeviceBattery>();
        foreach (var model in paired ?? [])
        {
            if (Channel(model.Collection) is not { } info) continue;
            try
            {
                using var device = new HidDevice(info);
                if (AskAsus(device, model) is not { } battery) continue;
                found.Add(new DeviceBattery(Key("asus", info) + $":{model.Product:X4}", model.Name, Asus.Kind(model), DeviceSource.Hid, battery.Percent)
                    { Charging = battery.Charging, Coarse = battery.Coarse, Hardware = Devices.Hardware(info.Vendor, info.Product) });
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or TimeoutException or IOException) { }
        }
        return found;
    }

    // Rapoo's first generation (Dongles.RapooGen1*; untested by us on a mouse): the query written as output report 0xBA, then
    // the input report got back every few milliseconds until it has gone busy and then answered; a request the receiver lost
    // (never busy within 100 ms) is sent again, as mousectl does, within 1.5 s in all.
    private static IEnumerable<DeviceBattery> RapooGen1(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        var request = Dongles.RapooGen1Request(cable: info.Product == 0x4431);
        long deadline = Environment.TickCount64 + 1500;
        for (int sends = 0; sends < 4 && Environment.TickCount64 < deadline; sends++)
        {
            device.Write(request);
            long sent = Environment.TickCount64;
            bool busy = false;
            while (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(5);
                byte[]? reply;
                try { reply = device.GetInput(Dongles.RapooGen1Report, Math.Max(33, device.InputLength), 300); }
                catch (TimeoutException) { continue; }
                if (reply is null || Dongles.RapooGen1Status(reply) is not { } ok) continue;
                if (!ok) { busy = true; continue; }
                if (busy)
                {
                    if (Dongles.RapooGen1Battery(reply) is not { } battery) break;
                    claimed.Add(info.Path);
                    return [Mouse("rapoo", info, "Rapoo VT3 PRO", battery)];
                }
                if (Environment.TickCount64 - sent >= 100) break;
            }
        }
        return [];
    }

    private static IEnumerable<DeviceBattery> SteelSeriesMouse(HidInfo info, HashSet<string> claimed)
    {
        if (SteelSeriesMice.Find(info.Vendor, info.Product) is not { } model) return [];
        using var device = new HidDevice(info);
        var read = Hid.Transact<object>([device], () => device.Write(SteelSeriesMice.Request(model)), r => SteelSeriesMice.Battery(model, r) is { } b ? b : null, 600);
        if (read is not BatteryRead battery) return [];
        claimed.Add(info.Path);
        return [Mouse("steelseries", info, model.Name, battery) with { Source = DeviceSource.SteelSeries }];
    }

    // ----- The standard battery usages -----

    private static IEnumerable<DeviceBattery> ReadUsage(HidInfo info)
    {
        using var device = new HidDevice(info, readWrite: false);
        foreach (var battery in info.FeatureBatteries)
        {
            if (Hid.FeatureValue(device, battery, info.FeatureLength) is not { } value || HidBattery.Percent(value, battery.Min, battery.Max) is not { } percent) continue;
            var kind = (info.UsagePage, info.Usage) switch
            {
                (0x01, 0x06) => DeviceKind.Keyboard, (0x01, 0x02) => DeviceKind.Mouse, (0x01, 0x04 or 0x05) => DeviceKind.Controller, (0x0D, _) => DeviceKind.Pen, _ => DeviceKind.Other
            };
            return [new DeviceBattery(Key("hid", info), NameOf(info, Devices.KindName(kind)), Devices.Named(info.ProductName) ?? kind, DeviceSource.Hid, percent)
                { Hardware = Devices.Hardware(info.Vendor, info.Product), Address = AddressOf(info) }];
        }
        return [];
    }

    // ----- Logitech HID++ (device-battery-viewer's LogitechHidpp, ported) -----

    // A receiver's or a cabled device's HID++ channel: the long collection sends and receives, the short one only receives (HID++
    // 1.0 errors come short). Device slots it has found are kept with their feature indices, name, kind and key, so a later
    // reading costs one request.
    private sealed class LogitechReader
    {
        private const int TimeoutMs = 400, PingTimeoutMs = 1500;
        // Legacy: a HID++ 1.0 device, read through its registers.
        private sealed class Slot { public string Key = "", Name = ""; public bool Legacy; public DeviceKind Kind = DeviceKind.Other; public readonly Dictionary<int, byte> Features = []; public byte[]? Capabilities; public DeviceBattery? Last; public long LastAt; }
        // A device that sleeps between readings (a mouse left alone) keeps its last level this long, as asleep and not charging: a
        // device that charges on its cable is powered and answers, so a reading taken while it charged (a mouse just unplugged,
        // which sleeps at once) does not say it still does.
        private const long Asleep = 10 * 60_000;
        private static DeviceBattery? Remembered(Slot? slot) => slot?.Last is { } last && Environment.TickCount64 - slot.LastAt < Asleep ? last with { Charging = false, Asleep = true } : null;
        private readonly Dictionary<string, Slot> _known = [];
        public void Clear() => _known.Clear();
        // The receiver (its collections' group) a device was last read through, by the device's key.
        public string? Receiver(string key) => _known.FirstOrDefault(pair => pair.Value.Key == key).Key is { } slot ? slot[..slot.LastIndexOf('|')] : null;

        // The receivers and cabled devices the last Read went through, for the event listener (HidppEvents).
        public List<HidppEvents.Group> Groups { get; } = [];

        public IEnumerable<DeviceBattery> Read(List<HidInfo> collections, HashSet<string> claimed)
        {
            var found = new List<DeviceBattery>();
            var shorts = collections.Where(info => info.Usage == 0x0001).GroupBy(info => GroupKey(info.Path)).ToDictionary(g => g.Key, g => g.First());
            Groups.Clear();
            foreach (var longInfo in collections.Where(info => info.Usage == 0x0002))
            {
                string group = GroupKey(longInfo.Path);
                shorts.TryGetValue(group, out var shortInfo);
                Groups.Add(new HidppEvents.Group(group, longInfo, shortInfo, Hidpp.IsReceiver(longInfo.Product, longInfo.ProductName)));
                Channel channel;
                try { channel = new Channel(longInfo, shortInfo); }
                catch { continue; }
                using (channel)
                {
                    claimed.Add(longInfo.Path); if (shortInfo is not null) claimed.Add(shortInfo.Path);
                    foreach (var index in Hidpp.Indices(Hidpp.IsReceiver(longInfo.Product, longInfo.ProductName)))
                    {
                        DeviceBattery? device;
                        // A failed write or read (the receiver pulled out half way) costs this slot its reading, not the others'.
                        try { device = ReadOne(channel, group, index, longInfo); }
                        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { device = Remembered(_known.GetValueOrDefault(group + "|" + index)); }
                        // A device read on its cable and through its receiver is one entry: a reading it answered beats one kept
                        // from before (Remembered), whichever came first.
                        if (device is null) continue;
                        int same = found.FindIndex(known => known.Key == device.Key);
                        if (same < 0) found.Add(device);
                        else if (found[same].Asleep && !device.Asleep) found[same] = device;
                    }
                }
            }
            return found;
        }

        private DeviceBattery? ReadOne(Channel channel, string group, byte index, HidInfo info)
        {
            string slotKey = group + "|" + index;
            _known.TryGetValue(slotKey, out var slot);
            try
            {
                // Root's protocol version is the ping; a sleeping or empty slot does not answer.
                var version = channel.Request(index, 0x00, 1, [0, 0, 0x5A], PingTimeoutMs);
                if (version[0] < 2) return null;
            }
            catch (HidppError error) when (error.Code == Hidpp.InvalidSubId)
            {
                // HID++ 1.0: the battery registers. Its slot is kept as a 2.0 device's is, so it counts as present and keeps
                // its last reading through a missed one.
                if (Registers(channel, index, info) is not { } read) return Remembered(slot is { Legacy: true } ? slot : null);
                _known[slotKey] = new Slot { Key = read.Key, Name = read.Name, Legacy = true, Last = read, LastAt = Environment.TickCount64 };
                return read;
            }
            catch (Exception error) when (error is TimeoutException or HidppError) { return Remembered(slot); }
            // A slot a HID++ 1.0 device held is found afresh.
            if (slot is { Legacy: true }) slot = null;
            var device = new Device(channel, index, slot ?? new Slot());
            try
            {
                if (slot is null)
                {
                    var identity = device.Call(Hidpp.DeviceInfo, 0);
                    string unit = identity is { Length: >= 5 } ? Convert.ToHexString(identity, 1, 4) : "";
                    device.Slot.Key = "logitech:" + (unit.Length > 0 && unit != "00000000" ? unit : $"{info.Product:X4}:{index}");
                    string name = device.ReadName();
                    device.Slot.Name = name.Length > 0 ? name : info.ProductName.Length > 0 && index == 0xFF ? info.ProductName : "Logitech #" + index;
                    device.Slot.Kind = device.ReadKind();
                    _known[slotKey] = slot = device.Slot;
                }
                // A device without a battery feature (a keyboard on a cable) is not listed.
                if (device.ReadBattery() is not { } battery) return null;
                return Take(slot, battery);
            }
            catch (Exception error) when (error is TimeoutException or HidppError)
            {
                // The device fell asleep half way; its feature indices may have changed, so they are asked again next time.
                slot?.Features.Clear();
                return Remembered(slot);
            }
        }

        private static DeviceBattery Take(Slot slot, BatteryRead battery)
        {
            slot.Last = new DeviceBattery(slot.Key, slot.Name, slot.Kind != DeviceKind.Other ? slot.Kind : Devices.Named(slot.Name) ?? DeviceKind.Other, DeviceSource.Logitech, battery.Percent)
            { Charging = battery.Charging, Coarse = battery.Coarse };
            slot.LastAt = Environment.TickCount64;
            return slot.Last;
        }

        // ----- What the devices send unasked (HidppEvents) -----

        // Each device slot's battery feature index and which feature it is (0x1004 or 0x1000; the voltage and headset features
        // send no event), as found by the reads so far.
        public Dictionary<(string Group, byte Index), (byte Feature, int Battery)> BatteryIndices()
        {
            var indices = new Dictionary<(string, byte), (byte, int)>();
            foreach (var (slotKey, slot) in _known)
            {
                if (slot.Legacy) continue;
                int bar = slotKey.LastIndexOf('|');
                if (!byte.TryParse(slotKey[(bar + 1)..], out var index)) continue;
                if (slot.Features.TryGetValue(Hidpp.UnifiedBattery, out var unified) && unified != 0) indices[(slotKey[..bar], index)] = (unified, Hidpp.UnifiedBattery);
                else if (slot.Features.TryGetValue(Hidpp.BatteryStatus, out var status) && status != 0) indices[(slotKey[..bar], index)] = (status, Hidpp.BatteryStatus);
            }
            return indices;
        }

        // A battery event's reading for the device in a slot; null for a slot not known.
        public DeviceBattery? Event(string group, byte index, int battery, byte[] parameters)
        {
            if (!_known.TryGetValue(group + "|" + index, out var slot) || slot.Legacy || slot.Key.Length == 0) return null;
            var read = battery == Hidpp.UnifiedBattery ? Hidpp.Unified(parameters, slot.Capabilities) : Hidpp.Status(parameters);
            return Take(slot, read);
        }

        // A device whose link the receiver reports lost: its last reading, asleep and not charging (as Remembered).
        public DeviceBattery? LinkLost(string group, byte index)
            => _known.TryGetValue(group + "|" + index, out var slot) && slot.Last is { } last ? last with { Charging = false, Asleep = true } : null;

        // HID++ 1.0 devices: register 0x0D (mileage) or 0x07 (a level 1 to 7).
        private static DeviceBattery? Registers(Channel channel, byte index, HidInfo info)
        {
            BatteryRead? read = null;
            try { read = Hidpp.Mileage(channel.Register(index, 0x0D)); }
            catch (Exception error) when (error is TimeoutException or HidppError) { }
            if (read is null)
                try { read = Hidpp.Level(channel.Register(index, 0x07)); }
                catch (Exception error) when (error is TimeoutException or HidppError) { }
            if (read is not { } battery) return null;
            string name = index == 0xFF && info.ProductName.Length > 0 ? info.ProductName : "Logitech #" + index;
            return new DeviceBattery($"logitech:{info.Product:X4}:{index}", name, Devices.Named(name) ?? DeviceKind.Other, DeviceSource.Logitech, battery.Percent)
            { Charging = battery.Charging, Coarse = battery.Coarse };
        }

        private sealed class HidppError(int code) : Exception($"HID++ error 0x{code:X2}") { public int Code { get; } = code; }

        private sealed class Channel : IDisposable
        {
            private readonly HidDevice _long;
            private readonly List<HidDevice> _all = [];
            public Channel(HidInfo longInfo, HidInfo? shortInfo)
            {
                _long = new HidDevice(longInfo); _all.Add(_long);
                if (shortInfo is not null) try { _all.Add(new HidDevice(shortInfo)); } catch { }
            }
            public void Dispose() { foreach (var device in _all) device.Dispose(); }

            // A request; the reply's parameters, or TimeoutException, or HidppError.
            public byte[] Request(byte index, byte feature, int function, byte[] parameters, int timeoutMs = TimeoutMs)
            {
                var message = Hidpp.Request(index, feature, function, parameters);
                var result = Hid.Transact<object>(_all, () => _long.Write(message), report =>
                {
                    var (reply, error) = Hidpp.Match(report, index, feature, function);
                    return reply is not null ? reply : error is { } code ? new HidppError(code) : null;
                }, timeoutMs);
                return result switch { byte[] reply => reply, HidppError error => throw error, _ => throw new TimeoutException() };
            }

            // A HID++ 1.0 register read, sent short.
            public byte[] Register(byte index, byte register)
            {
                // HID++ 1.0 registers are read with short reports, which only the short collection takes.
                if (_all.Count < 2) throw new HidppError(0);
                var shortDevice = _all[1];
                var result = Hid.Transact<object>(_all, () => shortDevice.Write(Hidpp.RegisterRequest(index, register)), report =>
                {
                    if (report.Length < 6 || report[1] != index) return null;
                    if (report[2] == 0x81 && report[3] == register) return report[4..];
                    if (report[2] == 0x8F && report[3] == 0x81 && report[4] == register) return new HidppError(report[5]);
                    return null;
                }, TimeoutMs);
                return result switch { byte[] reply => reply, HidppError error => throw error, _ => throw new TimeoutException() };
            }
        }

        private sealed class Device(Channel channel, byte index, Slot slot)
        {
            public readonly Slot Slot = slot;

            // A feature's function; null when the device lacks the feature.
            public byte[]? Call(int feature, int function, params byte[] parameters)
            {
                if (!Slot.Features.TryGetValue(feature, out var featureIndex))
                    Slot.Features[feature] = featureIndex = channel.Request(index, 0x00, 0, Hidpp.FeatureParameters(feature))[0];
                return featureIndex == 0 ? null : channel.Request(index, featureIndex, function, parameters);
            }

            public string ReadName()
            {
                var count = Call(Hidpp.DeviceName, 0);
                if (count is null) return "";
                var name = new List<byte>();
                while (name.Count < count[0])
                {
                    var chunk = Call(Hidpp.DeviceName, 1, (byte)name.Count);
                    var part = (chunk ?? []).Take(count[0] - name.Count).TakeWhile(b => b != 0).ToArray();
                    if (part.Length == 0) break;
                    name.AddRange(part);
                }
                return System.Text.Encoding.UTF8.GetString([.. name]).Trim();
            }

            public DeviceKind ReadKind() => Call(Hidpp.DeviceName, 2) is { } reply ? Hidpp.Kind(reply[0]) : DeviceKind.Other;

            // 0x1004, then 0x1000, then 0x1001, then the headsets' 0x1F20.
            public BatteryRead? ReadBattery()
            {
                // 0x1004 capabilities do not change: asked once per device.
                if (Call(Hidpp.UnifiedBattery, 1) is { } status) return Hidpp.Unified(status, Slot.Capabilities ??= Call(Hidpp.UnifiedBattery, 0));
                if (Call(Hidpp.BatteryStatus, 0) is { } level) return Hidpp.Status(level);
                if (Call(Hidpp.BatteryVoltage, 0) is { } voltage) return Hidpp.Voltage(voltage);
                // A headset that is off answers without a valid measurement: no reading.
                if (Call(Hidpp.AdcMeasurement, 0) is { } adc) return Hidpp.Adc(adc);
                return null;
            }
        }
    }
}

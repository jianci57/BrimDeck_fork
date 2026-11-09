using BrimDeck.Core;

namespace BrimDeck.Native;

// Reads the Corsair receivers (Core's Corsair: the Dark Core RGB Pro SE, the Virtuoso SE and XT) on a thread of its own at
// below normal priority: their answer can take six seconds and is asked twice, which would hold every other device's reading
// if the battery worker (HidBatteries) waited for it. Each receiver is read at most once every DevicePolling.Slow, and at once
// when it arrives or the worker reads everything again (ReadNow); a reading is handed back to the worker, which lists it. A
// receiver that does not answer keeps the last reading it gave, asleep (it cannot tell a resting device from one switched
// off). Only get-property requests are sent. Handles of its own.
internal sealed class CorsairReader(Action<string, DeviceBattery> received) : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private IReadOnlyList<HidInfo> _wanted = [];
    private bool _stopped = true, _now;
    private Thread? _thread;
    // The reader's own: each device's last reading, by its key.
    private readonly Dictionary<string, DeviceBattery> _last = [];

    // The receivers' vendor collections, from the worker's latest pass. One that was not there before is read at once.
    public void Watch(IReadOnlyList<HidInfo> collections)
    {
        lock (_gate)
        {
            if (collections.Count == 0 && _thread is null) return;
            if (collections.Any(info => _wanted.All(known => known.Path != info.Path))) _now = true;
            _wanted = collections; _stopped = false;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "BrimDeck Corsair batteries" };
                _thread.Start();
            }
        }
        _wake.Set();
    }

    public void ReadNow() { lock (_gate) _now = true; _wake.Set(); }

    public void Stop()
    {
        lock (_gate) { _stopped = true; _wanted = []; }
        _wake.Set();
    }

    public void Dispose() => Stop();

    private void Run()
    {
        long at = -1;
        while (true)
        {
            IReadOnlyList<HidInfo> wanted;
            lock (_gate)
            {
                if (_stopped) { _thread = null; _last.Clear(); return; }
                bool due = _now || at < 0 || Environment.TickCount64 - at >= (long)DevicePolling.Slow.TotalMilliseconds;
                wanted = due ? _wanted : [];
                if (due) _now = false;
            }
            if (wanted.Count > 0)
            {
                at = Environment.TickCount64;
                var round = new HashSet<string>();
                foreach (var group in wanted.GroupBy(info => HidBatteries.GroupKey(info.Path)))
                {
                    if (Corsair.Find(group.First().Vendor, group.First().Product) is not { } model) continue;
                    var first = group.First();
                    // A headset's receiver and its cable share the key (the receiver's product), so they are one device.
                    string key = $"corsair:{first.Vendor:X4}:{(model.Receiver >= 0 ? model.Receiver : model.Product):X4}";
                    DeviceBattery? device = null;
                    try
                    {
                        if (Read(model, [.. group.OrderBy(info => Corsair.Collection(info.UsagePage) && info.Usage == 0x0001 ? 0 : 1)]) is { } battery)
                            device = new DeviceBattery(key, first.ProductName.Length > 0 ? first.ProductName : model.Name, model.Kind, DeviceSource.Hid, battery.Percent)
                                { Charging = battery.Charging, Hardware = Devices.Hardware(first.Vendor, first.Product) };
                    }
                    catch { }
                    device ??= _last.TryGetValue(key, out var last) ? last with { Charging = false, Asleep = true, Hardware = Devices.Hardware(first.Vendor, first.Product) } : null;
                    if (device is null) continue;
                    // A headset answering on its receiver and its cable in one round is one device: the reading that charges wins,
                    // and one that answered beats one asleep.
                    if (!round.Add(key) && _last.TryGetValue(key, out var kept) && (device.Asleep || kept.Charging && !kept.Asleep && !device.Charging)) continue;
                    _last[key] = device;
                    try { received(key, device); } catch { }
                }
                foreach (var gone in _last.Keys.Where(key => !round.Contains(key)).ToList()) _last.Remove(gone);
            }
            _wake.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    // One receiver: its collections in turn until one answers, within Corsair.BudgetMs. The level is asked, then asked again
    // to confirm (two different answers are no reading: one of them was another program's); the Virtuoso is asked its charge
    // state after.
    private static BatteryRead? Read(Corsair.Model model, List<HidInfo> collections)
    {
        long deadline = Environment.TickCount64 + Corsair.BudgetMs;
        int Left(int most) => (int)Math.Min(most, Math.Max(0, deadline - Environment.TickCount64));
        foreach (var info in collections)
        {
            if (Left(1) <= 0) break;
            using var device = new HidDevice(info) { StripZero = true };
            device.Flush();
            if (model.Session)
                foreach (var (property, target) in new[] { (Corsair.Firmware, Corsair.RouteReceiver), (Corsair.Heartbeat, Corsair.RouteReceiver), (Corsair.Heartbeat, Corsair.RouteDevice) })
                {
                    if (!Send(device, Corsair.Request(model, property, target))) break;
                    Drain(device, Left(800));
                }
            int? first = null;
            byte route = Corsair.RouteDevice;
            foreach (var each in Corsair.Routes(model))
            {
                route = each;
                if (!Send(device, Corsair.Request(model, Corsair.Level, route))) break;
                if ((first = Ask(device, Left(Corsair.AskMs), Corsair.Percent)) is not null) break;
            }
            if (first is null) continue;
            if (Send(device, Corsair.Request(model, Corsair.Level, route)) && Ask(device, Left(Corsair.ConfirmMs), Corsair.Percent) is int second && second != first) return null;
            bool charging = model.Bare && Send(device, Corsair.Request(model, Corsair.ChargeState, route))
                && Ask(device, Left(Corsair.ConfirmMs), reply => Corsair.Charging(reply) is { } state ? state ? 1 : 0 : null) == 1;
            return new(first, charging);
        }
        return null;
    }

    // A collection that refuses the request (not the receiver's protocol collection) is passed over.
    private static bool Send(HidDevice device, byte[] request)
    {
        try { device.Write(request); return true; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or TimeoutException) { return false; }
    }

    // The first report that parses, within the window; other reports (the receiver's notices, other answers) are passed over.
    private static int? Ask(HidDevice device, int windowMs, Func<byte[], int?> parse)
    {
        long deadline = Environment.TickCount64 + windowMs;
        while (Environment.TickCount64 < deadline)
        {
            if (device.Read((int)Math.Max(1, deadline - Environment.TickCount64)) is not { } report) return null;
            if (parse(report) is { } value) return value;
        }
        return null;
    }

    private static void Drain(HidDevice device, int windowMs)
    {
        long deadline = Environment.TickCount64 + windowMs;
        while (Environment.TickCount64 < deadline && device.Read((int)Math.Max(1, deadline - Environment.TickCount64)) is not null) { }
    }
}

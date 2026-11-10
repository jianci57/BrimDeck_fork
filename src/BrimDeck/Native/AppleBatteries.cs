using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Threading;
using BrimDeck.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace BrimDeck.Native;

// AirPods and Beats: each bud's and the case's charge, from Apple's proximity pairing advertisement (AppleAdvert), which they
// send while out of the case or with its lid open. A passive scan (no requests sent) runs only while the page or the collapsed
// reading shows and an AirPods or Beats device is connected over Bluetooth; only a strong signal (the buds next to the PC)
// whose family (AirPods or Beats) matches the connected device's name counts. The advertisement carries no address to match
// (it rotates), so a neighbour's AirPods of the same family right next to the PC could be read instead; the signal threshold
// keeps that to arm's length. Its entry takes the connected device's key, replacing the single level Windows has for it.
internal sealed class AppleBatteries(Dispatcher dispatcher, Action<DeviceSource, IReadOnlyList<DeviceBattery>> report) : IDeviceSource
{
    private const short StrongSignal = -60;
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    // A scan that the stack aborted (the radio switched off, the adapter reset) is started again after this, doubling up to
    // five minutes while it keeps failing.
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5), LastRetry = TimeSpan.FromMinutes(5);
    private BluetoothLEAdvertisementWatcher? _watcher;
    private DeviceDemand _demand;
    private DeviceBattery? _target;
    // The parts of earbuds, or the one level of a single-unit model (AirPods Max, the one-piece Beats).
    private (IReadOnlyList<PartBattery> Parts, BatteryRead? Single, DateTimeOffset At)? _last;
    private DispatcherTimer? _retry;
    private TimeSpan _backOff = FirstRetry;

    // It adds to what the Bluetooth source lists; it never lists a device of its own.
    public bool Listed => true;
    private DispatcherTimer? _stale;

    public void Start(DeviceDemand demand) { _demand = demand; Update(); }
    public void Stop() { _demand = DeviceDemand.None; _target = null; _last = null; Update(); report(DeviceSource.Apple, []); }
    public void Refresh() { }
    public void ReadNow() { }
    public void Dispose() => Stop();

    // Every list the Bluetooth source reports: whether an Apple device is connected, which the scan is for.
    // connected: what the Bluetooth source lists now (connected devices only), unmerged, so the AirPods' own entry never keeps
    // the scan going once they disconnect.
    public void Consider(IReadOnlyList<DeviceBattery> connected)
    {
        var target = connected.FirstOrDefault(device => device.Address is not null && IsApple(device.Name));
        if (target?.Key != _target?.Key) _last = null;
        _target = target is null ? null : target with { Source = DeviceSource.Bluetooth };
        Update();
    }
    private void Expire(object? sender, EventArgs e) { _stale?.Stop(); Publish(); }
    private static bool IsApple(string name) => name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || name.Contains("Beats", StringComparison.OrdinalIgnoreCase);

    private void Update()
    {
        bool wanted = _demand == DeviceDemand.Watch && _target is not null;
        if (!wanted) { _retry?.Stop(); _backOff = FirstRetry; }
        if (wanted && _watcher is null && _retry?.IsEnabled != true)
        {
            try
            {
                var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                // Apple's manufacturer data (company 0x004C, little endian) of the proximity pairing type (0x07, length 0x19)
                // only, so the stack drops the adverts of the user's own iPhone, Mac or Watch before they reach here.
                watcher.AdvertisementFilter.BytePatterns.Add(new BluetoothLEAdvertisementBytePattern(0xFF, 0,
                    new byte[] { (byte)AppleAdvert.Company, (byte)(AppleAdvert.Company >> 8), 0x07, 0x19 }.AsBuffer()));
                watcher.SignalStrengthFilter.InRangeThresholdInDBm = StrongSignal;
                watcher.Received += Received;
                watcher.Stopped += Stopped;
                watcher.Start();
                _watcher = watcher;
            }
            catch { _watcher = null; }
        }
        else if (!wanted && _watcher is not null) Drop();
        Publish();
    }

    private void Drop()
    {
        if (_watcher is null) return;
        _watcher.Received -= Received;
        _watcher.Stopped -= Stopped;
        try { _watcher.Stop(); } catch { }
        _watcher = null;
    }

    // Stopped by the stack rather than by Drop: started again after a while (Update), while it is still wanted.
    private void Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error == BluetoothError.Success) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (sender != _watcher) return;
            Drop();
            if (_demand != DeviceDemand.Watch || _target is null) return;
            _retry ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            _retry.Tick -= Retry; _retry.Tick += Retry;
            _retry.Interval = _backOff;
            _retry.Start();
            _backOff = TimeSpan.FromTicks(Math.Min(_backOff.Ticks * 2, LastRetry.Ticks));
        });
    }
    private void Retry(object? sender, EventArgs e) { _retry?.Stop(); Update(); }

    private void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.RawSignalStrengthInDBm < StrongSignal) return;
        foreach (var data in args.Advertisement.ManufacturerData)
        {
            if (data.CompanyId != AppleAdvert.Company) continue;
            var bytes = data.Data.ToArray();
            if (AppleAdvert.Parts(bytes) is not { } parts || AppleAdvert.Model(bytes) is not { } model) continue;
            var single = AppleAdvert.Single(bytes);
            if (parts.Count == 0 && single is null) continue;
            dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                // A scan that hears again has recovered: a later abort starts the back-off from the beginning.
                _backOff = FirstRetry;
                // The family must be the connected device's: AirPods for "Koooki's AirPods Pro", Beats for "Powerbeats Pro".
                string family = model.Contains("Beats", StringComparison.OrdinalIgnoreCase) || model.Contains("beats", StringComparison.Ordinal) ? "beats" : "AirPods";
                if (_target is not { } target || !target.Name.Contains(family, StringComparison.OrdinalIgnoreCase)) return;
                bool same = _last is { } last && last.Parts.SequenceEqual(parts) && last.Single == single;
                _last = (parts, single, DateTimeOffset.UtcNow);
                if (!same) Publish();
            });
        }
    }

    private void Publish()
    {
        // The buds go quiet in their closed case: the reading leaves once it is no longer fresh, without waiting for another.
        _stale?.Stop();
        if (_last is { } fresh && _demand == DeviceDemand.Watch && DateTimeOffset.UtcNow - fresh.At <= Fresh)
        {
            _stale ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            _stale.Tick -= Expire; _stale.Tick += Expire;
            _stale.Interval = Fresh - (DateTimeOffset.UtcNow - fresh.At) + TimeSpan.FromMilliseconds(50);
            _stale.Start();
        }
        if (_target is not { } target || _last is not { } last || DateTimeOffset.UtcNow - last.At > Fresh || _demand != DeviceDemand.Watch) { report(DeviceSource.Apple, []); return; }
        // A single-unit model shows its one level, without parts.
        if (last.Single is { } single)
        {
            report(DeviceSource.Apple, [target with { Source = DeviceSource.Apple, Percent = single.Percent, Parts = [], Coarse = true, Charging = single.Charging }]);
            return;
        }
        var buds = last.Parts.Where(part => part.Part != BatteryPart.Case && part.Percent is not null).ToList();
        report(DeviceSource.Apple, [target with
        {
            Source = DeviceSource.Apple, Percent = buds.Count > 0 ? buds.Min(part => part.Percent) : target.Percent, Parts = last.Parts, Coarse = true,
            Charging = buds.Count > 0 && buds.All(part => part.Charging)
        }]);
    }
}

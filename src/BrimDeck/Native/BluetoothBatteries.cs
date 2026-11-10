using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Threading;
using BrimDeck.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace BrimDeck.Native;

// Paired Bluetooth devices and the battery Windows itself reads for them, as Settings › Bluetooth shows it: the device node's
// DEVPKEY_Bluetooth_Battery ({104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2), which Windows keeps for hands-free headsets (the
// AT+IPHONEACCEV/+BIEV level on the HFP node) and for Bluetooth LE devices with a battery service (on the BTHLE node). Two
// watchers follow the paired devices (association endpoints, for the name, the kind and whether each is connected) and the
// Bluetooth device nodes (for the level). Windows does not tell when a node's level changes (BlueGauge's notes), so the
// connected devices' levels are read again from its cache every 5 seconds (DevicePolling.Cached), which reaches no device.
// Windows leaves the last level on a node after the device disconnects, so only connected devices are listed. Every connected
// LE device's own battery service (GattBattery) is followed too: its level where Windows has none, and whether it charges where
// the device tells it (Battery Level Status), which Windows never keeps.
internal sealed class BluetoothBatteries(Dispatcher dispatcher, Action<DeviceSource, IReadOnlyList<DeviceBattery>> report) : IDeviceSource
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string Connected = "System.Devices.Aep.IsConnected", Address = "System.Devices.Aep.DeviceAddress", Category = "System.Devices.Aep.Category",
        Appearance = "System.Devices.Aep.Bluetooth.Le.Appearance";
    private static readonly string[] EndpointProperties = [Connected, Address, Category, Appearance];
    // Categories that are not peripherals with a battery of their own worth listing.
    private static readonly string[] Ignored = ["Communication.Phone", "Computers", "Computer", "Display", "Network"];

    private sealed record Endpoint(string Id, string Name, string? Category, int? Appearance, bool Connected, bool LowEnergy);
    private readonly Dictionary<string, Endpoint> _endpoints = [];
    // Device node id → (address, level).
    private readonly Dictionary<string, (string Address, int? Level)> _nodes = [];
    private readonly Dictionary<string, GattBattery> _gatt = [];
    private readonly List<DeviceWatcher> _watchers = [];
    private DispatcherTimer? _poll;
    private DeviceDemand _demand;
    private bool _nodesListed;
    private int _generation;

    // Every watcher that started has listed what there is.
    public bool Listed => _listed >= _watchers.Count;
    private int _listed;

    public void Start(DeviceDemand demand)
    {
        bool running = _watchers.Count > 0;
        _demand = demand;
        if (!running)
        {
            int generation = ++_generation;
            Watch(BluetoothDevice.GetDeviceSelectorFromPairingState(true), EndpointProperties, DeviceInformationKind.AssociationEndpoint, generation, lowEnergy: false);
            Watch(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), EndpointProperties, DeviceInformationKind.AssociationEndpoint, generation, lowEnergy: true);
            Watch("System.Devices.DeviceInstanceId:~<\"BTH\"", [BatteryKey], DeviceInformationKind.Device, generation, lowEnergy: null);
        }
        // The page and the collapsed reading poll the GATT levels.
        if (demand == DeviceDemand.Watch)
        {
            _poll ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = DevicePolling.Cached };
            _poll.Tick -= PollTick; _poll.Tick += PollTick; _poll.Start();
        }
        else _poll?.Stop();
        Publish();
    }

    public void Stop()
    {
        _generation++;
        foreach (var watcher in _watchers)
        {
            try { if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop(); } catch { }
        }
        _watchers.Clear();
        _poll?.Stop();
        foreach (var gatt in _gatt.Values) gatt.Dispose();
        _gatt.Clear(); _endpoints.Clear(); _nodes.Clear(); _nodesListed = false; _listed = 0;
        report(DeviceSource.Bluetooth, []); report(DeviceSource.Gatt, []);
    }

    // The page opened: Windows' levels at once, the GATT ones that send no notices as their minute allows.
    public void Refresh() => ReadAll(force: false);
    public void ReadNow() => ReadAll(force: true);

    // Windows' own levels for the connected devices (a property read from its cache, about half a millisecond each), and the
    // GATT values whose devices send no notices, once a minute or (force) now.
    private void ReadAll(bool force = false)
    {
        if (_demand != DeviceDemand.Watch) return;
        foreach (var gatt in _gatt.Values) gatt.Poll(force);
        var connected = _endpoints.Values.Where(endpoint => endpoint.Connected).Select(endpoint => Devices.AddressIn(endpoint.Id)).ToHashSet();
        int generation = _generation;
        foreach (var (id, node) in _nodes.Where(node => node.Value.Level is not null && connected.Contains(node.Value.Address)).ToList()) Reread(id, generation);
    }
    private async void Reread(string id, int generation)
    {
        Dictionary<string, object?>? properties = null;
        try { properties = Copy((await DeviceInformation.CreateFromIdAsync(id, [BatteryKey], DeviceInformationKind.Device)).Properties); }
        catch { }
        await dispatcher.InvokeAsync(() => { if (properties is not null && generation == _generation && _nodes.ContainsKey(id)) Node(id, properties, update: true); });
    }
    private void PollTick(object? sender, EventArgs e) => ReadAll();

    private void Watch(string aqs, string[] properties, DeviceInformationKind kind, int generation, bool? lowEnergy)
    {
        DeviceWatcher watcher;
        try { watcher = DeviceInformation.CreateWatcher(aqs, properties, kind); }
        catch { return; }
        void Post(Action action) => dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (generation == _generation) action(); });
        bool node = lowEnergy is null;
        watcher.Added += (_, info) => { var id = info.Id; var name = info.Name; var props = Copy(info.Properties); Post(() => { if (node) Node(id, props); else Add(id, name, props, lowEnergy == true); }); };
        watcher.Updated += (_, update) => { var id = update.Id; var props = Copy(update.Properties); Post(() => { if (node) Node(id, props, update: true); else Update(id, props); }); };
        watcher.Removed += (_, update) => { var id = update.Id; Post(() => { if (node) _nodes.Remove(id); else _endpoints.Remove(id); Publish(); }); };
        watcher.EnumerationCompleted += (_, _) => Post(() => { if (node) _nodesListed = true; _listed++; Publish(); });
        try { watcher.Start(); _watchers.Add(watcher); } catch { }
    }
    private static Dictionary<string, object?> Copy(IReadOnlyDictionary<string, object> properties) => properties.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);

    private void Add(string id, string name, Dictionary<string, object?> properties, bool lowEnergy)
    {
        _endpoints[id] = new Endpoint(id, name, Text(properties, Category), properties.TryGetValue(Appearance, out var a) && a is ushort raw ? raw : null,
            properties.TryGetValue(Connected, out var c) && c is true, lowEnergy);
        Publish();
    }
    private void Update(string id, Dictionary<string, object?> properties)
    {
        if (!_endpoints.TryGetValue(id, out var known)) return;
        if (properties.TryGetValue(Connected, out var c)) known = known with { Connected = c is true };
        if (properties.TryGetValue(Category, out _)) known = known with { Category = Text(properties, Category) };
        _endpoints[id] = known;
        Publish();
    }
    private void Node(string id, Dictionary<string, object?> properties, bool update = false)
    {
        if (Devices.AddressIn(id) is not { } address) return;
        bool has = properties.TryGetValue(BatteryKey, out var value);
        int? level = has && value is byte b ? b : has && value is int i ? i : null;
        // An update that does not carry the level keeps the one known.
        if (update && !has && _nodes.TryGetValue(id, out var known)) level = known.Level;
        if (level is null && !_nodes.ContainsKey(id) && !update) { _nodes[id] = (address, null); return; }
        if (_nodes.TryGetValue(id, out var was) && was == (address, level)) return;
        _nodes[id] = (address, level);
        Publish();
    }
    private static string? Text(Dictionary<string, object?> properties, string key) => properties.TryGetValue(key, out var value) ? value switch
    {
        string text => text, string[] list => string.Join(",", list), _ => null
    } : null;

    private void Publish()
    {
        var plain = new List<DeviceBattery>();
        var gatt = new List<DeviceBattery>();
        var wanted = new HashSet<string>();
        foreach (var endpoint in _endpoints.Values)
        {
            if (!endpoint.Connected || endpoint.Category is { } category && Ignored.Any(ignored => category.Contains(ignored, StringComparison.OrdinalIgnoreCase))) continue;
            string? address = Devices.AddressIn(endpoint.Id);
            if (address is null) continue;
            int? level = _nodes.Values.Where(node => node.Address == address && node.Level is not null).Select(node => node.Level).FirstOrDefault();
            string? hardware = _nodes.Where(node => node.Value.Address == address).Select(node => Devices.HardwareIn(node.Key)).FirstOrDefault(found => found is not null);
            var kind = Devices.Kind(endpoint.Category, endpoint.Appearance, endpoint.Name);
            // A connected LE device is followed through its own battery service once the nodes are listed: its level where Windows
            // has none, and whether it charges where it tells.
            bool charging = false;
            if (endpoint.LowEnergy && _nodesListed && _demand == DeviceDemand.Watch)
            {
                wanted.Add(address);
                if (!_gatt.TryGetValue(address, out var reader))
                {
                    reader = new GattBattery(ulong.Parse(address, System.Globalization.NumberStyles.HexNumber), dispatcher, () => Publish());
                    _gatt[address] = reader; reader.Open();
                }
                charging = reader.Charging == true;
                if (level is null)
                {
                    if (reader.Level is { } read)
                    {
                        gatt.Add(new DeviceBattery("bt:" + address, endpoint.Name, kind, DeviceSource.Gatt, Devices.Clamp(read)) { Address = address, Hardware = hardware, Charging = charging });
                        continue;
                    }
                    if (!reader.Done) continue;
                }
            }
            // A device that reports nothing shows as not available, unless it is not a kind worth listing.
            if (level is null && kind == DeviceKind.Other) continue;
            plain.Add(new DeviceBattery("bt:" + address, endpoint.Name, kind, DeviceSource.Bluetooth, level is { } l ? Devices.Clamp(l) : null)
                { Address = address, Hardware = hardware, Charging = charging });
        }
        foreach (var gone in _gatt.Keys.Where(address => !wanted.Contains(address)).ToList()) { _gatt[gone].Dispose(); _gatt.Remove(gone); }
        report(DeviceSource.Bluetooth, plain);
        report(DeviceSource.Gatt, gatt);
    }

    public void Dispose() => Stop();

    // One LE device's battery service: its level (0x2A19) and, where the device has it, its Battery Level Status (Battery Service
    // 1.1, whose power state says whether it charges), each read once and then followed through its notices, as BlueZ, Android
    // and BlueGauge follow the level. Asking for the notices is the one thing written (the characteristics' client configuration,
    // which every Bluetooth host writes; BleBatterySubscribe writes the same so that Windows' own reading stays current). A value
    // whose device cannot send notices is read again once a minute at most (DevicePolling.Gatt): each read reaches the device.
    // Done once the first read has finished, with or without a level.
    private sealed class GattBattery(ulong address, Dispatcher dispatcher, Action changed) : IDisposable
    {
        private BluetoothLEDevice? _device;
        private GattDeviceService? _service;
        private GattCharacteristic? _level, _status;
        private bool _levelNotifies, _statusNotifies, _busy, _disposed;
        private long _readAt;
        public int? Level { get; private set; }
        public bool? Charging { get; private set; }
        public bool Done { get; private set; }

        public async void Open()
        {
            if (_busy || _disposed) return;
            _busy = true;
            try
            {
                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                if (_device is not null && !_disposed)
                {
                    var services = await _device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached);
                    if (services.Status == GattCommunicationStatus.Success && services.Services.Count > 0)
                    {
                        _service = services.Services[0];
                        foreach (var other in services.Services.Skip(1)) other.Dispose();
                        var characteristics = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Cached);
                        if (characteristics.Status == GattCommunicationStatus.Success)
                        {
                            _level = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == GattCharacteristicUuids.BatteryLevel);
                            _status = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == BatteryService.LevelStatus);
                        }
                    }
                }
                if (_level is not null && !_disposed) _levelNotifies = await Follow(_level);
                if (_status is not null && !_disposed) _statusNotifies = await Follow(_status);
                await Read(all: true);
            }
            catch { }
            await dispatcher.InvokeAsync(() =>
            {
                _busy = false;
                if (_disposed) { Close(); return; }
                Done = true;
                changed();
            });
        }

        // Notices asked for, where the characteristic sends them; true when the device took the request.
        private async Task<bool> Follow(GattCharacteristic characteristic)
        {
            var properties = characteristic.CharacteristicProperties;
            var mode = properties.HasFlag(GattCharacteristicProperties.Notify) ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : properties.HasFlag(GattCharacteristicProperties.Indicate) ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                : GattClientCharacteristicConfigurationDescriptorValue.None;
            if (mode == GattClientCharacteristicConfigurationDescriptorValue.None) return false;
            characteristic.ValueChanged += ValueChanged;
            try { return await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(mode) == GattCommunicationStatus.Success; }
            catch { return false; }
        }

        private void ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var value = args.CharacteristicValue.ToArray();
            bool level = sender.Uuid == GattCharacteristicUuids.BatteryLevel;
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => Take(level, value));
        }

        // The values whose devices send no notices, once a minute or (force) now.
        public async void Poll(bool force)
        {
            if (!Done || _busy || _disposed) return;
            bool quiet = _level is not null && !_levelNotifies || _status is not null && !_statusNotifies;
            if (!quiet || !force && Environment.TickCount64 - _readAt < (long)DevicePolling.Gatt.TotalMilliseconds) return;
            _busy = true;
            try { await Read(all: false); } catch { }
            _busy = false;
            if (_disposed) Close();
        }

        private async Task Read(bool all)
        {
            _readAt = Environment.TickCount64;
            foreach (var (characteristic, notifies, level) in new[] { (_level, _levelNotifies, true), (_status, _statusNotifies, false) })
            {
                if (characteristic is null || notifies && !all || _disposed) continue;
                var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (result.Status == GattCommunicationStatus.Success && result.Value is { } value)
                {
                    var bytes = value.ToArray();
                    await dispatcher.InvokeAsync(() => Take(level, bytes));
                }
            }
        }

        private void Take(bool level, byte[] value)
        {
            if (_disposed) return;
            int? newLevel = Level;
            bool? newCharging = Charging;
            if (level) { if (value.Length > 0) newLevel = value[0]; }
            else newCharging = BatteryService.Charging(value);
            if (newLevel == Level && newCharging == Charging) return;
            Level = newLevel; Charging = newCharging;
            if (Done) changed();
        }

        public void Dispose()
        {
            _disposed = true;
            if (!_busy) Close();
        }

        private void Close()
        {
            try
            {
                if (_level is not null) _level.ValueChanged -= ValueChanged;
                if (_status is not null) _status.ValueChanged -= ValueChanged;
                _service?.Dispose(); _device?.Dispose();
            }
            catch { }
            _level = _status = null; _service = null; _device = null;
        }
    }
}


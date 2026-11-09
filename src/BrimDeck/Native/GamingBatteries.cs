using System.Windows.Threading;
using BrimDeck.Core;
using Windows.Gaming.Input;

namespace BrimDeck.Native;

// Game controllers through Windows.Gaming.Input: Xbox controllers on the Xbox Wireless adapter or a cable, and others Windows
// gives a battery report for (RawGameController.TryGetBatteryReport: remaining and full capacity, and whether it charges).
// Only while the page or the collapsed reading shows (DeviceDemand.Watch): the reports carry no event, so they are read when
// a controller arrives and every 15 seconds (DevicePolling.Ask). A controller that is also a connected Bluetooth device merges with that entry by
// its vendor and product (DeviceRoster).
internal sealed class GamingBatteries(Dispatcher dispatcher, Action<DeviceSource, IReadOnlyList<DeviceBattery>> report) : IDeviceSource
{
    private DispatcherTimer? _poll;
    private bool _running;
    // Read at once when it starts; listening only, it reads nothing.
    public bool Listed => true;

    public void Start(DeviceDemand demand)
    {
        if (demand != DeviceDemand.Watch) { Stop(); return; }
        if (_running) return;
        _running = true;
        try
        {
            RawGameController.RawGameControllerAdded += Changed;
            RawGameController.RawGameControllerRemoved += Changed;
        }
        catch { }
        _poll ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = DevicePolling.Ask };
        _poll.Tick -= Tick; _poll.Tick += Tick; _poll.Start();
        Read();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try
        {
            RawGameController.RawGameControllerAdded -= Changed;
            RawGameController.RawGameControllerRemoved -= Changed;
        }
        catch { }
        _poll?.Stop();
        report(DeviceSource.Gaming, []);
    }

    public void Refresh() { if (_running) Read(); }
    public void ReadNow() => Refresh();
    private void Tick(object? sender, EventArgs e) => Read();
    private void Changed(object? sender, RawGameController controller) => dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (_running) Read(); });

    private void Read()
    {
        var devices = new List<DeviceBattery>();
        try
        {
            foreach (var controller in RawGameController.RawGameControllers)
            {
                if (!controller.IsWireless) continue;
                var battery = controller.TryGetBatteryReport();
                if (battery is null || battery.Status == Windows.System.Power.BatteryStatus.NotPresent) continue;
                int? percent = battery.RemainingCapacityInMilliwattHours is { } remaining && battery.FullChargeCapacityInMilliwattHours is { } full && full > 0
                    ? Devices.Clamp((int)Math.Round(remaining * 100.0 / full)) : null;
                string hardware = Devices.Hardware(controller.HardwareVendorId, controller.HardwareProductId);
                string name = controller.DisplayName is { Length: > 0 } display ? display : Loc.T("游戏手柄", "Game controller");
                devices.Add(new DeviceBattery("gip:" + (controller.NonRoamableId ?? hardware), name, DeviceKind.Controller, DeviceSource.Gaming, percent)
                {
                    Charging = battery.Status == Windows.System.Power.BatteryStatus.Charging, Hardware = hardware
                });
            }
        }
        catch { }
        report(DeviceSource.Gaming, devices);
    }

    public void Dispose() => Stop();
}

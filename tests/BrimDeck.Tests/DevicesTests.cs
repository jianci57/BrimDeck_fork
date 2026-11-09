using BrimDeck.Core;

// The devices' batteries, core: what a device is by its Bluetooth category, appearance or name; its address and its vendor and
// product from Windows' ids; one entry per device across sources; and the levels.
static class DevicesTests
{
    public static void Run(Action<string, bool> check)
    {
        Kinds(check);
        Ids(check);
        Roster(check);
        Levels(check);
    }

    private static void Kinds(Action<string, bool> check)
    {
        check("Devices: Windows' Bluetooth categories give the kind", Devices.Kind("Input.Mouse", null, "MCHOSE A7 V2 Ultra+") == DeviceKind.Mouse
            && Devices.Kind("Input.Gaming.Gamepad", 964, "Xbox Wireless Controller") == DeviceKind.Controller
            && Devices.Kind("Input.Keyboard", null, "F99") == DeviceKind.Keyboard
            && Devices.Kind("Communication.Headset.Bluetooth", null, "WH-1000XM4") == DeviceKind.Headphones);
        check("Devices: a headset whose name says earbuds is earbuds", Devices.Kind("Communication.Headset.Bluetooth", null, "OPPO Enco X3") == DeviceKind.Earbuds
            && Devices.Kind("Communication.Headset.Bluetooth", null, "AirPods Pro") == DeviceKind.Earbuds);
        check("Devices: the LE appearance gives the kind", Devices.Kind(null, 0x03C2, "x") == DeviceKind.Mouse && Devices.Kind(null, 0x03C1, "x") == DeviceKind.Keyboard
            && Devices.Kind(null, 0x03C4, "x") == DeviceKind.Controller && Devices.Kind(null, 0x03C7, "x") == DeviceKind.Pen && Devices.Kind(null, 0x0941, "x") == DeviceKind.Earbuds
            && Devices.Kind(null, 0x0943, "x") == DeviceKind.Headphones);
        check("Devices: names give the kind in Chinese and English", Devices.Named("罗技无线鼠标") == DeviceKind.Mouse && Devices.Named("Logitech MX Keys") == DeviceKind.Keyboard
            && Devices.Named("Surface Pen") == DeviceKind.Pen && Devices.Named("DualSense Wireless Controller") == DeviceKind.Controller && Devices.Named("Bose QC45") == DeviceKind.Headphones);
        check("Devices: a word inside another word is not a kind", Devices.Named("OpenRGB hub") is null && Devices.Named("联想笔记本") is null && Devices.Kind(null, null, "F99 MAX Dongle") == DeviceKind.Other);
    }

    private static void Ids(Action<string, bool> check)
    {
        check("Devices: the address from a BTHLE node, a hands-free node and an endpoint",
            Devices.AddressIn(@"BTHLE\Dev_cd5994078968\7&2dd3ebc8&0&cd5994078968") == "CD5994078968"
            && Devices.AddressIn(@"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0d58\7&8ee6b29&0&8099E7C236A8_C00000000") == "8099E7C236A8"
            && Devices.AddressIn("Bluetooth#Bluetooth3c:91:80:12:34:56-cd:59:94:07:89:68") == "CD5994078968");
        check("Devices: no address where there is none", Devices.AddressIn(@"BTH\MS_BTHLE\6&19f74fd8&0&3") is null && Devices.AddressIn(null) is null);
        check("Devices: vendor and product from LE, classic and USB ids",
            Devices.HardwareIn(@"BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02045e_PID&0b13_REV&0517_0c3526eaff88\8&1") == "045E:0B13"
            && Devices.HardwareIn(@"BTHENUM\{0000111e}_VID&0002054c_PID&0d58\7&8") == "054C:0D58"
            && Devices.HardwareIn(@"HID\VID_046D&PID_C52B&MI_02&Col01\8&1") == "046D:C52B" && Devices.Hardware(0x45E, 0xB13) == "045E:0B13");
    }

    private static DeviceBattery Bt(string address, int? percent, string name = "Mouse", DeviceKind kind = DeviceKind.Mouse) =>
        new("bt:" + address, name, kind, DeviceSource.Bluetooth, percent) { Address = address };

    private static void Roster(Action<string, bool> check)
    {
        var roster = new DeviceRoster();
        check("Devices: a source's list fills the roster", roster.Set(DeviceSource.Bluetooth, [Bt("AA", 50), Bt("BB", null, "Buds", DeviceKind.Earbuds)]) && roster.All.Count == 2);
        check("Devices: the same list again changes nothing", !roster.Set(DeviceSource.Bluetooth, [Bt("AA", 50), Bt("BB", null, "Buds", DeviceKind.Earbuds)]));
        // A new level, or charging starting, of a device already listed reaches the page: the roster changes and hands out a new list.
        var before = roster.All;
        check("Devices: a new level of a listed device is a change, in a new list", roster.Set(DeviceSource.Bluetooth, [Bt("AA", 48), Bt("BB", null, "Buds", DeviceKind.Earbuds)])
            && !ReferenceEquals(before, roster.All) && roster.All.Single(d => d.Key == "bt:AA").Percent == 48
            && roster.Set(DeviceSource.Bluetooth, [Bt("AA", 48) with { Charging = true }, Bt("BB", null, "Buds", DeviceKind.Earbuds)]) && roster.All.Single(d => d.Key == "bt:AA").Charging);
        roster.Set(DeviceSource.Bluetooth, [Bt("AA", 50), Bt("BB", null, "Buds", DeviceKind.Earbuds)]);
        // GATT reads the earbuds Windows has no level for: one entry, the reading's, under the Bluetooth key.
        roster.Set(DeviceSource.Gatt, [new DeviceBattery("bt:BB", "Buds", DeviceKind.Earbuds, DeviceSource.Gatt, 70) { Address = "BB" }]);
        check("Devices: two sources of one device make one entry with the reading", roster.All.Count == 2 && roster.All.Single(d => d.Key == "bt:BB").Percent == 70);
        // A vendor source (Logitech) keys by its own id but knows the address: it replaces the plain Bluetooth reading.
        roster.Set(DeviceSource.Logitech, [new DeviceBattery("hidpp:1", "MX Master 3", DeviceKind.Mouse, DeviceSource.Logitech, 85) { Address = "AA", Charging = true }]);
        var aa = roster.All.Single(d => d.Key == "bt:AA");
        check("Devices: the more exact source wins and the Bluetooth key stays", roster.All.Count == 2 && aa.Source == DeviceSource.Logitech && aa.Percent == 85 && aa.Charging && aa.Name == "MX Master 3");
        roster.Set(DeviceSource.Gaming, [new DeviceBattery("gip:x", "Xbox", DeviceKind.Controller, DeviceSource.Gaming, 40) { Hardware = "045E:0B13" }]);
        roster.Set(DeviceSource.Bluetooth, [Bt("AA", 50), Bt("BB", null, "Buds", DeviceKind.Earbuds), Bt("CC", 100, "Xbox Wireless Controller", DeviceKind.Controller) with { Hardware = "045E:0B13" }]);
        check("Devices: a controller seen by Windows.Gaming.Input and Bluetooth is one", roster.All.Count(d => d.Kind == DeviceKind.Controller) == 1);
        check("Devices: the order is by kind, then name", roster.All.Select(d => d.Kind).SequenceEqual([DeviceKind.Mouse, DeviceKind.Earbuds, DeviceKind.Controller]));
        roster.Set(DeviceSource.Logitech, []);
        var two = DeviceRoster.Merge([
            new DeviceBattery("ps:1", "DualSense", DeviceKind.Controller, DeviceSource.Sony, 40) { Hardware = "054C:0CE6" },
            new DeviceBattery("ps:2", "DualSense", DeviceKind.Controller, DeviceSource.Sony, 90) { Hardware = "054C:0CE6" },
            Bt("X1", null, "DualSense Wireless Controller", DeviceKind.Controller) with { Hardware = "054C:0CE6" },
            Bt("X2", null, "DualSense Wireless Controller", DeviceKind.Controller) with { Hardware = "054C:0CE6" }]);
        check("Devices: two controllers of one model stay two each, not one", two.Count == 4);
        var chain = DeviceRoster.Merge([Bt("AA", null), new DeviceBattery("logitech:1", "M", DeviceKind.Mouse, DeviceSource.Logitech, 50) { Address = "AA" },
            new DeviceBattery("logitech:1", "M", DeviceKind.Mouse, DeviceSource.Hid, 49)]);
        check("Devices: entries linked through another are one, under the Bluetooth key", chain.Count == 1 && chain[0].Key == "bt:AA" && chain[0].Percent == 50);
        check("Devices: a source that empties leaves the others", roster.All.Single(d => d.Key == "bt:AA").Source == DeviceSource.Bluetooth);
        var parts = new DeviceBattery("bt:DD", "AirPods", DeviceKind.Earbuds, DeviceSource.Apple, 90)
        { Parts = [new(BatteryPart.Left, 80, false), new(BatteryPart.Right, 60, false), new(BatteryPart.Case, 10, false)] };
        check("Devices: earbuds' lowest is the lower bud, not the case", parts.Lowest == 60);
        check("Devices: a device that reports nothing has no lowest", Bt("EE", null).Lowest is null);
    }

    private static void Levels(Action<string, bool> check)
    {
        check("Devices: levels at the low and the warning thresholds", Devices.Level(15, false, 25, 15) == MemoryLevel.Critical && Devices.Level(16, false, 25, 15) == MemoryLevel.Elevated
            && Devices.Level(25, false, 25, 15) == MemoryLevel.Elevated && Devices.Level(26, false, 25, 15) == MemoryLevel.Normal);
        check("Devices: a charging or unreported battery is never low", Devices.Level(3, true, 25, 15) == MemoryLevel.Normal && Devices.Level(null, false, 25, 15) == MemoryLevel.Normal);
        check("Devices: figures", Devices.Figure(42) == "42%" && Devices.Figure(null) == "—" && Devices.Figure(130) == "100%");
    }
}

using BrimDeck.Core;

// The vendor protocols against the bytes they are documented with: requests byte for byte (with their checksums), and replies
// read as the devices send them, other reports passed over.
static class DeviceProtocolTests
{
    public static void Run(Action<string, bool> check)
    {
        Logitech(check);
        RazerReports(check);
        DongleFrames(check);
        MoreDongles(check);
        AsusAndSteelSeries(check);
        Pushed(check);
        Corsairs(check);
        Omni(check);
        RapooFirst(check);
        HeadsetReplies(check);
        Controllers(check);
        Usages(check);
        Apple(check);
    }

    private static byte[] Long(byte device, byte feature, int function, params byte[] parameters)
    {
        var report = new byte[20];
        report[0] = 0x11; report[1] = device; report[2] = feature; report[3] = (byte)((function << 4) | Hidpp.SoftwareId);
        parameters.CopyTo(report, 4);
        return report;
    }

    private static void Logitech(Action<string, bool> check)
    {
        check("HID++: a long request, padded", Hidpp.Request(1, 0x00, 1, 0, 0, 0x5A).SequenceEqual(new byte[] { 0x11, 1, 0, 0x1E, 0, 0, 0x5A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        check("HID++: the reply to its request", Hidpp.Match(Long(1, 0x08, 1, 55, 4, 1), 1, 0x08, 1).Reply is [55, 4, 1, ..]);
        check("HID++: another device's or another function's report is not the reply", Hidpp.Match(Long(2, 0x08, 1, 55), 1, 0x08, 1) == (null, null)
            && Hidpp.Match(Long(1, 0x08, 0, 55), 1, 0x08, 1) == (null, null));
        check("HID++: a 2.0 error", Hidpp.Match([0x11, 1, 0xFF, 0x08, 0x1E, 0x05, 0, 0], 1, 0x08, 1).Error == 0x05);
        check("HID++: a 1.0 error (the device speaks only 1.0)", Hidpp.Match([0x10, 1, 0x8F, 0x00, 0x1E, 0x01, 0], 1, 0x00, 1).Error == Hidpp.InvalidSubId);
        check("HID++: an event (software id 0) is not a reply", Hidpp.Match([0x11, 1, 0x08, 0x00, 50, 0, 0], 1, 0x08, 0) == (null, null));
        check("HID++: receivers hold six devices, a cable one", Hidpp.Indices(Hidpp.IsReceiver(0xC52B, "USB Receiver")).Count == 6 && Hidpp.Indices(Hidpp.IsReceiver(0xC08B, "G502 HERO")).SequenceEqual(new byte[] { 0xFF })
            && Hidpp.IsReceiver(0xC548, "") && Hidpp.IsReceiver(0x1234, "LIGHTSPEED Receiver"));
        check("HID++: 0x1004 with a state of charge, charging", Hidpp.Unified([55, 4, 1, 0], [0x0F, 0x03]) == new BatteryRead(55, true, false));
        check("HID++: 0x1004 without one maps its steps", Hidpp.Unified([0, 2, 0, 0], [0x0F, 0x01]) == new BatteryRead(20, false, true)
            && Hidpp.Unified([0, 8, 3, 0], [0x0F, 0x01]) == new BatteryRead(100, true, true));
        check("HID++: 0x1000 level and status", Hidpp.Status([70, 50, 0]) == new BatteryRead(70) && Hidpp.Status([90, 50, 1]).Charging && Hidpp.Status([0, 0, 3]).Percent == 100
            && Hidpp.Status([0, 0, 0]).Percent is null);
        // The events as a G502 X LIGHTSPEED on a C547 receiver sent them, the cable put in and pulled out (slot 1, 0x1004 at index 6).
        byte[] plugged = [0x11, 0x01, 0x06, 0x00, 0x5A, 0x08, 0x01, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        check("HID++: a battery event is read as its feature's status reply",
            Hidpp.Event(plugged) is { Device: 1, Feature: 6, Event: 0 } e && Hidpp.Unified(e.Parameters, [0x0F, 0x03]) == new BatteryRead(90, true)
            && Hidpp.Unified(Hidpp.Event([0x11, 0x01, 0x06, 0x00, 0x5A, 0x08, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])!.Value.Parameters, [0x0F, 0x03]) == new BatteryRead(90));
        check("HID++: a reply to a program (software id), a short report and a HID++ 1.0 notice are not events",
            Hidpp.Event([0x11, 0x01, 0x06, 0x1E, 0x5A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]) is null && Hidpp.Event([0x10, 0x01, 0x06, 0x00, 0, 0, 0]) is null
            && Hidpp.Event([0x11, 0x01, 0x41, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]) is null);
        check("HID++: a 0x41 notice, link made or lost by bit 0x40", Hidpp.Connection([0x10, 0x02, 0x41, 0x04, 0x01, 0x47, 0x40]) == (2, true)
            && Hidpp.Connection([0x10, 0x02, 0x41, 0x04, 0x41, 0x47, 0x40]) == (2, false) && Hidpp.Connection([0x10, 0x02, 0x81, 0x00, 0, 1, 0]) is null);
        check("HID++: the receiver's notification flags, read and written back with the wireless bit",
            Hidpp.Flags([0x10, 0xFF, 0x81, 0x00, 0x00, 0x01, 0x00]) is [0, 1, 0] && Hidpp.Flags([0x10, 0x01, 0x81, 0x00, 0, 1, 0]) is null
            && Hidpp.RegisterWrite(0xFF, Hidpp.NotificationFlags, 0x10, 0x01, 0x00).SequenceEqual(new byte[] { 0x10, 0xFF, 0x80, 0x00, 0x10, 0x01, 0x00 }));
        check("Battery Level Status: the charge state, else wired power",
            BatteryService.Charging([0x00, 0x20, 0x00]) == true && BatteryService.Charging([0x00, 0x40, 0x00]) == false && BatteryService.Charging([0x00, 0x60, 0x00]) == false
            && BatteryService.Charging([0x00, 0x02, 0x00]) == true && BatteryService.Charging([0x00, 0x00, 0x00]) is null && BatteryService.Charging([0x00]) is null);
        check("HID++: 0x1001 millivolts big endian on the curve", Hidpp.Voltage([0x0F, 0x50, 0x00]) is { Percent: 70, Charging: false, Coarse: true } && Hidpp.Voltage([0x10, 0x5A, 0x80]) is { Percent: 100, Charging: true });
        check("HID++: the voltage curve's ends and a point between", Hidpp.VoltageToPercent(4300) == 100 && Hidpp.VoltageToPercent(3400) == 0 && Hidpp.VoltageToPercent(3811) == 50);
        check("HID++: 0x1F20 headsets, full and charging by bit", Hidpp.Adc([0x0F, 0xA0, 0x07]) is { Percent: 100, Charging: true } && Hidpp.Adc([0x0F, 0xA0, 0x01]) is { Charging: false, Coarse: true }
            && Hidpp.Adc([0x0F, 0x50, 0x03]) is { Percent: 70, Charging: true });
        check("HID++: 0x1F20 without a valid measurement (the headset off) is no reading, not 0 %", Hidpp.Adc([0, 0, 0]) is null && Hidpp.Adc([0x0F, 0xA0, 0x02]) is null);
        check("HID++ 1.0: register 0x0D and 0x07", Hidpp.Mileage([80, 0, 0x40]) == new BatteryRead(80, true) && Hidpp.Level([5, 0x00]) is { Percent: 50, Coarse: true } && Hidpp.Level([3, 0x22]).Percent == 100
            && Hidpp.RegisterRequest(2, 0x0D).SequenceEqual(new byte[] { 0x10, 2, 0x81, 0x0D, 0, 0, 0 }));
        check("HID++: device types", Hidpp.Kind(0) == DeviceKind.Keyboard && Hidpp.Kind(3) == DeviceKind.Mouse && Hidpp.Kind(5) == DeviceKind.Mouse && Hidpp.Kind(8) == DeviceKind.Headphones && Hidpp.Kind(7) == DeviceKind.Other);
    }

    private static void RazerReports(Action<string, bool> check)
    {
        var request = Razer.Request(0x1F, Razer.Battery);
        check("Razer: the battery request as razer-battery-report encodes it", request.Length == 90 && request.Take(8).SequenceEqual(new byte[] { 0x00, 0x1F, 0x00, 0x00, 0x00, 0x02, 0x07, 0x80 })
            && request[88] == (0x02 ^ 0x07 ^ 0x80) && request[89] == 0 && request.Skip(8).Take(80).All(b => b == 0));
        var reply = Razer.Request(0x1F, Razer.Battery); reply[0] = Razer.Success; reply[9] = 0xFF; reply[88] = Razer.Checksum(reply);
        check("Razer: a successful reply and its level", Razer.Reply(reply, Razer.Battery) == (Razer.Success, 0xFF) && Razer.Percent(0xFF) == 100 && Razer.Percent(0) == 0 && Razer.Percent(128) == 50);
        var garbled = (byte[])reply.Clone(); garbled[10] ^= 1;
        check("Razer: a bad checksum or another command is no reply", Razer.Reply(garbled, Razer.Battery) is null && Razer.Reply(reply, Razer.ChargingState) is null && Razer.Reply(reply[..89], Razer.Battery) is null);
        check("Razer: the catalog's models and transaction ids", Razer.Find(0x00C1) is { Transaction: 0x1F, Name: "Razer Viper V3 Pro" } && Razer.Find(0x007D) is { Transaction: 0x3F }
            && Razer.Find(0x007B) is { Transaction: 0xFF } && Razer.Find(0x00E6) is { Interface: 3 } && Razer.Find(0x9999) is null
            && Razer.Catalog.Select(model => model.Product).Distinct().Count() == Razer.Catalog.Count);
        check("Razer: the keyboards, receiver and cable, with OpenRazer's ids and interfaces", Razer.Find(0x0296) is { Transaction: 0x9F, Interface: 2, Kind: DeviceKind.Keyboard }
            && Razer.Find(0x0298) is { Transaction: 0x1F, Interface: 3, Kind: DeviceKind.Keyboard } && Razer.Find(0x025A) is { Transaction: 0x3F, Interface: 2 }
            && Razer.Find(0x02BA) is { Interface: 3 } && Razer.Find(0x00C1) is { Kind: DeviceKind.Mouse });
    }

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    private static void MoreDongles(Action<string, bool> check)
    {
        // Compx's 17-byte frame: vigil-watt's captures on an ATK 101B receiver.
        check("Compx frame: a reply on battery and one on the cable", Dongles.CompxFrame(Bytes("08 04 00 00 00 02 5F 00 10 01 00 00 00 00 00 00 D7")) == new BatteryRead(95)
            && Dongles.CompxFrame(Bytes("08 04 00 00 00 02 5F 01 10 61 00 00 00 00 00 00 76")) == new BatteryRead(95, true));
        check("Compx frame: a bad checksum, a pushed 0x03 frame and the request echoed are none",
            Dongles.CompxFrame(Bytes("08 04 00 00 00 02 5F 00 10 01 00 00 00 00 00 00 D8")) is null
            && Dongles.CompxFrame(Bytes("08 03 00 00 00 01 01 CB B3 8D 00 00 00 00 00 00 3D")) is null && Dongles.CompxFrame(Dongles.CompxRequest(0x04)) is null);
        check("Compx frame: only the listed products, only their 17-byte 0xFF02 collection, never BitMouse on their other ones",
            Dongles.Find(0x373B, 0x101B, 0xFF02, "", 0, 1, 0x0002, 17, 17) == Dongles.Protocol.CompxFramed
            && Dongles.Find(0x373B, 0x101B, 0xFF05, "", 0, 1, 0x0001, 64, 64) is null && Dongles.Find(0x373B, 0x10C9, 0xFF02, "", 0, 1, 0x0002, 20, 20) is null
            && Dongles.Find(0x3554, 0xFA09, 0xFF02, "", 0, 1, 0x0002, 17, 17) is null && Dongles.Find(0x373B, 0x1119, 0xFF04, "", 8) == Dongles.Protocol.Compx
            && Dongles.Find(0x373B, 0x9999, 0xFF05, "", 0) == Dongles.Protocol.BitMouse);

        // The 0x83 exchange: report id 0, 00 00 02 02 00 83 in the 64 bytes after it.
        var beast = Dongles.F83Find(0x36A7, 0xA880)!;
        var request = Dongles.F83Request(beast);
        check("0x83: the request as HaloBattery sends it", request.Length == 65 && request[..7].SequenceEqual(new byte[] { 0, 0, 0, 2, 2, 0, 0x83 }) && request.Skip(7).All(b => b == 0));
        check("0x83: a WLmouse reply with and without its report id; the request read back is none",
            Dongles.F83Battery(beast, Bytes("00 A1 00 02 02 00 83 00 4B 00")) == new BatteryRead(75) && Dongles.F83Battery(beast, Bytes("A1 00 02 02 00 83 01 4B 00")) == new BatteryRead(75, true)
            && Dongles.F83Battery(beast, Bytes("A2 00 02 02 00 83 00 4B 00")) == new BatteryRead(75) && Dongles.F83Battery(beast, request) is null);
        var gwolves = Dongles.F83Find(0x33E4, 0x3854)!;
        check("0x83: G-Wolves' receiver says not active (not A1): asleep", Dongles.F83Battery(gwolves, Bytes("00 A2 00 02 02 00 83 00 00 00")) == new BatteryRead(null, Asleep: true)
            && Dongles.F83Battery(gwolves, Bytes("00 A1 00 02 02 00 83 00 3C 00")) == new BatteryRead(60));
        var old = Dongles.F83Find(0x33E4, 0x5804)!;
        check("0x83: the older G-Wolves exchange, on the cable", old is { Exchange: Dongles.F83Exchange.Old, Cable: true }
            && Dongles.F83Request(old)[..5].SequenceEqual(new byte[] { 0, 0, 0x02, 0x8F, 0x00 }) && Dongles.F83Request(Dongles.F83Find(0x33E4, 0x5803)!)[4] == 0x01
            && Dongles.F83Battery(old, Bytes("00 A1 02 8F 00 01 50 00")) == new BatteryRead(80, true));
        var a5 = Dongles.F83Find(0x2023, 0xF013)!;
        check("0x83: the MCHOSE A5 Pro Max's level clamped to 100", Dongles.F83Battery(a5, Bytes("00 A1 00 02 02 00 83 01 6E 00")) == new BatteryRead(100, true));
        check("0x83: G-Wolves on the 65-byte feature collection, LAMZU on interface 2, the rest on 0xFFFF",
            Dongles.Find(0x33E4, 0x3854, 0xFF00, "", 65) == Dongles.Protocol.F83 && Dongles.Find(0x33E4, 0x3854, 0xFF00, "", 9) is null
            && Dongles.Find(0x373E, 0x001E, 0xFFFF, "", 65, 2) == Dongles.Protocol.F83 && Dongles.Find(0x373E, 0x001E, 0xFFFF, "", 65, 1) is null
            && Dongles.Find(0x36A7, 0xA887, 0xFFFF, "", 65, 1) == Dongles.Protocol.F83 && Dongles.Find(0x36A7, 0xA887, 0xFF00, "", 65, 1) is null);
        DeviceBattery Device(string hardware, bool asleep, int? percent = 60) => new("f83:" + hardware, "G-Wolves", DeviceKind.Mouse, DeviceSource.Hid, percent) { Asleep = asleep, Hardware = hardware };
        var receiver = Device("33E4:3854", asleep: true);
        check("Twins: a receiver finding its mouse asleep is left out while the mouse's cable reads it, and only then",
            Dongles.Shadowed(receiver, [receiver, Device("33E4:4719", asleep: false)]) && !Dongles.Shadowed(receiver, [receiver])
            && !Dongles.Shadowed(receiver, [receiver, Device("33E4:5804", asleep: false)]) && !Dongles.Shadowed(Device("33E4:3854", asleep: false), [Device("33E4:4719", asleep: false)])
            && Dongles.Twins.Contains(("0B05:1AD0", "0B05:1C69")) && Dongles.Twins.Contains(("1038:1838", "1038:183A")));

        // MCHOSE on RealTek: every byte after the report id inverted.
        var realtek = new byte[21]; realtek[0] = 0x11; realtek[1] = 0xF9;
        byte[] payload = [0x53, 0x52, 0x31, 0x00, 5, 2, 7, 0, 0x09, 93, 1, 93, 1];
        for (int i = 2; i < realtek.Length; i++) realtek[i] = (byte)((i - 2 < payload.Length ? payload[i - 2] : 0) ^ 0xFF);
        check("MCHOSE RealTek: the inverted request and an inverted reply", Dongles.MchoseRealtekRequest(false)[..4].SequenceEqual(new byte[] { 0x11, 0xF9, 0xFF, 0xFF })
            && Dongles.MchoseRealtekRequest(false).Length == 21 && Dongles.MchoseRealtekRequest(true).Length == 65 && Dongles.MchoseRealtekRequest(true)[0] == 0x12
            && Dongles.MchoseRealtekBattery(realtek) == (new BatteryRead(93, true), 0x0031) && Dongles.MchoseRealtekBattery(Dongles.MchoseRealtekRequest(false)) is null);
        check("MCHOSE Gmouse: the query and the G7's replies (HaloBattery #8)", Dongles.GmouseRequest()[..9].SequenceEqual(new byte[] { 0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01 })
            && Dongles.GmouseRequest().Length == 65 && Dongles.GmouseBattery(Bytes("aa 30 a5 0b 0a 01 01 01 2e 00 00 00")) == new BatteryRead(46)
            && Dongles.GmouseBattery(Bytes("00 aa 30 a5 3c 0a 01 01 01 2e 01 00")) == new BatteryRead(46, true) && Dongles.GmouseBattery(Bytes("4d 01 00 00 00 09 00 00 00 00 00")) is null);
        check("MCHOSE: the G3 V2 takes the Gmouse query on its 0x10 collection, not the 0x4D frame", Dongles.Find(0x3837, 0x4245, 0xFF01, "Mouse", 0, 2, 0x0010, 65, 65) == Dongles.Protocol.Gmouse
            && Dongles.Find(0x3837, 0x4245, 0xFF01, "Mouse", 0, 2, 0x0001, 65, 65) is null && Dongles.Find(0x3837, 0x5011, 0xFF01, "MCHOSE G7 Mouse", 0) == Dongles.Protocol.Mchose);
        check("LINGBAO M5PRO Max: F7, and vigil-watt's replies", Dongles.LingbaoRequest()[..2].SequenceEqual(new byte[] { 0x00, 0xF7 }) && Dongles.LingbaoRequest().Length == 65
            && Dongles.LingbaoBattery(Bytes("00 00 00 5f 01 00 01 02")) == new BatteryRead(95) && Dongles.LingbaoBattery(Bytes("00 00 00 5a 01 01 01 02")) == new BatteryRead(90, true)
            && Dongles.LingbaoBattery(Dongles.LingbaoRequest()) is null);
        var nzxt = Bytes("4e 02 97 00 01 40 01 86 0f fd ff 40 01 f6 00 43 00 4d 00 64 00 1e 00 08 00 40 01");
        var nzxtCable = (byte[])nzxt.Clone(); nzxtCable[22] = 1;
        check("NZXT Lift Elite: the telemetry request and reply; the acknowledgement is none", Dongles.NzxtRequest()[..5].SequenceEqual(new byte[] { 0x4E, 0x02, 0x81, 0x00, 0xB0 })
            && Dongles.NzxtBattery(nzxt) == new BatteryRead(77) && Dongles.NzxtBattery(nzxtCable) == new BatteryRead(77, true) && Dongles.NzxtBattery(Bytes("4e e5 00 00")) is null);
        check("Block 0x1A: CHERRY PIXIU75 and HECATE G5M Pro; a heartbeat is none",
            Dongles.StatusBlockRequest(Dongles.StatusBlock.Cherry)[..5].SequenceEqual(new byte[] { 0x04, 0, 0, 0x1A, 0x04 }) && Dongles.StatusBlockRequest(Dongles.StatusBlock.Hecate)[4] == 0
            && Dongles.StatusBlockBattery(Dongles.StatusBlock.Cherry, Bytes("04 00 00 1a 04 00 00 00 30 00 00 00")) == new BatteryRead(48)
            && Dongles.StatusBlockBattery(Dongles.StatusBlock.Cherry, Bytes("04 00 00 1a 04 00 00 00 31 01 00 00")) == new BatteryRead(49, true)
            && Dongles.StatusBlockBattery(Dongles.StatusBlock.Hecate, Bytes("04 00 00 1a 00 00 00 00 16 01 00")) == new BatteryRead(22, true)
            && Dongles.StatusBlockBattery(Dongles.StatusBlock.Hecate, Bytes("04 00 00 1a 00 00 00 00 64 02 00")) == new BatteryRead(100)
            && Dongles.StatusBlockBattery(Dongles.StatusBlock.Hecate, Bytes("04 aa 00 aa 00 00 00 00 ff 00 00")) is null);
    }

    private static void AsusAndSteelSeries(Action<string, bool> check)
    {
        var gladius = Asus.Find(0x0B05, 0x197F)!;
        var reply = new byte[65]; reply[1] = 0x12; reply[2] = 0x07; reply[5] = 64; reply[10] = 1;
        check("ASUS: the request, and a mouse's percent and charging", Asus.Request(gladius)[..3].SequenceEqual(new byte[] { 0x00, 0x12, 0x07 }) && Asus.Request(gladius).Length == 65
            && Asus.Reply(gladius, reply) is BatteryRead { Percent: 64, Charging: true });
        var chakram = Asus.Find(0x0B05, 0x18E5)!;
        var steps = (byte[])reply.Clone(); steps[5] = 3; steps[10] = 0;
        var carry = (byte[])reply.Clone(); carry[5] = 0; carry[7] = 2; carry[10] = 0;
        check("ASUS: the older mice count four steps (the Strix Carry at 7)", Asus.Reply(chakram, steps) is BatteryRead { Percent: 75, Coarse: true }
            && Asus.Reply(Asus.Find(0x0B05, 0x18B4)!, carry) is BatteryRead { Percent: 50 } && Asus.Reply(chakram, reply) == Asus.Refused);
        var unknown = new byte[65]; unknown[1] = 0xFF; unknown[2] = 0xAA;
        var other = (byte[])reply.Clone(); other[2] = 0x04;
        check("ASUS: a command the firmware does not know, or zeros, end the ask; another report is passed over",
            Asus.Reply(gladius, unknown) == Asus.Refused && Asus.Reply(gladius, new byte[65]) == Asus.Refused && Asus.Reply(gladius, other) is null);
        var keyboard = new byte[65]; keyboard[1] = 0x12; keyboard[2] = 0x01; keyboard[6] = 0x5F; keyboard[9] = 0;
        var falchion = (byte[])keyboard.Clone(); falchion[6] = 0; falchion[11] = 70; falchion[9] = 1;
        check("ASUS: keyboards (12 01), the Falchion at 11", Asus.Request(Asus.Find(0x0B05, 0x1A83)!)[..3].SequenceEqual(new byte[] { 0, 0x12, 0x01 })
            && Asus.Reply(Asus.Find(0x0B05, 0x1A83)!, keyboard) is BatteryRead { Percent: 95, Charging: false }
            && Asus.Reply(Asus.Find(0x0B05, 0x193E)!, falchion) is BatteryRead { Percent: 70, Charging: true });
        var delta = Asus.Find(0x0B05, 0x1AFA)!;
        var level = new byte[64]; level[0] = 0xCC; level[1] = 0x12; level[2] = 0x07; level[5] = 0x05; level[6] = 89;
        var notReady = (byte[])level.Clone(); notReady[6] = 0xFE;
        check("ASUS: the Delta II headset, report 0xCC, and its charging state", Asus.Request(delta)[..3].SequenceEqual(new byte[] { 0xCC, 0x12, 0x07 })
            && Asus.Reply(delta, level) is BatteryRead { Percent: 89 } && Asus.Reply(delta, notReady) == Asus.Refused
            && Asus.ChargingRequest(delta)![..3].SequenceEqual(new byte[] { 0xCC, 0x12, 0x08 }) && Asus.Charging(delta, [0xCC, 0x12, 0x08, 0, 0, 1]) == true);
        check("ASUS: each model on its interface and collection", Dongles.Find(0x0B05, 0x197F, 0xFF01, "", 0, 0, 1, 65, 65) == Dongles.Protocol.Asus
            && Dongles.Find(0x0B05, 0x197F, 0xFF01, "", 0, 1, 1, 65, 65) is null && Dongles.Find(0x0B05, 0x197F, 0x0001, "", 0, 0, 2, 8, 0) is null
            && Dongles.Find(0x0B05, 0x1AD0, 0xFF01, "", 0, 2, 1, 65, 65, collection: 3) == Dongles.Protocol.Asus
            && Dongles.Find(0x0B05, 0x1AD0, 0xFF00, "", 0, 2, 1, 65, 65, collection: 2) is null && Dongles.Find(0x0B05, 0x1ACE, 0xFF01, "", 0, 2, 1, 65, 65, collection: 3) is null);

        var aerox = SteelSeriesMice.Find(0x1038, 0x1838)!;
        check("SteelSeries Aerox: 00 D2 through the receiver, 00 92 on the cable", SteelSeriesMice.Request(aerox).SequenceEqual(new byte[] { 0x00, 0xD2 })
            && SteelSeriesMice.Request(SteelSeriesMice.Find(0x1038, 0x183A)!).SequenceEqual(new byte[] { 0x00, 0x92 }));
        check("SteelSeries Aerox: steps of five, a percent above 21, charging in bit 7, 0 asleep", SteelSeriesMice.Battery(aerox, [0x00, 0xD2, 0x10]) == new BatteryRead(75, Coarse: true)
            && SteelSeriesMice.Battery(aerox, [0xD2, 0x95]) == new BatteryRead(100, true, true) && SteelSeriesMice.Battery(aerox, [0x00, 0xD2, 0xC5]) == new BatteryRead(69, true)
            && SteelSeriesMice.Battery(aerox, [0x00, 0xD2, 0x00]) == new BatteryRead(null, Asleep: true) && SteelSeriesMice.Battery(aerox, [0x00, 0x92, 0x10]) is null);
        var rival = SteelSeriesMice.Find(0x1038, 0x1830)!;
        check("SteelSeries Rival 3 Wireless: either layout, the charging byte 0 or 1", SteelSeriesMice.Request(rival).SequenceEqual(new byte[] { 0x00, 0xAA, 0x01 })
            && SteelSeriesMice.Battery(rival, [0x00, 75, 0x00, 0x01]) == new BatteryRead(75, true) && SteelSeriesMice.Battery(rival, [0xAA, 80, 0x00, 0x00]) == new BatteryRead(80)
            && SteelSeriesMice.Battery(rival, [75, 0x00, 0x07]) is null);
        check("SteelSeries mice: on interface 3's vendor collection (the Rival 650 on 0); the headsets stay headsets",
            Dongles.Find(0x1038, 0x1838, 0xFFC0, "", 0, 3, 1, 65, 65) == Dongles.Protocol.SteelSeries && Dongles.Find(0x1038, 0x1838, 0x0001, "", 0, 3, 2, 8, 0) is null
            && Dongles.Find(0x1038, 0x1838, 0xFFC0, "", 0, 0, 1, 65, 65) is null && Dongles.Find(0x1038, 0x1726, 0xFFC0, "", 0, 0, 1, 33, 33) == Dongles.Protocol.SteelSeries
            && Dongles.Find(0x1038, 0x12AD, 0xFFC0, "", 0, 5, 1, 33, 33) is null);
    }

    private static void Pushed(Action<string, bool> check)
    {
        check("Beken: misaka310's X1 sample (90 %), the X6 in tenths, charging on its dock", Pushes.Battery(Pushes.Kind.Beken, Bytes("03 B1 40 01 5A")) == new BatteryRead(90)
            && Pushes.Battery(Pushes.Kind.Beken, Bytes("03 85 40 01 05")) == new BatteryRead(50, Coarse: true) && Pushes.Battery(Pushes.Kind.Beken, Bytes("03 55 40 03 40")) == new BatteryRead(64, true)
            && Pushes.Battery(Pushes.Kind.Beken, Bytes("01 00 00 00 00")) is null && Pushes.Name(Pushes.Kind.Beken, Bytes("03 B1 40 01 5A")) == "ATTACK SHARK X1");
        check("DELUX M900 Pro and Inphic ZHEN: one receiver id, told apart by the collection", Pushes.Battery(Pushes.Kind.Delux, Bytes("03 30 40 01 59")) == new BatteryRead(89)
            && Pushes.Battery(Pushes.Kind.Delux, Bytes("03 30 40 03 59")) == new BatteryRead(89, true) && Pushes.Battery(Pushes.Kind.InphicZhen, Bytes("03 76 41 01 55")) == new BatteryRead(85)
            && Pushes.Battery(Pushes.Kind.InphicZhen, Bytes("03 76 41 03 54")) == new BatteryRead(84, true)
            && Pushes.Find(0x1D57, 0xFA65, 0x000A, 0, 2, 5) == Pushes.Kind.Delux && Pushes.Find(0x1D57, 0xFA65, 0xFF00, 1, 2, 5) == Pushes.Kind.InphicZhen
            && Pushes.Find(0x1D57, 0xFA60, 0x000A, 0, 2, 5) == Pushes.Kind.Beken && Pushes.Find(0x1D57, 0xFA60, 0x000A, 0, 1, 5) is null);
        var rapoo = new byte[19]; rapoo[0] = 0x07; rapoo[1] = 0x20; rapoo[7] = 0x01; rapoo[8] = 95; rapoo[13] = 0x13; rapoo[14] = 0x1B; rapoo[18] = 0xD2;
        var unlinked = (byte[])rapoo.Clone(); unlinked[18] = 0x7F;
        var cable = (byte[])rapoo.Clone(); cable[1] = 0x22; cable[7] = 0x02; cable[18] = 0x00;
        var settling = (byte[])cable.Clone(); settling[8] = 1;
        check("Rapoo: the heartbeat's level, the radio down asleep, charging on its cable, a settling 1 % none", Pushes.Battery(Pushes.Kind.Rapoo, rapoo) == new BatteryRead(95)
            && Pushes.Battery(Pushes.Kind.Rapoo, unlinked) == new BatteryRead(null, Asleep: true) && Pushes.Battery(Pushes.Kind.Rapoo, cable) == new BatteryRead(95, true)
            && Pushes.Battery(Pushes.Kind.Rapoo, settling) is null && Pushes.Find(0x24AE, 0x1B13, 0xFF00, 0x0002, 1, 19) == Pushes.Kind.Rapoo
            && Pushes.Find(0x24AE, 0x1B13, 0xFF00, 0x000E, 1, 33) is null && Pushes.Name(Pushes.Kind.Rapoo, rapoo) == "Rapoo Mouse");
        check("AULA G9: the percent and charging; 0 and 0 is none", Pushes.Battery(Pushes.Kind.AulaG9, Bytes("AB 47 4D 86 00 00 50 02")) == new BatteryRead(80, true)
            && Pushes.Battery(Pushes.Kind.AulaG9, Bytes("AB 47 4D 86 00 00 00 00")) is null && Pushes.KindOf(Pushes.Kind.AulaG9) == DeviceKind.Headphones);
        var steam = new byte[15]; steam[0] = 0x43; steam[1] = 2; steam[2] = 55;
        var steamReset = (byte[])steam.Clone(); steamReset[1] = 0;
        check("Steam Controller puck: report 0x43, a slot per interface", Pushes.Battery(Pushes.Kind.SteamPuck, steam) == new BatteryRead(55, true)
            && Pushes.Battery(Pushes.Kind.SteamPuck, steamReset) is null && Pushes.Find(0x28DE, 0x1304, 0xFF00, 1, 3, 64) == Pushes.Kind.SteamPuck
            && Pushes.Find(0x28DE, 0x1304, 0xFF00, 1, 1, 64) is null);
        check("Steam Controller puck: a controller disconnected keeps its level, asleep; connected again, awake",
            Pushes.Apply(Pushes.Kind.SteamPuck, new BatteryRead(55, true), [0x79, 0x01]) == new BatteryRead(55, Asleep: true)
            && Pushes.Apply(Pushes.Kind.SteamPuck, new BatteryRead(55, Asleep: true), [0x46, 0x02]) == new BatteryRead(55) && Pushes.Apply(Pushes.Kind.SteamPuck, null, [0x79, 0x01]) is null);
        check("Rapoo: the radio down after a reading keeps the level, asleep", Pushes.Apply(Pushes.Kind.Rapoo, new BatteryRead(95), unlinked) == new BatteryRead(95, Asleep: true));
        check("JBL Quantum 910: report 0x08 the level (HaloBattery's 08 1e = 30 %), 0x09 off and on", Pushes.Apply(Pushes.Kind.Jbl, null, [0x08, 0x1E]) == new BatteryRead(30)
            && Pushes.Apply(Pushes.Kind.Jbl, new BatteryRead(30), [0x09, 0x00]) == new BatteryRead(30, Asleep: true) && Pushes.Apply(Pushes.Kind.Jbl, new BatteryRead(30, Asleep: true), [0x09, 0x01]) == new BatteryRead(30)
            && Pushes.Apply(Pushes.Kind.Jbl, null, [0x09, 0x01]) is null && Pushes.Apply(Pushes.Kind.Jbl, null, [0x2F, 0x01]) is null
            && Pushes.Find(0x0ECB, 0x2088, 0xFF13, 0x0001, 5, 64) == Pushes.Kind.Jbl && Pushes.Find(0x0ECB, 0x2088, 0x000C, 0x0001, 5, 64) is null);
        check("PULSE 3D: steps of ten, charging without a level, disconnected asleep", Pushes.Apply(Pushes.Kind.Pulse3D, null, Bytes("B0 00 00 50 08 00 00 00")) == new BatteryRead(80, Coarse: true)
            && Pushes.Apply(Pushes.Kind.Pulse3D, new BatteryRead(80, Coarse: true), Bytes("B0 00 00 80 08 00 00 00")) == new BatteryRead(80, true, true)
            && Pushes.Apply(Pushes.Kind.Pulse3D, new BatteryRead(80, Coarse: true), Bytes("B0 00 00 50 00 00 00 00")) == new BatteryRead(80, Coarse: true, Asleep: true)
            && Pushes.Apply(Pushes.Kind.Pulse3D, null, Bytes("01 00 00 50 08")) is null && Pushes.Find(0x054C, 0x0D5E, 0xFF01, 0x0020, 3, 8) == Pushes.Kind.Pulse3D
            && Pushes.Source(Pushes.Kind.Pulse3D) == DeviceSource.Sony);
        check("Cloud Flight 2: the level at each step, charging only from FB 09, off and on from FB 0A (vigil-watt's samples)",
            Pushes.Apply(Pushes.Kind.CloudFlight2, null, Bytes("fb 13 00 28 5f")) == new BatteryRead(40)
            && Pushes.Apply(Pushes.Kind.CloudFlight2, new BatteryRead(40), Bytes("fb 09 01")) == new BatteryRead(40, true)
            && Pushes.Apply(Pushes.Kind.CloudFlight2, new BatteryRead(94, true), Bytes("fb 13 00 5f 04")) == new BatteryRead(95, true)
            && Pushes.Apply(Pushes.Kind.CloudFlight2, new BatteryRead(95, true), Bytes("fb 0a 00")) == new BatteryRead(95, true, Asleep: true)
            && Pushes.Apply(Pushes.Kind.CloudFlight2, null, Bytes("45 02 00 00")) is null && Pushes.Apply(Pushes.Kind.CloudFlight2, null, Bytes("51 02 2c 00 1a 00 0e c0")) == new BatteryRead(44)
            && Pushes.Find(0x03F0, 0x0AC1, 0xFF90, 0xFF00, 3, 64) == Pushes.Kind.CloudFlight2 && Pushes.Find(0x03F0, 0x0AC1, 0xFF13, 0x0001, 3, 62) is null);
    }

    private static void Corsairs(Action<string, bool> check)
    {
        var darkCore = Corsair.Find(0x1B1C, 0x1B7F)!;
        var virtuoso = Corsair.Find(0x1B1C, 0x0A3E)!;
        check("Corsair: the Dark Core's framed request and the Virtuoso's bare one", Corsair.Request(darkCore, Corsair.Level)[..4].SequenceEqual(new byte[] { 0x00, 0x09, 0x02, 0x0F })
            && Corsair.Request(darkCore, Corsair.Level).Length == 65 && Corsair.Request(virtuoso, Corsair.ChargeState, Corsair.RouteReceiver)[..4].SequenceEqual(new byte[] { 0x02, 0x08, 0x02, 0x10 })
            && Corsair.Request(virtuoso, Corsair.Level).Length == 64);
        check("Corsair: the captured answer (55 %), the Virtuoso's shape, tenths; zero and other frames are none",
            Corsair.Percent(Bytes("01 02 00 26 02 00")) == 55 && Corsair.Percent(Bytes("01 01 02 00 fd 02 00")) == 76 && Corsair.Percent(Bytes("01 02 00 00 00 00")) is null
            && Corsair.Percent(Bytes("01 02 00 70 17 00")) is null && Corsair.Percent(Bytes("02 02 00 26 02 00")) is null && Corsair.Percent(Bytes("01 02 01 26 02 00")) is null
            && Corsair.Charging(Bytes("01 01 02 00 01 00")) == true && Corsair.Charging(Bytes("01 01 02 00 02 00")) == false);
        check("Corsair: the routes, cables first to themselves", Corsair.Routes(Corsair.Find(0x1B1C, 0x0A3D)!).SequenceEqual(new byte[] { 0x08, 0x09 })
            && Corsair.Routes(virtuoso).SequenceEqual(new byte[] { 0x09, 0x08 }) && Corsair.Routes(darkCore).SequenceEqual(new byte[] { 0x09 })
            && Corsair.Find(0x1B1C, 0x0A40) is { Session: true } && Corsair.Find(0x1B1C, 0x1B7E) is null);
    }

    private static void Omni(Action<string, bool> check)
    {
        check("ROG Omni: the pairing table request", Asus.OmniTableRequest()[..3].SequenceEqual(new byte[] { 0x01, 0xA0, 0x00 }));
        var table = Asus.OmniTable(Bytes("01 A0 00 02 00 B0 1A 02 04 65 1B 03 02 00 00"))!;
        check("ROG Omni: vigil-watt's table (a Strix Scope II 96 on report 2) and a mouse on report 3", table.Count == 2
            && table[0] is { Product: 0x1AB0, ReportId: 2, Collection: 2, Family: Asus.Family.Keyboard, LevelAt: 6 }
            && table[1] is { Product: 0x1B65, ReportId: 3, Collection: 3, Family: Asus.Family.Mouse } && Asus.OmniTable(Bytes("02 12 01 00 00")) is null
            && Asus.OmniTable(Bytes("01 A0 00 01 00 34 12 02 04"))!.Count == 0);
        var reply = new byte[64]; Bytes("02 12 01 00 00 00 5F 02 01 00 14 5F BC 10").CopyTo(reply, 0);
        check("ROG Omni: the Scope II 96's battery on its channel (95 %, not charging)", Asus.Request(table[0])[..3].SequenceEqual(new byte[] { 0x02, 0x12, 0x01 })
            && Asus.Reply(table[0], reply) is BatteryRead { Percent: 95, Charging: false });
    }

    private static void RapooFirst(Action<string, bool> check)
    {
        var request = Dongles.RapooGen1Request(cable: false);
        check("Rapoo first generation: the battery query, on the receiver and the cable", request.Length == 33 && request[..8].SequenceEqual(new byte[] { 0xBA, 0xA5, 0xAA, 0, 0, 0, 0, 0 })
            && Dongles.RapooGen1Request(cable: true)[1] == 0xFF);
        check("Rapoo first generation: busy, the answer, and a stale block answer", Dongles.RapooGen1Status(Bytes("BA 02 00 00")) == false && Dongles.RapooGen1Status(Bytes("01 02 37")) == true
            && Dongles.RapooGen1Battery(Bytes("BA 01 02 37 00")) == new BatteryRead(55, true) && Dongles.RapooGen1Battery(Bytes("01 01 4B")) == new BatteryRead(75)
            && Dongles.RapooGen1Battery(Bytes("BA 01 00 37 00")) is null && Dongles.RapooGen1Battery(Bytes("BA 02 01 37 00")) is null);
        check("Rapoo first generation: only its receiver and cable, on a vendor collection with the 33-byte report",
            Dongles.Find(0x24AE, 0x1231, 0xFF00, "", 0, 1, 1, 33, 33) == Dongles.Protocol.RapooGen1 && Dongles.Find(0x24AE, 0x1231, 0x0001, "", 0, 0, 2, 8, 0) is null
            && Dongles.Find(0x24AE, 0x1412, 0xFF00, "", 0, 1, 2, 19, 33) is null);
    }

    private static void DongleFrames(Action<string, bool> check)
    {
        check("ATK/VXE: the battery request with its checksum", Dongles.BitMouseRequest(0x07).Take(8).SequenceEqual(new byte[] { 0x08, 0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01 }));
        check("ATK/VXE: a reply's level; an asleep mouse or a cabled placeholder is none",
            Dongles.BitMouseBattery([0x08, 0x72, 0x00, 0x00, 0x01, 0x07, 0x01, 87, 0x10, 0x0F]) == new BatteryRead(87)
            && Dongles.BitMouseBattery([0x08, 0x72, 0x01, 0x00, 0x01, 0x07, 0x01, 87, 0x10, 0x0F]) is null
            && Dongles.BitMouseBattery([0x08, 0x72, 0x00, 0x00, 0x00, 0x07, 0x01, 0, 0, 0]) is null
            && Dongles.BitMouseCharging([0x08, 0x72, 0x00, 0x00, 0x01, 0x0F, 0x01, 1]) == true);
        var compx = Dongles.CompxRequest(0x04);
        check("Compx: the battery request, its checksum 0x49", compx.Length == 17 && compx[0] == 0x08 && compx[1] == 0x04 && compx[16] == 0x49 && compx.Sum(b => b) % 256 == 0x55);
        check("Compx: a reply; charging only at 1", Dongles.CompxBattery([0x08, 0x04, 0, 0, 0, 1, 63, 1, 0x0F, 0xA0, 0, 0, 0, 0, 0, 0, 0]) == new BatteryRead(63, true)
            && Dongles.CompxBattery([0x08, 0x04, 0, 0, 0, 1, 63, 2]) == new BatteryRead(63) && Dongles.CompxBattery([0x08, 0x03, 0, 0, 0, 0, 63, 0]) is null);
        var vgn = new byte[64]; vgn[0] = 0x04; vgn[3] = 0x1A; vgn[8] = 41; vgn[9] = 0;
        check("VGN keyboards: the request and a reply", Dongles.VgnRequest().Take(5).SequenceEqual(new byte[] { 0x04, 0, 0, 0x1A, 0x06 }) && Dongles.VgnBattery(vgn) == new BatteryRead(41));
        var endgame = new byte[32]; endgame[0] = 0xA1; endgame[1] = 0x01; endgame[16] = 77;
        check("Endgame Gear: the request and a reply; a busy one is none", Dongles.EndgameRequest(32)[..2].SequenceEqual(new byte[] { 0xA1, 0xB4 }) && Dongles.EndgameBattery(endgame) == new BatteryRead(77)
            && Dongles.EndgameBattery([0xA1, 0x03, .. new byte[30]]) is null);
        check("VAXEE: five points a step", Dongles.VaxeeRequest(8)[..5].SequenceEqual(new byte[] { 0x0E, 0xA5, 0x0B, 0x01, 0x01 }) && Dongles.VaxeeBattery([0x0E, 0, 0, 0, 0, 17]) == new BatteryRead(85, Coarse: true));
        var nordic = new byte[32]; nordic[0] = 0x05; nordic[9] = 52; nordic[12] = 1;
        var nordicCharging = (byte[])nordic.Clone(); nordicCharging[10] = 1;
        var nordicOffline = (byte[])nordic.Clone(); nordicOffline[12] = 0;
        check("Nordic receivers (Ninjutso): the level, charging, and an offline mouse asleep", Dongles.NordicRequest(32)[..5].SequenceEqual(new byte[] { 0x05, 0x15, 0, 0, 0x01 })
            && Dongles.NordicRequest(32).Length == 32 && Dongles.NordicBattery(nordic) == new BatteryRead(52) && Dongles.NordicBattery(nordicCharging) == new BatteryRead(52, true)
            && Dongles.NordicBattery(nordicOffline) == new BatteryRead(null, Asleep: true));
        check("Nordic receivers: only the Ninjutso Sora V2's, on its vendor collection", Dongles.Find(0x1915, 0xAE1C, 0xFFA0, "", 33) == Dongles.Protocol.Nordic
            && Dongles.Find(0x1915, 0xAE1C, 0x0001, "", 0) is null && Dongles.Find(0x1915, 0x7777, 0xFFA0, "", 33) is null);
        var inphic = new byte[33]; inphic[0] = 0x0C; inphic[2] = 0x20; inphic[6] = 1; inphic[0x12] = 66; inphic[0x13] = 2;
        check("Inphic IN9: the handshake with its payload, the query, a reply", Dongles.InphicRequest(0xF0, 3, Dongles.InphicHandshake)[..9].SequenceEqual(new byte[] { 0x0C, 0x01, 0xF0, 0x00, 0x03, 0x01, 0x01, 0xFF, 0 })
            && Dongles.InphicRequest(0x20, 3)[..6].SequenceEqual(new byte[] { 0x0C, 0x01, 0x20, 0x00, 0x03, 0 }) && Dongles.InphicRequest(0x20, 3).Length == 33
            && Dongles.InphicBattery(inphic) == new BatteryRead(66, true));
        check("MCHOSE: the device info request with its checksum", Dongles.MchoseRequest()[..9].SequenceEqual(new byte[] { 0x4D, 0x01, 0x01, 0x00, 0x00, 0x09, 0x00, 0x00, 0x08 }));
        var mchose = new byte[64]; mchose[0] = 0x4D; mchose[5] = 0x09; mchose[8 + 10] = 1; mchose[8 + 11] = 0; mchose[8 + 12] = 91;
        var off = (byte[])mchose.Clone(); off[8 + 12] = 0;
        check("MCHOSE: a reply's level; a disconnected mouse is none", Dongles.MchoseBattery(mchose) == new BatteryRead(91) && Dongles.MchoseBattery(off) is null);
        check("MCHOSE: the mouse protocol only for its mice, never the MagDock or the X9", Dongles.IsMchoseMouse(0x5011, "MCHOSE G7 Mouse") && Dongles.IsMchoseMouse(0x5011, "迈从无线鼠标")
            && !Dongles.IsMchoseMouse(0x1012, "MCHOSE MagDock Mouse") && !Dongles.IsMchoseMouse(0x6045, "X9") && !Dongles.IsMchoseMouse(0x4001, "MCHOSE K99"));
        // AJAZZ AJ179 / XCTECH: report-id 0 + 0x10; asleep keeps last %, never a false 0 %.
        var ajazzReq = Dongles.AjazzInfoRequest();
        check("AJAZZ: 0x10 request is report-id 0, cmd 0x10, zero checksum", ajazzReq.Length == 33 && ajazzReq[0] == 0 && ajazzReq[1] == 0x10 && ajazzReq[2] == 0 && ajazzReq[3] == 0
            && ajazzReq[32] == 0 && ajazzReq.Skip(4).Take(28).All(b => b == 0));
        check("AJAZZ: supported IDs (verified wireless + documented wired)", Dongles.IsAjazz(0x249A, 0x5C2F) && Dongles.IsAjazz(0x248A, 0x5C2E)
            && Dongles.IsAjazz(0x248A, 0x5D2E) && Dongles.IsAjazz(0x248A, 0x5E2E)
            && !Dongles.IsAjazz(0x249A, 0x5B2F) && !Dongles.IsAjazz(0x248A, 0x5B2E));
        check("AJAZZ: MI_02 / UsagePage 1 Usage 0 collection", Dongles.AjazzCollection(2, 0x01, 0x00, 33, 33) && Dongles.AjazzCollection(-1, 0x01, 0x00, 33, 33)
            && !Dongles.AjazzCollection(0, 0x01, 0x02, 33, 33) && !Dongles.AjazzCollection(2, 0x01, 0x00, 8, 8));
        // Live sample shape from PROTOCOL.md / Zero PoC: id M041, 92 %, not charging, connected; asleep keeps 82 %.
        var awake = new byte[32]; awake[0] = 0x10; awake[1] = 0x00; awake[2] = 0x01; awake[3] = 0x0b;
        awake[4] = (byte)'M'; awake[5] = (byte)'0'; awake[6] = (byte)'4'; awake[7] = (byte)'1';
        awake[10] = 0x11; awake[12] = 0; awake[13] = 92; awake[14] = 1;
        var asleep = (byte[])awake.Clone(); asleep[13] = 82; asleep[14] = 0;
        var withId = new byte[33]; withId[0] = 0; awake.CopyTo(withId, 1);
        check("AJAZZ: 0x10 awake and asleep (last %, never 0); leading report-id stripped shape",
            Dongles.AjazzParseInfo(awake) == new Dongles.AjazzInfo(92, false, true, "M041")
            && Dongles.AjazzParseInfo(asleep) == new Dongles.AjazzInfo(82, false, false, "M041")
            && Dongles.AjazzParseInfo(withId) == new Dongles.AjazzInfo(92, false, true, "M041")
            && Dongles.AjazzBattery(Dongles.AjazzParseInfo(asleep)!.Value) == new BatteryRead(82, Asleep: true)
            && Dongles.AjazzBattery(Dongles.AjazzParseInfo(awake)!.Value) == new BatteryRead(92)
            && Dongles.AjazzParseInfo([0x10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]) is null
            && Dongles.AjazzParseInfo([0x10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 101, 1]) is null);
        check("AJAZZ: passive 0xC0 online/offline", Dongles.AjazzParseStatus([0xC0, 1, 55]) == (true, 55)
            && Dongles.AjazzParseStatus([0x00, 0xC0, 0, 40]) == (false, 40)
            && Dongles.AjazzParseStatus([0xC0, 1, 200]) == (true, null));
        check("AJAZZ: on its cable a mouse is never asleep, whatever the connected byte says",
            Dongles.AjazzBattery(Dongles.AjazzParseInfo(asleep)!.Value, cable: true) == new BatteryRead(82));
        // The mouse on its cable, with its receiver plugged in too: the receiver reports it not connected.
        DeviceBattery Ajazz(string hardware, string name, bool asleep) => new("ajazz:" + hardware, name, DeviceKind.Mouse, DeviceSource.Hid, 82) { Asleep = asleep, Hardware = hardware };
        var receiver = Ajazz("249A:5C2F", "M041", asleep: true);
        check("AJAZZ: the receiver's entry of a mouse its cable reads is left out, and only that one",
            Dongles.AjazzShadowed(receiver, [receiver, Ajazz("248A:5C2E", "M041", asleep: false)])
            && !Dongles.AjazzShadowed(receiver, [receiver])
            && !Dongles.AjazzShadowed(receiver, [receiver, Ajazz("248A:5C2E", "M179", asleep: false)])
            && !Dongles.AjazzShadowed(Ajazz("249A:5C2F", "M041", asleep: false), [Ajazz("248A:5C2E", "M041", asleep: false)]));
    }

    private static void HeadsetReplies(Action<string, bool> check)
    {
        check("Headsets: the HyperX requests go to the vendor collections only", Headsets.Channel(Headsets.Kind.CloudAlpha).UsagePage == Headsets.VendorPages);
        check("Headsets: models found by vendor and product", Headsets.Find(0x1038, 0x12AD) == Headsets.Kind.Arctis7 && Headsets.Find(0x1038, 0x2202) == Headsets.Kind.Nova7Steps
            && Headsets.Find(0x03F0, 0x098D) == Headsets.Kind.CloudAlpha && Headsets.Find(0x3837, 0x6045) == Headsets.Kind.MchoseX9 && Headsets.Find(0x3837, 0x1012) is null);
        check("Headsets: requests", Headsets.Request(Headsets.Kind.Arctis7).SequenceEqual(new byte[] { 0x06, 0x18 }) && Headsets.Request(Headsets.Kind.CloudII).Length == 52
            && Headsets.Request(Headsets.Kind.NovaPro)[..2].SequenceEqual(new byte[] { 0x06, 0xB0 }));
        check("Headsets: Arctis 7 and Arctis 9", Headsets.Battery(Headsets.Kind.Arctis7, [0x06, 0x18, 72]) == new BatteryRead(72)
            && Headsets.Battery(Headsets.Kind.Arctis9, [0, 0, 0, 0x9A, 1]) == new BatteryRead(100, true) && Headsets.Battery(Headsets.Kind.Arctis9, [0, 0, 0, 0x64, 0]) == new BatteryRead(0));
        check("Headsets: a value out of its range is no level, never an empty battery",
            Headsets.Battery(Headsets.Kind.Arctis7, [0x06, 0x18, 200]) is null && Headsets.Battery(Headsets.Kind.Arctis7, [0x01, 0, 0]) is null && Headsets.Battery(Headsets.Kind.Arctis9, [0x02, 0, 0, 0x10, 0]) is null
            && Headsets.Battery(Headsets.Kind.Nova7Steps, [0xB0, 0, 9, 3]) is null);
        check("Headsets: Nova 7 in percent and in steps, off is none", Headsets.Battery(Headsets.Kind.Nova7Percent, [0xB0, 0, 64, 3]) == new BatteryRead(64)
            && Headsets.Battery(Headsets.Kind.Nova7Steps, [0xB0, 0, 3, 1]) == new BatteryRead(75, true, true) && Headsets.Battery(Headsets.Kind.Nova7Percent, [0xB0, 0, 64, 0]) is null);
        var pro = new byte[16]; pro[0] = 0x06; pro[1] = 0xB0; pro[6] = 4; pro[15] = 0x02;
        check("Headsets: Nova Pro in eighths", Headsets.Battery(Headsets.Kind.NovaPro, pro) == new BatteryRead(50, true, true));
        check("Headsets: HyperX Cloud Alpha level and charging", Headsets.Battery(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x0B, 58]) == new BatteryRead(58)
            && Headsets.Charging(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x0C, 1]) == true && Headsets.Battery(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x0C, 1]) is null);
        check("Headsets: HyperX Cloud Alpha asked whether its headset is connected first", Headsets.ConnectionRequest(Headsets.Kind.CloudAlpha) is [0x21, 0xBB, 0x03, ..] and { Length: 31 }
            && Headsets.Connected(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x03, 0x01]) == false && Headsets.Connected(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x03, 0x02]) == true
            && Headsets.Connected(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x0B, 0x01]) is null && Headsets.ConnectionRequest(Headsets.Kind.CloudII) is null);
        check("Headsets: HyperX Cloud II", Headsets.Battery(Headsets.Kind.CloudII, [0x06, 0xFF, 0xBB, 0x02, 0, 0x0F, 0xA0, 81]) == new BatteryRead(81));
        check("Headsets: HyperX Cloud Flight by voltage, on its cable", Headsets.Battery(Headsets.Kind.CloudFlight, [0x21, 0xFF, 0x05, 0x0E, 0xA0]) is { Coarse: true, Percent: 29 }
            && Headsets.Battery(Headsets.Kind.CloudFlight, [0x21, 0xFF, 0x05, 0x10, 0x40]) is { Charging: true, Percent: null });
        check("Headsets: MCHOSE X9, the battery field only; other fields are none", Headsets.Battery(Headsets.Kind.MchoseX9, [0x55, 0x65, 40, 0x02]) == new BatteryRead(40)
            && Headsets.Battery(Headsets.Kind.MchoseX9, [0x55, 0x65, 40, 0x03]) is null && Headsets.Battery(Headsets.Kind.MchoseX9, [0x55, 0x65, 40, 0x04]) is null
            && Headsets.Battery(Headsets.Kind.MchoseX9, [0x55, 0x65, 40, 26]) is null);
        check("Headsets: HyperX Cloud III and II Core, the capture's 48 % and its charging state", Headsets.Find(0x03F0, 0x0995) == Headsets.Kind.CloudIII
            && Headsets.Request(Headsets.Kind.CloudIII) is [0x66, 0x89, ..] and { Length: 62 } && Headsets.Battery(Headsets.Kind.CloudIII, Bytes("66 89 0e d7 30 00")) == new BatteryRead(48)
            && Headsets.Battery(Headsets.Kind.CloudIII, Bytes("66 89 00 00 30 00")) is null && Headsets.Charging(Headsets.Kind.CloudIII, Bytes("66 8a 00 00")) == false
            && Headsets.Charging(Headsets.Kind.CloudIII, Bytes("66 0c 01 00")) == true && Headsets.Channel(Headsets.Kind.CloudIII).UsagePage == 0xFF13);
        check("Headsets: HyperX Cloud III S asks for a value (01 at 3), its reply and its own notice", Headsets.Find(0x03F0, 0x02CC) == Headsets.Kind.CloudIIIS
            && Headsets.Request(Headsets.Kind.CloudIIIS)[..6].SequenceEqual(new byte[] { 0x0C, 0x02, 0x03, 0x01, 0x00, 0x06 })
            && Headsets.Battery(Headsets.Kind.CloudIIIS, Bytes("0c 02 03 01 00 06 37")) == new BatteryRead(55) && Headsets.Battery(Headsets.Kind.CloudIIIS, Bytes("0c 02 03 01 00 06 ff")) is null
            && Headsets.Battery(Headsets.Kind.CloudIIIS, Bytes("0d 00 00 00 01 3c")) == new BatteryRead(60) && Headsets.Charging(Headsets.Kind.CloudIIIS, Bytes("0c 02 03 01 00 48 01")) == true);
        check("Headsets: HyperX Cloud Alpha 2 (51 02), the two captures' levels", Headsets.Find(0x03F0, 0x08BE) == Headsets.Kind.CloudAlpha2
            && Headsets.Request(Headsets.Kind.CloudAlpha2) is [0x50, 0x02, ..] and { Length: 64 } && Headsets.Battery(Headsets.Kind.CloudAlpha2, Bytes("51 02 33 00 1a 00 00")) == new BatteryRead(51)
            && Headsets.Battery(Headsets.Kind.CloudAlpha2, Bytes("51 02 43 00 1a 00 80")) == new BatteryRead(67, true) && Headsets.Battery(Headsets.Kind.CloudAlpha2, Bytes("61 02 43 00 1a 00 80")) is null);
    }

    private static void Controllers(Action<string, bool> check)
    {
        var usb = new byte[64]; usb[0] = 0x01; usb[53] = 0x07;
        var bt = new byte[78]; bt[0] = 0x31; bt[54] = 0x15;
        var full = new byte[64]; full[0] = 0x01; full[53] = 0x2A;
        check("DualSense: USB and Bluetooth reports", PlayStation.Battery(0x0CE6, usb, bluetooth: false) == new BatteryRead(75) && PlayStation.Battery(0x0CE6, bt, bluetooth: true) == new BatteryRead(55, true)
            && PlayStation.Battery(0x0CE6, full, bluetooth: false) == new BatteryRead(100));
        // Windows pads every report to the collection's longest: a short Bluetooth 0x01 report comes 78 bytes long, all zeros after it.
        var padded = new byte[78]; padded[0] = 0x01;
        check("DualSense: a short Bluetooth report, padded, carries none", PlayStation.Battery(0x0CE6, padded, bluetooth: true) is null
            && PlayStation.Battery(0x0CE6, new byte[10] { 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, bluetooth: true) is null);
        var ds4 = new byte[64]; ds4[0] = 0x01; ds4[30] = 0x08;
        var ds4Cable = new byte[78]; ds4Cable[0] = 0x11; ds4Cable[32] = 0x1B;
        var ds4Charging = (byte[])ds4Cable.Clone(); ds4Charging[32] = 0x15;
        var ds4Short = new byte[547]; ds4Short[0] = 0x01;
        check("DualShock 4: on battery, charging and full on its cable; a padded short Bluetooth report carries none", PlayStation.Battery(0x09CC, ds4, bluetooth: false) == new BatteryRead(85)
            && PlayStation.Battery(0x09CC, ds4Cable, bluetooth: true) == new BatteryRead(100) && PlayStation.Battery(0x09CC, ds4Charging, bluetooth: true) == new BatteryRead(55, true)
            && PlayStation.Battery(0x09CC, ds4Short, bluetooth: true) is null);
    }

    private static void Usages(Action<string, bool> check)
    {
        check("HID battery usages", HidBattery.IsBattery(0x06, 0x20) && HidBattery.IsBattery(0x0D, 0x3B) && HidBattery.IsBattery(0x85, 0x66) && !HidBattery.IsBattery(0x01, 0x02));
        check("HID battery: scaled by the logical range", HidBattery.Percent(50, 0, 100) == 50 && HidBattery.Percent(255, 0, 255) == 100 && HidBattery.Percent(3, 0, 4) == 75
            && HidBattery.Percent(300, 0, 255) is null);
    }

    private static void Apple(Action<string, bool> check)
    {
        // AirPods Pro: byte 6 0x86 with byte 5's 0x20 set (not flipped): left the low nibble 6 (60 %), right the high 8 (80 %);
        // case 3 (30 %) charging, as OpenPods and AirPodsDesktop read it.
        byte[] advert = [0x07, 0x19, 0x01, 0x0E, 0x20, 0x2B, 0x86, 0x43, 0, 0];
        var parts = AppleAdvert.Parts(advert)!;
        check("AirPods: the model and each part's level", AppleAdvert.Model(advert) == "AirPods Pro" && parts.Count == 3 && parts[0] == new PartBattery(BatteryPart.Left, 60, false)
            && parts[1] == new PartBattery(BatteryPart.Right, 80, false) && parts[2] == new PartBattery(BatteryPart.Case, 30, true) && AppleAdvert.Single(advert) is null);
        // Flipped (0x20 clear): the left is the high nibble, and its charging bit is 0x02; the right charges here.
        byte[] flipped = [0x07, 0x19, 0x01, 0x0E, 0x20, 0x0B, 0x86, 0x1F, 0, 0];
        var swapped = AppleAdvert.Parts(flipped)!;
        check("AirPods: flipped buds swap with their charging, a missing case is left out", swapped.Count == 2 && swapped[0] == new PartBattery(BatteryPart.Left, 80, false)
            && swapped[1] == new PartBattery(BatteryPart.Right, 60, true));
        // AirPods Max: one level (byte 6's low nibble, 7 = 70 %), charging, no parts.
        byte[] max = [0x07, 0x19, 0x01, 0x0A, 0x20, 0x2B, 0x07, 0x1F, 0, 0];
        check("AirPods Max and the one-piece Beats: one level, no left and right", AppleAdvert.Model(max) == "AirPods Max" && AppleAdvert.Parts(max) is { Count: 0 }
            && AppleAdvert.Single(max) == new BatteryRead(70, true, true) && AppleAdvert.Single([0x07, 0x19, 0x01, 0x10, 0x20, 0x2B, 0x0F, 0x0F]) is null);
        check("AirPods: another advertisement is none", AppleAdvert.Parts([0x10, 0x05, 0, 0, 0, 0, 0, 0]) is null && AppleAdvert.Model([0x07, 0x19, 0x01, 0x99, 0x99]) is null);
    }
}

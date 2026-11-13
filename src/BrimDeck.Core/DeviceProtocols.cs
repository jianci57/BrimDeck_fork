namespace BrimDeck.Core;

// The vendor protocols the devices page reads batteries with, as bytes in and readings out: building each request and parsing
// each reply, with no I/O, so every one is tested against the bytes it is documented with. The app's HID layer
// (Native/HidBatteries.cs) sends them. Sources, by protocol:
// - Logitech HID++ 2.0: after device-battery-viewer by CharlesGool (https://github.com/CharlesGool/device-battery-viewer,
//   MIT License; see ThirdPartyNotices.txt), whose flow, battery features and voltage curve are ported; the HID++ 1.0 battery
//   registers and the 0x1F20 headset feature are from the protocol as the Linux kernel and Solaar describe it (facts only).
// - Razer: after razer-battery-report by Timofey Gelazoniya (https://github.com/xzeldon/razer-battery-report, MIT License;
//   see ThirdPartyNotices.txt): the 90-byte report, its checksum and the catalog of models and transaction ids.
// - ATK/VXE (Nordic "BitMouse") and Compx frames: after Lkig/ATK-A9-Battery and cobanov/atkctl (MIT); Inphic IN9: after
//   InphicBatteryTray by Tan-TanZi (MIT). See ThirdPartyNotices.txt.
// - AJAZZ / AJ179 (XCTECH 249A:5C2F receiver, documented wired 248A:5C2E/5D2E/5E2E): interrupt report framing after
//   Rockeyxx/aj179-linux (MIT; see ThirdPartyNotices.txt) and a read-only PoC on a 249A:5C2F / M041 dongle — not the AJ 8K
//   Feature 0xF7 path.
// - SteelSeries, HyperX and MCHOSE headsets, MCHOSE mice, VGN keyboards (and ATK's on the same frame, measured here),
//   Endgame Gear, VAXEE, Nordic receivers and the PlayStation controllers: written from the documented bytes alone
//   (HeadsetControl, the Linux drivers and the vendors' own web drivers are look-only).
// - After HaloBattery (https://github.com/HeyOkay/HaloBattery, MIT; see ThirdPartyNotices.txt): the Compx 17-byte frame's
//   reply rules (pulsar.py), the 0x83 feature exchange of WLmouse, G-Wolves, LAMZU and the MCHOSE A5 Pro Max with its model
//   lists, the MCHOSE RealTek and G7 exchanges (mchose.py), the HyperX Cloud III, III S and Alpha 2 requests, the NZXT Lift
//   Elite's telemetry, SteelSeries's mouse replies (steelseries.py), the Razer keyboards' transaction ids and interfaces, the
//   Corsair receivers' get-property exchange (corsair.py) and the JBL Quantum 910's reports (jbl.py).
// - After vigil-watt (https://github.com/evren-oct/vigil-watt, MIT): the ATK, HECATE and VGN collections for the Compx frame,
//   the LINGBAO M5PRO Max, CHERRY PIXIU75 and HECATE G5M Pro reads, the MCHOSE G3 V2 query, the ROG Delta II headset, the ROG
//   Omni receiver's pairing table, and the DELUX, Inphic ZHEN, Rapoo, AULA G9 and HyperX Cloud Flight 2 reports.
// - Rapoo's first generation: after mousectl's rapoo_vt3pro driver (https://github.com/D3m0nZOnFire/mousectl, MIT).
// - SteelSeries mice: rivalcfg's device profiles (https://github.com/flozz/rivalcfg, WTFPL). Beken (Attack Shark) receivers:
//   after incconutwo's and misaka310's mouse-battery-tray (MIT). Rapoo: also Iris-0109's rapoo-tray (MIT). The LAMZU Inca on
//   the 0x83 exchange: Fan4Metal's mouse_tray and MouseDock's device table (MIT) list it.
// - ASUS ROG and TUF and the Omni receiver's model ids (G-Helper, GPL), the Ninjutso receiver and the rest of the Compx model
//   table (Fan4Metal's mouse_tray, no license), the Steam Controller puck's reports (SDL) and the PULSE 3D's status report
//   (HeadsetControl, GPL): facts only, the code written from the documented bytes.
// - AirPods and Beats: the proximity pairing advertisement's layout as OpenPods and AirPodsDesktop read it (GPL; look-only,
//   facts only).

// One battery reading from a protocol: the percentage (null: the device answered without one), whether it charges, whether
// the level is coarse (a voltage, or a few steps), and whether the mouse is asleep / not answering (Asleep): the last percent
// still stands; never invent 0 % for that case.
public readonly record struct BatteryRead(int? Percent, bool Charging = false, bool Coarse = false, bool Asleep = false);

public static class Hidpp
{
    public const int Vendor = 0x046D;
    public const byte Short = 0x10, Long = 0x11;
    // The software id (low nibble of a request's byte 3) is what tells a reply to one program's request from another's, since a
    // receiver sends every reply to every program that has it open. It must be one nobody else uses: OpenRGB 0x07, LGSTrayEx
    // 0x0A, Solaar 0x0B, Logitech G HUB 0x0D and the firmware's 0x0F are taken (Solaar's lib/logitech_receiver/base.py lists
    // them); 0x0E is free.
    public const byte SoftwareId = 0x0E;
    public const int ShortLength = 7, LongLength = 20;
    public const int DeviceInfo = 0x0003, DeviceName = 0x0005, BatteryStatus = 0x1000, BatteryVoltage = 0x1001, UnifiedBattery = 0x1004, AdcMeasurement = 0x1F20;
    // HID++ 1.0's error for a request it does not know: the device speaks only HID++ 1.0.
    public const int InvalidSubId = 0x01;

    // A receiver (Unifying, Bolt, Lightspeed, Nano) holds devices 1 to 6; a device on a cable or its own dongle is 0xFF.
    public static bool IsReceiver(int product, string productName) => product is >= 0xC500 and <= 0xC5FF || productName.Contains("receiver", StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<byte> Indices(bool receiver) => receiver ? [1, 2, 3, 4, 5, 6] : [0xFF];

    // A long request: [0x11, device, feature index, function << 4 | software id, parameters...], padded to 20 bytes.
    public static byte[] Request(byte device, byte feature, int function, params byte[] parameters)
    {
        var message = new byte[LongLength];
        message[0] = Long; message[1] = device; message[2] = feature; message[3] = (byte)((function << 4) | SoftwareId);
        Array.Copy(parameters, 0, message, 4, Math.Min(parameters.Length, LongLength - 4));
        return message;
    }

    // What a report says to a request: its parameters (bytes 4 on), an error code (HID++ 2.0's 0xFF or 1.0's 0x8F), or nothing
    // (another report: a movement, an event, another program's reply).
    public static (byte[]? Reply, int? Error) Match(byte[] report, byte device, byte feature, int function)
    {
        byte fsw = (byte)((function << 4) | SoftwareId);
        if (report.Length < 6 || report[0] is not (Short or Long) || report[1] != device) return (null, null);
        if (report[2] == feature && report[3] == fsw) return (report[4..], null);
        if (report[0] == Long && report[2] == 0xFF && report[3] == feature && report[4] == fsw) return (null, report[5]);
        if (report[0] == Short && report[2] == 0x8F && report[3] == feature && report[4] == fsw) return (null, report[5]);
        return (null, null);
    }

    // Root (feature index 0) function 0: the index of a feature, 0 when the device lacks it.
    public static byte[] FeatureParameters(int feature) => [(byte)(feature >> 8), (byte)feature];

    // The voltage curve device-battery-viewer uses for the Lightspeed mice' 0x1001: millivolts to percent between its points.
    private static readonly (int Millivolts, int Percent)[] Curve =
        [(4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0)];
    public static int VoltageToPercent(int millivolts)
    {
        if (millivolts >= Curve[0].Millivolts) return 100;
        for (int i = 1; i < Curve.Length; i++)
        {
            var (high, highPercent) = Curve[i - 1]; var (low, lowPercent) = Curve[i];
            if (millivolts >= low) return (int)Math.Round(lowPercent + (highPercent - lowPercent) * (millivolts - low) / (double)(high - low));
        }
        return 0;
    }

    // 0x1004 status (function 1) with its capabilities (function 0): the state of charge, or, where the device has none, the
    // level's step (full, good, low, critical); charging 1 to 3 (charging, slowly, complete on power).
    public static BatteryRead Unified(byte[] status, byte[]? capabilities)
    {
        int soc = status[0], bits = status[1], state = status[2];
        bool steps = capabilities is { Length: > 1 } && (capabilities[1] & 0x02) == 0;
        if (steps) soc = (bits & 8) != 0 ? 100 : (bits & 4) != 0 ? 50 : (bits & 2) != 0 ? 20 : 5;
        return new(Devices.Clamp(soc), state is >= 1 and <= 3, steps);
    }

    // 0x1000 function 0: the level (0 is no reading), and the status, 1 to 4 charging; 3 full.
    public static BatteryRead Status(byte[] reply) => new(reply[2] == 3 ? 100 : reply[0] > 0 ? Devices.Clamp(reply[0]) : null, reply[2] is >= 1 and <= 4);

    // 0x1001 function 0: millivolts big endian, and bit 7 of the flags for external power.
    public static BatteryRead Voltage(byte[] reply) => new(VoltageToPercent((reply[0] << 8) | reply[1]), (reply[2] & 0x80) != 0, Coarse: true);

    // 0x1F20 (the G headsets) function 0: millivolts big endian and flags, as Solaar reads them: bit 0x01 the measurement is
    // valid (without it the headset is off, which is no reading, not an empty battery), 0x02 charging, 0x07 full. The level
    // goes through the Lightspeed mice' curve above, not one for the headset's own cell (HeadsetControl calibrates each
    // headset), so it is coarse.
    public static BatteryRead? Adc(byte[] reply)
    {
        int millivolts = (reply[0] << 8) | reply[1], flags = reply[2];
        if ((flags & 0x01) == 0) return null;
        return new(flags == 0x07 ? 100 : VoltageToPercent(millivolts), (flags & 0x02) != 0, Coarse: true);
    }

    // HID++ 1.0 register 0x0D (battery mileage): the percentage, and the top two bits of byte 2, 1 charging and 2 full.
    public static byte[] RegisterRequest(byte device, byte register) => [Short, device, 0x81, register, 0, 0, 0];
    public static byte[] RegisterWrite(byte device, byte register, byte p0, byte p1, byte p2) => [Short, device, 0x80, register, p0, p1, p2];

    // What a receiver or device sends unasked, as Solaar and the Linux driver read it. A receiver's register 0x00 holds which
    // notices it sends: bit 0 of its second byte is the wireless one (0x41, a device's link made or lost), which OpenLogi and
    // Solaar turn on and BrimDeck turns on the same way, leaving the other bits as they were.
    public const byte NotificationFlags = 0x00, WirelessNotifications = 0x01;
    // The register's three bytes in a reply to RegisterRequest(0xFF, NotificationFlags); null for any other report.
    public static byte[]? Flags(byte[] report) => report.Length >= 7 && report[0] == Short && report[1] == 0xFF && report[2] == 0x81 && report[3] == NotificationFlags
        ? report[4..7] : null;
    // A 0x41 notice: the device's index and whether its link is up (bit 0x40 of byte 4 set means lost).
    public static (byte Device, bool Linked)? Connection(byte[] report) => report.Length >= 5 && report[0] is Short or Long && report[2] == 0x41
        ? (report[1], (report[4] & 0x40) == 0) : null;
    // A HID++ 2.0 event: a long report with software id 0 (no program asked for it) on a feature index (HID++ 1.0 notices use
    // 0x40 and up). Its function nibble is the event's number; its parameters lay out as the feature's matching reply does
    // (0x1004 and 0x1000 event 0 as their status replies).
    public static (byte Device, byte Feature, int Event, byte[] Parameters)? Event(byte[] report) => report.Length >= LongLength && report[0] == Long
        && report[2] is > 0 and < 0x40 && (report[3] & 0x0F) == 0 ? (report[1], report[2], report[3] >> 4, report[4..LongLength]) : null;

    public static BatteryRead Mileage(byte[] reply) => new(Devices.Clamp(reply[0]), (reply[2] >> 6) is 1 or 2);
    // Register 0x07 (battery status): a level 1 to 7 and its status, 0x21, 0x24, 0x25 charging and 0x22, 0x26 full.
    public static BatteryRead Level(byte[] reply)
    {
        int percent = reply[0] switch { 1 => 5, 2 => 10, 3 => 20, 4 => 35, 5 => 50, 6 => 75, 7 => 100, _ => 0 };
        return new(reply[1] is 0x22 or 0x26 ? 100 : percent > 0 ? percent : null, reply[1] is 0x21 or 0x22 or 0x24 or 0x25 or 0x26, Coarse: true);
    }

    // 0x0005 function 2: the kind of device.
    public static DeviceKind Kind(int type) => type switch
    {
        0 or 2 => DeviceKind.Keyboard, 3 or 4 or 5 => DeviceKind.Mouse, 8 => DeviceKind.Headphones, 11 or 12 => DeviceKind.Controller, _ => DeviceKind.Other
    };
}

public static class Razer
{
    public const int Vendor = 0x1532, ReportLength = 90;
    // Byte 0 of a reply: 0x02 success, 0x01 busy and 0x04 no answer (try again), 0x03 failure, 0x05 not supported.
    public const byte Success = 0x02, Busy = 0x01, NoResponse = 0x04;
    public static readonly (byte Class, byte Id, byte Size) Battery = (0x07, 0x80, 0x02), ChargingState = (0x07, 0x84, 0x02);

    // The request with its checksum: XOR of bytes 2 to 87.
    public static byte[] Request(byte transaction, (byte Class, byte Id, byte Size) command)
    {
        var bytes = new byte[ReportLength];
        bytes[1] = transaction; bytes[5] = command.Size; bytes[6] = command.Class; bytes[7] = command.Id;
        bytes[88] = Checksum(bytes);
        return bytes;
    }
    public static byte Checksum(byte[] bytes) { byte crc = 0; for (int i = 2; i < 88; i++) crc ^= bytes[i]; return crc; }

    // A reply to the command: its status, and argument 1 (bytes 8 on are the arguments), or null when it is another report or
    // its checksum is wrong.
    public static (byte Status, byte Value)? Reply(byte[] bytes, (byte Class, byte Id, byte Size) command)
    {
        if (bytes.Length != ReportLength || Checksum(bytes) != bytes[88] || bytes[6] != command.Class || bytes[7] != command.Id) return null;
        return (bytes[0], bytes[9]);
    }
    public static int Percent(byte raw) => (raw * 100 + 127) / 255;

    // The catalog: the transaction id each model answers to, the interface to use, how long to wait between the request and the
    // reply (microseconds), and whether it reports charging. Values from razer-battery-report's catalog (derived from openrazer).
    // The keyboards at its end (Kind Keyboard) are OpenRazer's keyboard driver's values as HaloBattery's protocols.md lists them:
    // 0x9F on the HyperSpeed receiver, 0x1F (the BlackWidow V3 Pro 0x3F) on the cable, each on its USB interface; only the
    // DeathStalker V2 Pro TKL is confirmed on hardware there. Their wait is the mice' usual one.
    public sealed record Model(int Product, string Name, byte Transaction, int Interface, int WaitMicroseconds, bool Charging, DeviceKind Kind = DeviceKind.Mouse);
    public static readonly IReadOnlyList<Model> Catalog =
    [
        new(0x001F, "Razer Naga Epic", 0xFF, 0, 600, true), new(0x0024, "Razer Mamba 2012", 0xFF, 0, 600, true), new(0x0025, "Razer Mamba 2012", 0xFF, 0, 600, true),
        new(0x0032, "Razer Ouroboros", 0xFF, 0, 600, true), new(0x003E, "Razer Naga Epic Chroma", 0xFF, 0, 600, true), new(0x003F, "Razer Naga Epic Chroma", 0xFF, 0, 600, true),
        new(0x0044, "Razer Mamba Chroma", 0xFF, 0, 600, true), new(0x0045, "Razer Mamba Chroma", 0xFF, 0, 600, true),
        new(0x0059, "Razer Lancehead", 0x3F, 0, 600, true), new(0x005A, "Razer Lancehead", 0x3F, 0, 600, true),
        new(0x0062, "Razer Atheris", 0x1F, 0, 400_000, false), new(0x006F, "Razer Lancehead Wireless", 0x1F, 0, 31_000, true), new(0x0070, "Razer Lancehead Wireless", 0x1F, 0, 31_000, true),
        new(0x0072, "Razer Mamba Wireless", 0x3F, 0, 31_000, true), new(0x0073, "Razer Mamba Wireless", 0x3F, 0, 31_000, true),
        new(0x0077, "Razer Pro Click", 0x1F, 0, 31_000, true), new(0x007A, "Razer Viper Ultimate", 0xFF, 0, 59_900, true), new(0x007B, "Razer Viper Ultimate", 0xFF, 0, 59_900, true),
        new(0x007C, "Razer DeathAdder V2 Pro", 0x3F, 0, 59_900, true), new(0x007D, "Razer DeathAdder V2 Pro", 0x3F, 0, 59_900, true),
        new(0x0080, "Razer Pro Click", 0x1F, 0, 31_000, true), new(0x0083, "Razer Basilisk X HyperSpeed", 0xFF, 0, 31_000, false),
        new(0x0086, "Razer Basilisk Ultimate", 0x1F, 0, 31_000, true), new(0x0088, "Razer Basilisk Ultimate", 0x1F, 0, 31_000, true),
        new(0x008F, "Razer Naga Pro", 0x1F, 0, 600, true), new(0x0090, "Razer Naga Pro", 0x1F, 0, 31_000, true),
        new(0x0094, "Razer Orochi V2", 0x1F, 0, 400_000, false), new(0x0095, "Razer Orochi V2", 0x1F, 0, 400_000, false),
        new(0x009A, "Razer Pro Click Mini", 0x1F, 0, 31_000, true), new(0x009C, "Razer DeathAdder V2 X HyperSpeed", 0x1F, 0, 31_000, false),
        new(0x009E, "Razer Viper Mini SE", 0x1F, 0, 59_900, true), new(0x009F, "Razer Viper Mini SE", 0x1F, 0, 59_900, true),
        new(0x00A5, "Razer Viper V2 Pro", 0x1F, 0, 31_000, true), new(0x00A6, "Razer Viper V2 Pro", 0x1F, 0, 31_000, true),
        new(0x00A7, "Razer Naga V2 Pro", 0x1F, 0, 31_000, true), new(0x00A8, "Razer Naga V2 Pro", 0x1F, 0, 31_000, true),
        new(0x00AA, "Razer Basilisk V3 Pro", 0x1F, 0, 31_000, true), new(0x00AB, "Razer Basilisk V3 Pro", 0x1F, 0, 31_000, true),
        new(0x00AF, "Razer Cobra Pro", 0x1F, 0, 31_000, true), new(0x00B0, "Razer Cobra Pro", 0x1F, 0, 31_000, true),
        new(0x00B3, "Razer HyperPolling Wireless Dongle", 0x1F, 0, 59_900, true), new(0x00B4, "Razer Naga V2 HyperSpeed", 0x1F, 0, 31_000, false),
        new(0x00B6, "Razer DeathAdder V3 Pro", 0x1F, 0, 31_000, true), new(0x00B7, "Razer DeathAdder V3 Pro", 0x1F, 0, 31_000, true),
        new(0x00B8, "Razer Viper V3 HyperSpeed", 0x1F, 0, 59_900, false), new(0x00B9, "Razer Basilisk V3 X HyperSpeed", 0x1F, 0, 31_000, false),
        new(0x00BE, "Razer DeathAdder V4 Pro", 0x1F, 0, 31_000, true), new(0x00BF, "Razer DeathAdder V4 Pro", 0x1F, 0, 31_000, true),
        new(0x00C0, "Razer Viper V3 Pro", 0x1F, 0, 31_000, true), new(0x00C1, "Razer Viper V3 Pro", 0x1F, 0, 59_900, true),
        new(0x00C2, "Razer DeathAdder V3 Pro", 0x1F, 0, 31_000, true), new(0x00C3, "Razer DeathAdder V3 Pro", 0x1F, 0, 31_000, true),
        new(0x00C4, "Razer DeathAdder V3 HyperSpeed", 0x1F, 0, 31_000, true), new(0x00C5, "Razer DeathAdder V3 HyperSpeed", 0x1F, 0, 31_000, true),
        new(0x00C7, "Razer Pro Click V2 Vertical Edition", 0x1F, 0, 31_000, true), new(0x00C8, "Razer Pro Click V2 Vertical Edition", 0x1F, 0, 31_000, true),
        new(0x00CC, "Razer Basilisk V3 Pro 35K", 0x1F, 0, 31_000, true), new(0x00CD, "Razer Basilisk V3 Pro 35K", 0x1F, 0, 31_000, true),
        new(0x00D0, "Razer Pro Click V2", 0x1F, 0, 31_000, true), new(0x00D1, "Razer Pro Click V2", 0x1F, 0, 31_000, true),
        new(0x00D3, "Razer Basilisk Mobile", 0x1F, 0, 31_000, false), new(0x00D4, "Razer Basilisk Mobile", 0x1F, 0, 31_000, false),
        new(0x00D6, "Razer Basilisk V3 Pro 35K Phantom Green Edition", 0x1F, 0, 31_000, true), new(0x00D7, "Razer Basilisk V3 Pro 35K Phantom Green Edition", 0x1F, 0, 31_000, true),
        new(0x00DA, "Razer Cobra HyperSpeed", 0x1F, 0, 31_000, true), new(0x00DB, "Razer Cobra HyperSpeed", 0x1F, 0, 31_000, true),
        new(0x00DE, "Razer Viper V3 Pro SE", 0x1F, 0, 31_000, true), new(0x00DF, "Razer Viper V3 Pro SE", 0x1F, 0, 59_900, true),
        new(0x00E2, "Razer Basilisk V4 Pro", 0x1F, 3, 59_900, true), new(0x00E3, "Razer Basilisk V4 Pro", 0x1F, 3, 59_900, true),
        new(0x00E5, "Razer Viper V4 Pro", 0x1F, 3, 59_900, true), new(0x00E6, "Razer Viper V4 Pro", 0x1F, 3, 59_900, true),
        new(0x0296, "Razer DeathStalker V2 Pro TKL", 0x9F, 2, 31_000, true, DeviceKind.Keyboard), new(0x0298, "Razer DeathStalker V2 Pro TKL", 0x1F, 3, 31_000, true, DeviceKind.Keyboard),
        new(0x0290, "Razer DeathStalker V2 Pro", 0x9F, 2, 31_000, true, DeviceKind.Keyboard), new(0x0292, "Razer DeathStalker V2 Pro", 0x1F, 3, 31_000, true, DeviceKind.Keyboard),
        new(0x025C, "Razer BlackWidow V3 Pro", 0x9F, 2, 31_000, true, DeviceKind.Keyboard), new(0x025A, "Razer BlackWidow V3 Pro", 0x3F, 2, 31_000, true, DeviceKind.Keyboard),
        new(0x0271, "Razer BlackWidow V3 Mini HyperSpeed", 0x9F, 3, 31_000, true, DeviceKind.Keyboard), new(0x0258, "Razer BlackWidow V3 Mini HyperSpeed", 0x1F, 3, 31_000, true, DeviceKind.Keyboard),
        new(0x02BA, "Razer BlackWidow V4 Mini HyperSpeed", 0x9F, 3, 31_000, true, DeviceKind.Keyboard), new(0x02B9, "Razer BlackWidow V4 Mini HyperSpeed", 0x1F, 3, 31_000, true, DeviceKind.Keyboard),
        new(0x02D5, "Razer BlackWidow V4 Tenkeyless HyperSpeed", 0x9F, 2, 31_000, true, DeviceKind.Keyboard), new(0x02D7, "Razer BlackWidow V4 Tenkeyless HyperSpeed", 0x1F, 3, 31_000, true, DeviceKind.Keyboard),
    ];
    public static Model? Find(int product) => Catalog.FirstOrDefault(model => model.Product == product);
}

// The 2.4 GHz mice of the Chinese brands, each a frame on a vendor collection.
public static class Dongles
{
    // AULA (狼蛛) keyboards on their 2.4 GHz receiver (VID 0x3554, the F87 PRO's 0xFA09, usage page 0xFF02, usage 0x0002,
    // report id 0x13, 20 bytes): [report id, command, 0x01, 0x00, zeros, checksum], the checksum the low byte of the sum of the
    // nineteen bytes before it. These are the frames the AULA F87 PRO software writes, read from its HID sessions. Command 0x05
    // asks for the receiver's state; the receiver answers with an 0x0A report: [0x13, 0x0A, 0x01, 0x00, length (4), source
    // command (0x05), percent, ...]. Only that answer is a reading.
    public const int AulaVendor = 0x3554;
    public const int AulaF87Pro = 0xFA09;
    public static byte[] AulaRequest(byte command)
    {
        var frame = new byte[20];
        frame[0] = 0x13; frame[1] = command; frame[2] = 0x01;
        int sum = 0; for (int i = 0; i < 19; i++) sum += frame[i];
        frame[19] = (byte)sum;
        return frame;
    }
    public static BatteryRead? AulaBattery(byte[] reply)
        => reply.Length >= 7 && reply[0] == 0x13 && reply[1] == 0x0A && reply[5] == 0x05 && reply[6] <= 100 ? new(reply[6]) : null;

    // ATK, VXE and VGN on Nordic receivers (VID 0x373B, usage page 0xFF05, report 0x08, 64 bytes): [checksum, 0x72, parameter
    // length, sequence, target (1 the mouse behind a receiver), command, payload length, payload...], the checksum the low byte
    // of the sum of the bytes after it. Battery is command 0x07.
    public const int AtkVendor = 0x373B;
    public static byte[] BitMouseRequest(byte command, byte target = 1)
    {
        var frame = new byte[64];
        frame[0] = 0x08; frame[2] = 0x72; frame[3] = 0x02; frame[4] = 0; frame[5] = target; frame[6] = command; frame[7] = 0x01;
        int sum = 0; for (int i = 2; i < 64; i++) sum += frame[i];
        frame[1] = (byte)sum;
        return frame;
    }
    // The reply: [0x08, 0x72, status (0 the mouse answered), _, target, command, length, percent, millivolts little endian]. A
    // wired mouse asked for itself gives 0 % at 0 mV, which is no reading.
    public static BatteryRead? BitMouseBattery(byte[] reply)
    {
        if (reply.Length < 10 || reply[0] != 0x08 || reply[1] != 0x72 || reply[5] != 0x07 || reply[2] != 0) return null;
        int percent = reply[7], millivolts = reply[8] | (reply[9] << 8);
        if (percent > 100 || percent == 0 && millivolts < 2000) return null;
        return new(percent);
    }
    public static bool? BitMouseCharging(byte[] reply) => reply.Length >= 8 && reply[0] == 0x08 && reply[1] == 0x72 && reply[5] == 0x0F && reply[2] == 0 ? reply[7] == 1 : null;

    // Compx receivers, report 0x08, 17 bytes: [0x08, command, status, address high, address low, length, data × 10, checksum],
    // the checksum making the sum of all seventeen bytes 0x55. Battery is command 0x04. The request goes only to the receivers
    // atkctl documents: the ATK A9 Ultra (373B:1119 and 373B:111A, usage page 0xFF04, sent as feature report 8). Compx's own
    // vendor id 0x3554 and the page 0xFF02 (the VXE R1 and VGN F1 receivers) are not in atkctl, are not tested by us on those
    // receivers, and are NOT sent to: the same command byte could mean something else in another firmware.
    public static byte[] CompxRequest(byte command)
    {
        var frame = new byte[17];
        frame[0] = 0x08; frame[1] = command;
        int sum = 0; for (int i = 0; i < 16; i++) sum += frame[i];
        frame[16] = (byte)(0x55 - sum);
        return frame;
    }
    // The reply (atkctl): [0x08, 0x04, ..., percent at 6, 1 at 7 while charging].
    public static BatteryRead? CompxBattery(byte[] reply)
        => reply.Length >= 8 && reply[0] == 0x08 && reply[1] == 0x04 && reply[6] <= 100 ? new(reply[6], reply[7] == 1) : null;

    // VGN 2.4 GHz keyboards (320F:5088, usage page 0xFF1C, report 0x04, 64 bytes): command 0x1A; the reply echoes it at byte 3,
    // the percentage at 8 and charging at 9.
    public const int VgnVendor = 0x320F;
    public static byte[] VgnRequest() { var frame = new byte[64]; frame[0] = 0x04; frame[3] = 0x1A; frame[4] = 0x06; return frame; }
    public static BatteryRead? VgnBattery(byte[] reply) => reply.Length >= 10 && reply[0] == 0x04 && reply[3] == 0x1A && reply[8] <= 100 ? new(reply[8], reply[9] != 0) : null;

    // ATK's keyboards answer that same frame on the same page: the Z87 Pro's dongle (373B:1012, "ATK Z87 Dongle", usage page
    // 0xFF1C, usage 0x0092, 64-byte reports) answered 04 00 00 1A 06 00 00 00 5C ... to it on a keyboard at 92 %, which is what
    // VgnBattery reads. Measured; the sibling keyboards of the family are not, so only this pair is sent to.
    public static readonly (int Product, string Name)[] AtkKeyboards = [(0x1012, "ATK Z87 Pro")];
    public static string? AtkKeyboard(int product)
    {
        foreach (var (id, name) in AtkKeyboards) if (id == product) return name;
        return null;
    }

    // Endgame Gear (VID 0x3367, usage page 0xFF01, feature 0xA1): A1 B4; byte 1 is 0x01 for a reading, the percentage at 16.
    public const int EndgameVendor = 0x3367;
    public static byte[] EndgameRequest(int length) { var frame = new byte[Math.Max(2, length)]; frame[0] = 0xA1; frame[1] = 0xB4; return frame; }
    public static BatteryRead? EndgameBattery(byte[] reply) => reply.Length >= 17 && reply[0] == 0xA1 && reply[1] == 0x01 && reply[16] <= 100 ? new(reply[16]) : null;

    // VAXEE (VID 0x3057, usage page 0xFF05, feature 0x0E): 0E A5 0B 01 01; byte 5 is the level 0 to 20, five points each.
    public const int VaxeeVendor = 0x3057;
    public static byte[] VaxeeRequest(int length) { var frame = new byte[Math.Max(5, length)]; frame[0] = 0x0E; frame[1] = 0xA5; frame[2] = 0x0B; frame[3] = 0x01; frame[4] = 0x01; return frame; }
    public static BatteryRead? VaxeeBattery(byte[] reply) => reply.Length >= 6 && reply[0] == 0x0E && reply[5] <= 20 ? new(reply[5] * 5, Coarse: true) : null;

    // Nordic's own receivers (VID 0x1915, usage page 0xFFA0, feature 0x05, 32 bytes): 05 15 00 00 01, then the reply read back
    // ~90 ms later: the percentage at 9, charging at 10, full at 11, the mouse online at 12. 0x1915 is the chip maker's id, which
    // unrelated products also carry (the HP Prime wireless kit, Crazyradio), so only the Ninjutso Sora V2's receiver and cable
    // (NinjutsoProducts, from Fan4Metal's mouse_tray, facts only) are sent to. Offline, or 0 %, is the mouse asleep.
    public const int NordicVendor = 0x1915;
    public static readonly int[] NinjutsoProducts = [0xAE1C, 0xAE11];
    public static byte[] NordicRequest(int length) { var frame = new byte[Math.Max(5, length)]; frame[0] = 0x05; frame[1] = 0x15; frame[4] = 0x01; return frame; }
    public static BatteryRead? NordicBattery(byte[] reply)
    {
        if (reply.Length < 13 || reply[0] != 0x05 || reply[9] > 100) return null;
        if (reply[12] == 0 || reply[9] == 0) return new(null, Asleep: true);
        bool full = reply[11] != 0;
        return new(full ? 100 : reply[9], reply[10] != 0 && !full);
    }

    // ----- Compx's 17-byte frame on its own collection -----

    // The Compx frame above (report 0x08, command 0x04, the checksum making all 17 bytes sum to 0x55), written as an output
    // report on the collection with usage page 0xFF02, usage 0x0002 and 17-byte input and output reports, and answered on its
    // input report: [0x08, 0x04, ..., status at 5, percent at 6, 1 at 7 on the cable (charging), millivolts big endian at 8].
    // Measured by vigil-watt on ATK receivers and mice on their cables, and by HaloBattery on Pulsar, VXE and Hitscan; the rest
    // of the list is Fan4Metal's mouse_tray model table (facts only). The page alone is not enough: the VXE R1 Nearlink has a
    // 20-byte 0xFF02 collection as well, and the VIA collection (0xFF60) echoes whatever it is sent. So only these vendor and
    // product pairs, on the 17-byte collection, are sent to.
    public static readonly (int Vendor, int Product)[] CompxFramed =
    [
        // ATK Wireless Charging dongle: measured on its 17-byte 0xFF02 / usage 2 collection.
        (AtkVendor, 0x12DA),
        // ATK receivers (F1 Pro Max 8K and 1K, F1 V3 Ultimate+, F1 Ultimate 2.0, A9 Air GEM) and mice on their cables.
        (AtkVendor, 0x101B), (AtkVendor, 0x101A), (AtkVendor, 0x1303), (AtkVendor, 0x11D9), (AtkVendor, 0x11E4), (AtkVendor, 0x1278),
        (AtkVendor, 0x1015), (AtkVendor, 0x12A9), (AtkVendor, 0x1293),
        // VXE R1 Nearlink, VXE R1 SE+ (1K receiver), ATK F1 Ultimate, ATK A9 Ultimate's cable, VXE MAD R and MAD R Major Plus.
        (AtkVendor, 0x10C9), (AtkVendor, 0x10C7), (AtkVendor, 0x1085), (AtkVendor, 0x1031), (AtkVendor, 0x102E), (AtkVendor, 0x11B6),
        (AtkVendor, 0x104D), (AtkVendor, 0x103F), (AtkVendor, 0x1040), (AtkVendor, 0x104C),
        // Pulsar X2 V2 Mini, VXE R1 Pro Max, VXE R1 SE+, VGN F2 Master+, VGN F1 Pro, VGN F2 Pro Max, Zaopin Z2 Mini, Scyrox V8.
        (0x3554, 0xF508), (0x3554, 0xF507), (0x3554, 0xF58A), (0x3554, 0xF58C), (0x3554, 0xF58E), (0x3554, 0xF58F),
        (0x3554, 0xFB56), (0x3554, 0xFB57), (0x3554, 0xF503), (0x3554, 0xF502), (0x3554, 0xFB3E), (0x3554, 0xFB3D),
        (0x3554, 0xF524), (0x3554, 0xF526), (0x3554, 0xF5F7), (0x3554, 0xF5F6),
        // Pulsar X2 CrazyLight Mini, Hitscan Hyperlight, HECATE's receiver and mice, Dareu A950 Air and Wing.
        (0x3710, 0x5406), (0x3710, 0x3414), (0x3770, 0x0200), (0x3770, 0x0100),
        (0x35BB, 0xD3FB), (0x35BB, 0xD3FD), (0x35BB, 0xD3FA), (0x35BB, 0xD3FC),
        (0x260D, 0x1175), (0x260D, 0x1193), (0x260D, 0x1154), (0x260D, 0x1196),
    ];
    public static bool IsCompxFramed(int vendor, int product) => CompxFramed.Contains((vendor, product));
    public static bool CompxFramedCollection(int usagePage, int usage, int inputLength, int outputLength)
        => usagePage == 0xFF02 && usage == 0x0002 && inputLength == 17 && outputLength == 17;
    // A reply counts only when its checksum holds and it answers command 0x04: the receivers push 0x03 frames of their own on
    // the same collection, and some echo the request back (its checksum right, every byte from 2 to 15 zero).
    public static BatteryRead? CompxFrame(byte[] reply)
    {
        if (reply.Length < 17 || reply[0] != 0x08 || reply[1] != 0x04 || reply[6] > 100) return null;
        int sum = 0; for (int i = 0; i < 17; i++) sum += reply[i];
        if ((byte)sum != 0x55) return null;
        bool echo = true; for (int i = 2; i < 16; i++) echo &= reply[i] == 0;
        return echo ? null : new(reply[6], reply[7] == 1);
    }

    // ----- The 0x83 feature exchange (WLmouse, G-Wolves, LAMZU, MCHOSE A5 Pro Max) -----

    // One firmware family's exchange, on a vendor collection with a 64-byte feature report and no report id (FeatureLength 65
    // with the id byte): set feature report 0 to 00 00 02 02 00 83 (marker, route 2, length 2, page 0, command 0x83) and read
    // feature report 0 back until it answers A1 00 02 02 00 83 <charging> <percent>. As HaloBattery reads it (wlmouse.py,
    // gwolves.py, lamzu.py, mchose.py: request, reply offsets, the 15 reads 50 ms apart). G-Wolves' web driver shows "device
    // not active" when the marker is not A1, so an A2 there is the mouse asleep (WLmouse answers A2 with a reading); MCHOSE's
    // reference clamps the level to 100. The older G-Wolves models take 00 02 8F <1 over a receiver, 0 on the cable> and answer
    // A1 02 8F .. <charging> <percent>. Which collection: interface 2 on the LAMZU mice; the 0xFFFF page elsewhere; on G-Wolves
    // the collection whose feature report is exactly 65 bytes, as its web driver picks it.
    public enum F83Exchange { New, Old }
    // Receiver: for a mouse on its cable, the product of its receiver (Twins).
    public sealed record F83Model(int Vendor, int Product, string Name, bool Cable, F83Exchange Exchange = F83Exchange.New, bool Strict = false,
        bool Clamp = false, int Interface = -1, int Receiver = -1);
    public const int WlmouseVendor = 0x36A7, GwolvesVendor = 0x33E4;
    public static readonly IReadOnlyList<F83Model> F83Models = BuildF83();
    private static List<F83Model> BuildF83()
    {
        var models = new List<F83Model>
        {
            new(WlmouseVendor, 0xA880, "WLmouse Beast X Max", false), new(WlmouseVendor, 0xA887, "WLmouse Beast X", false),
            new(WlmouseVendor, 0xA868, "WLmouse Beast X Mini Pro", false),
            new(0x373E, 0x001E, "LAMZU Maya X", false, Interface: 2), new(0x373E, 0x001C, "LAMZU Maya X", true, Interface: 2, Receiver: 0x001E),
            new(0x37B0, 0x0010, "LAMZU Inca", false, Interface: 2), new(0x37B0, 0x0009, "LAMZU Inca", true, Interface: 2, Receiver: 0x0010),
            new(0x2023, 0xF013, "MCHOSE A5 Pro Max", false, Clamp: true), new(0x2023, 0xF015, "MCHOSE A5 Pro Max", false, Clamp: true),
            new(0x2023, 0xF019, "MCHOSE A5 Pro Max", true, Clamp: true),
            // The shared 8K receiver does not say which mouse is behind it.
            new(GwolvesVendor, 0x3854, "G-Wolves", false, Strict: true),
        };
        // The mice on the shared receiver, on their cables (G-Wolves' web driver's PIDWired).
        foreach (var (product, name) in new[] { (0x5418, "HTS Plus"), (0x5419, "HTS Plus"), (0x5219, "HTS Plus Pro"), (0x4718, "Lycan"), (0x4719, "Lycan"),
            (0x5618, "HTXU"), (0x5619, "HTXU"), (0x3619, "Fenrir Pro"), (0x3519, "Fenrir Asym"), (0x2719, "HTX Mini"), (0x4219, "WARG") })
            models.Add(new(GwolvesVendor, product, "G-Wolves " + name, true, Strict: true, Receiver: 0x3854));
        // The models with a receiver of their own: (receiver, cable), the exchange its web driver uses.
        foreach (var (receiver, cable, name, exchange) in new[]
        {
            (0x3817, 0x3808, "HTM Plus", F83Exchange.New), (0x6817, 0x6808, "HSK Pro 2.0", F83Exchange.New), (0x5617, 0x5608, "HTXU", F83Exchange.New),
            (0x3617, 0x3608, "Fenrir Pro", F83Exchange.New), (0x3917, 0x3908, "VUK", F83Exchange.Old), (0x7913, 0x7904, "HT-S2", F83Exchange.Old),
            (0x3717, 0x3708, "Fenrir Max", F83Exchange.Old), (0x5317, 0x5308, "HTS Ultra", F83Exchange.Old), (0x7713, 0x7704, "HTR", F83Exchange.Old),
            (0x3517, 0x3508, "Fenrir", F83Exchange.Old), (0x7917, 0x7908, "HT-S2 Pro", F83Exchange.Old), (0x5817, 0x5808, "HSK Pro", F83Exchange.Old),
            (0x5807, -1, "HSK Pro", F83Exchange.Old), (0x2717, 0x2708, "HTX Mini", F83Exchange.Old), (0x5417, 0x5408, "HTS Plus", F83Exchange.Old),
            (0x5407, -1, "HTS Plus", F83Exchange.Old), (0x5717, 0x5708, "HTX", F83Exchange.Old), (0x5707, -1, "HTX", F83Exchange.Old),
            (0x5917, 0x5908, "HSK Plus", F83Exchange.Old), (0x5907, -1, "HSK Plus", F83Exchange.Old), (0x7203, 0x7204, "HSK Lite", F83Exchange.Old),
            (0x7717, 0x7708, "HTR Pro", F83Exchange.Old), (0x5803, 0x5804, "HSK Pro ACE", F83Exchange.Old), (0x5403, 0x5404, "HTS Plus ACE", F83Exchange.Old),
            (0x5703, 0x5704, "HTX ACE", F83Exchange.Old), (0x5903, 0x5904, "HSK Plus ACE", F83Exchange.Old),
        })
        {
            models.Add(new(GwolvesVendor, receiver, "G-Wolves " + name, false, exchange, Strict: true));
            if (cable >= 0) models.Add(new(GwolvesVendor, cable, "G-Wolves " + name, true, exchange, Strict: true, Receiver: receiver));
        }
        return models;
    }
    public static F83Model? F83Find(int vendor, int product) => F83Models.FirstOrDefault(model => model.Vendor == vendor && model.Product == product);
    public static bool F83Collection(F83Model model, int iface, int usagePage, int featureLength)
        => model.Vendor == GwolvesVendor ? featureLength == 65
        : usagePage == 0xFFFF && featureLength >= 65 && (model.Interface < 0 || iface == model.Interface);

    // The request as a feature report: report id 0, then the 64 bytes.
    public static byte[] F83Request(F83Model model)
    {
        var frame = new byte[65];
        if (model.Exchange == F83Exchange.Old) { frame[2] = 0x02; frame[3] = 0x8F; frame[4] = (byte)(model.Cable ? 0x00 : 0x01); }
        else { frame[3] = 0x02; frame[4] = 0x02; frame[6] = 0x83; }
        return frame;
    }
    // The reply, with or without its report id byte in front; null for anything else (the request read back, another
    // command's answer). Asleep: a strict model answered with a marker other than A1.
    public static BatteryRead? F83Battery(F83Model model, byte[] reply)
    {
        foreach (int at in new[] { 1, 0 })
        {
            if (reply.Length < at + (model.Exchange == F83Exchange.Old ? 6 : 8)) continue;
            byte marker = reply[at];
            bool shaped = model.Exchange == F83Exchange.Old
                ? reply[at + 1] == 0x02 && reply[at + 2] == 0x8F
                : reply[at + 4] == 0x00 && reply[at + 5] == 0x83;
            if (!shaped || marker is not (0xA1 or 0xA2)) continue;
            if (marker == 0xA2 && (model.Strict || model.Exchange == F83Exchange.Old)) return new(null, Asleep: true);
            if (marker == 0xA2 && model.Clamp) continue;
            int charging = model.Exchange == F83Exchange.Old ? reply[at + 4] : reply[at + 6], level = model.Exchange == F83Exchange.Old ? reply[at + 5] : reply[at + 7];
            if (level > 100 && !model.Clamp) return null;
            return new(Math.Min(level, 100), charging != 0);
        }
        return null;
    }

    // ----- MCHOSE on its RealTek receivers (5253) and the Gmouse query -----

    // The 0x5253 receivers (M7 Ultra, L7 family; 5253:1020, the mouse on its cable 5253:0031), usage page 0xFF01: feature report
    // 0x11 (20 bytes) or 0x12 (64) carrying command 0x06 with every payload byte inverted, set and then got back, asked afresh
    // each time (one ask read twice only returns the request). The reply echoes 0xF9 (0x06 inverted) at 1 and inverts its
    // payload from 2: vendor id and model little endian, firmware (4 bytes), flags, level, charging. From HaloBattery's
    // mchose.py (measured on an M7 Ultra), after alexfrih/mchose-linux's PROTOCOL.md.
    public const int MchoseRealtek = 0x5253;
    public static readonly int[] MchoseRealtekProducts = [0x1020, 0x0031];
    public static byte[] MchoseRealtekRequest(bool longReport)
    {
        var frame = new byte[longReport ? 65 : 21];
        frame[0] = (byte)(longReport ? 0x12 : 0x11);
        frame[1] = 0x06 ^ 0xFF;
        for (int i = 2; i < frame.Length; i++) frame[i] = 0xFF;
        return frame;
    }
    public static (BatteryRead Read, int Model)? MchoseRealtekBattery(byte[] reply)
    {
        if (reply.Length < 13 || reply[0] is not (0x11 or 0x12) || reply[1] != (0x06 ^ 0xFF)) return null;
        int Byte(int at) => reply[2 + at] ^ 0xFF;
        int vendor = Byte(0) | Byte(1) << 8;
        if (vendor is not (MchoseRealtek or MchoseVendor) || Byte(9) > 100) return null;
        return (new(Byte(9), Byte(10) != 0), Byte(2) | Byte(3) << 8);
    }
    public static string? MchoseRealtekModel(int model) => model == 0x0031 ? "MCHOSE M7 Ultra" : null;

    // The Gmouse query (MCHOSE G3 V2 on 3837:4245 and G7 on A8A5:2255, collection 0xFF01 usage 0x10): a 65-byte output
    // report 00 55 30 A5 0B 2E 01 01 01, answered by an input report starting AA 30 with the level at 8 and charging at 9. As
    // vigil-watt (G3 V2, measured) and HaloBattery (G7, measured) read it.
    public static readonly (int Vendor, int Product)[] Gmouse = [(MchoseVendor, 0x4245), (0xA8A5, 0x2255)];
    public static byte[] GmouseRequest() { var frame = new byte[65]; new byte[] { 0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01 }.CopyTo(frame, 0); return frame; }
    public static BatteryRead? GmouseBattery(byte[] reply)
    {
        int at = reply.Length > 1 && reply[0] == 0xAA && reply[1] == 0x30 ? 0 : reply.Length > 2 && reply[1] == 0xAA && reply[2] == 0x30 ? 1 : -1;
        if (at < 0 || reply.Length < at + 10 || reply[at + 8] > 100) return null;
        return new(reply[at + 8], reply[at + 9] != 0);
    }

    // ----- Others asked once per pass -----

    // LINGBAO M5PRO Max on its receiver (3151:402D, interface 2, usage page 0xFFFF usage 2, a 65-byte feature report): set
    // 00 F7, get report 0 back: [0, ack, 0 (status), percent, 0x01, 1 charging]. vigil-watt captured it from the vendor's
    // driver; the mouse on its cable (3151:4026) only echoes the command, and is not sent to.
    public static byte[] LingbaoRequest() { var frame = new byte[65]; frame[1] = 0xF7; return frame; }
    public static BatteryRead? LingbaoBattery(byte[] reply)
        => reply.Length >= 6 && reply[0] == 0 && reply[2] == 0 && reply[4] == 0x01 && reply[3] <= 100 ? new(reply[3], reply[5] == 1) : null;

    // NZXT Lift Elite (1E71:2101 receiver, 2129 the mouse on its cable; usage page 0xFFCA usage 1): the output report 4E 02 81
    // 00 B0 (read the telemetry of the mouse behind the dongle), answered 4E 02 97 after a 4E E5 acknowledgement: the percent
    // little endian at 17, bit 0 of 22 while on its cable. From HaloBattery's nzxt.py (captures of NZXT CAM).
    public static byte[] NzxtRequest() { var frame = new byte[64]; frame[0] = 0x4E; frame[1] = 0x02; frame[2] = 0x81; frame[4] = 0xB0; return frame; }
    public static BatteryRead? NzxtBattery(byte[] reply)
    {
        if (reply.Length < 27 || reply[0] != 0x4E || reply[1] != 0x02 || reply[2] != 0x97) return null;
        int level = reply[17] | reply[18] << 8;
        return level <= 100 ? new(level, (reply[22] & 0x01) != 0) : null;
    }

    // Block 0x1A on the 0xFF1C / 0x0092 collection (report 0x04, 64 bytes): 04 00 00 1A <length>, answered with the block
    // number echoed at 3 and its data from 8: the percent, then the charging state. CHERRY PIXIU75 (046A:01E4 receiver, 01E2 on
    // its cable; asks for 4 bytes, bit 0 of 9 charging) and HECATE G5M Pro (35BB:D00B; asks with no length, 9 is 0 discharging,
    // 1 charging, 2 full), as vigil-watt measured them. A heartbeat (04 AA) carries another block number.
    public enum StatusBlock { Cherry, Hecate }
    public static byte[] StatusBlockRequest(StatusBlock kind) { var frame = new byte[64]; frame[0] = 0x04; frame[3] = 0x1A; frame[4] = (byte)(kind == StatusBlock.Cherry ? 0x04 : 0x00); return frame; }
    public static BatteryRead? StatusBlockBattery(StatusBlock kind, byte[] reply)
        => reply.Length >= 10 && reply[0] == 0x04 && reply[3] == 0x1A && reply[8] <= 100
            ? new(reply[8], kind == StatusBlock.Cherry ? (reply[9] & 0x01) != 0 : reply[9] == 1) : null;

    // Rapoo's first generation (VT3 PRO: 24AE:1231 its receiver, 4431 on its cable), after mousectl's rapoo_vt3pro driver
    // (MIT; measured on a 1231 receiver): output report 0xBA, 32 bytes [link (A5 the receiver, FF the cable), AA (the battery
    // query; A4 and A5 there read and write the mouse's memory, never sent here), length 0, address 0 (4 bytes)], answered
    // through the input report 0xBA got back: [status, state (1 discharging, 2 charging), percent]. The status reads busy (02)
    // once the receiver has taken the request and 01 with the answer; a 01 before any busy is still the last answer. A
    // block answer has 0 where the state goes, so a stale one is told apart. Not tried by us on a mouse.
    public const int RapooVendor = 0x24AE;
    public static readonly int[] RapooGen1Products = [0x1231, 0x4431];
    public const byte RapooGen1Report = 0xBA;
    public static byte[] RapooGen1Request(bool cable) { var frame = new byte[33]; frame[0] = RapooGen1Report; frame[1] = (byte)(cable ? 0xFF : 0xA5); frame[2] = 0xAA; return frame; }
    // The status of an input report got back: true OK, false busy, null another report.
    public static bool? RapooGen1Status(byte[] reply)
    {
        int at = reply.Length > 1 && reply[0] == RapooGen1Report ? 1 : 0;
        return reply.Length > at ? reply[at] == 0x01 : null;
    }
    public static BatteryRead? RapooGen1Battery(byte[] reply)
    {
        int at = reply.Length > 1 && reply[0] == RapooGen1Report ? 1 : 0;
        if (reply.Length < at + 3 || reply[at] != 0x01 || reply[at + 1] is not (1 or 2)) return null;
        return new(Math.Min((int)reply[at + 2], 100), reply[at + 1] == 2);
    }

    // A receiver and the same mouse on its cable, where the receiver reports the mouse asleep while the cable reads it: the
    // receiver's entry is left out when its cable read the mouse in the same pass ("VVVV:PPPP" each).
    public static readonly (string Receiver, string Cable)[] Twins = BuildTwins();
    private static (string, string)[] BuildTwins()
    {
        var twins = new List<(string, string)>();
        void Pair(int vendor, int receiver, int cable) => twins.Add((Devices.Hardware(vendor, receiver), Devices.Hardware(vendor, cable)));
        foreach (var cable in F83Models.Where(model => model.Receiver >= 0)) Pair(cable.Vendor, cable.Receiver, cable.Product);
        Pair(0x2023, 0xF013, 0xF019); Pair(0x2023, 0xF015, 0xF019);
        Pair(NordicVendor, 0xAE1C, 0xAE11);
        Pair(0x1E71, 0x2101, 0x2129);
        foreach (var (receiver, cable) in Asus.Pairs) Pair(Asus.Vendor, receiver, cable);
        foreach (var (receiver, cable) in SteelSeriesMice.Pairs) Pair(Headsets.SteelSeries, receiver, cable);
        return [.. twins.Distinct()];
    }
    public static bool Shadowed(DeviceBattery device, IEnumerable<DeviceBattery> devices)
        => device.Asleep && device.Hardware is { } hardware && Twins.Any(twin => twin.Receiver == hardware
            && devices.Any(other => !other.Asleep && other.Percent is not null && other.Hardware == twin.Cable));

    // Inphic IN9 (248A:5B2E cable, 5B2F receiver; on 2.4 GHz vendor 0x249A, whose product is not fixed; feature 0x0C, 33
    // bytes): [0x0C, 0x01, command, 0, sequence, payload...]; 0xF0 the handshake (payload 01 01 FF), 0x20 the battery, the two
    // with one sequence number. The reply echoes 0x20 at 2, is valid with byte 6 set, the percentage at 0x12, 2 at 0x13 charging.
    public const int InphicVendor = 0x248A, InphicWireless = 0x249A;
    public static readonly byte[] InphicHandshake = [0x01, 0x01, 0xFF];
    public static byte[] InphicRequest(byte command, byte sequence, params byte[] payload)
    {
        var frame = new byte[33];
        frame[0] = 0x0C; frame[1] = 0x01; frame[2] = command; frame[4] = sequence;
        Array.Copy(payload, 0, frame, 5, Math.Min(payload.Length, frame.Length - 5));
        return frame;
    }
    public static BatteryRead? InphicBattery(byte[] reply) => reply.Length >= 0x14 && reply[0] == 0x0C && reply[2] == 0x20 && reply[6] != 0 && reply[0x12] <= 100 ? new(reply[0x12], reply[0x13] == 2) : null;

    // MCHOSE mice (VID 0x3837; usage page 0xFF01 or 0xFF0B; output report 0x4D, 64 bytes), from the vendor's web driver. Not
    // tested by us on a receiver. Realtek's 0x0BDA:0x4762 is not sent to: Realtek's id is on countless products and the pair is
    // not verified:
    // [0x4D, 0x01, flags 1 (checksummed), length, command low, command high, 0, 0, data..., XOR of bytes 2 to 7 + length].
    // Device info is command 0x0900: its payload (from byte 8) holds the connection at 10, charging at 11 and the level at 12;
    // a level of 0 means the mouse is not connected, and a first byte of 0xFF that the receiver is not ready.
    public const int MchoseVendor = 0x3837;
    public static byte[] MchoseRequest()
    {
        var frame = new byte[64];
        frame[0] = 0x4D; frame[1] = 0x01; frame[2] = 0x01; frame[3] = 0x00; frame[4] = 0x00; frame[5] = 0x09;
        byte xor = 0; for (int i = 2; i < 8; i++) xor ^= frame[i];
        frame[8] = xor;
        return frame;
    }
    // The MCHOSE devices the mouse protocol is sent to: those that name themselves a mouse (its keyboards, headsets and the
    // MagDock 3837:1012 speak other protocols).
    public static bool IsMchoseMouse(int product, string name) => product is not (0x1012 or 0x6045)
        && (name.Contains("mouse", StringComparison.OrdinalIgnoreCase) || name.Contains("鼠标", StringComparison.Ordinal));
    public static BatteryRead? MchoseBattery(byte[] reply)
    {
        if (reply.Length < 21 || reply[0] != 0x4D || reply[4] != 0x00 || reply[5] != 0x09 || reply[8] == 0xFF) return null;
        int level = reply[8 + 12];
        return level is > 0 and <= 100 ? new(level, reply[8 + 11] != 0) : null;
    }

    // AJAZZ AJ179-family dual-mode mice on the XCTECH 2.4 GHz receiver (VID 0x249A PID 0x5C2F, HID MI_02) and the documented
    // USB-cable PIDs on vendor 0x248A (5C2E / 5D2E / 5E2E — from aj179-linux PROTOCOL.md; the reporter's Zero PC verified the
    // wireless 249A:5C2F / device id M041 only). Framing is a 32-byte vendor report on interrupt IN/OUT (Windows WriteFile
    // buffer length = OutputReportByteLength with a leading report-id 0). Command 0x10 is answered by the receiver even while
    // the mouse sleeps: battery at byte 13, charging at 12, connected at 14; unsolicited 0xC0 carries online at 1 and battery
    // at 2. Sleep must keep the last percent (Asleep), never a false 0 %.
    public const int AjazzWirelessVendor = 0x249A, AjazzReceiverProduct = 0x5C2F;
    public const int AjazzWiredVendor = 0x248A;
    public static readonly int[] AjazzWiredProducts = [0x5C2E, 0x5D2E, 0x5E2E];
    public const int AjazzReportLength = 32;
    public const byte AjazzCmdInfo = 0x10, AjazzStatus = 0xC0;

    public static bool IsAjazzReceiver(int product) => product == AjazzReceiverProduct;
    public static bool IsAjazzWired(int product) => Array.IndexOf(AjazzWiredProducts, product) >= 0;
    public static bool IsAjazz(int vendor, int product)
        => vendor == AjazzWirelessVendor && IsAjazzReceiver(product)
        || vendor == AjazzWiredVendor && IsAjazzWired(product);

    // The config collection: MI_02 (UsagePage 0x0001 / Usage 0x0000), In/Out report length 33 with report-id 0 on Windows.
    public static bool AjazzCollection(int iface, int usagePage, int usage, int inputLength, int outputLength)
    {
        if (outputLength < 32 || inputLength < 32) return false;
        if (iface == 2) return true;
        return usagePage == 0x01 && usage == 0x00 && outputLength >= 33 && inputLength >= 33;
    }

    // Report-id 0 + 32-byte vendor frame: cmd at [1], seq/len 0, checksum = sum(payload bytes [5..31]) & 0xFF at [32].
    public static byte[] AjazzInfoRequest()
    {
        var frame = new byte[AjazzReportLength + 1];
        frame[1] = AjazzCmdInfo;
        int sum = 0;
        for (int i = 5; i < frame.Length - 1; i++) sum += frame[i];
        frame[^1] = (byte)(sum & 0xFF);
        return frame;
    }

    // Parsed 0x10 info. DeviceId is the four ASCII chars at bytes 4..7 (e.g. M041 / M179) when printable.
    public readonly record struct AjazzInfo(int Percent, bool Charging, bool Connected, string DeviceId);

    public static AjazzInfo? AjazzParseInfo(byte[] reply)
    {
        int o = reply.Length > 15 && reply[0] == 0 && reply[1] == AjazzCmdInfo ? 1 : 0;
        if (reply.Length < o + 15 || reply[o] != AjazzCmdInfo) return null;
        int bat = reply[o + 13];
        if (bat > 100) return null;
        bool connected = reply[o + 14] != 0;
        // Asleep + 0 % with no other evidence is not reported as empty — avoids a false zero while the link is down.
        if (!connected && bat == 0) return null;
        return new(bat, reply[o + 12] != 0, connected, AjazzDeviceId(reply, o + 4));
    }

    public static (bool Online, int? Percent)? AjazzParseStatus(byte[] reply)
    {
        int o = reply.Length > 2 && reply[0] == 0 && reply[1] == AjazzStatus ? 1 : 0;
        if (reply.Length < o + 3 || reply[o] != AjazzStatus) return null;
        int bat = reply[o + 2];
        return (reply[o + 1] == 1, bat <= 100 ? bat : null);
    }

    // cable: read on the mouse's own cable. There the mouse answers for itself and has no idle or asleep status (aj179-linux
    // PROTOCOL.md), so it is never asleep, whatever byte 14 says.
    public static BatteryRead? AjazzBattery(AjazzInfo info, bool cable = false)
        => new(info.Percent, info.Charging, Asleep: !cable && !info.Connected);

    // A mouse on its cable with its receiver also plugged in is read twice: on the cable, and through the receiver, which then
    // reports it not connected (aj179-linux PROTOCOL.md), so asleep. The receiver's entry is left out when the cable read the
    // mouse of the same id in the same pass.
    public static bool AjazzShadowed(DeviceBattery device, IEnumerable<DeviceBattery> devices)
        => device.Asleep && device.Hardware == Devices.Hardware(AjazzWirelessVendor, AjazzReceiverProduct)
        && devices.Any(other => other.Hardware is { } hardware && IsAjazzCable(hardware) && !other.Asleep && other.Percent is not null && other.Name == device.Name);
    private static bool IsAjazzCable(string hardware) => AjazzWiredProducts.Any(product => hardware == Devices.Hardware(AjazzWiredVendor, product));

    private static string AjazzDeviceId(byte[] reply, int at)
    {
        if (reply.Length < at + 4) return "";
        Span<char> chars = stackalloc char[4];
        int n = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = reply[at + i];
            if (b is < 0x20 or > 0x7E) break;
            chars[n++] = (char)b;
        }
        return n == 0 ? "" : new string(chars[..n]);
    }

    // The 2.4 GHz protocols a request is sent with.
    public enum Protocol
    {
        BitMouse, Compx, Vgn, Endgame, Vaxee, Inphic, Mchose, Ajazz, CompxFramed, F83, Nordic, MchoseRealtek, Gmouse, Lingbao, Nzxt, Cherry, Hecate,
        Asus, SteelSeries, RapooGen1, Aula
    }

    // The Compx receivers atkctl documents (the ATK A9 Ultra), by vendor, product and usage page.
    private static readonly (int Vendor, int Product, int UsagePage)[] CompxVerified = [(AtkVendor, 0x1119, 0xFF04), (AtkVendor, 0x111A, 0xFF04)];

    // Which protocol, if any, a USB collection is sent a battery request with. A vendor id and a usage page alone are not
    // enough where the id is shared with other products (Compx's 0x3554, Nordic's 0x1915, Realtek's 0x0BDA): "read only" holds
    // only if the protocol is the right one, and the same command byte can be another operation in another firmware. So those
    // go only to a vendor and product pair that is verified (CompxVerified), and the rest of the list is the vendors' own ids.
    // Their frames come from the sources named above and have not been tried by us on every receiver.
    // The AJAZZ receiver and cables are known vendor and product pairs (IsAjazz) and are matched first: the receiver shares Inphic's
    // 2.4 GHz vendor id 0x249A, and only its configuration collection (AjazzCollection) is sent to, never Inphic's frame. The
    // interface, usage and report lengths are asked for that collection only.
    // The products below with a protocol of their own (CompxFramed, the 0x83 family, Gmouse, ASUS, SteelSeries's mice and the
    // rest) are matched by vendor and product before the vendor-wide rules, and only their one collection is sent to: no
    // other collection of theirs is given another brand's frame (an ATK receiver's 0xFF05 collection gets no BitMouse request).
    public static Protocol? Find(int vendor, int product, int usagePage, string productName, int featureLength, int iface = -1, int usage = -1,
        int inputLength = 0, int outputLength = 0, int collection = -1)
    {
        if (IsAjazz(vendor, product)) return AjazzCollection(iface, usagePage, usage, inputLength, outputLength) ? Protocol.Ajazz : null;
        if (vendor == AulaVendor && product == AulaF87Pro) return usagePage == 0xFF02 && usage == 0x0002 && inputLength >= 20 && outputLength >= 20 ? Protocol.Aula : null;
        if (IsCompxFramed(vendor, product)) return CompxFramedCollection(usagePage, usage, inputLength, outputLength) ? Protocol.CompxFramed : null;
        if (F83Find(vendor, product) is { } f83) return F83Collection(f83, iface, usagePage, featureLength) ? Protocol.F83 : null;
        if (Gmouse.Contains((vendor, product))) return usagePage == 0xFF01 && usage == 0x0010 && outputLength >= 65 ? Protocol.Gmouse : null;
        if (vendor == NordicVendor && NinjutsoProducts.Contains(product)) return usagePage == 0xFFA0 && featureLength >= 32 ? Protocol.Nordic : null;
        if (vendor == MchoseRealtek && MchoseRealtekProducts.Contains(product)) return usagePage == 0xFF01 && featureLength >= 21 ? Protocol.MchoseRealtek : null;
        if (vendor == 0x3151 && product == 0x402D) return iface == 2 && usagePage == 0xFFFF && usage == 0x0002 && featureLength == 65 ? Protocol.Lingbao : null;
        if (vendor == 0x1E71 && product is 0x2101 or 0x2129) return usagePage == 0xFFCA && usage == 0x0001 && outputLength >= 64 ? Protocol.Nzxt : null;
        if (vendor == 0x046A && product is 0x01E4 or 0x01E2) return usagePage == 0xFF1C && usage == 0x0092 && outputLength >= 64 ? Protocol.Cherry : null;
        if (vendor == 0x35BB && product == 0xD00B) return usagePage == 0xFF1C && usage == 0x0092 && outputLength >= 64 ? Protocol.Hecate : null;
        if (Asus.Find(vendor, product) is { } asus) return Asus.Collection(asus, iface, collection, usagePage, outputLength) ? Protocol.Asus : null;
        if (SteelSeriesMice.Find(vendor, product) is { } mouse) return SteelSeriesMice.Collection(mouse, iface, usagePage, outputLength) ? Protocol.SteelSeries : null;
        if (vendor == RapooVendor && RapooGen1Products.Contains(product)) return usagePage >= 0xFF00 && outputLength >= 33 ? Protocol.RapooGen1 : null;
        if (vendor == AtkVendor && usagePage == 0xFF05) return Protocol.BitMouse;
        if (CompxVerified.Contains((vendor, product, usagePage))) return Protocol.Compx;
        if (vendor == VgnVendor && usagePage == 0xFF1C && product == 0x5088) return Protocol.Vgn;
        // ATK's keyboards on the same page, answered with VGN's frame (AtkKeyboards): only a product measured here, and only its
        // keyboard collection.
        if (vendor == AtkVendor && AtkKeyboard(product) is not null) return usagePage == 0xFF1C && usage == 0x0092 && outputLength >= 64 ? Protocol.Vgn : null;
        if (vendor == EndgameVendor && usagePage == 0xFF01) return Protocol.Endgame;
        if (vendor == VaxeeVendor && usagePage == 0xFF05) return Protocol.Vaxee;
        if (vendor == InphicVendor && product is 0x5B2E or 0x5B2F && featureLength >= 33) return Protocol.Inphic;
        // The 2.4 GHz receiver's product is not fixed (InphicBatteryTray): only its vendor collection.
        if (vendor == InphicWireless && usagePage >= 0xFF00 && featureLength >= 33) return Protocol.Inphic;
        // MCHOSE's mouse protocol only on MCHOSE devices that call themselves a mouse (its keyboards, headsets and the MagDock
        // speak other protocols).
        if (vendor == MchoseVendor && usagePage is 0xFF01 or 0xFF0B && IsMchoseMouse(product, productName)) return Protocol.Mchose;
        return null;
    }
}

// ASUS ROG and TUF mice, keyboards and headsets on their own dongles and cables, from G-Helper's model classes (GPL; facts
// only): a 65-byte output report [report id, 0x12, 0x07] (keyboards 0x12 0x01) on the model's interface, answered by an input
// report echoing the id and the two bytes. Mice: the level at 5 (the Strix Carry at 7), a percent or, on the older models, a
// step 0 to 4 (× 25), charging at 10; 0 is the mouse in standby. Keyboards: the percent at 6 (the Falchion at 11), 1 at 9
// while charging. FF AA is a command the firmware does not know, and an answer of zeros is none. Headsets (Delta II, Pelta):
// report 0xCC, 12 07 with the percent at 6 (0xFE: not ready), then 12 08 with 1 at 5 while charging (vigil-watt measured it on
// a Delta II). The 51 37 energy command G-Helper also sends is a write and is never sent. The ROG Omni receiver (0B05:1ACE)
// says which models are paired through its pairing table (OmniTable, below).
public static class Asus
{
    public const int Vendor = 0x0B05;
    public enum Family { Mouse, Keyboard, Headset }
    // Steps: the level is a step 0 to 4. Collection: the HID collection (&col0N) where the interface splits, -1 for any.
    public sealed record Model(int Product, string Name, Family Family, int Interface, byte ReportId = 0, bool Steps = false, int LevelAt = 5, int Collection = -1);
    public static readonly IReadOnlyList<Model> Catalog =
    [
        new(0x18E5, "ROG Chakram", Family.Mouse, 0, Steps: true), new(0x18E3, "ROG Chakram", Family.Mouse, 0, Steps: true),
        new(0x1960, "ROG Keris Wireless", Family.Mouse, 0, Steps: true), new(0x195E, "ROG Keris Wireless", Family.Mouse, 0, Steps: true),
        new(0x1A59, "ROG Keris Wireless EVA Edition", Family.Mouse, 0, Steps: true), new(0x1A57, "ROG Keris Wireless EVA Edition", Family.Mouse, 0, Steps: true),
        new(0x1908, "ROG Pugio II", Family.Mouse, 0, Steps: true), new(0x1906, "ROG Pugio II", Family.Mouse, 0, Steps: true),
        new(0x1949, "ROG Strix Impact II Wireless", Family.Mouse, 0, Steps: true), new(0x1947, "ROG Strix Impact II Wireless", Family.Mouse, 0, Steps: true),
        new(0x18A0, "ROG Gladius II Wireless", Family.Mouse, 2, Steps: true), new(0x18B4, "ROG Strix Carry", Family.Mouse, 1, Steps: true, LevelAt: 7),
        new(0x1A72, "ROG Gladius III Aimpoint", Family.Mouse, 0), new(0x1A70, "ROG Gladius III Aimpoint", Family.Mouse, 0),
        new(0x1B0C, "ROG Gladius III Aimpoint EVA-02", Family.Mouse, 0), new(0x1B0A, "ROG Gladius III Aimpoint EVA-02", Family.Mouse, 0),
        new(0x197F, "ROG Gladius III Wireless", Family.Mouse, 0), new(0x197D, "ROG Gladius III Wireless", Family.Mouse, 0),
        new(0x1A1A, "ROG Chakram X", Family.Mouse, 0), new(0x1A18, "ROG Chakram X", Family.Mouse, 0),
        new(0x1A94, "ROG Harpe Ace Aim Lab Edition", Family.Mouse, 0), new(0x1A92, "ROG Harpe Ace Aim Lab Edition", Family.Mouse, 0),
        new(0x1B67, "ROG Harpe Ace Extreme", Family.Mouse, 0), new(0x1B63, "ROG Harpe Ace Mini", Family.Mouse, 0),
        new(0x1A68, "ROG Keris Wireless Aimpoint", Family.Mouse, 0), new(0x1A66, "ROG Keris Wireless Aimpoint", Family.Mouse, 0),
        new(0x1979, "ROG Spatha X", Family.Mouse, 0), new(0x1977, "ROG Spatha X", Family.Mouse, 0),
        new(0x19F4, "TUF Gaming M4 Wireless", Family.Mouse, 0), new(0x1A8D, "TX Gaming Mouse", Family.Mouse, 0),
        new(0x1AF5, "TX Gaming Mini", Family.Mouse, 0), new(0x1AF3, "TX Gaming Mini", Family.Mouse, 0),
        new(0x1C57, "TUF Gaming Mini Miku", Family.Mouse, 0), new(0x1C56, "TUF Gaming Mini Miku", Family.Mouse, 0),
        new(0x1C69, "ROG Harpe II Ace", Family.Mouse, 0), new(0x1AD0, "ROG Harpe II Ace", Family.Mouse, 2, ReportId: 0x03, Collection: 3),
        new(0x1B16, "ROG Keris II Ace", Family.Mouse, 0), new(0x1C0C, "ROG Keris II Origin", Family.Mouse, 0), new(0x1D4C, "ROG Keris II Origin KJP", Family.Mouse, 0),
        new(0x1A24, "ASUS MD200", Family.Mouse, 2), new(0x1CD3, "ASUS MD301", Family.Mouse, 3), new(0x1CD5, "ASUS MD301", Family.Mouse, 3),
        new(0x1A83, "ROG Azoth", Family.Keyboard, 1, LevelAt: 6), new(0x1A85, "ROG Azoth", Family.Keyboard, 1, LevelAt: 6),
        new(0x1B3F, "ROG Azoth Extreme", Family.Keyboard, 1, LevelAt: 6), new(0x1CEF, "ROG Azoth Extreme SE", Family.Keyboard, 1, LevelAt: 6),
        new(0x1C24, "ROG Azoth X", Family.Keyboard, 1, LevelAt: 6), new(0x1C25, "ROG Azoth X", Family.Keyboard, 1, LevelAt: 6),
        new(0x193C, "ROG Falchion", Family.Keyboard, 1, LevelAt: 11), new(0x193E, "ROG Falchion", Family.Keyboard, 1, LevelAt: 11),
        new(0x1AAE, "ROG Strix Scope II 96 Wireless", Family.Keyboard, 1, LevelAt: 6), new(0x1B78, "ROG Strix Scope II 96 RX Wireless", Family.Keyboard, 1, LevelAt: 6),
        new(0x1AFA, "ROG Delta II", Family.Headset, -1, ReportId: 0xCC, LevelAt: 6), new(0x1D41, "ROG Delta II", Family.Headset, -1, ReportId: 0xCC, LevelAt: 6),
        new(0x1B84, "ROG Pelta", Family.Headset, -1, ReportId: 0xCC, LevelAt: 6),
    ];
    // The mice with a receiver and a cable of their own (receiver, cable), for Dongles.Twins.
    public static readonly (int Receiver, int Cable)[] Pairs =
    [
        (0x18E5, 0x18E3), (0x1960, 0x195E), (0x1A59, 0x1A57), (0x1908, 0x1906), (0x1949, 0x1947), (0x1A72, 0x1A70), (0x1B0C, 0x1B0A), (0x197F, 0x197D),
        (0x1A1A, 0x1A18), (0x1A94, 0x1A92), (0x1A68, 0x1A66), (0x1979, 0x1977), (0x1AF5, 0x1AF3), (0x1C57, 0x1C56), (0x1AD0, 0x1C69), (0x1CD3, 0x1CD5),
    ];
    public static Model? Find(int vendor, int product) => vendor == Vendor ? Catalog.FirstOrDefault(model => model.Product == product) : null;
    // The model's interface and collection, with an output report the 65-byte request fits; a headset's on its vendor page.
    public static bool Collection(Model model, int iface, int collection, int usagePage, int outputLength)
        => outputLength >= 64 && (model.Interface < 0 || iface == model.Interface) && (model.Collection < 0 || collection == model.Collection)
        && (model.Family != Family.Headset || usagePage == 0xFF00);

    public static byte[] Request(Model model) => Frame(model, model.Family == Family.Keyboard ? (byte)0x01 : (byte)0x07);
    public static byte[]? ChargingRequest(Model model) => model.Family == Family.Headset ? Frame(model, 0x08) : null;
    private static byte[] Frame(Model model, byte command) { var frame = new byte[65]; frame[0] = model.ReportId; frame[1] = 0x12; frame[2] = command; return frame; }

    // What a report says to the request: a reading, Refused (the firmware does not know the command, answered nothing, or
    // answered with a level out of range), or null for another report.
    public static readonly object Refused = new();
    public static object? Reply(Model model, byte[] r)
    {
        byte command = model.Family == Family.Keyboard ? (byte)0x01 : (byte)0x07;
        if (r.Length < 12 || r[0] != model.ReportId) return null;
        if (r[1] == 0xFF && r[2] == 0xAA || r[1] == 0 && r[2] == 0 && r[3] == 0) return Refused;
        if (r[1] != 0x12 || r[2] != command) return null;
        int raw = r[model.LevelAt];
        return model.Family switch
        {
            Family.Mouse when model.Steps => raw <= 4 ? new BatteryRead(raw * 25, r[10] != 0, Coarse: true) : Refused,
            Family.Mouse => raw <= 100 ? new BatteryRead(raw, r[10] != 0) : Refused,
            Family.Keyboard => raw <= 100 ? new BatteryRead(raw, r[9] == 1) : Refused,
            _ => raw <= 100 ? new BatteryRead(raw) : Refused
        };
    }
    public static bool? Charging(Model model, byte[] r) => model.Family == Family.Headset && r.Length >= 6 && r[0] == model.ReportId && r[1] == 0x12 && r[2] == 0x08 ? r[5] == 1 : null;

    public static DeviceKind Kind(Model model) => model.Family switch { Family.Keyboard => DeviceKind.Keyboard, Family.Headset => DeviceKind.Headphones, _ => DeviceKind.Mouse };

    // The ROG Omni receiver (0B05:1ACE), as vigil-watt reads it (measured with a Strix Scope II 96 Wireless): its pairing table,
    // asked on interface 2's collection 1 with 01 A0 00 (a read), answers 01 A0 00 <count>, then from 5 an entry of four bytes per
    // paired device: its product id little endian, the report id its channel uses, and its type. Each device is then asked on
    // its own channel, the collection numbered as its report id (the mice 3, the keyboards 2), with its model's offsets.
    // The product ids are those the receiver lists, as G-Helper maps them (facts only). The Harpe II Ace's own receiver
    // (1AD0) has no table and is read as a model of its own.
    public const int OmniReceiver = 0x1ACE, OmniInterface = 2, OmniTableCollection = 1;
    private static readonly IReadOnlyList<Model> OmniModels =
    [
        new(0x1B65, "ROG Harpe Ace Mini", Family.Mouse, OmniInterface), new(0x1C0E, "ROG Keris II Origin", Family.Mouse, OmniInterface),
        new(0x1D4E, "ROG Keris II Origin KJP", Family.Mouse, OmniInterface), new(0x1A94, "ROG Harpe Ace Aim Lab Edition", Family.Mouse, OmniInterface),
        new(0x1AD7, "ROG Strix Impact III Wireless", Family.Mouse, OmniInterface), new(0x1A72, "ROG Gladius III Aimpoint", Family.Mouse, OmniInterface),
        new(0x1A68, "ROG Keris Wireless Aimpoint", Family.Mouse, OmniInterface), new(0x1A6A, "ROG Keris Wireless Aimpoint", Family.Mouse, OmniInterface),
        new(0x1B1A, "ROG Keris II Ace", Family.Mouse, OmniInterface), new(0x1B18, "ROG Keris II Ace", Family.Mouse, OmniInterface),
        new(0x1B68, "ROG Harpe Ace Extreme", Family.Mouse, OmniInterface), new(0x1B69, "ROG Harpe Ace Extreme", Family.Mouse, OmniInterface),
        new(0x1A85, "ROG Azoth", Family.Keyboard, OmniInterface, LevelAt: 6), new(0x1B42, "ROG Azoth Extreme", Family.Keyboard, OmniInterface, LevelAt: 6),
        new(0x1CF1, "ROG Azoth Extreme SE", Family.Keyboard, OmniInterface, LevelAt: 6), new(0x1AB0, "ROG Strix Scope II 96 Wireless", Family.Keyboard, OmniInterface, LevelAt: 6),
        new(0x1B7A, "ROG Strix Scope II 96 RX Wireless", Family.Keyboard, OmniInterface, LevelAt: 6),
        new(0x1B06, "ROG Falchion RX Low Profile", Family.Keyboard, OmniInterface, LevelAt: 11),
    ];
    public static byte[] OmniTableRequest() { var frame = new byte[64]; frame[0] = 0x01; frame[1] = 0xA0; return frame; }
    // The paired devices this receiver lists that a model is known for, each with its channel's report id (as Model.ReportId
    // and Model.Collection); null for another report.
    public static IReadOnlyList<Model>? OmniTable(byte[] r)
    {
        if (r.Length < 5 || r[0] != 0x01 || r[1] != 0xA0 || r[2] != 0x00) return null;
        var paired = new List<Model>();
        for (int i = 0, at = 5; i < r[3] && at + 3 < r.Length; i++, at += 4)
        {
            int product = r[at] | r[at + 1] << 8;
            if (product == 0) break;
            if (OmniModels.FirstOrDefault(model => model.Product == product) is { } known && r[at + 2] is > 0 and < 16)
                paired.Add(known with { ReportId = r[at + 2], Collection = r[at + 2] });
        }
        return paired;
    }
}

// SteelSeries's wireless mice, as rivalcfg's device profiles (WTFPL) describe them, with HaloBattery's reading of the replies.
// Aerox 3, 5 and 9 Wireless and Prime Wireless on interface 3: the battery command 0x92 on the cable, the same with the
// wireless flag 0x40 (0xD2) through the receiver; the reply echoes the command, then bit 7 charging and a step 1 to 21, five
// points each (above 21 a percent, as steel-mouse reads it); 0 is the mouse off or asleep. Rival 3 Wireless (and Gen 2) on
// interface 3 and Rival 650 on interface 0: 00 AA 01, three bytes back, the level first and charging third (rivalcfg), or
// AA, the level and charging two on (steel-mouse); the charging byte must be 0 or 1.
public static class SteelSeriesMice
{
    public enum Exchange { AeroxReceiver, AeroxCable, Rival }
    public sealed record Model(int Product, string Name, int Interface, Exchange Exchange);
    public static readonly IReadOnlyList<Model> Catalog =
    [
        new(0x1838, "SteelSeries Aerox 3 Wireless", 3, Exchange.AeroxReceiver), new(0x183A, "SteelSeries Aerox 3 Wireless", 3, Exchange.AeroxCable),
        new(0x1878, "SteelSeries Aerox 3 Wireless", 3, Exchange.AeroxReceiver), new(0x187A, "SteelSeries Aerox 3 Wireless", 3, Exchange.AeroxCable),
        new(0x1852, "SteelSeries Aerox 5 Wireless", 3, Exchange.AeroxReceiver), new(0x1854, "SteelSeries Aerox 5 Wireless", 3, Exchange.AeroxCable),
        new(0x185C, "SteelSeries Aerox 5 Wireless", 3, Exchange.AeroxReceiver), new(0x185E, "SteelSeries Aerox 5 Wireless", 3, Exchange.AeroxCable),
        new(0x1860, "SteelSeries Aerox 5 Wireless", 3, Exchange.AeroxReceiver), new(0x1862, "SteelSeries Aerox 5 Wireless", 3, Exchange.AeroxCable),
        new(0x1858, "SteelSeries Aerox 9 Wireless", 3, Exchange.AeroxReceiver), new(0x185A, "SteelSeries Aerox 9 Wireless", 3, Exchange.AeroxCable),
        new(0x1874, "SteelSeries Aerox 9 Wireless", 3, Exchange.AeroxReceiver), new(0x1876, "SteelSeries Aerox 9 Wireless", 3, Exchange.AeroxCable),
        new(0x1840, "SteelSeries Prime Wireless", 3, Exchange.AeroxReceiver), new(0x1842, "SteelSeries Prime Wireless", 3, Exchange.AeroxCable),
        new(0x1848, "SteelSeries Prime Mini Wireless", 3, Exchange.AeroxReceiver), new(0x184A, "SteelSeries Prime Mini Wireless", 3, Exchange.AeroxCable),
        new(0x1830, "SteelSeries Rival 3 Wireless", 3, Exchange.Rival), new(0x1872, "SteelSeries Rival 3 Wireless Gen 2", 3, Exchange.Rival),
        new(0x1726, "SteelSeries Rival 650 Wireless", 0, Exchange.Rival), new(0x172B, "SteelSeries Rival 650 Wireless", 0, Exchange.Rival),
    ];
    public static readonly (int Receiver, int Cable)[] Pairs =
        [(0x1838, 0x183A), (0x1878, 0x187A), (0x1852, 0x1854), (0x185C, 0x185E), (0x1860, 0x1862), (0x1858, 0x185A), (0x1874, 0x1876), (0x1840, 0x1842), (0x1848, 0x184A)];
    public static Model? Find(int vendor, int product) => vendor == Headsets.SteelSeries ? Catalog.FirstOrDefault(model => model.Product == product) : null;
    // The model's interface, on its vendor collection with an output report (the mouse's own collections are not written to).
    public static bool Collection(Model model, int iface, int usagePage, int outputLength) => iface == model.Interface && usagePage >= 0xFF00 && outputLength > 0;

    // Report id 0, then the command.
    public static byte[] Request(Model model) => model.Exchange switch
    {
        Exchange.AeroxReceiver => [0x00, 0xD2], Exchange.AeroxCable => [0x00, 0x92], _ => [0x00, 0xAA, 0x01]
    };
    public static BatteryRead? Battery(Model model, byte[] r)
    {
        int m = r.Length > 1 && r[0] == 0x00 ? 1 : 0;
        if (model.Exchange == Exchange.Rival)
        {
            if (r.Length >= m + 4 && r[m] == 0xAA) return r[m + 1] <= 100 ? new(r[m + 1], r[m + 3] != 0) : null;
            return r.Length >= m + 3 && r[m] <= 100 && r[m + 2] is 0 or 1 ? new(r[m], r[m + 2] == 1) : null;
        }
        byte echo = model.Exchange == Exchange.AeroxReceiver ? (byte)0xD2 : (byte)0x92;
        if (r.Length < m + 2 || r[m] != echo) return null;
        int value = r[m + 1] & 0x7F;
        if (value == 0) return new(null, Asleep: true);
        bool charging = (r[m + 1] & 0x80) != 0;
        return value > 21 ? new(Math.Min(value, 100), charging) : new((value - 1) * 5, charging, Coarse: true);
    }
}

// Receivers that send their mouse's (or headset's, or controller's) battery on their own and are only listened to: nothing is
// ever written to them. Some send it every second or few, the rest only when something changes, so a listener keeps their
// collections open all the time (Native/PushEvents.cs) and each report is applied to the device's last state (Apply): a report
// that carries only the charging state or the link keeps the level that came before.
// - Beken receivers (Attack Shark 1D57:FA60 and the X6's FA62, interface 2, usage page 0x000A): 03 <device id> 40 <state>
//   <percent>, about every 2 s; the device id names the mouse, the X6 (0x85) counts 1 to 10; state 02, 03 or 80 is charging
//   on its dock (incconutwo's and misaka310's mouse-battery-tray; misaka310 verified the frame on an X1).
// - DELUX M900 Pro (1D57:FA65, usage page 0x000A): 03 30 40 <state> <percent>, 03 charging, 02 full; Inphic ZHEN on the same
//   receiver id (usage page 0xFF00 usage 1): 03 76 41 <state> <percent>, 03 charging. As vigil-watt documents both.
// - Rapoo's second generation (24AE, usage page 0xFF00 usage 2, input report 0x07 of 19 bytes, every ~3 s while the mouse
//   moves, silent at rest): the connection in the low nibble of 1 (0 the receiver, 2 the cable), 7 is 2 while charging, the
//   percent at 8 (0 or 1 for a moment after the cable goes in: no reading), bit 7 of 18 the radio link (receiver only). As
//   vigil-watt (measured) and Iris-0109's rapoo-tray read it.
// - AULA G9 headset (38A6:0030, usage page 0xFFAB): AB 47 4D 86, the percent at 6, 2 at 7 charging, every second; 0 and 0 is
//   no reading (vigil-watt).
// - The Steam Controller (2025) puck (28DE:1304, 1305; a controller per interface, 2 to 5): input report 0x43, the state at 1
//   (1 discharging, 2 charging, 4 charged) and the percent at 2, about every 2.5 s; 0x79 or 0x46 with 1 at 1 the controller
//   disconnected, 2 connected. As SDL's driver names them (via HaloBattery's steam.py; facts only from SDL).
// - JBL Quantum 910 (0ECB:2088, usage page 0xFF13 usage 1): report 0x08 with the percent at 1, only when it changes (on the
//   charger, at a step); report 0x09 with 0 at 1 when the headset is switched off, 1 on (HaloBattery's jbl.py).
// - Sony PULSE 3D (054C:0D5E, interface 3, usage page 0xFF01 usage 0x20): report 0xB0 only on a change, the level at 3 in
//   steps of ten or 0x80 while charging (the level is then not sent), bit 0x08 of 4 while the headset is connected
//   (HeadsetControl's sony_pulse_3d; GPL, facts only).
// - HyperX Cloud Flight 2 (03F0:0AC1, usage page 0xFF90): FB 13 00 <percent> at each step (byte 4 is a charge stage, not a
//   charging flag), FB 09 01 / 00 the cable put in / pulled out (the only charging state), FB 0A 00 / 01 the headset off / on,
//   and 51 02 <percent> as it connects (vigil-watt, measured).
public static class Pushes
{
    public enum Kind { Beken, Delux, InphicZhen, Rapoo, AulaG9, SteamPuck, Jbl, Pulse3D, CloudFlight2 }
    public static readonly int[] RapooProducts = [0x1412, 0x4612, 0x1B13, 0x4B13, 0x1460, 0x4660, 0x1406, 0x4606, 0x1410, 0x1411, 0x1417];
    public static Kind? Find(int vendor, int product, int usagePage, int usage, int iface, int inputLength) => (vendor, product) switch
    {
        (0x1D57, 0xFA60 or 0xFA62) when iface == 2 && usagePage == 0x000A => Kind.Beken,
        (0x1D57, 0xFA65) when usagePage == 0x000A => Kind.Delux,
        (0x1D57, 0xFA65) when usagePage == 0xFF00 && usage == 0x0001 => Kind.InphicZhen,
        (0x24AE, _) when RapooProducts.Contains(product) && usagePage == 0xFF00 && usage == 0x0002 && inputLength >= 19 => Kind.Rapoo,
        (0x38A6, 0x0030) when usagePage == 0xFFAB => Kind.AulaG9,
        (0x28DE, 0x1304 or 0x1305) when iface is >= 2 and <= 5 && usagePage >= 0xFF00 => Kind.SteamPuck,
        (0x0ECB, 0x2088) when usagePage == 0xFF13 && usage == 0x0001 => Kind.Jbl,
        (PlayStation.Vendor, 0x0D5E) when usagePage == 0xFF01 && usage == 0x0020 => Kind.Pulse3D,
        (Headsets.HyperXHp, 0x0AC1) when usagePage == 0xFF90 => Kind.CloudFlight2,
        _ => null
    };

    private static readonly Dictionary<int, string> BekenNames = new()
    {
        [0xB1] = "ATTACK SHARK X1", [0x55] = "ATTACK SHARK X11", [0x10] = "ATTACK SHARK R1", [0x85] = "ATTACK SHARK X6", [0x4D] = "ATTACK SHARK X3",
        [0xBE] = "ATTACK SHARK X11 Pro", [0x07] = "ATTACK SHARK X11 SE",
    };

    // A report applied to the device's last state (null before anything was heard): the new state, or null when the report says
    // nothing about the battery. Asleep: the device says it is off, or its radio link is down; its level stands.
    public static BatteryRead? Apply(Kind kind, BatteryRead? last, byte[] r)
    {
        switch (kind)
        {
            case Kind.Beken when r.Length >= 5 && r[0] == 0x03 && r[2] == 0x40:
                int level = r[1] == 0x85 && r[4] is > 0 and <= 10 ? r[4] * 10 : r[4];
                return level <= 100 ? new(level, r[3] is 0x02 or 0x03 or 0x80, Coarse: r[1] == 0x85) : null;
            case Kind.Delux when r.Length >= 5 && r[0] == 0x03 && r[1] == 0x30 && r[2] == 0x40 && r[4] <= 100:
                return new(r[3] == 0x02 ? 100 : r[4], r[3] == 0x03);
            case Kind.InphicZhen when r.Length >= 5 && r[0] == 0x03 && r[1] == 0x76 && r[2] == 0x41 && r[4] <= 100:
                return new(r[4], r[3] == 0x03);
            case Kind.Rapoo when r.Length >= 19 && r[0] == 0x07:
                if ((r[1] & 0x0F) == 0 && (r[18] & 0x80) == 0) return (last ?? new(null)) with { Charging = false, Asleep = true };
                return r[8] is >= 2 and <= 100 ? new(r[8], r[7] == 0x02 && r[8] < 100) : null;
            case Kind.AulaG9 when r.Length >= 8 && r[0] == 0xAB && r[1] == 0x47 && r[2] == 0x4D && r[3] == 0x86 && r[6] <= 100 && (r[6] != 0 || r[7] != 0):
                return new(r[6], r[7] == 0x02);
            case Kind.SteamPuck when r.Length >= 15 && r[0] == 0x43 && r[1] is 1 or 2 or 4 && r[2] <= 100:
                return new(r[2], r[1] == 2);
            // The slot's wireless status (0x79 or 0x46): 1 the controller disconnected, 2 connected.
            case Kind.SteamPuck when r.Length >= 2 && r[0] is 0x79 or 0x46 && r[1] is 1 or 2:
                return last is { } slot ? slot with { Charging = slot.Charging && r[1] == 2, Asleep = r[1] == 1 } : null;
            case Kind.Jbl when r.Length >= 2 && r[0] == 0x08 && r[1] <= 100:
                return new(r[1]);
            case Kind.Jbl when r.Length >= 2 && r[0] == 0x09 && r[1] is 0 or 1:
                return last is { } heard ? heard with { Asleep = r[1] == 0 } : null;
            case Kind.Pulse3D when r.Length >= 5 && r[0] == 0xB0:
                if ((r[4] & 0x08) == 0) return (last ?? new(null)) with { Charging = false, Asleep = true };
                if (r[3] == 0x80) return (last ?? new(null, Coarse: true)) with { Charging = true, Asleep = false };
                return r[3] <= 100 ? new(r[3], Coarse: true) : null;
            case Kind.CloudFlight2 when r.Length >= 4 && r[0] == 0xFB && r[1] == 0x13 && r[2] == 0x00 && r[3] <= 100:
                return (last ?? new(null)) with { Percent = r[3], Asleep = false };
            case Kind.CloudFlight2 when r.Length >= 3 && r[0] == 0xFB && r[1] == 0x09 && r[2] is 0 or 1:
                return (last ?? new(null)) with { Charging = r[2] == 1 };
            case Kind.CloudFlight2 when r.Length >= 3 && r[0] == 0xFB && r[1] == 0x0A && r[2] is 0 or 1:
                return last is { } known ? known with { Asleep = r[2] == 0 } : null;
            case Kind.CloudFlight2 when r.Length >= 3 && r[0] == 0x51 && r[1] == 0x02 && r[2] <= 100:
                return (last ?? new(null)) with { Percent = r[2], Asleep = false };
            default:
                return null;
        }
    }
    public static BatteryRead? Battery(Kind kind, byte[] r) => Apply(kind, null, r);

    // The device's name: a Beken report names its mouse; the rest are fixed (Rapoo's battery collection calls itself a keyboard).
    public static string Name(Kind kind, byte[]? report) => kind switch
    {
        Kind.Beken => report is { Length: > 1 } && BekenNames.TryGetValue(report[1], out var name) ? name : "ATTACK SHARK",
        Kind.Delux => "DELUX M900 Pro", Kind.InphicZhen => "Inphic ZHEN", Kind.Rapoo => "Rapoo Mouse", Kind.AulaG9 => "AULA G9",
        Kind.Jbl => "JBL Quantum 910 Wireless", Kind.Pulse3D => "PULSE 3D Wireless Headset", Kind.CloudFlight2 => "HyperX Cloud Flight 2", _ => "Steam Controller"
    };
    // Whether a report names the device (a Beken report carries its mouse's id).
    public static bool Names(Kind kind) => kind == Kind.Beken;
    public static DeviceKind KindOf(Kind kind) => kind switch
    {
        Kind.AulaG9 or Kind.Jbl or Kind.Pulse3D or Kind.CloudFlight2 => DeviceKind.Headphones, Kind.SteamPuck => DeviceKind.Controller, _ => DeviceKind.Mouse
    };
    public static DeviceSource Source(Kind kind) => kind switch { Kind.CloudFlight2 => DeviceSource.HyperX, Kind.Pulse3D => DeviceSource.Sony, _ => DeviceSource.Hid };
}

// Corsair's "bragi" exchange (ckb-next and OpenLinkHub name it), as HaloBattery's corsair.py reads it (MIT): a get-property
// request routed to the device behind the receiver, on the receiver's vendor collection (usage page 0xFF42, usage 1 answers;
// usage 2 is its notice channel). The Dark Core RGB Pro SE (1B1C:1B7F) takes it behind report id 0: 00 09 02 0F (route 9 the
// mouse, 2 get, 0x0F the battery level); the Virtuoso RGB Wireless SE and XT take a bare 64-byte frame with a leading 0x02
// (02 09 02 0F; Windows refuses the report-id-0 form there), and answer the charge state on property 0x10 (1 charging, 2 on
// battery). The answer is routed 0x01, then 02 and 00 (no error) and the value little endian, the Virtuoso's with a source
// byte (01) before the 02: tenths of a percent. It comes late (measured ~4.7 s after the request on a resting Dark Core), and
// another program on the same receiver can put its own answer on the channel, so the ask is repeated and two answers must
// agree. The 0A40 receiver answered only after a session of three more gets (the firmware 0x13, then the heartbeat 0x12 of
// the receiver, route 8, and of the headset): every request here is a get, nothing is set. The cables (0A3D, 0A62) are asked
// on route 8 first, the 0A3E and 0A64 receivers on route 9 first, each with the other as the fallback (HeadsetControl's
// order, as HaloBattery has it).
public static class Corsair
{
    public const int Vendor = 0x1B1C;
    public const byte RouteReceiver = 0x08, RouteDevice = 0x09, Level = 0x0F, ChargeState = 0x10, Heartbeat = 0x12, Firmware = 0x13;
    public sealed record Model(int Product, string Name, DeviceKind Kind, bool Bare, bool Session = false, bool Cable = false, int Receiver = -1);
    public static readonly IReadOnlyList<Model> Catalog =
    [
        new(0x1B7F, "Corsair Dark Core RGB Pro SE", DeviceKind.Mouse, false),
        new(0x0A40, "Corsair Virtuoso RGB Wireless SE", DeviceKind.Headphones, true, Session: true),
        new(0x0A3E, "Corsair Virtuoso RGB Wireless SE", DeviceKind.Headphones, true),
        new(0x0A3D, "Corsair Virtuoso RGB Wireless SE", DeviceKind.Headphones, true, Cable: true, Receiver: 0x0A3E),
        new(0x0A64, "Corsair Virtuoso RGB Wireless XT", DeviceKind.Headphones, true),
        new(0x0A62, "Corsair Virtuoso RGB Wireless XT", DeviceKind.Headphones, true, Cable: true, Receiver: 0x0A64),
    ];
    public static Model? Find(int vendor, int product) => vendor == Vendor ? Catalog.FirstOrDefault(model => model.Product == product) : null;
    // The receiver's vendor collections, usage 1 first.
    public static bool Collection(int usagePage) => usagePage == 0xFF42;
    public static IReadOnlyList<byte> Routes(Model model) => model.Cable ? [RouteReceiver, RouteDevice] : model.Product is 0x0A3E or 0x0A64 ? [RouteDevice, RouteReceiver] : [RouteDevice];

    public static byte[] Request(Model model, byte property, byte route = RouteDevice)
    {
        var frame = new byte[model.Bare ? 64 : 65];
        if (model.Bare) { frame[0] = 0x02; frame[1] = route; frame[2] = 0x02; frame[3] = property; }
        else { frame[1] = route; frame[2] = 0x02; frame[3] = property; }
        return frame;
    }
    // The value of a get answer; null for anything else (the receiver's device list, notices, the heartbeats' answers that
    // do not match every field).
    public static int? Value(byte[] reply)
    {
        var r = reply.Length >= 65 && reply[0] == 0x00 ? reply[1..] : reply;
        if (r.Length < 5 || r[0] != 0x01) return null;
        if (r[1] == 0x02 && r[2] == 0x00) return r[3] | r[4] << 8;
        if (r.Length >= 6 && r[1] == 0x01 && r[2] == 0x02 && r[3] == 0x00) return r[4] | r[5] << 8;
        return null;
    }
    public static int? Percent(byte[] reply) => Value(reply) is int value and > 0 and <= 1000 ? value / 10 : null;
    public static bool? Charging(byte[] reply) => Value(reply) switch { 1 => true, 2 => false, _ => null };
    // How long an answer may take: the first ask, then the confirming one (the device is awake by then), and the whole
    // reading of one receiver.
    public const int AskMs = 6000, ConfirmMs = 3000, BudgetMs = 7000;
}

// Wireless headsets on their own dongles. Each is a request on an interface and a reply with the level.
public static class Headsets
{
    public const int SteelSeries = 0x1038, HyperXHp = 0x03F0, HyperXKingston = 0x0951;
    public enum Kind { Arctis7, Arctis9, Nova7Percent, Nova7Steps, NovaPro, CloudAlpha, CloudII, CloudFlight, MchoseX9, CloudIII, CloudIIIS, CloudAlpha2 }

    // Which headset a vendor and product are, and the interface (or usage page) its battery lives on.
    public static Kind? Find(int vendor, int product) => (vendor, product) switch
    {
        (SteelSeries, 0x1260 or 0x12AD or 0x1252 or 0x1280) => Kind.Arctis7,
        (SteelSeries, 0x12C2 or 0x12C4) => Kind.Arctis9,
        (SteelSeries, 0x22A1 or 0x227E or 0x2258 or 0x229E or 0x22AD or 0x22A9 or 0x22A5) => Kind.Nova7Percent,
        (SteelSeries, 0x2202 or 0x2206 or 0x223A or 0x227A or 0x22A4) => Kind.Nova7Steps,
        (SteelSeries, 0x12E0 or 0x12E5) => Kind.NovaPro,
        (HyperXHp, 0x098D) => Kind.CloudAlpha,
        (HyperXHp, 0x0696 or 0x018B) => Kind.CloudII,
        (HyperXKingston, 0x16C4 or 0x1723) => Kind.CloudFlight,
        // HaloBattery's hyperx_cloud3.py (the Cloud III Wireless, and the Cloud II Core Wireless on 0995 and 0795 from a
        // capture of NGENUITY), hyperx_cloud3s.py and hyperx_alpha2.py (captures of NGENUITY).
        (HyperXHp, 0x05B7 or 0x0C9D or 0x0995 or 0x0795) => Kind.CloudIII,
        (HyperXHp, 0x02CC or 0x06BE) => Kind.CloudIIIS,
        (HyperXHp, 0x08BE) => Kind.CloudAlpha2,
        (Dongles.MchoseVendor, 0x6045) => Kind.MchoseX9,
        _ => null
    };
    // The interface (MI_xx) to talk on, or the usage page when that is what tells it apart; -1 for either.
    // VendorPages: any vendor-defined usage page (0xFF00 and up), so a request never goes to the headset's consumer or audio
    // control collections.
    public const int VendorPages = -2;
    public static (int Interface, int UsagePage) Channel(Kind kind) => kind switch
    {
        Kind.Arctis7 => (5, -1), Kind.Arctis9 => (0, -1), Kind.Nova7Percent or Kind.Nova7Steps => (-1, 0xFFC0), Kind.NovaPro => (4, -1),
        Kind.CloudII => (-1, 0xFF90), Kind.MchoseX9 => (-1, 0xFF90), Kind.CloudIII or Kind.CloudAlpha2 => (-1, 0xFF13), Kind.CloudIIIS => (3, VendorPages),
        _ => (-1, VendorPages)
    };
    // The Cloud III S answers on whichever collection of its interface declares the reply's report, not always the one asked:
    // its siblings on that interface are read too.
    public static bool ReadsSiblings(Kind kind) => kind == Kind.CloudIIIS;
    // Some Cloud III dongles take the request only as a feature report: Windows refuses the output report with "Incorrect
    // function", and the same bytes go as a feature report (HyperHeadset's fallback, as HaloBattery has it).
    public static bool FeatureFallback(Kind kind) => kind == Kind.CloudIII;
    // How long a reply may take, in milliseconds.
    public static int Wait(Kind kind) => kind switch { Kind.CloudAlpha2 => 1500, Kind.CloudIII or Kind.CloudIIIS => 1000, _ => 600 };

    // The requests, report id first.
    public static byte[] Request(Kind kind) => kind switch
    {
        Kind.Arctis7 => [0x06, 0x18],
        Kind.Arctis9 => [0x00, 0x20],
        Kind.Nova7Percent or Kind.Nova7Steps => [0x00, 0xB0],
        Kind.NovaPro => Padded([0x06, 0xB0], 31),
        Kind.CloudAlpha => Padded([0x21, 0xBB, 0x0B], 31),
        Kind.CloudII => Padded([0x06, 0xFF, 0xBB, 0x02], 52),
        Kind.CloudFlight => Padded([0x21, 0xFF, 0x05], 20),
        Kind.MchoseX9 => Padded([0x55, 0x65, 0x01], 64),
        Kind.CloudIII => Padded([0x66, 0x89], 62),
        // 01 at 3 asks for a value; 00 would set one, which is never sent.
        Kind.CloudIIIS => Padded([0x0C, 0x02, 0x03, 0x01, 0x00, 0x06], 64),
        Kind.CloudAlpha2 => Padded([0x50, 0x02], 64),
        _ => []
    };
    // A request asking whether the headset is connected to its dongle at all, where the level alone would not tell (a dongle
    // whose headset is off may still answer with a level).
    public static byte[]? ConnectionRequest(Kind kind) => kind switch { Kind.CloudAlpha => Padded([0x21, 0xBB, 0x03], 31), _ => null };
    // The answer: false when the headset is not connected (Cloud Alpha: 0x01 at 3); null for another report.
    public static bool? Connected(Kind kind, byte[] r) => kind switch
    {
        Kind.CloudAlpha when r.Length >= 4 && r[0] == 0x21 && r[1] == 0xBB && r[2] == 0x03 => r[3] != 0x01,
        _ => null
    };
    // A second request for the charging state, where it comes apart from the level.
    public static byte[]? ChargingRequest(Kind kind) => kind switch
    {
        Kind.CloudAlpha => Padded([0x21, 0xBB, 0x0C], 31), Kind.CloudII => Padded([0x06, 0xFF, 0xBB, 0x03], 52),
        Kind.CloudIII => Padded([0x66, 0x8A], 62), Kind.CloudIIIS => Padded([0x0C, 0x02, 0x03, 0x01, 0x00, 0x48], 64), _ => null
    };
    private static byte[] Padded(byte[] head, int length) { var frame = new byte[length]; head.CopyTo(frame, 0); return frame; }

    // The reading in a reply; null for another report, or a headset that is off or not connected.
    public static BatteryRead? Battery(Kind kind, byte[] r) => kind switch
    {
        // The replies are read at the documented offsets (none of them is documented to echo its request); a value outside its
        // range is no reading, never an empty battery. The level of the Arctis 9 runs from 0x64 to 0x9A. The queue is flushed
        // before each request (Hid.Transact), so the first report after it is the reply, as HeadsetControl reads it; another
        // report (a volume change) arriving in between could still be read as a level, which is not verified on a headset.
        // The offsets count the report id at 0, so a report of another id (a button, padded with zeros) is passed over.
        Kind.Arctis7 when r.Length >= 3 && r[0] == 0x06 && r[2] <= 100 => new(r[2]),
        Kind.Arctis9 when r.Length >= 5 && r[3] is >= 0x64 and <= 0x9A => new(Devices.Clamp((int)Math.Round((r[3] - 0x64) * 100.0 / (0x9A - 0x64))), r[4] == 0x01),
        Kind.Nova7Percent when r.Length >= 4 && r[3] != 0 && r[2] <= 100 => new(r[2], r[3] is 0x01 or 0x02),
        Kind.Nova7Steps when r.Length >= 4 && r[3] != 0 && r[2] <= 4 => new(r[2] * 25, r[3] is 0x01 or 0x02, Coarse: true),
        Kind.NovaPro when r.Length >= 16 && r[0] == 0x06 && r[15] != 0x01 && r[6] <= 8 => new((int)Math.Round(r[6] * 12.5), r[15] == 0x02, Coarse: true),
        Kind.CloudAlpha when r.Length >= 4 && r[0] == 0x21 && r[1] == 0xBB && r[2] == 0x0B && r[3] <= 100 => new(r[3]),
        Kind.CloudII when r.Length >= 8 && r[0] == 0x06 && r[1] == 0xFF && r[2] == 0xBB && r[3] == 0x02 && r[7] <= 100 => new(r[7]),
        // Millivolts; above 4107 the headset is on its cable. The level is a line between 3648 and 3975 mV.
        Kind.CloudFlight when r.Length >= 5 && r[0] == 0x21 && r[1] == 0xFF && r[2] == 0x05 && ((r[3] << 8) | r[4]) is var mv && mv > 3000
            => new(mv > 4107 ? null : Devices.Clamp((int)Math.Round((mv - 3648) * 100.0 / (3975 - 3648))), mv > 4107, Coarse: true),
        // 55 65 VV FF: a value and the field it belongs to. Only field 0x02 is the battery; the others (a heartbeat, other
        // telemetry) are passed over. No field is documented to say charging.
        Kind.MchoseX9 when r.Length >= 4 && r[0] == 0x55 && r[1] == 0x65 && r[3] == 0x02 && r[2] <= 100 => new(r[2]),
        // 66 89 (or its response id 0D): the percent at 4, unless 2 and 3 are both 0 (no reading yet).
        Kind.CloudIII when r.Length >= 5 && r[0] == 0x66 && r[1] is 0x89 or 0x0D && (r[2] != 0 || r[3] != 0) && r[4] <= 100 => new(r[4]),
        // 0C .. .. .. .. 06 <percent> (0xFF: no value), or the headset's own notice 0D with 1 at 4 and the percent at 5.
        Kind.CloudIIIS when r.Length >= 7 && r[0] == 0x0C && r[5] == 0x06 && r[6] <= 100 => new(r[6]),
        Kind.CloudIIIS when r.Length >= 6 && r[0] == 0x0D && r[4] == 0x01 && r[5] <= 100 => new(r[5]),
        // 51 02 <percent> .. .. .. <flags>, bit 7 of the flags while charging.
        Kind.CloudAlpha2 when r.Length >= 7 && r[0] == 0x51 && r[1] == 0x02 && r[2] <= 100 => new(r[2], (r[6] & 0x80) != 0),
        _ => null
    };
    public static bool? Charging(Kind kind, byte[] r) => kind switch
    {
        Kind.CloudAlpha when r.Length >= 4 && r[0] == 0x21 && r[1] == 0xBB && r[2] == 0x0C => r[3] == 1,
        Kind.CloudII when r.Length >= 5 && r[0] == 0x06 && r[1] == 0xFF && r[2] == 0xBB && r[3] == 0x03 => r[4] == 1,
        // 0 not charging, 1 charging, 2 full (on its cable, no longer charging); anything else says nothing.
        Kind.CloudIII when r.Length >= 3 && r[0] == 0x66 && r[1] is 0x8A or 0x0C && r[2] <= 2 => r[2] == 1,
        Kind.CloudIIIS when r.Length >= 7 && r[0] == 0x0C && r[5] == 0x48 && r[6] <= 2 => r[6] == 1,
        Kind.CloudIIIS when r.Length >= 6 && r[0] == 0x0D && r[4] == 0x0A && r[5] <= 2 => r[5] == 1,
        _ => null
    };

    public static string Name(Kind kind) => kind switch
    {
        Kind.Arctis7 => "SteelSeries Arctis 7", Kind.Arctis9 => "SteelSeries Arctis 9", Kind.Nova7Percent or Kind.Nova7Steps => "SteelSeries Arctis Nova 7",
        Kind.NovaPro => "SteelSeries Arctis Nova Pro Wireless", Kind.CloudAlpha => "HyperX Cloud Alpha Wireless", Kind.CloudII => "HyperX Cloud II Wireless",
        Kind.CloudFlight => "HyperX Cloud Flight", Kind.CloudIII => "HyperX Cloud III Wireless", Kind.CloudIIIS => "HyperX Cloud III S Wireless",
        Kind.CloudAlpha2 => "HyperX Cloud Alpha 2", _ => "MCHOSE X9"
    };
    public static DeviceSource Source(Kind kind) => kind switch
    {
        Kind.CloudAlpha or Kind.CloudII or Kind.CloudFlight or Kind.CloudIII or Kind.CloudIIIS or Kind.CloudAlpha2 => DeviceSource.HyperX,
        Kind.MchoseX9 => DeviceSource.Hid, _ => DeviceSource.SteelSeries
    };
}

// The Bluetooth Battery Service's Battery Level Status (1.1): a flags byte, then the power state, little endian, whose bits 5
// and 6 are the battery's charge state (1 charging, 2 discharging while in use, 3 discharging at rest, 0 unknown) and bits 1
// and 2 whether wired power is connected (1 yes). The layout is as the Polar BLE SDK and quiet-battery read it; the
// specification's own table was not at hand.
public static class BatteryService
{
    public static readonly Guid LevelStatus = new("00002BED-0000-1000-8000-00805F9B34FB");

    // Whether it charges; null when the device does not say.
    public static bool? Charging(byte[] value)
    {
        if (value.Length < 3) return null;
        int state = value[1] | value[2] << 8;
        return ((state >> 5) & 3) switch { 1 => true, 2 or 3 => false, _ => ((state >> 1) & 3) == 1 ? true : null };
    }
}

// PlayStation controllers: their battery rides in every full input report, so it is read without asking.
public static class PlayStation
{
    public const int Vendor = 0x054C;
    public static bool IsDualSense(int product) => product is 0x0CE6 or 0x0DF2;
    public static bool IsDualShock(int product) => product is 0x05C4 or 0x09CC or 0x0BA0;

    // The status byte of a report: DualSense at 53 on USB (report 0x01) and 54 on Bluetooth (0x31); DualShock 4 at 30 on USB
    // (0x01) and 32 on Bluetooth (0x11). The link is the collection's, not the report's length: Windows pads every report to the
    // collection's longest, so a short Bluetooth report 0x01 (sent before a program asks for full reports) is as long as a USB
    // one, and carries no battery.
    public static BatteryRead? Battery(int product, byte[] r, bool bluetooth)
    {
        if (IsDualSense(product))
        {
            int at = !bluetooth && r.Length >= 64 && r[0] == 0x01 ? 53 : bluetooth && r.Length >= 78 && r[0] == 0x31 ? 54 : -1;
            if (at < 0) return null;
            int level = r[at] & 0x0F, state = r[at] >> 4;
            return state switch
            {
                // 0x2 is full (hid-playstation): on its cable, no longer charging.
                0x0 => new(Math.Min(level * 10 + 5, 100)), 0x1 => new(Math.Min(level * 10 + 5, 100), true), 0x2 => new(100), _ => null
            };
        }
        if (IsDualShock(product))
        {
            int at = !bluetooth && r.Length >= 64 && r[0] == 0x01 ? 30 : bluetooth && r.Length >= 78 && r[0] == 0x11 ? 32 : -1;
            if (at < 0) return null;
            int level = r[at] & 0x0F; bool cable = (r[at] & 0x10) != 0;
            // On its cable: 0 to 10 charging, 11 full (hid-playstation), no longer charging.
            if (cable) return level <= 9 ? new(level * 10 + 5, true) : level == 10 ? new(100, true) : level == 11 ? new(100) : null;
            return level <= 10 ? new(Math.Min(level * 10 + 5, 100)) : null;
        }
        return null;
    }
}

// Only feature reports are read: a battery usage carried in input reports alone (many Bluetooth keyboards and mice, USI and
// Wacom pens) sits in a collection Windows keeps to itself, which no program can read; those devices show through Windows'
// own Bluetooth level instead, or not at all.
// The HID battery usages any device may carry: Battery Strength (Generic Device Controls 0x06/0x20, Digitizer 0x0D/0x3B), and the
// Battery System page's state of charge (0x85/0x65 absolute, 0x66 remaining). The value is scaled by its logical range.
public static class HidBattery
{
    public static bool IsBattery(int page, int usage) => (page, usage) is (0x06, 0x20) or (0x0D, 0x3B) or (0x85, 0x65) or (0x85, 0x66);
    public static int? Percent(int value, int logicalMin, int logicalMax)
    {
        if (logicalMax <= logicalMin) logicalMax = logicalMin + 100;
        if (value < logicalMin || value > logicalMax) return null;
        return logicalMin == 0 && logicalMax == 100 ? value : Devices.Clamp((int)Math.Round((value - logicalMin) * 100.0 / (logicalMax - logicalMin)));
    }
}

// AirPods and Beats: their charge rides in Apple's proximity pairing advertisement (manufacturer data of company 0x004C, type
// 0x07, length 0x19), in steps of ten; written from the layout OpenPods and AirPodsDesktop read (facts only). Byte 6 holds the
// buds' levels, byte 7 the charging bits (high nibble) and the case (low); 15 is no reading. With byte 5's bit 0x20 set the
// left bud is byte 6's low nibble; clear (flipped), its high one. Single-unit models (AirPods Max, the one-piece Beats) carry
// their one level in byte 6's low nibble.
public static class AppleAdvert
{
    public const ushort Company = 0x004C;
    public static string? Model(byte[] data) => data.Length >= 5 ? ((data[3] << 8) | data[4]) switch
    {
        0x0220 => "AirPods", 0x0F20 => "AirPods", 0x1320 => "AirPods", 0x0E20 => "AirPods Pro", 0x1420 or 0x2420 => "AirPods Pro", 0x2720 => "AirPods Pro",
        0x0A20 => "AirPods Max", 0x0B20 => "Powerbeats Pro", 0x0520 => "BeatsX", 0x1020 => "Beats Flex", 0x0620 => "Beats Solo", 0x0920 => "Beats Studio",
        0x0320 => "Powerbeats", _ => null
    } : null;

    private static bool IsProximity(byte[] data) => data.Length >= 8 && data[0] == 0x07 && data[1] == 0x19;
    private static int? Level(int nibble) => nibble <= 10 ? nibble * 10 : null;
    // One piece, without buds or a case of its own: AirPods Max, Beats Solo, Studio and Flex, BeatsX, Powerbeats 3.
    public static bool IsSingle(byte[] data) => data.Length >= 5 && ((data[3] << 8) | data[4]) is 0x0A20 or 0x0620 or 0x0920 or 0x0520 or 0x1020 or 0x0320;

    // A single-unit model's one level, and charging (bit 0x01); null for earbuds, another advertisement, or no reading.
    public static BatteryRead? Single(byte[] data)
        => IsProximity(data) && IsSingle(data) && Level(data[6] & 0x0F) is { } level ? new(level, (data[7] & 0x10) != 0, Coarse: true) : null;

    // The parts' levels: left, right and the case; empty for a single-unit model (Single), null when this is not a proximity
    // pairing advertisement.
    public static IReadOnlyList<PartBattery>? Parts(byte[] data)
    {
        if (!IsProximity(data)) return null;
        if (IsSingle(data)) return [];
        bool flipped = (data[5] & 0x20) == 0;
        int high = data[6] >> 4, low = data[6] & 0x0F, caseLevel = data[7] & 0x0F, charging = data[7] >> 4;
        int left = flipped ? high : low, right = flipped ? low : high;
        bool leftCharging = (charging & (flipped ? 0x02 : 0x01)) != 0, rightCharging = (charging & (flipped ? 0x01 : 0x02)) != 0, caseCharging = (charging & 0x04) != 0;
        var parts = new List<PartBattery>();
        if (Level(left) is not null || Level(right) is not null)
        {
            parts.Add(new(BatteryPart.Left, Level(left), leftCharging));
            parts.Add(new(BatteryPart.Right, Level(right), rightCharging));
        }
        if (Level(caseLevel) is not null) parts.Add(new(BatteryPart.Case, Level(caseLevel), caseCharging));
        return parts;
    }
}

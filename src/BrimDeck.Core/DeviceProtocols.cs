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
//   lists, the MCHOSE RealTek and G7 exchanges and the 0x55 reads of its headsets and newer devices (mchose.py), the HyperX Cloud III, III S and Alpha 2 requests, the NZXT Lift
//   Elite's telemetry, SteelSeries's mouse replies (steelseries.py), the Razer keyboards' transaction ids and interfaces, the
//   Corsair receivers' get-property exchange (corsair.py) and the JBL Quantum 910's reports (jbl.py).
// - After vigil-watt (https://github.com/evren-oct/vigil-watt, MIT): the ATK, HECATE and VGN collections for the Compx frame,
//   the LINGBAO M5PRO Max, CHERRY PIXIU75 and HECATE G5M Pro reads, the MCHOSE G3 V2 query, the ROG Delta II headset, the ROG
//   Omni receiver's pairing table, and the DELUX, Inphic ZHEN, Rapoo, AULA G9 and HyperX Cloud Flight 2 reports.
// - The MCHOSE A7's receiver and the 41E4:2001 keyboards' battery command: from marsuyone/mchose-battery-tray's notes
//   (https://github.com/marsuyone/mchose-battery-tray, no license; the bytes only), with HaloBattery's mchose_kb.py (the K87)
//   and its reading of the 0x3837 receivers. The MCHOSE RealTek model names: the vendor driver's table as
//   alexfrih/mchose-linux lists it (MIT; names only).
// - Rapoo's first generation: after mousectl's rapoo_vt3pro driver (https://github.com/D3m0nZOnFire/mousectl, MIT).
// - SteelSeries mice: rivalcfg's device profiles (https://github.com/flozz/rivalcfg, WTFPL). Beken (Attack Shark) receivers:
//   after incconutwo's and misaka310's mouse-battery-tray (MIT). Rapoo: also Iris-0109's rapoo-tray (MIT). The LAMZU Inca on
//   the 0x83 exchange: Fan4Metal's mouse_tray and MouseDock's device table (MIT) list it.
// - ASUS ROG and TUF and the Omni receiver's model ids (G-Helper, GPL), the Ninjutso receiver and the rest of the Compx model
//   table (Fan4Metal's mouse_tray, no license), the Steam Controller puck's reports (SDL) and the PULSE 3D's status report
//   (HeadsetControl, GPL): facts only, the code written from the documented bytes.
// - AirPods and Beats: the proximity pairing advertisement's layout as OpenPods and AirPodsDesktop read it (GPL; look-only,
//   facts only).
// - The models added later to brands already read, each noted where it is: HaloBattery (MIT: the Compx pairs of pulsar.py and
//   lamzu.py, the split Compx frame of irocks.py, dareu.py and endgame_we.py, the Kingston Cloud II, the Corsair receivers,
//   the Logitech headsets and the G535, Rapoo's V700DIY, the Razer ids), MouseDock (MIT: its verified profiles, the G7's
//   cable rule, the 3554:FA09 frames), vigil-watt (MIT: Rapoo's collection, the 3554:FA09 model codes), Klegus/mchose-macos
//   and NaoCoding/ShowMeTheConnection (MIT), G-Helper (GPL, facts only) and wzk112/MchoseHub (no license, ids only).

// One battery reading from a protocol: the percentage (null: the device answered without one), whether it charges, whether
// the level is coarse (a voltage, or a few steps), and whether the mouse is asleep / not answering (Asleep): the last percent
// still stands; never invent 0 % for that case.
public readonly record struct BatteryRead(int? Percent, bool Charging = false, bool Coarse = false, bool Asleep = false);

// Windows reports some vendor pages with their two bytes swapped (a descriptor's 0xFF00 read back as 0x00FF), so a page a
// source gives without saying it was read on Windows is taken in either order. Vendor: any vendor-defined page (0xFF00 and
// up), in either order; never a standard page.
public static class UsagePages
{
    public static int Swapped(int page) => (page & 0xFF) << 8 | page >> 8 & 0xFF;
    public static bool Is(int reported, int page) => reported == page || reported == Swapped(page);
    public static bool Vendor(int reported) => reported >= 0xFF00 || Swapped(reported) >= 0xFF00;
}

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

    // The G headsets' dongles (G533, G633, G635, G933, G935, G733, G PRO, G PRO X, G PRO X 2; HaloBattery's logitech.py after
    // HeadsetControl's list) carry HID++ on one collection, usage page 0xFF43 usage 0x0202, with both the short report 0x10
    // and the long 0x11 on it: that collection is the long one and there is no short one. Taken by its shape (it must declare
    // output report 0x11 and take 20-byte reports), the page in either byte order.
    public static bool HeadsetCollection(int usagePage, int usage, int inputLength, IReadOnlySet<byte> outputIds)
        => UsagePages.Is(usagePage, 0xFF43) && usage == 0x0202 && inputLength >= LongLength && outputIds.Contains(Long);

    // The G535 (046D:0AC4) speaks HID++ 1.0 on its long collection: register 0x0D read with a long report 11 FF 05 0D, answered
    // 1x FF 05 0D <millivolts big endian> <state>, 3 charging (HaloBattery's logitech.py, after HeadsetControl). HeadsetControl
    // reads it on the consumer collection, which is never written to here; only its 0xFF43 collection, where it has one.
    // The level goes through HaloBattery's curve of HeadsetControl's measurements on this model.
    public const int G535 = 0x0AC4;
    public static byte[] HeadsetRegisterRequest(byte device, byte register) { var frame = new byte[LongLength]; frame[0] = Long; frame[1] = device; frame[2] = 0x05; frame[3] = register; return frame; }
    private static readonly (int Millivolts, int Percent)[] G535Curve = [(4175, 100), (3817, 50), (3766, 30), (3730, 20), (3664, 5), (3310, 0)];
    public static BatteryRead? G535Battery(byte[] r, byte device)
    {
        if (r.Length < 7 || r[0] is not (Short or Long) || r[1] != device || r[2] != 0x05 || r[3] != 0x0D) return null;
        int millivolts = r[4] << 8 | r[5];
        if (millivolts is < 2500 or > 5000) return null;
        int percent = 0;
        if (millivolts >= G535Curve[0].Millivolts) percent = 100;
        else for (int i = 1; i < G535Curve.Length; i++)
            {
                var (high, highPercent) = G535Curve[i - 1]; var (low, lowPercent) = G535Curve[i];
                if (millivolts < low) continue;
                percent = (int)Math.Round(lowPercent + (highPercent - lowPercent) * (millivolts - low) / (double)(high - low));
                break;
            }
        return new(percent, r[6] == 0x03, Coarse: true);
    }

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
        // The Orochi V2's 2026 receiver, as openrazer lists it (GPL; facts only): the same transaction and wait.
        new(0x00E0, "Razer Orochi V2", 0x1F, 0, 400_000, false),
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
        // The Joro keyboard, openrazer's keyboard driver (GPL; facts only): transaction 0x1F, its report on interface 3.
        new(0x02CD, "Razer Joro", 0x1F, 3, 31_000, true, DeviceKind.Keyboard),
        // HaloBattery's razer.py (MIT): the Mouse Dock Pro (0x1F, OpenRazer's id), and the Pro Type Ultra's receiver (0x9F, its
        // level measured in #196). Neither's interface is documented: any (-1), the device's own collection first.
        new(0x00A4, "Razer Mouse Dock Pro", 0x1F, -1, 31_000, true), new(0x027B, "Razer Pro Type Ultra", 0x9F, -1, 31_000, true, DeviceKind.Keyboard),
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
    // The receiver is not AULA's alone: the AULA F87 Pro, F99 Pro and F75, MCHOSE's G75 Pro and KX75, LINGBAO's K98 and KZZI's
    // K75 V2 sit behind the same 3554:FA09 receiver on the same frames. Its answer to 13 05 (13 05 01 00 0A, checksummed) names
    // the keyboard with six bytes at 5: the codes below, as vigil-watt measured them (aula.md, lingbao.md and mchose.md, MIT:
    // the F99 Pro, the K98 and the G75 Pro) and its table from M HUB lists the KX75's, and MouseDock (MIT) the K75 V2's (which
    // it asks with 06 at 4; asked with 00 here, it may not answer with it). A code not known leaves the
    // receiver's own name. The level comes in the push 13 0A 01 00 04 05 <percent> <flags> or the answer 13 4A 01 00 02
    // <percent> <flags>, every frame checksummed (byte 19 the low byte of the sum of the nineteen before it, MouseDock's check):
    // the flags' high nibble is charging (0x10 on the cable, measured on the F99 Pro), its low nibble full, which is not.
    public static readonly (byte[] Code, string Name)[] AulaModels =
    [
        ([0x03, 0x00, 0x00, 0x00, 0x01, 0x58], "AULA F99 Pro"), ([0x03, 0x00, 0x00, 0x00, 0x02, 0x12], "LINGBAO K98"),
        ([0x03, 0x00, 0x00, 0x00, 0x01, 0x15], "MCHOSE G75 Pro"),
        ([0x03, 0x00, 0x00, 0x00, 0x03, 0xA2], "KZZI K75 V2"), ([0x03, 0x00, 0x00, 0x00, 0x01, 0xC7], "MCHOSE KX75"),
        // NaoCoding/ShowMeTheConnection's kzzi_k75_lite.py (MIT), from KZZI's own driver (asked with 06 at 4 there as well).
        ([0x03, 0x00, 0x00, 0x00, 0x01, 0x44], "KZZI K75 Lite"),
        // KZZI's web driver's model table (its donglePassword values; names only).
        ([0x03, 0x00, 0x00, 0x00, 0x03, 0xF3], "KZZI I98"), ([0x03, 0x00, 0x00, 0x00, 0x01, 0x68], "KZZI I75"), ([0x03, 0x00, 0x00, 0x00, 0x03, 0xBB], "KZZI Z75"),
    ];
    private static bool AulaChecked(byte[] r)
    {
        if (r.Length < 20 || r[0] != 0x13) return false;
        int sum = 0; for (int i = 0; i < 19; i++) sum += r[i];
        return (byte)sum == r[19];
    }
    // The keyboard an answer to 13 05 names; null for another report or a code not known.
    public static string? AulaModel(byte[] r)
    {
        if (!AulaChecked(r) || r[1] != 0x05 || r[2] != 0x01 || r[3] != 0x00) return null;
        foreach (var (code, name) in AulaModels)
            if (r.AsSpan(5, 6).SequenceEqual(code)) return name;
        return null;
    }
    public static BatteryRead? AulaBattery(byte[] r)
    {
        if (!AulaChecked(r) || r[2] != 0x01 || r[3] != 0x00) return null;
        int at = r[1] == 0x0A && r[4] == 0x04 && r[5] == 0x05 ? 6 : r[1] == 0x4A && r[4] == 0x02 ? 5 : -1;
        if (at < 0 || r[at] > 100) return null;
        return new(r[at], (r[at + 1] & 0xF0) != 0);
    }

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
    // VgnBattery reads. Measured. ATK HUB (bpcdn.atkgear.com/hub-v3, its "EVISION" keyboards) gives the same frame and page to
    // its other keyboards' receivers, listed after it (not measured by us; the Z87 Pro's 1012 and 1013 serve as both a cable
    // and a receiver in its table), and reads charging as 2 at 9 (AtkKeyboardBattery).
    public static readonly (int Product, string Name)[] AtkKeyboards =
    [
        (0x1012, "ATK Z87 Pro"), (0x1013, "ATK Z87 Pro"), (0x1023, "YOGO Q75"), (0x1029, "VGN S87"), (0x10DA, "ATK TV88"), (0x118D, "ATK S99"),
        (0x118F, "ATK S87"), (0x11FB, "ATK TV99"), (0x305F, "ATK V100"), (0x3060, "ATK V100 Pro"), (0x4288, "ATK TV108"),
    ];
    // The receivers ATK HUB adds (not the measured Z87 Pro's, which keeps VgnBattery): charging only as 2 at 9, and a level of 0
    // (a keyboard away) is no reading.
    public static BatteryRead? AtkKeyboardBattery(byte[] reply) => VgnBattery(reply) is { Percent: > 0 } read ? read with { Charging = reply[9] == 2 } : null;
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
    // The Pulsar X2 Wireless on 3710:9604, 5602 and 9605: Pulsar's web driver sends them to its "vMouse" page, which matches
    // VAXEE's 3057 ids too and asks this same feature report on 0xFF05 (bbb.pulsar.gg). Not measured by us.
    public static readonly int[] PulsarVaxeeProducts = [0x9604, 0x5602, 0x9605];
    public static byte[] VaxeeRequest(int length) { var frame = new byte[Math.Max(5, length)]; frame[0] = 0x0E; frame[1] = 0xA5; frame[2] = 0x0B; frame[3] = 0x01; frame[4] = 0x01; return frame; }
    public static BatteryRead? VaxeeBattery(byte[] reply) => reply.Length >= 6 && reply[0] == 0x0E && reply[5] <= 20 ? new(reply[5] * 5, Coarse: true) : null;

    // Nordic's own receivers (VID 0x1915, usage page 0xFFA0, feature 0x05, 32 bytes): 05 15 00 00 01, then the reply read back
    // ~90 ms later: the percentage at 9, charging at 10, full at 11, the mouse online at 12. 0x1915 is the chip maker's id, which
    // unrelated products also carry (the HP Prime wireless kit, Crazyradio), so only the Ninjutso Sora V2's receiver and cable
    // (NinjutsoProducts, from Fan4Metal's mouse_tray, facts only) are sent to. Offline, or 0 %, is the mouse asleep. The Sora V2
    // on AE8A (verified upstream) and the Sora V2 8K on AE8C (not measured) take the same 32-byte read: MouseDock's profiles (MIT).
    public const int NordicVendor = 0x1915;
    public static readonly int[] NinjutsoProducts = [0xAE1C, 0xAE11, 0xAE8A, 0xAE8C];
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
        // HaloBattery's pulsar.py and lamzu.py (MIT): the Cougar Revenger Pro's receivers and cable (measured, #354), VGN's
        // Dragonfly 4K (its receiver measured, #323) and cable, the Redragon M991 / K1NG Max's receiver and cable (both
        // measured, #183), the Teevolution Terra Pro keyboard's receiver (#300), the LAMZU Thorn's 4K receiver and cable
        // (checked against its live readings), Hitscan's 8K receiver (measured, #158) and the Corsair Sabre v2 Pro
        // Ultralight (#290; 1B1C is Corsair's own id, which has other protocols, so the exact pair).
        (0x3554, 0xF5DF), (0x3554, 0xF5DE), (0x3554, 0xF5DC), (0x3554, 0xF505), (0x3554, 0xF506), (0x3554, 0xF5D5), (0x3554, 0xF55E),
        (0x3554, 0xF523), (0x3554, 0xF510), (0x3554, 0xF50F), (0x3770, 0x0300), (0x1B1C, 0x2B2A),
        // The Sabre v2 Pro's other receivers, which OpenLinkHub reads as 2B2A (GPL; facts only; not measured by us).
        (0x1B1C, 0x2B34), (0x1B1C, 0x2B35),
        // MouseDock's profiles verified upstream (MIT): the EWEADN Z7's receiver and the ATK Zero; and the two its reader
        // table gives the same frame (ATK 12E9, the LAMZU Crystal's F50D), not measured.
        (0x3554, 0xF517), (AtkVendor, 0x11D8), (AtkVendor, 0x12E9), (0x3554, 0xF50D),
    ];
    private static readonly HashSet<(int, int)> CompxFramedSet = [.. CompxFramed];
    public static bool IsCompxFramed(int vendor, int product) => CompxFramedSet.Contains((vendor, product));
    // The pairs the brands' official web drivers (2026-11-15) read with this frame: their 17-byte 0xFF02 / usage 2 collection
    // gets it, and, unlike the pairs above, their other collections go on to the rules after (an ATK mouse's 0xFF05 keeps the
    // BitMouse read it was sent before these were listed).
    public static readonly (int Vendor, int Product)[] CompxOfficial =
    [
        // Not measured by us. ATK HUB (bpcdn.atkgear.com/hub-v3): every ATK and VXE mouse and receiver it reads with this frame
        // (its "COMPX" class), less the update-mode ids (3554:F2FF and F400 to F410, 373B:2xxx) and the A9 Ultra's pair
        // CompxVerified sends to its own way.
        (AtkVendor, 0x1014), (AtkVendor, 0x1016), (AtkVendor, 0x1017), (AtkVendor, 0x1018), (AtkVendor, 0x1019), (AtkVendor, 0x102F),
        (AtkVendor, 0x1030), (AtkVendor, 0x103C), (AtkVendor, 0x103D), (AtkVendor, 0x103E), (AtkVendor, 0x1045), (AtkVendor, 0x104A),
        (AtkVendor, 0x104B), (AtkVendor, 0x105B), (AtkVendor, 0x1065), (AtkVendor, 0x1066), (AtkVendor, 0x1067), (AtkVendor, 0x1068),
        (AtkVendor, 0x1069), (AtkVendor, 0x106A), (AtkVendor, 0x1084), (AtkVendor, 0x1086), (AtkVendor, 0x1087), (AtkVendor, 0x10AC),
        (AtkVendor, 0x10C8), (AtkVendor, 0x10CA), (AtkVendor, 0x10CB), (AtkVendor, 0x10CC), (AtkVendor, 0x10E7), (AtkVendor, 0x10E8),
        (AtkVendor, 0x10E9), (AtkVendor, 0x10FD), (AtkVendor, 0x1106), (AtkVendor, 0x1107), (AtkVendor, 0x110D), (AtkVendor, 0x110E),
        (AtkVendor, 0x1115), (AtkVendor, 0x1116), (AtkVendor, 0x1117), (AtkVendor, 0x111B), (AtkVendor, 0x1135), (AtkVendor, 0x113E),
        (AtkVendor, 0x113F), (AtkVendor, 0x1149), (AtkVendor, 0x114A), (AtkVendor, 0x114B), (AtkVendor, 0x114C), (AtkVendor, 0x1157),
        (AtkVendor, 0x1158), (AtkVendor, 0x1159), (AtkVendor, 0x115B), (AtkVendor, 0x115C), (AtkVendor, 0x115D), (AtkVendor, 0x115E),
        (AtkVendor, 0x11A0), (AtkVendor, 0x11A6), (AtkVendor, 0x11A7), (AtkVendor, 0x11A9), (AtkVendor, 0x11AA), (AtkVendor, 0x11AB),
        (AtkVendor, 0x11B3), (AtkVendor, 0x11C8), (AtkVendor, 0x11CC), (AtkVendor, 0x11CD), (AtkVendor, 0x11CE), (AtkVendor, 0x11CF),
        (AtkVendor, 0x11D2), (AtkVendor, 0x11DD), (AtkVendor, 0x11E3), (AtkVendor, 0x11E5), (AtkVendor, 0x11E7), (AtkVendor, 0x11EC),
        (AtkVendor, 0x11ED), (AtkVendor, 0x11F0), (AtkVendor, 0x11F2), (AtkVendor, 0x11F3), (AtkVendor, 0x11F4), (AtkVendor, 0x11F5),
        (AtkVendor, 0x11F6), (AtkVendor, 0x11F7), (AtkVendor, 0x11F9), (AtkVendor, 0x11FD), (AtkVendor, 0x11FE), (AtkVendor, 0x1201),
        (AtkVendor, 0x120A), (AtkVendor, 0x120B), (AtkVendor, 0x120D), (AtkVendor, 0x120E), (AtkVendor, 0x121F), (AtkVendor, 0x1230),
        (AtkVendor, 0x1245), (AtkVendor, 0x1246), (AtkVendor, 0x1248), (AtkVendor, 0x1249), (AtkVendor, 0x124A), (AtkVendor, 0x124E),
        (AtkVendor, 0x1256), (AtkVendor, 0x125A), (AtkVendor, 0x1266), (AtkVendor, 0x1269), (AtkVendor, 0x126C), (AtkVendor, 0x126D),
        (AtkVendor, 0x126E), (AtkVendor, 0x126F), (AtkVendor, 0x1270), (AtkVendor, 0x1271), (AtkVendor, 0x1272), (AtkVendor, 0x1273),
        (AtkVendor, 0x1274), (AtkVendor, 0x1279), (AtkVendor, 0x127A), (AtkVendor, 0x127C), (AtkVendor, 0x127E), (AtkVendor, 0x127F),
        (AtkVendor, 0x1285), (AtkVendor, 0x1286), (AtkVendor, 0x1289), (AtkVendor, 0x128A), (AtkVendor, 0x128B), (AtkVendor, 0x128C),
        (AtkVendor, 0x128D), (AtkVendor, 0x128E), (AtkVendor, 0x1292), (AtkVendor, 0x1296), (AtkVendor, 0x129B), (AtkVendor, 0x129C),
        (AtkVendor, 0x129D), (AtkVendor, 0x12A2), (AtkVendor, 0x12A7), (AtkVendor, 0x12A8), (AtkVendor, 0x12AB), (AtkVendor, 0x12AF),
        (AtkVendor, 0x12B0), (AtkVendor, 0x12B3), (AtkVendor, 0x12B4), (AtkVendor, 0x12BF), (AtkVendor, 0x12C6), (AtkVendor, 0x12CC),
        (AtkVendor, 0x12D7), (AtkVendor, 0x12F7), (AtkVendor, 0x12F9), (AtkVendor, 0x12FA), (AtkVendor, 0x12FB), (AtkVendor, 0x12FF),
        (AtkVendor, 0x1316), (AtkVendor, 0x131A), (AtkVendor, 0x132A), (AtkVendor, 0x132B), (AtkVendor, 0x133B), (AtkVendor, 0x1340),
        (AtkVendor, 0x1341), (AtkVendor, 0x1342), (AtkVendor, 0x1358), (AtkVendor, 0x1359), (AtkVendor, 0x1363), (0x3554, 0xF50A), (0x3554, 0xF50B),
        (0x3554, 0xF58D), (0x3554, 0xFB12),
        // VGN HUB (hub.vgnlab.com): the Dragonfly and F-series mice and receivers on 0xFF02 / 2.
        (0x3554, 0xF511), (0x3554, 0xFB51), (0x3554, 0xFB50), (0x3554, 0xFB49), (0x3554, 0xFB4A), (0x3554, 0xFB4B), (0x3554, 0xFB53),
        (0x3554, 0xFB52), (0x3554, 0xFB55), (0x3554, 0xFB54), (0x3554, 0xFB58), (0x3554, 0xFB59), (0x391D, 0x1005), (0x391D, 0x1A05),
        (0x391D, 0x101D), (0x391D, 0x1A1A), (0x391D, 0x1034), (0x391D, 0x1A2F), (0x391D, 0x1032), (0x391D, 0x102D), (0x391D, 0x1036),
        (0x391D, 0x1A31), (0x391D, 0x1035), (0x391D, 0x1A30), (0x391D, 0x1038), (0x391D, 0x1A32), (0x391D, 0x1039), (0x391D, 0x1A33),
        // Pulsar's web driver (bbb.pulsar.gg, its "cMouse" list; 3554:F40A there is an update-mode id and is left out, and so
        // are 3710:F004 and F005, whose role no source says).
        (0x3710, 0x5603), (0x3710, 0x5407), (0x3710, 0x5501), (0x3710, 0x5502), (0x3710, 0x5503), (0x3710, 0x5504), (0x3710, 0x3415),
        (0x3710, 0x3501), (0x3710, 0x3502), (0x3710, 0x3503), (0x3710, 0x3504), (0x3710, 0x3505), (0x3710, 0x3506), (0x3710, 0x3507),
        (0x3710, 0x3508), (0x3710, 0x3509), (0x3710, 0x3510), (0x3710, 0x3511), (0x3710, 0x3512), (0x3710, 0x3513), (0x3710, 0x3514),
        (0x3710, 0x3515), (0x3710, 0x3516), (0x3710, 0x3517), (0x3710, 0x3518), (0x3710, 0x3519), (0x3710, 0x3520), (0x3710, 0x3521),
        (0x3710, 0x3522), (0x3710, 0x3523), (0x3710, 0x3524), (0x3710, 0x3525), (0x3710, 0x3526), (0x3710, 0x3527), (0x3710, 0x3528),
        (0x3710, 0x3529), (0x3710, 0x7501), (0x3710, 0x7502), (0x3710, 0x7503), (0x3710, 0x7504), (0x3710, 0x7505), (0x3710, 0x7506),
        (0x3710, 0x7507), (0x3710, 0x7508), (0x3710, 0x7509), (0x3710, 0x7510), (0x3710, 0x9502), (0x3710, 0x9601), (0x3710, 0x7601),
        (0x3710, 0x3601), (0x3710, 0x5601), (0x3710, 0x7602), (0x3710, 0x7604), (0x3710, 0x3603),
        (0x3710, 0x7603), (0x3710, 0x9503), (0x3710, 0x3611), (0x3710, 0x3604), (0x3710, 0x3605), (0x3710, 0x3606), (0x3710, 0x3607),
        (0x3710, 0x3608), (0x3710, 0x3609), (0x3710, 0x3612), (0x3710, 0x3613), (0x3710, 0x3614), (0x3710, 0x3615), (0x3710, 0x3618),
        (0x3710, 0x3619), (0x3710, 0x3610), (0x3710, 0x7605),
        // Scyrox (F5F4 receiver, F515 cable), Attack Shark (FB43, FB44 receivers, F50E, F516 cables; the keyboard receiver F809,
        // which DELUX's driver also lists), EWEADN's "jiangmeng" mice (F59F to F5B5 the X23 Max, FB35 to FB48 the S9 and A8,
        // FB5E and FB5F the EXX Alpha6), AJAZZ's (F564, F567, F545 receivers, F566, F546 cables), DELUX's (FB26 and FB23
        // receivers, FB25 cable) and DAREU's receivers.
        (0x3554, 0xF5F4), (0x3554, 0xF515), (0x3554, 0xFB43), (0x3554, 0xFB44), (0x3554, 0xF50E), (0x3554, 0xF516), (0x3554, 0xF809),
        (0x3554, 0xF59F), (0x3554, 0xF5A0), (0x3554, 0xF5B2), (0x3554, 0xF5B3), (0x3554, 0xF5B5), (0x3554, 0xFB35), (0x3554, 0xFB36),
        (0x3554, 0xFB45), (0x3554, 0xFB47), (0x3554, 0xFB48), (0x3554, 0xFB5E), (0x3554, 0xFB5F), (0x3554, 0xF564), (0x3554, 0xF567),
        (0x3554, 0xF545), (0x3554, 0xF566), (0x3554, 0xF546), (0x3554, 0xFB26), (0x3554, 0xFB23), (0x3554, 0xFB25), (0x260D, 0x1108),
        (0x260D, 0x1114), (0x260D, 0x1115), (0x260D, 0x1135), (0x260D, 0x1156), (0x260D, 0x1163), (0x260D, 0x1219), (0x260D, 0x1227),
        (0x260D, 0x1238), (0x260D, 0x1246), (0x260D, 0x1257), (0x260D, 0x1258), (0x260D, 0x1261), (0x260D, 0x1288), (0x260D, 0x1333),
    ];
    private static readonly HashSet<(int, int)> CompxOfficialSet = [.. CompxOfficial];
    public static bool IsCompxOfficial(int vendor, int product) => CompxOfficialSet.Contains((vendor, product));
    // G-Wolves' shared 8K receiver and the Lycan on its cable belong to the 0x83 family (F83Models), but a unit without that
    // feature report answers this frame on its 17-byte collection instead (HaloBattery's pulsar.py, measured on a WARG's
    // receiver, #297; Fan4Metal's mouse_tray lists the pair for it, facts only). Asked only where the 0x83 exchange has no
    // collection, so neither family takes a collection the other does.
    public static readonly (int Vendor, int Product)[] CompxBesideF83 = [(GwolvesVendor, 0x3854), (GwolvesVendor, 0x4719)];
    // The keyboards among them (the Teevolution Terra Pro's receiver); the rest are mice.
    // F809, Attack Shark's and DELUX's keyboard receiver, is one too.
    public static DeviceKind CompxKind(int vendor, int product) => (vendor, product) is (0x3554, 0xF523) or (0x3554, 0xF809) ? DeviceKind.Keyboard : DeviceKind.Mouse;
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

    // ----- The Compx frame split over two collections -----

    // The same 17-byte frame, set as feature report 0x08 on the 0xFF02 / usage 2 collection (which has no output report), and
    // answered as an input report on the receiver's 0xFF01 / usage 0 collection, under header 0x09 (or 0x08). From HaloBattery
    // (MIT): irocks.py (iRocks M31R, measured on both ids in #309; its receiver answers from a cache while the mouse is off,
    // so command 0x03 is asked first and only 1 at 6 is the mouse online), dareu.py (DAREU A950 on 260D:1074 measured, 1084
    // from MousePower) and endgame_we.py (Endgame Gear XM2we 3367:1961, okms/xm2we-linux's protocol). Data: the reply's 2
    // is 0 for data (1 an empty acknowledgement), as the DAREU and Endgame sources check; Status: also its 3 and 4 zero and its
    // 5 the data length 1 to 10, which the Endgame source alone checks. iRocks's source checks neither, so neither is asked of it.
    public sealed record CompxSplitModel(int Vendor, int Product, string Name, bool Online, bool Status, int Receiver = -1, bool Data = false);
    public static readonly IReadOnlyList<CompxSplitModel> CompxSplit =
    [
        new(0x14A5, 0xC009, "iRocks M31R", Online: true, Status: false), new(0x14A5, 0xC010, "iRocks M31R", Online: false, Status: false, Receiver: 0xC009),
        new(0x260D, 0x1074, "DAREU A950", Online: false, Status: false, Data: true), new(0x260D, 0x1084, "DAREU", Online: false, Status: false, Data: true),
        new(EndgameVendor, 0x1961, "Endgame Gear XM2we", Online: false, Status: true, Data: true),
    ];
    public static CompxSplitModel? CompxSplitFind(int vendor, int product) => CompxSplit.FirstOrDefault(model => model.Vendor == vendor && model.Product == product);
    public static bool CompxSplitCommand(int usagePage, int usage, int featureLength) => UsagePages.Is(usagePage, 0xFF02) && usage == 0x0002 && featureLength >= 17;
    public static bool CompxSplitReply(int usagePage, int usage, int inputLength) => UsagePages.Is(usagePage, 0xFF01) && usage == 0x0000 && inputLength >= 17;
    private static bool CompxSplitFrame(byte[] r, byte command)
    {
        if (r.Length < 17 || r[0] is not (0x08 or 0x09) || r[1] != command) return false;
        int sum = 0; for (int i = 0; i < 17; i++) sum += r[i];
        if ((byte)sum != 0x55) return false;
        bool echo = true; for (int i = 2; i < 16; i++) echo &= r[i] == 0;
        return !echo;
    }
    public static BatteryRead? CompxSplitBattery(CompxSplitModel model, byte[] r)
    {
        if (!CompxSplitFrame(r, 0x04) || r[6] > 100) return null;
        if (model.Data && r[2] != 0 || model.Status && (r[3] != 0 || r[4] != 0 || r[5] is < 1 or > 10)) return null;
        return new(r[6], r[7] != 0);
    }
    // The answer to 0x03: whether the mouse is online; null for another report.
    public static bool? CompxSplitOnline(byte[] r) => CompxSplitFrame(r, 0x03) && r[2] == 0 ? r[6] == 1 : null;

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
    // BrandOnly: Name is the brand alone (the model unknown), so the product string names the device where it has one.
    public sealed record F83Model(int Vendor, int Product, string Name, bool Cable, F83Exchange Exchange = F83Exchange.New, bool Strict = false,
        bool Clamp = false, int Interface = -1, int Receiver = -1, bool BrandOnly = false);
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
            // The A5 Pro's and AX5's ids, by Klegus/mchose-macos's table (MIT), whose transport (feature report 0 on interface 2,
            // page 0xFFFF, the A1 marker) is the A5 Pro Max's; the 0x83 read itself is not measured on them.
            new(0x2023, 0xF017, "MCHOSE A5 Pro", true, Clamp: true, Interface: 2), new(0x2023, 0xF022, "MCHOSE AX5 Pro Max", true, Clamp: true, Interface: 2),
            new(0x2023, 0xF024, "MCHOSE AX5 Pro", false, Clamp: true, Interface: 2),
            new(0x2023, 0xF026, "MCHOSE AX5 Pro", true, Clamp: true, Interface: 2, Receiver: 0xF024),
            // The shared 8K receiver does not say which mouse is behind it.
            new(GwolvesVendor, 0x3854, "G-Wolves", false, Strict: true),
        };
        // LAMZU's other receivers (the 4K and 8K ones, and the 1K ones 37B0:000B to 002B and 373E:001D), from LAMZU's web driver
        // (its 0x83 class), on interface 2 as the Maya X and Inca; not measured by us.
        // Left out: 000C (the Tachi's, which HaloBattery took off its list), and 0048 and 0054 (the LM20 receivers, whose
        // firmware-update mode keeps the same id, so a receiver being updated would be asked).
        foreach (var product in new[] { 0x0015, 0x000E, 0x001B, 0x0023, 0x001E, 0x0027, 0x002A, 0x002E, 0x0032, 0x0036, 0x003B, 0x003F,
            0x000B, 0x000F, 0x0013, 0x000D, 0x0019, 0x0003, 0x002B })
            models.Add(new(0x37B0, product, "LAMZU", false, Interface: 2, BrandOnly: true));
        models.Add(new(0x373E, 0x0016, "LAMZU", false, Interface: 2, BrandOnly: true));
        models.Add(new(0x373E, 0x001D, "LAMZU", false, Interface: 2, BrandOnly: true));
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

    // ----- MCHOSE: the 7 series' RealTek exchange, the reads on report 0x55, and the Gmouse query -----

    // MCHOSE's 7 series (L7, M7, A7, K7) on vendor 0x5253 (the M7 Ultra's and L7 Pro's receiver 5253:1020, the A7's
    // 5253:1021, the M7 Ultra on its cable 5253:0031) and on MCHOSE's newer 0x3837 (the A7 V2 Ultra's 3837:100B), usage page
    // 0xFF01: feature report 0x11 (20 bytes) or 0x12 (64) carrying command 0x06 with every payload byte inverted, set and then
    // got back, asked afresh each time (one ask read twice only returns the request). The reply echoes 0xF9 (0x06 inverted) at
    // 1 and inverts its payload from 2: vendor id and model little endian, firmware (4 bytes), flags, level, charging. From
    // HaloBattery's mchose.py (measured on an M7 Ultra), after alexfrih/mchose-linux's PROTOCOL.md (the vendor driver's
    // protocol for the 7 series). The A7's receiver id is from marsuyone/mchose-battery-tray's notes (facts only), whose
    // sample reply carries model 0x0070 (the A7 in the driver's table) and its level where the M7 Ultra's is. On 0x5253 this
    // is the one read sent, to any receiver with that collection, as the driver does.
    // What else a reply says: a payload decoded all zero (vendor 0) is no answer: the receiver gives it while the mouse sleeps
    // (HaloBattery, eight seconds of it), but also in place of the first answer after idle (aipno/mchose-linux) and it is the
    // request itself read back, so it cannot say asleep and is skipped. A real reply with a level of 0 is the mouse asleep
    // (Voltide's MchoseA7UltraProvider.cs reads it so). Charging is the charge status 1 (HaloBattery and Voltide measured 1 on
    // the cable); 2 is charged on the cable (HifumiDaisuki01/MchoseBatteryTray's reading of M HUB), not charging. The flags
    // byte (link mode in bits 0 to 2, link up in bit 3, as M HUB's parser defines it) is not read: no source measured a reply
    // without the link.
    public const int MchoseRealtek = 0x5253;
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
        if (vendor is not (MchoseRealtek or MchoseVendor or MchoseKeyboardVendor) || Byte(9) > 100) return null;
        int model = Byte(2) | Byte(3) << 8;
        return Byte(9) == 0 ? (new(null, Asleep: true), model) : (new(Byte(9), Byte(10) == 1), model);
    }
    // The model in the reply, by the vendor driver's own table as mchose-linux lists it (names only).
    public static string? MchoseRealtekModel(int model) => model switch
    {
        0x00B0 => "MCHOSE L7 Pro", 0x00B1 => "MCHOSE L7 Ultra", 0x00C0 => "MCHOSE L7",
        0x0020 => "MCHOSE M7", 0x0030 => "MCHOSE M7 Pro", 0x0031 => "MCHOSE M7 Ultra", 0x0070 => "MCHOSE A7",
        _ => null
    };

    // MCHOSE's own vendor id 0x3837 carries several chips, each with its own read: the 7 series' RealTek exchange above, the
    // status read on output report 0x55 that M HUB (MCHOSE's driver) uses for its newer devices, M HUB's framed read for its
    // headsets on the same report, the 0x4D frame of the web driver's mice, the Gmouse query, the configuration read of the
    // keyboards on the 3033 receiver (MchoseConfig), the battery command of the G98 V3 and K87 V3 (MchoseQhw) and the 13 4A read
    // of M HUB's "BY" keyboards (MchoseKeyboardRequest). Which one a model speaks is
    // not something a receiver can be asked, so it is not looked up by model: each read goes only to the collection shape its
    // source documents and only where the collection declares the read's report id, the kind (MchoseKind) decides the
    // order (the reads measured on hardware first), and a reply counts only when it has that read's exact shape. The read
    // that answers is kept for the device (HidBatteries), and the others are no longer sent to it. 0x3837 is
    // MCHOSE's alone, so every firmware behind it is MCHOSE's; the chip makers' and shared ids keep their lists (Gmouse's
    // 0xA8A5, the A5 Pro Max's 0x2023, the 41E4 keyboards), and 0x5253 gets the RealTek exchange only.
    public enum MchoseRead { Realtek, Status, Audio, Frame4D, Gmouse, Config, Qhw, By }
    // The kind by the name, else for the products whose name does not say it: the V9 Turbo's dongle and headset and the X9
    // headset, the G98 V3 keyboard (which does not answer the RealTek exchange, HaloBattery #362) and the keyboards' 3033
    // receiver and their cables, whose names do not say it, and M HUB's BY keyboard on 201B. The MagDock (1012) and the ids M
    // HUB lists in firmware-update mode (MchoseUpdateProducts) are sent nothing.
    // The V9 Turbo headset's dock (6009), V9 Turbo (600D), V9 Pro 2 (6011) and R9 (6013) are headsets and the K20 Pro (6003),
    // K20 (60C4) and K20 GT (60C6) speakers, by wzk112/MchoseHub's device catalog (no license; the ids only).
    // Legacy: without those hints, as the device keys were made before them (a key keeps its prefix).
    public static DeviceKind? MchoseKind(int product, string name, bool legacy = false) => product switch
    {
        0x1012 => DeviceKind.Other,
        _ when Devices.Named(name) is { } named => named,
        0x6008 or 0x600A or 0x6045 => DeviceKind.Headphones,
        0x6009 or 0x600D or 0x6011 or 0x6013 when !legacy => DeviceKind.Headphones,
        0x6003 or 0x60C4 or 0x60C6 when !legacy => DeviceKind.Speaker,
        0x1015 or 0x2023 or 0x2025 or 0x201B => DeviceKind.Keyboard,
        _ when MchoseConfigProducts.Contains(product) => DeviceKind.Keyboard,
        _ => null
    };
    // outputIds, featureIds: the report ids the collection declares; null where they are not known (Find, which only decides
    // whether the collection is MCHOSE's to read), and then the shape alone decides.
    public static IReadOnlyList<MchoseRead> MchoseReads(int product, string name, int usagePage, int usage, int featureLength, int outputLength,
        IReadOnlySet<byte>? outputIds = null, IReadOnlySet<byte>? featureIds = null)
    {
        // The keyboards whose read the web driver gives get that read alone: their other vendor collections are for firmware
        // updates (the cables' 0xFF00) or unknown, and are sent nothing. The G98 V3 and K87 V3 keep the status read they were
        // asked before, after it.
        MchoseRead[] order = MchoseUpdateProducts.Contains(product) ? []
            : MchoseConfigProducts.Contains(product) ? [MchoseRead.Config]
            : MchoseQhwProducts.Contains(product) ? [MchoseRead.Qhw, MchoseRead.Status]
            : product == 0x201B ? [MchoseRead.By]
            : MchoseKind(product, name) switch
        {
            DeviceKind.Mouse => [MchoseRead.Realtek, MchoseRead.Frame4D, MchoseRead.Gmouse, MchoseRead.Status],
            DeviceKind.Keyboard => [MchoseRead.Status],
            DeviceKind.Headphones or DeviceKind.Earbuds or DeviceKind.Speaker => [MchoseRead.Audio, MchoseRead.Status],
            null => [MchoseRead.Realtek, MchoseRead.Frame4D, MchoseRead.Gmouse, MchoseRead.Audio, MchoseRead.Status],
            _ => []
        };
        bool Output(byte id) => outputIds is null || outputIds.Contains(id);
        return [.. order.Where(read => read switch
        {
            MchoseRead.Realtek => usagePage == 0xFF01 && featureLength >= 21 && (featureIds is null || featureIds.Contains(0x11) || featureIds.Contains(0x12)),
            MchoseRead.Status or MchoseRead.Audio => usagePage >= 0xFF00 && outputLength >= 64 && Output(0x55),
            MchoseRead.Frame4D => usagePage is 0xFF01 or 0xFF0B && usage != 0x0010 && Output(0x4D),
            MchoseRead.Gmouse => usagePage == 0xFF01 && usage == 0x0010 && outputLength >= 65 && Output(0x00),
            MchoseRead.Config => MchoseConfigProducts.Contains(product) && usagePage == 0x0001 && usage == 0x0000 && outputLength >= 65 && Output(0x00),
            MchoseRead.Qhw => MchoseQhwProducts.Contains(product) && !name.Contains("CH585", StringComparison.OrdinalIgnoreCase)
                && usagePage == 0xFF80 && usage == 0x0001 && outputLength >= 9 && Output(0x4D),
            MchoseRead.By => product == 0x201B && usagePage == 0xFF02 && outputLength >= 20 && Output(0x13),
            _ => false
        })];
    }

    // MCHOSE's keyboards on the 2.4 GHz receiver 3837:3033 and on their cables (K87S 200E, G87 V2 2015, K99 V3 2020, G75 V2
    // 2021, the driver's table giving the cables the same collection and battery feature): the K87S, G87 V2, K99 V3 and G75
    // V2, the receiver's told apart by the product string. The cables' 0xFF00 collection is only for firmware updates and is
    // never sent to. The vendor's web driver (mchose.com.cn/cizhou) lists them so and gives the battery to these four among its
    // keyboards; it speaks to the collection on usage page 1, usage 0, with 64-byte reports and no report id, in frames
    // 55 <command> 00 <sum> <size> <offset low> <offset high> 00 <data>, answered AA <command> 00 <sum> <size> <offset low>
    // <offset high> 00 <data>, the sum being the low byte of every byte after it. Command 05 reads the function configuration,
    // 64 bytes a profile, whose byte 32 holds the level in its low seven bits and charging in bit 7. Asked here as the driver
    // asks on connecting: profile 0's first 56 bytes (55 05 00 38 38 00 00 00), the level then at 8 + 32 of the answer.
    // Measured on a K87S (#291): 0x35 on battery and 0xB5 charging at 53 %, 0x42 and 0xC2 at 66 %, the same in profiles 0 and
    // 1 whichever is active, the sum holding on every answer. The keyboard also sends FA FB 07 <level> <charging> unasked, but
    // only when its charging changes (nothing else in half an hour there), so it is asked. The 4D frames do not reach these
    // keyboards (nothing answered there). A level of 0 is no reading.
    // The ids M HUB's tables give devices in firmware-update mode (the keyboards' boot ids, the Qhw keyboards' 1017, 2024 and
    // 2026, the 0xFF00 update collections' 200F to 303D): never sent to, whatever their collections.
    public static readonly IReadOnlySet<int> MchoseUpdateProducts = new HashSet<int>
        { 0x1017, 0x2024, 0x2026, 0x200F, 0x2010, 0x2019, 0x201F, 0x2022, 0x3009, 0x300B, 0x3014, 0x3027, 0x3029, 0x3034, 0x303D, 0x301F };
    public const int MchoseConfigReceiver = 0x3033;
    public static readonly IReadOnlySet<int> MchoseConfigProducts = new HashSet<int> { MchoseConfigReceiver, 0x200E, 0x2015, 0x2020, 0x2021 };
    public static byte[] MchoseConfigRequest()
    {
        var frame = new byte[65];
        new byte[] { 0x00, 0x55, 0x05, 0x00, 0x38, 0x38, 0x00, 0x00, 0x00 }.CopyTo(frame, 0);
        return frame;
    }
    public static BatteryRead? MchoseConfig(byte[] r)
    {
        if (r.Length < 41 || r[0] != 0xAA || r[1] != 0x05 || r[5] != 0x00 || r[6] != 0x00) return null;
        int size = r[4];
        if (size is < 33 or > 0x38 || r.Length < 8 + size) return null;
        int sum = 0;
        for (int i = 4; i < 8 + size; i++) sum += r[i];
        if (r[3] != (byte)sum) return null;
        int level = r[40] & 0x7F;
        return level is > 0 and <= 100 ? new(level, (r[40] & 0x80) != 0) : null;
    }

    // The G98 V3 and K87 V3 on their receiver 3837:1015 and cables 2025 and 2023: the web driver's "Qhw" channel (mchose.com.cn/
    // cizhou), output report 0x4D on usage page 0xFF80, usage 1, packets of 31 bytes after the id: version 01, flags 03, the
    // data's length, the command (low byte first), a business code 00, a sequence number, the data, and the XOR of every byte
    // from the flags on. Its getBattery is command 0x0904 with no data: 4D 01 03 00 04 09 00 00 0E. The answer has the same
    // header (the driver matches version, flags, command, code and sequence), its data at 8: the charge status (0 on battery,
    // 1 charging, 2 full) and the level. Not measured by us. The same ids in firmware-update mode are named "CH585" and are
    // never sent to; the driver's 1017, 2024 and 2026 are update modes too and not in the list. The 4D frame that enters update
    // mode (AA 55 AA 55 AA 55) is never sent. A level of 0 is no reading.
    public static readonly IReadOnlySet<int> MchoseQhwProducts = new HashSet<int> { 0x1015, 0x2025, 0x2023 };
    public static byte[] MchoseQhwRequest() => [0x4D, 0x01, 0x03, 0x00, 0x04, 0x09, 0x00, 0x00, 0x0E];
    public static BatteryRead? MchoseQhw(byte[] r)
    {
        if (r.Length < 10 || r[0] != 0x4D || r[1] != 0x01 || r[2] != 0x03 || r[4] != 0x04 || r[5] != 0x09 || r[6] != 0x00 || r[7] != 0x00 || r[3] < 2) return null;
        int level = r[9];
        return level is > 0 and <= 100 && r[8] <= 2 ? new(level, r[8] == 1) : null;
    }

    // The status read on report 0x55: 55 65 01, zeros to 64 bytes, answered by 55 65 <level> <state>, the state 2 on battery,
    // 3 charging, 4 full, 26 asleep; anything else is no answer. M HUB's charge-status listener reads it so (HaloBattery #189,
    // from M HUB's driver), and JoaoKSS/MCHOSE_v9_PRO_Controller asks a V9 headset the same way. On the V9 Turbo+ it gave the
    // level M HUB shows (HaloBattery #193); on its cable that headset answers nothing, so it shows no charging.
    public static byte[] MchoseStatusRequest() { var frame = new byte[64]; frame[0] = 0x55; frame[1] = 0x65; frame[2] = 0x01; return frame; }
    public static BatteryRead? MchoseStatus(byte[] r) => r.Length < 4 || r[0] != 0x55 || r[1] != 0x65 ? null : r[3] switch
    {
        26 => new(null, Asleep: true),
        2 or 3 or 4 when r[2] <= 100 => new(r[2], r[3] == 3),
        _ => null
    };

    // M HUB's framed read for its headsets on report 0x55: AA, version 1, flags (1: checksummed), length (payload + 1), the
    // command type (00 01) and command 0x0B, then the payload and the checksum (flags, length, type, command and payload
    // XORed; 0 when the flags say none). The answer's payload is the sleep state (0 asleep), then the percent with bit 7 set
    // while charging. From HaloBattery's mchose.py (M HUB's senders and parser); tried before the status read, as M HUB does.
    public static byte[] MchoseAudioRequest()
    {
        var frame = new byte[64];
        new byte[] { 0x55, 0xAA, 0x01, 0x00, 0x01, 0x00, 0x01, 0x0B, 0x00 }.CopyTo(frame, 0);
        return frame;
    }
    public static BatteryRead? MchoseAudio(byte[] r)
    {
        if (r.Length < 11 || r[0] != 0x55 || r[1] != 0xAA || r[5] != 0x00 || r[6] != 0x01 || r[7] != 0x0B) return null;
        int flags = r[3], length = r[4];
        if (flags > 1 || length < 3 || r.Length < 8 + length) return null;
        int check = flags ^ length ^ r[5] ^ r[6] ^ r[7];
        for (int i = 8; i < 8 + length - 1; i++) check ^= r[i];
        if (r[8 + length - 1] != (flags == 1 ? check : 0)) return null;
        if (r[8] == 0) return new(null, Asleep: true);
        int level = r[9] & 0x7F;
        return level <= 100 ? new(level, (r[9] & 0x80) != 0) : null;
    }

    // MCHOSE keyboards on the 2.4 GHz receiver 41E4:2001 (a G87 in marsuyone/mchose-battery-tray's notes and
    // kmizmal/mchose-battery's G87Protocol.cs, a K87 in HaloBattery's mchose_kb.py; each measured its own), usage page 0xFF02,
    // the collection that declares report 0x13: the output report 13 4A 01 00, zeros, and at 19 the low byte of the sum of
    // the nineteen bytes before it (5E): getBattery's wireless command as M HUB, the vendor's web driver, sends it
    // (kmizmal/mchose-battery, from M HUB's code and measured on a G87). The answer is the input report 13 4A 01 00 02
    // <percent> <flags>, with the same sum at 19 (HaloBattery's K87 answer: 13 4A 01 00 02 64 01 00 .. C5). Byte 4 (02 in every
    // answer, 00 in the request) tells the answer from the request read back. A keyboard asleep does not answer. The flags are
    // M HUB's fullStatus (low nibble) and chargeStatus (high nibble): 01 on a full keyboard off its cable (HaloBattery,
    // kmizmal), 10 on the cable (kmizmal); only the high nibble is charging. The receiver's feature report 0x06 on 0xFF04 is not
    // read. Facts only from marsuyone's notes (no license); HaloBattery is MIT, kmizmal/mchose-battery GPL (facts only).
    public const int MchoseKeyboardVendor = 0x41E4, MchoseKeyboardReceiver = 0x2001;
    // The A5 V2 Ultra on its cable, 41E4:1101: the RealTek exchange on its 0xFF01 collection (WanCixiong/MCHOSE-Tray-Indicator,
    // MIT, measured; its receiver is 3837:100C).
    public const int MchoseA5Cable = 0x1101;
    // MCHOSE's second 4K receiver on 41E4:1102 (the web driver's ryMouseFilters and its "4k_v2_dongle" firmware for 0x1102, the
    // receiver 3837:100C also is): the same RealTek exchange on 0xFF01.
    public const int MchoseRealtekV2Receiver = 0x1102;
    public static byte[] MchoseKeyboardRequest()
    {
        var frame = new byte[20]; frame[0] = 0x13; frame[1] = 0x4A; frame[2] = 0x01;
        frame[19] = MchoseKeyboardSum(frame);
        return frame;
    }
    private static byte MchoseKeyboardSum(byte[] frame) { int sum = 0; for (int i = 0; i < 19; i++) sum += frame[i]; return (byte)sum; }
    // A level of 0 is no reading (a keyboard that answers is not empty), as is a frame whose sum does not hold.
    public static BatteryRead? MchoseKeyboardBattery(byte[] reply)
        => reply.Length >= 20 && reply[0] == 0x13 && reply[1] == 0x4A && reply[4] > 0 && reply[5] is > 0 and <= 100 && reply[19] == MchoseKeyboardSum(reply)
            ? new(reply[5], (reply[6] & 0xF0) != 0) : null;

    // The Gmouse query (MCHOSE G3 V2 on 3837:4245 and G7 on A8A5:2255, collection 0xFF01 usage 0x10): a 65-byte output
    // report 00 55 30 A5 0B 2E 01 01 01, answered by an input report starting AA 30 with the level at 8 and charging at 9. As
    // vigil-watt (G3 V2, measured) and HaloBattery (G7, measured) read it. On 0x3837 it is one of MCHOSE's reads (MchoseReads);
    // 0xA8A5 is a chip maker's id, so there it goes to the G7 alone.
    public static readonly (int Vendor, int Product)[] Gmouse = [(0xA8A5, 0x2255)];
    // The G7 on its cable, A8A4:2255: the same chip's wired id, which EWEADN's mice share, so only a collection named exactly
    // "MCHOSE G7" or "MCHOSE G7 Pro" (MouseDock's rule for it, MIT; not measured there).
    public static bool GmouseNamed(int vendor, int product, string name)
        => vendor == 0xA8A4 && product == 0x2255 && name.Trim() is var trimmed
        && (trimmed.Equals("MCHOSE G7", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("MCHOSE G7 Pro", StringComparison.OrdinalIgnoreCase));
    public static byte[] GmouseRequest() { var frame = new byte[65]; new byte[] { 0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01 }.CopyTo(frame, 0); return frame; }
    // The VGN N87 keyboard on the same receiver A8A5:2255 and on its cable A8A4:2355 (VGN HUB's table: the 0xFF01 / 0x10
    // collection of interface 2, report 0xF0): its "yjx" protocol, a 63-byte output report F0 <command> 02 <check> <length>
    // <address, 2> 00 <data>, the check the low byte of the sum of every byte from the length on (set in answers that carry
    // data). getBattery is command 0x30 with a length of 2: F0 30 02 00 02 00 00 00; the answer F0 30 02 <check> <length> ..
    // <level> <charging 1> from 8. The Gmouse query goes only where the collection declares report 0, the N87's only where it
    // declares 0xF0. Not measured by us.
    public static readonly (int Vendor, int Product) N87Cable = (0xA8A4, 0x2355);
    public static readonly (int Vendor, int Product)[] N87Ids = [(0xA8A5, 0x2255), N87Cable];
    public static byte[] N87Request() => BrandFamilies.Frame(64, 0xF0, 0x30, 0x02, 0x00, 0x02);
    public static BatteryRead? N87Battery(byte[] r)
    {
        if (r.Length < 10 || r[0] != 0xF0 || r[1] != 0x30 || r[2] != 0x02 || r[4] is < 2) return null;
        int sum = 0; for (int i = 4; i < r.Length && i < 64; i++) sum += r[i];
        if (r[3] != (byte)sum) return null;
        return r[8] is > 0 and <= 100 ? new(r[8], r[9] == 1) : null;
    }
    // A level of 0 is the placeholder of an answer not ready yet (vigil-watt's MchoseGmouseCodec.cs), no reading.
    public static BatteryRead? GmouseBattery(byte[] reply)
    {
        int at = reply.Length > 1 && reply[0] == 0xAA && reply[1] == 0x30 ? 0 : reply.Length > 2 && reply[1] == 0xAA && reply[2] == 0x30 ? 1 : -1;
        if (at < 0 || reply.Length < at + 10 || reply[at + 8] is 0 or > 100) return null;
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
        foreach (var cable in CompxSplit.Where(model => model.Receiver >= 0)) Pair(cable.Vendor, cable.Receiver, cable.Product);
        Pair(0x1E71, 0x2101, 0x2129);
        foreach (var (receiver, cable) in Asus.Pairs) Pair(Asus.Vendor, receiver, cable);
        foreach (var (receiver, cable) in SteelSeriesMice.Pairs) Pair(Headsets.SteelSeries, receiver, cable);
        return [.. twins.Distinct()];
    }
    // A dongle and the same headset on its cable that both answer while both are plugged in: the dongle's entry is left out
    // whenever the cable read the headset, asleep or not (the Audeze Maxwell's dongles and its cable, which HaloBattery's
    // audeze.py merges the same way, the cable winning).
    public static readonly (string Receiver, string Cable)[] CableWins =
    [
        (Devices.Hardware(AirohaRace.Audeze, AirohaRace.Maxwell), Devices.Hardware(AirohaRace.Audeze, AirohaRace.MaxwellCable)),
        (Devices.Hardware(AirohaRace.Audeze, AirohaRace.MaxwellXbox), Devices.Hardware(AirohaRace.Audeze, AirohaRace.MaxwellCable)),
    ];
    public static bool Shadowed(DeviceBattery device, IEnumerable<DeviceBattery> devices)
        => device.Hardware is { } hardware
        && (device.Asleep && Twins.Any(twin => twin.Receiver == hardware && devices.Any(other => !other.Asleep && other.Percent is not null && other.Hardware == twin.Cable))
            || CableWins.Any(twin => twin.Receiver == hardware && devices.Any(other => !other.Asleep && other.Percent is not null && other.Hardware == twin.Cable)));

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
    // a connection of 0 means the mouse is not connected, and a first byte of 0xFF that the receiver is not ready.
    public const int MchoseVendor = 0x3837;
    public static byte[] MchoseRequest()
    {
        var frame = new byte[64];
        frame[0] = 0x4D; frame[1] = 0x01; frame[2] = 0x01; frame[3] = 0x00; frame[4] = 0x00; frame[5] = 0x09;
        byte xor = 0; for (int i = 2; i < 8; i++) xor ^= frame[i];
        frame[8] = xor;
        return frame;
    }
    // The answer: 4D 01 <flags 0 or 1> <payload length> 00 09 .. .., the payload, and with flags 1 the XOR of bytes 2 to the
    // payload's end after it (mitsimi/mchose-tray, GPL, facts only, and kmizmal/mchose-battery, GPL, facts only: both check it;
    // mitsimi's K7 V2 Ultra+ sample 4D 01 01 10 00 09 00 00 37 38 .. 02 00 53 .. D8, 83 %). A payload too short to hold the
    // level is the error a collection that does not take the command gives (kmizmal measured 4D 01 01 01 00 09 00 00 FE F7 on
    // a V7 mouse's own collection), no reading. The connection at 10 is 0 while the receiver has no link to the mouse (asleep
    // or off; 2 on mitsimi's 2.4 GHz link, 1 on kmizmal's V7), a first payload byte FF the receiver busy (hxw-dream/
    // MCHOSE-Battery). Charging is the charge status 1: M HUB shows charging only for 1, and kmizmal measured 1 charging and 2
    // on the cable but not charging.
    public static BatteryRead? MchoseBattery(byte[] reply)
    {
        if (reply.Length < 9 || reply[0] != 0x4D || reply[1] != 0x01 || reply[2] > 1 || reply[4] != 0x00 || reply[5] != 0x09 || reply[8] == 0xFF) return null;
        int length = reply[3], end = 8 + length;
        if (length < 13 || end + reply[2] > reply.Length) return null;
        if (reply[2] == 1)
        {
            byte check = 0;
            for (int i = 2; i < end; i++) check ^= reply[i];
            if (reply[end] != check) return null;
        }
        if (reply[8 + 10] == 0) return new(null, Asleep: true);
        int level = reply[8 + 12];
        return level is > 0 and <= 100 ? new(level, reply[8 + 11] == 1) : null;
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
        Asus, SteelSeries, RapooGen1, Aula, MchoseKeyboard, CompxSplit, AtkSerial
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
    // outputIds: the output report ids the collection declares; null where they are not known (then the shape alone decides).
    public static Protocol? Find(int vendor, int product, int usagePage, string productName, int featureLength, int iface = -1, int usage = -1,
        int inputLength = 0, int outputLength = 0, int collection = -1, IReadOnlySet<byte>? outputIds = null)
    {
        if (IsAjazz(vendor, product)) return AjazzCollection(iface, usagePage, usage, inputLength, outputLength) ? Protocol.Ajazz : null;
        if (vendor == AulaVendor && product == AulaF87Pro)
            return usagePage == 0xFF02 && usage == 0x0002 && inputLength == 20 && outputLength == 20 && featureLength == 0 && (outputIds is null || outputIds.Contains(0x13)) ? Protocol.Aula : null;
        if (IsCompxFramed(vendor, product)) return CompxFramedCollection(usagePage, usage, inputLength, outputLength) ? Protocol.CompxFramed : null;
        if (IsCompxOfficial(vendor, product) && CompxFramedCollection(usagePage, usage, inputLength, outputLength)) return Protocol.CompxFramed;
        if (F83Find(vendor, product) is { } f83)
            return F83Collection(f83, iface, usagePage, featureLength) ? Protocol.F83
                : CompxBesideF83.Contains((vendor, product)) && CompxFramedCollection(usagePage, usage, inputLength, outputLength) ? Protocol.CompxFramed : null;
        if (Gmouse.Contains((vendor, product)) || GmouseNamed(vendor, product, productName) || (vendor, product) == N87Cable)
            return usagePage == 0xFF01 && usage == 0x0010
                && (outputLength >= 65 && (outputIds is null || outputIds.Contains(0x00)) && (vendor, product) != N87Cable
                    || N87Ids.Contains((vendor, product)) && outputLength >= 64 && outputIds?.Contains(0xF0) == true)
                ? Protocol.Gmouse : null;
        if (CompxSplitFind(vendor, product) is not null) return CompxSplitCommand(usagePage, usage, featureLength) ? Protocol.CompxSplit : null;
        if (vendor == NordicVendor && NinjutsoProducts.Contains(product)) return usagePage == 0xFFA0 && featureLength >= 32 ? Protocol.Nordic : null;
        // MCHOSE: on 0x5253 the 7 series' exchange on its configuration collection; on 0x3837 whichever of the brand's reads
        // the collection's shape takes (MchoseReads).
        if (vendor == MchoseRealtek) return usagePage == 0xFF01 && featureLength >= 21 ? Protocol.MchoseRealtek : null;
        if (vendor == MchoseVendor) return MchoseReads(product, productName, usagePage, usage, featureLength, outputLength).Count > 0 ? Protocol.Mchose : null;
        if (vendor == MchoseKeyboardVendor && product is MchoseA5Cable or MchoseRealtekV2Receiver) return usagePage == 0xFF01 && featureLength >= 21 ? Protocol.MchoseRealtek : null;
        if (vendor == MchoseKeyboardVendor && product == MchoseKeyboardReceiver)
            return usagePage == 0xFF02 && inputLength >= 20 && outputLength >= 20 && (outputIds is null || outputIds.Contains(0x13)) ? Protocol.MchoseKeyboard : null;
        if (vendor == 0x3151 && product == 0x402D) return iface == 2 && usagePage == 0xFFFF && usage == 0x0002 && featureLength == 65 ? Protocol.Lingbao : null;
        if (vendor == 0x1E71 && product is 0x2101 or 0x2129) return usagePage == 0xFFCA && usage == 0x0001 && outputLength >= 64 ? Protocol.Nzxt : null;
        if (vendor == 0x046A && product is 0x01E4 or 0x01E2) return usagePage == 0xFF1C && usage == 0x0092 && outputLength >= 64 ? Protocol.Cherry : null;
        if (vendor == 0x35BB && product == 0xD00B) return usagePage == 0xFF1C && usage == 0x0092 && outputLength >= 64 ? Protocol.Hecate : null;
        if (Asus.Find(vendor, product) is { } asus) return Asus.Collection(asus, iface, collection, usagePage, outputLength) ? Protocol.Asus : null;
        if (SteelSeriesMice.Find(vendor, product) is { } mouse) return SteelSeriesMice.Collection(mouse, iface, usagePage, outputLength) ? Protocol.SteelSeries : null;
        if (vendor == RapooVendor && RapooGen1Products.Contains(product)) return usagePage >= 0xFF00 && outputLength >= 33 ? Protocol.RapooGen1 : null;
        if (vendor == AtkVendor && usagePage == 0xFF05) return Protocol.BitMouse;
        // ATK's N9 Pro and N9 Ultra: their consumer collection (the one ATK HUB lists them by) only finds the device; nothing is
        // sent to it, the battery being read on the device's serial port (AtkHeadsets.SerialRequest).
        if (vendor == AtkVendor && AtkHeadsets.SerialProducts.Any(p => p.Product == product))
            return usagePage == 0x000C && usage == 0x0001 ? Protocol.AtkSerial : null;
        if (CompxVerified.Contains((vendor, product, usagePage))) return Protocol.Compx;
        if (vendor == VgnVendor && usagePage == 0xFF1C && product == 0x5088) return Protocol.Vgn;
        // ATK's keyboards on the same page, answered with VGN's frame (AtkKeyboards): the measured Z87 Pro and the receivers ATK
        // HUB lists, and only their keyboard collection.
        if (vendor == AtkVendor && AtkKeyboard(product) is not null) return usagePage == 0xFF1C && usage == 0x0092 && outputLength >= 64 ? Protocol.Vgn : null;
        if (vendor == EndgameVendor && usagePage == 0xFF01) return Protocol.Endgame;
        if (vendor == VaxeeVendor && usagePage == 0xFF05) return Protocol.Vaxee;
        if (vendor == 0x3710 && PulsarVaxeeProducts.Contains(product)) return usagePage == 0xFF05 && featureLength >= 6 ? Protocol.Vaxee : null;
        if (vendor == InphicVendor && product is 0x5B2E or 0x5B2F && featureLength >= 33) return Protocol.Inphic;
        // The 2.4 GHz receiver's product is not fixed (InphicBatteryTray): only its vendor collection.
        if (vendor == InphicWireless && usagePage >= 0xFF00 && featureLength >= 33) return Protocol.Inphic;
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
    // Buds: earbuds with a case, the left at 6, the right at 7 and the case at 8, the level the lower bud's, charging any of
    // the low two bits of 5 in the 12 08 answer (the Cetra SpeedNova, G-Helper's CetraSpeedNova). Interface -1 on a keyboard:
    // G-Helper takes its 0xFF00 collection with a 64-byte output report, so any vendor collection is asked.
    public sealed record Model(int Product, string Name, Family Family, int Interface, byte ReportId = 0, bool Steps = false, int LevelAt = 5, int Collection = -1,
        bool Buds = false);
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
        // HaloBattery's asus.py (MIT): the TX Gaming Mouse Mini Miku on the mice's frame; not measured by us.
        new(0x1C5A, "TX Gaming Mouse Mini Miku", Family.Mouse, 0),
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
        // G-Helper's StrixScopeRXTKLWireless, FalchionRX (the percent at 11, as the Falchion) and ClaymoreII, on the interface
        // it finds (any vendor collection here), and its CetraSpeedNova.
        new(0x1A07, "ROG Strix Scope RX TKL Wireless", Family.Keyboard, -1, LevelAt: 6), new(0x1B04, "ROG Falchion RX", Family.Keyboard, -1, LevelAt: 11),
        new(0x196B, "ROG Claymore II", Family.Keyboard, -1, LevelAt: 6),
        new(0x1AD3, "ROG Cetra SpeedNova", Family.Headset, -1, ReportId: 0xCC, LevelAt: 6, Buds: true),
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
        && (model.Family != Family.Headset || (model.Buds ? UsagePages.Is(usagePage, 0xFF00) : usagePage == 0xFF00))
        && (model.Family != Family.Keyboard || model.Interface >= 0 || UsagePages.Vendor(usagePage));

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
        if (model.Buds)
        {
            if (raw > 100) return Refused;
            return new BatteryRead(r[7] <= 100 ? Math.Min(raw, r[7]) : raw);
        }
        return model.Family switch
        {
            Family.Mouse when model.Steps => raw <= 4 ? new BatteryRead(raw * 25, r[10] != 0, Coarse: true) : Refused,
            Family.Mouse => raw <= 100 ? new BatteryRead(raw, r[10] != 0) : Refused,
            Family.Keyboard => raw <= 100 ? new BatteryRead(raw, r[9] == 1) : Refused,
            _ => raw <= 100 ? new BatteryRead(raw) : Refused
        };
    }
    public static bool? Charging(Model model, byte[] r) => model.Family == Family.Headset && r.Length >= 6 && r[0] == model.ReportId && r[1] == 0x12 && r[2] == 0x08
        ? model.Buds ? (r[5] & 0x03) != 0 : r[5] == 1 : null;
    // A Buds model's parts from its 12 07 answer: the left, the right and the case, each without a level when out of range.
    public static IReadOnlyList<PartBattery> Parts(Model model, byte[] r)
    {
        if (!model.Buds || r.Length < 9 || r[0] != model.ReportId || r[1] != 0x12 || r[2] != 0x07) return [];
        int? Level(byte value) => value <= 100 ? value : null;
        return [new(BatteryPart.Left, Level(r[6]), false), new(BatteryPart.Right, Level(r[7]), false), new(BatteryPart.Case, Level(r[8]), false)];
    }

    public static DeviceKind Kind(Model model) => model.Buds ? DeviceKind.Earbuds
        : model.Family switch { Family.Keyboard => DeviceKind.Keyboard, Family.Headset => DeviceKind.Headphones, _ => DeviceKind.Mouse };

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
// - Rapoo's own vendor id 0x24AE as a whole (its second generation pushes this one report and is only listened to, so no
//   model list is needed): the listed products on 0xFF00 / usage 2 as before, and any other on the collection vigil-watt
//   takes for it (measured on Windows): 0xFF00 as its usage page or its usage (Rapoo puts it in either), 19-byte input
//   reports. Not the first generation (RapooGen1Products, asked with 0xBA).
// - Rapoo V700DIY-98 keyboard (24AE:1924 on its receiver, 4824 on its cable): its receiver pushes BD 01 .. with the state at 7
//   (1 on battery, 2 charging) and the level at 8, which Rapoo's own device layer reads (HaloBattery's rapoo.py, captured on
//   the reporter's keyboard); report 0x07 is taken on it as well, as that layer does. Its vendor collections are listened to.
public static class Pushes
{
    public enum Kind { Beken, Delux, InphicZhen, Rapoo, AulaG9, SteamPuck, Jbl, Pulse3D, CloudFlight2, RapooKeyboard }
    // The listed ones also on a longer report; then VT0 Air Max (14A1, measured by pedro3z0) and VT3 PRO V2 (14A5) from
    // vigil-watt's rapoo.md, VT7 (1413 receiver, 4613 cable, measured) and VT9 Pro V2 (14A9) from HaloBattery's rapoo.py, and
    // VT9S V2 (1453, MouseDock's profile verified upstream). (The VT2 MAX's report 7 is 13 bytes, without the link byte at 18.)
    public static readonly int[] RapooProducts = [0x1412, 0x4612, 0x1B13, 0x4B13, 0x1460, 0x4660, 0x1406, 0x4606, 0x1410, 0x1411, 0x1417,
        0x14A1, 0x14A5, 0x1413, 0x4613, 0x14A9, 0x1453];
    public static readonly int[] RapooKeyboards = [0x1924, 0x4824];
    // The collection Rapoo's status report comes on, for a product of its vendor id.
    public static bool RapooStatus(int product, int usagePage, int usage, int inputLength)
        => !Dongles.RapooGen1Products.Contains(product)
        && (RapooProducts.Contains(product) && usagePage == 0xFF00 && usage == 0x0002 && inputLength >= 19
            || (UsagePages.Is(usagePage, 0xFF00) || usage == 0xFF00) && inputLength == 19);
    public static Kind? Find(int vendor, int product, int usagePage, int usage, int iface, int inputLength) => (vendor, product) switch
    {
        (0x1D57, 0xFA60 or 0xFA62) when iface == 2 && usagePage == 0x000A => Kind.Beken,
        (0x1D57, 0xFA65) when usagePage == 0x000A => Kind.Delux,
        (0x1D57, 0xFA65) when usagePage == 0xFF00 && usage == 0x0001 => Kind.InphicZhen,
        (0x24AE, _) when RapooKeyboards.Contains(product) && (UsagePages.Vendor(usagePage) || usage == 0xFF00) && inputLength >= 9 => Kind.RapooKeyboard,
        (0x24AE, _) when RapooStatus(product, usagePage, usage, inputLength) => Kind.Rapoo,
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
            case Kind.Rapoo or Kind.RapooKeyboard when r.Length >= 19 && r[0] == 0x07:
                if ((r[1] & 0x0F) == 0 && (r[18] & 0x80) == 0) return (last ?? new(null)) with { Charging = false, Asleep = true };
                return r[8] is >= 2 and <= 100 ? new(r[8], r[7] == 0x02 && r[8] < 100) : null;
            case Kind.RapooKeyboard when r.Length >= 9 && r[0] == 0xBD && r[1] == 0x01 && r[7] is 1 or 2 && r[8] <= 100:
                return new(r[8], r[7] == 2);
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
        Kind.Jbl => "JBL Quantum 910 Wireless", Kind.Pulse3D => "PULSE 3D Wireless Headset", Kind.CloudFlight2 => "HyperX Cloud Flight 2",
        Kind.RapooKeyboard => "Rapoo V700DIY-98", _ => "Steam Controller"
    };
    // Whether a report names the device (a Beken report carries its mouse's id).
    public static bool Names(Kind kind) => kind == Kind.Beken;
    public static DeviceKind KindOf(Kind kind) => kind switch
    {
        Kind.AulaG9 or Kind.Jbl or Kind.Pulse3D or Kind.CloudFlight2 => DeviceKind.Headphones, Kind.SteamPuck => DeviceKind.Controller,
        Kind.RapooKeyboard => DeviceKind.Keyboard, _ => DeviceKind.Mouse
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
        // HaloBattery's corsair.py: the K65 PLUS Wireless's receiver (route 9 on the report-id-0 frame, as the Dark Core; 100 %
        // read on the reporter's keyboard, #154) and the M75 Wireless's on the same exchange; the Void v2 Wireless, Virtuoso Max
        // and HS80 Max receivers on HeadsetControl's corsair_void_v2w exchange (02 09 02 0F bare, the level at 4 and 5 in
        // tenths after the firmware and heartbeat gets; the Void v2 measured, #28).
        new(0x2B07, "Corsair K65 PLUS Wireless", DeviceKind.Keyboard, false), new(0x1BDC, "Corsair M75 Wireless", DeviceKind.Mouse, false),
        new(0x2A08, "Corsair Void v2 Wireless", DeviceKind.Headphones, true, Session: true), new(0x2A02, "Corsair Virtuoso Max Wireless", DeviceKind.Headphones, true, Session: true),
        new(0x0A97, "Corsair HS80 Max Wireless", DeviceKind.Headphones, true, Session: true),
        // Not measured by us. Corsair's other SLIPSTREAM receivers, which OpenLinkHub (GPL; facts only) and ckb-next (GPL) read
        // the same way as 1B7F, route 9 on the report-id-0 frame: the general receivers 1BA6 and 2B00 (any Slipstream device may
        // be paired, so the kind is not known), the Harpoon's 1B65, the Dark Core RGB Pro's 1B81, the Ironclaw's 1B66 and the
        // K57's 1B62; the receiver's own name is shown where it has one. The first Virtuoso RGB Wireless's receivers (0A42,
        // 0A44), as the Virtuoso SE; the headset dongle 0A73 OpenLinkHub reads for the HS80 RGB and the Virtuoso alike; the
        // HS80 RGB Wireless's receiver 0A6B and cable 0A69, on the
        // same frame (HaloBattery's corsair.py, MIT, after ST1CKEL/HS80_Control's measurement); and the Virtuoso Max, HS80 Max
        // and Void Max ids OpenLinkHub reads with the Void v2 family (2A03, 2A0F, 2A0C, 2A05, 2A12).
        new(0x1BA6, "Corsair Slipstream Wireless", DeviceKind.Other, false), new(0x2B00, "Corsair Slipstream Wireless", DeviceKind.Other, false),
        new(0x1B65, "Corsair Harpoon RGB Wireless", DeviceKind.Mouse, false), new(0x1B81, "Corsair Dark Core RGB Pro", DeviceKind.Mouse, false),
        new(0x1B66, "Corsair Ironclaw RGB Wireless", DeviceKind.Mouse, false), new(0x1B62, "Corsair K57 RGB Wireless", DeviceKind.Keyboard, false),
        new(0x0A42, "Corsair Virtuoso RGB Wireless", DeviceKind.Headphones, true), new(0x0A44, "Corsair Virtuoso RGB Wireless", DeviceKind.Headphones, true),
        new(0x0A73, "Corsair Wireless Headset", DeviceKind.Headphones, true),
        new(0x0A6B, "Corsair HS80 RGB Wireless", DeviceKind.Headphones, true),
        new(0x0A69, "Corsair HS80 RGB Wireless", DeviceKind.Headphones, true, Cable: true, Receiver: 0x0A6B),
        .. new[] { 0x2A03, 0x2A0F, 0x2A0C, 0x2A05, 0x2A12 }.Select(product => new Model(product, "Corsair Wireless Headset", DeviceKind.Headphones, true, Session: true)),
    ];
    public static Model? Find(int vendor, int product) => vendor == Vendor ? Catalog.FirstOrDefault(model => model.Product == product) : null;
    // The charge state (0x10) is asked only of the Virtuoso SE and XT (and the first Virtuoso RGB Wireless's receivers, the
    // same headset family), as corsair.py limits it; the Void v2 family's answer has no charging flag there and HeadsetControl
    // does not ask it, so those are not asked something no source has seen. A cable model not asked (the HS80 RGB's) is
    // charging while its level is read: corsair.py takes it so, the protocol carrying no flag.
    public static bool AsksCharging(Model model) => model.Bare && model.Product is 0x0A40 or 0x0A3E or 0x0A3D or 0x0A64 or 0x0A62 or 0x0A42 or 0x0A44;
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
    // route: the route the question went to. An answer through the receiver's own route (8) is taken only for a question asked
    // there: anything else answered by the receiver itself (its firmware, its own properties) is not the device's.
    public static int? Value(byte[] reply, byte route = RouteDevice)
    {
        var r = reply.Length >= 65 && reply[0] == 0x00 ? reply[1..] : reply;
        if (r.Length < 5 || r[0] != 0x01) return null;
        if (r[1] == 0x02 && r[2] == 0x00) return r[3] | r[4] << 8;
        if (r.Length >= 6 && r[1] == 0x01 && r[2] == 0x02 && r[3] == 0x00) return r[4] | r[5] << 8;
        // The answer through the receiver's own route (8, a headset on its cable): 01 00 02 00 <low> <high>, the route less 8 at
        // 1 (HaloBattery's corsair.py, from the HS80's measurement, the only place it takes it).
        if (route == RouteReceiver && r.Length >= 6 && r[1] == 0x00 && r[2] == 0x02 && r[3] == 0x00) return r[4] | r[5] << 8;
        return null;
    }
    public static int? Percent(byte[] reply, byte route = RouteDevice) => Value(reply, route) is int value and > 0 and <= 1000 ? value / 10 : null;
    public static bool? Charging(byte[] reply, byte route = RouteDevice) => Value(reply, route) switch { 1 => true, 2 => false, _ => null };
    // How long an answer may take: the first ask, then the confirming one (the device is awake by then), and the whole
    // reading of one receiver.
    public const int AskMs = 6000, ConfirmMs = 3000, BudgetMs = 7000;
}

// Wireless headsets on their own dongles. Each is a request on an interface and a reply with the level.
public static class Headsets
{
    public const int SteelSeries = 0x1038, HyperXHp = 0x03F0, HyperXKingston = 0x0951;
    public enum Kind { Arctis7, Arctis9, Nova7Percent, Nova7Steps, NovaPro, CloudAlpha, CloudII, CloudFlight, CloudIII, CloudIIIS, CloudAlpha2, CloudIIKingston }

    // Which headset a vendor and product are, and the interface (or usage page) its battery lives on.
    public static Kind? Find(int vendor, int product) => (vendor, product) switch
    {
        (SteelSeries, 0x1260 or 0x12AD or 0x1252 or 0x1280) => Kind.Arctis7,
        (SteelSeries, 0x12C2 or 0x12C4) => Kind.Arctis9,
        // Nova 7P uses the same status request as the 7/7X: 220A reports steps; 22A7 and 2298 report percent.
        // Product IDs and scales: HeadsetControl lib/devices/steelseries_arctis_nova_7p.hpp (protocol facts only).
        (SteelSeries, 0x22A1 or 0x227E or 0x2258 or 0x229E or 0x22AD or 0x22A9 or 0x22A5 or 0x22A7 or 0x2298) => Kind.Nova7Percent,
        (SteelSeries, 0x2202 or 0x2206 or 0x223A or 0x227A or 0x22A4 or 0x220A) => Kind.Nova7Steps,
        (SteelSeries, 0x12E0 or 0x12E5) => Kind.NovaPro,
        (HyperXHp, 0x098D) => Kind.CloudAlpha,
        (HyperXHp, 0x0696 or 0x018B) => Kind.CloudII,
        (HyperXKingston, 0x16C4 or 0x1723) => Kind.CloudFlight,
        // The Kingston-branded Cloud II Wireless (HaloBattery's hyperx.py, from HeadsetControl's and HyperHeadset's notes; read
        // on the reporter's dongle, #192): its own 62-byte exchange on 0xFF13, answered under report id 0x0B.
        (HyperXKingston, 0x1718) => Kind.CloudIIKingston,
        // HaloBattery's hyperx_cloud3.py (the Cloud III Wireless, and the Cloud II Core Wireless on 0995 and 0795 from a
        // capture of NGENUITY), hyperx_cloud3s.py and hyperx_alpha2.py (captures of NGENUITY).
        (HyperXHp, 0x05B7 or 0x0C9D or 0x0995 or 0x0795) => Kind.CloudIII,
        (HyperXHp, 0x02CC or 0x06BE) => Kind.CloudIIIS,
        (HyperXHp, 0x08BE) => Kind.CloudAlpha2,
        _ => null
    };
    // The interface (MI_xx) to talk on, or the usage page when that is what tells it apart; -1 for either.
    // VendorPages: any vendor-defined usage page (0xFF00 and up), so a request never goes to the headset's consumer or audio
    // control collections.
    public const int VendorPages = -2;
    public static (int Interface, int UsagePage) Channel(Kind kind) => kind switch
    {
        Kind.Arctis7 => (5, -1), Kind.Arctis9 => (0, -1), Kind.Nova7Percent or Kind.Nova7Steps => (-1, 0xFFC0), Kind.NovaPro => (4, -1),
        Kind.CloudII => (-1, 0xFF90), Kind.CloudIII or Kind.CloudAlpha2 or Kind.CloudIIKingston => (-1, 0xFF13), Kind.CloudIIIS => (3, VendorPages),
        _ => (-1, VendorPages)
    };
    // The Cloud III S answers on whichever collection of its interface declares the reply's report, not always the one asked:
    // its siblings on that interface are read too.
    public static bool ReadsSiblings(Kind kind) => kind == Kind.CloudIIIS;
    // Some Cloud III dongles take the request only as a feature report: Windows refuses the output report with "Incorrect
    // function", and the same bytes go as a feature report (HyperHeadset's fallback, as HaloBattery has it).
    public static bool FeatureFallback(Kind kind) => kind == Kind.CloudIII;
    // How long a reply may take, in milliseconds.
    public static int Wait(Kind kind) => kind switch { Kind.CloudAlpha2 => 1500, Kind.CloudIII or Kind.CloudIIIS or Kind.CloudIIKingston => 1000, _ => 600 };

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
        Kind.CloudIII => Padded([0x66, 0x89], 62),
        // 01 at 3 asks for a value; 00 would set one, which is never sent.
        Kind.CloudIIIS => Padded([0x0C, 0x02, 0x03, 0x01, 0x00, 0x06], 64),
        Kind.CloudAlpha2 => Padded([0x50, 0x02], 64),
        Kind.CloudIIKingston => KingstonRequest(0x02),
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
        Kind.CloudIII => Padded([0x66, 0x8A], 62), Kind.CloudIIIS => Padded([0x0C, 0x02, 0x03, 0x01, 0x00, 0x48], 64),
        Kind.CloudIIKingston => KingstonRequest(0x03), _ => null
    };
    // 06 00 02 00 9A 00 00 68 4A 8E 0A 00 00 00 BB <command>: 02 the level, 03 the charging state.
    private static byte[] KingstonRequest(byte command) => Padded([0x06, 0x00, 0x02, 0x00, 0x9A, 0x00, 0x00, 0x68, 0x4A, 0x8E, 0x0A, 0x00, 0x00, 0x00, 0xBB, command], 62);
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
        // 66 89 (or its response id 0D): the percent at 4, unless 2 and 3 are both 0 (no reading yet).
        Kind.CloudIII when r.Length >= 5 && r[0] == 0x66 && r[1] is 0x89 or 0x0D && (r[2] != 0 || r[3] != 0) && r[4] <= 100 => new(r[4]),
        // 0C .. .. .. .. 06 <percent> (0xFF: no value), or the headset's own notice 0D with 1 at 4 and the percent at 5.
        Kind.CloudIIIS when r.Length >= 7 && r[0] == 0x0C && r[5] == 0x06 && r[6] <= 100 => new(r[6]),
        Kind.CloudIIIS when r.Length >= 6 && r[0] == 0x0D && r[4] == 0x01 && r[5] <= 100 => new(r[5]),
        // 51 02 <percent> .. .. .. <flags>, bit 7 of the flags while charging.
        Kind.CloudAlpha2 when r.Length >= 7 && r[0] == 0x51 && r[1] == 0x02 && r[2] <= 100 => new(r[2], (r[6] & 0x80) != 0),
        // 0B .. BB 02 .. .. .. <percent>.
        Kind.CloudIIKingston when r.Length >= 8 && r[0] == 0x0B && r[2] == 0xBB && r[3] == 0x02 && r[7] <= 100 => new(r[7]),
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
        // 0B .. BB 03 <state>: 0 not charging, 1 charging, 2 full (on its cable either way).
        Kind.CloudIIKingston when r.Length >= 5 && r[0] == 0x0B && r[2] == 0xBB && r[3] == 0x03 && r[4] <= 2 => r[4] != 0,
        _ => null
    };

    public static string Name(Kind kind) => kind switch
    {
        Kind.Arctis7 => "SteelSeries Arctis 7", Kind.Arctis9 => "SteelSeries Arctis 9", Kind.Nova7Percent or Kind.Nova7Steps => "SteelSeries Arctis Nova 7",
        Kind.NovaPro => "SteelSeries Arctis Nova Pro Wireless", Kind.CloudAlpha => "HyperX Cloud Alpha Wireless", Kind.CloudII or Kind.CloudIIKingston => "HyperX Cloud II Wireless",
        Kind.CloudFlight => "HyperX Cloud Flight", Kind.CloudIII => "HyperX Cloud III Wireless", Kind.CloudIIIS => "HyperX Cloud III S Wireless",
        Kind.CloudAlpha2 => "HyperX Cloud Alpha 2", _ => ""
    };
    public static DeviceSource Source(Kind kind) => kind switch
    {
        Kind.CloudAlpha or Kind.CloudII or Kind.CloudFlight or Kind.CloudIII or Kind.CloudIIIS or Kind.CloudAlpha2 or Kind.CloudIIKingston => DeviceSource.HyperX,
        _ => DeviceSource.SteelSeries
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

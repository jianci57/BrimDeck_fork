namespace BrimDeck.Core;

// Reads from ATK HUB (bpcdn.atkgear.com/hub-v3) and VGN HUB (hub.vgnlab.com), the brands' own web drivers, for the devices
// their other readers here do not cover. None is measured by us; each goes only to the collection the driver opens and only
// where it declares the report, and an answer counts only with its command echoed.
// - HFD keyboards (ATK A87 PRO, A98, A98 Pro, A87 Ultra; receivers 373B:111F, 114E, 1152, cables 111E, 114D, 1151, 1231): VIA's
//   raw collection (usage page 0xFF60, usage 0x61, report 0): id_get_keyboard_value 02 with the custom id 0x96 and the battery
//   value 0x24, answered 02 96 24 <level> <charging, 0 on battery>. A firmware that echoes the request gives a level of 0,
//   which is no reading.
// - Bytech keyboards (ATK A87 Ultra, A98 Ultra, V75X Ultra, RS8 Neo's receivers 373B:1242, 1244, 124B, 1255; VGN V98Pro V4,
//   V98Pro V4 TIMI, V87Pro V2 and V108Pro's 391D:2A03, 2A04, 2A0A, 2A06, 2A08), on 0xFF60 / 0x61 with 63-byte packets: command
//   0x87 (getBatteryStatus) as 87 00 00 01 00 00, zeros, and at 62 the check 0xFF less (the report id plus the sum of the
//   packet) mod 256: the library takes the seed from the first output report id the collection declares, and refuses a
//   collection with none (report 0), so only a collection declaring a nonzero output report is asked, with that id. The
//   answer: 87, then after the six header bytes the level and a state whose high nibble is charging.
// - ATK's Nearlink mice on the Evision firmware (X1 Nearlink 373B:108B and its receiver 108C, F1 Nearlink 1156, receiver
//   1179, A9 Ultimate Nearlink 11B5), usage page 0xFFB2, usage 1: the power_info command 0x1A that the Evision keyboards
//   answer (<report> 00 00 1A, the driver's frame without the keyboards' 06), on report 5 where the collection declares it
//   (wireless) and report 4 otherwise (the cable), never both; the answer echoes 1A at 3, the level at 8 and a nonzero 9
//   while charging.
// - ATK A9 PRO MAX (cable 373B:1147, receivers 1148 and 1196) on its HECHI firmware, usage page 0xFFA0, usage 2: output report
//   0x0B with command 0x13 (mouse info); the answer 0B 13 <length> <result>, then at 17 the charging state, at 18 the level,
//   at 20 1 while the mouse is linked to the receiver (asked of the receivers only: the driver does not use it, and the mouse
//   on its cable has no link). A level over 100 is no reading.
// - ATK YOGO 75 and YOGO 75 PRO on their Evision V2 firmware (receivers 373B:1200 and 11FF, cables 119C and 119B), VIA's raw
//   collection (0xFF60 / 0x61, report 0, 32 bytes on the receivers, 64 on the cables): frames AA <command> 00 00 00 <sequence,
//   low byte first> 00, the answer AA .. <result, FF an error> .. .. <sequence> .. with its data from 8. The driver reads the
//   power with "start" (0x10) first and then power_info (0x30), and sends no "end" after it: a start the keyboard is left in
//   is how ATK HUB leaves it every time it reads, so the read is sent the same way, start first. The answer is the one with
//   power_info's sequence: the level at 8 (the driver clamps it to 100; over 100 is no reading here) and 2 at 9 charging.
[BrandFamilies]
public static class AtkOfficial
{
    public const int AtkVendor = 0x373B, VgnVendor = 0x391D;

    public static readonly int[] HfdProducts = [0x111F, 0x114E, 0x1152, 0x111E, 0x114D, 0x1151, 0x1231];
    public static byte[] HfdRequest() => BrandFamilies.Frame(33, 0x00, 0x02, 0x96, 0x24);
    // The answer with its report id 0 in front.
    public static BatteryRead? HfdBattery(byte[] r)
        => r.Length >= 6 && r[0] == 0x00 && r[1] == 0x02 && r[2] == 0x96 && r[3] == 0x24 && r[4] is > 0 and <= 100 ? new(r[4], r[5] != 0) : null;

    public static readonly (int Vendor, int Product)[] BytechReceivers =
        [(AtkVendor, 0x1242), (AtkVendor, 0x1244), (AtkVendor, 0x124B), (AtkVendor, 0x1255),
         (VgnVendor, 0x2A03), (VgnVendor, 0x2A04), (VgnVendor, 0x2A0A), (VgnVendor, 0x2A06), (VgnVendor, 0x2A08)];
    public static byte[] BytechRequest(byte reportId, byte seed)
    {
        var frame = BrandFamilies.Frame(64, reportId, 0x87, 0x00, 0x00, 0x01, 0x00, 0x00);
        int sum = seed; for (int i = 1; i < 63; i++) sum += frame[i];
        frame[63] = (byte)(0xFF - sum % 256);
        return frame;
    }
    public static BatteryRead? BytechBattery(byte[] r)
        => r.Length >= 9 && r[1] == 0x87 && r[7] is > 0 and <= 100 ? new(r[7], (r[8] & 0xF0) != 0) : null;

    public static readonly int[] NearlinkProducts = [0x108B, 0x108C, 0x1156, 0x1179, 0x11B5];
    public static byte[] NearlinkRequest(byte reportId) => BrandFamilies.Frame(64, reportId, 0x00, 0x00, 0x1A);
    public static BatteryRead? NearlinkBattery(byte[] r)
        => r.Length >= 10 && r[0] is 0x04 or 0x05 && r[3] == 0x1A && r[8] is > 0 and <= 100 ? new(r[8], r[9] != 0) : null;

    public static readonly int[] YogoProducts = [0x1200, 0x11FF, 0x119C, 0x119B];
    public const byte YogoStart = 0x10, YogoPower = 0x30, YogoStartSequence = 0x01, YogoPowerSequence = 0x02;
    // One frame for a collection whose output report is this long (report id included).
    public static byte[] YogoFrame(byte command, byte sequence, int outputLength)
        => BrandFamilies.Frame(outputLength >= 65 ? 65 : 33, 0x00, 0xAA, command, 0x00, 0x00, 0x00, sequence, 0x00);
    // The answer without its zero report id.
    public static BatteryRead? YogoBattery(byte[] r)
        => r.Length >= 10 && r[0] == 0xAA && r[2] != 0xFF && r[5] == YogoPowerSequence && r[6] == 0x00 && r[8] is > 0 and <= 100
            ? new(r[8], r[9] == 2) : null;

    public static readonly int[] HechiProducts = [0x1147, 0x1148, 0x1196];
    public static byte[] HechiRequest() => BrandFamilies.Frame(64, 0x0B, 0x13);
    public static BatteryRead? HechiBattery(byte[] r) => Hechi(r, receiver: true);
    public static BatteryRead? HechiCableBattery(byte[] r) => Hechi(r, receiver: false);
    private static BatteryRead? Hechi(byte[] r, bool receiver)
    {
        // The request read back has a length of 0; an answer carries its data's.
        if (r.Length < 21 || r[0] != 0x0B || r[1] != 0x13 || r[2] == 0 || r[3] == 0xFF) return null;
        if (receiver && r[20] != 1) return new(null, Asleep: true);
        return r[18] is > 0 and <= 100 ? new(r[18], r[17] == 1) : null;
    }

    private static bool Hfd(HidShape s) => s.Vendor == AtkVendor && HfdProducts.Contains(s.Product) && MouseShapes.Collection(s, 0xFF60, 0x61) && s.OutputLength >= 6;
    private static bool Bytech(HidShape s) => BytechReceivers.Contains((s.Vendor, s.Product)) && MouseShapes.Collection(s, 0xFF60, 0x61) && s.OutputLength >= 64;
    private static bool Nearlink(HidShape s) => s.Vendor == AtkVendor && NearlinkProducts.Contains(s.Product) && MouseShapes.Collection(s, 0xFFB2, 0x01) && s.OutputLength >= 10;
    private static bool Yogo(HidShape s) => s.Vendor == AtkVendor && YogoProducts.Contains(s.Product) && MouseShapes.Collection(s, 0xFF60, 0x61)
        && s.OutputLength is 33 or 65 && s.InputLength >= 11;
    private static bool Hechi(HidShape s) => s.Vendor == AtkVendor && HechiProducts.Contains(s.Product) && MouseShapes.Collection(s, 0xFFA0, 0x02) && s.OutputLength >= 21;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("ATK HFD keyboard", Hfd, _ => [new BrandWay("atk.hfd", WayTransport.Output, HfdRequest(), HfdBattery) { TimeoutMs = 600 }])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "atkkb" },
        new("Bytech keyboard", Bytech, s => s.OutputIds.Where(id => id != 0).Order().Take(1)
            .Select(id => new BrandWay("bytech.battery", WayTransport.Output, BytechRequest(id, id), BytechBattery) { TimeoutMs = 600 }).ToList())
        { Kind = _ => DeviceKind.Keyboard, Prefix = "bytech" },
        new("ATK Nearlink mouse", Nearlink, s => s.OutputIds.Contains(0x05)
            ? [new BrandWay("atk.nearlink", WayTransport.Output, NearlinkRequest(0x05), NearlinkBattery) { TimeoutMs = 600 }]
            : [new BrandWay("atk.nearlink-cable", WayTransport.Output, NearlinkRequest(0x04), NearlinkBattery) { TimeoutMs = 600 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "atk" },
        new("ATK Evision V2 keyboard", Yogo, s =>
        [
            new BrandWay("atk.evision-v2", WayTransport.Output, YogoFrame(YogoPower, YogoPowerSequence, s.OutputLength), YogoBattery)
            {
                Before = [YogoFrame(YogoStart, YogoStartSequence, s.OutputLength)], GapMs = 50, TimeoutMs = 1500, StripZero = true,
            },
        ])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "atkkb" },
        new("ATK HECHI mouse", Hechi, s => [new BrandWay("atk.hechi", WayTransport.Output, HechiRequest(), s.Product == 0x1147 ? HechiCableBattery : HechiBattery) { TimeoutMs = 800 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "atk" },
    ];
}

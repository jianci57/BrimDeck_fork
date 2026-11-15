namespace BrimDeck.Core;

// EWEADN (前行者) mice, and the AULA SC580 on the same receiver.
// - The receiver 089D:062F (the EWEADN S9, device id M156, and H2, M302; the AULA SC580, M137; the EWEADN X23, M230) and
//   the mouse on its cable 088D:062E. The device-info command 0x30 as a 33-byte output report (report id 0), answered on
//   the input report (report id 0, so read without it): 30 <status> 01 0B <device id, 4 ASCII> <firmware, 2> <ic type>
//   <report max> <charging 0/1> <percent> <online 0/1>. Status 02 is the receiver answering for a mouse asleep (with no
//   payload; MouseDock saw 30 02 01), and so is online 0. On its cable the mouse charges: HaloBattery reports it charging
//   whatever the flag says. Two collections take it, each with its own request as its source sent it: 0xFF01 usage 0x10,
//   30 then zeros and the checksum (the sum of bytes 5 to 31 in the last byte: 0 here), from EWEADN's own web HUB as
//   HaloBattery's eweadn.py (MIT; measured on an S9, its answer 30 00 01 0B 4D 31 35 36 .. 46 01: M156, 70 %, and the
//   asleep answer 30 02) has it; and 0xFF02 usage 0 with 33-byte input and output reports, 30 00 01 then zeros, as
//   MouseDock's AulaBatteryProtocol (MIT; measured on an SC580 receiver, M137 at 82 %) sends it. The device id is not used
//   to name the mouse (one read answers for any of them, and the product string names it); the read only requires four
//   printable characters there, which neither the request read back nor a stray frame carries.
// - The S9 on the beiyin receiver 372E:1026 (shared by the S9 Pro, Max and Ultra), usage page 0xFF00 usage 1, which has
//   only feature report 0x03 (63 bytes): set 03 <checksum> 50 00 02 4F 81 (the checksum the sum of the 62 bytes after it),
//   read back 03 50 00 0B 02 01 <state> ...: the percent in the low 7 bits of [6], bit 7 charging. A state of 0 is the
//   dongle answering for a sleeping mouse (a real 0 % does not exist; the mouse switches off first). From vigil-watt's
//   EweadnMouseCodec and its notes (MIT; from EWEADN's web HUB, its answer 03 50 00 0B 02 01 49 ..: 73 %, reported by a
//   contributor). The same frame goes to the S7 and S9 series' other beiyin receivers, 1027, 1029, 103D, 105E, 106E, 107B,
//   107C, 108A and 108D, which EWEADN's web driver (its beiying SDK) speaks to the same way; not measured by us. The SDK's
//   keyboards on 372E (ES98, X98, Alpha 87) need a per-model key first and are not read.
[BrandFamilies]
public static class EweadnMice
{
    public const int ReceiverVendor = 0x089D, Receiver = 0x062F, CableVendor = 0x088D, Cable = 0x062E, BeiyinVendor = 0x372E, BeiyinReceiver = 0x1026;

    // HaloBattery's request on 0xFF01 / 0x10 and MouseDock's on 0xFF02 / 0, report id 0 first (33 bytes).
    public static byte[] InfoRequest() => BrandFamilies.Frame(33, 0x00, 0x30);
    public static byte[] IdentifyRequest() => BrandFamilies.Frame(33, 0x00, 0x30, 0x00, 0x01);
    // The answer without its report id (StripZero).
    public static BatteryRead? Info(byte[] r)
    {
        if (r.Length < 15 || r[0] != 0x30) return null;
        if (r[1] == 0x02) return new(null, Asleep: true);
        if (r[1] != 0x00 || r[2] != 0x01 || r[3] != 0x0B || r[12] > 1 || r[14] > 1) return null;
        for (int i = 4; i < 8; i++) if (r[i] is < 0x20 or > 0x7E) return null;
        if (r[14] == 0) return new(null, Asleep: true);
        return r[13] is > 0 and <= 100 ? new(r[13], r[12] == 1) : null;
    }
    // The mouse on its cable: charging.
    public static BatteryRead? CableInfo(byte[] r) => Info(r) is { Percent: not null } read ? read with { Charging = true } : Info(r);

    public static byte[] BeiyinRequest()
    {
        var frame = BrandFamilies.Frame(64, 0x03, 0x00, 0x50, 0x00, 0x02, 0x4F, 0x81);
        int sum = 0;
        for (int i = 2; i < frame.Length; i++) sum += frame[i];
        frame[1] = (byte)sum;
        return frame;
    }
    public static BatteryRead? Beiyin(byte[] r)
    {
        // 0B 02 01 after the command's echo: the BASICS answer (MCHOSE's driver checks the 02; another command's answer, such as
        // the wireless-online one another program asks, is not this).
        if (r.Length < 7 || r[0] != 0x03 || r[1] != 0x50 || r[2] != 0x00 || r[3] != 0x0B || r[4] != 0x02 || r[5] != 0x01) return null;
        int level = r[6] & 0x7F;
        if (r[6] == 0) return new(null, Asleep: true);
        return level is > 0 and <= 100 ? new(level, (r[6] & 0x80) != 0) : null;
    }

    private static bool Hub(HidShape shape) => shape.Vendor == ReceiverVendor && shape.Product == Receiver || shape.Vendor == CableVendor && shape.Product == Cable;
    private static bool InfoCollection(HidShape shape) => MouseShapes.Collection(shape, 0xFF01, 0x10) && shape.OutputLength >= 33 && shape.InputLength >= 16;
    private static bool IdentifyCollection(HidShape shape) => MouseShapes.Collection(shape, 0xFF02, 0x00) && shape.OutputLength == 33 && shape.InputLength == 33;
    public static readonly IReadOnlySet<int> BeiyinReceivers = new HashSet<int> { BeiyinReceiver, 0x1027, 0x1029, 0x103D, 0x105E, 0x106E, 0x107B, 0x107C, 0x108A, 0x108D };
    // The beiyin read's collection: usage page 0xFF00, usage 1, with the 63-byte feature report 3.
    public static bool BeiyinShape(HidShape shape) => MouseShapes.Collection(shape, 0xFF00, 0x01) && shape.FeatureLength >= 64;
    private static bool BeiyinCollection(HidShape shape) => shape.Vendor == BeiyinVendor && BeiyinReceivers.Contains(shape.Product) && BeiyinShape(shape);

    public static bool Takes(HidShape shape) => Hub(shape) && (InfoCollection(shape) || IdentifyCollection(shape)) || BeiyinCollection(shape);

    public static IReadOnlyList<BrandWay> Ways(HidShape shape)
    {
        if (BeiyinCollection(shape)) return [new BrandWay("eweadn.beiyin", WayTransport.Feature, BeiyinRequest(), Beiyin) { GapMs = 20, FeatureLength = 64 }];
        Func<byte[], BatteryRead?> parse = shape.Vendor == CableVendor ? CableInfo : Info;
        if (InfoCollection(shape)) return [new BrandWay("eweadn.info", WayTransport.Output, InfoRequest(), parse) { TimeoutMs = 600, StripZero = true }];
        return [new BrandWay("eweadn.identify", WayTransport.Output, IdentifyRequest(), parse) { TimeoutMs = 600, StripZero = true }];
    }

    public static IEnumerable<BrandFamily> Families =>
    [
        new("EWEADN mouse", Takes, Ways) { Kind = _ => DeviceKind.Mouse, Prefix = "eweadn" },
    ];
}

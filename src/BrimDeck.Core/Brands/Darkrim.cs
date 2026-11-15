namespace BrimDeck.Core;

// The DARKRIM SHADOW (寸渊影弑) headset's dongle, 3654:6901 (a Jieli design; the id's owner is not known, so only this product).
// The only collection that answers is interface 3's (usage page 0xFF02 usage 2, 65-byte reports numbered 0): the official
// client's GetBasicInfo, FE 00 10 00 02 00 00 03 00 09 01 00 E2 3C, is answered by notices FE <seq> 11 00 03 00 00 <payload
// length> 00 <payload> <CRC-16/CCITT-FALSE of the frame, big endian>; the battery is the notice whose payload is 01 <length>
// <value>, bit 7 of the value set while charging and the percent below it (one value, or left, right and case; all three
// 0 is the headset not linked to the dongle, as vigil-watt's codec reads it, so asleep). The
// interface reads the request back first (command 10), which is no reading. The dongle's answers have one reader: while
// DARKRIM.exe runs, the two would each take half and the dongle resets its link, so the brand stands aside for it. From
// vigil-watt's darkrim.md and DarkrimCodec.cs (MIT; measured: FE 00 11 00 03 00 00 03 00 01 01 5A 98 B4, 90 %).
[BrandFamilies]
public static class Darkrim
{
    public const int Vendor = 0x3654, Shadow = 0x6901;

    public static byte[] Request() => BrandFamilies.Frame(65, 0x00, 0xFE, 0x00, 0x10, 0x00, 0x02, 0x00, 0x00, 0x03, 0x00, 0x09, 0x01, 0x00, 0xE2, 0x3C);

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)(crc << 1 ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    // The reply with its report id 0 taken off (StripZero).
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 11 || r[0] != 0xFE || r[2] != 0x11) return null;
        int length = r[7], end = 9 + length;
        if (length < 3 || end + 2 > r.Length || Crc16(r.AsSpan(0, end)) != (r[end] << 8 | r[end + 1]) || r[9] != 0x01) return null;
        int values = Math.Min(r[10], length - 2);
        if (values < 1) return null;
        // One value, or the two buds (and the case, not shown): the lower bud of those with a percent from 1 to 100.
        var read = r.AsSpan(11, Math.Min(values, 2)).ToArray().Where(value => (value & 0x7F) is > 0 and <= 100).ToList();
        if (read.Count > 0) return new(read.Min(value => value & 0x7F), read.Any(value => (value & 0x80) != 0));
        return values > 1 && r.AsSpan(11, Math.Min(values, 3)).IndexOfAnyExcept((byte)0) < 0 ? new(null, Asleep: true) : null;
    }

    private static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == Shadow && HeadsetPages.Is(s, 0xFF02) && s.Usage == 0x0002
        && s.Interface is 3 or -1 && s.OutputLength >= 65 && s.InputLength >= 65;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("DARKRIM", Takes, _ => [new BrandWay("darkrim.basicinfo", WayTransport.Output, Request(), Battery) { TimeoutMs = 1000, StripZero = true }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "darkrim", StandAsideFor = ["DARKRIM.exe"] },
    ];
}

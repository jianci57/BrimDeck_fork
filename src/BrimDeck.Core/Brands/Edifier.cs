namespace BrimDeck.Core;

// The EDIFIER HALO SPACE (花再 Halo Space) on its 2.4 GHz receiver, 2D99:E049 (only this product: who owns 0x2D99 is not
// confirmed). Its control collection is usage page 0xFF14 usage 1 (64-byte input and output, interface 4), output report
// 0x2E: 2E AA EC <command> <length, big endian> <payload XOR A5> <sum of bytes 1 to the payload's end>; the answer 2F BB EC
// <command> <length> <payload XOR A5> <its sum from byte 1>, the rest of the 64 bytes left over from older answers. The
// battery is command D0 with no payload, 2E AA EC D0 00 00 66, answered 2F BB EC D0 00 01 <percent XOR A5> <sum>; there is no
// charging state (the headset turns off on the charger in 2.4 GHz mode). From FerrisPulse's EDIFIER_HALO_SPACE_PROTOCOL.md
// (GPL; facts only; captured on the headset: 2F BB EC D0 00 01 86 FE, 35 %).
[BrandFamilies]
public static class Edifier
{
    public const int Vendor = 0x2D99, HaloSpace = 0xE049;

    public static byte[] Request() => BrandFamilies.Frame(7, 0x2E, 0xAA, 0xEC, 0xD0, 0x00, 0x00, 0x66);
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 8 || r[0] != 0x2F || r[1] != 0xBB || r[2] != 0xEC || r[3] != 0xD0 || r[4] != 0x00 || r[5] != 0x01) return null;
        int sum = 0;
        for (int i = 1; i <= 6; i++) sum += r[i];
        int percent = r[6] ^ 0xA5;
        return (byte)sum == r[7] && percent is > 0 and <= 100 ? new(percent) : null;
    }

    private static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == HaloSpace && HeadsetPages.Is(s, 0xFF14) && s.Usage == 0x0001
        && s.InputLength >= 64 && s.OutputLength >= 64;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("EDIFIER", Takes, _ => [new BrandWay("edifier.battery", WayTransport.Output, Request(), Battery) { TimeoutMs = 800 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "edifier" },
    ];
}

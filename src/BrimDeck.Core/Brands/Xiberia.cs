namespace BrimDeck.Core;

// XIBERIA (西伯利亚) headsets on their 2.4 GHz receivers, vendor 0x36F9 (the receivers' own id; XIBERIA HUB binds any
// receiver whose descriptor declares reports 0x55 and 0x41, not a product list, so every 36F9 vendor collection that declares
// 0x55, the command report, is asked).
// Output report 0x55, 64 bytes: 55 65 00 00 (0x65 the battery, 00 the device index); the answer 55 65 00 <percent>, no
// charging state. From Voltide's XiberiaT7Provider.cs (MIT; from XIBERIA HUB's ActHID.dll, checked on a T7, 36F9:F008). The
// query read back (55 65 00 00) is no reading.
[BrandFamilies]
public static class Xiberia
{
    public const int Vendor = 0x36F9;

    public static byte[] Request() => BrandFamilies.Frame(4, 0x55, 0x65, 0x00, 0x00);
    public static BatteryRead? Battery(byte[] r) => r.Length >= 4 && r[0] == 0x55 && r[1] == 0x65 && r[2] == 0x00 && r[3] is > 0 and <= 100 ? new(r[3]) : null;

    private static bool Takes(HidShape s) => s.Vendor == Vendor && HeadsetPages.Vendor(s) && s.InputLength >= 64 && s.OutputLength >= 64;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("XIBERIA", Takes, _ => [new BrandWay("xiberia.battery", WayTransport.Output, Request(), Battery) { TimeoutMs = 800 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "xiberia" },
    ];
}

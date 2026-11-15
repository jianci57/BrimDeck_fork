namespace BrimDeck.Core;

// The ASTRO A50 Gen 5 base station (046D:0B1C). 0x046D is Logitech's id, which Hidpp reads on usage page 0xFF00, so only this
// product and its own vendor collection (usage page 0xFF32 usage 0x74; interface 8): report 02, 64-byte interrupt frames
// 02 0C <length> 00 <command> <handle> <payload>, the length counting from byte 3. The battery GET is 02 0C 03 00 06 0C; the
// answer 02 0C 06 00 06 00 <percent> <level> <docked or charging>, and the base also sends it on its own when the state
// changes. HeadsetControl's logitech_astro_a50.hpp (GPL; facts only; verified on a base station) and HaloBattery's astro.py
// (MIT; a base station read on Windows on that page and usage, so the page is taken as is). The GET read back has a length of
// 3 and is no reading; no source says what the base answers with the headset off.
// The Gen 4 (9886:002C), which needs the link asked first, is AstroA50Gen4.
[BrandFamilies]
public static class AstroA50
{
    public const int Vendor = 0x046D, Gen5 = 0x0B1C;

    public static byte[] Request() => BrandFamilies.Frame(6, 0x02, 0x0C, 0x03, 0x00, 0x06, 0x0C);
    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 9 && r[0] == 0x02 && r[1] == 0x0C && r[2] > 3 && r[3] == 0x00 && r[4] == 0x06 && r[6] is > 0 and <= 100 ? new(r[6], r[8] != 0) : null;

    private static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == Gen5 && s.UsagePage == 0xFF32 && s.Usage == 0x0074 && s.OutputLength >= 64
        && s.InputLength >= 64;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("ASTRO", Takes, _ => [new BrandWay("astro.battery", WayTransport.Output, Request(), Battery) { TimeoutMs = 1000 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "astro" },
    ];
}

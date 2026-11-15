namespace BrimDeck.Core;

// The ASTRO A50 Gen 4 base station, 9886:002C (Astro Gaming's old id, which nothing else here reads; only this product). Its
// one vendor collection: interface 6, usage page 0xFF32 usage 0x74, report 02 in 64-byte frames, one reply per request and
// nothing sent unasked. A request is 02 <command>, zeros after; a reply 02 <status> <length> <payload>, status 02 an answer
// with a payload (00 accepted without one, 01 an error), the length not to be trusted. The reply does not name its command.
// The base keeps serving the last level after the headset is switched off, so the headset status (0x54) is asked first: bit 1
// of its payload is the live link to the headset (bit 0 docked); without it the headset is off. Then the battery (0x7C): bit
// 7 charging, bits 0 to 6 the percent. HeadsetControl's logitech_astro_a50_gen4.hpp (GPL; facts only; verified on Linux, the
// page and usage being the ones it gives for Windows) and tdryer/eh-fifty (MIT; the commands and the status bits). The page is
// taken in either byte order (no source read it on Windows). A zero percent is not taken.
[BrandFamilies]
public static class AstroA50Gen4
{
    public const int Vendor = 0x9886, Base = 0x002C;
    public const byte Status = 0x54, Battery = 0x7C;

    public static byte[] Request(byte command) => BrandFamilies.Frame(64, 0x02, command);

    private static bool Answer(byte[] r) => r.Length >= 4 && r[0] == 0x02 && r[1] == 0x02;
    public static bool? Linked(byte[] r) => Answer(r) ? (r[3] & 0x02) != 0 : null;
    public static BatteryRead? Level(byte[] r) => Answer(r) && (r[3] & 0x7F) is > 0 and <= 100 ? new(r[3] & 0x7F, (r[3] & 0x80) != 0) : null;

    private static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == Base && HeadsetPages.Is(s, 0xFF32) && s.Usage == 0x0074
        && s.Interface is 6 or -1 && s.OutputLength >= 64 && s.InputLength >= 64;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("ASTRO A50 Gen 4", Takes,
            _ => [new BrandWay("astro.gen4", WayTransport.Output, Request(Battery), Level) { Gate = Request(Status), Gated = Linked, TimeoutMs = 1000 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "astro", DisplayName = _ => "ASTRO A50 Gen 4" },
    ];
}

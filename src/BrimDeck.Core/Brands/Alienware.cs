namespace BrimDeck.Core;

// The Alienware Pro Wireless keyboard on its 2.4 GHz receiver, 413C:211D ("Alienware Pro KB Gaming Receiver"). 0x413C is
// Dell's vendor id, which Dell's other devices carry too, so only this pair is taken, and only its vendor collection (usage
// page 0xFF00, usage 1, interface 1, a 65-byte feature report with no report id). The read is the one Alienware Command Center
// sends (USB captures): feature report 0 set to 0E A1 and zeros; the receiver answers with an input report 0E A1 <percent>
// <state>, the state 0x04 with the cable in and 0x00 on battery. Any other frame on the collection is not the answer. From
// HaloBattery's alienware.py and its #287 (MIT; checked against Command Center on the keyboard: 37 % and 92 %, the charging
// state following the cable; its diagnostics list the vendor collection as interface 1, ff00:0001).
[BrandFamilies]
public static class Alienware
{
    public const int Vendor = 0x413C, Receiver = 0x211D;

    public static byte[] Request() => BrandFamilies.Frame(65, 0x00, 0x0E, 0xA1);
    // The input report without its zero report id: 0E A1 <percent> <state>. Zero is no reading (the request read back).
    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 4 && r[0] == 0x0E && r[1] == 0xA1 && r[2] is >= 1 and <= 100 ? new(r[2], r[3] == 0x04) : null;

    // The vendor page as Windows reports it, or byte-swapped (as some descriptors come out of HidP_GetCaps).
    private static bool Page(int actual, int page) => actual == page || actual == ((page & 0xFF) << 8 | page >> 8);
    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && shape.Product == Receiver && Page(shape.UsagePage, 0xFF00)
        && shape.Usage == 0x0001 && shape.Interface == 1 && shape.FeatureLength == 65 && shape.InputLength >= 5;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Alienware", Takes,
            _ => [new BrandWay("alienware.battery", WayTransport.FeatureListen, Request(), Battery) { TimeoutMs = 800, StripZero = true }])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "alienware", DisplayName = _ => "Alienware Pro Wireless" },
    ];
}

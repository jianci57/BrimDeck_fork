namespace BrimDeck.Core;

// FIFINE headsets on their 2.4 GHz dongles, vendor 0x3142 (FIFINE's own: every collection of the brand with the shape is
// asked). The vendor collection (usage page 0xFF90 usage 1) takes report 0x55 both ways, 64 bytes: the state query 55 65 01
// 00 is answered 55 65 <percent> <status> ..., status 03 charging and 1A the link to the headset lost; 55 0A ... says the
// headset is off or out of range. HeadsetControl's fifine_ht6.hpp (GPL; facts only: from a capture of FIFINE's SoundLink,
// verified on an HT6, 3142:00C3). The query read back (55 65 01 00) is no reading: its status is 0.
// The same Actions frame as ATK's N9 V2 Ultimate (AtkHeadsets).
[BrandFamilies]
public static class Fifine
{
    public const int Vendor = 0x3142;

    public static byte[] Request() => BrandFamilies.Frame(4, 0x55, 0x65, 0x01, 0x00);
    public static BatteryRead? Battery(byte[] r) => AtkHeadsets.StateBattery(r);

    private static bool Takes(HidShape s) => s.Vendor == Vendor && HeadsetPages.Is(s, 0xFF90) && s.Usage == 0x0001 && s.OutputLength >= 64 && s.InputLength >= 4;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("FIFINE", Takes, _ => [new BrandWay("fifine.state", WayTransport.Output, Request(), Battery) { TimeoutMs = 1000 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "fifine" },
    ];
}

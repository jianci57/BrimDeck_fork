namespace BrimDeck.Core;

// Redragon M690 PRO on Sinowealth's shared vendor id 0x258A, so only its two products: 002E on its cable, 002F its 2.4 GHz
// receiver. The vendor collection (usage page 0xFF00 usage 1, interface 1) carries the 7-byte feature report 5 for short
// commands; the status command 0x90 (the vendor app polls it every 30 s) is set as 05 90 and its answer got back from
// report 5 in exactly 8 bytes (the firmware stalls a longer read): 05 90 11 <percent> through the receiver; 05 90 10 01
// on the cable while charging (no level), 05 90 10 02 once charged (then 100 %). The receiver answers 0x90 from its own
// copy even while the mouse is off, with the last level it holds, so it is asked the link first, as the vendor app does:
// 05 80 01 .. the mouse linked, 05 80 00 .. off or asleep (the read then answers that); the cable is not asked it. The link
// answer is a feature report too, so the read needs the runner to ask a feature gate by setting and getting the report;
// until it does, the receiver's read goes unanswered rather than giving the held level. The M719 PRO needs an
// authentication handshake first and is not read. From OpenMouse's Redragon M690 PRO module and notes (AGPL; facts only,
// verified on firmware 2.95 and 2.97 through OpenMouse Bridge on Windows: the link read 00 while the mouse slept, and
// battery then gave the receiver's last value).
[BrandFamilies]
public static class RedragonMice
{
    public const int Vendor = 0x258A, Cable = 0x002E, Receiver = 0x002F;

    public static byte[] Request() => BrandFamilies.Frame(8, 0x05, 0x90);
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 4 || r[0] != 0x05 || r[1] != 0x90) return null;
        return r[2] switch
        {
            0x11 when r[3] is > 0 and <= 100 => new(r[3]),
            0x10 when r[3] == 0x01 => new(null, true),
            0x10 when r[3] == 0x02 => new(100),
            _ => null
        };
    }
    public static byte[] LinkRequest() => BrandFamilies.Frame(8, 0x05, 0x80);
    public static bool? Linked(byte[] r) => r.Length >= 3 && r[0] == 0x05 && r[1] == 0x80 && r[2] <= 1 ? r[2] == 1 : null;

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && shape.Product is Cable or Receiver
        && MouseShapes.Collection(shape, 0xFF00, 0x01) && shape.FeatureLength >= 8;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Redragon mouse", Takes, shape => [new BrandWay("redragon.m690", WayTransport.Feature, Request(), Battery)
            { GapMs = 60, FeatureLength = 8, Gate = shape.Product == Receiver ? LinkRequest() : null, Gated = Linked }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "redragon", DisplayName = _ => "Redragon M690 PRO" },
    ];
}

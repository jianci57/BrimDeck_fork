namespace BrimDeck.Core;

// Headsets whose dongles speak Airoha's RACE protocol on a vendor collection, usage page 0xFF13 usage 1, 62-byte reports.
// The battery query is output report 06 07 80 05 5A 03 00 D6 0C 00: report id 6, 7 bytes of frame, 0x80 (the headset behind
// the dongle; 0x00 would ask the dongle, which has no battery), then the frame 05 5A 03 00 D6 0C 00 (a request, 3 bytes of
// body: command 0x0CD6 and the role 0). The dongle does not send the answer: input report 7 is fetched (HidD_GetInputReport),
// laid out 07 <len> 80 <len bytes of frames>, every byte after the len valid ones being older reports' leftovers. The answer
// is 05 5D 05 00 D6 0C 00 00 <percent> (status 0, role 0), usually in the same report after the acknowledgement 05 5B 03 00
// D6 0C <status>, which is no reading (a status other than 0 is a refusal). The valid bytes can still hold older messages: a
// result before the acknowledgement is an older one (vigil-watt), and the last result is the level (on link-up the headset
// sends a falling run of results that ends on the level, Headroom), so the last result after the last acknowledgement is
// taken. A report of len 0 (nothing new) is none, nor a 0 (a headset just switched on reports 0 for a moment). The protocol
// has no charging state; a Maxwell on its cable is always charging (it cannot be switched off), so the cable's reading is
// charging (HaloBattery). Sources: HeadsetControl's audeze_maxwell_device.hpp (GPL; facts only: the report layout, the
// battery command and the refusal), HaloBattery's audeze.py and skullcandy.py (MIT; measured on Windows on a Maxwell dongle
// 4B19, its cable 4B1A and an Xbox dongle 4B18, and a Skull-HQ capture of a PLYR dongle answering 70 %), Headroom's
// headroom_race.py (MIT; the recipient byte and the falling run, on a Crusher PLYR 720 on Linux) and vigil-watt's
// docs/hardware/akg.md (MIT; an N9 Hybrid on Windows answering 50 % and 100 %).
// - Audeze (0x3329, Audeze's own id, but only the products named: HaloBattery writes to no other): Maxwell dongle 4B19, Xbox
//   dongle 4B18, cable 4B1A; Maxwell 2 4B29 and its Xbox dongle 4B28 (HeadsetControl, on interface 5). With no headset linked
//   a Maxwell dongle names itself "... Dongle" ("Audeze Maxwell Dongle", "Audeze Maxwell XBOX Dongle") instead of "Audeze
//   Maxwell HID" and keeps answering the last level it held, so such a dongle is not asked (HaloBattery's audeze.py).
// - AKG N9 Hybrid (0ECB:20D9; 0x0ECB is Harman's, shared with JBL): only its 62/62 collection on interface 3.
// - Skullcandy (0x34F0, its own id; but only the two products a source has): PLYR 3210 and Crusher PLYR 720 5310.
// The page is taken as Windows reported it where a source read it there (FF13); for the Maxwell 2 and the Crusher, also in
// the other byte order.
[BrandFamilies]
public static class AirohaRace
{
    public const int Audeze = 0x3329, Harman = 0x0ECB, Skullcandy = 0x34F0;
    public const int Maxwell = 0x4B19, MaxwellXbox = 0x4B18, MaxwellCable = 0x4B1A, Maxwell2 = 0x4B29, Maxwell2Xbox = 0x4B28;
    public const int AkgN9 = 0x20D9, Plyr = 0x3210, CrusherPlyr = 0x5310;
    public const byte ReplyId = 0x07;
    private const int ReportLength = 62;

    public static byte[] Request() => BrandFamilies.Frame(ReportLength, 0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C, 0x00);

    private static readonly byte[] Result = [0x05, 0x5D, 0x05, 0x00, 0xD6, 0x0C, 0x00, 0x00];
    private static readonly byte[] Ack = [0x05, 0x5B, 0x03, 0x00, 0xD6, 0x0C];
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 3 || r[0] != ReplyId || r[2] != 0x80) return null;
        int length = r[1];
        if (length < Result.Length + 1 || length > ReportLength - 3 || 3 + length > r.Length) return null;
        var valid = r.AsSpan(3, length);
        int ack = valid.LastIndexOf(Ack);
        if (ack >= 0)
        {
            if (ack + Ack.Length < valid.Length && valid[ack + Ack.Length] != 0x00) return null;
            valid = valid[(ack + Ack.Length)..];
        }
        int at = valid.LastIndexOf(Result);
        if (at < 0 || at + Result.Length >= valid.Length) return null;
        int percent = valid[at + Result.Length];
        return percent is > 0 and <= 100 ? new(percent) : null;
    }
    public static BatteryRead? CableBattery(byte[] r) => Battery(r) is { } read ? read with { Charging = true } : null;

    private static bool Page(HidShape s, bool measured) => measured ? s.UsagePage == 0xFF13 : HeadsetPages.Is(s, 0xFF13);
    private static bool Collection(HidShape s, bool measured)
        => Page(s, measured) && s.Usage == 0x0001 && s.InputLength >= ReportLength && s.OutputLength >= ReportLength;

    public static bool TakesAudeze(HidShape s) => s.Vendor == Audeze && s.Product switch
    {
        Maxwell or MaxwellXbox or MaxwellCable => Collection(s, measured: true),
        Maxwell2 or Maxwell2Xbox => Collection(s, measured: false),
        _ => false
    };
    public static bool TakesAkg(HidShape s) => s.Vendor == Harman && s.Product == AkgN9 && Collection(s, measured: true) && s.Interface is 3 or -1;
    public static bool TakesSkullcandy(HidShape s) => s.Vendor == Skullcandy && s.Product switch
    {
        Plyr => Collection(s, measured: true),
        CrusherPlyr => Collection(s, measured: false),
        _ => false
    };

    // A dongle that says no headset is linked (its name ends in "Dongle"): the level it would give is the last one it held.
    public static bool NoHeadset(HidShape s) => s.Product != MaxwellCable && s.Name.Trim().EndsWith("Dongle", StringComparison.OrdinalIgnoreCase);

    private static BrandWay Way(string name, Func<byte[], BatteryRead?>? parse = null)
        => new(name, WayTransport.OutputGetInput, Request(), parse ?? Battery) { ReplyId = ReplyId, GapMs = 50, TimeoutMs = 1000 };

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Audeze", TakesAudeze, s => NoHeadset(s) ? [] : [Way("audeze.race", s.Product == MaxwellCable ? CableBattery : null)])
        {
            Kind = _ => DeviceKind.Headphones, Prefix = "audeze",
            DisplayName = s => s.Product is Maxwell2 or Maxwell2Xbox ? "Audeze Maxwell 2" : "Audeze Maxwell"
        },
        new("AKG", TakesAkg, _ => [Way("akg.race")]) { Kind = _ => DeviceKind.Headphones, Prefix = "akg", DisplayName = _ => "AKG N9 Hybrid" },
        new("Skullcandy", TakesSkullcandy, _ => [Way("skullcandy.race")])
        {
            Kind = _ => DeviceKind.Headphones, Prefix = "skullcandy",
            DisplayName = s => s.Product == CrusherPlyr ? "Skullcandy Crusher PLYR 720" : "Skullcandy PLYR"
        },
    ];
}

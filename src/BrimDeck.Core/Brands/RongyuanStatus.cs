namespace BrimDeck.Core;

// Keyboards on Rongyuan's (Royuan's) 2.4 GHz receivers, which answer the battery themselves: MonsGeek, AJAZZ, VGN and
// CAROTMAS. The receivers carry the chip maker's vendor ids (Yichip's 0x3151, and 0x391D), shared with other products, so
// only the listed vendor and product pairs are asked, and only on their configuration collection: usage page 0xFFFF, usage 2,
// a 65-byte feature report with no report id. 3151:402D (LINGBAO's mouse receiver) is read elsewhere and is not listed.
//
// The read is the receivers' status query 0xF7, a feature report set to F7 and zeros, then read back. Rongyuan's checksum
// (0xFF less the sum of the seven bytes before it, 0x08 here) follows where the vendor's driver sends it; MonsGeek's driver
// sends the bare F7. The status, after the zero report id: [0] 1 when a relayed answer waits, [1] the keyboard's percent,
// [2] the mouse's, [3] 0 while the keyboard is online, [4] 0 while the mouse is, [5] 1 when the receiver takes a command,
// [6] what is paired (1 keyboard, 2 mouse, 3 both). A reading needs a keyboard paired, online, and a percent of 1 to 100; a
// keyboard paired but offline is asleep. A receiver with a mouse alone paired gives the mouse's percent and online flag the
// same way (MonsGeek's driver reads [2] and [4] then); with both paired, the keyboard's is read. The request read back (F7 at [0]), the zeros of a receiver just plugged in and the
// cable's echo are none. The receiver keeps its last value while the keyboard is away: [3] tells. No charging state is read:
// VGN V HUB reads one at [9], but on the Neon75 Air it stayed 0 with the cable in, while [1] jumped to 100 (mousedock's
// VGN-NEON75-AIR.md), so a 100 there may be the cable's and not a measured level.
// Facts from: HaloBattery's monsgeek.py (MIT; MonsGeek's driver, 3151:5026 measured at 54 % and 52 %); mousedock (MIT;
// VGN V HUB's RyServe.setStatus for 391D:2A05, measured at 82 %; MonsGeek's V4 driver for 3151:5038, user-tested);
// vigil-watt (MIT; AK870 3151:4011 measured at 41 %); YesCharge (MIT; CAROTMAS Mars68 3151:502B, its receiver status);
// sharkfin's protocol notes (GPL, facts only: the field layout and the checksum).
// The other receivers: the "dongle" filters of MonsGeek's and Akko's web drivers (app.monsgeek.com, web.akkogear.com: the
// same list, all usage page 0xFFFF, usage 2, interface 2), which both ask with the bare F7 every two seconds
// (___dongleCheck24GStatusByF7), and VGN's receivers on the same collection in VGN HUB's table (hub.vgnlab.com: the Neon75
// Extreme and Turbo, and the Dragonfly mice's). These ids are shared by many makers (Epomaker, Skyloong, YUNZII, IROK and
// others name 3151:5038 their own), so the name is the product string's, the table's only when the receiver gives none. Not
// measured by us beyond the receivers named above.
[BrandFamilies]
public static class RongyuanStatus
{
    public const byte Status = 0xF7;

    // F7 and zeros (MonsGeek), or with Rongyuan's checksum at the eighth byte after the report id.
    public static byte[] Request(bool checksum) => checksum ? BrandFamilies.Frame(65, 0x00, Status, 0, 0, 0, 0, 0, 0, 0xFF - Status) : BrandFamilies.Frame(65, 0x00, Status);

    // The feature report as read back, report id 0 first.
    // mouse: a mouse receiver's, whose mouse fields are read whatever the pairing says. A receiver in its radio's update state
    // (1 at 9) gives no reading.
    public static BatteryRead? Battery(byte[] r) => Battery(r, false);
    public static BatteryRead? MouseBattery(byte[] r) => Battery(r, true);
    private static BatteryRead? Battery(byte[] r, bool mouse)
    {
        if (r.Length < 10 || r[0] != 0x00 || r[1] > 1 || r[7] is not (1 or 2 or 3) || r[9] == 1) return null;
        if (mouse && r[7] == 1) return null;
        // The keyboard's fields, or the mouse's where a mouse alone is paired or the receiver is a mouse's.
        bool mouseFields = mouse || r[7] == 2;
        int level = mouseFields ? r[3] : r[2], offline = mouseFields ? r[5] : r[4];
        if (offline == 1) return new(null, Asleep: true);
        return offline == 0 && level is >= 1 and <= 100 ? new(level) : null;
    }

    public static bool Collection(HidShape shape) => shape.UsagePage == 0xFFFF && shape.Usage == 0x0002 && shape.FeatureLength == 65;

    // The receivers of mice (VGN's Dragonfly receivers in VGN HUB's table; MonsGeek's mouse receivers), listed as mice when
    // their product string does not say.
    // (VGN HUB's "vgn-ry-mouse" receivers; MonsGeek's own table's mouse receivers: AJAZZ AJ159 APEX and Havit M5 Pro on 5007,
    // AJ179P, AJ159P and MS955WB on 5020 and 504B, VK M5 PRO on 374A:A329; 391D:1A0E, VGN's id block of mouse receivers.)
    private static readonly HashSet<(int, int)> MouseReceivers =
        [(0x391D, 0x1A04), (0x391D, 0x1A08), (0x391D, 0x1A0E), (0x391D, 0x1A13), (0x391D, 0x1A25), (0x391D, 0x1A2B), (0x391D, 0x1A41),
         (0x3151, 0x5007), (0x3151, 0x5020), (0x3151, 0x504B), (0x374A, 0xA329)];

    private static BrandFamily Family(string brand, string prefix, bool checksum, params (int Vendor, int Product, string Name)[] models)
        => new(brand, shape => Collection(shape) && models.Any(model => model.Vendor == shape.Vendor && model.Product == shape.Product),
            shape => [new BrandWay(prefix + ".status", WayTransport.Feature, Request(checksum),
                MouseReceivers.Contains((shape.Vendor, shape.Product)) ? MouseBattery : Battery) { GapMs = 100, FeatureLength = 65 }])
        {
            Kind = shape => MouseReceivers.Contains((shape.Vendor, shape.Product)) ? DeviceKind.Mouse : DeviceKind.Keyboard, Prefix = prefix,
            DisplayName = shape => shape.Name.Length > 0 ? null : models.FirstOrDefault(model => model.Vendor == shape.Vendor && model.Product == shape.Product).Name,
        };

    // MonsGeek's and Akko's receiver list, less the pairs a family above names and LINGBAO's 3151:402D (read elsewhere).
    private static readonly (int Vendor, int Product, string Name)[] Shared =
    [
        // 3151:5031 (the Kysona Uranus's) is left out: HaloBattery took it off its list.
        .. new[] { 0x4014, 0x4017, 0x4021, 0x4027, 0x4038, 0x5006, 0x5007, 0x5008, 0x5020, 0x5037, 0x503D, 0x503F, 0x5040, 0x5042, 0x5045,
            0x504B, 0x504E, 0x5050, 0x5052, 0x505A }.Select(product => (0x3151, product, "2.4G receiver")),
        (0x391D, 0x1A0E, "2.4G receiver"),
        .. new[] { 0xE3E0, 0xE3E1, 0xE3E2, 0xE3E6, 0xE3E7, 0xE3E8, 0xE3F5, 0xE3F6, 0xE3F7, 0xE3FB, 0xE3FC, 0xE3FD }.Select(product => (0x342D, product, "2.4G receiver")),
        .. new[] { 0xA205, 0xA217, 0xA220, 0xA223, 0xA226, 0xA229, 0xA234, 0xA237, 0xA251, 0xA263, 0xA268, 0xA311, 0xA314, 0xA319, 0xA322, 0xA329 }
            .Select(product => (0x374A, product, "2.4G receiver")),
        (0x38EE, 0x0000, "2.4G receiver"), (0x38EE, 0x0002, "2.4G receiver"), (0x38EE, 0x0004, "2.4G receiver"),
        (0x347A, 0xA302, "2.4G receiver"), (0x347A, 0xA305, "2.4G receiver"), (0x379A, 0x1804, "2.4G receiver"), (0x379A, 0x1807, "2.4G receiver"),
        (0x38A9, 0x0002, "2.4G receiver"), (0x25AA, 0x2008, "2.4G receiver"), (0x25AA, 0x4008, "2.4G receiver"), (0x25AA, 0x4018, "2.4G receiver"),
        (0x0DB0, 0x197E, "2.4G receiver"), (0x0DB0, 0x8E9A, "2.4G receiver"), (0x145F, 0x0376, "2.4G receiver"), (0x145F, 0x038D, "2.4G receiver"),
    ];

    public static IEnumerable<BrandFamily> Families =>
    [
        Family("AJAZZ", "ajazz", true, (0x3151, 0x4011, "AJAZZ AK870")),
        Family("CAROTMAS", "carotmas", true, (0x3151, 0x502B, "CAROTMAS Mars68")),
        Family("MonsGeek", "monsgeek", false, (0x3151, 0x5026, "MonsGeek FUN60 Pro"), (0x3151, 0x5038, "MonsGeek M2 V5 HE")),
        Family("VGN", "vgn", true, (0x391D, 0x2A05, "VGN Neon75 Air"), (0x391D, 0x2A07, "VGN Neon75 Turbo"), (0x3151, 0x5055, "VGN Neon75 Extreme"),
            (0x391D, 0x1A04, "VGN Dragonfly3 Pro Max"), (0x391D, 0x1A08, "VGN Dragonfly Y2 V2 Pro Max"),
            (0x391D, 0x1A13, "VGN Dragonfly F1 V2 Pro Max"), (0x391D, 0x1A25, "VGN Dragonfly 3 Ultra"), (0x391D, 0x1A2B, "VGN Dragonfly F1 V5 Ultra"),
            (0x391D, 0x1A41, "VGN Dragonfly F2 V2 Ultra")),
        Family("Rongyuan receiver", "rongyuan", false, Shared),
    ];
}

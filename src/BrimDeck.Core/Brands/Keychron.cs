namespace BrimDeck.Core;

// Keychron mice on Keychron's own vendor id 0x3434: any of its devices with the collection Keychron Launcher's "8k" mouse
// protocol runs on (usage page 0xFFC1, usage 1), which Launcher itself treats as a mouse whatever the product (the M6 on
// D060, the M5 on D048, the Ultra-Link 8K receiver D028, the Link-KM D026 among them). The status command 0x06 on report
// 0xB3 (63 bytes after the id), answered on input report 0xB4: B4 06 ..., the percent in the low 7 bits of [20] (the
// report id counted), bit 7 set while charging. Launcher sends 0xB3 as an output report (OpenMouse's driver, confirmed on
// an M6); csutcliff/keychron-battery-dkms, as HaloBattery's keychron.py has it, sets it as a feature report and reads the
// answer from the interrupt endpoint the same way. Each goes only where the collection declares 0xB3 of that kind.
// The G3 Air, LM7 Air and their Ultra-Link receivers (D077, 2801, D05B, D078) speak the 8K Nordic protocol on 0xFF0A
// instead and are left out, as Launcher leaves them; so are the 4K mice (0xFF0A, checksummed Nordic framing, decoded from
// Launcher and not confirmed on hardware) and the "1k" protocol on page 0x8C (Launcher uses it only where a device has no
// 0xFFC1 collection, which one collection's shape cannot tell).
// From HaloBattery's keychron.py (MIT) and OpenMouse's Keychron 8K driver (AGPL; facts only: the collection, the output
// report and the charging bit).
[BrandFamilies]
public static class KeychronMice
{
    public const int Vendor = 0x3434;
    public static readonly int[] NordicProducts = [0xD077, 0x2801, 0xD05B, 0xD078];

    public static byte[] Request() => BrandFamilies.Frame(2, 0xB3, 0x06);
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 21 || r[0] != 0xB4 || r[1] != 0x06) return null;
        int level = r[20] & 0x7F;
        return level is > 0 and <= 100 ? new(level, (r[20] & 0x80) != 0) : null;
    }

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && !NordicProducts.Contains(shape.Product)
        && MouseShapes.Collection(shape, 0xFFC1, 0x01) && shape.InputLength >= 21;

    // Other brands' mice on the same firmware platform (their vendor ids are shared, so only these products), sent the
    // status command as an output report only, as their sources do:
    // - 京东京造 JZM5, its 2.4 GHz receiver 362D:D107 (Lemokey's id), usage page 0x8C usage 1: from
    //   LANMIN-X/Jingzao-JZM5-Battery's JZM5BatteryTray.swift (MIT; measured on the receiver).
    // - RIDLAG Owl X 8K, its receiver 362D:FF09 on 0xFFC1 / 1: from MouseDock's device database (MIT; from static analysis).
    // - Motospeed X6, its 8K receiver 0BDA:FFE0 (Realtek's id) on 0xFFC1 (usage 1 as on Keychron's own): from OpenMouse's
    //   Motospeed module (AGPL; facts only, not verified on hardware). On its cable (FFF1) the mouse answers a random level,
    //   so the cable is not asked.
    // - VGN's Dragonfly mice on their 8K receivers, 391D:1A20 to 1A23, 1A35, 1A36, 1A3B, 1A3C and 1A3E, on 0xFFC1 / 1: VGN HUB's
    //   table (hub.vgnlab.com) gives them this collection and the B3 06 status command. Not measured by us; their cables are
    //   not asked (the Motospeed cable's random levels).
    public static readonly int[] VgnReceivers = [0x1A20, 0x1A21, 0x1A22, 0x1A23, 0x1A35, 0x1A36, 0x1A3B, 0x1A3C, 0x1A3E];
    public static readonly (string Brand, string Prefix, string Name, int Vendor, int Product, int Page)[] Partners =
    [
        ("JD Jingzao mouse", "jingzao", "京东京造 JZM5", 0x362D, 0xD107, 0x008C),
        ("RIDLAG mouse", "ridlag", "RIDLAG Owl X 8K", 0x362D, 0xFF09, 0xFFC1),
        ("Motospeed mouse", "motospeed", "Motospeed X6", 0x0BDA, 0xFFE0, 0xFFC1),
    ];

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Keychron mouse", Takes, _ =>
        [
            new BrandWay("keychron.status", WayTransport.Output, Request(), Battery) { TimeoutMs = 500 },
            new BrandWay("keychron.status-feature", WayTransport.FeatureListen, Request(), Battery) { TimeoutMs = 500 },
        ])
        { Kind = _ => DeviceKind.Mouse, Prefix = "keychron" },
        .. Partners.Select(partner => new BrandFamily(partner.Brand,
            shape => shape.Vendor == partner.Vendor && shape.Product == partner.Product && MouseShapes.Collection(shape, partner.Page, 0x01) && shape.InputLength >= 21,
            _ => [new BrandWay(partner.Prefix + ".status", WayTransport.Output, Request(), Battery) { TimeoutMs = 500 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = partner.Prefix, DisplayName = _ => partner.Name }),
        new("VGN mouse", shape => shape.Vendor == 0x391D && VgnReceivers.Contains(shape.Product) && MouseShapes.Collection(shape, 0xFFC1, 0x01) && shape.InputLength >= 21,
            _ => [new BrandWay("vgn.b3", WayTransport.Output, Request(), Battery) { TimeoutMs = 500 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "vgn" },
    ];
}

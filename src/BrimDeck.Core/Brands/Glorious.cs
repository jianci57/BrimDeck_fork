namespace BrimDeck.Core;

// Glorious wireless mice on Sinowealth's shared vendor id 0x258A, so only these receivers: the Model O Wireless's (2022) and
// the Model D Wireless's (2023), and the Model O 2 PRO 4K/8K (2035, its cable 201B) and the Model D 2 PRO 4K/8K (2036, 201C).
// Each is read with the 0x83 feature exchange (MouseShapes.Request83) on its vendor collection with the unnumbered 64-byte
// feature report (FeatureLength 65 with the id byte): usage page 0xFFFF (usage 0 on the 4K/8K mice, 0 or 1 on the older
// receivers) or 0xFFA0 usage 1 on the older receivers. The receiver needs about 100 ms between the request and the read (the
// query goes to the mouse over the air). The older mice on their cables (2011, 2012) are not asked: no source reads them
// there (Glorious's own monitors take the cable's id appearing as the charging signal; the mouse then leaves the 2.4 GHz
// link, so its receiver has no reading for it). The 4K/8K cable answers the exchange itself (OpenMouse's capture of Glorious CORE on a
// 201B: A1 00 02 02 00 83 00 64).
// From HaloBattery's glorious.py (MIT; measured on a 2022 receiver, HaloBattery #281, after GloriousBatteryMonitor-Go, MIT,
// whose device table gives 2023), and, for the other product ids, the 4K/8K collection and the status codes, OpenMouse's
// glorious-classic and glorious-core2 modules (AGPL; facts only, from Glorious CORE's own device table and frame builders).
[BrandFamilies]
public static class GloriousMice
{
    public const int Vendor = 0x258A;
    public static readonly IReadOnlyDictionary<int, string> Products = new Dictionary<int, string>
    {
        [0x2022] = "Glorious Model O Wireless", [0x2023] = "Glorious Model D Wireless",
        [0x201B] = "Glorious Model O 2 PRO 4K/8K", [0x2035] = "Glorious Model O 2 PRO 4K/8K",
        [0x201C] = "Glorious Model D 2 PRO 4K/8K", [0x2036] = "Glorious Model D 2 PRO 4K/8K",
    };

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && Products.ContainsKey(shape.Product) && shape.FeatureLength == 65
        && (MouseShapes.Collection(shape, 0xFFFF, 0) || MouseShapes.Collection(shape, 0xFFFF, 1) || MouseShapes.Collection(shape, 0xFFA0, 1));

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Glorious", Takes, _ => [new BrandWay("glorious.f83", WayTransport.Feature, MouseShapes.Request83(), MouseShapes.Battery83) { GapMs = 100, FeatureLength = 65 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "glorious", DisplayName = shape => Products.GetValueOrDefault(shape.Product) },
    ];
}

namespace BrimDeck.Core;

// MCHOSE G7 MAX mouse on 41E4:1204 (its cable: the driver's connect mode is wired for 1204) and 41E4:1001 and 1003 (in the same
// list, taken here as its receivers; not confirmed). M HUB's web driver (mchose.com.cn, main bundle) opens their usage page 0xFF00
// collection and asks the BASICS command 21 50 00 02 4F 81 as feature report 3, the same frame and answer as EWEADN's beiyin
// receivers (EweadnMice.BeiyinRequest: the checksum at 1 recomputed, read back 03 50 00 0B 02 01 <state>, the level in the low
// seven bits of 6 and charging in bit 7). The driver sends it, waits 20 ms and reads the feature report back, up to three
// times 300 ms apart; asked here three times, 60 ms apart. Not measured by us.
[BrandFamilies]
public static class MchoseG7Max
{
    public const int Vendor = 0x41E4;
    public static readonly IReadOnlySet<int> Products = new HashSet<int> { 0x1204, 0x1001, 0x1003 };

    private static bool Takes(HidShape s) => s.Vendor == Vendor && Products.Contains(s.Product) && EweadnMice.BeiyinShape(s);

    public static IEnumerable<BrandFamily> Families =>
    [
        new("MCHOSE G7 MAX", Takes, _ => [new BrandWay("mchose.g7max", WayTransport.Feature, EweadnMice.BeiyinRequest(), EweadnMice.Beiyin)
            { GapMs = 60, Attempts = 3, FeatureLength = 64 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "mchose" },
    ];
}

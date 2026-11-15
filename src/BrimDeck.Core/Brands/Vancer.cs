namespace BrimDeck.Core;

// Vancer x Arbiter Studio Akitsu on the shared CompX vendor id 0x373E (LAMZU and Attack Shark use it too), so only its two
// products: 0066 on its cable, 0067 its 8K receiver. Usage page 0xFFFF usage 0, the unnumbered 64-byte feature report:
// the 0x83 feature exchange (MouseShapes.Request83), page 0 command 0x83 of Arbiter's Web HUB. OpenMouse's captures
// (AGPL; facts only) of both paths answer A1 00 02 02 00 83 00 64: not charging, 100 %.
[BrandFamilies]
public static class VancerMice
{
    public const int Vendor = 0x373E;
    public static readonly int[] Products = [0x0066, 0x0067];

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && Products.Contains(shape.Product) && shape.FeatureLength == 65
        && MouseShapes.Collection(shape, 0xFFFF, 0);

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Vancer", Takes, _ => [new BrandWay("vancer.f83", WayTransport.Feature, MouseShapes.Request83(), MouseShapes.Battery83) { GapMs = 100, FeatureLength = 65 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "vancer", DisplayName = _ => "Vancer Akitsu" },
    ];
}

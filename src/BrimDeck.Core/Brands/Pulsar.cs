namespace BrimDeck.Core;

// Pulsar X2 V3 Mini on its Sonix firmware: 3710:5403 its 8K receiver, 3402 on its cable (the X2 CrazyLight Mini on 5406
// and 3414 takes the Compx frame and is read elsewhere). On interface 3, usage page 0xFFFF usage 1, the unnumbered 64-byte
// feature report: 00 08 81 01 (the battery query), zeros, and in bytes 62 and 63 the 16-bit sum of the 62 before them,
// little endian; got back from the same report: 01 08 81 01 .. .. <percent> (the request read back has 00 first). On its
// cable the mouse charges: HaloBattery reports it charging. From HaloBattery's pulsar.py (MIT; measured on a receiver on
// Windows, HaloBattery #213: 00 01 08 81 01 00 00 18 with the report id, 24 %; the same mouse seen as 3402 on its cable,
// where its reply was not captured), after jonkristian/pulsar-x3-python (MIT), whose PROTOCOL_ANALYSIS lists the query
// 08 81 01 with the percent at 6 on the X3, and MouseDock's Sonix reader (MIT; the collection and the reply's 01).
[BrandFamilies]
public static class PulsarMice
{
    public const int Vendor = 0x3710, Receiver = 0x5403, Cable = 0x3402;
    public static readonly int[] Products = [Receiver, Cable];

    // Report id 0 first (65 bytes).
    public static byte[] Request()
    {
        var frame = BrandFamilies.Frame(65, 0x00, 0x00, 0x08, 0x81, 0x01);
        int sum = 0;
        for (int i = 1; i < 63; i++) sum += frame[i];
        frame[63] = (byte)sum;
        frame[64] = (byte)(sum >> 8);
        return frame;
    }
    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 8 && r[0] == 0x00 && r[1] == 0x01 && r[2] == 0x08 && r[3] == 0x81 && r[4] == 0x01 && r[7] is > 0 and <= 100 ? new(r[7]) : null;
    // The mouse on its cable: charging.
    public static BatteryRead? CableBattery(byte[] r) => Battery(r) is { } read ? read with { Charging = true } : null;

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && Products.Contains(shape.Product) && shape.Interface == 3
        && MouseShapes.Collection(shape, 0xFFFF, 0x01) && shape.FeatureLength == 65;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Pulsar", Takes, shape => [new BrandWay("pulsar.sonix", WayTransport.Feature, Request(), shape.Product == Cable ? CableBattery : Battery)
            { GapMs = 50, FeatureLength = 65 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "pulsar-sonix", DisplayName = _ => "Pulsar X2 V3 Mini" },
    ];
}

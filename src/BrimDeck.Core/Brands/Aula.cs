namespace BrimDeck.Core;

// AULA (狼蛛) mice with a read of their own (the SC580 shares EWEADN's receiver and is read there, EweadnMice).
// - SC380 Pro on its 2.4 GHz receiver 258A:0138 (Sinowealth's shared vendor id: this product only), usage page 0xFF02
//   usage 2, feature report 9 (520 bytes): [9, checksum, command, argument, 1, 0, length low, length high, data...], the
//   checksum the sum of the bytes after it. Command 09 argument 01 length 1 reads the battery; the answer comes back on the
//   same report with the header (and its checksum) echoed and the percent at 8. The request read back carries 0 there.
//   AULA's utility reads the battery only after the link read (07 00, 1 while the mouse is linked); what the receiver
//   answers for a mouse that is off is not documented, and the link read is not sent (a feature read cannot be asked
//   first). From OpenMouse's AULA SC380 Pro module (AGPL; facts only, from AULA's utility, verified on a receiver on
//   Windows). The cable (258A:0136) was not tested there and is not sent to.
// - L9 Ultra on its receiver 38A6:28FD (not AULA G9's 38A6:0030, which Pushes listens to), its 0xFFA8 usage 2 collection:
//   output report 0x0B with the status command 0x81, answered on input report 0x0A: 0A 81 ..., 1 at 17 while charging, the
//   percent at 18. From vigil-watt's AulaL9Codec (MIT; reported by a contributor; only the 0xFFA8 collection answers) and,
//   for the usage, MouseDock's AULA FFA8 family (MIT).
[BrandFamilies]
public static class AulaMice
{
    public const int Sc380Vendor = 0x258A, Sc380Receiver = 0x0138, L9Vendor = 0x38A6, L9Receiver = 0x28FD;

    public static byte[] Sc380Request()
    {
        var frame = BrandFamilies.Frame(8, 0x09, 0x00, 0x09, 0x01, 0x01, 0x00, 0x01, 0x00);
        int sum = 0;
        for (int i = 2; i < frame.Length; i++) sum += frame[i];
        frame[1] = (byte)sum;
        return frame;
    }
    public static BatteryRead? Sc380(byte[] r)
        => r.Length >= 9 && r[0] == 0x09 && r[2] == 0x09 && r[3] == 0x01 && r[4] == 0x01 && r[5] == 0x00 && r[6] == 0x01 && r[7] == 0x00
            && r[8] is > 0 and <= 100 ? new(r[8]) : null;

    public static BatteryRead? AulaA9(byte[] r)
    {
        if (r.Length < 4 || r[0] != 0x55 || r[1] != 0x65 || r[3] == 0x00) return null;
        if (r[3] == 0x1A) return new(null, Asleep: true);
        return r[2] is > 0 and <= 100 ? new(r[2], r[3] is 0x03 or 0x04) : null;
    }

    public static byte[] L9Request() => BrandFamilies.Frame(2, 0x0B, 0x81);
    public static BatteryRead? L9(byte[] r)
        => r.Length >= 19 && r[0] == 0x0A && r[1] == 0x81 && r[17] <= 1 && r[18] is > 0 and <= 100 ? new(r[18], r[17] == 1) : null;

    private static bool Sc380Collection(HidShape shape) => shape.Vendor == Sc380Vendor && shape.Product == Sc380Receiver
        && MouseShapes.Collection(shape, 0xFF02, 0x02) && shape.FeatureLength >= 9;
    private static bool L9Collection(HidShape shape) => shape.Vendor == L9Vendor && shape.Product == L9Receiver
        && MouseShapes.Collection(shape, 0xFFA8, 0x02) && shape.InputLength >= 19 && shape.OutputLength >= 3;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("AULA mouse", shape => Sc380Collection(shape) || L9Collection(shape), shape => Sc380Collection(shape)
            ? [new BrandWay("aula.sc380", WayTransport.Feature, Sc380Request(), Sc380)]
            : [new BrandWay("aula.l9", WayTransport.Output, L9Request(), L9) { TimeoutMs = 600 }])
        {
            Kind = _ => DeviceKind.Mouse, Prefix = "aula-mouse",
            DisplayName = shape => shape.Product == Sc380Receiver ? "AULA SC380 Pro" : "AULA L9 Ultra",
        },
        // The AULA A9 headset's base (38A6:001D, named "AULA-A9" or "AULA-A9 PC"): AULA's web driver asks it with output report
        // 0x55, 55 65 00 and zeros to 64 bytes, on usage page 0xFF90, usage 1, and reads 55 65 <percent> <state>, the state
        // 03 and 04 charging (the driver takes both so, the level as read), 1A the headset not linked: the frame ATK's N9 V2
        // answers, whose 04 means full there (AulaA9). Not measured by us.
        new("AULA headset", shape => shape.Vendor == L9Vendor && shape.Product == 0x001D && HeadsetPages.Is(shape, 0xFF90) && shape.Usage == 0x0001
                && shape.InputLength >= 4 && shape.OutputLength >= 64
                && shape.Name.Trim() is var name && (name.Equals("AULA-A9", StringComparison.OrdinalIgnoreCase) || name.Equals("AULA-A9 PC", StringComparison.OrdinalIgnoreCase)),
            _ => [new BrandWay("aula.a9", WayTransport.Output, BrandFamilies.Frame(64, 0x55, 0x65, 0x00), AulaA9) { TimeoutMs = 1000 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "aula-headset", DisplayName = _ => "AULA A9" },
    ];
}

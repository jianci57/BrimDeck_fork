namespace BrimDeck.Core;

// Thunderobot (雷神) ML903 NearLink Se on its receiver 369B:0234, usage page 0xFFA0 usage 2, 64-byte input and output
// reports. Output report 0xA0 with the device-info command 0x02, answered on input report 0xA1: A1 02 and then fields of a
// two-byte tag and a value whose length the tag fixes (0204: 2, 0211: 4, 0213: 6, 0225 and 0226: 1), zeros after the last.
// 0225 is the battery percent; 0226 is the work mode, not a charging state, and no charging state is read. A tag not in
// that list, a second battery field, a non-zero byte after the fields or a field cut short is no answer. From vigil-watt's
// ThunderobotCodec and its notes (MIT; tags from Thunderobot's own DLL, the layout from a measured answer:
// A1 02 04 01 11 02 11 E3 01 9B 36 02 13 31 32 33 34 35 36 02 25 23 02 26 01, 35 %).
[BrandFamilies]
public static class ThunderobotMice
{
    public const int Vendor = 0x369B, Ml903 = 0x0234;

    public static byte[] Request() => BrandFamilies.Frame(2, 0xA0, 0x02);
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 5 || r[0] != 0xA1 || r[1] != 0x02) return null;
        int? level = null;
        for (int at = 1; at < r.Length;)
        {
            if (r[at] == 0)
            {
                for (int i = at; i < r.Length; i++) if (r[i] != 0) return null;
                break;
            }
            if (at + 2 > r.Length) return null;
            int length = (r[at] << 8 | r[at + 1]) switch { 0x0204 => 2, 0x0211 => 4, 0x0213 => 6, 0x0225 or 0x0226 => 1, _ => 0 };
            if (length == 0 || at + 2 + length > r.Length) return null;
            if (r[at] == 0x02 && r[at + 1] == 0x25)
            {
                if (level is not null) return null;
                level = r[at + 2];
            }
            at += 2 + length;
        }
        return level is > 0 and <= 100 ? new(level.Value) : null;
    }

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && shape.Product == Ml903
        && MouseShapes.Collection(shape, 0xFFA0, 0x02) && shape.InputLength >= 25;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Thunderobot", Takes, _ => [new BrandWay("thunderobot.info", WayTransport.Output, Request(), Battery) { TimeoutMs = 800 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "thunderobot", DisplayName = _ => "Thunderobot ML903" },
    ];
}

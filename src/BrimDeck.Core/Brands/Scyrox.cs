namespace BrimDeck.Core;

// Scyrox's newer mice on 0x395A (the older V6 and V8 are on Compx's 0x3554 and read with the Compx frame elsewhere). Its
// models speak different commands on the same report, so each read goes only to the products its source names, on the
// vendor collection usage page 0xFF00 usage 2 with 64-byte input and output reports. Frames are output report 0x08, the
// command after the id, zeros, and in the last byte the low byte of the id plus the command bytes.
// - V6S / V8S: the Turbo 8K receiver 000E, the mouse on its cable 000B, and 000C. The status command 2E 00 03 (08 2E 00 03
//   .. 39) is answered 08 2E .. .. <percent> <charging 1/0> <linked 1/0> ..., its last byte the low byte of the sum of the
//   63 before it. Linked 0 is the receiver answering for a mouse that is not connected (asleep or off), with 0 %: the mouse
//   asleep. A frame the firmware did not take is sent back unchanged, so the request itself is no answer. From
//   zzy6-a/ScyroxTray's README and ScyroxV2.cs (no license; facts only, from Scyrox's S-Center 2.0 web driver, measured:
//   08 2E 00 03 32 01 01 03 00 03 00 08 00 00 0B .. 86, 50 % charging) and MouseDock's Scyrox reader (MIT; linked 0 as
//   offline, charging and linked above 1 refused).
// - V12M on 0006: the profile read 08 01 .. 09, answered 08 01 with the percent at 20; the answer counts only with [3] not
//   0, [4] equal to [5] and at most 3, [7] from 1 to 5 and [62] not 1, and no charging state is read. From MouseDock's
//   ReferenceBatteryProtocol (MIT; ported from another author's reference program).
[BrandFamilies]
public static class ScyroxMice
{
    public const int Vendor = 0x395A, V12m = 0x0006;
    public static readonly int[] StatusProducts = [0x000E, 0x000B, 0x000C];

    private static byte[] Frame(params byte[] command)
    {
        var frame = BrandFamilies.Frame(64, 0x08);
        command.CopyTo(frame, 1);
        int sum = 0x08;
        foreach (byte b in command) sum += b;
        frame[63] = (byte)sum;
        return frame;
    }
    public static byte[] StatusRequest() => Frame(0x2E, 0x00, 0x03);
    public static byte[] ProfileRequest() => Frame(0x01);

    public static BatteryRead? Status(byte[] r)
    {
        if (r.Length < 64 || r[0] != 0x08 || r[1] != 0x2E) return null;
        int sum = 0;
        for (int i = 0; i < 63; i++) sum += r[i];
        if ((byte)sum != r[63] || r[5] > 1 || r[6] > 1 || r.AsSpan(0, 64).SequenceEqual(StatusRequest())) return null;
        if (r[6] == 0) return new(null, Asleep: true);
        return r[4] is > 0 and <= 100 ? new(r[4], r[5] == 1) : null;
    }
    public static BatteryRead? Profile(byte[] r)
        => r.Length >= 64 && r[0] == 0x08 && r[1] == 0x01 && r[3] != 0 && r[4] == r[5] && r[4] <= 3 && r[7] is >= 1 and <= 5 && r[62] != 1
            && r[20] is > 0 and <= 100 ? new(r[20]) : null;

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && (StatusProducts.Contains(shape.Product) || shape.Product == V12m)
        && MouseShapes.Collection(shape, 0xFF00, 0x02) && shape.InputLength == 64 && shape.OutputLength == 64;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Scyrox", Takes, shape => shape.Product == V12m
            ? [new BrandWay("scyrox.profile", WayTransport.Output, ProfileRequest(), Profile) { TimeoutMs = 600 }]
            : [new BrandWay("scyrox.status", WayTransport.Output, StatusRequest(), Status) { TimeoutMs = 600 }])
        {
            Kind = _ => DeviceKind.Mouse, Prefix = "scyrox",
            DisplayName = shape => shape.Product == V12m ? "Scyrox V12M" : "Scyrox V6S / V8S",
        },
    ];
}

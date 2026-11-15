namespace BrimDeck.Core;

// HyperX Pulsefire mice, on HP's 0x03F0 and Kingston's 0x0951 (their headsets have readers of their own: Headsets,
// Pushes), so only these products, each on its vendor collection, usage page 0xFF00 usage 1.
// - Pulsefire Haste (0951:1727 cable, 03F0:0F8F and 048E cable, 03F0:028E receiver) and Pulsefire Dart (0951:16E1
//   receiver, 16E2 cable): NGenuity2's heartbeat, the unnumbered 64-byte output report 51 (zeros after), answered by the
//   input report 51 00 00 03 <percent> <charging> (the report id 0 stripped): 0 not charging, 1 charging, 2 charged.
//   Wired mice and some firmware answer 0 or 0xFF for the percent, which is no reading. NGenuity asks the connection first
//   (46, answered 46 00 00 <1 through the receiver, 2 on the cable> <1 the mouse linked, 0 not>) and goes no further while
//   the mouse is not linked; so does this read, which takes the receiver's "not linked" as the mouse asleep or off. From
//   santeri3700's Pulsefire Dart reverse engineering (CC0; captures of NGenuity on a Dart: 46 00 00 01 01 then
//   51 00 00 03 62 01 .., 98 % charging; 46 00 00 01 00 with the mouse away) and OpenMouse's HyperX module (AGPL; facts
//   only: the Haste's ids, the connection query and the charging values).
// - Pulsefire Haste 2 Wireless (03F0:0D97 and 0F98 receivers, interface 2): NGENUITY's battery query, output report 0x50
//   with sub-command 2, answered on input report 0x51: 51 02 <percent> <charge status> (0 not charging, 1 charging, 2
//   full, 3 charger error, 0xFF unknown), then temperature and millivolts. The link query 52 01 is asked first where the
//   collection declares report 0x52: 53 01 <1 linked, 0 asleep or off>. From HaloBattery's hyperx_ngenuity.py (MIT, after
//   PMarinov1994/ngenuity-linux-cli, confirmed by that project on a Haste 2 Wireless).
[BrandFamilies]
public static class HyperXMice
{
    public const int Hp = 0x03F0, Kingston = 0x0951;
    public static readonly (int Vendor, int Product, string Name)[] Heartbeat =
    [
        (Kingston, 0x1727, "HyperX Pulsefire Haste"), (Hp, 0x0F8F, "HyperX Pulsefire Haste"), (Hp, 0x048E, "HyperX Pulsefire Haste"),
        (Hp, 0x028E, "HyperX Pulsefire Haste Wireless"), (Kingston, 0x16E1, "HyperX Pulsefire Dart"), (Kingston, 0x16E2, "HyperX Pulsefire Dart"),
    ];
    public static readonly (int Vendor, int Product, string Name)[] Battery2 =
        [(Hp, 0x0D97, "HyperX Pulsefire Haste 2 Wireless"), (Hp, 0x0F98, "HyperX Pulsefire Haste 2 Wireless")];

    public static byte[] HeartbeatRequest() => BrandFamilies.Frame(65, 0x00, 0x51);
    // The answer without its report id (StripZero).
    public static BatteryRead? HeartbeatBattery(byte[] r)
        => r.Length >= 6 && r[0] == 0x51 && r[4] is > 0 and <= 100 && r[5] <= 2 ? new(r[4], r[5] == 1) : null;
    public static byte[] ConnectionRequest() => BrandFamilies.Frame(65, 0x00, 0x46);
    // The connection answer without its report id: linked on the cable, or through the receiver while the mouse is linked.
    public static bool? Connected(byte[] r)
        => r.Length >= 5 && r[0] == 0x46 ? r[3] switch { 0x02 => true, 0x01 => r[4] == 0x01, _ => null } : null;

    public static byte[] Battery2Request() => BrandFamilies.Frame(2, 0x50, 0x02);
    public static BatteryRead? Battery2Battery(byte[] r)
        => r.Length >= 4 && r[0] == 0x51 && r[1] == 0x02 && r[2] is > 0 and <= 100 && r[3] is <= 3 or 0xFF ? new(r[2], r[3] == 1) : null;
    public static byte[] LinkRequest() => BrandFamilies.Frame(2, 0x52, 0x01);
    public static bool? Linked(byte[] r) => r.Length >= 3 && r[0] == 0x53 && r[1] == 0x01 ? r[2] != 0 : null;

    private static (int Vendor, int Product, string Name)? Find((int Vendor, int Product, string Name)[] list, HidShape shape)
    {
        foreach (var model in list) if (model.Vendor == shape.Vendor && model.Product == shape.Product) return model;
        return null;
    }
    public static bool Takes(HidShape shape) => (Find(Heartbeat, shape) is not null || Find(Battery2, shape) is not null)
        && MouseShapes.Collection(shape, 0xFF00, 0x01) && shape.InputLength >= 6;

    public static IReadOnlyList<BrandWay> Ways(HidShape shape) => Find(Battery2, shape) is not null
        ? [new BrandWay("hyperx.battery", WayTransport.Output, Battery2Request(), Battery2Battery)
            { TimeoutMs = 500, Gate = shape.OutputIds.Contains(0x52) ? LinkRequest() : null, Gated = Linked }]
        : [new BrandWay("hyperx.heartbeat", WayTransport.Output, HeartbeatRequest(), HeartbeatBattery)
            { TimeoutMs = 600, StripZero = true, Gate = ConnectionRequest(), Gated = Connected }];

    public static IEnumerable<BrandFamily> Families =>
    [
        new("HyperX mouse", Takes, Ways)
        {
            Kind = _ => DeviceKind.Mouse, Prefix = "hyperx-mouse",
            DisplayName = shape => (Find(Heartbeat, shape) ?? Find(Battery2, shape))?.Name,
        },
    ];
}

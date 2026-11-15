namespace BrimDeck.Core;

// GameSir (盖世小鸡) controllers on their 2.4 GHz receivers and cables, vendor 0x3537 (GameSir's own), read by the collection
// they answer on rather than by model; both pages are as measured on Windows (HidP_GetCaps), the byte-swapped order is taken
// too.
// - Status (Nova 2, Nova 2 Lite, T4N2): the vendor collection on page 0xFF7A, usage 1, 65-byte reports both ways, takes the
//   device information request 00 01 01 00 (unnumbered, zeros after). The answer, report id 0 first: bytes 1 to 6 the
//   receiver's MAC, byte 19 the percent. A MAC of zeros is a receiver not listening, and the request read back (01 01 then
//   zeros) is no answer. From WWWyyy452/GamesirBatteryTray (MIT; Nova 2 on 3537:1098, checked against GameSir Connect). On
//   its cable (3537:100F) the Nova 2 Lite answers FF at 19: charging, no percent (YesCharge, MIT, on hardware). A percent of 0
//   is not taken.
// - Heartbeat (G7 Pro 8K, Cyclone 2 and 3, Tarantula CE): on the vendor collection page 0xFFF0, usage 0x40, the heartbeat
//   output report 0F F2 00 that GameSir Connect sends all along brings the controller's input report 0x12; byte 36 is the
//   percent and bit 0 of byte 35 says it charges (on the Tarantula CE, 3537:103D, bytes 28 and 27). 0 at 36 is the stream
//   before the controller is ready, not a level (vigil-watt's GameSir notes, MIT). From agnostos-t/gamesir-battery-tray (MIT;
//   G7 Pro 8K on 3537:1033, checked against GameSir Connect), the Tarantula CE's offsets from vo1/gamesir-battery-level (facts
//   only). The heartbeat changes no setting.
[BrandFamilies]
public static class GameSir
{
    public const int Vendor = 0x3537;
    public const int TarantulaCe = 0x103D;

    public static bool StatusCollection(HidShape shape) => shape.Vendor == Vendor && shape.UsagePage is 0xFF7A or 0x7AFF && shape.Usage == 0x0001
        && shape.InputLength >= 65 && shape.OutputLength >= 65;
    public static bool HeartbeatCollection(HidShape shape) => shape.Vendor == Vendor && shape.UsagePage is 0xFFF0 or 0xF0FF && shape.Usage == 0x0040
        && shape.InputLength >= (shape.Product == TarantulaCe ? 29 : 37);

    public static byte[] StatusRequest() => BrandFamilies.Frame(4, 0x00, 0x01, 0x01, 0x00);
    public static BatteryRead? Status(byte[] r)
    {
        if (r.Length < 20 || r[0] != 0x00) return null;
        if (r.AsSpan(1, 6).IndexOfAnyExcept((byte)0) < 0) return null;
        // The request read back: 01 01, nothing after.
        if (r[1] == 0x01 && r[2] == 0x01 && r.AsSpan(3).IndexOfAnyExcept((byte)0) < 0) return null;
        if (r[19] == 0xFF) return new(null, Charging: true);
        return r[19] is > 0 and <= 100 ? new(r[19]) : null;
    }

    public static byte[] Heartbeat() => [0x0F, 0xF2, 0x00];
    public static BatteryRead? Report(byte[] r, bool tarantulaCe)
    {
        int level = tarantulaCe ? 28 : 36, charge = level - 1;
        if (r.Length <= level || r[0] != 0x12 || r[level] is 0 or > 100) return null;
        return new(r[level], (r[charge] & 0x01) != 0);
    }

    public static IEnumerable<BrandFamily> Families =>
    [
        new("GameSir", shape => StatusCollection(shape) || HeartbeatCollection(shape), shape =>
        {
            if (StatusCollection(shape)) return [new BrandWay("gamesir.status", WayTransport.Output, StatusRequest(), Status) { TimeoutMs = 800 }];
            bool tarantulaCe = shape.Product == TarantulaCe;
            return [new BrandWay("gamesir.heartbeat", WayTransport.OutputListen, Heartbeat(), r => Report(r, tarantulaCe)) { TimeoutMs = 600 }];
        })
        { Kind = _ => DeviceKind.Controller, Prefix = "gamesir" },
    ];
}

namespace BrimDeck.Core;

// SteelSeries headsets the Headsets class does not list, vendor 0x1038 (shared with the mice SteelSeriesMice takes and the
// headsets Headsets takes, so only these products). Each on interface 3:
// - Arctis 1 (12B3, Xbox 12B6), Arctis 7X (12D7) and 7P (12D5), usage page 0xFF43 usage 0x0202: output report 06, 06 12; the
//   answer 06 12 <status> <percent>, status 01 the headset off; no charging state.
// - Arctis 7+ (220E, PS5 2212, Xbox 2216, Destiny 2236), Nova 3P/3X Wireless (2269, 226D), Nova 5/5X (2232, 2253) and the
//   Arctis GameBuds (230A), usage page 0xFFC0 usage 1, reports not numbered: 00 B0, answered B0 ...:
//   7+: B0 <status> <step 0..4> <charging>, status 01 the headset off, charging 01;
//   Nova 3P/5: B0 <status> .. <percent> <charging>, status 02 the headset off, charging 01;
//   GameBuds: B0 .. .. <left status> <right status> <left %> <right %>, status 03 a bud out of the case (the lower of those is
//   the reading; a bud in the case reports 0 % and is off), 02 a bud docked or off (both docked: asleep, as HeadsetControl
//   reports them offline); no charging state.
// - Arctis Nova Elite (2244) and Nova Pro Omni (2290), usage page 0xFFC0 usage 1, report 01: 01 B0 (64 bytes), answered
//   01 B0 .. .. .. .. <percent> at 6; the Elite's power state at 14 (01 the headset offline, 02 charging on the cable, 04
//   standby, 08 online) and charging at 15 (02); the Omni's state at 15 (01 offline, 02 charging on the cable, 08 on
//   battery). A switched-off Elite reports 0 %.
// Requests, layouts and product ids from HeadsetControl (GPL; facts only: steelseries_arctis_1, _7_plus, _nova_3p_wireless,
// _nova_5, _gamebuds, and pull request 584 for the Omni, verified on one: 93 %), with HaloBattery's steelseries.py and
// steelseries_elite.py (MIT) for the B0 echo every answer starts with and the Elite (measured: 01 b0 00 00 01 00 1f 64 ...,
// 31 %). The request read back (B0 00 00 ..., 06 12 00 00) carries no level and is no reading. The Arctis Pro Wireless (1290)
// is not read: its level is a second request whose answer does not echo it.
[BrandFamilies]
public static class SteelSeriesHeadsets
{
    public const int Vendor = 0x1038;
    public enum Exchange { Arctis1, Arctis7Plus, Nova5, GameBuds, Elite, Omni }

    public static Exchange? Find(int product) => product switch
    {
        0x12B3 or 0x12B6 or 0x12D7 or 0x12D5 => Exchange.Arctis1,
        0x220E or 0x2212 or 0x2216 or 0x2236 => Exchange.Arctis7Plus,
        0x2269 or 0x226D or 0x2232 or 0x2253 => Exchange.Nova5,
        // The GameBuds X (2317) on the GameBuds' exchange: HaloBattery's steelseries.py (MIT), checked against GG on its user's
        // receiver; not measured by us.
        0x230A or 0x2317 => Exchange.GameBuds,
        0x2244 => Exchange.Elite,
        0x2290 => Exchange.Omni,
        _ => null
    };

    public static byte[] Request(Exchange exchange) => exchange switch
    {
        Exchange.Arctis1 => [0x06, 0x12],
        Exchange.Elite or Exchange.Omni => BrandFamilies.Frame(64, 0x01, 0xB0),
        _ => [0x00, 0xB0]
    };

    public static Func<byte[], BatteryRead?> Battery(Exchange exchange) => exchange switch
    {
        Exchange.Arctis1 => r => r.Length < 4 || r[0] != 0x06 || r[1] != 0x12 ? null
            : r[2] == 0x01 ? new(null, Asleep: true) : r[3] is > 0 and <= 100 ? new(r[3]) : null,
        Exchange.Arctis7Plus => r => r.Length < 4 || r[0] != 0xB0 ? null
            : r[1] == 0x01 ? new(null, Asleep: true) : r[2] is > 0 and <= 4 ? new(r[2] * 25, r[3] == 0x01, Coarse: true) : null,
        Exchange.Nova5 => r => r.Length < 5 || r[0] != 0xB0 ? null
            : r[1] == 0x02 ? new(null, Asleep: true) : r[3] is > 0 and <= 100 ? new(r[3], r[4] == 0x01) : null,
        Exchange.GameBuds => GameBudsBattery,
        Exchange.Elite => r => r.Length < 16 || r[0] != 0x01 || r[1] != 0xB0 ? null
            : r[14] == 0x01 ? new(null, Asleep: true) : r[6] is > 0 and <= 100 ? new(r[6], r[15] == 0x02 || r[14] == 0x02) : null,
        _ => r => r.Length < 16 || r[0] != 0x01 || r[1] != 0xB0 ? null
            : r[15] == 0x01 ? new(null, Asleep: true) : r[15] is 0x02 or 0x08 && r[6] is > 0 and <= 100 ? new(r[6], r[15] == 0x02) : null,
    };

    private static BatteryRead? GameBudsBattery(byte[] r)
    {
        if (r.Length < 7 || r[0] != 0xB0) return null;
        var levels = new List<int>();
        for (int bud = 0; bud < 2; bud++)
            if (r[3 + bud] == 0x03 && r[5 + bud] is > 0 and <= 100) levels.Add(r[5 + bud]);
        if (levels.Count > 0) return new(levels.Min());
        return r[3] is 0x02 or 0x03 && r[4] is 0x02 or 0x03 ? new(null, Asleep: true) : null;
    }

    private static bool Takes(HidShape s)
    {
        if (s.Vendor != Vendor || Find(s.Product) is not { } exchange || s.Interface is not (3 or -1) || s.OutputLength < 2) return false;
        return exchange == Exchange.Arctis1 ? HeadsetPages.Is(s, 0xFF43) && s.Usage == 0x0202 : HeadsetPages.Is(s, 0xFFC0) && s.Usage == 0x0001;
    }

    private static IReadOnlyList<BrandWay> Ways(HidShape s)
    {
        var exchange = Find(s.Product)!.Value;
        string name = exchange switch
        {
            Exchange.Arctis1 => "steelseries-headset.arctis1", Exchange.Elite or Exchange.Omni => "steelseries-headset.status1",
            _ => "steelseries-headset.status"
        };
        return [new BrandWay(name, WayTransport.Output, Request(exchange), Battery(exchange)) { TimeoutMs = 1000, StripZero = exchange is not (Exchange.Arctis1 or Exchange.Elite or Exchange.Omni) }];
    }

    public static IEnumerable<BrandFamily> Families =>
    [
        new("SteelSeries Headset", Takes, Ways)
        { Kind = s => Find(s.Product) == Exchange.GameBuds ? DeviceKind.Earbuds : DeviceKind.Headphones, Prefix = "steelseries-headset" },
    ];
}

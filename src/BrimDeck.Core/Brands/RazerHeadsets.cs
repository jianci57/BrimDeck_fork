namespace BrimDeck.Core;

// Razer's headsets on their own receivers, vendor 0x1532. The id is shared with the mice and keyboards Razer (the 90-byte
// reader) takes, so only these products are taken, each on its documented vendor collection:
// - BlackShark V2 Pro 2020 (0528) and Nari (051C), usage page 0xFF00 usage 1: feature report 0xFF, set FF 0A 00 FD 04 12 F1
//   02 05 and got back about 100 ms later; a fresh answer begins FF 0F 05 FE 12 04 1F 08 05, then the power state at 11 (09
//   charging, 01/02/03 on battery, 06 charged), the voltage in mV at 12 and 13 (big endian; 0 with the headset off) and the
//   level at 14 in coarse steps (100, 80, 50, 30, 10). FF 01 00 FE ... is the receiver's stale copy of an old answer (no new
//   one from the headset): its bytes are no reading, and when it is still all the receiver holds at the second try, the
//   headset is off (HaloBattery waits up to 0.5 s and then says so; here two tries of 150 ms each). While charging the level
//   byte is not the battery's, so only the charging state is given. From HaloBattery's blackshark2020.py (MIT; measured on a
//   0528 on Windows, so its page is taken only as measured); the Nari's capture in Modzeleczek/RazerNariBatteryLevel (no
//   license stated; facts only) has the same request and answer layout (FF 0F 05 FE 12 04 1F 08 05 03 05 02 0D E0 1E:
//   3552 mV, level 30) but no usage page, so the Nari's is taken in either byte order. The 0528's cable (052E) is not read:
//   HaloBattery reads the battery from the receiver only.
// - BlackShark V2 Pro 2023 (0555, 0556) and 0583, usage page 0xFF00 or 0xFF14 usage 1: the "PA" protocol, output report 02.
//   The query (02 80 08 .. .. 50 41 08 .. 03 <command>) is only answered in remote mode, which is switched on before
//   (02 80 07 .. .. 50 41 0E .. 02 E1 01, sent twice: the first only wakes the link) and off after (the same with 00). The
//   answer is 02 .. 50 49 ..., the command at 12, 01 (acknowledged) at 13, the length at 14 and the level at 15. As
//   HaloBattery's blackshark.py (MIT) and vigil-watt's razer-headset.md (MIT, measured on a 0555: 02 0E 50 49 08 CE 92 DE 05
//   00 04 00 21 01 01 4F, 79 %) give it. Charging is another query (0x2A), asked right after the level in the same remote
//   session (the Barracuda Pro's too), as HaloBattery does. A switched-off headset does not
//   answer. Both sources are Windows programs taking the page as Windows gives it (0xFF00 or 0xFF14, as is).
// - Barracuda Pro (053A), usage page 0xFF00 usage 0: the same family two bytes earlier, report 01: query 01 80 08 50 41 08 08
//   03 <command>, remote mode 01 80 07 50 41 0E 08 02 E1 01 / 00; the answer 01 80 .. 50 49 ..., the command at 13, 01 (an
//   answer) or 02 (a notice) at 14, the length at 15, the level at 16 (HaloBattery's barracuda.py, MIT, from a capture of
//   Synapse on the headset on Windows: the page as is). A switched-off headset does not answer.
// - Kraken V4 (056C), usage page 0xFF14 usage 1: report 02, 02 00 60 00 00 00 04 00 00 80 <command> with the XOR of bytes 0
//   to 61 at 62; the answer 02 02 60 ... 80 80 <command> 01 01 <value>, its XOR of bytes 1 to 61 at 62 (a notice, 02 0A 60
//   ..., is none). The receiver answers one frame at a time, so only the level (0x21) is asked. vigil-watt's
//   RazerKrakenCodec.cs (MIT; measured on Windows by its contributor, the page as is: 02 02 60 00 00 00 05 00 80 80 21 01 01
//   3F .. 79, 63 %).
// Not taken: the Barracuda X 2022 (0552) speaks Airoha RACE behind an app-mode switch with no documented undo and gives only a
// voltage; 0536, which HaloBattery lists for PA after a report (its issue #256, still open) without an answer from it. The
// BlackShark V3 (057A), read at most every five minutes, is RazerBlackSharkV3.
[BrandFamilies]
public static class RazerHeadsets
{
    public const int Vendor = 0x1532;
    public static readonly int[] Status2020 = [0x0528, 0x051C];
    public static readonly int[] Pa = [0x0555, 0x0556, 0x0583];
    public const int BarracudaPro = 0x053A, KrakenV4 = 0x056C;
    private const byte Battery = 0x21;

    // ----- BlackShark V2 Pro 2020 and Nari: feature report 0xFF -----
    public static byte[] StatusRequest() => BrandFamilies.Frame(64, 0xFF, 0x0A, 0x00, 0xFD, 0x04, 0x12, 0xF1, 0x02, 0x05);
    private static readonly byte[] StatusPrefix = [0xFF, 0x0F, 0x05, 0xFE, 0x12, 0x04, 0x1F, 0x08, 0x05];
    private static readonly byte[] StalePrefix = [0xFF, 0x01, 0x00, 0xFE, 0x12, 0x04, 0x1F, 0x08, 0x05];
    public static BatteryRead? StatusBattery(byte[] r)
    {
        if (r.Length < 15 || !r.AsSpan(0, StatusPrefix.Length).SequenceEqual(StatusPrefix)) return null;
        int millivolts = r[12] << 8 | r[13];
        if (millivolts == 0) return new(null, Asleep: true);
        if (r[11] == 0x09) return new(null, Charging: true);
        return r[14] is > 0 and <= 100 ? new(r[14], Coarse: true) : null;
    }
    // The receiver's stale copy: no new answer from the headset.
    public static bool StatusStale(byte[] r) => r.Length >= 15 && r.AsSpan(0, StalePrefix.Length).SequenceEqual(StalePrefix);
    // One read's answers in turn: a stale copy at the first try may be an answer not there yet, at the second the headset is off.
    public static Func<byte[], BatteryRead?> StatusReads()
    {
        int stale = 0;
        return r => StatusBattery(r) ?? (StatusStale(r) && ++stale >= 2 ? new BatteryRead(null, Asleep: true) : null);
    }

    // ----- BlackShark V2 Pro 2023: PA on report 02 -----
    public static byte[] PaRemote(bool on) => BrandFamilies.Frame(64, 0x02, 0x80, 0x07, 0x00, 0x00, 0x50, 0x41, 0x0E, 0x00, 0x02, 0xE1, on ? (byte)1 : (byte)0);
    public static byte[] PaQuery(byte command) => BrandFamilies.Frame(64, 0x02, 0x80, 0x08, 0x00, 0x00, 0x50, 0x41, 0x08, 0x00, 0x03, command);
    public static BatteryRead? PaBattery(byte[] r)
        => r.Length >= 16 && r[0] == 0x02 && r[2] == 0x50 && r[3] == 0x49 && r[12] == Battery && r[13] == 0x01 && r[14] >= 1 && r[15] is > 0 and <= 100
            ? new(r[15]) : null;
    // The charging query's answer (command 0x2A): its one data byte non-zero while charging. Asked in the same remote session
    // right after the level, as HaloBattery's blackshark.py read_battery does.
    public const byte Charge = 0x2A;
    public static bool? PaCharging(byte[] r)
        => r.Length >= 16 && r[0] == 0x02 && r[2] == 0x50 && r[3] == 0x49 && r[12] == Charge && r[13] == 0x01 && r[14] >= 1 ? r[15] != 0 : null;

    // ----- Barracuda Pro: PA on report 01 -----
    public static byte[] BarracudaRemote(bool on) => BrandFamilies.Frame(64, 0x01, 0x80, 0x07, 0x50, 0x41, 0x0E, 0x08, 0x02, 0xE1, on ? (byte)1 : (byte)0);
    public static byte[] BarracudaQuery(byte command) => BrandFamilies.Frame(64, 0x01, 0x80, 0x08, 0x50, 0x41, 0x08, 0x08, 0x03, command);
    public static BatteryRead? BarracudaBattery(byte[] r)
        => r.Length >= 17 && r[0] == 0x01 && r[3] == 0x50 && r[4] == 0x49 && r[13] == Battery && r[14] is 0x01 or 0x02 && r[15] is > 0 and <= 32
            && r[16] is > 0 and <= 100 ? new(r[16]) : null;
    // Its charging answer, the same way (HaloBattery's barracuda.py: charging = payload[0]).
    public static bool? BarracudaCharging(byte[] r)
        => r.Length >= 17 && r[0] == 0x01 && r[3] == 0x50 && r[4] == 0x49 && r[13] == Charge && r[14] is 0x01 or 0x02 && r[15] is > 0 and <= 32 ? r[16] != 0 : null;

    // ----- Kraken V4: report 02 with an XOR -----
    public static byte[] KrakenQuery(byte command)
    {
        var frame = BrandFamilies.Frame(64, 0x02, 0x00, 0x60, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x80, command);
        byte xor = 0;
        for (int i = 0; i < 62; i++) xor ^= frame[i];
        frame[62] = xor;
        return frame;
    }
    public static BatteryRead? KrakenBattery(byte[] r)
    {
        if (r.Length != 64 || r[0] != 0x02 || r[1] != 0x02 || r[2] != 0x60 || r[10] != Battery || r[11] != 0x01 || r[12] != 0x01) return null;
        byte xor = 0;
        for (int i = 1; i < 62; i++) xor ^= r[i];
        return xor == r[62] && r[13] is > 0 and <= 100 ? new(r[13]) : null;
    }

    private static bool Takes(HidShape s)
    {
        if (s.Vendor != Vendor) return false;
        if (Status2020.Contains(s.Product))
            return (s.Product == 0x0528 ? s.UsagePage == 0xFF00 : HeadsetPages.Is(s, 0xFF00)) && s.Usage == 0x0001 && s.FeatureLength >= 64;
        if (Pa.Contains(s.Product)) return s.UsagePage is 0xFF00 or 0xFF14 && s.Usage == 0x0001 && s.OutputLength >= 64 && s.InputLength >= 64;
        if (s.Product == BarracudaPro) return s.UsagePage == 0xFF00 && s.Usage == 0x0000 && s.OutputLength >= 64 && s.InputLength >= 64;
        if (s.Product == KrakenV4) return s.UsagePage == 0xFF14 && s.Usage == 0x0001 && s.OutputLength >= 64 && s.InputLength >= 64;
        return false;
    }

    private static IReadOnlyList<BrandWay> Ways(HidShape s)
    {
        if (Status2020.Contains(s.Product))
            return [new BrandWay("razer-headset.status", WayTransport.Feature, StatusRequest(), StatusReads()) { GapMs = 150, Attempts = 2 }];
        if (Pa.Contains(s.Product))
            return [new BrandWay("razer-headset.pa", WayTransport.Output, PaQuery(Battery), PaBattery)
                { Before = [PaRemote(true), PaRemote(true)], After = [PaRemote(false)], GapMs = 35, TimeoutMs = 700, Then = PaQuery(Charge), Charging = PaCharging }];
        if (s.Product == BarracudaPro)
            return [new BrandWay("razer-headset.barracuda", WayTransport.Output, BarracudaQuery(Battery), BarracudaBattery)
                { Before = [BarracudaRemote(true)], After = [BarracudaRemote(false)], GapMs = 50, TimeoutMs = 900, Then = BarracudaQuery(Charge), Charging = BarracudaCharging }];
        if (s.Product == KrakenV4)
            return [new BrandWay("razer-headset.kraken", WayTransport.Output, KrakenQuery(Battery), KrakenBattery) { TimeoutMs = 1000 }];
        return [];
    }

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Razer Headset", Takes, Ways) { Kind = _ => DeviceKind.Headphones, Prefix = "razer-headset" },
    ];
}

namespace BrimDeck.Core;

// Sony PULSE Explore earbuds. Nothing is written: feature report B0 (64 bytes) is fetched and read. Two ways in, on Sony's
// shared vendor id 0x054C, so only these products and collections, as FerrisPulse measured them on Windows:
// - the PlayStation Link receiver 0ECC, usage page 0xFF01 usage 0x20, no output report. The receiver serves the PULSE Elite
//   as well, so the report's model field is checked: B0 50 01 at 0 to 2 and 11 00 at 29 and 30. Byte 55 holds the left bud's
//   level in its low nibble and the right's in its high nibble, steps 0 to 15 (percent (step * 100 + 7) / 15); bit 0 of byte
//   39 says the buds are linked, and bits 4 and 5 of byte 56 that the left and right are online. The reading is the lower of
//   the buds online; with none online (in the case, or the link down) the earbuds are asleep, the levels being the last ones
//   reported.
// - the charging case on USB 0ED0, usage page 0xFF13 usage 1, 62-byte output reports, product string "PULSE Explore": the
//   same report with 11 01 at 29 and 30, bits 4 and 5 of byte 56 saying the left and right bud are in the case. The reading
//   is the lower of the buds in the case, charging while one is below 100 %; the case's own battery is not in the report.
// From FerrisPulse's SonyDeviceHelper.PulseExplore.cs and docs/PULSE_EXPLORE.md (GPL; facts only; measured on a receiver and
// a case, levels DD read as 87 % against Sony's own panel). A zero step is not taken.
[BrandFamilies]
public static class SonyPulse
{
    public const int Vendor = 0x054C, Receiver = 0x0ECC, Case = 0x0ED0;
    public const byte Report = 0xB0;
    private const int Length = 64;

    private static int Percent(int step) => (step * 100 + 7) / 15;

    private static bool Header(byte[] r, byte model) => r.Length >= Length && r[0] == Report && r[1] == 0x50 && r[2] == 0x01 && r[29] == 0x11 && r[30] == model;

    public static BatteryRead? Wireless(byte[] r)
    {
        if (!Header(r, 0x00)) return null;
        bool linked = (r[39] & 0x01) != 0, left = linked && (r[56] & 0x10) != 0, right = linked && (r[56] & 0x20) != 0;
        if (!left && !right) return new(null, Asleep: true);
        int low = Math.Min(left ? Percent(r[55] & 0x0F) : 100, right ? Percent(r[55] >> 4) : 100);
        return low > 0 ? new(low, Coarse: true) : null;
    }

    public static BatteryRead? Usb(byte[] r)
    {
        if (!Header(r, 0x01)) return null;
        bool left = (r[56] & 0x10) != 0, right = (r[56] & 0x20) != 0;
        if (!left && !right) return new(null, Asleep: true);
        int low = Math.Min(left ? Percent(r[55] & 0x0F) : 100, right ? Percent(r[55] >> 4) : 100);
        return low > 0 ? new(low, Charging: low < 100, Coarse: true) : null;
    }

    private static bool Takes(HidShape s) => s.Vendor == Vendor && s.FeatureLength == Length && s.InputLength == Length && s.Product switch
    {
        Receiver => s.UsagePage == 0xFF01 && s.Usage == 0x0020 && s.OutputLength == 0,
        Case => s.UsagePage == 0xFF13 && s.Usage == 0x0001 && s.OutputLength == 62 && string.Equals(s.Name.Trim(), "PULSE Explore", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static IReadOnlyList<BrandWay> Ways(HidShape s) => s.Product == Receiver
        ? [new BrandWay("pulse.receiver", WayTransport.FeatureGet, [Report], Wireless) { FeatureLength = Length }]
        : [new BrandWay("pulse.case", WayTransport.FeatureGet, [Report], Usb) { FeatureLength = Length }];

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Sony PULSE", Takes, Ways) { Kind = _ => DeviceKind.Earbuds, Prefix = "pulse", DisplayName = _ => "PULSE Explore" },
    ];
}

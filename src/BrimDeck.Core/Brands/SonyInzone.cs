namespace BrimDeck.Core;

// Sony's INZONE headsets on their 2.4 GHz dongles, vendor 0x054C (shared with the PlayStation controllers and the PULSE 3D,
// which other readers take), so only these products: the INZONE H5 (0EBF, usage page 0xFF04 usage 2) and H9 II (0FA8, usage
// page 0xFF04 usage 1, interface 5), Sony's vendor HCI over HID on report 02. The battery GET is 02 0C 01 00 FC 08 96 C3 41
// 04 01 <transaction, little endian> <sum of bytes 6 to 12>; the transaction is 2 (the dongle's own notices carry 1). The
// answer is an HCI event: 02 <length> 04 FF <param length> 00 96 C3 <address, high nibble 1 = to the PC> 04 <10 = RET>
// <transaction> <charger> <percent>, the sum of bytes 5 to <length> at <length> + 1; the percent 0xFF is the headset offline.
// HeadsetControl's sony_inzone_protocol.hpp (GPL; facts only). No answer from a device is quoted there; the H5 and H9 II are
// listed as supported.
// Not read: the INZONE H9 (0E53) speaks the same commands over a serial port. The INZONE Buds (0EC2) send their battery only
// on their own, every three to five minutes while in use (HeadsetControl's pull request #505), which a read that listens for
// a second never catches; and no source names their collection. The PULSE Explore is SonyPulse.
[BrandFamilies]
public static class SonyInzone
{
    public const int Vendor = 0x054C, H5 = 0x0EBF, H9II = 0x0FA8;
    private const byte Transaction = 0x02;

    public static byte[] Request()
    {
        var frame = BrandFamilies.Frame(14, 0x02, 0x0C, 0x01, 0x00, 0xFC, 0x08, 0x96, 0xC3, 0x41, 0x04, 0x01, Transaction, 0x00);
        int sum = 0;
        for (int i = 6; i <= 12; i++) sum += frame[i];
        frame[13] = (byte)sum;
        return frame;
    }

    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 15 || r[0] != 0x02 || r[2] != 0x04 || r[3] != 0xFF || r[5] != 0x00 || r[6] != 0x96 || r[7] != 0xC3 || r[8] >> 4 != 0x1) return null;
        int length = r[1];
        if (length < 14 || length > 62 || length + 1 >= r.Length) return null;
        int sum = 0;
        for (int i = 5; i <= length; i++) sum += r[i];
        if ((byte)sum != r[length + 1]) return null;
        if (r[9] != 0x04 || r[10] is not (0x10 or 0xA0) || r[11] != Transaction || r[12] != 0x00) return null;
        if (r[14] == 0xFF) return new(null, Asleep: true);
        return r[14] is > 0 and <= 100 ? new(r[14], r[13] != 0) : null;
    }

    private static bool Takes(HidShape s) => s.Vendor == Vendor && HeadsetPages.Is(s, 0xFF04) && s.InputLength >= 64 && s.OutputLength >= 64 && s.Product switch
    {
        H5 => s.Usage == 0x0002,
        H9II => s.Usage == 0x0001 && s.Interface is 5 or -1,
        _ => false
    };

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Sony INZONE", Takes, _ => [new BrandWay("inzone.battery", WayTransport.Output, Request(), Battery) { TimeoutMs = 1000 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "inzone" },
    ];
}

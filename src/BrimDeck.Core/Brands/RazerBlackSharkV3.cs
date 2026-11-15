namespace BrimDeck.Core;

// The Razer BlackShark V3 on its 2.4 GHz dongle, 1532:057A (Razer's id is shared, so only this product), on its vendor
// collection: usage page 0xFF14 usage 1, interface 5, 64-byte frames with report id 02. It answers neither the PA frames of
// the 2023 BlackShark nor Razer's 90-byte feature reports. A frame: 02 at 0, the status at 1 (02 in a reply), transaction 0x60
// at 2, 4 + the argument count at 6, the direction at 9 (0x80 a query, 0x00 a wake), the class at 10 (0x21 the battery, 0x2A
// charging, 0x02 the first wake), 0x00 (a request), 0x01 (a reply) or 0x02 (a push) at 11, the argument count at 12, the value
// at 13, and the XOR of bytes 0 to 61 at 62. A value of 0xFF is "not ready" (the link not up yet). Synapse sends two wake
// frames (class 02, then 2A, direction 00) and the charging query before the battery query, and so does this read; the
// answers come as input reports. The charging answer is not read (it comes before the battery query, and the battery's read
// starts with the input reports flushed), so the reading has no charging state. A link-up push (class 0x20) is no reading.
// From HaloBattery's blackshark_v3.py (MIT; after a Synapse capture of this dongle on Windows showing 97 %, so the page as
// is: 02 02 60 00 00 00 05 00 00 80 21 01 01 61 ... A5). The dongle resets its radio link when asked often (OpenRazer's
// daemon, asking every 2 seconds, kept resetting it; the driver's author advises minutes between queries): one read in five
// minutes, the last reading standing between, and a single try. A switched-off headset does not answer (Windows gives no
// other sign of it).
[BrandFamilies]
public static class RazerBlackSharkV3
{
    public const int Vendor = 0x1532, Dongle = 0x057A;
    public const byte Battery = 0x21, Charging = 0x2A, Init = 0x02;
    private const byte Query = 0x80, Wake = 0x00;

    public static byte[] Frame(byte command, byte direction = Query)
    {
        var frame = BrandFamilies.Frame(64, 0x02, 0x00, 0x60, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, direction, command);
        byte xor = 0;
        for (int i = 0; i < 62; i++) xor ^= frame[i];
        frame[62] = xor;
        return frame;
    }

    public static BatteryRead? Level(byte[] r)
    {
        if (r.Length < 63 || r[0] != 0x02 || r[1] != 0x02 || r[2] != 0x60 || r[10] != Battery || r[11] is not (0x01 or 0x02) || r[12] < 1) return null;
        byte xor = 0;
        for (int i = 0; i < 62; i++) xor ^= r[i];
        return xor == r[62] && r[13] is > 0 and <= 100 ? new(r[13]) : null;
    }

    private static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == Dongle && s.UsagePage == 0xFF14 && s.Usage == 0x0001
        && s.Interface is 5 or -1 && s.OutputLength >= 64 && s.InputLength >= 64;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Razer BlackShark V3", Takes,
            _ => [new BrandWay("blackshark-v3.battery", WayTransport.Output, Frame(Battery), Level)
            {
                Before = [Frame(Init, Wake), Frame(Charging, Wake), Frame(Charging)], GapMs = 50, TimeoutMs = 1000, Attempts = 1, MinIntervalMs = 300_000
            }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "razer-headset" },
    ];
}

namespace BrimDeck.Core;

// The Lofree Flow Lite84 on its 2.4 GHz receiver. The receiver borrows Apple's ids, 05AC:024F, which Apple's own wired
// Aluminium Keyboard carries too, and other makers' receivers as well; that keyboard has no 0xFF02 collection. So only the
// receiver's control collection is asked: usage page 0xFF02, usage 2, interface 1, 17-byte output reports with report id 8,
// product string "2.4G Wireless Receiver" (the receiver's dump). The read is Lofree's Control HUB's, in Compx's 17-byte frame:
// [0x08, command, 0, 0, 0, 0x80 (a keyboard command), zeros, checksum], the checksum making all seventeen bytes sum to 0x55.
// Command 0x03 asks whether the keyboard is online and is asked first, as the driver does: its answer [0x08, 0x03, status 0,
// ..., 1 at 6 online, checksum] lets the read go on, and 0 there is the keyboard off or asleep (HaloBattery then takes no
// level). Command 0x04 is answered by [0x08, 0x04, status 0, ..., percent at 6, 1 at 7 while charging, millivolts at 8 and
// 9, checksum]. A reply counts only with the status 0 and its checksum right; the receiver also sends frames of its own on
// the collection. From HaloBattery's lofree.py (MIT; checked on a Flow Lite84: level and charging).
[BrandFamilies]
public static class Lofree
{
    public const int Vendor = 0x05AC, Receiver = 0x024F;
    public const string ReceiverName = "2.4G Wireless Receiver";

    public static byte[] Request(byte command)
    {
        var frame = BrandFamilies.Frame(17, 0x08, command, 0x00, 0x00, 0x00, 0x80);
        int sum = 0; for (int i = 0; i < 16; i++) sum += frame[i];
        frame[16] = (byte)(0x55 - sum);
        return frame;
    }
    // A reply to the command: status 0, and the seventeen bytes summing to 0x55.
    private static bool Reply(byte[] r, byte command)
    {
        if (r.Length < 17 || r[0] != 0x08 || r[1] != command || r[2] != 0x00) return false;
        int sum = 0; for (int i = 0; i < 17; i++) sum += r[i];
        return (byte)sum == 0x55;
    }
    // The online answer: true online, false off or asleep, null for any other frame (the request read back too, whose 0 at 6
    // would otherwise say asleep).
    public static bool? Online(byte[] r) => Reply(r, 0x03) && !r.AsSpan(0, 17).SequenceEqual(Request(0x03))
        ? r[6] switch { 1 => true, 0 => false, _ => null } : null;
    public static BatteryRead? Battery(byte[] r) => Reply(r, 0x04) && r[6] is > 0 and <= 100 ? new(r[6], r[7] == 1) : null;

    private static bool Page(int actual, int page) => actual == page || actual == ((page & 0xFF) << 8 | page >> 8);
    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && shape.Product == Receiver && Page(shape.UsagePage, 0xFF02)
        && shape.Usage == 0x0002 && shape.Interface == 1 && shape.OutputLength == 17 && shape.InputLength >= 17
        && string.Equals(shape.Name.Trim(), ReceiverName, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Lofree", Takes,
            _ => [new BrandWay("lofree.flow", WayTransport.Output, Request(0x04), Battery) { TimeoutMs = 800, Gate = Request(0x03), Gated = Online }])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "lofree", DisplayName = _ => "Lofree Flow Lite84" },
    ];
}

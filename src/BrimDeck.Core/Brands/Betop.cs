namespace BrimDeck.Core;

// BETOP (北通) controllers on their 2.4 GHz receivers, vendor 0x20BC (BETOP's own: the Kunpeng 20 on 20BC:5191 and the KP40D on
// 20BC:515C answer the same status read, so every 20BC receiver is asked it rather than a list of models). The receiver has
// four collections, told apart by their usage: 3 the vendor one, 2 the mouse, 6 the keyboard and 5 the gamepad the controller
// is to Windows. Only the vendor one is asked; its page is written 0xFF00 in the HID specification's order, but Windows
// reports it as 0x00FF (both sources measured that), so either is taken. It takes output report 0x02 with command 0x15, zeros
// after (the report's own length); the answer is input report 02 15 <percent> 00 <state>, the state 0x50 on battery and 0x51
// charging (its low nibble set while charging). A state whose high nibble is not 5 is no answer (the request read back is
// 02 15 00 00 00), and neither is a level of 0 (vigil-watt's BETOP notes, MIT: the receiver's placeholder while not ready).
// From oitsukiii/betop-battery (MIT; Kunpeng 20, checked against BETOP's own client on Windows 11) and HaloBattery's
// beitong.py (MIT; KP40D, from a capture of the official client). Command 0x22 writes the settings and is never sent.
[BrandFamilies]
public static class Betop
{
    public const int Vendor = 0x20BC;

    public static byte[] Request() => BrandFamilies.Frame(2, 0x02, 0x15);
    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 5 && r[0] == 0x02 && r[1] == 0x15 && r[3] == 0x00 && (r[4] & 0xF0) == 0x50 && r[2] is > 0 and <= 100 ? new(r[2], (r[4] & 0x0F) != 0) : null;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("BETOP", shape => shape.Vendor == Vendor && shape.UsagePage is 0x00FF or 0xFF00 && shape.Usage == 0x0003 && shape.InputLength >= 5,
            _ => [new BrandWay("betop.status", WayTransport.Output, Request(), Battery) { TimeoutMs = 800 }])
        { Kind = _ => DeviceKind.Controller, Prefix = "betop" },
    ];
}

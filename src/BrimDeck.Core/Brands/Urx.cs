namespace BrimDeck.Core;

// The URX Core75 on its 2.4 GHz receiver, 36B0:3002 ("URX Core75 Wireless", measured in HaloBattery #245). That receiver id is
// not URX's alone: mousedock (MIT) reads PMO's keyboards (WAVE 75, Aurora 80, P75, PMO80) behind 36B0:3002 with a protocol of
// their own, and VIA's custom values mean what each firmware makes them mean. So only the pair whose product string names URX
// is asked, and only on the keyboard's VIA raw HID collection (usage page 0xFF60, usage 0x61, interface 1, 33-byte reports
// with report id 0, as #245's probe listed and wrote them). The read is URX HUB's: VIA's
// custom get (0x08) on channel 0, value 7; the answer echoes 08 00 07 and gives the percent next. No charging state is sent.
// A firmware that does not know the command answers FF ..., and the request read back has a zero level: neither is a reading.
// From HaloBattery's urx.py (MIT; checked against URX HUB on the keyboard: 76 %).
[BrandFamilies]
public static class Urx
{
    public const int Vendor = 0x36B0, Receiver = 0x3002;

    public static byte[] Request() => BrandFamilies.Frame(33, 0x00, 0x08, 0x00, 0x07);
    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 4 && r[0] == 0x08 && r[1] == 0x00 && r[2] == 0x07 && r[3] is >= 1 and <= 100 ? new(r[3]) : null;

    private static bool Page(int actual, int page) => actual == page || actual == ((page & 0xFF) << 8 | page >> 8);
    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && shape.Product == Receiver
        && shape.Name.Contains("URX", StringComparison.OrdinalIgnoreCase)
        && Page(shape.UsagePage, 0xFF60) && shape.Usage == 0x0061 && shape.Interface == 1 && shape.InputLength == 33 && shape.OutputLength == 33;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("URX", Takes, _ => [new BrandWay("urx.via", WayTransport.Output, Request(), Battery) { TimeoutMs = 800, StripZero = true }])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "urx", DisplayName = _ => "URX Core75" },
    ];
}

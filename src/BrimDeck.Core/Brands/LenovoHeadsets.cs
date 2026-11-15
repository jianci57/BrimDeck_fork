namespace BrimDeck.Core;

// The Lenovo Wireless VoIP Headset's receiver (17EF:A07D). 0x17EF is shared by all of Lenovo's devices, so only this product,
// on its vendor collection (usage page 0xFF07 usage 0x0222, interface 3): the status command is feature report 24 01 (61
// bytes, nothing after the command), answered by input report 27 01 <error> ... with the percent at 7 and no charging state;
// a non-zero error byte is the headset not connected to the receiver. The other commands carry 01 and a value after the
// command and set it; they are never sent. HeadsetControl's lenovo_wireless_voip.hpp (GPL; facts only; no answer quoted).
[BrandFamilies]
public static class LenovoHeadsets
{
    public const int Vendor = 0x17EF, VoipReceiver = 0xA07D;

    public static byte[] Request() => BrandFamilies.Frame(61, 0x24, 0x01);
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 8 || r[0] != 0x27 || r[1] != 0x01) return null;
        if (r[2] != 0x00) return new(null, Asleep: true);
        return r[7] is > 0 and <= 100 ? new(r[7]) : null;
    }

    private static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == VoipReceiver && HeadsetPages.Is(s, 0xFF07) && s.Usage == 0x0222
        && s.Interface is 3 or -1 && s.InputLength >= 8;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Lenovo Headset", Takes, _ => [new BrandWay("lenovo-headset.status", WayTransport.FeatureListen, Request(), Battery) { TimeoutMs = 1000 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "lenovo-headset" },
    ];
}

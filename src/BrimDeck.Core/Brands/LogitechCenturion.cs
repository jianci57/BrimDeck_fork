namespace BrimDeck.Core;

// Logitech G PRO X 2 LIGHTSPEED headset on its receiver (046D:0AF7), whose battery is not on HID++: its vendor collection
// (usage page 0xFFA0, usage 1, 64-byte reports) takes output report 0x51 with the fixed legacy battery request
// 51 08 00 03 1A 00 03 00 04 0A, zeros to 64 bytes, and answers 51 0B .. with 04 at 8, the percent at 10 and the state at 12
// (0 on battery, 2 charging). 51 03 is an acknowledgement and 51 05 a power notice, neither a reading; a power notice with 00
// at 6 says the headset is off. From vigil-watt's CenturionCodec (MIT; measured on the receiver on Windows, so the page as
// is: 45 % on battery, 43 % charging), HeadsetControl's logitech_gpro_x2_lightspeed.hpp (GPL; facts only: the legacy request,
// the acknowledgement and the power notices) and HaloBattery's logitech_centurion.py (MIT; the same). Only this one product:
// Logitech's id carries HID++ everywhere else.
[BrandFamilies]
public static class LogitechCenturion
{
    public const int Vendor = 0x046D, Product = 0x0AF7;

    public static byte[] Request() => BrandFamilies.Frame(64, 0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A);
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length >= 7 && r[0] == 0x51 && r[1] == 0x05 && r[6] == 0x00) return new(null, Asleep: true);
        return r.Length >= 13 && r[0] == 0x51 && r[1] == 0x0B && r[8] == 0x04 && r[10] <= 100 ? new(r[10], r[12] == 0x02) : null;
    }

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Logitech Centurion", shape => shape.Vendor == Vendor && shape.Product == Product && shape.UsagePage == 0xFFA0 && shape.Usage == 0x0001
                && shape.InputLength >= 64 && shape.OutputLength >= 64,
            _ => [new BrandWay("logitech.centurion", WayTransport.Output, Request(), Battery) { TimeoutMs = 800 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "centurion", DisplayName = shape => shape.Name.Length > 0 ? null : "PRO X 2 LIGHTSPEED" },
    ];
}

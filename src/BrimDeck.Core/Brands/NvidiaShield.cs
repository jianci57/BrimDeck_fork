namespace BrimDeck.Core;

// The NVIDIA SHIELD controller (2017, 0955:7214) on its cable. Vendor 0x0955 is NVIDIA's, and only this product's read was
// checked on hardware (the 2015 controller, 0955:7210, which SDL sends the same commands to, is not asked), and only on the
// collection that declares output report 0x04 (Declares). The battery command is output report 04 07 00 (command 7,
// transaction 0), zeros to 33 bytes; the answer is input report 03 07 00 with the capacity in percent at byte 14 (after the
// average, minimum, boot and present voltages and the thermistor). Layout from Linux's hid-nvidia-shield.c (facts only), the
// byte and the request from HaloBattery's shield.py (MIT; checked on a v01.04 controller over USB and Bluetooth on Windows 11).
// SDL_hidapi_shield.c (zlib) sends the same command but takes a step × 20 from byte 5, a byte the driver leaves unnamed
// (reserved); the percent at byte 14 was read on hardware and is followed. Charging is another command (0x3A) and is not
// asked.
[BrandFamilies]
public static class NvidiaShield
{
    public const int Vendor = 0x0955, Product = 0x7214;

    // Not a keyboard, mouse or consumer collection of the controller's.
    public static bool Collection(HidShape shape) => shape.Vendor == Vendor && shape.Product == Product
        && !(shape.UsagePage == 0x0001 && shape.Usage is 0x0002 or 0x0006) && shape.UsagePage != 0x000C && shape.InputLength >= 15;

    public static byte[] Request() => BrandFamilies.Frame(33, 0x04, 0x07, 0x00);
    public static BatteryRead? Battery(byte[] r)
    {
        // 03 07 and the transaction number the request sent (0): another program's answer carries its own.
        if (r.Length < 15 || r[0] != 0x03 || r[1] != 0x07 || r[2] != 0x00 || r[14] > 100) return null;
        // An answer with every field zero (no voltage at all) is none.
        if (r.AsSpan(3, 12).IndexOfAnyExcept((byte)0) < 0) return null;
        return new(r[14]);
    }

    public static IEnumerable<BrandFamily> Families =>
    [
        new("NVIDIA", Collection, _ => [new BrandWay("shield.battery", WayTransport.Output, Request(), Battery) { TimeoutMs = 800 }])
        { Kind = _ => DeviceKind.Controller, Prefix = "shield", DisplayName = _ => "NVIDIA SHIELD Controller" },
    ];
}

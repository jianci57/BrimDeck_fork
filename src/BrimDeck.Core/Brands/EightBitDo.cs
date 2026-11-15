namespace BrimDeck.Core;

// 8BitDo controllers in their own (D-input) mode on a cable or dongle, vendor 0x2DC8 (8BitDo's own), only the products SDL's
// 8BitDo driver reads the level of (HIDAPI_Driver8BitDo_IsSupportedDevice): SF30 Pro 6000/6100, SN30 Pro 6001/6101, Pro 2
// 6003/6006, Pro 3 6009, Ultimate 2 Wireless 6012 (its 34-byte reports; the 12-byte ones of firmware 1.02 are too short here)
// and Ultimate 3 202F. The Ultimate 2C (301B) sends 11-byte reports without a level (SDL), and no source says where 8BitDo's
// other products keep a level, so none of them is listened to. Nothing is sent: the
// gamepad collection's input reports are only listened to. The level is in the enhanced report, id 0x04 on USB: byte 14,
// bits 0 to 6 the percent and bit 7 charging (SDL_hidapi_8bitdo.c, zlib). The Pro 3 (2DC8:6009) sends that report all the
// time; the others only after a program (Steam, SDL) switched them with feature report 0x06, which is never sent here: it
// left a Pro 2 without input in DirectInput games until it was turned off (HaloBattery #101). A 0 in byte 14 is the padding
// of the ordinary report, not an empty battery, so it is no reading (HaloBattery's eightbitdo.py, MIT; a Pro 3 read on
// hardware). Over Bluetooth the report is 0x01, but the brand families are not read over Bluetooth.
[BrandFamilies]
public static class EightBitDo
{
    public const int Vendor = 0x2DC8;

    public static readonly int[] Pads = [0x6000, 0x6100, 0x6001, 0x6101, 0x6003, 0x6006, 0x6009, 0x6012, 0x202F];

    public static bool Collection(HidShape shape) => shape.Vendor == Vendor && Pads.Contains(shape.Product) && shape.UsagePage == 0x0001
        && shape.Usage is 0x0004 or 0x0005 && shape.InputLength >= 15;

    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 15 || r[0] != 0x04) return null;
        int level = r[14] & 0x7F;
        return level is >= 1 and <= 100 ? new(level, (r[14] & 0x80) != 0) : null;
    }

    public static IEnumerable<BrandFamily> Families =>
    [
        new("8BitDo", Collection, _ => [new BrandWay("8bitdo.report", WayTransport.Listen, [], Battery) { TimeoutMs = 500, Attempts = 1 }])
        { Kind = _ => DeviceKind.Controller, Prefix = "8bitdo" },
    ];
}

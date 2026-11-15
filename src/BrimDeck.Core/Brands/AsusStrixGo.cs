namespace BrimDeck.Core;

// The ROG Strix Go 2.4 headset (0B05:18D6 on its dongle, 18D7 on its USB-C cable). 0x0B05 is ASUS's id for every ROG device,
// which Asus and the Omni reader take by product, so only these two: feature report 0xFF on the vendor collection with a
// 64-byte feature report, set FF 08 00 FD 04 12 F1 03 52 01 and got back 35 ms later. A live answer has 1B at 1, the charge
// state at 9 (0A charging, 03 on battery) and the battery's voltage in mV at 11 and 12 (little endian); FF AA at 1 is a
// packet the firmware does not know, and anything else without the 1B is the headset off or gone: no reading, except bytes
// 1 to 3 all zero, which HaloBattery reads as the headset off, asleep or disconnected (asleep here). The level is the quarter
// step Armoury Crate shows for the voltage (100 from 4096 mV, 75 from 3800, 50 from 3700, 25 from 3500, else 10); while charging
// the charger's ~160 mV lift is taken off below 4100 mV first. On the cable the headset always charges. As HaloBattery's
// asus.py (MIT) reads it, from G-Helper's reading of the model corrected on the reporter's headset (3743 mV at 50 %, 3831 mV
// at 75 %, against Armoury Crate).
[BrandFamilies]
public static class AsusStrixGo
{
    public const int Vendor = 0x0B05, Dongle = 0x18D6, Cable = 0x18D7;

    public static byte[] Request() => BrandFamilies.Frame(64, 0xFF, 0x08, 0x00, 0xFD, 0x04, 0x12, 0xF1, 0x03, 0x52, 0x01);

    public static Func<byte[], BatteryRead?> Battery(bool cable) => r =>
    {
        if (r.Length < 13 || r[0] != 0xFF) return null;
        if (r[1] == 0x00 && r[2] == 0x00 && r[3] == 0x00) return new(null, Asleep: true);
        if (r[1] != 0x1B) return null;
        int millivolts = r[11] | r[12] << 8;
        if (millivolts is < 3000 or > 4500) return null;
        bool charging = cable || r[9] == 0x0A;
        if (r[9] == 0x0A && millivolts < 4100) millivolts -= 160;
        int level = millivolts >= 4096 ? 100 : millivolts >= 3800 ? 75 : millivolts >= 3700 ? 50 : millivolts >= 3500 ? 25 : 10;
        return new(level, charging, Coarse: true);
    };

    private static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product is Dongle or Cable && HeadsetPages.Vendor(s) && s.FeatureLength >= 64;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("ASUS Headset", Takes, s => [new BrandWay("asus-headset.status", WayTransport.Feature, Request(), Battery(s.Product == Cable)) { GapMs = 35 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "asus-headset", DisplayName = _ => "ROG Strix Go 2.4" },
    ];
}

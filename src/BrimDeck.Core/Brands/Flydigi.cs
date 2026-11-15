namespace BrimDeck.Core;

// Flydigi (飞智) controllers on their dongles and cables, vendor 0x37D7 (Flydigi's own): every pad on the vendor collection
// below, not a list of models (SDL's driver names the Vader 5 Pro 2401, Apex 5 2501 and Apex 6 2502; later pads on the same
// protocol are read too). Not the CD2 charging dock (37D7:6001, usage page 0xFFA0 usage 1 as well, no report ids): it answers
// command 1 with its own heartbeat, whose bytes would be read as a pad's battery (OpenFlydigi's PROTOCOL.md, CC0).
// The pad keeps its own protocol
// on the vendor collection usage page 0xFFA0, usage 1 (measured on Windows in HaloBattery #191: the Vader 5 Pro's interface 1;
// 0xA0FF is taken too, in case Windows reports the page byte-swapped). The read is the device info request 5A A5 01 02 00,
// sent as output report 0x03 where the collection numbers
// its reports (Apex 5) and as unnumbered report 0x00 where it does not (Vader 5, Apex 6: SDL zeroes the id for them). The
// answer is an input report 5A A5 01 ..., the report id first where there is one; byte 5 of it (counted from 5A) is the
// device id, byte 11 the battery: high nibble 0 on battery, 1 charging, 2 charged, low nibble the level step. The step is
// × 20 (0 to 5) on every model but the Apex 6, where SDL takes it × 10 (0 to 10): SDL_hidapi_flydigi.c (zlib,
// HandleInfoResponse) decides by the device id (149, 150, 152 are the Apex 6) and HaloBattery's flydigi.py (MIT, checked on
// a Vader 5 Pro) agrees on × 20 for the rest (OpenFlydigi measured 05 there on an Apex 5 on its dongle). The seven steps
// (0 to 6) of wutong233/flydigi-battery (MIT) are another channel's, the Space Station service's pipe, not this byte. Coarse
// either way. SDL's acquire command (0x1C) takes the pad over and is never sent; neither is anything else.
[BrandFamilies]
public static class Flydigi
{
    public const int Vendor = 0x37D7;
    public const int Apex6 = 0x2502;
    private static readonly byte[] Info = [0x5A, 0xA5, 0x01, 0x02, 0x00];
    private static readonly HashSet<int> Apex6Ids = [149, 150, 152];

    // Flydigi's products on 0x37D7 that are not pads.
    public static readonly int[] NotPads = [0x6001];
    public static bool Pad(int product) => !NotPads.Contains(product);

    public static bool Collection(HidShape shape) => shape.Vendor == Vendor && Pad(shape.Product) && shape.UsagePage is 0xFFA0 or 0xA0FF
        && shape.Usage == 0x0001 && shape.InputLength >= 32;

    // The request as the collection takes it: report 0x03 where it declares that id, else unnumbered.
    public static byte[] Request(bool numbered) => [numbered ? (byte)0x03 : (byte)0x00, .. Info];

    public static BatteryRead? Battery(byte[] r, bool apex6)
    {
        int at = r.Length > 0 && r[0] == 0x5A ? 0 : 1;
        if (r.Length - at < 31 || r[at] != 0x5A || r[at + 1] != 0xA5 || r[at + 2] != 0x01) return null;
        // The request read back carries no device id (its byte 5 is padding).
        if (r[at + 5] == 0) return null;
        bool tens = apex6 || Apex6Ids.Contains(r[at + 5]);
        int state = r[at + 11] >> 4, step = r[at + 11] & 0x0F;
        if (state > 2 || step > (tens ? 10 : 5)) return null;
        return new(state == 2 ? 100 : step * (tens ? 10 : 20), state is 1 or 2, Coarse: true);
    }

    // Windows.Gaming.Input's report for a Flydigi pad is the constant remaining = full = 1000 mWh whatever the battery holds
    // (HaloBattery #191: it said full for a Vader 4 Pro flashing low, and the same for a Vader 5 Pro on its dongle). The Vader 4
    // Pro shows up under Microsoft's Xbox 360 id 045E:028E; Windows.Gaming.Input names it "Xbox 360 Controller for Windows",
    // and only the HID product string of its 045E:028E gamepad collection names Flydigi ("Controller (Flydigi VADER4)" in the
    // diagnostics of #191), so that is the name asked here, as HaloBattery's xinput.py does. Such a report is no reading;
    // another vendor's 1000 of 1000 can be a full battery and is kept (HaloBattery's xinput.py, #216 review).
    public const int MicrosoftVendor = 0x045E, Xbox360Product = 0x028E;
    public static bool WindowsPlaceholder(int vendor, int product, string? hidName, int? remaining, int? full)
        => remaining == 1000 && full == 1000 && (vendor == Vendor || vendor == MicrosoftVendor && product == Xbox360Product
            && hidName is { } text && (text.Contains("flydigi", StringComparison.OrdinalIgnoreCase) || text.Contains("vader", StringComparison.OrdinalIgnoreCase)));
    // A pad this family reads over HID: its Windows.Gaming.Input entry would list it a second time.
    public static bool ReadOverHid(int vendor, int product) => vendor == Vendor && Pad(product);

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Flydigi", Collection, shape =>
        {
            bool numbered = shape.OutputIds.Contains(0x03);
            bool apex6 = shape.Product == Apex6;
            return [new BrandWay("flydigi.info", WayTransport.Output, Request(numbered), r => Battery(r, apex6)) { TimeoutMs = 600 }];
        })
        { Kind = _ => DeviceKind.Controller, Prefix = "flydigi" },
    ];
}

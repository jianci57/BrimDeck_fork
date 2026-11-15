namespace BrimDeck.Core;

// Jabra (GN), vendor 0x0B0E: GN's own id, so every collection of the brand that has the shape is asked. The GN Protocol on
// the vendor collection (usage page 0xFF00, usage 1 on the Link 380 and the Evolve2 65 Flex, 5 on the Link 390), report 05
// both ways, 64 bytes: a GET is 05 <to> 00 <sequence> <0x40 | 6> <group> <command>; the answer 05 00 <from> <sequence>
// <0xC0 | length> <group> <command> <payload>, the length counting from byte 1 to the payload's end; group 0xFE is a refusal,
// its reason in place of the command: F3 the headset offline (off or out of range), F5 nothing at that address. The battery
// is group 0x12 command 0x02: <flags> <percent> ..., bit 0 of the flags set while charging. The headset is at 0x04 behind a
// Link dongle and at 0x08 on its own USB cable, so both are asked (the dongle's first where it is one). A dongle's F3 is the
// headset asleep; any other refusal is no reading (the other address may be the headset's). From HaloBattery's jabra.py (MIT;
// checked on a Link 380, 0B0E:24C7, with an Evolve2 65 on Windows: battery 04 15, 21 %) and HeadsetControl's
// jabra_gnp_protocol.hpp (GPL; facts only: the 0x08 address, the refusal reasons and the Link 390's usage 5, verified on Linux
// and macOS).
[BrandFamilies]
public static class Jabra
{
    public const int Vendor = 0x0B0E;
    public const byte Dongle = 0x04, Usb = 0x08;
    private const byte Sequence = 0x20;
    // Products whose headset is on the cable itself (the Evolve2 65 Flex); the rest are asked through a dongle first.
    public static readonly int[] OnUsb = [0x2519];

    public static byte[] Request(byte to) => BrandFamilies.Frame(7, 0x05, to, 0x00, Sequence, 0x46, 0x12, 0x02);

    public static Func<byte[], BatteryRead?> Battery(byte from) => r =>
    {
        if (r.Length < 9 || r[0] != 0x05 || r[1] != 0x00 || r[2] != from || r[3] != Sequence || (r[4] & 0xC0) != 0xC0) return null;
        if (r[5] == 0xFE) return from == Dongle && r[6] == 0xF3 ? new BatteryRead(null, Asleep: true) : null;
        int length = r[4] & 0x3F;
        if (r[5] != 0x12 || r[6] != 0x02 || length < 8 || length + 1 > r.Length) return null;
        return r[8] is > 0 and <= 100 ? new(r[8], (r[7] & 0x01) != 0) : null;
    };

    private static bool Takes(HidShape s) => s.Vendor == Vendor && HeadsetPages.Is(s, 0xFF00) && s.Usage is 0x0001 or 0x0005
        && s.OutputLength >= 64 && s.InputLength >= 64;

    private static IReadOnlyList<BrandWay> Ways(HidShape s)
    {
        BrandWay dongle = new("jabra.dongle", WayTransport.Output, Request(Dongle), Battery(Dongle)) { TimeoutMs = 800 },
            usb = new("jabra.usb", WayTransport.Output, Request(Usb), Battery(Usb)) { TimeoutMs = 800 };
        return OnUsb.Contains(s.Product) ? [usb, dongle] : [dongle, usb];
    }

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Jabra", Takes, Ways) { Kind = _ => DeviceKind.Headphones, Prefix = "jabra" },
    ];
}

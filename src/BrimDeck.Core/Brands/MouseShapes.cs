namespace BrimDeck.Core;

// What the mouse families share: matching a documented collection, and the 0x83 feature exchange two of them speak.
public static class MouseShapes
{
    // A usage page or usage as documented, or with its two bytes swapped: Windows (HidP_GetCaps) reports some vendor pages
    // byte-swapped (a descriptor's 0xFF00 read back as 0x00FF), so a page a source read from the descriptor rather than
    // measured on Windows is accepted in either order.
    public static bool Is(int value, int documented) => value == documented || value == Swap(documented);
    private static int Swap(int value) => (value & 0xFF) << 8 | (value >> 8 & 0xFF);
    public static bool Collection(HidShape shape, int page, int usage) => Is(shape.UsagePage, page) && Is(shape.Usage, usage);
    // Any vendor-defined page (0xFF00 and up), in either byte order.
    public static bool VendorPage(HidShape shape) => shape.UsagePage >= 0xFF00 || (shape.UsagePage & 0xFF) == 0xFF;

    // The 0x83 feature exchange (Sinowealth's framed query, the same envelope the existing WLmouse, G-Wolves and LAMZU reader
    // uses): feature report 0, 64 bytes, 00 00 02 02 00 83 (target 2 the mouse, length 2, bank 0, register 0x83). Read back
    // with the report id first: [1] status, [3..5] target, length and bank (0), [6] 0x83, [7] charging (1; Glorious's monitors
    // also see 2 and 3 on the cable once charged), [8] percent. Status: A1 answered; A4 the receiver cannot find the mouse (asleep
    // or off); A0 busy or waking up and A2 failed or waking (the sources differ; a waking mouse may answer A2 with 0 % first),
    // A3 busy: only A1 is a reading. A percent of 0 (Glorious CORE shows it as 1 %) is taken as no reading. The request read
    // back has 00 where the status goes.
    public static byte[] Request83() => BrandFamilies.Frame(65, 0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x83);
    public static BatteryRead? Battery83(byte[] r)
    {
        if (r.Length < 9 || r[0] != 0x00 || r[5] != 0x00 || r[6] != 0x83) return null;
        if (r[1] == 0xA4) return new(null, Asleep: true);
        return r[1] == 0xA1 && r[8] is > 0 and <= 100 ? new(r[8], r[7] == 1) : null;
    }
}

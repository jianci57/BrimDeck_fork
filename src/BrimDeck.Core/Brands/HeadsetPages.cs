namespace BrimDeck.Core;

// Usage pages as the headset families match them. Windows (HidP_GetCaps) reports some vendor pages with their two bytes
// swapped (BETOP's 0xFF00 comes back as 0x00FF), and most sources give a page without saying whether it was read on Windows
// or from the descriptor, so both orders are accepted.
internal static class HeadsetPages
{
    public static bool Is(HidShape shape, int page) => shape.UsagePage == page || shape.UsagePage == ((page & 0xFF) << 8 | page >> 8);
    // A vendor-defined page (0xFF00 to 0xFFFF), in either byte order; never a standard page such as 0x01 or 0x0C.
    public static bool Vendor(HidShape shape) => shape.UsagePage >> 8 == 0xFF || (shape.UsagePage & 0xFF) == 0xFF;
}

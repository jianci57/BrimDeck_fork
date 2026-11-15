namespace BrimDeck.Core;

// Ryunix Kyu Pro MX1, the Amkette XS series and the EWEADN X23 Pro, one OEM firmware on Elan's shared vendor id 0x04F3, so
// only its products: 026F the receiver, 026E the cable (026D is the XS's Bluetooth id, whose collections are not opened).
// Nothing is sent: the configuration collection (usage page 0x0A, usage 0xC7 on the MX1 and 0 on the XS and X23 Pro)
// carries input report 0x04 with the mouse's status, 04 <active 0/1> <stage> <polling code> <percent> <battery status> ...,
// the status 0 discharging, 1 charging, 2 full on the cable. Active 0 is the receiver reporting a mouse that is not
// connected (MouseDock reads it as offline): the mouse asleep or off, its level not a reading. A polling code outside the
// firmware's table, a status above 2 or a percent above 100 is another report. The report is heard only when the mouse
// sends it: the MX1's contributed reports and MouseDock's X23 Pro reader take it as sent unasked, while Amkette's
// configurator asks for it by reading feature report 0x05, which this read does not do (a pass that hears nothing has no
// reading). From OpenMouse's Ryunix and Amkette modules (AGPL; facts only: a capture on a wired XS Flow 2 Speed,
// 04 01 02 01 64 02 00 00 01 20, 100 % full, and the MX1's contributed reports) and MouseDock's X23 Pro reader (MIT).
[BrandFamilies]
public static class RyunixMice
{
    public const int Vendor = 0x04F3;
    public static readonly int[] Products = [0x026E, 0x026F];
    private static readonly int[] PollingCodes = [0x08, 0x04, 0x02, 0x01, 0x11, 0x12, 0x14];

    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 7 || r[0] != 0x04 || r[1] > 1 || !PollingCodes.Contains(r[3]) || r[5] > 2 || r[4] > 100) return null;
        if (r[1] == 0) return new(null, Asleep: true);
        return r[4] > 0 ? new(r[4], r[5] == 1) : null;
    }

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && Products.Contains(shape.Product)
        && (MouseShapes.Collection(shape, 0x000A, 0xC7) || MouseShapes.Collection(shape, 0x000A, 0x00)) && shape.InputLength >= 7;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Ryunix", Takes, _ => [new BrandWay("ryunix.listen", WayTransport.Listen, [], Battery) { TimeoutMs = 600, Attempts = 1 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "ryunix" },
    ];
}

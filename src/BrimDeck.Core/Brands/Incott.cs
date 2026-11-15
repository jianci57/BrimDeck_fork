namespace BrimDeck.Core;

// Incott 8K wireless (PixArt's shared vendor id 0x093A, so only its two products: 522C on the receiver, 622C on its cable).
// Nothing is sent: while the mouse is moved or clicked it sends input report 0x09 on its vendor collection (usage page
// 0xFF05 usage 1) by itself, 09 <state> <stage and polling> ..., the state the percent up to 100 and 128 plus the percent while
// charging. At rest it sends none, so a pass that hears nothing has no reading. Its feature reports' battery bytes are
// constants (OpenMouse disproved two of them over a full charge) and are not read. The report is 8 bytes; where the
// collection's input report is longer, the bytes after the eighth are zeros, or it is another report (MouseDock). Other
// brands' mice use the same PixArt receiver id (MouseDock lists an AULA SC900 on 522C with the same report), so the
// product string names the mouse. From OpenMouse's Incott module and notes (AGPL; facts only: measured over a charge
// cycle, 09 5F 10 04 .. at 95 %, 09 E1 10 04 .. charging at 97 %) and MouseDock's PixArt input reader (MIT).
[BrandFamilies]
public static class IncottMice
{
    public const int Vendor = 0x093A;
    public static readonly int[] Products = [0x522C, 0x622C];

    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 3 || r[0] != 0x09) return null;
        for (int i = 8; i < r.Length; i++) if (r[i] != 0) return null;
        int state = r[1];
        if (state is > 0 and <= 100) return new(state);
        return state - 128 is > 0 and <= 100 ? new(state - 128, true) : null;
    }

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && Products.Contains(shape.Product)
        && MouseShapes.Collection(shape, 0xFF05, 0x01) && shape.InputLength >= 3;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Incott", Takes, _ => [new BrandWay("incott.listen", WayTransport.Listen, [], Battery) { TimeoutMs = 600, Attempts = 1 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "incott" },
    ];
}

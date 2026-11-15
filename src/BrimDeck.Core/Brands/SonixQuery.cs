namespace BrimDeck.Core;

// Keyboards on Sonix-platform 2.4 GHz receivers that answer the battery query 20 01 on their raw HID collection (usage page
// 0xFF60, usage 0x61, 33-byte input and output reports with report id 0): [0, 0x20, 0x01, zeros, checksum], the checksum the
// low byte of the sum of the 32 bytes before it (0x21). The answer, after the zero report id: [0x20, 0x01, 0x00, percent,
// zeros, checksum], the checksum the low byte of the sum of the 31 bytes before it. No charging state is sent. Zero is the
// keyboard asleep (and the request read back), not a level.
//
// - AULA F108 Pro and GANSS GS3104T-Pro (and the AULA F75 Max on the same platform) on 05AC:024F: Apple's ids, borrowed by
//   the receiver, which Apple's own wired Aluminium Keyboard carries too (it has no 0xFF60 collection) and Lofree's Flow
//   Lite84 receiver as well. The battery is on interface 3 only (interfaces 0 and 4 give none), so the collection is taken on
//   interface 3, or where the product string names the keyboard ("F108Pro Dongle" measured; GANSS, GS3104) when the
//   interface number moved. vigil-watt (MIT; GANSS capture 97 %, F108 Pro checked on the keyboard), aula-keyboard-battery-tray
//   and Aula-F75-Max-Driver (MIT; the same query and answer for the F75 Max).
// - EWEADN X87 on 0C45:FEFE, Sonix's id: EWEADN's driver takes only interface 3 (its interface 4 is the same page with 65-byte
//   reports and must not be written). vigil-watt (MIT; from the driver, measured at 100 %). The AULA F87 Pro V2D's receiver
//   carries 0C45:FEFE as well (aula-keyboard-battery-tray, MIT), so the receiver's own product string names the keyboard,
//   not "EWEADN X87".
// - The Huafenda platform's device-info read, which AJAZZ's, EWEADN's, Epomaker's and AULA's web drivers all send (AJAZZ's
//   ajazz.driveall.cn and hub, Epomaker's hub.epomaker.com, EWEADN's huafenda SDK): AA 10 <length> <address, 2> 00 <last
//   packet> 00, report id 0 first, the info read in packets of the report's size less 8 (24 bytes on the 2.4 GHz receivers'
//   33-byte collection, 56 on a 65-byte one), each answered 55 10 <length> <address, 2> .. with its data from 8. The info's
//   byte 16 is the link (0 cable, 1 Bluetooth, 2 2.4 GHz), 17 the level and 18 the charge state (1 and 2 while on the cable),
//   so the first packet carries them: it alone is asked (as the drivers' first packet; a 65-byte collection's single packet is
//   their last). AJAZZ's driver sends it on the 2.4 GHz receiver's interface 3 (0C45:FEFE, the collection the 20 01 query is
//   asked on), so it is that collection's second read; the other receivers and keyboards of the drivers' tables take it on
//   their own raw collection (HuafendaProducts). A level of 0 is not ready yet (EWEADN's SDK asks again then): no reading.
//   Not measured by us.
[BrandFamilies]
public static class SonixQuery
{
    public const int AppleVendor = 0x05AC, BorrowedReceiver = 0x024F, SonixVendor = 0x0C45, EweadnReceiver = 0xFEFE;

    public static byte[] Request()
    {
        var frame = BrandFamilies.Frame(33, 0x00, 0x20, 0x01);
        int sum = 0; for (int i = 0; i < 32; i++) sum += frame[i];
        frame[32] = (byte)sum;
        return frame;
    }

    // The input report without its zero report id.
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 32 || r[0] != 0x20 || r[1] != 0x01 || r[2] != 0x00 || r[3] is 0 or > 100) return null;
        int sum = 0; for (int i = 0; i < 31; i++) sum += r[i];
        return (byte)sum == r[31] ? new(r[3]) : null;
    }

    // The device-info read's first packet for a collection whose output report is this long (report id included).
    public static byte[] HuafendaRequest(int outputLength)
        => outputLength >= 65 ? BrandFamilies.Frame(65, 0x00, 0xAA, 0x10, 0x38, 0x00, 0x00, 0x00, 0x01, 0x00)
            : BrandFamilies.Frame(33, 0x00, 0xAA, 0x10, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00);
    // The answer without its zero report id.
    public static BatteryRead? Huafenda(byte[] r)
    {
        if (r.Length < 27 || r[0] != 0x55 || r[1] != 0x10 || r[2] < 19 || r[3] != 0x00 || r[4] != 0x00) return null;
        return r[25] is > 0 and <= 100 ? new(r[25], r[26] is 1 or 2) : null;
    }

    // The drivers' other Huafenda receivers and keyboards (0C45 is Sonix's id and 05AC Apple's, both shared, so only these):
    // AJAZZ's and EWEADN's receivers FEF9, FEFA, FEFC and FEFD, Epomaker's TH and G84 Pro series on 8006 to 8100 and the TH65's
    // receiver 8012, and the TH87 DE's 05AC:0250.
    public static readonly (int Vendor, int Product)[] HuafendaProducts =
    [
        .. new[] { 0xFEF9, 0xFEFA, 0xFEFC, 0xFEFD, 0x8006, 0x800A, 0x8009, 0x8010, 0x8100, 0x8007, 0x800B, 0x8011, 0x8012 }.Select(product => (SonixVendor, product)),
        (AppleVendor, 0x0250),
    ];
    // Their device-info collection (the drivers' first function block): the 2.4 GHz receivers' usage 0x61 on page 0xFF60 on
    // interface 3 alone (their interface 4 on the same page and the cables' 0xFF67 are the animation channels, never written),
    // the keyboards' on page 0xFF68; 33 or 65 bytes.
    private static readonly HashSet<int> HuafendaReceivers = [0xFEF9, 0xFEFA, 0xFEFC, 0xFEFD, 0x8012];
    public static bool HuafendaCollection(HidShape shape)
        => shape.Usage == 0x0061 && shape.InputLength is 33 or 65 && shape.OutputLength is 33 or 65
            && (shape.Vendor == SonixVendor && HuafendaReceivers.Contains(shape.Product)
                ? Page(shape.UsagePage, 0xFF60) && shape.Interface == 3 : Page(shape.UsagePage, 0xFF68));
    public static bool TakesHuafenda(HidShape shape) => HuafendaProducts.Contains((shape.Vendor, shape.Product)) && HuafendaCollection(shape);
    private static BrandWay HuafendaWay(HidShape shape) => new("huafenda.info", WayTransport.Output, HuafendaRequest(shape.OutputLength), Huafenda) { TimeoutMs = 600, StripZero = true };

    private static bool Page(int actual, int page) => actual == page || actual == ((page & 0xFF) << 8 | page >> 8);
    public static bool Collection(HidShape shape) => Page(shape.UsagePage, 0xFF60) && shape.Usage == 0x0061 && shape.InputLength == 33 && shape.OutputLength == 33;

    // The model a 05AC:024F receiver names, if it names one.
    public static string? Named(string name)
        => name.Contains("F108", StringComparison.OrdinalIgnoreCase) ? "AULA F108 Pro"
            : name.Contains("GANSS", StringComparison.OrdinalIgnoreCase) || name.Contains("GS3104", StringComparison.OrdinalIgnoreCase) ? "GANSS GS3104T-Pro"
            : null;

    public static bool TakesBorrowed(HidShape shape) => shape.Vendor == AppleVendor && shape.Product == BorrowedReceiver && Collection(shape)
        && (shape.Interface == 3 || Named(shape.Name) is not null);
    public static bool TakesEweadn(HidShape shape) => shape.Vendor == SonixVendor && shape.Product == EweadnReceiver && Collection(shape) && shape.Interface == 3;

    private static IReadOnlyList<BrandWay> Ways(string prefix, HidShape shape) =>
        [new BrandWay(prefix + ".query", WayTransport.Output, Request(), Battery) { TimeoutMs = 800, StripZero = true }, HuafendaWay(shape)];

    public static IEnumerable<BrandFamily> Families =>
    [
        new("AULA / GANSS", TakesBorrowed, shape => Ways("sonix", shape))
        { Kind = _ => DeviceKind.Keyboard, Prefix = "sonix", DisplayName = shape => Named(shape.Name) },
        new("EWEADN", TakesEweadn, shape => Ways("eweadn", shape))
        { Kind = _ => DeviceKind.Keyboard, Prefix = "eweadn" },
        new("Huafenda keyboard", TakesHuafenda, shape => [HuafendaWay(shape)])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "huafenda" },
    ];
}

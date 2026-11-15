using BrimDeck.Core;

// The brand families (BrandFamilies): every brand's matching and reads, and the rules all of them keep.
static class BrandFamilyTests
{
    public static HidShape Shape(int vendor, int product, int page, int usage = 1, int iface = -1, int input = 64, int output = 64, int feature = 0,
        byte[]? outputIds = null, byte[]? featureIds = null, string name = "")
        => new(vendor, product, page, usage, iface, input, output, feature, (outputIds ?? []).ToHashSet(), (featureIds ?? []).ToHashSet(), name);

    public static void Run(Action<string, bool> check)
    {
        Rules(check);
        Betop(check);
        VgnV108(check);
    }

    // Pull request #278's capture: 100 % on the receiver (byte 6 = 01: full, off the cable).
    private static void VgnV108(Action<string, bool> check)
    {
        var reply = Bytes("13 4A 01 00 02 64 01 00 00 00 00 00 00 00 00 00 00 00 00 C5");
        var charging = Bytes("13 4A 01 00 02 32 10 00 00 00 00 00 00 00 00 00 00 00 00 A2");
        check("VGN V108: the request with its sum, the captured 100 % answer, a charging state, a wrong sum and a level over 100 are none",
            BrimDeck.Core.VgnV108.Request().SequenceEqual(Bytes("13 4A 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5E"))
            && BrimDeck.Core.VgnV108.Battery(reply) == new BatteryRead(100) && BrimDeck.Core.VgnV108.Battery(charging) == new BatteryRead(50, true)
            && BrimDeck.Core.VgnV108.Battery([.. reply[..^1], 0]) is null
            && BrimDeck.Core.VgnV108.Battery(Bytes("13 4A 01 00 02 65 01 00 00 00 00 00 00 00 00 00 00 00 00 C6")) is null
            && BrimDeck.Core.VgnV108.Battery(Bytes("13 4A 01 00 02 00 01 00 00 00 00 00 00 00 00 00 00 00 00 61")) is null
            && BrimDeck.Core.VgnV108.Battery(BrimDeck.Core.VgnV108.Request()) is null);
        check("VGN V108: only interface 1 of 391D:2A02, with 20-byte reports and report 0x13 declared, never a standard collection",
            BrandFamilies.Find(Shape(0x391D, 0x2A02, 0xFF02, 0x0002, 1, 25, 20, outputIds: [0x13])) is { Family.Brand: "VGN V108", Ways.Count: 1 }
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0x0001, 0x0000, 1, 25, 20, outputIds: [0x13])) is { Family.Brand: "VGN V108", Ways.Count: 1 }
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0x0001, 0x0006, 1, 25, 20, outputIds: [0x13])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0x0001, 0x0080, 1, 25, 20, outputIds: [0x13])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0x000C, 0x0001, 1, 25, 20, outputIds: [0x13])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0xFF02, 0x0002, 0, 8, 1, outputIds: [0x00])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0xFF02, 0x0002, 1, 19, 20, outputIds: [0x13])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0xFF02, 0x0002, 1, 25, 20, outputIds: [0x01])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A05, 0xFF00, 0x0001, 1, 25, 20, outputIds: [0x13])) is not { Family.Brand: "VGN V108" });
    }

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    // What every family must keep: unique brands and read names (they are kept on disk), names under the brand's prefix, and
    // never a standard collection (mouse, keyboard, consumer, system control) asked.
    private static void Rules(Action<string, bool> check)
    {
        var all = BrandFamilies.All;
        check("Brand families: found by their attribute", all.Count > 0 && all.Any(family => family.Brand == "BETOP"));
        check("Brand families: each brand once", all.Select(family => family.Brand).Distinct().Count() == all.Count);
        int[] standard = [0x01, 0x0C];
        bool none = true;
        foreach (var family in all)
            foreach (var page in standard)
                for (int vendor = 0; vendor < 0x10000; vendor += 0x101)
                    if (family.Takes(Shape(vendor, 0x0001, page))) none = false;
        check("Brand families: none takes a generic desktop or consumer collection", none);
    }

    private static void Betop(Action<string, bool> check)
    {
        var receiver = Shape(0x20BC, 0x5073, 0x00FF, 3, 1, outputIds: [0x02]);
        var found = BrandFamilies.Find(receiver);
        check("BETOP: any 20BC receiver's vendor collection (the Kunpeng 70's id is not known) takes the status read, its page as Windows reports it or as written",
            found is { Family.Brand: "BETOP" } && found.Value.Ways.Select(way => way.Name).SequenceEqual(["betop.status"])
            && BrandFamilies.Find(Shape(0x20BC, 0x5191, 0xFF00, 3, 1, outputIds: [0x02])) is { Ways.Count: 1 }
            && BrandFamilies.Find(Shape(0x20BD, 0x5191, 0x00FF, 3, outputIds: [0x02])) is null);
        check("BETOP: never the receiver's mouse, keyboard or gamepad collections, nor another usage on its vendor page",
            BrandFamilies.Find(Shape(0x20BC, 0x5191, 0x0001, 2, outputIds: [0x02])) is null && BrandFamilies.Find(Shape(0x20BC, 0x5191, 0x0001, 6, outputIds: [0x02])) is null
            && BrandFamilies.Find(Shape(0x20BC, 0x5191, 0x0001, 5, outputIds: [0x02])) is null && BrandFamilies.Find(Shape(0x20BC, 0x5191, 0x00FF, 1, outputIds: [0x02])) is null);
        check("BETOP: not sent where the collection declares no report 0x02",
            BrandFamilies.Find(Shape(0x20BC, 0x5191, 0x00FF, 3, outputIds: [0x01])) is { Ways.Count: 0 });
        check("BETOP: the request, a reading on battery and one charging (the Kunpeng 20's capture, vigil-watt's 93 %), and the request read back and a level of 0 are none",
            BrimDeck.Core.Betop.Request().SequenceEqual(new byte[] { 0x02, 0x15 })
            && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x64, 0x00, 0x51, 0x01, 0x01, 0x00]) == new BatteryRead(100, true)
            && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x5D, 0x00, 0x50]) == new BatteryRead(93)
            && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x37, 0x00, 0x50]) == new BatteryRead(55)
            && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x00, 0x00, 0x50]) is null
            && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x00, 0x00, 0x00]) is null && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x99, 0x00, 0x50]) is null
            && BrimDeck.Core.Betop.Battery([0x02, 0x12, 0x40, 0x00, 0x50]) is null);
    }
}

using BrimDeck.Core;

// The controller brands (Flydigi, GameSir, 8BitDo, NVIDIA SHIELD) and the XInput levels of the Xbox 360 wireless receiver:
// which collections each takes and which it leaves, the requests, and the answers from the sources.
static class BrandsControllerTests
{
    private static HidShape Shape(int vendor, int product, int page, int usage = 1, int iface = -1, int input = 64, int output = 64, byte[]? outputIds = null)
        => BrandFamilyTests.Shape(vendor, product, page, usage, iface, input, output, outputIds: outputIds);
    private static string[] Names((BrandFamily Family, IReadOnlyList<BrandWay> Ways)? found) => found is { } f ? [.. f.Ways.Select(way => way.Name)] : [];
    private static byte[] Frame(int length, params (int At, byte Value)[] bytes)
    {
        var frame = new byte[length];
        foreach (var (at, value) in bytes) frame[at] = value;
        return frame;
    }

    public static void Run(Action<string, bool> check)
    {
        Others(check);
        Flydigi(check);
        GameSir(check);
        EightBitDo(check);
        Shield(check);
        XInput(check);
    }

    // None of these collections is one another reader already takes.
    private static void Others(Action<string, bool> check)
    {
        (int Vendor, int Product, int Page, int Usage)[] taken =
            [(0x37D7, 0x2401, 0xFFA0, 1), (0x37D7, 0x2501, 0xFFA0, 1), (0x37D7, 0x2502, 0xFFA0, 1), (0x3537, 0x1098, 0xFF7A, 1), (0x3537, 0x1033, 0xFFF0, 0x40),
             (0x3537, 0x103D, 0xFFF0, 0x40), (0x2DC8, 0x6009, 0x01, 5), (0x2DC8, 0x6003, 0x01, 5), (0x0955, 0x7214, 0x01, 5)];
        check("Controller brands: no collection they take is another reader's",
            taken.All(t => Dongles.Find(t.Vendor, t.Product, t.Page, "", 0, -1, t.Usage, 64, 64) is null && Headsets.Find(t.Vendor, t.Product) is null
                && Corsair.Find(t.Vendor, t.Product) is null && Pushes.Find(t.Vendor, t.Product, t.Page, t.Usage, -1, 64) is null
                && t.Vendor != PlayStation.Vendor));
    }

    private static void Flydigi(Action<string, bool> check)
    {
        var apex5 = BrandFamilies.Find(Shape(0x37D7, 0x2501, 0xFFA0, 1, 2, 33, 33, [0x03]));
        var vader5 = BrandFamilies.Find(Shape(0x37D7, 0x2401, 0xA0FF, 1, 1, 33, 33, [0x00]));
        check("Flydigi: the Vader 5 Pro's, Apex 5's and Apex 6's vendor collection 0xFFA0:1 (either byte order) takes the info read, numbered where it declares report 0x03",
            apex5 is { Family.Brand: "Flydigi" } && Names(apex5).SequenceEqual(["flydigi.info"])
            && apex5.Value.Ways[0].Request.SequenceEqual(new byte[] { 0x03, 0x5A, 0xA5, 0x01, 0x02, 0x00 })
            && vader5 is { Family.Brand: "Flydigi" } && vader5.Value.Ways[0].Request.SequenceEqual(new byte[] { 0x00, 0x5A, 0xA5, 0x01, 0x02, 0x00 })
            && BrandFamilies.Find(Shape(0x37D7, 0x2502, 0xFFA0, 1, input: 33, outputIds: [0x00])) is { Family.Brand: "Flydigi" });
        // HaloBattery #191's diagnostics: VID=37d7 PID=6001 if=0 usage=ffa0:0001 'flydigi' 'Flydigi CD2'.
        check("Flydigi: every pad of the brand on its vendor collection (a later model too), never the CD2 charging dock on the same page",
            BrandFamilies.Find(Shape(0x37D7, 0x6001, 0xFFA0, 1, 0, 65, 65, [0x00])) is null
            && BrandFamilies.Find(Shape(0x37D7, 0x2601, 0xFFA0, 1, 1, 33, 33, [0x00])) is { Family.Brand: "Flydigi", Ways.Count: 1 });
        check("Flydigi: not its gamepad, another usage, a short collection or another vendor (Cypress 04B4 is the old chip)",
            BrandFamilies.Find(Shape(0x37D7, 0x2401, 0x01, 5, outputIds: [0x00])) is null && BrandFamilies.Find(Shape(0x37D7, 0x2401, 0xFFA0, 2, outputIds: [0x00])) is null
            && BrandFamilies.Find(Shape(0x37D7, 0x2401, 0xFFA0, 1, input: 16, outputIds: [0x00])) is null
            && BrandFamilies.Find(Shape(0x04B4, 0x2412, 0xFFA0, 1, outputIds: [0x05])) is null);
        check("Flydigi: not sent where the collection declares neither report 0x03 nor 0x00",
            BrandFamilies.Find(Shape(0x37D7, 0x2501, 0xFFA0, 1, input: 33, outputIds: [0x05])) is { Ways.Count: 0 });

        // The info answer as HaloBattery's tests frame it (device 130, connection 1, firmware 41 71), numbered and as Windows
        // hands over an unnumbered one (a zero id first).
        static byte[] Info(byte battery, byte device = 130, byte id = 0x03) =>
            [id, .. Frame(31, (0, 0x5A), (1, 0xA5), (2, 0x01), (5, device), (6, 1), (11, battery), (15, 0x41), (16, 0x71))];
        var apex5Way = apex5!.Value.Ways[0];
        var apex6Way = BrandFamilies.Find(Shape(0x37D7, 0x2502, 0xFFA0, 1, input: 33, outputIds: [0x00]))!.Value.Ways[0];
        check("Flydigi: the battery byte, × 20 on battery and charging, full when charged, coarse",
            apex5Way.Parse(Info(0x03)) == new BatteryRead(60, Coarse: true) && apex5Way.Parse(Info(0x12, id: 0x00)) == new BatteryRead(40, true, Coarse: true)
            && apex5Way.Parse(Info(0x20, id: 0x04)) == new BatteryRead(100, true, Coarse: true)
            && apex5Way.Parse(Info(0x05)[1..]) == new BatteryRead(100, Coarse: true));
        check("Flydigi: the Apex 6 (by its product or its device id 149, 150, 152) counts its step × 10",
            apex6Way.Parse(Info(0x07, 149, 0x00)) == new BatteryRead(70, Coarse: true) && apex5Way.Parse(Info(0x07, 150)) == new BatteryRead(70, Coarse: true)
            && apex6Way.Parse(Info(0x0A, 0x01, 0x00)) == new BatteryRead(100, Coarse: true) && apex5Way.Parse(Info(0x06)) is null);
        check("Flydigi: out of range, the request read back, the pad's input stream and zeros are none",
            apex5Way.Parse(Info(0x30)) is null && apex5Way.Parse(Info(0xFF)) is null
            && apex5Way.Parse(BrandFamilies.Frame(33, 0x03, 0x5A, 0xA5, 0x01, 0x02, 0x00)) is null
            && apex5Way.Parse(BrandFamilies.Frame(33, 0x03, 0x5A, 0xA5, 0xEF, 0x01, 0x82)) is null && apex5Way.Parse(new byte[33]) is null
            && apex5Way.Parse(Info(0x03)[..20]) is null);
        // #191: the Vader 5 Pro on 37d7:2401 and the Vader 4 Pro on 045e:028e, both 'Xbox 360 Controller for Windows' to
        // Windows.Gaming.Input; the Vader 4's HID gamepad collection is 'Controller (Flydigi VADER4)'.
        check("Flydigi: Windows.Gaming.Input's 1000 of 1000 is a placeholder for Flydigi's pads only, the Vader 4 Pro known by its HID name",
            BrimDeck.Core.Flydigi.WindowsPlaceholder(0x37D7, 0x2401, null, 1000, 1000)
            && BrimDeck.Core.Flydigi.WindowsPlaceholder(0x045E, 0x028E, "Controller (Flydigi VADER4)", 1000, 1000)
            && !BrimDeck.Core.Flydigi.WindowsPlaceholder(0x045E, 0x028E, "Controller (XBOX 360 For Windows)", 1000, 1000)
            && !BrimDeck.Core.Flydigi.WindowsPlaceholder(0x045E, 0x028E, null, 1000, 1000)
            && !BrimDeck.Core.Flydigi.WindowsPlaceholder(0x045E, 0x0B13, null, 1000, 1000)
            && !BrimDeck.Core.Flydigi.WindowsPlaceholder(0x37D7, 0x2401, null, 640, 1000));
        check("Flydigi: only the pads read over HID are left out of Windows.Gaming.Input's list",
            BrimDeck.Core.Flydigi.ReadOverHid(0x37D7, 0x2401) && BrimDeck.Core.Flydigi.ReadOverHid(0x37D7, 0x2502)
            && !BrimDeck.Core.Flydigi.ReadOverHid(0x37D7, 0x6001) && !BrimDeck.Core.Flydigi.ReadOverHid(0x045E, 0x028E));
    }

    private static void GameSir(Action<string, bool> check)
    {
        var nova = BrandFamilies.Find(Shape(0x3537, 0x1098, 0xFF7A, 1, 2, 65, 65, [0x00]));
        var g7 = BrandFamilies.Find(Shape(0x3537, 0x1033, 0xFFF0, 0x40, 1, 64, 64, [0x0F]));
        check("GameSir: any 3537 status collection (0xFF7A:1, 65 bytes) takes the status read, any 0xFFF0:0x40 the heartbeat",
            nova is { Family.Brand: "GameSir" } && Names(nova).SequenceEqual(["gamesir.status"]) && nova.Value.Ways[0].Request.SequenceEqual(new byte[] { 0x00, 0x01, 0x01, 0x00 })
            && g7 is { Family.Brand: "GameSir" } && Names(g7).SequenceEqual(["gamesir.heartbeat"]) && g7.Value.Ways[0].Transport == WayTransport.OutputListen
            && g7.Value.Ways[0].Request.SequenceEqual(new byte[] { 0x0F, 0xF2, 0x00 })
            && Names(BrandFamilies.Find(Shape(0x3537, 0x100F, 0x7AFF, 1, 2, 65, 65, [0x00]))).SequenceEqual(["gamesir.status"])
            && Names(BrandFamilies.Find(Shape(0x3537, 0x10B8, 0xF0FF, 0x40, 1, 64, 64, [0x0F]))).SequenceEqual(["gamesir.heartbeat"]));
        check("GameSir: not its gamepad or keyboard, another usage, a short collection, or another vendor",
            BrandFamilies.Find(Shape(0x3537, 0x1098, 0x01, 5, outputIds: [0x00])) is null && BrandFamilies.Find(Shape(0x3537, 0x1033, 0x01, 6, outputIds: [0x0F])) is null
            && BrandFamilies.Find(Shape(0x3537, 0x1033, 0xFFF0, 0x01, outputIds: [0x0F])) is null
            && BrandFamilies.Find(Shape(0x3537, 0x1098, 0xFF7A, 1, input: 64, output: 64, outputIds: [0x00])) is null
            && BrandFamilies.Find(Shape(0x3538, 0x1098, 0xFF7A, 1, input: 65, output: 65, outputIds: [0x00])) is null);
        check("GameSir: not sent where the collection does not declare the report",
            BrandFamilies.Find(Shape(0x3537, 0x1033, 0xFFF0, 0x40, outputIds: [0x01])) is { Ways.Count: 0 }
            && BrandFamilies.Find(Shape(0x3537, 0x1098, 0xFF7A, 1, input: 65, output: 65, outputIds: [0x02])) is { Ways.Count: 0 });

        // GamesirBatteryTray's README: 00 | 01 F4 96 50 03 25 | ... 28 00 ... | 49 is 73 %.
        var readme = Frame(65, (1, 0x01), (2, 0xF4), (3, 0x96), (4, 0x50), (5, 0x03), (6, 0x25), (11, 0x28), (19, 0x49));
        var cable = (byte[])readme.Clone(); cable[19] = 0xFF;
        var empty = (byte[])readme.Clone(); empty[19] = 0x00;
        check("GameSir: the status answer's percent, FF on the cable charging without one; the request read back, a MAC of zeros, 0 and over 100 are none",
            BrimDeck.Core.GameSir.Status(readme) == new BatteryRead(73) && BrimDeck.Core.GameSir.Status(cable) == new BatteryRead(null, true)
            && BrimDeck.Core.GameSir.Status(BrandFamilies.Frame(65, 0x00, 0x01, 0x01, 0x00)) is null
            && BrimDeck.Core.GameSir.Status(Frame(65, (19, 0x49))) is null && BrimDeck.Core.GameSir.Status(Frame(65, (1, 0x01), (19, 0x65))) is null
            && BrimDeck.Core.GameSir.Status(Frame(65, (19, 0xFF))) is null && BrimDeck.Core.GameSir.Status(empty) is null
            && BrimDeck.Core.GameSir.Status([0x01, .. readme[1..]]) is null && BrimDeck.Core.GameSir.Status(readme[..19]) is null);
        // agnostos-t's G7 Pro 8K: 81 % off the dock, 87 % charging; the Tarantula CE's bytes are 27 and 28.
        var g7Way = g7!.Value.Ways[0];
        var tarantula = BrandFamilies.Find(Shape(0x3537, 0x103D, 0xFFF0, 0x40, input: 64, outputIds: [0x0F]))!.Value.Ways[0];
        check("GameSir: report 0x12's percent and charging bit, at 36 and 35 (27 and 28 on the Tarantula CE)",
            g7Way.Parse(Frame(64, (0, 0x12), (36, 81))) == new BatteryRead(81) && g7Way.Parse(Frame(64, (0, 0x12), (35, 1), (36, 87))) == new BatteryRead(87, true)
            && tarantula.Parse(Frame(64, (0, 0x12), (27, 1), (28, 50))) == new BatteryRead(50, true) && tarantula.Parse(Frame(64, (0, 0x12), (28, 101))) is null);
        check("GameSir: another report, over 100, a level of 0 (the stream not ready), a report of zeros and the heartbeat are none",
            g7Way.Parse(Frame(64, (0, 0x10), (36, 81))) is null && g7Way.Parse(Frame(64, (0, 0x12), (36, 255))) is null
            && g7Way.Parse(Frame(64, (0, 0x12), (1, 0x80), (35, 1))) is null && tarantula.Parse(Frame(64, (0, 0x12), (5, 0x80))) is null
            && g7Way.Parse(Frame(64, (0, 0x12))) is null && g7Way.Parse(BrandFamilies.Frame(64, 0x0F, 0xF2, 0x00)) is null
            && g7Way.Parse(Frame(36, (0, 0x12), (35, 1))) is null);
    }

    private static void EightBitDo(Action<string, bool> check)
    {
        var pro3 = BrandFamilies.Find(Shape(0x2DC8, 0x6009, 0x01, 5, input: 64, outputIds: [0x00]));
        check("8BitDo: the gamepad or joystick collection of a pad SDL reads the level of is listened to, nothing sent",
            pro3 is { Family.Brand: "8BitDo" } && Names(pro3).SequenceEqual(["8bitdo.report"]) && pro3.Value.Ways[0] is { Transport: WayTransport.Listen, Request.Length: 0 }
            && Names(BrandFamilies.Find(Shape(0x2DC8, 0x6003, 0x01, 4, input: 64))).SequenceEqual(["8bitdo.report"])
            && new[] { 0x6000, 0x6100, 0x6001, 0x6101, 0x6006, 0x6012, 0x202F }.All(product => Names(BrandFamilies.Find(Shape(0x2DC8, product, 0x01, 5, input: 64))).Length == 1));
        check("8BitDo: not its keyboard or mouse, a vendor collection, a report too short, the Ultimate 2C, another 8BitDo product or another vendor",
            BrandFamilies.Find(Shape(0x2DC8, 0x5200, 0x01, 6)) is null && BrandFamilies.Find(Shape(0x2DC8, 0x5200, 0x01, 2)) is null
            && BrandFamilies.Find(Shape(0x2DC8, 0x6009, 0xFF00, 1)) is null && BrandFamilies.Find(Shape(0x2DC8, 0x6000, 0x01, 5, input: 9)) is null
            && BrandFamilies.Find(Shape(0x2DC8, 0x301B, 0x01, 5, input: 12)) is null && BrandFamilies.Find(Shape(0x2DC8, 0x301B, 0x01, 5)) is null
            && BrandFamilies.Find(Shape(0x2DC8, 0x3106, 0x01, 5)) is null && BrandFamilies.Find(Shape(0x2DC8, 0x5006, 0x01, 4)) is null
            && BrandFamilies.Find(Shape(0x2DC9, 0x6009, 0x01, 5)) is null);
        // HaloBattery's Pro 3 report, read on hardware: 20 %.
        byte[] real = [0x04, 0x0F, 0x80, 0x7F, 0x7F, 0x80, 0, 0, 0, 0, 0, 0, 0x09, 0x60, 0x14, 0xD7, 0xFB, 0xF6, 0xFF, 0xAA, 0x0F, 0x09, 0, 0x05, 0, 0x10, 0, 0xD0, 0x8E, 0x75, 0x01, 0x66];
        check("8BitDo: report 0x04's byte 14, bit 7 charging; 0 is padding; other reports are none",
            BrimDeck.Core.EightBitDo.Battery(real) == new BatteryRead(20) && BrimDeck.Core.EightBitDo.Battery([.. real[..14], 0x80 | 90]) == new BatteryRead(90, true)
            && BrimDeck.Core.EightBitDo.Battery([.. real[..14], 0x00]) is null && BrimDeck.Core.EightBitDo.Battery([.. real[..14], 0x80]) is null
            && BrimDeck.Core.EightBitDo.Battery([.. real[..14], 0x7F]) is null && BrimDeck.Core.EightBitDo.Battery([0x01, .. real[1..]]) is null
            && BrimDeck.Core.EightBitDo.Battery([0x03, .. real[1..]]) is null && BrimDeck.Core.EightBitDo.Battery(real[..14]) is null);
    }

    private static void Shield(Action<string, bool> check)
    {
        var shield = BrandFamilies.Find(Shape(0x0955, 0x7214, 0x01, 5, input: 65, output: 33, outputIds: [0x04]));
        check("SHIELD: the 2017 controller's collection that declares report 0x04 takes the battery command 04 07 00, 33 bytes",
            shield is { Family.Brand: "NVIDIA" } && Names(shield).SequenceEqual(["shield.battery"])
            && shield.Value.Ways[0].Request.SequenceEqual(BrandFamilies.Frame(33, 0x04, 0x07, 0x00)) && shield.Value.Ways[0].Request.Length == 33);
        check("SHIELD: not another NVIDIA product, its consumer or keyboard collection, or one without report 0x04",
            BrandFamilies.Find(Shape(0x0955, 0x7210, 0x01, 5, outputIds: [0x04])) is null && BrandFamilies.Find(Shape(0x0955, 0x7214, 0x0C, 1, outputIds: [0x04])) is null
            && BrandFamilies.Find(Shape(0x0955, 0x7214, 0x01, 6, outputIds: [0x04])) is null
            && BrandFamilies.Find(Shape(0x0955, 0x7214, 0x01, 5, outputIds: [0x01])) is { Ways.Count: 0 });
        // 03 07 00, voltages (average, thermistor, minimum, boot, present), then the percent at 14.
        var reply = Frame(65, (0, 0x03), (1, 0x07), (3, 0x0E), (4, 0x10), (6, 0x40), (7, 0x02), (8, 0xC4), (9, 0x0E), (10, 0x22), (11, 0x10), (12, 0x0E), (13, 0x10), (14, 73));
        check("SHIELD: the answer's percent at byte 14; another command's or another transaction's answer, the request read back, zeros and over 100 are none",
            BrimDeck.Core.NvidiaShield.Battery(reply) == new BatteryRead(73) && BrimDeck.Core.NvidiaShield.Battery(reply[..33]) == new BatteryRead(73)
            && BrimDeck.Core.NvidiaShield.Battery([0x03, 0x07, 0x05, .. reply[3..]]) is null
            && BrimDeck.Core.NvidiaShield.Battery([0x03, 0x3A, .. reply[2..]]) is null && BrimDeck.Core.NvidiaShield.Battery(BrandFamilies.Frame(33, 0x04, 0x07, 0x00)) is null
            && BrimDeck.Core.NvidiaShield.Battery(Frame(65, (0, 0x03), (1, 0x07))) is null && BrimDeck.Core.NvidiaShield.Battery([.. reply[..14], 101]) is null
            && BrimDeck.Core.NvidiaShield.Battery(reply[..14]) is null);
    }

    private static void XInput(Action<string, bool> check)
    {
        check("XInput: the Xbox 360 receiver's slots are product 0 with the wireless flag",
            XInputBattery.Receiver360Slot(0, 0x0002) && XInputBattery.Receiver360Slot(0, 0x000F) && !XInputBattery.Receiver360Slot(0, 0x0000)
            && !XInputBattery.Receiver360Slot(0x0B13, 0x0002));
        check("XInput: four levels as SDL takes them, coarse; wired is charging without a level; no level for unknown or out of range; nothing for an empty slot",
            XInputBattery.Battery(0x02, 0) == new BatteryRead(10, Coarse: true) && XInputBattery.Battery(0x03, 1) == new BatteryRead(40, Coarse: true)
            && XInputBattery.Battery(0x03, 2) == new BatteryRead(70, Coarse: true) && XInputBattery.Battery(0x02, 3) == new BatteryRead(100, Coarse: true)
            && XInputBattery.Battery(0x01, 3) == new BatteryRead(null, Charging: true) && XInputBattery.Battery(0x01, 0) == new BatteryRead(null, Charging: true)
            && XInputBattery.Battery(0xFF, 1) == new BatteryRead(null)
            && XInputBattery.Battery(0x03, 4) == new BatteryRead(null) && XInputBattery.Battery(0x00, 3) is null);
    }
}

using BrimDeck.Core;

// The reads taken from the brands' official web drivers (2026-11-15): each new read's request byte for byte, its answer and
// the reports it must not take, and the collections it goes to and none other.
static class OfficialDriverTests
{
    public static void Run(Action<string, bool> check)
    {
        Mchose(check);
        SharedReceivers(check);
        KnownFrames(check);
        AtkAndVgn(check);
        KeyboardsAndBigBrands(check);
        SessionAndSerial(check);
    }

    private static HidShape Shape(int vendor, int product, int page, int usage = 1, int iface = -1, int input = 64, int output = 64, int feature = 0,
        byte[]? outputIds = null, byte[]? featureIds = null, string name = "")
        => BrandFamilyTests.Shape(vendor, product, page, usage, iface, input, output, feature, outputIds, featureIds, name);
    private static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    private static string? Family(HidShape shape) => BrandFamilies.Find(shape) is { Ways.Count: > 0 } found ? found.Family.Brand : null;
    private static byte[] Padded(int length, string hex) { var frame = new byte[length]; Bytes(hex).CopyTo(frame, 0); return frame; }

    private static void Mchose(Action<string, bool> check)
    {
        var none = new HashSet<byte>();
        check("MCHOSE: the 3033 keyboards' cables take the configuration read on their 1:0 collection, and nothing on their update collection",
            Dongles.MchoseReads(0x200E, "MCHOSE K87S", 0x0001, 0x0000, 0, 65, new HashSet<byte> { 0x00 }, none).SequenceEqual([Dongles.MchoseRead.Config])
            && Dongles.MchoseReads(0x2021, "", 0x0001, 0x0000, 0, 65, new HashSet<byte> { 0x00 }, none).SequenceEqual([Dongles.MchoseRead.Config])
            && Dongles.MchoseReads(0x200E, "MCHOSE K87S", 0xFF00, 0x0001, 0, 65, new HashSet<byte> { 0x55 }, none).Count == 0
            && Dongles.MchoseReads(0x3033, "MCHOSE K99 V3 2.4G", 0xFF70, 0x71, 0, 64).Count == 0
            && Dongles.MchoseKind(0x2015, "") == DeviceKind.Keyboard);

        // The Qhw answer as the driver builds its header: version 1, flags 3, length 2, command 04 09, code 0, sequence 0, then
        // the charge status and the level, and the XOR of the flags on.
        static byte[] Qhw(byte status, byte level, byte flags = 3, byte sequence = 0)
        {
            byte[] body = [0x01, flags, 0x02, 0x04, 0x09, 0x00, sequence, status, level];
            byte xor = 0; foreach (var b in body[1..]) xor ^= b;
            return [0x4D, .. body, xor];
        }
        check("MCHOSE Qhw (G98 V3, K87 V3): the battery command with its XOR, status 1 charging and 2 full, and another header or a level of 0 none",
            Dongles.MchoseQhwRequest().SequenceEqual(Bytes("4D 01 03 00 04 09 00 00 0E"))
            && Dongles.MchoseQhw(Qhw(1, 55)) == new BatteryRead(55, true) && Dongles.MchoseQhw(Qhw(0, 80)) == new BatteryRead(80)
            && Dongles.MchoseQhw(Qhw(2, 100)) == new BatteryRead(100)
            && Dongles.MchoseQhw(Qhw(0, 0)) is null && Dongles.MchoseQhw(Qhw(0, 101)) is null && Dongles.MchoseQhw(Qhw(3, 50)) is null
            && Dongles.MchoseQhw(Qhw(0, 50, flags: 1)) is null && Dongles.MchoseQhw(Qhw(0, 50, sequence: 1)) is null
            && Dongles.MchoseQhw(Dongles.MchoseQhwRequest()) is null && Dongles.MchoseQhw(Padded(32, "4D 01 03 00 04 09 00 00 0E")) is null);
        check("MCHOSE Qhw: only the 0xFF80 collection of 1015, 2025 and 2023 that declares 0x4D, never one named CH585 (update mode)",
            Dongles.MchoseReads(0x1015, "MCHOSE G98 V3 2.4G", 0xFF80, 0x0001, 0, 32, new HashSet<byte> { 0x4D }, none).SequenceEqual([Dongles.MchoseRead.Qhw])
            && Dongles.MchoseReads(0x2023, "MCHOSE K87 V3", 0xFF80, 0x0001, 0, 32, new HashSet<byte> { 0x4D }, none).SequenceEqual([Dongles.MchoseRead.Qhw])
            && Dongles.MchoseReads(0x1015, "CH585", 0xFF80, 0x0001, 0, 32, new HashSet<byte> { 0x4D }, none).Count == 0
            && Dongles.MchoseReads(0x1015, "MCHOSE G98 V3 2.4G", 0xFF81, 0x0001, 0, 32, new HashSet<byte> { 0x4E }, none).Count == 0
            && Dongles.MchoseReads(0x1017, "MCHOSE G98 V3", 0xFF80, 0x0001, 0, 32, new HashSet<byte> { 0x4D }, none).Count == 0);
        check("MCHOSE: the ids M HUB lists in firmware-update mode are sent nothing on any collection, and 3016 (a G75 V2 cable) takes no BY read",
            new[] { 0x1017, 0x2024, 0x2026, 0x2010, 0x200F, 0x303D }.All(product =>
                Dongles.MchoseReads(product, "", 0xFF00, 0x0001, 65, 65, new HashSet<byte> { 0x55, 0x4D, 0x00 }, new HashSet<byte> { 0x11 }).Count == 0
                && Dongles.MchoseReads(product, "", 0xFF01, 0x0001, 65, 65, new HashSet<byte> { 0x55, 0x4D, 0x00 }, new HashSet<byte> { 0x11 }).Count == 0)
            && !Dongles.MchoseReads(0x3016, "", 0xFF02, 0x0002, 0, 20, new HashSet<byte> { 0x13 }, none).Contains(Dongles.MchoseRead.By));
        check("MCHOSE BY keyboards (3837:201B, 3016): the 13 4A read on their 0xFF02 collection only",
            Dongles.MchoseReads(0x201B, "MCHOSE 2.4G Wireless-G87_V2", 0xFF02, 0x0002, 0, 20, new HashSet<byte> { 0x13 }, none).SequenceEqual([Dongles.MchoseRead.By])
            && Dongles.MchoseReads(0x201B, "MCHOSE 2.4G Wireless-G87_V2", 0xFF02, 0x0002, 0, 20, new HashSet<byte> { 0x06 }, none).Count == 0
            && Dongles.MchoseReads(0x201B, "MCHOSE 2.4G Wireless-G87_V2", 0xFF01, 0x0001, 21, 65, new HashSet<byte> { 0x55, 0x4D }, new HashSet<byte> { 0x11 }).Count == 0);
        check("MCHOSE: the second 4K receiver 41E4:1102 takes the RealTek exchange on 0xFF01 alone",
            Dongles.Find(0x41E4, 0x1102, 0xFF01, "", 21) == Dongles.Protocol.MchoseRealtek && Dongles.Find(0x41E4, 0x1102, 0xFF0B, "", 21) is null);
        var shapeG9 = Shape(0x291D, 0x385D, 0xFF90, 1, output: 64, outputIds: [0x55], name: "MCHOSE G9 PRO");
        check("MCHOSE G9 PRO: the V9 Pro's read on any vendor collection declaring 0x55; the S9 PRO and V9 are not taken",
            Family(shapeG9) == "MCHOSE V9 Pro" && Family(shapeG9 with { UsagePage = 0xFF00 }) == "MCHOSE V9 Pro"
            && Family(shapeG9 with { UsagePage = 0x000C }) is null && Family(shapeG9 with { Name = "MCHOSE S9 PRO" }) is null
            && Family(shapeG9 with { Name = "MCHOSE V9" }) is null);
        var g7max = Shape(0x41E4, 0x1204, 0xFF00, 1, input: 0, output: 0, feature: 64, featureIds: [0x03]);
        var g7maxAnswer = Padded(64, "03 50 00 0B 02 01 C9");
        check("MCHOSE G7 MAX: the beiyin BASICS read as feature report 3 on 41E4:1204, 1001 and 1003, the level and charging bit",
            Family(g7max) == "MCHOSE G7 MAX" && Family(g7max with { Product = 0x1001 }) == "MCHOSE G7 MAX" && Family(g7max with { Product = 0x1003 }) == "MCHOSE G7 MAX"
            && Family(g7max with { Product = 0x1102 }) is null
            && new[] { 0x1204, 0x1001, 0x1003 }.All(product => Dongles.Find(0x41E4, product, 0xFF00, "", 64, -1, 1, 0, 0) is null)
            && EweadnMice.Beiyin(Padded(64, "03 50 00 0B 01 01 49")) is null
            && Family(g7max with { UsagePage = 0xFF01 }) is null && Family(g7max with { FeatureIds = new HashSet<byte> { 0x04 } }) is null
            && EweadnMice.BeiyinRequest()[..7].SequenceEqual(Bytes("03 22 50 00 02 4F 81"))
            && EweadnMice.Beiyin(g7maxAnswer) == new BatteryRead(73, true) && EweadnMice.Beiyin(EweadnMice.BeiyinRequest()) is null);
        check("EWEADN: the S7 and S9 series' other beiyin receivers take the same read",
            Family(Shape(0x372E, 0x108D, 0xFF00, 1, input: 0, output: 0, feature: 64, featureIds: [0x03])) == "EWEADN mouse"
            && Family(Shape(0x372E, 0x106C, 0xFF00, 1, input: 0, output: 0, feature: 64, featureIds: [0x03])) is null);
    }

    private static void SharedReceivers(Action<string, bool> check)
    {
        // F7's status as read back, report id 0 first: relay, keyboard %, mouse %, keyboard offline, mouse offline, takes, paired.
        byte[] Status(byte keyboard, byte mouse, byte keyboardOff, byte mouseOff, byte paired) => Padded(65, $"00 00 {keyboard:X2} {mouse:X2} {keyboardOff:X2} {mouseOff:X2} 01 {paired:X2}");
        check("Rongyuan F7: the keyboard's level, the mouse's where a mouse alone is paired, offline asleep, and the keyboard's when both are",
            RongyuanStatus.Battery(Status(54, 0, 0, 1, 1)) == new BatteryRead(54) && RongyuanStatus.Battery(Status(0, 77, 1, 0, 2)) == new BatteryRead(77)
            && RongyuanStatus.Battery(Status(0, 77, 1, 1, 2)) == new BatteryRead(null, Asleep: true) && RongyuanStatus.Battery(Status(60, 40, 0, 0, 3)) == new BatteryRead(60)
            && RongyuanStatus.Battery(Status(0, 0, 0, 0, 0)) is null && RongyuanStatus.Battery(RongyuanStatus.Request(false)) is null
            && RongyuanStatus.MouseBattery(Status(70, 55, 1, 0, 3)) == new BatteryRead(55) && RongyuanStatus.MouseBattery(Status(70, 0, 0, 0, 1)) is null
            && RongyuanStatus.Battery([.. Status(54, 0, 0, 1, 1)[..9], 0x01, .. Status(54, 0, 0, 1, 1)[10..]]) is null);
        var f7 = Shape(0x3151, 0x5042, 0xFFFF, 2, 2, 0, 0, 65, featureIds: [0x00]);
        check("Rongyuan F7: MonsGeek's and Akko's receiver list and VGN's on 0xFFFF / 2 only, never LINGBAO's 402D, named by their product string",
            Family(f7) == "Rongyuan receiver" && Family(f7 with { Vendor = 0x374A, Product = 0xA329 }) == "Rongyuan receiver"
            && Family(f7 with { Vendor = 0x391D, Product = 0x1A25 }) == "VGN" && Family(f7 with { Product = 0x5055 }) == "VGN"
            && Family(f7 with { Product = 0x5038 }) == "MonsGeek" && Family(f7 with { Product = 0x402D }) is null
            && Family(f7 with { UsagePage = 0xFF00 }) is null && Family(f7 with { Product = 0x502F }) is null && Family(f7 with { Product = 0x5031 }) is null
            && Family(f7 with { Vendor = 0x391D, Product = 0x1A0E }) == "Rongyuan receiver" 
            && BrandFamilies.Find(f7 with { Product = 0x5038, Name = "2.4G Wireless Keyboard" })!.Value.Family.DisplayName(f7 with { Product = 0x5038, Name = "2.4G Wireless Keyboard" }) is null
            && BrandFamilies.Find(f7 with { Product = 0x5038 })!.Value.Family.DisplayName(f7 with { Product = 0x5038 }) == "MonsGeek M2 V5 HE"
            && BrandFamilies.Find(f7 with { Vendor = 0x391D, Product = 0x1A25 })!.Value.Family.Kind(f7 with { Vendor = 0x391D, Product = 0x1A25 }) == DeviceKind.Mouse);
        var v13 = Shape(0x258A, 0x027B, 0xFF02, 2, input: 20, output: 20, outputIds: [0x13]);
        check("VGN 13 receivers: the V98pro V3, V87 V2 and N75 V2 receivers on 0xFF02 / 2 with report 0x13 only; Sinowealth's other ids not",
            Family(v13) == "VGN 13 receiver" && Family(v13 with { Vendor = 0x391D, Product = 0x2A01 }) == "VGN 13 receiver"
            && Family(v13 with { Product = 0x01BB }) != "VGN 13 receiver" && Family(v13 with { Product = 0x024E }) is null
            && Family(v13 with { UsagePage = 0xFF00, Usage = 1 }) is null && Family(v13 with { OutputIds = new HashSet<byte> { 0x06 } }) is null);
        var b3 = Shape(0x391D, 0x1A22, 0xFFC1, 1, input: 64, output: 64, outputIds: [0xB3]);
        check("VGN B3 mice: the Dragonfly 8K receivers on 0xFFC1 / 1 only, not their cables",
            Family(b3) == "VGN mouse" && Family(b3 with { Product = 0x1A3E }) == "VGN mouse" && Family(b3 with { Product = 0x1025 }) is null
            && Family(b3 with { UsagePage = 0xFF02, Usage = 2 }) is null);
    }

    private static void KnownFrames(Action<string, bool> check)
    {
        check("Compx: the official drivers' pairs on their 17-byte 0xFF02 / 2 collection, an ATK mouse's 0xFF05 keeping BitMouse, never an update-mode id, and the A9 Ultra kept on its own way",
            Dongles.Find(0x373B, 0x1273, 0xFF02, "", 0, 1, 2, 17, 17) == Dongles.Protocol.CompxFramed
            && Dongles.Find(0x373B, 0x1273, 0xFF05, "", 0, 1, 1, 64, 64) == Dongles.Protocol.BitMouse
            && Dongles.Find(0x3554, 0xF515, 0xFF00, "", 0, 1, 1, 64, 64) is null
            && Dongles.Find(0x391D, 0x1A05, 0xFF02, "", 0, 1, 2, 17, 17) == Dongles.Protocol.CompxFramed
            && Dongles.Find(0x3710, 0x3529, 0xFF02, "", 0, 1, 2, 17, 17) == Dongles.Protocol.CompxFramed
            && Dongles.Find(0x3554, 0xF809, 0xFF02, "", 0, 1, 2, 17, 17) == Dongles.Protocol.CompxFramed && Dongles.CompxKind(0x3554, 0xF809) == DeviceKind.Keyboard
            && Dongles.Find(0x3554, 0xF40A, 0xFF02, "", 0, 1, 2, 17, 17) is null && Dongles.Find(0x3554, 0xF2FF, 0xFF02, "", 0, 1, 2, 17, 17) is null
            && Dongles.Find(0x373B, 0x2151, 0xFF02, "", 0, 1, 2, 17, 17) is null
            && Dongles.Find(0x373B, 0x1119, 0xFF04, "", 0, 1, 2, 17, 17) == Dongles.Protocol.Compx
            && Dongles.Find(0x373B, 0x1273, 0xFF02, "", 0, 1, 2, 20, 20) is null);
        int[] otherPages = [0xFF00, 0xFF01, 0xFF04, 0xFF0B, 0xFF1C, 0xFFA0, 0x0001, 0x000C];
        check("Compx: every pair the official drivers add gets only its 17-byte 0xFF02 / 2 collection, nothing on another page or shape",
            Dongles.CompxOfficial.All(pair => Dongles.Find(pair.Vendor, pair.Product, 0xFF02, "", 0, 1, 2, 17, 17) == Dongles.Protocol.CompxFramed
                && Dongles.Find(pair.Vendor, pair.Product, 0xFF02, "", 65, 1, 2, 64, 64) is null
                && otherPages.All(page => Dongles.Find(pair.Vendor, pair.Product, page, "", 65, 1, 1, 64, 64) is null)));
        check("Pulsar X2 Wireless: VAXEE's feature read on 0xFF05 of 3710:9604, 5602 and 9605 only",
            Dongles.Find(0x3710, 0x9604, 0xFF05, "", 64) == Dongles.Protocol.Vaxee && Dongles.Find(0x3710, 0x5602, 0xFF05, "", 64) == Dongles.Protocol.Vaxee
            && Dongles.Find(0x3710, 0x9604, 0xFF02, "", 64) is null && Dongles.Find(0x3710, 0x9604, 0xFF05, "", 0) is null
            && Dongles.Find(0x3710, 0x9606, 0xFF05, "", 64) is null);
        check("LAMZU: the web driver's other receivers take the 0x83 exchange on interface 2, named by their product string",
            Dongles.F83Find(0x37B0, 0x0036) is { BrandOnly: true, Interface: 2 } lamzu && Dongles.F83Find(0x37B0, 0x0048) is null && Dongles.F83Find(0x37B0, 0x0054) is null && Dongles.F83Find(0x37B0, 0x000C) is null && Dongles.F83Collection(lamzu, 2, 0xFFFF, 65)
            && !Dongles.F83Collection(lamzu, 1, 0xFFFF, 65) && Dongles.F83Find(0x373E, 0x0016) is not null && Dongles.F83Find(0x37B0, 0x0005) is null
            && Dongles.F83Find(0x37B0, 0x0010) is { BrandOnly: false, Name: "LAMZU Inca" });
    }

    private static void AtkAndVgn(Action<string, bool> check)
    {
        check("ATK Evision keyboards: the HUB's receivers take the VGN frame on 0xFF1C / 0x92, named by the table, charging only as 2 at 9",
            Dongles.Find(0x373B, 0x118F, 0xFF1C, "", 0, -1, 0x0092, 64, 64) == Dongles.Protocol.Vgn && Dongles.AtkKeyboard(0x118F) == "ATK S87"
            && Dongles.Find(0x373B, 0x118E, 0xFF1C, "", 0, -1, 0x0092, 64, 64) is null
            && Dongles.AtkKeyboardBattery(Padded(64, "04 00 00 1A 06 00 00 00 5C 02")) == new BatteryRead(92, true)
            && Dongles.AtkKeyboardBattery(Padded(64, "04 00 00 1A 06 00 00 00 5C 01")) == new BatteryRead(92)
            && Dongles.AtkKeyboardBattery(Padded(64, "04 00 00 1A 06 00 00 00 00 00")) is null
            && Dongles.VgnBattery(Padded(64, "04 00 00 1A 06 00 00 00 5C 01")) == new BatteryRead(92, true));
        var n9 = Shape(0x373B, 0x12D4, 0xFF90, 1, input: 64, output: 64, outputIds: [0x55]);
        check("ATK N9 V2 PRO and ULTRA: their dongles take the Ultimate's read, named by model; the cables not",
            Family(n9) == "ATK Headset" && BrandFamilies.Find(n9)!.Value.Family.DisplayName(n9) == "ATK N9 V2 PRO"
            && Family(n9 with { Product = 0x12D6 }) == "ATK Headset" && Family(n9 with { Product = 0x12D3 }) is null);
        var hfd = Shape(0x373B, 0x1152, 0xFF60, 0x61, input: 33, output: 33, outputIds: [0x00]);
        check("ATK HFD keyboards: id_get_keyboard_value for the battery on the VIA collection, the answer echoed, an echo of the request none",
            Family(hfd) == "ATK HFD keyboard" && Family(hfd with { UsagePage = 0xFF00, Usage = 1 }) is null && Family(hfd with { Product = 0x1153 }) is null
            && AtkOfficial.HfdRequest()[..4].SequenceEqual(Bytes("00 02 96 24")) && AtkOfficial.HfdRequest().Length == 33
            && AtkOfficial.HfdBattery(Padded(33, "00 02 96 24 4B 00")) == new BatteryRead(75) && AtkOfficial.HfdBattery(Padded(33, "00 02 96 24 4B 01")) == new BatteryRead(75, true)
            && AtkOfficial.HfdBattery(AtkOfficial.HfdRequest()) is null && AtkOfficial.HfdBattery(Padded(33, "00 FF 96 24 4B 00")) is null);
        var bytech = Shape(0x391D, 0x2A08, 0xFF60, 0x61, input: 64, output: 64, outputIds: [0x09]);
        var request0 = AtkOfficial.BytechRequest(0x00, 0x00);
        check("Bytech keyboards: 87 00 00 01 00 00 with its check seeded by the report id (0x6E for 9), only on a collection declaring a nonzero report, the level and charging nibble",
            request0[..7].SequenceEqual(Bytes("00 87 00 00 01 00 00")) && request0[63] == 0x77
            && AtkOfficial.BytechRequest(0x09, 0x09) is { Length: 64 } r9 && r9[0] == 0x09 && r9[63] == 0x6E
            && BrandFamilies.Find(bytech) is { } found && found.Ways.Single().Name == "bytech.battery" && found.Ways.Single().Request[63] == 0x6E
            && Family(bytech with { Product = 0x2A03, OutputIds = new HashSet<byte> { 0x00 } }) is null
            && Family(bytech with { Product = 0x200B }) is null
            && AtkOfficial.BytechBattery(Padded(64, "00 87 00 00 01 00 02 3C 10")) == new BatteryRead(60, true)
            && AtkOfficial.BytechBattery(Padded(64, "00 87 00 00 01 00 02 3C 01")) == new BatteryRead(60)
            && AtkOfficial.BytechBattery(request0) is null);
        var nearlink = Shape(0x373B, 0x108C, 0xFFB2, 1, input: 64, output: 64, outputIds: [0x05]);
        check("ATK Nearlink mice: power_info on report 5 (4 on the cable) on 0xFFB2 / 1 only, its answer's level at 8",
            BrandFamilies.Find(nearlink) is { } nl && nl.Family.Brand == "ATK Nearlink mouse" && nl.Ways.Single().Request[..5].SequenceEqual(Bytes("05 00 00 1A 00"))
            && BrandFamilies.Find(nearlink with { OutputIds = new HashSet<byte> { 0x04, 0x05 } })!.Value.Ways.Single().Name == "atk.nearlink" 
            && BrandFamilies.Find(nearlink with { Product = 0x108B, OutputIds = new HashSet<byte> { 0x04 } })!.Value.Ways.Single().Name == "atk.nearlink-cable"
            && AtkOfficial.NearlinkBattery(Padded(64, "05 01 00 1A 06 00 00 00 50 00")) == new BatteryRead(80)
            && AtkOfficial.NearlinkBattery(AtkOfficial.NearlinkRequest(0x05)) is null && Family(nearlink with { UsagePage = 0xFF05 }) is null);
        var hechi = Shape(0x373B, 0x1196, 0xFFA0, 2, input: 64, output: 64, outputIds: [0x0B]);
        var info = Padded(64, "0B 13 3F 00"); info[17] = 1; info[18] = 77; info[20] = 1;
        var away = Padded(64, "0B 13 3F 00"); away[18] = 77;
        check("ATK A9 PRO MAX (HECHI): mouse info 0x13 on 0xFFA0 / 2, the level at 18 and charging at 17, a mouse not linked asleep, an error none",
            Family(hechi) == "ATK HECHI mouse" && AtkOfficial.HechiRequest()[..2].SequenceEqual(Bytes("0B 13"))
            && AtkOfficial.HechiBattery(info) == new BatteryRead(77, true) && AtkOfficial.HechiBattery(away) == new BatteryRead(null, Asleep: true)
            && AtkOfficial.HechiBattery(Padded(64, "0B 13 3F FF")) is null && AtkOfficial.HechiBattery(AtkOfficial.HechiRequest()) is null
            && AtkOfficial.HechiCableBattery(away) == new BatteryRead(77)
            && BrandFamilies.Find(hechi with { Product = 0x1147 })!.Value.Ways.Single().Parse(away) == new BatteryRead(77)
            && BrandFamilies.Find(hechi)!.Value.Ways.Single().Parse(away) == new BatteryRead(null, Asleep: true));
        var n87 = Padded(64, "F0 30 02 00 02 00 00 00 41 01");
        int sum = 0; for (int i = 4; i < 64; i++) sum += n87[i]; n87[3] = (byte)sum;
        check("VGN N87: the yjx battery command on the Gmouse receiver's 0xFF01 / 0x10 where 0xF0 is declared, its check and level",
            Dongles.N87Request()[..8].SequenceEqual(Bytes("F0 30 02 00 02 00 00 00"))
            && Dongles.Find(0xA8A5, 0x2255, 0xFF01, "", 0, 2, 0x10, 64, 64, -1, new HashSet<byte> { 0xF0 }) == Dongles.Protocol.Gmouse
            && Dongles.Find(0xA8A4, 0x2355, 0xFF01, "", 0, 2, 0x10, 64, 64, -1, new HashSet<byte> { 0xF0 }) == Dongles.Protocol.Gmouse
            && Dongles.Find(0xA8A4, 0x2355, 0xFF01, "", 0, 2, 0x10, 65, 65, -1, new HashSet<byte> { 0x00 }) is null
            && Dongles.Find(0xA8A5, 0x2255, 0xFF01, "", 0, 2, 0x10, 65, 65, -1, new HashSet<byte> { 0x00 }) == Dongles.Protocol.Gmouse
            && Dongles.Find(0xA8A4, 0x2255, 0xFF01, "MCHOSE G7", 0, 2, 0x10, 64, 64, -1, new HashSet<byte> { 0xF0 }) is null
            && Dongles.N87Battery(n87) == new BatteryRead(65, true) && Dongles.N87Battery([.. n87[..3], (byte)(n87[3] + 1), .. n87[4..]]) is null
            && Dongles.N87Battery(Dongles.N87Request()) is null);
    }

    private static void KeyboardsAndBigBrands(Action<string, bool> check)
    {
        // The first packet's answer without its report id: 55 10 18 00 00 .. then the info, its 16 link, 17 level, 18 charge.
        var info = Padded(64, "55 10 18 00 00 00 00 00"); info[8 + 16] = 2; info[8 + 17] = 58; info[8 + 18] = 0;
        var charging = (byte[])info.Clone(); charging[8 + 16] = 0; charging[8 + 18] = 1;
        check("Huafenda: the device-info read's first packet for 33- and 65-byte collections, its level at 17 and charge state at 18",
            SonixQuery.HuafendaRequest(33)[..9].SequenceEqual(Bytes("00 AA 10 18 00 00 00 00 00")) && SonixQuery.HuafendaRequest(33).Length == 33
            && SonixQuery.HuafendaRequest(65)[..9].SequenceEqual(Bytes("00 AA 10 38 00 00 00 01 00")) && SonixQuery.HuafendaRequest(65).Length == 65
            && SonixQuery.Huafenda(info) == new BatteryRead(58) && SonixQuery.Huafenda(charging) == new BatteryRead(58, true)
            && SonixQuery.Huafenda(SonixQuery.HuafendaRequest(33)[1..]) is null && SonixQuery.Huafenda(Padded(64, "55 10 18 18 00")) is null
            && SonixQuery.Huafenda(Padded(64, "55 10 18 00 00")) is null);
        var x87 = Shape(0x0C45, 0xFEFE, 0xFF60, 0x61, 3, 33, 33, outputIds: [0x00]);
        check("Huafenda: a second read on the X87's and the borrowed receiver's queried collection, and its own family for the drivers' other ids only",
            BrandFamilies.Find(x87)!.Value.Ways.Select(w => w.Name).SequenceEqual(["eweadn.query", "huafenda.info"])
            && BrandFamilies.Find(x87 with { Vendor = 0x05AC, Product = 0x024F })!.Value.Ways.Select(w => w.Name).SequenceEqual(["sonix.query", "huafenda.info"])
            && Family(x87 with { Product = 0xFEFD }) == "Huafenda keyboard" && Family(x87 with { Product = 0xFEFD, Interface = 4, InputLength = 65, OutputLength = 65 }) is null
            && Family(x87 with { Product = 0x8006, UsagePage = 0xFF68, Interface = 2, InputLength = 65, OutputLength = 65 }) == "Huafenda keyboard"
            && Family(x87 with { Product = 0x8006, UsagePage = 0xFF67, Interface = 3, InputLength = 65, OutputLength = 65 }) is null
            && Family(x87 with { Product = 0x8006 }) is null
            && Family(x87 with { Product = 0xFEFE, Interface = 4, InputLength = 65, OutputLength = 65 }) is null
            && Family(x87 with { Product = 0x7040 }) is null && Family(x87 with { Product = 0x8012, Usage = 0x62 }) is null);
        var a9 = Shape(0x38A6, 0x001D, 0xFF90, 1, input: 64, output: 64, outputIds: [0x55], name: "AULA-A9");
        check("AULA A9: the base named AULA-A9 on 0xFF90 / 1, asked 55 65 00, the ATK headsets' answer",
            BrandFamilies.Find(a9) is { } found && found.Family.Brand == "AULA headset" && found.Ways.Single().Request[..3].SequenceEqual(Bytes("55 65 00"))
            && Family(a9 with { Name = "AULA-A9 PC" }) == "AULA headset" && Family(a9 with { Name = "AULA G9" }) is null && Family(a9 with { UsagePage = 0xFF00 }) is null
            && AulaMice.AulaA9(Bytes("55 65 50 04")) == new BatteryRead(80, true) && AulaMice.AulaA9(Bytes("55 65 50 02")) == new BatteryRead(80)
            && AulaMice.AulaA9(Bytes("55 65 50 1A")) == new BatteryRead(null, Asleep: true) && AulaMice.AulaA9(Bytes("55 65 00 00")) is null);
        check("Corsair: the Slipstream receivers and the added headsets in the catalog, a cable's answer through route 8 read",
            Corsair.Find(0x1B1C, 0x1BA6) is { Kind: DeviceKind.Other, Bare: false } && Corsair.Find(0x1B1C, 0x0A69) is { Cable: true, Receiver: 0x0A6B }
            && Corsair.Find(0x1B1C, 0x2A12) is { Session: true } && Corsair.Value(Bytes("01 00 02 00 4C 03"), Corsair.RouteReceiver) == 844 && Corsair.Value(Bytes("01 00 02 00 4C 03")) is null
            && Corsair.Value(Bytes("01 01 02 00 4C 03")) == 844 && Corsair.Find(0x1B1C, 0x1B62) is { Kind: DeviceKind.Keyboard }
            && !Corsair.AsksCharging(Corsair.Find(0x1B1C, 0x0A73)!) && Corsair.AsksCharging(Corsair.Find(0x1B1C, 0x0A42)!)
            && Corsair.Value(Bytes("01 02 00 4C 03")) == 844 && Corsair.Value(Bytes("01 00 01 00 4C 03")) is null
            && Dongles.IsCompxFramed(0x1B1C, 0x2B34));
        check("Corsair Void: the older Void ids on 0xFFC5 / 1",
            Family(Shape(0x1B1C, 0x1B27, 0xFFC5, 1, input: 5, output: 2, outputIds: [0xC9])) == "Corsair Void"
            && Family(Shape(0x1B1C, 0x1B7F, 0xFFC5, 1, input: 5, output: 2, outputIds: [0xC9])) is null);
        check("Razer, SteelSeries and ASUS: the Orochi V2 2026 receiver, the Joro, the GameBuds X and the TX Mini Miku",
            Razer.Find(0x00E0) is { Transaction: 0x1F } && Razer.Find(0x02CD) is { Kind: DeviceKind.Keyboard, Interface: 3 }
            && SteelSeriesHeadsets.Find(0x2317) == SteelSeriesHeadsets.Exchange.GameBuds && Asus.Find(0x0B05, 0x1C5A) is { Family: Asus.Family.Mouse });
    }

    private static void SessionAndSerial(Action<string, bool> check)
    {
        var yogo = Shape(0x373B, 0x1200, 0xFF60, 0x61, 3, input: 33, output: 33, outputIds: [0x00]);
        var answer = Padded(32, "AA 30 00 00 00 02 00 00 4E 02");
        check("ATK YOGO 75: start then power_info as ATK HUB sends them, no end, the answer by its sequence, the level at 8 and 2 at 9 charging",
            BrandFamilies.Find(yogo) is { } found && found.Family.Brand == "ATK Evision V2 keyboard"
            && found.Ways.Single() is { } way && way.Before.Count == 1 && way.After.Count == 0
            && way.Before[0][..8].SequenceEqual(Bytes("00 AA 10 00 00 00 01 00")) && way.Request[..8].SequenceEqual(Bytes("00 AA 30 00 00 00 02 00"))
            && way.Request.Length == 33 && BrandFamilies.Find(yogo with { Product = 0x119C, InputLength = 65, OutputLength = 65 })!.Value.Ways.Single().Request.Length == 65
            && AtkOfficial.YogoBattery(answer) == new BatteryRead(78, true) && AtkOfficial.YogoBattery(Padded(32, "AA 30 00 00 00 02 00 00 4E 01")) == new BatteryRead(78)
            && AtkOfficial.YogoBattery(Padded(32, "AA 10 00 00 00 01 00 00 4E 02")) is null && AtkOfficial.YogoBattery(Padded(32, "AA 30 FF 00 00 02 00 00 4E 02")) is null
            && AtkOfficial.YogoBattery(way.Request[1..]) is null && AtkOfficial.YogoBattery(Padded(32, "AA 30 00 00 00 02 00 00 65 00")) is null
            && Family(yogo with { UsagePage = 0xFF00, Usage = 1 }) is null && Family(yogo with { Product = 0x1201 }) is null);
        check("ATK N9 Pro and Ultra: their consumer collection only finds them (read on the serial port), 02 00 03 asked, the level and charging bit at 4",
            Dongles.Find(0x373B, 0x11CB, 0x000C, "", 0, -1, 0x0001, 2, 0) == Dongles.Protocol.AtkSerial
            && Dongles.Find(0x373B, 0x1203, 0x000C, "", 0, -1, 0x0001, 2, 0) == Dongles.Protocol.AtkSerial
            && Dongles.Find(0x373B, 0x11CB, 0xFF90, "", 0, -1, 0x0001, 64, 64) is null && Dongles.Find(0x373B, 0x11CB, 0x0001, "", 0, -1, 0x0002, 8, 0) is null
            && AtkHeadsets.SerialRequest().Length == 64 && AtkHeadsets.SerialRequest()[..3].SequenceEqual(Bytes("02 00 03"))
            && AtkHeadsets.SerialBattery(Padded(64, "03 00 03 00 D7")) == new BatteryRead(87, true) && AtkHeadsets.SerialBattery(Padded(64, "03 00 03 00 57")) == new BatteryRead(87)
            && AtkHeadsets.SerialBattery(Padded(64, "03 80 83 00 57")) is null && AtkHeadsets.SerialBattery(AtkHeadsets.SerialRequest()) is null);
    }
}

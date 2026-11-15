using BrimDeck.Core;
using static BrandFamilyTests;

// The keyboard brands declared as families: which collections they take (and the ones they must not), their requests, the
// answers from their sources, and what is not a reading (the request read back, zeros, other frames).
static class BrandsKeyboardTests
{
    private static string[] Names((BrandFamily Family, IReadOnlyList<BrandWay> Ways)? found) => found is { } f ? [.. f.Ways.Select(way => way.Name)] : [];
    private static byte[] Hex(string text) => [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(each => Convert.ToByte(each, 16))];
    private static byte[] Padded(int length, string head) { var frame = new byte[length]; Hex(head).CopyTo(frame, 0); return frame; }
    // A frame whose last byte is the low byte of the sum of the others.
    private static byte[] Summed(int length, string head)
    {
        var frame = Padded(length, head);
        int sum = 0; for (int i = 0; i < length - 1; i++) sum += frame[i];
        frame[^1] = (byte)sum;
        return frame;
    }

    public static void Run(Action<string, bool> check)
    {
        Alienware(check);
        Urx(check);
        Lofree(check);
        Rongyuan(check);
        Sonix(check);
    }

    private static void Alienware(Action<string, bool> check)
    {
        var vendor = Shape(0x413C, 0x211D, 0xFF00, 1, 1, input: 65, output: 0, feature: 65, featureIds: [0x00], name: "Alienware Pro KB Gaming Receiver");
        check("Alienware: the receiver's vendor collection takes the battery read, byte-swapped page too",
            Names(BrandFamilies.Find(vendor)).SequenceEqual(["alienware.battery"])
            && Names(BrandFamilies.Find(vendor with { UsagePage = 0x00FF })).SequenceEqual(["alienware.battery"]));
        check("Alienware: not Dell's other products on 413C, nor another interface or usage of the receiver",
            BrandFamilies.Find(vendor with { Product = 0x2113 }) is null && BrandFamilies.Find(vendor with { Interface = 0 }) is null
            && BrandFamilies.Find(vendor with { Usage = 2 }) is null && BrandFamilies.Find(Shape(0x413C, 0x211D, 0x0001, 6, 1)) is null);
        var request = BrimDeck.Core.Alienware.Request();
        check("Alienware: the request is feature report 0 set to 0E A1 (65 bytes)",
            request.Length == 65 && request[0] == 0x00 && request[1] == 0x0E && request[2] == 0xA1 && request.Skip(3).All(b => b == 0));
        check("Alienware: the captured answers (37 %, 92 %, 19 % charging); state reports, the request read back and over 100 are none",
            BrimDeck.Core.Alienware.Battery(Padded(64, "0E A1 25 00")) == new BatteryRead(37)
            && BrimDeck.Core.Alienware.Battery(Padded(64, "0E A1 5C 00")) == new BatteryRead(92)
            && BrimDeck.Core.Alienware.Battery(Padded(64, "0E A1 13 04")) == new BatteryRead(19, true)
            && BrimDeck.Core.Alienware.Battery(Padded(64, "0E 05 00 AC")) is null && BrimDeck.Core.Alienware.Battery(Padded(64, "0E A1 00 00")) is null
            && BrimDeck.Core.Alienware.Battery(Padded(64, "0E A1 65 00")) is null);
    }

    private static void Urx(Action<string, bool> check)
    {
        var via = Shape(0x36B0, 0x3002, 0xFF60, 0x61, 1, input: 33, output: 33, outputIds: [0x00], name: "URX Core75 Wireless");
        check("URX: the Core75's VIA collection takes the read",
            Names(BrandFamilies.Find(via)).SequenceEqual(["urx.via"]));
        check("URX: the shared receiver id under another name (PMO, a gamepad dongle) and other collections are not asked",
            BrandFamilies.Find(via with { Name = "PMO 2.4G Receiver" }) is null && BrandFamilies.Find(via with { Name = "RDMCTMZT Wireless 2.4G Dongle" }) is null
            && BrandFamilies.Find(via with { Interface = 2 }) is null && BrandFamilies.Find(via with { OutputLength = 65 }) is null
            && BrandFamilies.Find(via with { Product = 0x3009 }) is null);
        check("URX: the request is VIA's custom get, channel 0, value 7",
            BrimDeck.Core.Urx.Request() is { Length: 33 } r && r.Take(4).SequenceEqual(new byte[] { 0x00, 0x08, 0x00, 0x07 }) && r.Skip(4).All(b => b == 0));
        check("URX: the captured answer (76 %); the refusal, another value, zero and over 100 are none",
            BrimDeck.Core.Urx.Battery(Padded(32, "08 00 07 4C")) == new BatteryRead(76)
            && BrimDeck.Core.Urx.Battery(Padded(32, "FF 07")) is null && BrimDeck.Core.Urx.Battery(Padded(32, "08 00 06 4C")) is null
            && BrimDeck.Core.Urx.Battery(Padded(32, "08 00 07 00")) is null && BrimDeck.Core.Urx.Battery(Padded(32, "08 00 07 65")) is null);
    }

    private static void Lofree(Action<string, bool> check)
    {
        var control = Shape(0x05AC, 0x024F, 0xFF02, 2, 1, input: 17, output: 17, outputIds: [0x08], name: "2.4G Wireless Receiver");
        var found = BrandFamilies.Find(control);
        check("Lofree: the Flow Lite84 receiver's control collection takes the read, the online query asked first as its gate",
            found is { Family.Brand: "Lofree" } && Names(found).SequenceEqual(["lofree.flow"]) && found.Value.Ways[0].Before.Count == 0
            && found.Value.Ways[0].Gate is { } gate && gate.SequenceEqual(BrimDeck.Core.Lofree.Request(0x03)) && found.Value.Ways[0].Gated is not null);
        // Apple's wired Aluminium Keyboard on the same ids: its keyboard, consumer and vendor collections.
        bool apple = new[] { Shape(0x05AC, 0x024F, 0x0001, 6, 0, name: "Apple Keyboard"), Shape(0x05AC, 0x024F, 0x000C, 1, 1, name: "Apple Keyboard"),
            Shape(0x05AC, 0x024F, 0xFF00, 1, 1, input: 17, output: 17, outputIds: [0x08], name: "Apple Keyboard"),
            control with { Name = "Apple Keyboard" } }.All(shape => BrandFamilies.Find(shape) is null);
        check("Lofree: Apple's own keyboard on 05AC:024F is never asked", apple);
        check("Lofree: not the receiver's other collections nor one whose output report cannot take the frame",
            BrandFamilies.Find(control with { UsagePage = 0xFF04 }) is null && BrandFamilies.Find(control with { OutputLength = 20 }) is null
            && BrandFamilies.Find(control with { Interface = 0 }) is null);
        var battery = BrimDeck.Core.Lofree.Request(0x04);
        check("Lofree: the requests carry the keyboard flag and sum to 0x55",
            battery.Length == 17 && battery[0] == 0x08 && battery[1] == 0x04 && battery[5] == 0x80 && battery[16] == 0xC9
            && BrimDeck.Core.Lofree.Request(0x03)[16] == 0xCA && battery.Sum(b => b) % 256 == 0x55);
        static byte[] Reply(byte command, byte level, byte charging, byte status = 0)
        {
            var frame = Padded(17, $"08 {command:X2} {status:X2} 00 00 00 {level:X2} {charging:X2} 0E CE");
            int sum = 0; for (int i = 0; i < 16; i++) sum += frame[i];
            frame[16] = (byte)(0x55 - sum);
            return frame;
        }
        var bad = Reply(0x04, 57, 1); bad[16] ^= 0xFF;
        check("Lofree: a battery answer (57 %, charging); the online answer, a bad checksum, a status, zero and the request read back are none",
            BrimDeck.Core.Lofree.Battery(Reply(0x04, 57, 1)) == new BatteryRead(57, true) && BrimDeck.Core.Lofree.Battery(Reply(0x04, 80, 0)) == new BatteryRead(80)
            && BrimDeck.Core.Lofree.Battery(Reply(0x03, 1, 0)) is null && BrimDeck.Core.Lofree.Battery(bad) is null
            && BrimDeck.Core.Lofree.Battery(Reply(0x04, 57, 0, status: 1)) is null && BrimDeck.Core.Lofree.Battery(Reply(0x04, 0, 0)) is null
            && BrimDeck.Core.Lofree.Battery(battery) is null);
        var badOnline = Reply(0x03, 1, 0); badOnline[16] ^= 0xFF;
        check("Lofree: the online answer lets the read go on with 1, says asleep with 0; a bad checksum, a status, the battery answer and the request read back are none",
            BrimDeck.Core.Lofree.Online(Reply(0x03, 1, 0)) == true && BrimDeck.Core.Lofree.Online(Reply(0x03, 0, 0)) == false
            && BrimDeck.Core.Lofree.Online(Reply(0x03, 2, 0)) is null && BrimDeck.Core.Lofree.Online(badOnline) is null
            && BrimDeck.Core.Lofree.Online(Reply(0x03, 1, 0, status: 1)) is null && BrimDeck.Core.Lofree.Online(Reply(0x04, 1, 0)) is null
            && BrimDeck.Core.Lofree.Online(BrimDeck.Core.Lofree.Request(0x03)) is null);
    }

    private static void Rongyuan(Action<string, bool> check)
    {
        var config = Shape(0x3151, 0x5026, 0xFFFF, 2, 2, input: 0, output: 0, feature: 65, featureIds: [0x00]);
        var named = new (int Vendor, int Product, string Brand, string Way, string Model)[]
        {
            (0x3151, 0x5026, "MonsGeek", "monsgeek.status", "MonsGeek FUN60 Pro"), (0x3151, 0x5038, "MonsGeek", "monsgeek.status", "MonsGeek M2 V5 HE"),
            (0x3151, 0x4011, "AJAZZ", "ajazz.status", "AJAZZ AK870"), (0x391D, 0x2A05, "VGN", "vgn.status", "VGN Neon75 Air"),
            (0x3151, 0x502B, "CAROTMAS", "carotmas.status", "CAROTMAS Mars68"),
        };
        check("Rongyuan: each listed receiver's configuration collection takes its brand's status read",
            named.All(each => BrandFamilies.Find(config with { Vendor = each.Vendor, Product = each.Product }) is { } found
                && found.Family.Brand == each.Brand && Names(found).SequenceEqual([each.Way])
                && found.Family.DisplayName(config with { Vendor = each.Vendor, Product = each.Product }) == each.Model));
        check("Rongyuan: not LINGBAO's 3151:402D, the cables (502F, 5030), another 3151 product, nor the receiver's other collections",
            BrandFamilies.Find(config with { Product = 0x402D }) is null && BrandFamilies.Find(config with { Product = 0x502F }) is null
            && BrandFamilies.Find(config with { Product = 0x5030 }) is null && BrandFamilies.Find(config with { Product = 0x4012 }) is null
            && BrandFamilies.Find(config with { Usage = 1 }) is null && BrandFamilies.Find(config with { FeatureLength = 64 }) is null);
        var bare = RongyuanStatus.Request(false);
        var summed = RongyuanStatus.Request(true);
        check("Rongyuan: MonsGeek's bare F7, and F7 with Rongyuan's checksum (00 F7 00 00 00 00 00 00 08)",
            bare.Length == 65 && bare[1] == 0xF7 && bare.Where((b, i) => i != 1).All(b => b == 0)
            && summed.Length == 65 && summed.Take(9).SequenceEqual(Hex("00 F7 00 00 00 00 00 00 08")) && summed.Skip(9).All(b => b == 0)
            && BrandFamilies.Find(config) is { } monsgeek && monsgeek.Ways[0].Request.SequenceEqual(bare)
            && BrandFamilies.Find(config with { Product = 0x4011 }) is { } ajazz && ajazz.Ways[0].Request.SequenceEqual(summed));
        check("Rongyuan: the measured statuses (AK870 41 %, Neon75 Air 82 %, Mars68 72 %), a keyboard away is asleep",
            RongyuanStatus.Battery(Padded(65, "00 00 29 00 00 01 01 01 60")) == new BatteryRead(41)
            && RongyuanStatus.Battery(Padded(65, "00 00 52 00 00 01 01 01")) == new BatteryRead(82)
            && RongyuanStatus.Battery(Padded(65, "00 00 48 00 00 01 01 01")) == new BatteryRead(72)
            && RongyuanStatus.Battery(Padded(65, "00 00 3D 00 01 01 01 01")) == new BatteryRead(null, Asleep: true));
        check("Rongyuan: the request read back, zeros, a mouse alone without a level, a zero or too high level are none; a mouse alone away is asleep",
            RongyuanStatus.Battery(bare) is null && RongyuanStatus.Battery(summed) is null && RongyuanStatus.Battery(new byte[65]) is null
            && RongyuanStatus.Battery(Padded(65, "00 00 29 00 00 00 01 02")) is null
            && RongyuanStatus.Battery(Padded(65, "00 00 29 00 00 01 01 02")) == new BatteryRead(null, Asleep: true)
            && RongyuanStatus.Battery(Padded(65, "00 01 00 00 00 01 01 01")) is null
            && RongyuanStatus.Battery(Padded(65, "00 00 65 00 00 01 01 01")) is null);
    }

    private static void Sonix(Action<string, bool> check)
    {
        var raw = Shape(0x05AC, 0x024F, 0xFF60, 0x61, 3, input: 33, output: 33, outputIds: [0x00], name: "2.4G Wireless Receiver");
        check("Sonix: the borrowed 05AC:024F receiver's raw collection on interface 3, or named by the keyboard, takes the query",
            Names(BrandFamilies.Find(raw)).SequenceEqual(["sonix.query", "huafenda.info"])
            && Names(BrandFamilies.Find(raw with { Interface = 2, Name = "F108Pro Dongle" })).SequenceEqual(["sonix.query", "huafenda.info"])
            && BrandFamilies.Find(raw with { Name = "F108Pro Dongle" })?.Family.DisplayName(raw with { Name = "F108Pro Dongle" }) == "AULA F108 Pro"
            && BrandFamilies.Find(raw with { Interface = 2, Name = "GANSS GS3104T-Pro" })?.Family.DisplayName(raw with { Name = "GANSS" }) == "GANSS GS3104T-Pro");
        check("Sonix: not Apple's keyboard, another interface without a name, nor the 65-byte collection beside it",
            BrandFamilies.Find(Shape(0x05AC, 0x024F, 0x0001, 6, 0, name: "Apple Keyboard")) is null
            && BrandFamilies.Find(raw with { Interface = 4 }) is null && BrandFamilies.Find(raw with { InputLength = 65, OutputLength = 65 }) is null
            && BrandFamilies.Find(raw with { Usage = 0x62 }) is null);
        var x87 = raw with { Vendor = 0x0C45, Product = 0xFEFE, Name = "" };
        check("Sonix: EWEADN X87's receiver on interface 3 only, named by its product string (AULA's F87 Pro V2D shares 0C45:FEFE)",
            BrandFamilies.Find(x87) is { Family.Brand: "EWEADN" } found && Names(found).SequenceEqual(["eweadn.query", "huafenda.info"]) && found.Family.DisplayName(x87) is null
            && BrandFamilies.Find(x87 with { Interface = 4, InputLength = 65, OutputLength = 65 }) is null && BrandFamilies.Find(x87 with { Interface = 4 }) is null
            && BrandFamilies.Find(x87 with { Product = 0xFEFD }) is { Family.Brand: "Huafenda keyboard" });
        check("Sonix: the query is 00 20 01 ... 21",
            SonixQuery.Request() is { Length: 33 } q && q[1] == 0x20 && q[2] == 0x01 && q[32] == 0x21 && q.Skip(3).Take(29).All(b => b == 0));
        var bad = Summed(32, "20 01 00 61"); bad[31] ^= 0x01;
        check("Sonix: the captured answers (GANSS 97 %, X87 100 %); zero, a bad checksum, another command and the request read back are none",
            SonixQuery.Battery(Summed(32, "20 01 00 61")) == new BatteryRead(97) && SonixQuery.Battery(Summed(32, "20 01 00 64")) == new BatteryRead(100)
            && Summed(32, "20 01 00 61")[31] == 0x82 && Summed(32, "20 01 00 64")[31] == 0x85
            && SonixQuery.Battery(Summed(32, "20 01 00 00")) is null && SonixQuery.Battery(bad) is null && SonixQuery.Battery(Summed(32, "20 02 00 61")) is null
            && SonixQuery.Battery(SonixQuery.Request()[1..]) is null && SonixQuery.Battery(Summed(32, "20 01 00 65")) is null);
    }
}

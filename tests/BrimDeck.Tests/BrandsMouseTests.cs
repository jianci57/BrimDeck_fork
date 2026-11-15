using BrimDeck.Core;
using static BrandFamilyTests;

// The mouse brand families (src/BrimDeck.Core/Brands): which collections each takes and which it leaves to the readers that
// have them, the requests byte for byte, the sources' sample answers, and the request read back (or another frame) refused.
static class BrandsMouseTests
{
    private static readonly List<BrandWay> Seen = [];

    private static IReadOnlyList<string>? Ways(HidShape shape, string brand)
    {
        if (BrandFamilies.Find(shape) is not { } found || found.Family.Brand != brand) return null;
        Seen.AddRange(found.Ways);
        return [.. found.Ways.Select(way => way.Name)];
    }
    private static bool Named(HidShape shape, string brand, params string[] names) => Ways(shape, brand) is { } ways && ways.SequenceEqual(names);
    private static bool Untaken(HidShape shape) => BrandFamilies.Find(shape) is null;
    private static BrandWay Way(HidShape shape, string name) => BrandFamilies.Find(shape)!.Value.Ways.Single(way => way.Name == name);
    private static byte[] Pad(int length, params byte[] head) => BrandFamilies.Frame(length, head);
    private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", ""));
    private static bool Zeros(byte[] frame, int from) => frame.Skip(from).All(b => b == 0);

    public static void Run(Action<string, bool> check)
    {
        Seen.Clear();
        Glorious(check);
        Vancer(check);
        Keychron(check);
        Darmoshark(check);
        Eweadn(check);
        Aula(check);
        Thunderobot(check);
        HyperX(check);
        Finalmouse(check);
        Incott(check);
        Ryunix(check);
        Redragon(check);
        Scyrox(check);
        Pulsar(check);
        Others(check);
        check("Mouse families: every read within the limits (1 to 2 attempts, at most 1000 ms, listening at most 600 ms)",
            Seen.Count > 0 && Seen.All(way => way.Attempts is >= 1 and <= 2 && way.TimeoutMs <= (way.Transport == WayTransport.Listen ? 600 : 1000)));
    }

    private static void Glorious(Action<string, bool> check)
    {
        var receiver = Shape(0x258A, 0x2022, 0xFFFF, 0, input: 0, output: 0, feature: 65, featureIds: [0x00]);
        check("Glorious: the Model O and Model D receivers and the 4K/8K mice on their 65-byte feature collection take the 0x83 read",
            Named(receiver, "Glorious", "glorious.f83") && Named(Shape(0x258A, 0x2023, 0xFFA0, 1, feature: 65, featureIds: [0x00]), "Glorious", "glorious.f83")
            && Named(Shape(0x258A, 0x2036, 0xFFFF, 0, feature: 65, featureIds: [0x00]), "Glorious", "glorious.f83")
            && Named(Shape(0x258A, 0x201B, 0xFFFF, 0, feature: 65, featureIds: [0x00]), "Glorious", "glorious.f83")
            && BrandFamilies.Find(Shape(0x258A, 0x201B, 0xFFFF, 0, feature: 65, featureIds: [0x00]))!.Value.Family.DisplayName(Shape(0x258A, 0x201B, 0xFFFF)) == "Glorious Model O 2 PRO 4K/8K");
        check("Glorious: the older mice on their cables (2011, 2012), another 258A product, the collection without the feature report, or one numbering another report is not asked",
            Untaken(Shape(0x258A, 0x2011, 0xFFA0, 1, feature: 65, featureIds: [0x00])) && Untaken(Shape(0x258A, 0x2012, 0xFFFF, 0, feature: 65, featureIds: [0x00]))
            && Untaken(Shape(0x258A, 0x2099, 0xFFFF, 0, feature: 65, featureIds: [0x00])) && Untaken(Shape(0x258A, 0x2035, 0xFFFF, 1, feature: 0))
            && BrandFamilies.Find(Shape(0x258A, 0x2022, 0xFFFF, 0, feature: 65, featureIds: [0x05])) is { Ways.Count: 0 });
        check("Glorious: the request is 00 00 00 02 02 00 83 padded to 65 bytes, about 100 ms before the read",
            MouseShapes.Request83().SequenceEqual(Pad(65, 0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x83)) && Way(receiver, "glorious.f83") is { GapMs: 100, FeatureLength: 65 });
        check("Glorious: A1 answers read (CORE's capture on a 201B cable: 100 %; 75 %, 1 charging, 2 charged), A4 is asleep; the request read back, A0, A2, 0 % and 101 % are none",
            MouseShapes.Battery83(Pad(65, [0x00, .. Hex("a1 00 02 02 00 83 00 64")])) == new BatteryRead(100)
            && MouseShapes.Battery83(Pad(65, 0x00, 0xA1, 0, 0, 0, 0, 0x83, 0x00, 0x4B)) == new BatteryRead(75)
            && MouseShapes.Battery83(Pad(65, 0x00, 0xA1, 0, 0, 0, 0, 0x83, 0x01, 0x4B)) == new BatteryRead(75, true)
            && MouseShapes.Battery83(Pad(65, 0x00, 0xA1, 0, 0, 0, 0, 0x83, 0x02, 0x64)) == new BatteryRead(100)
            && MouseShapes.Battery83(Pad(65, 0x00, 0xA4, 0, 0, 0, 0, 0x83, 0x00, 0x50)) == new BatteryRead(null, Asleep: true)
            && MouseShapes.Battery83(MouseShapes.Request83()) is null && MouseShapes.Battery83(Pad(65, 0x00, 0xA2, 0, 0, 0, 0, 0x83, 0x00, 0x00)) is null
            && MouseShapes.Battery83(Pad(65, 0x00, 0xA0, 0, 0, 0, 0, 0x83, 0x00, 0x50)) is null
            && MouseShapes.Battery83(Pad(65, 0x00, 0xA1, 0, 0, 0, 0, 0x83, 0x00, 0x00)) is null && MouseShapes.Battery83(Pad(65, 0x00, 0xA1, 0, 0, 0, 0, 0x83, 0x00, 101)) is null
            && MouseShapes.Battery83(Pad(65, 0x00, 0xA1, 0, 0, 0, 0, 0x81, 0x00, 0x50)) is null);
    }

    private static void Vancer(Action<string, bool> check)
    {
        check("Vancer: the Akitsu's cable and receiver take the 0x83 read; LAMZU's Maya X on the same vendor id stays with its reader",
            Named(Shape(0x373E, 0x0067, 0xFFFF, 0, feature: 65, featureIds: [0x00]), "Vancer", "vancer.f83")
            && Named(Shape(0x373E, 0x0066, 0xFFFF, 0, feature: 65, featureIds: [0x00]), "Vancer", "vancer.f83")
            && Untaken(Shape(0x373E, 0x001E, 0xFFFF, 0, 2, feature: 65, featureIds: [0x00])) && Untaken(Shape(0x373E, 0x0068, 0xFFFF, 0, feature: 65, featureIds: [0x00])));
        check("Vancer: OpenMouse's capture of both paths, A1 00 02 02 00 83 00 64, reads 100 %",
            MouseShapes.Battery83(Pad(65, [0x00, .. Hex("a1 00 02 02 00 83 00 64")])) == new BatteryRead(100));
    }

    private static void Keychron(Action<string, bool> check)
    {
        var link = Shape(0x3434, 0xD028, 0xFFC1, 1, 4, outputIds: [0xB3]);
        check("Keychron: any 3434 collection on 0xFFC1 / 1 takes the status read, as an output or a feature report where it declares 0xB3 so",
            Named(link, "Keychron mouse", "keychron.status") && Named(Shape(0x3434, 0xD060, 0xC1FF, 0x0100, outputIds: [0xB3], featureIds: [0xB3]), "Keychron mouse", "keychron.status", "keychron.status-feature")
            && BrandFamilies.Find(Shape(0x3434, 0xD048, 0xFFC1, 1, outputIds: [0xB5])) is { Ways.Count: 0 });
        check("Keychron: the 8K Nordic mice and receivers, a keyboard's VIA collection (0xFF60) and the 1k page (0x8C) are not asked",
            Untaken(Shape(0x3434, 0xD077, 0xFFC1, 1, outputIds: [0xB3])) && Untaken(Shape(0x3434, 0xD05B, 0xFFC1, 1, outputIds: [0xB3]))
            && Untaken(Shape(0x3434, 0x0A50, 0xFF60, 0x61, outputIds: [0x00])) && Untaken(Shape(0x3434, 0xD060, 0x008C, 1, featureIds: [0x51])));
        var reply = Pad(64, 0xB4, 0x06); reply[20] = 0x55;
        var charging = Pad(64, 0xB4, 0x06); charging[20] = 0xD5;
        var over = Pad(64, 0xB4, 0x06); over[20] = 0x7F;
        check("Keychron: B3 06; B4 06 reads the low 7 bits of 20 (85 %, bit 7 charging); the request read back, 0 % and 127 % are none",
            KeychronMice.Request().SequenceEqual(new byte[] { 0xB3, 0x06 }) && KeychronMice.Battery(reply) == new BatteryRead(85) && KeychronMice.Battery(charging) == new BatteryRead(85, true)
            && KeychronMice.Battery(Pad(64, 0xB3, 0x06)) is null && KeychronMice.Battery(Pad(64, 0xB4, 0x06)) is null && KeychronMice.Battery(over) is null
            && KeychronMice.Battery(Pad(64, 0xB4, 0x07, 0x55)) is null);
        check("Keychron's platform: the JZM5 receiver (page 0x8C), the RIDLAG Owl X 8K and the Motospeed X6 receivers (0xFFC1) take the status read, each its own brand",
            Named(Shape(0x362D, 0xD107, 0x008C, 0x01, outputIds: [0xB3]), "JD Jingzao mouse", "jingzao.status")
            && Named(Shape(0x362D, 0xFF09, 0xFFC1, 0x01, 1, outputIds: [0xB3, 0xB5]), "RIDLAG mouse", "ridlag.status")
            && Named(Shape(0x0BDA, 0xFFE0, 0xC1FF, 0x0100, outputIds: [0xB3, 0xB5]), "Motospeed mouse", "motospeed.status")
            && Way(Shape(0x362D, 0xD107, 0x008C, 0x01, outputIds: [0xB3]), "jingzao.status").Request.SequenceEqual(new byte[] { 0xB3, 0x06 }));
        check("Keychron's platform: the Motospeed X6 on its cable (random levels), another Realtek or 362D product and another collection are not asked",
            Untaken(Shape(0x0BDA, 0xFFF1, 0xFFC1, 0x01, outputIds: [0xB3])) && Untaken(Shape(0x0BDA, 0x4762, 0xFFC1, 0x01, outputIds: [0xB3]))
            && Untaken(Shape(0x362D, 0xD108, 0x008C, 0x01, outputIds: [0xB3])) && Untaken(Shape(0x362D, 0xD107, 0xFFC1, 0x01, outputIds: [0xB3])));
    }

    private static void Darmoshark(Action<string, bool> check)
    {
        var receiver = Shape(0x248A, 0xFF30, 0x008C, 1, 1, input: 33, output: 33, feature: 1001, outputIds: [0xB2], featureIds: [0x51, 0x52, 0x53]);
        check("Darmoshark: the FF30 receiver and the M3 and N3 cables on page 0x8C (either byte order) take the identity read",
            Named(receiver, "Darmoshark", "darmoshark.identity") && Named(Shape(0x248A, 0xFF12, 0x8C00, 0x0100, 2, feature: 1001, featureIds: [0x51]), "Darmoshark", "darmoshark.identity")
            && Named(Shape(0x248A, 0xFF10, 0x008C, 1, feature: 1001, featureIds: [0x51]), "Darmoshark", "darmoshark.identity"));
        check("Darmoshark: Inphic's and AJAZZ's 248A products, another FF30 collection and an unknown 248A product are not asked",
            Untaken(Shape(0x248A, 0x5B2F, 0xFF00, 1, feature: 33)) && Untaken(Shape(0x248A, 0x5C2E, 0x0001, 0, 2, 33, 33))
            && Untaken(Shape(0x248A, 0xFF30, 0x0001, 2)) && Untaken(Shape(0x248A, 0xFF31, 0x008C, 1, feature: 1001, featureIds: [0x51])));
        var identity = Way(receiver, "darmoshark.identity");
        check("Darmoshark: 51 06 in 21 bytes, after the bond read 51 03, read back in 21 bytes 120 ms later",
            identity.Request.SequenceEqual(Pad(21, 0x51, 0x06)) && identity.Before.Count == 1 && identity.Before[0].SequenceEqual(Pad(21, 0x51, 0x03))
            && identity is { GapMs: 120, FeatureLength: 21 });
        var cableIdentity = Way(Shape(0x248A, 0xFF12, 0x008C, 1, 2, feature: 1001, featureIds: [0x51]), "darmoshark.identity");
        check("Darmoshark: through the receiver, the bond answer still in the buffer with link 0 is the mouse asleep, with link 1 no answer yet; on the cable it is never a reading",
            identity.Parse(Pad(21, Hex("51 03 01 8A 24 10 FF 00"))) == new BatteryRead(null, Asleep: true)
            && identity.Parse(Pad(21, Hex("51 03 01 8A 24 10 FF 01"))) is null
            && identity.Parse(Pad(21, Hex("51 06 01 00 8A 24 10 FF 03 01 09 09 00 00"))) == new BatteryRead(9)
            && cableIdentity.Parse(Pad(21, Hex("51 03 01 8A 24 10 FF 00"))) is null
            && cableIdentity.Parse(Pad(21, Hex("51 06 01 00 8A 24 12 FF 09 02 00 4B 01"))) == new BatteryRead(75, true));
        check("Darmoshark: quiet-battery's N3 answer 51 06 01 00 8A 24 10 FF 03 01 09 09 00 00 reads 9 %; the bond answer, the request and a foreign id are none",
            DarmosharkMice.Identity(Pad(21, Hex("51 06 01 00 8A 24 10 FF 03 01 09 09 00 00"))) == new BatteryRead(9)
            && DarmosharkMice.Identity(Pad(21, Hex("51 06 01 00 8A 24 12 FF 09 02 00 4B 01"))) == new BatteryRead(75, true)
            && DarmosharkMice.Identity(Pad(21, Hex("51 03 01 8A 24 10 FF 01"))) is null && DarmosharkMice.Identity(DarmosharkMice.IdentityRequest()) is null
            && DarmosharkMice.Identity(Pad(21, Hex("51 06 01 00 8B 24 10 FF 03 01 09 09 00 00"))) is null
            && DarmosharkMice.Identity(Pad(21, Hex("51 06 01 00 8A 24 10 FF 03 01 09 65 00 00"))) is null);
        var m3s = Shape(0x1915, 0x0723, 0xFF0A, 1, input: 65, output: 65, outputIds: [0x00]);
        check("Darmoshark: the M3S Pro's receiver and cable on 0xFF0A / 1 take the power query; Ninjutso's Nordic receiver is not asked",
            Named(m3s, "Darmoshark", "darmoshark.power") && Named(Shape(0x1915, 0x073C, 0x0AFF, 0x0100, input: 65, output: 65, outputIds: [0x00]), "Darmoshark", "darmoshark.power")
            && Untaken(Shape(0x1915, 0xAE1C, 0xFFA0, 1, feature: 32)) && Untaken(Shape(0x1915, 0x0724, 0xFF0A, 1, input: 65, output: 65, outputIds: [0x00])));
        var cable = DarmosharkMice.PowerRequest(false);
        var wireless = DarmosharkMice.PowerRequest(true);
        check("Darmoshark: the power query 01 00 81 01 with checksum 30 on the cable, 41 .. DE through the receiver, the 64 bytes summing to A1",
            cable.Length == 65 && cable[0] == 0 && cable.Skip(1).Take(4).SequenceEqual(new byte[] { 0x01, 0x00, 0x81, 0x01 }) && cable[64] == 30
            && wireless[1] == 0x41 && wireless[64] == 0xDE && (byte)wireless.Skip(1).Sum(b => b) == 0xA1 && Way(m3s, "darmoshark.power") is { StripZero: true }
            && Way(m3s, "darmoshark.power").Request.SequenceEqual(wireless));
        var dms = Pad(64, 0x41, 0x00, 0x81, 0x01, 0x00, 0x02, 0x55, 0x01);
        var v2 = Pad(64, 0x41, 0x00, 0x81, 0x01, 0x00, 0x08, 0x15, 0x19, 0x00, 0x00, 0x00, 0x50);
        check("Darmoshark: the dms answer reads 6 (85 %), the dms_v2 answer 11 (80 %); the request read back, another command and 0 % are none",
            DarmosharkMice.Power(dms) == new BatteryRead(85) && DarmosharkMice.Power(v2) == new BatteryRead(80)
            && DarmosharkMice.Power(wireless[1..]) is null && DarmosharkMice.Power(Pad(64, 0x41, 0x00, 0x81, 0x02, 0x00, 0x02, 0x55)) is null
            && DarmosharkMice.Power(Pad(64, 0x41, 0x00, 0x81, 0x01, 0x00, 0x02, 0x00)) is null);
    }

    private static void Eweadn(Action<string, bool> check)
    {
        var hub = Shape(0x089D, 0x062F, 0xFF01, 0x10, input: 33, output: 33, outputIds: [0x00]);
        var aula = Shape(0x089D, 0x062F, 0xFF02, 0x00, input: 33, output: 33, outputIds: [0x00]);
        check("EWEADN: the 089D:062F receiver takes HaloBattery's info read on 0xFF01 / 0x10 and MouseDock's identify read on 0xFF02 / 0 (33 / 33), as does the 088D:062E cable",
            Named(hub, "EWEADN mouse", "eweadn.info") && Named(aula, "EWEADN mouse", "eweadn.identify")
            && Named(Shape(0x088D, 0x062E, 0xFF01, 0x10, input: 33, output: 33, outputIds: [0x00]), "EWEADN mouse", "eweadn.info"));
        check("EWEADN: another collection of the receiver (65-byte 0xFF02, the mouse page) and another product are not asked",
            Untaken(Shape(0x089D, 0x062F, 0xFF02, 0x00, input: 65, output: 65, outputIds: [0x00])) && Untaken(Shape(0x089D, 0x062F, 0x0001, 0x02))
            && Untaken(Shape(0x089D, 0x0630, 0xFF01, 0x10, input: 33, output: 33, outputIds: [0x00])));
        check("EWEADN: 00 30 then zeros (checksum 0) and 00 30 00 01, 33 bytes each, the answer read without its report id",
            EweadnMice.InfoRequest().SequenceEqual(Pad(33, 0x00, 0x30)) && EweadnMice.IdentifyRequest().SequenceEqual(Pad(33, 0x00, 0x30, 0x00, 0x01))
            && Way(hub, "eweadn.info") is { StripZero: true } && Way(aula, "eweadn.identify") is { StripZero: true });
        check("EWEADN: the S9 (M156, 70 %) and the SC580 (M137, 82 %, charging) read; 30 02 01 and online 0 are asleep; the request read back and a broken id are none",
            EweadnMice.Info(Pad(32, Hex("30 00 01 0B 4D 31 35 36 01 22 95 AA 00 46 01"))) == new BatteryRead(70)
            && EweadnMice.Info(Pad(32, Hex("30 00 01 0B 4D 31 33 37 01 22 95 AA 01 52 01"))) == new BatteryRead(82, true)
            && EweadnMice.Info(Pad(32, 0x30, 0x02, 0x01)) == new BatteryRead(null, Asleep: true) && EweadnMice.Info(Pad(32, 0x30, 0x02)) == new BatteryRead(null, Asleep: true)
            && EweadnMice.Info(Pad(32, Hex("30 00 01 0B 4D 31 33 37 01 22 95 AA 00 52 00"))) == new BatteryRead(null, Asleep: true)
            && EweadnMice.Info(EweadnMice.IdentifyRequest()[1..]) is null && EweadnMice.Info(EweadnMice.InfoRequest()[1..]) is null
            && EweadnMice.Info(Pad(32, Hex("30 00 01 0B 00 00 00 00 01 22 95 AA 00 46 01"))) is null
            && EweadnMice.Info(Pad(32, Hex("30 00 01 0B 4D 31 35 36 01 22 95 AA 00 65 01"))) is null);
        var cable = Way(Shape(0x088D, 0x062E, 0xFF01, 0x10, input: 33, output: 33, outputIds: [0x00]), "eweadn.info");
        check("EWEADN: on its cable the mouse reads charging whatever its flag; asleep stays asleep; through the receiver the flag stands",
            cable.Parse(Pad(32, Hex("30 00 01 0B 4D 31 35 36 01 22 95 AA 00 46 01"))) == new BatteryRead(70, true)
            && cable.Parse(Pad(32, 0x30, 0x02)) == new BatteryRead(null, Asleep: true)
            && Way(hub, "eweadn.info").Parse(Pad(32, Hex("30 00 01 0B 4D 31 35 36 01 22 95 AA 00 46 01"))) == new BatteryRead(70));
        var beiyin = Shape(0x372E, 0x1026, 0xFF00, 0x01, input: 0, output: 0, feature: 64, featureIds: [0x03]);
        check("EWEADN: the beiyin S9 receiver's feature collection takes the basics read; its 0xFF06 collection is not asked",
            Named(beiyin, "EWEADN mouse", "eweadn.beiyin") && Untaken(Shape(0x372E, 0x1026, 0xFF06, 0x01, input: 31, output: 63, outputIds: [0x09])));
        var request = EweadnMice.BeiyinRequest();
        check("EWEADN: 03 22 50 00 02 4F 81 padded to 64 bytes (0x22 the sum of the bytes after it), read back 20 ms later",
            request.Length == 64 && request.Take(7).SequenceEqual(new byte[] { 0x03, 0x22, 0x50, 0x00, 0x02, 0x4F, 0x81 }) && Zeros(request, 7)
            && Way(beiyin, "eweadn.beiyin") is { GapMs: 20, FeatureLength: 64 });
        check("EWEADN: vigil-watt's answer 03 50 00 0B 02 01 49 .. reads 73 % (C9: charging), a sleeping 0 is asleep; the request, the residue 03 00 02 and 80 (0 % charging) are none",
            EweadnMice.Beiyin(Pad(64, Hex("03 50 00 0B 02 01 49 21 07 01 90 65 04 A5 5A 00"))) == new BatteryRead(73)
            && EweadnMice.Beiyin(Pad(64, Hex("03 50 00 0B 02 01 C9 21 07 01 90 65 04 A5 5A 00"))) == new BatteryRead(73, true)
            && EweadnMice.Beiyin(Pad(64, Hex("03 50 00 0B 02 01 00 21 07 01 90 65 04 A5 5A 00"))) == new BatteryRead(null, Asleep: true)
            && EweadnMice.Beiyin(Pad(64, Hex("03 50 00 0B 02 01 80"))) is null && EweadnMice.Beiyin(Pad(64, Hex("03 50 01 0B 02 01 49"))) is null
            && EweadnMice.Beiyin(request) is null && EweadnMice.Beiyin(Hex("03 00 02 00 00")) is null
            && EweadnMice.Beiyin(Pad(64, Hex("03 50 00 0B 02 01 7F"))) is null);
    }

    private static void Aula(Action<string, bool> check)
    {
        var sc380 = Shape(0x258A, 0x0138, 0xFF02, 0x02, input: 0, output: 0, feature: 520, featureIds: [0x09]);
        check("AULA: the SC380 Pro receiver's 0xFF02 / 2 collection takes the battery read; its untested cable is not asked",
            Named(sc380, "AULA mouse", "aula.sc380") && Untaken(Shape(0x258A, 0x0136, 0xFF02, 0x02, feature: 520, featureIds: [0x09]))
            && Untaken(Shape(0x258A, 0x0138, 0xFF01, 0x01, feature: 520, featureIds: [0x09])));
        check("AULA: the SC380 Pro request is 09 0C 09 01 01 00 01 00 (the checksum the sum of the bytes after it), read back at its full length",
            AulaMice.Sc380Request().SequenceEqual(Hex("09 0C 09 01 01 00 01 00")) && Way(sc380, "aula.sc380").FeatureLength == 0);
        check("AULA: the echoed header with 4E reads 78 %; the request read back (0 there), another command and 101 % are none",
            AulaMice.Sc380(Pad(520, Hex("09 0C 09 01 01 00 01 00 4E"))) == new BatteryRead(78) && AulaMice.Sc380(Pad(520, AulaMice.Sc380Request())) is null
            && AulaMice.Sc380(Pad(520, Hex("09 0B 07 00 01 00 01 00 01"))) is null && AulaMice.Sc380(Pad(520, Hex("09 0C 09 01 01 00 01 00 65"))) is null);
        var l9 = Shape(0x38A6, 0x28FD, 0xFFA8, 0x02, input: 64, output: 64, outputIds: [0x0B]);
        check("AULA: the L9 Ultra receiver's 0xFFA8 / 2 collection takes the status read; another usage, and AULA G9's 38A6:0030 (its listener's), are not asked",
            Named(l9, "AULA mouse", "aula.l9") && Named(Shape(0x38A6, 0x28FD, 0xA8FF, 0x0200, input: 64, output: 64, outputIds: [0x0B]), "AULA mouse", "aula.l9")
            && Untaken(Shape(0x38A6, 0x28FD, 0xFFA8, 0x01, input: 64, output: 64, outputIds: [0x0B]))
            && Untaken(Shape(0x38A6, 0x0030, 0xFFAB, 0x01)) && Untaken(Shape(0x38A6, 0x28FD, 0xFF02, 0x02, input: 64, output: 64, outputIds: [0x0B])));
        var charging = Pad(32, 0x0A, 0x81); charging[17] = 0x01; charging[18] = 0x40;
        var empty = Pad(32, 0x0A, 0x81); empty[17] = 0x01;
        check("AULA: 0B 81; 0A 81 with 1 at 17 and 40 at 18 reads 64 % charging; 0 %, the request read back and another report are none",
            AulaMice.L9Request().SequenceEqual(new byte[] { 0x0B, 0x81 }) && AulaMice.L9(charging) == new BatteryRead(64, true)
            && AulaMice.L9(empty) is null && AulaMice.L9(Pad(32, 0x0B, 0x81)) is null && AulaMice.L9(Pad(32, 0x0A, 0x85)) is null);
    }

    private static void Thunderobot(Action<string, bool> check)
    {
        check("Thunderobot: the ML903 receiver's 0xFFA0 / 2 collection takes the device-info read; another collection or product is not asked",
            Named(Shape(0x369B, 0x0234, 0xFFA0, 0x02, input: 64, output: 64, outputIds: [0xA0]), "Thunderobot", "thunderobot.info")
            && Untaken(Shape(0x369B, 0x0234, 0xFFA0, 0x01, input: 64, output: 64, outputIds: [0xA0])) && Untaken(Shape(0x369B, 0x0235, 0xFFA0, 0x02, input: 64, output: 64)));
        var measured = Pad(64, Hex("A1 02 04 01 11 02 11 E3 01 9B 36 02 13 31 32 33 34 35 36 02 25 23 02 26 01"));
        var tail = (byte[])measured.Clone(); tail[40] = 0x01;
        check("Thunderobot: A0 02; vigil-watt's measured answer reads 35 %; an unknown tag, a byte after the fields, a second level and the request are none",
            ThunderobotMice.Request().SequenceEqual(new byte[] { 0xA0, 0x02 }) && ThunderobotMice.Battery(measured) == new BatteryRead(35)
            && ThunderobotMice.Battery(Pad(64, Hex("A1 02 99 01 02 25 23"))) is null && ThunderobotMice.Battery(tail) is null
            && ThunderobotMice.Battery(Pad(64, Hex("A1 02 25 23 02 25 24"))) is null && ThunderobotMice.Battery(Pad(64, 0xA0, 0x02)) is null
            && ThunderobotMice.Battery(Pad(64, Hex("A1 02 26 01"))) is null);
    }

    private static void HyperX(Action<string, bool> check)
    {
        var haste = Shape(0x03F0, 0x028E, 0xFF00, 0x01, input: 65, output: 65, outputIds: [0x00]);
        var haste2 = Shape(0x03F0, 0x0D97, 0xFF00, 0x01, 2, input: 64, output: 64, outputIds: [0x50]);
        check("HyperX: the Haste and Dart take the heartbeat, the Haste 2 Wireless the battery query, each on 0xFF00 / 1 (either byte order)",
            Named(haste, "HyperX mouse", "hyperx.heartbeat") && Named(Shape(0x0951, 0x16E1, 0x00FF, 0x0100, input: 65, output: 65, outputIds: [0x00]), "HyperX mouse", "hyperx.heartbeat")
            && Named(haste2, "HyperX mouse", "hyperx.battery") && BrandFamilies.Find(Shape(0x03F0, 0x0F98, 0xFF00, 0x01, input: 64, output: 64, outputIds: [0x00])) is { Ways.Count: 0 });
        check("HyperX: the headsets on the same vendor ids (Cloud Flight, Cloud Alpha, Cloud Flight 2) and another collection are not asked",
            Untaken(Shape(0x0951, 0x16C4, 0xFF00, 0x01, input: 65, output: 65, outputIds: [0x00])) && Untaken(Shape(0x03F0, 0x098D, 0xFF00, 0x01, input: 65, output: 65, outputIds: [0x00]))
            && Untaken(Shape(0x03F0, 0x0AC1, 0xFF90, 0x01)) && Untaken(Shape(0x03F0, 0x028E, 0xFF01, 0x01, input: 65, output: 65, outputIds: [0x00])));
        var heartbeat = Way(haste, "hyperx.heartbeat");
        check("HyperX: the heartbeat is 51 after report id 0 (its answer read without it), asked after the connection 46; the battery query 50 02 after the link 52 01 where 0x52 is declared",
            HyperXMice.HeartbeatRequest().SequenceEqual(Pad(65, 0x00, 0x51)) && heartbeat is { StripZero: true } && heartbeat.Gate!.SequenceEqual(Pad(65, 0x00, 0x46))
            && HyperXMice.Battery2Request().SequenceEqual(new byte[] { 0x50, 0x02 }) && Way(haste2, "hyperx.battery").Gate is null
            && Way(Shape(0x03F0, 0x0D97, 0xFF00, 0x01, 2, input: 64, output: 64, outputIds: [0x50, 0x52]), "hyperx.battery").Gate!.SequenceEqual(new byte[] { 0x52, 0x01 }));
        check("HyperX: NGenuity's connection answers: 46 00 00 01 01 linked, 46 00 00 01 00 not (asleep), 46 00 00 02 the cable; Haste 2's 53 01 01 linked, 53 01 00 not; other frames none",
            heartbeat.Gated!(Pad(64, Hex("46 00 00 01 01"))) == true && heartbeat.Gated(Pad(64, Hex("46 00 00 01 00"))) == false
            && heartbeat.Gated(Pad(64, Hex("46 00 00 02 EC 00"))) == true && heartbeat.Gated(Pad(64, Hex("51 00 00 03 62 01"))) is null
            && heartbeat.Gated(Pad(64, Hex("46 00 00 05 01"))) is null
            && HyperXMice.Linked(Pad(64, 0x53, 0x01, 0x01)) == true && HyperXMice.Linked(Pad(64, 0x53, 0x01, 0x00)) == false && HyperXMice.Linked(Pad(64, 0x51, 0x02, 0x50)) is null);
        check("HyperX: santeri3700's capture 51 00 00 03 62 01 0F 66 10 00 40 reads 98 % charging; 51 00 00 03 <50> 01 0F 8B 10 80 %; a wired 0 or FF and the request read back are none",
            HyperXMice.HeartbeatBattery(Pad(64, Hex("51 00 00 03 62 01 0F 66 10 00 40"))) == new BatteryRead(98, true)
            && HyperXMice.HeartbeatBattery(Pad(64, Hex("51 00 00 03 50 01 0F 8B 10 02 40"))) == new BatteryRead(80, true)
            && HyperXMice.HeartbeatBattery(Pad(64, Hex("51 00 00 03 3C 00 0F 8B 10"))) == new BatteryRead(60)
            && HyperXMice.HeartbeatBattery(Pad(64, Hex("51 00 00 03 00 01"))) is null && HyperXMice.HeartbeatBattery(Pad(64, Hex("51 00 00 03 FF 02"))) is null
            && HyperXMice.HeartbeatBattery(Pad(64, 0x51)) is null);
        check("HyperX: 51 02 <percent> <status> reads (status 1 charging, FF unknown); the query read back, 0 % and status 4 are none",
            HyperXMice.Battery2Battery(Pad(64, 0x51, 0x02, 0x50, 0x01)) == new BatteryRead(80, true) && HyperXMice.Battery2Battery(Pad(64, 0x51, 0x02, 0x50, 0xFF)) == new BatteryRead(80)
            && HyperXMice.Battery2Battery(Pad(64, 0x50, 0x02)) is null && HyperXMice.Battery2Battery(Pad(64, 0x51, 0x02, 0x00, 0x00)) is null
            && HyperXMice.Battery2Battery(Pad(64, 0x51, 0x02, 0x50, 0x04)) is null && HyperXMice.Battery2Battery(Pad(64, 0x51, 0x01, 0x50, 0x00)) is null);
    }

    private static void Finalmouse(Action<string, bool> check)
    {
        var dongle = Shape(0x361D, 0x0100, 0xFF00, 0x01, input: 64, output: 64, outputIds: [0x04]);
        check("Finalmouse: any 361D dongle's 0xFF00 / 1 collection that declares output report 4 takes the status, then the voltage read",
            Named(dongle, "Finalmouse", "finalmouse.status", "finalmouse.voltage")
            && BrandFamilies.Find(Shape(0x361D, 0x0300, 0xFF00, 0x01, input: 64, output: 64, outputIds: [0x01])) is { Ways.Count: 0 });
        check("Finalmouse: the UltralightX on its cable (firmware updates only) and another page are not asked",
            Untaken(Shape(0x361D, 0x0102, 0xFF00, 0x01, input: 64, output: 64, outputIds: [0x04])) && Untaken(Shape(0x361D, 0x0100, 0xFF01, 0x01, input: 64, output: 64, outputIds: [0x04])));
        check("Finalmouse: the requests are 04 02 A6 00 (command 38) and 04 02 85 00 (command 5)",
            FinalmouseMice.Request(FinalmouseMice.BatteryStatus).SequenceEqual(new byte[] { 0x04, 0x02, 0xA6, 0x00 })
            && FinalmouseMice.Request(FinalmouseMice.BatteryVoltage).SequenceEqual(new byte[] { 0x04, 0x02, 0x85, 0x00 }));
        check("Finalmouse: XPanel's curve (4380 mV 100 %, 3880 50 %, 3810 38 %, 2900 0 %; 0 and FFFF no reading)",
            FinalmouseMice.Percent(4400) == 100 && FinalmouseMice.Percent(4380) == 100 && FinalmouseMice.Percent(3880) == 50 && FinalmouseMice.Percent(3810) == 38
            && FinalmouseMice.Percent(2900) == 0 && FinalmouseMice.Percent(0) is null && FinalmouseMice.Percent(0xFFFF) is null);
        check("Finalmouse: HaloBattery's measured answers 0x1064 (78 %) and 0x10B4 (88 %) read coarse; status 80 % reads; FF, 0 V, another command and report 4 are none",
            FinalmouseMice.Voltage(Pad(64, 0x05, 0x04, 0x05, 0x02, 0x64, 0x10)) == new BatteryRead(78, Coarse: true)
            && FinalmouseMice.Voltage(Pad(64, 0x05, 0x04, 0x85, 0x02, 0xB4, 0x10)) == new BatteryRead(88, Coarse: true)
            && FinalmouseMice.Status(Pad(64, 0x05, 0x05, 0x26, 0x03, 0x50, 0x2C, 0x10)) == new BatteryRead(80)
            && FinalmouseMice.Status(Pad(64, 0x05, 0x05, 0x26, 0x03, 0xFF, 0x00, 0x00)) is null && FinalmouseMice.Voltage(Pad(64, 0x05, 0x04, 0x05, 0x02, 0x00, 0x00)) is null
            && FinalmouseMice.Voltage(Pad(64, 0x05, 0x03, 0x12, 0x01, 0x01)) is null && FinalmouseMice.Voltage(Pad(64, 0x04, 0x02, 0x85, 0x00)) is null
            && FinalmouseMice.Voltage(Pad(64, 0x05, 0x04, 0x05, 0x02, 0xB8, 0x0B)) is null);
        check("Finalmouse: a frame whose length does not match its payload, and millivolts outside 2500 to 4600 (4700, 2400), are none",
            FinalmouseMice.Voltage(Pad(64, 0x05, 0x05, 0x05, 0x02, 0x64, 0x10)) is null && FinalmouseMice.Status(Pad(64, 0x05, 0x04, 0x26, 0x03, 0x50, 0x2C, 0x10)) is null
            && FinalmouseMice.Voltage(Pad(64, 0x05, 0x04, 0x05, 0x02, 0x5C, 0x12)) is null && FinalmouseMice.Voltage(Pad(64, 0x05, 0x04, 0x05, 0x02, 0x60, 0x09)) is null
            && FinalmouseMice.Voltage(Pad(64, 0x05, 0x04, 0x05, 0x02, 0xF8, 0x11)) == new BatteryRead(100, Coarse: true));
    }

    private static void Incott(Action<string, bool> check)
    {
        var receiver = Shape(0x093A, 0x522C, 0xFF05, 0x01, 3, input: 9);
        check("Incott: the 8K's receiver and cable on 0xFF05 / 1 (either byte order) are listened to, nothing sent, named by their product string; other PixArt products and usages are not",
            Named(receiver, "Incott", "incott.listen") && Named(Shape(0x093A, 0x622C, 0x05FF, 0x0100, 2, input: 9), "Incott", "incott.listen")
            && BrandFamilies.Find(receiver)!.Value.Family.DisplayName(receiver) is null
            && Untaken(Shape(0x093A, 0x2510, 0xFF05, 0x01, input: 9)) && Untaken(Shape(0x093A, 0x522C, 0x0001, 0x02)) && Untaken(Shape(0x093A, 0x522C, 0xFF05, 0x02, 3, input: 9)));
        check("Incott: OpenMouse's captures 09 5F 10 04 .. (95 %) and 09 E1 10 04 .. (97 % charging) read, also zero-padded to 33; 0, 101 to 128, bytes after the eighth and another report are none",
            IncottMice.Battery(Hex("09 5F 10 04 00 0F 0F 10")) == new BatteryRead(95) && IncottMice.Battery(Hex("09 E1 10 04 00 0F 0F 10")) == new BatteryRead(97, true)
            && IncottMice.Battery(Pad(33, Hex("09 5F 10 04 00 0F 0F 10"))) == new BatteryRead(95)
            && IncottMice.Battery(Pad(33, Hex("09 5F 10 04 00 0F 0F 10 01"))) is null
            && IncottMice.Battery(Hex("09 00 10 04")) is null && IncottMice.Battery(Hex("09 70 10 04")) is null && IncottMice.Battery(Hex("09 80 10 04")) is null
            && IncottMice.Battery(Hex("01 5F 10 04")) is null);
    }

    private static void Ryunix(Action<string, bool> check)
    {
        check("Ryunix: the MX1 (usage 0xC7) and the Amkette XS (usage 0) on page 0x0A are listened to; other Elan products and pages are not",
            Named(Shape(0x04F3, 0x026F, 0x000A, 0xC7, input: 10), "Ryunix", "ryunix.listen") && Named(Shape(0x04F3, 0x026E, 0x0A00, 0x00, input: 10), "Ryunix", "ryunix.listen")
            && Untaken(Shape(0x04F3, 0x0001, 0x000A, 0xC7, input: 10)) && Untaken(Shape(0x04F3, 0x026F, 0x0001, 0x02, input: 10)));
        check("Ryunix: the wired XS capture 04 01 02 01 64 02 .. reads 100 % (full, not charging), 04 01 02 04 54 01 84 % charging; a bad polling code, status 3 or another report are none",
            RyunixMice.Battery(Hex("04 01 02 01 64 02 00 00 01 20")) == new BatteryRead(100) && RyunixMice.Battery(Hex("04 01 02 04 54 01 02")) == new BatteryRead(84, true)
            && RyunixMice.Battery(Hex("04 01 02 03 32 00 01")) is null && RyunixMice.Battery(Hex("04 01 02 04 32 03 01")) is null
            && RyunixMice.Battery(Hex("01 01 02 04 32 00 01")) is null && RyunixMice.Battery(Hex("04 01 02 04 00 00 01")) is null
            && RyunixMice.Battery(Hex("04 02 02 04 32 00 01")) is null && RyunixMice.Battery(Hex("04 01 02 04 65 00 01")) is null);
        check("Ryunix: active 0 (the receiver's mouse not connected) is asleep whatever level it carries; a malformed one is still none",
            RyunixMice.Battery(Hex("04 00 02 01 37 00 00 00 01 20")) == new BatteryRead(null, Asleep: true)
            && RyunixMice.Battery(Hex("04 00 02 01 00 00 00")) == new BatteryRead(null, Asleep: true) && RyunixMice.Battery(Hex("04 00 02 03 37 00 00")) is null);
    }

    private static void Redragon(Action<string, bool> check)
    {
        var receiver = Shape(0x258A, 0x002F, 0xFF00, 0x01, 1, input: 8, output: 0, feature: 520, featureIds: [0x05, 0x08]);
        check("Redragon: the M690 PRO's receiver and cable on 0xFF00 / 1 take the status read; the M719 PRO and another 258A product are not asked",
            Named(receiver, "Redragon mouse", "redragon.m690") && Named(Shape(0x258A, 0x002E, 0x00FF, 0x0100, 1, feature: 520, featureIds: [0x05, 0x08]), "Redragon mouse", "redragon.m690")
            && Untaken(Shape(0x258A, 0x0049, 0xFF00, 0x01, feature: 520, featureIds: [0x05])) && Untaken(Shape(0x258A, 0x002F, 0xFF01, 0x01, feature: 520, featureIds: [0x05])));
        var cable = Shape(0x258A, 0x002E, 0xFF00, 0x01, 1, feature: 520, featureIds: [0x05, 0x08]);
        check("Redragon: 05 90 padded to 8 bytes and read back in exactly 8, 60 ms later; the receiver is asked the link 05 80 first, the cable is not",
            RedragonMice.Request().SequenceEqual(Pad(8, 0x05, 0x90)) && Way(receiver, "redragon.m690") is { FeatureLength: 8, GapMs: 60 }
            && Way(receiver, "redragon.m690").Gate!.SequenceEqual(Pad(8, 0x05, 0x80)) && Way(cable, "redragon.m690").Gate is null);
        check("Redragon: the link answer 05 80 01 01 is linked, 05 80 00 01 and 05 80 00 02 the mouse off or asleep; another answer is none",
            RedragonMice.Linked(Hex("05 80 01 01 00 00 00 00")) == true && RedragonMice.Linked(Hex("05 80 00 01 00 00 00 00")) == false
            && RedragonMice.Linked(Hex("05 80 00 02 00 00 00 00")) == false && RedragonMice.Linked(Hex("05 90 11 64 00 00 00 00")) is null
            && RedragonMice.Linked(Hex("05 80 07 00 00 00 00 00")) is null);
        check("Redragon: 05 90 11 64 reads 100 %, 05 90 10 01 charging without a level, 05 90 10 02 full; the request read back and another command are none",
            RedragonMice.Battery(Hex("05 90 11 64 00 00 00 00")) == new BatteryRead(100) && RedragonMice.Battery(Hex("05 90 11 63 00 00 00 00")) == new BatteryRead(99)
            && RedragonMice.Battery(Hex("05 90 10 01 00 00 00 00")) == new BatteryRead(null, true) && RedragonMice.Battery(Hex("05 90 10 02 00 00 00 00")) == new BatteryRead(100)
            && RedragonMice.Battery(RedragonMice.Request()) is null && RedragonMice.Battery(Hex("05 80 01 01 00 00 00 00")) is null
            && RedragonMice.Battery(Hex("05 90 11 00 00 00 00 00")) is null);
    }

    private static void Scyrox(Action<string, bool> check)
    {
        var turbo = Shape(0x395A, 0x000E, 0xFF00, 0x02, 2, 64, 64, outputIds: [0x08]);
        check("Scyrox: the V6S / V8S ids take the status read, the V12M the profile read, on 0xFF00 / 2 with 64 / 64; other 395A products and lengths are not asked",
            Named(turbo, "Scyrox", "scyrox.status") && Named(Shape(0x395A, 0x000B, 0xFF00, 0x02, 2, 64, 64, outputIds: [0x08]), "Scyrox", "scyrox.status")
            && Named(Shape(0x395A, 0x0006, 0x00FF, 0x0200, 2, 64, 64, outputIds: [0x08]), "Scyrox", "scyrox.profile")
            && Untaken(Shape(0x395A, 0x0001, 0xFF00, 0x02, 2, 64, 64, outputIds: [0x08])) && Untaken(Shape(0x395A, 0x000E, 0xFF00, 0x02, 2, 17, 17, outputIds: [0x08])));
        var status = ScyroxMice.StatusRequest();
        check("Scyrox: 08 2E 00 03 .. 39 and 08 01 .. 09, 64 bytes",
            status.Length == 64 && status.Take(4).SequenceEqual(new byte[] { 0x08, 0x2E, 0x00, 0x03 }) && status[63] == 0x39 && Zeros(status[..63], 4)
            && ScyroxMice.ProfileRequest().Length == 64 && ScyroxMice.ProfileRequest()[1] == 0x01 && ScyroxMice.ProfileRequest()[63] == 0x09);
        var measured = Pad(64, Hex("08 2E 00 03 32 01 01 03 00 03 00 08 00 00 0B")); measured[63] = 0x86;
        var broken = (byte[])measured.Clone(); broken[63] = 0x87;
        var unlinked = Pad(64, Hex("08 2E 00 03 00 00 00 03 00 03 00 08 00 00 0B")); unlinked[63] = (byte)unlinked.Take(63).Sum(b => b);
        var linkTwo = Pad(64, Hex("08 2E 00 03 32 00 02")); linkTwo[63] = (byte)linkTwo.Take(63).Sum(b => b);
        check("Scyrox: ScyroxTray's measured answer (checksum 86) reads 50 % charging, an unlinked mouse (linked 0, 0 %) is asleep; a wrong checksum, the request sent back and linked 2 are none",
            ScyroxMice.Status(measured) == new BatteryRead(50, true) && ScyroxMice.Status(unlinked) == new BatteryRead(null, Asleep: true)
            && ScyroxMice.Status(broken) is null && ScyroxMice.Status(status) is null && ScyroxMice.Status(linkTwo) is null);
        var profile = Pad(64, 0x08, 0x01, 0x00, 0x01, 0x02, 0x02, 0x00, 0x03); profile[20] = 0x3C;
        var stale = (byte[])profile.Clone(); stale[62] = 0x01;
        check("Scyrox: a V12M profile answer reads 20 (60 %); the request read back and one flagged at 62 are none",
            ScyroxMice.Profile(profile) == new BatteryRead(60) && ScyroxMice.Profile(ScyroxMice.ProfileRequest()) is null && ScyroxMice.Profile(stale) is null);
    }

    private static void Pulsar(Action<string, bool> check)
    {
        var receiver = Shape(0x3710, 0x5403, 0xFFFF, 0x01, 3, input: 0, output: 0, feature: 65, featureIds: [0x00]);
        var cable = Shape(0x3710, 0x3402, 0xFFFF, 0x0100, 3, feature: 65, featureIds: [0x00]);
        check("Pulsar: the X2 V3 Mini's receiver and cable on interface 3, 0xFFFF / 1, take the Sonix read; the X2 CrazyLight Mini (Compx), other interfaces and collections are not asked",
            Named(receiver, "Pulsar", "pulsar.sonix") && Named(cable, "Pulsar", "pulsar.sonix")
            && Untaken(Shape(0x3710, 0x5406, 0xFF02, 0x02, 1, 17, 17)) && Untaken(Shape(0x3710, 0x5403, 0xFFFF, 0x01, 1, feature: 65, featureIds: [0x00]))
            && Untaken(Shape(0x3710, 0x5403, 0xFF00, 0x01, 3, feature: 65, featureIds: [0x00])) && Untaken(Shape(0x3710, 0x5403, 0xFFFF, 0x02, 3, feature: 65, featureIds: [0x00]))
            && Untaken(Shape(0x3710, 0x5403, 0x0001, 0x02, 3, feature: 65)));
        var request = PulsarMice.Request();
        check("Pulsar: 00 | 00 08 81 01, zeros, 8A 00 (the 16-bit sum) in the last two bytes, read back 50 ms later",
            request.Length == 65 && request.Take(5).SequenceEqual(new byte[] { 0x00, 0x00, 0x08, 0x81, 0x01 }) && request[63] == 0x8A && request[64] == 0x00
            && Zeros(request[..63], 5) && Way(receiver, "pulsar.sonix") is { GapMs: 50, FeatureLength: 65 });
        check("Pulsar: HaloBattery #213's reply 00 01 08 81 01 00 00 18 reads 24 % (charging on the cable); the request read back, one with 00 first, another command and 101 % are none",
            PulsarMice.Battery(Pad(65, Hex("00 01 08 81 01 00 00 18"))) == new BatteryRead(24)
            && Way(receiver, "pulsar.sonix").Parse(Pad(65, Hex("00 01 08 81 01 00 00 18"))) == new BatteryRead(24)
            && Way(cable, "pulsar.sonix").Parse(Pad(65, Hex("00 01 08 81 01 00 00 18"))) == new BatteryRead(24, true)
            && Way(cable, "pulsar.sonix").Parse(request) is null && PulsarMice.Battery(request) is null
            && PulsarMice.Battery(Pad(65, 0x00, 0x00, 0x08, 0x81, 0x01, 0x00, 0x00, 0x41)) is null
            && PulsarMice.Battery(Pad(65, 0x00, 0x01, 0x08, 0x85, 0x03, 0x00, 0x00, 0x41)) is null && PulsarMice.Battery(Pad(65, 0x00, 0x01, 0x08, 0x81, 0x01, 0x00, 0x00, 0x65)) is null);
    }

    // The pairs other readers have (Dongles.Find, Pushes.Find, the headsets) are taken by no mouse family.
    private static void Others(Action<string, bool> check)
    {
        HidShape[] others =
        [
            Shape(0x373E, 0x001C, 0xFFFF, 0, 2, feature: 65, featureIds: [0x00]), Shape(0x1915, 0xAE11, 0xFFA0, 1, feature: 32, featureIds: [0x05]),
            Shape(0xA8A5, 0x2255, 0xFF01, 0x10, input: 65, output: 65, outputIds: [0x00]), Shape(0x3151, 0x402D, 0xFFFF, 2, 2, feature: 65, featureIds: [0x00]),
            Shape(0x3710, 0x3414, 0xFF02, 2, 1, 17, 17, outputIds: [0x08]),
            Shape(0x3554, 0xF5F7, 0xFF02, 2, 1, 17, 17, outputIds: [0x08]), Shape(0x249A, 0x5C2F, 0x0001, 0, 2, 33, 33, outputIds: [0x00]),
            Shape(0x1D57, 0xFA60, 0x000A, 1, 2), Shape(0x38A6, 0x0030, 0xFFAB, 1), Shape(0x03F0, 0x05B7, 0xFF13, 1),
        ];
        check("Mouse families: no collection another reader has (LAMZU, Ninjutso, Gmouse, LINGBAO, Compx, AJAZZ, Beken, AULA G9, HyperX Cloud III)",
            others.All(shape => Untaken(shape)));
        // The Angry Miao Infinity 8K's receiver 3151:5007 is in MonsGeek's receiver list: the F7 status, read for its mouse.
        check("Mouse families: the AM Infinity 8K's receiver takes the Rongyuan status read, not a mouse family's",
            BrandFamilies.Find(Shape(0x3151, 0x5007, 0xFFFF, 2, 2, feature: 65, featureIds: [0x00, 0x05])) is { Family.Brand: "Rongyuan receiver" });
    }
}

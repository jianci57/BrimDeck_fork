using BrimDeck.Core;

// The headset brand families: what each takes (and what it leaves to the readers that already have it), its requests, the
// answers its sources quote, and the request read back or an acknowledgement read as no reading.
static class BrandsHeadsetTests
{
    private static HidShape Shape(int vendor, int product, int page, int usage = 1, int iface = -1, int input = 64, int output = 64, int feature = 0,
        byte[]? outputIds = null, byte[]? featureIds = null) => BrandFamilyTests.Shape(vendor, product, page, usage, iface, input, output, feature, outputIds, featureIds);

    // Hex bytes, padded with zeros to length.
    private static byte[] Hex(string hex, int length = 0)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        return bytes.Length >= length ? bytes : BrandFamilies.Frame(length, bytes);
    }

    private static string[] Names(HidShape shape, string brand)
        => BrandFamilies.Find(shape) is { } found && found.Family.Brand == brand ? [.. found.Ways.Select(way => way.Name)] : [];
    private static bool Untaken(HidShape shape, string brand) => BrandFamilies.Find(shape) is not { } found || found.Family.Brand != brand;
    private static BatteryRead? Parse(HidShape shape, byte[] reply) => BrandFamilies.Find(shape)!.Value.Ways[0].Parse(reply);

    public static void Run(Action<string, bool> check)
    {
        // The PA charging answer, laid out as blackshark.py's parse_reply reads it (constructed): command 2A at 12, ACK 01, length 1,
        // the flag. Barracuda Pro the same two bytes later.
        var paCharge = new byte[64]; paCharge[0] = 0x02; paCharge[2] = 0x50; paCharge[3] = 0x49; paCharge[12] = 0x2A; paCharge[13] = 0x01; paCharge[14] = 0x01; paCharge[15] = 0x01;
        var paLevelReply = (byte[])paCharge.Clone(); paLevelReply[12] = 0x21; paLevelReply[15] = 79;
        var bcCharge = new byte[64]; bcCharge[0] = 0x01; bcCharge[3] = 0x50; bcCharge[4] = 0x49; bcCharge[13] = 0x2A; bcCharge[14] = 0x01; bcCharge[15] = 0x01; bcCharge[16] = 0x00;
        check("Razer PA and Barracuda Pro: the charging question (2A) after the level, its answer, and the level answer is not a charging one",
            BrimDeck.Core.RazerHeadsets.PaQuery(0x2A)[10] == 0x2A && BrimDeck.Core.RazerHeadsets.PaCharging(paCharge) == true
            && BrimDeck.Core.RazerHeadsets.PaCharging(paLevelReply) is null && BrimDeck.Core.RazerHeadsets.BarracudaCharging(bcCharge) == false
            && BrandFamilies.Find(new HidShape(0x1532, 0x0555, 0xFF00, 1, -1, 64, 64, 0, new HashSet<byte> { 0x02 }, new HashSet<byte>(), "")) is { } pa
            && pa.Ways[0].Then is { } then && then[10] == 0x2A && pa.Ways[0].Charging is not null);

        Existing(check);
        Razer(check);
        Sony(check);
        Jabra(check);
        Actions(check);
        Xiberia(check);
        Asus(check);
        Astro(check);
        Lenovo(check);
        Darkrim(check);
        Edifier(check);
        SteelSeries(check);
    }

    // No product here is one an existing reader already takes.
    private static void Existing(Action<string, bool> check)
    {
        int[] razer = [.. RazerHeadsets.Status2020, .. RazerHeadsets.Pa, RazerHeadsets.BarracudaPro, RazerHeadsets.KrakenV4];
        int[] steelseries = [0x12B3, 0x12B6, 0x12D7, 0x12D5, 0x220E, 0x2212, 0x2216, 0x2236, 0x2269, 0x226D, 0x2232, 0x2253, 0x230A, 0x2244, 0x2290];
        check("Headset brands: no product is one Razer, Headsets, SteelSeriesMice, Asus or Pushes reads already",
            razer.All(product => BrimDeck.Core.Razer.Find(product) is null)
            && steelseries.All(product => Headsets.Find(SteelSeriesHeadsets.Vendor, product) is null && SteelSeriesMice.Find(SteelSeriesHeadsets.Vendor, product) is null
                && SteelSeriesHeadsets.Find(product) is not null)
            && BrimDeck.Core.Asus.Find(AsusStrixGo.Vendor, AsusStrixGo.Dongle) is null && BrimDeck.Core.Asus.Find(AsusStrixGo.Vendor, AsusStrixGo.Cable) is null
            && new[] { SonyInzone.H5, SonyInzone.H9II }.All(product => Pushes.Find(SonyInzone.Vendor, product, 0xFF04, 1, -1, 64) is null));
        check("Headset brands: SteelSeries headsets the Headsets class reads (Nova 7) and its mice are not taken",
            Untaken(Shape(0x1038, 0x22A1, 0xFFC0, 1, 3, outputIds: [0]), "SteelSeries Headset")
            && Untaken(Shape(0x1038, 0x1838, 0xFFC0, 1, 3, outputIds: [0]), "SteelSeries Headset"));
    }

    private static void Razer(Action<string, bool> check)
    {
        const string brand = "Razer Headset";
        check("Razer headsets: the 2020 BlackShark (its page as measured on Windows) and the Nari (either byte order) take the feature read on 0xFF00 usage 1 with a 64-byte feature report; a mouse in Razer's catalog is not taken",
            Names(Shape(0x1532, 0x0528, 0xFF00, 1, feature: 64, featureIds: [0xFF]), brand).SequenceEqual(["razer-headset.status"])
            && Names(Shape(0x1532, 0x051C, 0x00FF, 1, feature: 64, featureIds: [0xFF]), brand).SequenceEqual(["razer-headset.status"])
            && Untaken(Shape(0x1532, 0x0528, 0x00FF, 1, feature: 64, featureIds: [0xFF]), brand)
            && Untaken(Shape(0x1532, 0x0528, 0xFF00, 1, feature: 32, featureIds: [0xFF]), brand)
            && Untaken(Shape(0x1532, 0x0528, 0x000C, 1, feature: 64, featureIds: [0xFF]), brand)
            && Untaken(Shape(0x1532, 0x052E, 0xFF00, 1, feature: 64, featureIds: [0xFF]), brand)
            && Untaken(Shape(0x1532, 0x00B7, 0xFF00, 1, feature: 91, featureIds: [0]), brand));
        check("Razer headsets: PA on 0xFF00 or 0xFF14 for the 2023 BlackShark, report 01 on usage 0 for the Barracuda Pro, the Kraken V4 on 0xFF14 (each page as Windows gave it to the sources); the Barracuda X and BlackShark V3 are not taken",
            Names(Shape(0x1532, 0x0555, 0xFF00, 1, 3, outputIds: [0x02]), brand).SequenceEqual(["razer-headset.pa"])
            && Names(Shape(0x1532, 0x0583, 0xFF14, 1, outputIds: [0x02]), brand).SequenceEqual(["razer-headset.pa"])
            && Untaken(Shape(0x1532, 0x0555, 0x00FF, 1, 3, outputIds: [0x02]), brand) && Untaken(Shape(0x1532, 0x0555, 0xFF00, 1, 3, input: 32, outputIds: [0x02]), brand)
            && Untaken(Shape(0x1532, 0x053A, 0x00FF, 0, outputIds: [0x01]), brand) && Untaken(Shape(0x1532, 0x056C, 0x14FF, 1, 5, outputIds: [0x02]), brand)
            && BrandFamilies.Find(Shape(0x1532, 0x0556, 0xFF00, 1, outputIds: [0x01])) is { Ways.Count: 0 }
            && Names(Shape(0x1532, 0x053A, 0xFF00, 0, outputIds: [0x01]), brand).SequenceEqual(["razer-headset.barracuda"])
            && Untaken(Shape(0x1532, 0x053A, 0x000C, 1, outputIds: [0x01]), brand)
            && Names(Shape(0x1532, 0x056C, 0xFF14, 1, 5, outputIds: [0x02]), brand).SequenceEqual(["razer-headset.kraken"])
            && Untaken(Shape(0x1532, 0x0552, 0xFF00, 1, outputIds: [0x01]), brand) && Untaken(Shape(0x1532, 0x057A, 0xFF00, 1, outputIds: [0x02]), brand));

        var status = Shape(0x1532, 0x0528, 0xFF00, 1, feature: 64, featureIds: [0xFF]);
        var statusWay = BrandFamilies.Find(status)!.Value.Ways[0];
        check("Razer headsets: the 2020 request, and HaloBattery's measured answers on battery (80), charged (100) and charging (no level), the Nari's capture (30, 3552 mV)",
            statusWay.Request.Length == 64 && statusWay.Request.AsSpan(0, 9).SequenceEqual(Hex("FF 0A 00 FD 04 12 F1 02 05")) && statusWay.GapMs >= 100
            && statusWay.Attempts == 2
            && RazerHeadsets.StatusBattery(Hex("FF 0F 05 FE 12 04 1F 08 05 03 05 01 0E C0 50", 64)) == new BatteryRead(80, Coarse: true)
            && RazerHeadsets.StatusBattery(Hex("FF 0F 05 FE 12 04 1F 08 05 06 05 06 10 88 64", 64)) == new BatteryRead(100, Coarse: true)
            && RazerHeadsets.StatusBattery(Hex("FF 0F 05 FE 12 04 1F 08 05 05 03 09 10 80 50", 64)) == new BatteryRead(null, Charging: true)
            && RazerHeadsets.StatusBattery(Hex("FF 0F 05 FE 12 04 1F 08 05 03 05 02 0D E0 1E 6C", 64)) == new BatteryRead(30, Coarse: true));
        check("Razer headsets: the receiver's stale copy (FF 01 00 ...) and the request read back are no reading; 0 mV is the headset off",
            RazerHeadsets.StatusBattery(Hex("FF 01 00 FE 12 04 1F 08 05 05 03 09 10 88 50", 64)) is null
            && RazerHeadsets.StatusBattery(RazerHeadsets.StatusRequest()) is null
            && RazerHeadsets.StatusBattery(Hex("FF 0F 05 FE 12 04 1F 08 05 03 05 01 00 00 00", 64)) == new BatteryRead(null, Asleep: true)
            && RazerHeadsets.StatusBattery(Hex("FF 0F 05 FE 12 04 1F 08 05 03 05 01 0E C0 FF", 64)) is null);
        // HaloBattery's measured "headset off" read: the stale header, still there when it gives up.
        var off = Hex("FF 01 00 FE 12 04 1F 08 05 05 03 09 10 88 50", 64);
        var reads = RazerHeadsets.StatusReads();
        var late = RazerHeadsets.StatusReads();
        check("Razer headsets: the stale copy at the first try is no reading (the answer may not be there yet), at the second the headset off; a fresh answer at the second try is the reading",
            reads(off) is null && reads(off) == new BatteryRead(null, Asleep: true)
            && late(off) is null && late(Hex("FF 0F 05 FE 12 04 1F 08 05 03 05 01 0E C0 50", 64)) == new BatteryRead(80, Coarse: true)
            && RazerHeadsets.StatusReads()(RazerHeadsets.StatusRequest()) is null);

        var pa = BrandFamilies.Find(Shape(0x1532, 0x0555, 0xFF00, 1, 3, outputIds: [0x02]))!.Value.Ways[0];
        check("Razer headsets: PA remote mode on twice before the query and off after, the frames as HaloBattery and vigil-watt give them",
            pa.Request.AsSpan(0, 11).SequenceEqual(Hex("02 80 08 00 00 50 41 08 00 03 21")) && pa.Request.Skip(11).All(b => b == 0)
            && pa.Before.Count == 2 && pa.Before.All(frame => frame.AsSpan(0, 12).SequenceEqual(Hex("02 80 07 00 00 50 41 0E 00 02 E1 01")))
            && pa.After.Count == 1 && pa.After[0].AsSpan(0, 12).SequenceEqual(Hex("02 80 07 00 00 50 41 0E 00 02 E1 00"))
            && pa.TimeoutMs <= 1000 && pa.Attempts <= 2);
        check("Razer headsets: vigil-watt's measured PA answer (79 %); the charging answer, the query and the remote frames read back are none",
            RazerHeadsets.PaBattery(Hex("02 0E 50 49 08 CE 92 DE 05 00 04 00 21 01 01 4F", 64)) == new BatteryRead(79)
            && RazerHeadsets.PaBattery(Hex("02 0E 50 49 08 CF B6 E0 05 00 04 00 2A 01 01 00", 64)) is null
            && RazerHeadsets.PaBattery(pa.Request) is null && RazerHeadsets.PaBattery(pa.Before[0]) is null
            && RazerHeadsets.PaBattery(Hex("02 0E 50 49 08 CE 92 DE 05 00 04 00 21 01 01 C8", 64)) is null);

        var barracuda = BrandFamilies.Find(Shape(0x1532, 0x053A, 0xFF00, 0, outputIds: [0x01]))!.Value.Ways[0];
        check("Razer headsets: the Barracuda Pro's query and remote frames, an answer and a notice as HaloBattery's tests build them, the query read back none",
            barracuda.Request.AsSpan(0, 9).SequenceEqual(Hex("01 80 08 50 41 08 08 03 21"))
            && barracuda.Before.Single().AsSpan(0, 10).SequenceEqual(Hex("01 80 07 50 41 0E 08 02 E1 01"))
            && barracuda.After.Single().AsSpan(0, 10).SequenceEqual(Hex("01 80 07 50 41 0E 08 02 E1 00"))
            && RazerHeadsets.BarracudaBattery(Hex("01 80 0A 50 49 08 00 00 00 00 00 00 00 21 01 01 37", 64)) == new BatteryRead(55)
            && RazerHeadsets.BarracudaBattery(Hex("01 80 0A 50 49 08 00 00 00 00 00 00 00 21 02 01 37", 64)) == new BatteryRead(55)
            && RazerHeadsets.BarracudaBattery(Hex("01 80 0A 50 49 08 00 00 00 00 00 00 00 2A 01 01 01", 64)) is null
            && RazerHeadsets.BarracudaBattery(barracuda.Request) is null);

        var kraken = RazerHeadsets.KrakenQuery(0x21);
        check("Razer headsets: the Kraken V4 query with its XOR (C7), vigil-watt's measured answers (63 %, 100 %), and the charging answer, a bad XOR and the query none",
            kraken.AsSpan(0, 13).SequenceEqual(Hex("02 00 60 00 00 00 04 00 00 80 21 00 00")) && kraken[62] == 0xC7 && RazerHeadsets.KrakenQuery(0x2A)[62] == 0xCC
            && RazerHeadsets.KrakenBattery(KrakenReply("02 02 60 00 00 00 05 00 80 80 21 01 01 3F", 0x79)) == new BatteryRead(63)
            && RazerHeadsets.KrakenBattery(KrakenReply("02 02 60 00 00 00 05 00 80 80 21 01 01 64", 0x22)) == new BatteryRead(100)
            && RazerHeadsets.KrakenBattery(KrakenReply("02 02 60 00 00 00 05 00 80 80 2A 01 01 00", 0x4D)) is null
            && RazerHeadsets.KrakenBattery(KrakenReply("02 02 60 00 00 00 05 00 80 80 21 01 01 3F", 0x78)) is null
            && RazerHeadsets.KrakenBattery(kraken) is null);
    }
    private static byte[] KrakenReply(string head, byte xor) { var frame = Hex(head, 64); frame[62] = xor; return frame; }

    private static void Sony(Action<string, bool> check)
    {
        const string brand = "Sony INZONE";
        check("Sony INZONE: the H5 on 0xFF04 usage 2, the H9 II on usage 1 interface 5, with 64-byte reports; a DualSense, the H9 (serial), the Buds (they only push), the PULSE Explore and the PULSE 3D are not taken",
            Names(Shape(0x054C, 0x0EBF, 0xFF04, 2, 0, outputIds: [0x02]), brand).SequenceEqual(["inzone.battery"])
            && Names(Shape(0x054C, 0x0FA8, 0x04FF, 1, 5, outputIds: [0x02]), brand).SequenceEqual(["inzone.battery"])
            && Untaken(Shape(0x054C, 0x0FA8, 0xFF04, 1, 4, outputIds: [0x02]), brand) && Untaken(Shape(0x054C, 0x0EBF, 0xFF04, 1, 0, outputIds: [0x02]), brand)
            && Untaken(Shape(0x054C, 0x0EBF, 0xFF04, 2, 0, input: 32, outputIds: [0x02]), brand)
            && new[] { 0x0CE6, 0x0E53, 0x0EC2, 0x0ECC, 0x0ED0, 0x0D5E }.All(product => Untaken(Shape(0x054C, product, 0xFF04, 1, outputIds: [0x02]), brand)));
        var request = SonyInzone.Request();
        // The answer laid out as HeadsetControl parses it (no answer is quoted there): length 14, the sum of 5..14 at 15.
        static byte[] Event(byte charger, byte percent, byte type = 0x10, byte transaction = 0x02)
        {
            var frame = Hex($"02 0E 04 FF 0A 00 96 C3 14 04 {type:X2} {transaction:X2} 00 {charger:X2} {percent:X2}", 64);
            int sum = 0;
            for (int i = 5; i <= 14; i++) sum += frame[i];
            frame[15] = (byte)sum;
            return frame;
        }
        var broken = Event(0, 0x50);
        broken[15]++;
        check("Sony INZONE: the GET (transaction 2, its sum A1); an answer, charging, and 0xFF the headset offline; a wrong sum, the dongle's own transaction 1 and the request read back are none",
            request.SequenceEqual(Hex("02 0C 01 00 FC 08 96 C3 41 04 01 02 00 A1"))
            && SonyInzone.Battery(Event(0, 0x50)) == new BatteryRead(80) && SonyInzone.Battery(Event(1, 0x32)) == new BatteryRead(50, true)
            && SonyInzone.Battery(Event(0, 0xFF)) == new BatteryRead(null, Asleep: true)
            && SonyInzone.Battery(broken) is null && SonyInzone.Battery(Event(0, 0x50, transaction: 0x01)) is null
            && SonyInzone.Battery(Hex("02 0C 01 00 FC 08 96 C3 41 04 01 02 00 A1", 64)) is null && SonyInzone.Battery(Event(0, 0x00)) is null
            && SonyInzone.Battery(Event(0, 0x65)) is null);
    }

    private static void Jabra(Action<string, bool> check)
    {
        const string brand = "Jabra";
        check("Jabra: any 0B0E vendor collection (usage 1 or 5) with report 05 is asked; the dongles at 0x04 first, the Evolve2 65 Flex at 0x08 first; other usages are not taken",
            Names(Shape(0x0B0E, 0x24C7, 0xFF00, 1, 3, outputIds: [0x05]), brand).SequenceEqual(["jabra.dongle", "jabra.usb"])
            && Names(Shape(0x0B0E, 0x2E57, 0x00FF, 5, 3, outputIds: [0x05]), brand).SequenceEqual(["jabra.dongle", "jabra.usb"])
            && Names(Shape(0x0B0E, 0x2519, 0xFF00, 1, 3, outputIds: [0x05]), brand).SequenceEqual(["jabra.usb", "jabra.dongle"])
            && BrandFamilies.Find(Shape(0x0B0E, 0x24C7, 0xFF00, 1, 3, outputIds: [0x04])) is { Ways.Count: 0 }
            && Untaken(Shape(0x0B0E, 0x24C7, 0xFF00, 2, 3, outputIds: [0x05]), brand) && Untaken(Shape(0x0B0F, 0x24C7, 0xFF00, 1, 3, outputIds: [0x05]), brand));
        // The refusals as HeadsetControl documents them (group FE, the reason in place of the command; constructed).
        check("Jabra: the GET 12/02 to the headset; HaloBattery's measured payload 04 15 (21 %); the dongle's \"headset offline\" refusal (F3) the headset asleep; \"nothing at that address\" (F5), F3 from the cable's address, the request read back and another address's answer are none",
            BrimDeck.Core.Jabra.Request(BrimDeck.Core.Jabra.Dongle).SequenceEqual(Hex("05 04 00 20 46 12 02"))
            && BrimDeck.Core.Jabra.Request(BrimDeck.Core.Jabra.Usb).SequenceEqual(Hex("05 08 00 20 46 12 02"))
            && BrimDeck.Core.Jabra.Battery(0x04)(Hex("05 00 04 20 C8 12 02 04 15", 64)) == new BatteryRead(21)
            && BrimDeck.Core.Jabra.Battery(0x04)(Hex("05 00 04 20 CA 12 02 01 3C 0F A0", 64)) == new BatteryRead(60, true)
            && BrimDeck.Core.Jabra.Battery(0x04)(Hex("05 00 04 20 C6 FE F3", 64)) == new BatteryRead(null, Asleep: true)
            && BrimDeck.Core.Jabra.Battery(0x04)(Hex("05 00 04 20 C6 FE F5", 64)) is null
            && BrimDeck.Core.Jabra.Battery(0x08)(Hex("05 00 08 20 C6 FE F3", 64)) is null
            && BrimDeck.Core.Jabra.Battery(0x04)(Hex("05 04 00 20 46 12 02", 64)) is null
            && BrimDeck.Core.Jabra.Battery(0x08)(Hex("05 00 04 20 C8 12 02 04 15", 64)) is null);
    }

    // FIFINE and ATK's N9 V2 Ultimate: the same 55 65 frame.
    private static void Actions(Action<string, bool> check)
    {
        check("FIFINE: any 3142 collection on 0xFF90 usage 1 with report 0x55; ATK's N9 V2 Ultimate on its 0xFF90 collection only, never its 0xFF82 twin nor the BitMouse page 0xFF05",
            Names(Shape(0x3142, 0x00C3, 0xFF90, 1, 0, outputIds: [0x55]), "FIFINE").SequenceEqual(["fifine.state"])
            && Untaken(Shape(0x3142, 0x00C3, 0xFF90, 2, 0, outputIds: [0x55]), "FIFINE")
            && Names(Shape(0x373B, 0x1262, 0xFF90, 1, 0, outputIds: [0x55]), "ATK Headset").SequenceEqual(["atk-headset.state"])
            && Names(Shape(0x373B, 0x1262, 0x90FF, 1, 0, outputIds: [0x55]), "ATK Headset").SequenceEqual(["atk-headset.state"])
            && Untaken(Shape(0x373B, 0x1262, 0xFF82, 1, 0, outputIds: [0x55]), "ATK Headset")
            && Untaken(Shape(0x373B, 0x1262, 0xFF05, 1, 0, outputIds: [0x55]), "ATK Headset")
            && Untaken(Shape(0x373B, 0x1263, 0xFF90, 1, 0, outputIds: [0x55]), "ATK Headset") && Untaken(Shape(0x373B, 0x1262, 0xFF90, 2, 0, outputIds: [0x55]), "ATK Headset")
            && Dongles.Find(0x373B, 0x1262, 0xFF90, "ATK N9V2 ULTIMATE dongle", 0, 0, 1, 64, 64) is null);
        check("FIFINE and ATK: the query 55 65 01 00; Voltide's measured answer (100 %), charging, full, the headset off (55 0A) and the link lost (1A); the query read back is none",
            Fifine.Request().SequenceEqual(Hex("55 65 01 00")) && AtkHeadsets.Request().SequenceEqual(Hex("55 65 01 00"))
            && AtkHeadsets.StateBattery(Hex("55 65 64 02 02 00 00 04 00 00 00 00 01 02 01 00 05 02 00", 64)) == new BatteryRead(100)
            && Fifine.Battery(Hex("55 65 32 03", 64)) == new BatteryRead(50, true) && Fifine.Battery(Hex("55 65 50 02 02 00 00 FF", 64)) == new BatteryRead(80)
            && AtkHeadsets.StateBattery(Hex("55 65 63 04", 64)) == new BatteryRead(100)
            && AtkHeadsets.StateBattery(Hex("55 0A 00 00", 64)) == new BatteryRead(null, Asleep: true)
            && Fifine.Battery(Hex("55 65 40 1A", 64)) == new BatteryRead(null, Asleep: true)
            && Fifine.Battery(Hex("55 65 01 00", 64)) is null && AtkHeadsets.StateBattery(Hex("55 65 C8 02", 64)) is null
            && AtkHeadsets.StateBattery(Hex("55 AA 01 02", 64)) is null);
    }

    private static void Xiberia(Action<string, bool> check)
    {
        check("XIBERIA: any 36F9 vendor collection of 64-byte reports that declares 0x55; not a standard collection, not without the report",
            Names(Shape(0x36F9, 0xF008, 0xFF00, 1, outputIds: [0x55, 0x41]), "XIBERIA").SequenceEqual(["xiberia.battery"])
            && Names(Shape(0x36F9, 0xF00A, 0x00FF, 1, outputIds: [0x55]), "XIBERIA").SequenceEqual(["xiberia.battery"])
            && Untaken(Shape(0x36F9, 0xF008, 0x000C, 1, outputIds: [0x55]), "XIBERIA")
            && BrandFamilies.Find(Shape(0x36F9, 0xF008, 0xFF00, 1, outputIds: [0x41])) is { Ways.Count: 0 });
        check("XIBERIA: the query 55 65 00 00, Voltide's documented answer (55 65 00 4B, 75 %), and the query read back none",
            BrimDeck.Core.Xiberia.Request().SequenceEqual(Hex("55 65 00 00")) && BrimDeck.Core.Xiberia.Battery(Hex("55 65 00 4B", 64)) == new BatteryRead(75)
            && BrimDeck.Core.Xiberia.Battery(Hex("55 65 00 00", 64)) is null && BrimDeck.Core.Xiberia.Battery(Hex("55 65 00 65", 64)) is null);
    }

    private static void Asus(Action<string, bool> check)
    {
        const string brand = "ASUS Headset";
        var dongle = Shape(0x0B05, 0x18D6, 0xFF00, 1, 0, feature: 64, featureIds: [0xFF]);
        var cable = Shape(0x0B05, 0x18D7, 0xFF00, 1, 0, feature: 64, featureIds: [0xFF]);
        check("ASUS Strix Go 2.4: the dongle and the cable on a vendor collection with a 64-byte feature report; the Delta II (Asus reads it) and a short feature report are not taken",
            Names(dongle, brand).SequenceEqual(["asus-headset.status"]) && Names(cable, brand).SequenceEqual(["asus-headset.status"])
            && Untaken(Shape(0x0B05, 0x1AFA, 0xFF00, 1, feature: 64, featureIds: [0xFF]), brand)
            && Untaken(Shape(0x0B05, 0x18D6, 0x000C, 1, feature: 64, featureIds: [0xFF]), brand)
            && Untaken(Shape(0x0B05, 0x18D6, 0xFF00, 1, feature: 32, featureIds: [0xFF]), brand));
        static byte[] Reply(int millivolts, byte state)
        {
            var frame = Hex("FF 1B", 64);
            frame[9] = state; frame[11] = (byte)millivolts; frame[12] = (byte)(millivolts >> 8);
            return frame;
        }
        check("ASUS Strix Go 2.4: the request; the reporter's 3831 mV (75 %) and 3743 mV (50 %) on battery, 3897 mV charging (lift taken off: 50 %), the cable always charging",
            BrandFamilies.Find(dongle)!.Value.Ways[0].Request.AsSpan(0, 10).SequenceEqual(Hex("FF 08 00 FD 04 12 F1 03 52 01"))
            && Parse(dongle, Reply(3831, 0x03)) == new BatteryRead(75, Coarse: true) && Parse(dongle, Reply(3743, 0x03)) == new BatteryRead(50, Coarse: true)
            && Parse(dongle, Reply(3897, 0x0A)) == new BatteryRead(50, true, Coarse: true) && Parse(dongle, Reply(4150, 0x03)) == new BatteryRead(100, Coarse: true)
            && Parse(cable, Reply(4077, 0x03)) == new BatteryRead(75, true, Coarse: true));
        check("ASUS Strix Go 2.4: bytes 1 to 3 zero (off or asleep, HaloBattery) asleep; the unknown-packet answer (FF AA), a frame without the live header and the request read back are none",
            Parse(dongle, Hex("FF", 64)) == new BatteryRead(null, Asleep: true)
            && Parse(dongle, Hex("FF FF AA", 64)) is null && Parse(dongle, Reply(3831, 0x03).Select((b, i) => i == 1 ? (byte)0x1A : b).ToArray()) is null
            && Parse(dongle, BrandFamilies.Find(dongle)!.Value.Ways[0].Request) is null);
    }

    private static void Astro(Action<string, bool> check)
    {
        var base5 = Shape(0x046D, 0x0B1C, 0xFF32, 0x74, 8, outputIds: [0x02]);
        check("ASTRO A50 Gen 5: its 0xFF32 usage 0x74 collection only, the page as Windows gave it to HaloBattery (Logitech's 0xFF00 is HID++'s); the Gen 4 is not taken",
            Names(base5, "ASTRO").SequenceEqual(["astro.battery"]) && Untaken(Shape(0x046D, 0x0B1C, 0xFF00, 1, outputIds: [0x02]), "ASTRO")
            && Untaken(Shape(0x046D, 0x0B1C, 0x32FF, 0x74, 8, outputIds: [0x02]), "ASTRO") && Untaken(Shape(0x046D, 0x0B1C, 0xFF32, 0x74, 8, input: 9, outputIds: [0x02]), "ASTRO")
            && Untaken(Shape(0x046D, 0xC539, 0xFF32, 0x74, outputIds: [0x02]), "ASTRO") && Untaken(Shape(0x9886, 0x002C, 0xFF32, 0x74, outputIds: [0x02]), "ASTRO"));
        check("ASTRO A50 Gen 5: the GET 02 0C 03 00 06 0C; the documented answer 02 0C 06 00 06 00 <percent> <level> <dock>; the GET read back is none",
            AstroA50.Request().SequenceEqual(Hex("02 0C 03 00 06 0C"))
            && AstroA50.Battery(Hex("02 0C 06 00 06 00 4B 4B 01", 64)) == new BatteryRead(75, true) && AstroA50.Battery(Hex("02 0C 06 00 06 00 4B 4B 00", 64)) == new BatteryRead(75)
            && AstroA50.Battery(Hex("02 0C 03 00 06 0C", 64)) is null && AstroA50.Battery(Hex("02 0C 06 00 0A 00 4B 4B 00", 64)) is null);
    }

    private static void Lenovo(Action<string, bool> check)
    {
        var receiver = Shape(0x17EF, 0xA07D, 0xFF07, 0x0222, 3, input: 61, output: 0, feature: 61, featureIds: [0x24]);
        check("Lenovo Wireless VoIP: the receiver's 0xFF07 usage 0x0222 collection on interface 3 only",
            Names(receiver, "Lenovo Headset").SequenceEqual(["lenovo-headset.status"])
            && Untaken(Shape(0x17EF, 0xA07D, 0xFF07, 0x0001, 3, featureIds: [0x24]), "Lenovo Headset")
            && Untaken(Shape(0x17EF, 0x6019, 0xFF07, 0x0222, 3, featureIds: [0x24]), "Lenovo Headset"));
        check("Lenovo Wireless VoIP: the status command 24 01 (61 bytes), the answer 27 01 00 with the percent at 7, an error byte (not connected), another report none",
            LenovoHeadsets.Request().Length == 61 && LenovoHeadsets.Request().AsSpan(0, 3).SequenceEqual(Hex("24 01 00"))
            && LenovoHeadsets.Battery(Hex("27 01 00 00 00 00 00 40", 61)) == new BatteryRead(64)
            && LenovoHeadsets.Battery(Hex("27 01 01", 61)) == new BatteryRead(null, Asleep: true)
            && LenovoHeadsets.Battery(Hex("24 01 00 00 00 00 00 40", 61)) is null && LenovoHeadsets.Battery(Hex("27 0E 00 00 00 00 00 40", 61)) is null);
    }

    private static void Darkrim(Action<string, bool> check)
    {
        var dongle = Shape(0x3654, 0x6901, 0xFF02, 2, 3, input: 65, output: 65, outputIds: [0]);
        var family = BrandFamilies.Find(dongle);
        check("DARKRIM: interface 3's 0xFF02 usage 2 collection only (interface 4's report 02 does not answer), standing aside for DARKRIM.exe",
            Names(dongle, "DARKRIM").SequenceEqual(["darkrim.basicinfo"]) && family!.Value.Family.StandAsideFor.Contains("DARKRIM.exe")
            && family.Value.Ways[0].StripZero
            && Untaken(Shape(0x3654, 0x6901, 0xFF02, 2, 4, outputIds: [0x02]), "DARKRIM") && Untaken(Shape(0x3654, 0x6902, 0xFF02, 2, 3, input: 65, output: 65, outputIds: [0]), "DARKRIM"));
        var charging = Hex("FE 00 11 00 03 00 00 03 00 01 01 DA 00 00");
        ushort crc = BrimDeck.Core.Darkrim.Crc16(charging.AsSpan(0, 12));
        charging[12] = (byte)(crc >> 8); charging[13] = (byte)crc;
        check("DARKRIM: the CRC on vigil-watt's captured frames, the GetBasicInfo request, the measured notice (90 %) and charging (DA); the request read back, the GetVersion echo and a bad CRC are none",
            BrimDeck.Core.Darkrim.Crc16(Hex("FE 00 10 00 02 00 00 03 00 09 01 00")) == 0xE23C && BrimDeck.Core.Darkrim.Crc16(Hex("FE 04 10 00 02 00 00 03 00 09 01 00")) == 0xB37B
            && BrimDeck.Core.Darkrim.Request().Length == 65 && BrimDeck.Core.Darkrim.Request().AsSpan(0, 15).SequenceEqual(Hex("00 FE 00 10 00 02 00 00 03 00 09 01 00 E2 3C"))
            && BrimDeck.Core.Darkrim.Battery(Hex("FE 00 11 00 03 00 00 03 00 01 01 5A 98 B4", 64)) == new BatteryRead(90)
            && BrimDeck.Core.Darkrim.Battery(charging) == new BatteryRead(90, true)
            && BrimDeck.Core.Darkrim.Battery(Hex("FE 00 10 00 02 00 00 03 00 09 01 00 E2 3C", 64)) is null
            && BrimDeck.Core.Darkrim.Battery(Hex("FE 05 10 00 02 00 00 03 00 01 01 5A 39 2C", 64)) is null
            && BrimDeck.Core.Darkrim.Battery(Hex("FE 00 11 00 03 00 00 03 00 01 01 5A 98 B5", 64)) is null
            && BrimDeck.Core.Darkrim.Battery(Hex("FE 00 11 00 03 00 00 10 00 03 0E 00 01 01 01 09 30 31 2E 30 31 2E 30 35 00 7A DC", 64)) is null);
        static byte[] Notice(params byte[] payload)
        {
            var frame = new byte[64];
            new byte[] { 0xFE, 0x00, 0x11, 0x00, 0x03, 0x00, 0x00, (byte)payload.Length, 0x00 }.CopyTo(frame, 0);
            payload.CopyTo(frame, 9);
            ushort sum = BrimDeck.Core.Darkrim.Crc16(frame.AsSpan(0, 9 + payload.Length));
            frame[9 + payload.Length] = (byte)(sum >> 8); frame[10 + payload.Length] = (byte)sum;
            return frame;
        }
        check("DARKRIM: left, right and case: the lower bud, charging by bit 7; all three 0 (not linked) asleep; a single 0 none",
            BrimDeck.Core.Darkrim.Battery(Notice(0x01, 0x03, 0x50, 0x46, 0x64)) == new BatteryRead(70)
            && BrimDeck.Core.Darkrim.Battery(Notice(0x01, 0x03, 0xD0, 0x46, 0x64)) == new BatteryRead(70, true)
            && BrimDeck.Core.Darkrim.Battery(Notice(0x01, 0x03, 0x00, 0x00, 0x00)) == new BatteryRead(null, Asleep: true)
            && BrimDeck.Core.Darkrim.Battery(Notice(0x01, 0x01, 0x00)) is null);
    }

    private static void Edifier(Action<string, bool> check)
    {
        check("EDIFIER Halo Space: its 0xFF14 usage 1 collection with report 0x2E; the 0xFF13 collection beside it is not taken",
            Names(Shape(0x2D99, 0xE049, 0xFF14, 1, 4, outputIds: [0x2E]), "EDIFIER").SequenceEqual(["edifier.battery"])
            && Untaken(Shape(0x2D99, 0xE049, 0xFF13, 1, 4, outputIds: [0x2E]), "EDIFIER") && Untaken(Shape(0x2D99, 0xE04A, 0xFF14, 1, 4, outputIds: [0x2E]), "EDIFIER"));
        check("EDIFIER Halo Space: the request 2E AA EC D0 00 00 66, FerrisPulse's capture 2F BB EC D0 00 01 86 FE (35 %), a wrong sum and the request read back none",
            BrimDeck.Core.Edifier.Request().SequenceEqual(Hex("2E AA EC D0 00 00 66"))
            && BrimDeck.Core.Edifier.Battery(Hex("2F BB EC D0 00 01 86 FE 5A A3 A1 A5", 64)) == new BatteryRead(35)
            && BrimDeck.Core.Edifier.Battery(Hex("2F BB EC D0 00 01 86 FD", 64)) is null
            && BrimDeck.Core.Edifier.Battery(Hex("2E AA EC D0 00 00 66", 64)) is null
            && BrimDeck.Core.Edifier.Battery(Hex("2F BB EC C9 00 01 86 F7", 64)) is null);
    }

    private static void SteelSeries(Action<string, bool> check)
    {
        const string brand = "SteelSeries Headset";
        var arctis1 = Shape(0x1038, 0x12B3, 0xFF43, 0x0202, 3, outputIds: [0x06]);
        var nova5 = Shape(0x1038, 0x2232, 0xFFC0, 1, 3, outputIds: [0]);
        var plus = Shape(0x1038, 0x220E, 0xFFC0, 1, 3, outputIds: [0]);
        var buds = Shape(0x1038, 0x230A, 0xFFC0, 1, 3, outputIds: [0]);
        var elite = Shape(0x1038, 0x2244, 0xFFC0, 1, 3, outputIds: [0x01]);
        var omni = Shape(0x1038, 0x2290, 0xFFC0, 1, 3, outputIds: [0x01]);
        check("SteelSeries headsets: the Arctis 1 family on 0xFF43 usage 0x0202, the B0 family and the Elite and Omni on 0xFFC0 usage 1, all on interface 3; the Pro Wireless not",
            Names(arctis1, brand).SequenceEqual(["steelseries-headset.arctis1"]) && Names(nova5, brand).SequenceEqual(["steelseries-headset.status"])
            && Names(plus, brand).SequenceEqual(["steelseries-headset.status"]) && Names(buds, brand).SequenceEqual(["steelseries-headset.status"])
            && Names(elite, brand).SequenceEqual(["steelseries-headset.status1"]) && Names(omni, brand).SequenceEqual(["steelseries-headset.status1"])
            && BrandFamilies.Find(Shape(0x1038, 0x2244, 0xFFC0, 1, 3, outputIds: [0x07])) is { Ways.Count: 0 }
            && Untaken(Shape(0x1038, 0x2232, 0xFFC0, 1, 4, outputIds: [0]), brand) && Untaken(Shape(0x1038, 0x2232, 0x000C, 1, 3, outputIds: [0]), brand)
            && Untaken(Shape(0x1038, 0x1290, 0xFF00, 1, 3, outputIds: [0x40, 0x41]), brand)
            && BrandFamilies.Find(buds)!.Value.Family.Kind(buds) == DeviceKind.Earbuds);
        check("SteelSeries headsets: the requests 06 12, 00 B0 and 01 B0 (64 bytes)",
            BrandFamilies.Find(arctis1)!.Value.Ways[0].Request.SequenceEqual(Hex("06 12")) && BrandFamilies.Find(nova5)!.Value.Ways[0].Request.SequenceEqual(Hex("00 B0"))
            && BrandFamilies.Find(nova5)!.Value.Ways[0].StripZero && !BrandFamilies.Find(elite)!.Value.Ways[0].StripZero
            && BrandFamilies.Find(elite)!.Value.Ways[0].Request.SequenceEqual(Hex("01 B0", 64)));
        check("SteelSeries headsets: the documented answers of each layout, HaloBattery's measured Elite answer (31 %), the headset off",
            Parse(arctis1, Hex("06 12 00 50", 8)) == new BatteryRead(80) && Parse(arctis1, Hex("06 12 01 50", 8)) == new BatteryRead(null, Asleep: true)
            && Parse(plus, Hex("B0 00 03 01", 64)) == new BatteryRead(75, true, Coarse: true) && Parse(plus, Hex("B0 01 03 00", 64)) == new BatteryRead(null, Asleep: true)
            && Parse(nova5, Hex("B0 03 00 40 01", 64)) == new BatteryRead(64, true) && Parse(nova5, Hex("B0 02 00 40 00", 64)) == new BatteryRead(null, Asleep: true)
            && Parse(buds, Hex("B0 00 00 03 03 50 46", 64)) == new BatteryRead(70) && Parse(buds, Hex("B0 00 00 03 02 50 00", 64)) == new BatteryRead(80)
            && Parse(elite, Hex("01 B0 00 00 01 00 1F 64", 64)) == new BatteryRead(31) && Parse(elite, Hex("01 B0 00 00 01 00 1F 64 00 00 00 00 00 00 01", 64)) == new BatteryRead(null, Asleep: true)
            && Parse(omni, Hex("01 B0 00 00 00 00 5D 00 00 00 00 00 00 00 00 08", 64)) == new BatteryRead(93)
            && Parse(omni, Hex("01 B0 00 00 00 00 5D 00 00 00 00 00 00 00 00 02", 64)) == new BatteryRead(93, true)
            && Parse(omni, Hex("01 B0 00 00 00 00 5D 00 00 00 00 00 00 00 00 01", 64)) == new BatteryRead(null, Asleep: true));
        check("SteelSeries headsets: buds both docked or off are asleep (HeadsetControl's offline); the Elite on its cable (power 02) charges",
            Parse(buds, Hex("B0 00 00 02 02 00 00", 64)) == new BatteryRead(null, Asleep: true) && Parse(buds, Hex("B0 00 00 03 02 00 00", 64)) == new BatteryRead(null, Asleep: true)
            && Parse(elite, Hex("01 B0 00 00 01 00 1F 64 00 00 00 00 00 00 02", 64)) == new BatteryRead(31, true)
            && Parse(elite, Hex("01 B0 00 00 01 00 1F 64 00 00 00 00 00 00 04", 64)) == new BatteryRead(31));
        check("SteelSeries headsets: each request read back, another report, and a level out of range are none",
            Parse(arctis1, Hex("06 12 00 00", 8)) is null && Parse(plus, Hex("B0", 64)) is null && Parse(nova5, Hex("B0", 64)) is null
            && Parse(buds, Hex("B0", 64)) is null && Parse(buds, Hex("B0 00 00 01 02 00 00", 64)) is null
            && Parse(elite, Hex("01 B0", 64)) is null && Parse(omni, Hex("01 B0", 64)) is null
            && Parse(nova5, Hex("01 00 63 02 00", 64)) is null && Parse(plus, Hex("B0 00 05 00", 64)) is null && Parse(nova5, Hex("B0 03 00 C8 00", 64)) is null);
    }
}

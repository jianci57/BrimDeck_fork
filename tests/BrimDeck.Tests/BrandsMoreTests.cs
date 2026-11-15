using BrimDeck.Core;

// The brand families on the newer transports: Airoha RACE headsets (input report fetched after the request), the PULSE
// Explore (feature report fetched alone), the ASTRO A50 Gen 4 and the RK R98Pro V2 (a question first), the BlackShark V3 (read
// at most every five minutes). What each takes and leaves, its requests, the answers its sources quote, and acknowledgements,
// echoes, zeros and stale bytes read as no reading.
static class BrandsMoreTests
{
    private static HidShape Shape(int vendor, int product, int page, int usage = 1, int iface = -1, int input = 64, int output = 64, int feature = 0,
        byte[]? outputIds = null, byte[]? featureIds = null, string name = "")
        => BrandFamilyTests.Shape(vendor, product, page, usage, iface, input, output, feature, outputIds, featureIds, name);

    // Hex bytes, padded with zeros to length.
    private static byte[] Hex(string hex, int length = 0)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        return bytes.Length >= length ? bytes : BrandFamilies.Frame(length, bytes);
    }

    private static string[] Names(HidShape shape, string brand)
        => BrandFamilies.Find(shape) is { } found && found.Family.Brand == brand ? [.. found.Ways.Select(way => way.Name)] : [];
    private static bool Untaken(HidShape shape, string brand) => BrandFamilies.Find(shape) is not { } found || found.Family.Brand != brand;
    private static BrandWay Way(HidShape shape) => BrandFamilies.Find(shape)!.Value.Ways[0];
    private static bool Light(BrandWay way) => way.Attempts is >= 1 and <= 2 && way.TimeoutMs <= 1500;

    public static void Run(Action<string, bool> check)
    {
        Airoha(check);
        Pulse(check);
        Astro(check);
        BlackShark(check);
        Rk(check);
    }

    private static HidShape Race(int vendor, int product, int page = 0xFF13, int usage = 1, int iface = -1, string name = "")
        => Shape(vendor, product, page, usage, iface, 62, 62, outputIds: [0x06], name: name);

    private static void Airoha(Action<string, bool> check)
    {
        check("Airoha RACE: Audeze's Maxwell dongles, cable and Maxwell 2 on 0xFF13 usage 1; the measured page only as Windows gave it for the Maxwell, either order for the Maxwell 2; no other 3329 product, no other collection",
            Names(Race(0x3329, 0x4B19, name: "Audeze Maxwell HID"), "Audeze").SequenceEqual(["audeze.race"])
            && Names(Race(0x3329, 0x4B18, name: "Audeze Maxwell HID"), "Audeze").SequenceEqual(["audeze.race"])
            && Names(Race(0x3329, 0x4B1A, name: "Audeze Maxwell Headset"), "Audeze").SequenceEqual(["audeze.race"])
            && Names(Race(0x3329, 0x4B29, iface: 5), "Audeze").SequenceEqual(["audeze.race"])
            && Names(Race(0x3329, 0x4B28, 0x13FF, iface: 5), "Audeze").SequenceEqual(["audeze.race"])
            && Untaken(Race(0x3329, 0x4B19, 0x13FF), "Audeze") && Untaken(Race(0x3329, 0x4B1E), "Audeze")
            && Untaken(Race(0x3329, 0x4B19, 0x000C), "Audeze") && Untaken(Race(0x3329, 0x4B19, usage: 2), "Audeze")
            && Untaken(Shape(0x3329, 0x4B19, 0xFF13, 1, -1, 64, 32, outputIds: [0x06]), "Audeze"));
        check("Airoha RACE: a Maxwell dongle that names itself ...Dongle (no headset linked) is taken but not asked; the cable is asked whatever its name",
            BrandFamilies.Find(Race(0x3329, 0x4B19, name: "Audeze Maxwell Dongle")) is { Family.Brand: "Audeze", Ways.Count: 0 }
            && BrandFamilies.Find(Race(0x3329, 0x4B18, name: "Audeze Maxwell XBOX Dongle ")) is { Family.Brand: "Audeze", Ways.Count: 0 }
            && Names(Race(0x3329, 0x4B1A, name: "Audeze Maxwell Dongle"), "Audeze").SequenceEqual(["audeze.race"]));
        check("Airoha RACE: the AKG N9 Hybrid's 62/62 collection on interface 3 only (0x0ECB is Harman's: JBL's Quantum 910 is not taken)",
            Names(Race(0x0ECB, 0x20D9, iface: 3), "AKG").SequenceEqual(["akg.race"])
            && Untaken(Race(0x0ECB, 0x20D9, iface: 0), "AKG") && Untaken(Race(0x0ECB, 0x20D9, 0x13FF, iface: 3), "AKG")
            && BrandFamilies.Find(Race(0x0ECB, 0x2088, iface: 3)) is null);
        check("Airoha RACE: Skullcandy's PLYR (its dump: interface 5, ff13:0001, \"PLYR Dongle Chat\") and Crusher PLYR 720; not the SLYR Pro, not the consumer collection",
            Names(Race(0x34F0, 0x3210, iface: 5, name: "PLYR Dongle Chat"), "Skullcandy").SequenceEqual(["skullcandy.race"])
            && Names(Race(0x34F0, 0x5310, 0x13FF), "Skullcandy").SequenceEqual(["skullcandy.race"])
            && Untaken(Race(0x34F0, 0x3210, 0x13FF), "Skullcandy") && Untaken(Race(0x34F0, 0x2220), "Skullcandy")
            && Untaken(Race(0x34F0, 0x3210, 0x000C, iface: 5), "Skullcandy"));

        var way = Way(Race(0x34F0, 0x3210));
        check("Airoha RACE: output report 6 as the Skull-HQ capture has it (06 07 80 05 5A 03 00 D6 0C 00, 62 bytes), input report 7 fetched after it, light",
            way.Transport == WayTransport.OutputGetInput && way.ReplyId == 0x07 && Light(way)
            && way.Request.SequenceEqual(Hex("06 07 80 05 5A 03 00 D6 0C 00", 62)) && way.Request.Length == 62
            && Way(Race(0x3329, 0x4B19)).Request.SequenceEqual(way.Request) && Way(Race(0x0ECB, 0x20D9, iface: 3)).Request.SequenceEqual(way.Request));

        // Skull-HQ's capture (HaloBattery issue #104): ack and result in one report, 70 %, older bytes after the 16 valid ones.
        var plyr = Hex("07 10 80 05 5B 03 00 D6 0C 00 05 5D 05 00 D6 0C 00 00 46 00 00 00 00 FF 00 00 00 00 00 01 00", 62);
        // vigil-watt's N9 Hybrid sample (50 %), and its logged result 05 5D 05 00 D6 0C 00 00 64 put in the same layout (constructed).
        var n9 = Hex("07 10 80 05 5B 03 00 D6 0C 00 05 5D 05 00 D6 0C 00 00 32", 62);
        var n9Full = Hex("07 10 80 05 5B 03 00 D6 0C 00 05 5D 05 00 D6 0C 00 00 64", 62);
        check("Airoha RACE: the captured answers read 70 % (PLYR) and 50 % (N9 Hybrid), and 100 % in the same layout",
            AirohaRace.Battery(plyr) == new BatteryRead(70) && AirohaRace.Battery(n9) == new BatteryRead(50) && AirohaRace.Battery(n9Full) == new BatteryRead(100));
        check("Airoha RACE: the Maxwell's cable reads as charging (it charges whenever it is on the cable), its dongles not",
            Way(Race(0x3329, 0x4B1A, name: "Audeze Maxwell Headset")).Parse(plyr) == new BatteryRead(70, true)
            && Way(Race(0x3329, 0x4B19, name: "Audeze Maxwell HID")).Parse(plyr) == new BatteryRead(70));

        // Constructed from the documented layout: an older result before this request's acknowledgement, then the new one; a
        // falling run after the acknowledgement (the last one is the level); a refused acknowledgement with an old result after it.
        var older = Hex("07 19 80 05 5D 05 00 D6 0C 00 00 50 05 5B 03 00 D6 0C 00 05 5D 05 00 D6 0C 00 00 46", 62);
        var run = Hex("07 19 80 05 5B 03 00 D6 0C 00 05 5D 05 00 D6 0C 00 00 63 05 5D 05 00 D6 0C 00 00 61", 62);
        var oldOnly = Hex("07 10 80 05 5D 05 00 D6 0C 00 00 50 05 5B 03 00 D6 0C 00", 62);
        var refusedAck = Hex("07 10 80 05 5B 03 00 D6 0C 01 05 5D 05 00 D6 0C 00 00 46", 62);
        check("Airoha RACE: a result before the acknowledgement is an older one; the last of a run is the level; a refusal is none",
            AirohaRace.Battery(older) == new BatteryRead(70) && AirohaRace.Battery(run) == new BatteryRead(97)
            && AirohaRace.Battery(oldOnly) is null && AirohaRace.Battery(refusedAck) is null);

        // Constructed from the documented layout: the acknowledgement alone, then the same report still holding an older
        // result after its valid bytes; a dongle stuck echoing (HaloBattery: 07 00 80 00 ...); a refused or wrong-role result.
        var ack = Hex("07 07 80 05 5B 03 00 D6 0C 00 05 5D 05 00 D6 0C 00 00 41", 62);
        var refused = (byte[])plyr.Clone(); refused[16] = 0x01;
        var role = (byte[])plyr.Clone(); role[17] = 0x01;
        var zero = (byte[])plyr.Clone(); zero[18] = 0x00;
        var high = (byte[])plyr.Clone(); high[18] = 0x65;
        var cut = (byte[])plyr.Clone(); cut[1] = 0x0F;
        var dongle = (byte[])plyr.Clone(); dongle[2] = 0x00;
        check("Airoha RACE: the acknowledgement is no reading, nor a result past the valid length (stale), an empty or stuck report, a failed status or another role, 0 or over 100, a cut result, the request itself",
            AirohaRace.Battery(ack) is null && AirohaRace.Battery(Hex("07 00 80", 62)) is null && AirohaRace.Battery(Hex("07 00 80 05 5D 05 00 D6 0C 00 00 46", 62)) is null
            && AirohaRace.Battery(refused) is null && AirohaRace.Battery(role) is null && AirohaRace.Battery(zero) is null && AirohaRace.Battery(high) is null
            && AirohaRace.Battery(cut) is null && AirohaRace.Battery(dongle) is null && AirohaRace.Battery(AirohaRace.Request()) is null
            && AirohaRace.Battery(Hex("07 3C 80", 62)) is null);
    }

    private static void Pulse(Action<string, bool> check)
    {
        var receiver = Shape(0x054C, 0x0ECC, 0xFF01, 0x20, -1, 64, 0, 64, featureIds: [0xB0]);
        var usbCase = Shape(0x054C, 0x0ED0, 0xFF13, 1, -1, 64, 62, 64, outputIds: [0x06], featureIds: [0xB0], name: "PULSE Explore");
        check("Sony PULSE: the PlayStation Link receiver's 0xFF01 usage 0x20 collection and the case's 0xFF13 one (named PULSE Explore), each with feature report B0; nothing else of Sony's",
            Names(receiver, "Sony PULSE").SequenceEqual(["pulse.receiver"]) && Names(usbCase, "Sony PULSE").SequenceEqual(["pulse.case"])
            && Untaken(Shape(0x054C, 0x0ECC, 0x01FF, 0x20, -1, 64, 0, 64, featureIds: [0xB0]), "Sony PULSE")
            && Untaken(Shape(0x054C, 0x0ECC, 0xFF01, 0x01, -1, 64, 0, 64, featureIds: [0xB0]), "Sony PULSE")
            && Untaken(Shape(0x054C, 0x0ED0, 0xFF13, 1, -1, 64, 62, 64, featureIds: [0xB0], name: "Other"), "Sony PULSE")
            && Untaken(Shape(0x054C, 0x0EC2, 0xFF04, 1, -1, 64, 64, 64, featureIds: [0xB0]), "Sony PULSE")
            && BrandFamilies.Find(Shape(0x054C, 0x0ECC, 0xFF01, 0x20, -1, 64, 0, 64, featureIds: [0x80])) is { Ways.Count: 0 });
        var way = Way(receiver);
        check("Sony PULSE: report B0 fetched alone (64 bytes), nothing written",
            way.Transport == WayTransport.FeatureGet && way.Request.SequenceEqual(new byte[] { 0xB0 }) && way.FeatureLength == 64 && way.Before.Count == 0 && Light(way)
            && Way(usbCase).Transport == WayTransport.FeatureGet);

        // Constructed from FerrisPulse's documented fields and its recorded states (levels at 55, flags at 56, link at 39).
        static byte[] B0(byte model, byte levels, byte flags, byte link)
        {
            var r = Hex("B0 50 01", 64);
            r[29] = 0x11; r[30] = model; r[39] = link; r[55] = levels; r[56] = flags;
            return r;
        }
        check("Sony PULSE: both buds in use (levels DD) read 87 % in steps; one bud in the case reads the other (DF/AD the right 87, DF/5F the left 100); closed in the case (link 0) asleep",
            SonyPulse.Wireless(B0(0x00, 0xDD, 0xFF, 1)) == new BatteryRead(87, Coarse: true)
            && SonyPulse.Wireless(B0(0x00, 0xDF, 0xAD, 1)) == new BatteryRead(87, Coarse: true)
            && SonyPulse.Wireless(B0(0x00, 0xDF, 0x5F, 1)) == new BatteryRead(100, Coarse: true)
            && SonyPulse.Wireless(B0(0x00, 0xDE, 0x0F, 0)) == new BatteryRead(null, Asleep: true)
            && SonyPulse.Wireless(B0(0x00, 0x3D, 0xFF, 1)) == new BatteryRead(20, Coarse: true));
        check("Sony PULSE: on USB, the buds in the case (0F/1F the left full; BF/3F the right at 73 % charging), none in the case asleep",
            SonyPulse.Usb(B0(0x01, 0x0F, 0x1F, 0)) == new BatteryRead(100, Coarse: true)
            && SonyPulse.Usb(B0(0x01, 0xBF, 0x3F, 0)) == new BatteryRead(73, Charging: true, Coarse: true)
            && SonyPulse.Usb(B0(0x01, 0xBF, 0x0F, 0)) == new BatteryRead(null, Asleep: true));
        var elite = B0(0x00, 0xDD, 0xFF, 1); elite[29] = 0x12;
        var other = B0(0x00, 0xDD, 0xFF, 1); other[1] = 0x51;
        check("Sony PULSE: another model on the receiver (the PULSE Elite), the other path's report, another layout, a zero step or a short report is no reading",
            SonyPulse.Wireless(elite) is null && SonyPulse.Wireless(B0(0x01, 0xDD, 0xFF, 1)) is null && SonyPulse.Usb(B0(0x00, 0xDD, 0xFF, 1)) is null
            && SonyPulse.Wireless(other) is null && SonyPulse.Wireless(B0(0x00, 0xD0, 0xFF, 1)) is null && SonyPulse.Wireless(Hex("B0 50 01", 40)) is null);
    }

    private static void Astro(Action<string, bool> check)
    {
        var base4 = Shape(0x9886, 0x002C, 0xFF32, 0x74, 6, outputIds: [0x02]);
        check("ASTRO A50 Gen 4: its 0xFF32 usage 0x74 collection on interface 6 (either byte order); not another interface, the console mode's 002B or the Gen 5",
            Names(base4, "ASTRO A50 Gen 4").SequenceEqual(["astro.gen4"]) && Names(Shape(0x9886, 0x002C, 0x32FF, 0x74, 6, outputIds: [0x02]), "ASTRO A50 Gen 4").Length == 1
            && Untaken(Shape(0x9886, 0x002C, 0xFF32, 0x74, 3, outputIds: [0x02]), "ASTRO A50 Gen 4")
            && Untaken(Shape(0x9886, 0x002B, 0xFF32, 0x74, 6, outputIds: [0x02]), "ASTRO A50 Gen 4")
            && Untaken(Shape(0x9886, 0x002C, 0xFF32, 0x01, 6, outputIds: [0x02]), "ASTRO A50 Gen 4")
            && Names(Shape(0x046D, 0x0B1C, 0xFF32, 0x74, 8, outputIds: [0x02]), "ASTRO").SequenceEqual(["astro.battery"]));
        var way = Way(base4);
        check("ASTRO A50 Gen 4: the headset status 02 54 asked first, then the battery 02 7C (64-byte frames), light",
            way.Gate is { } gate && gate.SequenceEqual(Hex("02 54", 64)) && way.Request.SequenceEqual(Hex("02 7C", 64)) && way.Gated is not null && Light(way));
        // Constructed from the documented reply layout 02 <status> <length> <payload> (no source quotes a captured reply).
        check("ASTRO A50 Gen 4: status bit 1 is the link (03 and 02 linked, 01 docked alone not, 00 off); an error or an empty acceptance is not the answer",
            AstroA50Gen4.Linked(Hex("02 02 01 03", 64)) == true && AstroA50Gen4.Linked(Hex("02 02 01 02", 64)) == true
            && AstroA50Gen4.Linked(Hex("02 02 01 01", 64)) == false && AstroA50Gen4.Linked(Hex("02 02 01 00", 64)) == false
            && AstroA50Gen4.Linked(Hex("02 01 08 00 00 00 00 48 49 44", 64)) is null && AstroA50Gen4.Linked(Hex("02 00 00", 64)) is null);
        check("ASTRO A50 Gen 4: bit 7 charging and bits 0 to 6 the percent (E4 100 % charging, 50 80 %); 0, over 100, an error, an empty acceptance or the request read back is none",
            AstroA50Gen4.Level(Hex("02 02 01 E4", 64)) == new BatteryRead(100, true) && AstroA50Gen4.Level(Hex("02 02 01 50", 64)) == new BatteryRead(80)
            && AstroA50Gen4.Level(Hex("02 02 01 00", 64)) is null && AstroA50Gen4.Level(Hex("02 02 01 80", 64)) is null && AstroA50Gen4.Level(Hex("02 02 01 65", 64)) is null
            && AstroA50Gen4.Level(Hex("02 01 01 50", 64)) is null && AstroA50Gen4.Level(Hex("02 00 00", 64)) is null && AstroA50Gen4.Level(AstroA50Gen4.Request(0x7C)) is null);
    }

    private static void BlackShark(Action<string, bool> check)
    {
        var dongle = Shape(0x1532, 0x057A, 0xFF14, 1, 5, outputIds: [0x02], name: "BlackShark V3 HID");
        check("Razer BlackShark V3: the dongle's 0xFF14 usage 1 collection on interface 5, as measured; not its 0xFF13 or consumer collections, nor Razer's other headsets; nothing Razer's mouse reader knows",
            Names(dongle, "Razer BlackShark V3").SequenceEqual(["blackshark-v3.battery"])
            && Untaken(Shape(0x1532, 0x057A, 0xFF13, 1, 5, outputIds: [0x02]), "Razer BlackShark V3") && BrandFamilies.Find(Shape(0x1532, 0x057A, 0xFF13, 1, 5, 62, 62, outputIds: [0x06])) is null
            && Untaken(Shape(0x1532, 0x057A, 0x14FF, 1, 5, outputIds: [0x02]), "Razer BlackShark V3")
            && Untaken(Shape(0x1532, 0x057A, 0x000C, 1, 5, outputIds: [0x02]), "Razer BlackShark V3")
            && Untaken(Shape(0x1532, 0x056C, 0xFF14, 1, 5, outputIds: [0x02]), "Razer BlackShark V3")
            && BrimDeck.Core.Razer.Find(0x057A) is null);
        var way = Way(dongle);
        check("Razer BlackShark V3: Synapse's frames as captured (wake 02 then 2A, the charging query, then the battery query, each with its XOR), one try, at most once in five minutes",
            way.Before.Count == 3 && way.Before[0].SequenceEqual(Frame("02 00 60 00 00 00 04 00 00 00 02", 0x64))
            && way.Before[1].SequenceEqual(Frame("02 00 60 00 00 00 04 00 00 00 2A", 0x4C)) && way.Before[2].SequenceEqual(Frame("02 00 60 00 00 00 04 00 00 80 2A", 0xCC))
            && way.Request.SequenceEqual(Frame("02 00 60 00 00 00 04 00 00 80 21", 0xC7)) && way.After.Count == 0
            && way.Attempts == 1 && way.MinIntervalMs == 300_000 && way.TimeoutMs <= 1500 && way.Transport == WayTransport.Output);

        var battery97 = Frame("02 02 60 00 00 00 05 00 00 80 21 01 01 61", 0xA5);
        var notCharging = Frame("02 02 60 00 00 00 05 00 00 80 2A 01 01 00", 0xCF);
        // A push of the same layout with its XOR worked out (HaloBattery's test builds it so; constructed).
        var push = Frame("02 02 60 00 00 00 05 00 00 80 21 02 01 60", 0x00); push[62] = Xor(push);
        var notReady = Frame("02 02 60 00 00 00 05 00 00 80 21 01 01 FF", 0x00); notReady[62] = Xor(notReady);
        var badXor = (byte[])battery97.Clone(); badXor[62] ^= 0x01;
        check("Razer BlackShark V3: the captured answer reads 97 % (a push of the battery too); the charging answer, the request read back, a wrong XOR, not ready (FF) are none",
            RazerBlackSharkV3.Level(battery97) == new BatteryRead(97) && RazerBlackSharkV3.Level(push) == new BatteryRead(96)
            && RazerBlackSharkV3.Level(notCharging) is null && RazerBlackSharkV3.Level(RazerBlackSharkV3.Frame(0x21)) is null
            && RazerBlackSharkV3.Level(badXor) is null && RazerBlackSharkV3.Level(notReady) is null);
    }

    private static byte[] Frame(string head, byte xor) { var frame = Hex(head, 64); frame[62] = xor; return frame; }
    private static byte Xor(byte[] frame) { byte x = 0; for (int i = 0; i < 62; i++) x ^= frame[i]; return x; }

    private static void Rk(Action<string, bool> check)
    {
        var receiver = Shape(0x258A, 0x01BB, 0xFF02, 2, 1, 20, 20, outputIds: [0x13]);
        check("RK: the R98Pro V2's receivers (01BB, 0233) on their 0xFF02 usage 2 collection with 20-byte reports; no other Sinowealth product, length or page order",
            Names(receiver, "RK").SequenceEqual(["rk.r98pro"]) && Names(Shape(0x258A, 0x0233, 0xFF02, 2, 1, 20, 20, outputIds: [0x13]), "RK").Length == 1
            && Untaken(Shape(0x258A, 0x2022, 0xFF02, 2, 1, 20, 20, outputIds: [0x13]), "RK")
            && Untaken(Shape(0x258A, 0x01BB, 0xFF02, 2, 1, 64, 64, outputIds: [0x13]), "RK")
            && Untaken(Shape(0x258A, 0x01BB, 0x02FF, 2, 1, 20, 20, outputIds: [0x13]), "RK")
            && Untaken(Shape(0x258A, 0x01BB, 0xFF02, 1, 1, 20, 20, outputIds: [0x13]), "RK"));
        var way = Way(receiver);
        check("RK: the identity query (13 05 01, sum 19 at 19) asked first, then the battery (13 4A 01, 5E at 19), light",
            way.Gate is { } gate && gate.SequenceEqual(Sum(Hex("13 05 01", 20))) && gate[19] == 0x19
            && way.Request.SequenceEqual(Sum(Hex("13 4A 01", 20))) && way.Request[19] == 0x5E && Light(way));

        // Constructed from the documented answer layout 13 <command> 01 00 <length> <data>, byte 19 the sum.
        var identity = Sum(Hex("13 05 01 00 06 0A 00 00 00 00 46", 20));
        var otherKeyboard = Sum(Hex("13 05 01 00 06 0B 00 00 00 00 46", 20));
        var badSum = (byte[])identity.Clone(); badSum[19] ^= 0x01;
        check("RK: only the R98Pro V2's identity (0A 00 00 00 00 46) lets the read go on; another keyboard's, a bad sum or the request read back are not the answer",
            RkKeyboard.IsR98ProV2(identity) == true && RkKeyboard.IsR98ProV2(otherKeyboard) is null && RkKeyboard.IsR98ProV2(badSum) is null
            && RkKeyboard.IsR98ProV2(RkKeyboard.Request(0x05)) is null);
        check("RK: the battery answer's percent at 5 (55 85 %); the request read back, 0, over 100, a bad sum or another length is none",
            RkKeyboard.Level(Sum(Hex("13 4A 01 00 02 55", 20))) == new BatteryRead(85) && RkKeyboard.Level(RkKeyboard.Request(0x4A)) is null
            && RkKeyboard.Level(Sum(Hex("13 4A 01 00 02 00", 20))) is null && RkKeyboard.Level(Sum(Hex("13 4A 01 00 02 65", 20))) is null
            && RkKeyboard.Level(Hex("13 4A 01 00 02 55", 20)) is null && RkKeyboard.Level(Sum(Hex("13 4A 01 00 03 55", 20))) is null);
    }

    private static byte[] Sum(byte[] frame) { int sum = 0; for (int i = 0; i < 19; i++) sum += frame[i]; frame[19] = (byte)sum; return frame; }
}

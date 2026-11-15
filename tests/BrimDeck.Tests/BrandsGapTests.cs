using BrimDeck.Core;

// The models added to brands already read: each new id goes to its documented collection alone, and each reply is parsed as
// its source documents it (the bytes below are the sources' own samples where they give one).
static class BrandsGapTests
{
    private static byte[] Bytes(string hex) => [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => Convert.ToByte(part, 16))];
    // A Compx frame with its checksum (all 17 bytes sum to 0x55).
    private static byte[] Compx(params byte[] head) { var frame = new byte[17]; head.CopyTo(frame, 0); frame[16] = (byte)(0x55 - frame.Take(16).Sum(b => b)); return frame; }
    // A 3554:FA09 frame with its checksum (byte 19 the low byte of the sum of the nineteen before it).
    private static byte[] Fa09(params byte[] head) { var frame = new byte[20]; head.CopyTo(frame, 0); frame[19] = (byte)frame.Take(19).Sum(b => b); return frame; }
    private static HidShape Shape(int vendor, int product, int page, int usage = 1, int input = 64, int output = 64, byte[]? outputIds = null, string name = "")
        => BrandFamilyTests.Shape(vendor, product, page, usage, -1, input, output, 0, outputIds, null, name);

    public static void Run(Action<string, bool> check)
    {
        Compx17(check);
        CompxSplit(check);
        Rapoo(check);
        Mchose(check);
        Fa09Receiver(check);
        Logitech(check);
        Others(check);
    }

    private static void Compx17(Action<string, bool> check)
    {
        int[] added = [0xF5DF, 0xF5DE, 0xF5DC, 0xF505, 0xF506, 0xF5D5, 0xF55E, 0xF523, 0xF510, 0xF50F, 0xF517, 0xF50D];
        check("Compx: the added 3554 pairs, Hitscan's 8K receiver, ATK Zero and 12E9 and Corsair's Sabre v2 Pro take the frame on their 17-byte collection only",
            added.All(product => Dongles.Find(0x3554, product, 0xFF02, "", 0, 1, 0x0002, 17, 17) == Dongles.Protocol.CompxFramed
                && Dongles.Find(0x3554, product, 0xFF02, "", 0, 1, 0x0002, 20, 20) is null && Dongles.Find(0x3554, product, 0xFF01, "", 0, 1, 0x0002, 17, 17) is null)
            && Dongles.Find(0x3770, 0x0300, 0xFF02, "", 0, 1, 0x0002, 17, 17) == Dongles.Protocol.CompxFramed
            && Dongles.Find(0x373B, 0x11D8, 0xFF02, "", 0, 1, 0x0002, 17, 17) == Dongles.Protocol.CompxFramed && Dongles.Find(0x373B, 0x12E9, 0xFF02, "", 0, 1, 0x0002, 17, 17) == Dongles.Protocol.CompxFramed
            && Dongles.Find(0x1B1C, 0x2B2A, 0xFF02, "", 0, 1, 0x0002, 17, 17) == Dongles.Protocol.CompxFramed && Dongles.Find(0x1B1C, 0x2B2A, 0xFF42, "", 0, 1, 0x0001, 64, 64) is null
            && Dongles.Find(0x3554, 0xF57C, 0xFF02, "", 0, 1, 0x0002, 17, 17) is null);
        check("Compx: the Teevolution Terra Pro's receiver is a keyboard", Dongles.CompxKind(0x3554, 0xF523) == DeviceKind.Keyboard && Dongles.CompxKind(0x3554, 0xF505) == DeviceKind.Mouse);
        check("Compx: G-Wolves' receiver keeps the 0x83 exchange on its 65-byte feature collection and takes the frame only where it has none",
            Dongles.Find(0x33E4, 0x3854, 0xFFFF, "", 65, 1, 0x0001, 64, 64) == Dongles.Protocol.F83
            && Dongles.Find(0x33E4, 0x3854, 0xFF02, "", 0, 1, 0x0002, 17, 17) == Dongles.Protocol.CompxFramed
            && Dongles.Find(0x33E4, 0x3817, 0xFF02, "", 0, 1, 0x0002, 17, 17) is null);
    }

    private static void CompxSplit(Action<string, bool> check)
    {
        check("Compx split: iRocks, DAREU A950 and the XM2we are asked on their 0xFF02 feature collection alone (either byte order), the reply collection is not",
            Dongles.Find(0x14A5, 0xC009, 0xFF02, "", 17, 1, 0x0002, 0, 0) == Dongles.Protocol.CompxSplit && Dongles.Find(0x14A5, 0xC009, 0x02FF, "", 17, 1, 0x0002, 0, 0) == Dongles.Protocol.CompxSplit
            && Dongles.Find(0x14A5, 0xC009, 0xFF01, "", 0, 1, 0x0000, 17, 0) is null && Dongles.Find(0x260D, 0x1074, 0xFF02, "", 17, 1, 0x0002, 0, 0) == Dongles.Protocol.CompxSplit
            && Dongles.Find(0x3367, 0x1961, 0xFF02, "", 17, 1, 0x0002, 0, 0) == Dongles.Protocol.CompxSplit && Dongles.Find(0x3367, 0x1961, 0xFF01, "", 17, 1, 0x0000, 17, 0) is null
            && Dongles.Find(0x3367, 0x1234, 0xFF01, "", 0) == Dongles.Protocol.Endgame && Dongles.CompxSplitReply(0xFF01, 0x0000, 17) && !Dongles.CompxSplitReply(0xFF01, 0x0001, 17));
        var dareu = Dongles.CompxSplitFind(0x260D, 0x1074)!;
        var irocks = Dongles.CompxSplitFind(0x14A5, 0xC009)!;
        check("Compx split: DAREU's measured answer reads 90 % (09 04 00 00 00 02 5A .. EC); an empty acknowledgement, the request and a bad checksum are none",
            Bytes("09 04 00 00 00 02 5A 00 00 00 00 00 00 00 00 00 EC").SequenceEqual(Compx(0x09, 0x04, 0x00, 0x00, 0x00, 0x02, 0x5A))
            && Dongles.CompxSplitBattery(dareu, Compx(0x09, 0x04, 0x00, 0x00, 0x00, 0x02, 0x5A)) == new BatteryRead(90)
            && Dongles.CompxSplitBattery(dareu, Compx(0x09, 0x04, 0x01, 0x00, 0x00, 0x02, 0x5A)) is null && Dongles.CompxSplitBattery(dareu, Dongles.CompxRequest(0x04)) is null
            && Dongles.CompxSplitBattery(dareu, Bytes("09 04 00 00 00 02 5A 00 00 00 00 00 00 00 00 00 ED")) is null
            && Dongles.CompxSplitBattery(irocks, Compx(0x08, 0x04, 0x00, 0x00, 0x00, 0x02, 55, 1)) == new BatteryRead(55, true));
        check("Compx split: iRocks's 0x03 answer says online by 1 at 6; another report says nothing",
            Dongles.CompxSplitOnline(Compx(0x09, 0x03, 0x00, 0x00, 0x00, 0x01, 0x01)) == true && Dongles.CompxSplitOnline(Compx(0x09, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00)) == false
            && Dongles.CompxSplitOnline(Compx(0x09, 0x04, 0x00, 0x00, 0x00, 0x02, 0x5A)) is null && irocks.Online && !dareu.Online
            && Dongles.Twins.Contains(("14A5:C009", "14A5:C010")));
    }

    private static void Rapoo(Action<string, bool> check)
    {
        check("Rapoo: any product of its own id on the 19-byte status collection, 0xFF00 as the page (either order) or the usage; listen only",
            Pushes.Find(0x24AE, 0x14C7, 0xFF00, 0x0002, 1, 19) == Pushes.Kind.Rapoo && Pushes.Find(0x24AE, 0x14C7, 0x00FF, 0x0002, 1, 19) == Pushes.Kind.Rapoo
            && Pushes.Find(0x24AE, 0x14C7, 0x0001, 0xFF00, 1, 19) == Pushes.Kind.Rapoo && Pushes.Find(0x24AE, 0x14C7, 0xFF00, 0x0002, 1, 33) is null
            && Pushes.Find(0x24AE, 0x14C7, 0x0001, 0x0002, 1, 19) is null && Pushes.Find(0x24AE, 0x1231, 0xFF00, 0x0002, 1, 19) is null
            && Pushes.Find(0x24AE, 0x14A1, 0xFF00, 0x0002, 1, 20) == Pushes.Kind.Rapoo && Pushes.Find(0x24AF, 0x14C7, 0xFF00, 0x0002, 1, 19) is null);
        check("Rapoo V700DIY-98: its vendor collections, the 0xBD push as captured (84 %, on battery) and charging, and report 7 too",
            Pushes.Find(0x24AE, 0x1924, 0xFF00, 0x0001, 1, 20) == Pushes.Kind.RapooKeyboard && Pushes.Find(0x24AE, 0x4824, 0x000C, 0x0001, 1, 20) is null
            && Pushes.Battery(Pushes.Kind.RapooKeyboard, Bytes("BD 01 00 07 00 46 00 01 54 00")) == new BatteryRead(84)
            && Pushes.Battery(Pushes.Kind.RapooKeyboard, Bytes("BD 01 00 07 00 46 00 02 32 00")) == new BatteryRead(50, true)
            && Pushes.Battery(Pushes.Kind.RapooKeyboard, Bytes("BD 01 00 07 00 46 00 00 54 00")) is null && Pushes.Battery(Pushes.Kind.RapooKeyboard, Bytes("BD 02 00 07 00 46 00 01 54 00")) is null
            && Pushes.KindOf(Pushes.Kind.RapooKeyboard) == DeviceKind.Keyboard && Pushes.Battery(Pushes.Kind.Rapoo, Bytes("BD 01 00 07 00 46 00 01 54 00")) is null);
    }

    private static void Mchose(Action<string, bool> check)
    {
        check("MCHOSE: the headsets' and speakers' kinds, and the keys made before them keep their prefix",
            Dongles.MchoseKind(0x6011, "") == DeviceKind.Headphones && Dongles.MchoseKind(0x6009, "") == DeviceKind.Headphones && Dongles.MchoseKind(0x60C4, "") == DeviceKind.Speaker
            && Dongles.MchoseKind(0x6011, "", legacy: true) is null && Dongles.MchoseKind(0x6008, "", legacy: true) == DeviceKind.Headphones
            && Dongles.MchoseReads(0x6011, "", 0xFF00, 1, 0, 64).SequenceEqual([Dongles.MchoseRead.Audio, Dongles.MchoseRead.Status]));
        check("MCHOSE G7 on its cable (A8A4:2255): the Gmouse query only to a collection named so, as EWEADN's mice share the id",
            Dongles.Find(0xA8A4, 0x2255, 0xFF01, "MCHOSE G7", 0, 1, 0x0010, 65, 65) == Dongles.Protocol.Gmouse
            && Dongles.Find(0xA8A4, 0x2255, 0xFF01, " mchose g7 pro ", 0, 1, 0x0010, 65, 65) == Dongles.Protocol.Gmouse
            && Dongles.Find(0xA8A4, 0x2255, 0xFF01, "MCHOSE G7 MAX", 0, 1, 0x0010, 65, 65) is null && Dongles.Find(0xA8A4, 0x2255, 0xFF01, "X23", 0, 1, 0x0010, 65, 65) is null);
        check("MCHOSE A5 Pro and AX5: the 0x83 exchange on interface 2's 0xFFFF collection, a receiver and its cable twinned",
            Dongles.Find(0x2023, 0xF024, 0xFFFF, "", 65, 2, 0x0001, 0, 0) == Dongles.Protocol.F83 && Dongles.Find(0x2023, 0xF024, 0xFFFF, "", 65, 1, 0x0001, 0, 0) is null
            && Dongles.Find(0x2023, 0xF017, 0xFFFF, "", 65, 2) == Dongles.Protocol.F83 && Dongles.Find(0x2023, 0xF022, 0xFFFF, "", 65, 2) == Dongles.Protocol.F83
            && Dongles.Twins.Contains(("2023:F024", "2023:F026")));
        var v9 = Shape(0x291D, 0x385D, 0xFF90, 1, 64, 64, [0x55], "MCHOSE V9 PRO");
        check("MCHOSE V9 Pro: the status read on C-Media's 291D:385D, only its 0xFF90 usage 1 collection and only where the product names it",
            BrandFamilies.Find(v9) is { Family.Brand: "MCHOSE V9 Pro" } found && found.Ways.Select(way => way.Name).SequenceEqual(["mchose.v9pro"])
            && BrandFamilies.Find(Shape(0x291D, 0x385D, 0x90FF, 1, 64, 64, [0x55], "V9 PRO")) is { Family.Brand: "MCHOSE V9 Pro" }
            && BrandFamilies.Find(Shape(0x291D, 0x385D, 0xFF90, 1, 64, 64, [0x55], "MCHOSE S9 PRO")) is null
            && BrandFamilies.Find(Shape(0x291D, 0x385D, 0xFF00, 1, 64, 64, [0x55], "MCHOSE V9 PRO")) is null
            && BrandFamilies.Find(Shape(0x291D, 0x385D, 0xFF82, 1, 64, 64, [0x55], "MCHOSE V9 PRO")) is null
            && BrandFamilies.Find(Shape(0x291D, 0x385D, 0xFF90, 2, 64, 64, [0x55], "MCHOSE V9 PRO")) is null
            && BrandFamilies.Find(Shape(0x291D, 0x385D, 0x000C, 1, 64, 64, [0x55], "MCHOSE V9 PRO")) is null
            && MchoseV9Pro.Request().Length == 64 && MchoseV9Pro.Request().Take(3).SequenceEqual(new byte[] { 0x55, 0x65, 0x01 }));
        // HaloBattery's 60 % on the cable (00) and rafagfran's 20 % on battery (02); vigil-watt's 100 % (55 65 64 02); M HUB's 03
        // charging, 04 full and 1A asleep (aipno/mchose-linux); 0 the receiver's answer while the headset sleeps (vigil-watt).
        check("MCHOSE V9 Pro: every state: on the cable, on battery, charging, full; asleep (1A) and level 0 asleep, never a level",
            MchoseV9Pro.Battery(Bytes("55 65 3C 00")) == new BatteryRead(60, true) && MchoseV9Pro.Battery(Bytes("55 65 14 02")) == new BatteryRead(20)
            && MchoseV9Pro.Battery(Bytes("55 65 64 02 00 00")) == new BatteryRead(100) && MchoseV9Pro.Battery(Bytes("55 65 55 03")) == new BatteryRead(85, true)
            && MchoseV9Pro.Battery(Bytes("55 65 64 04")) == new BatteryRead(100)
            && MchoseV9Pro.Battery(Bytes("55 65 50 1A")) == new BatteryRead(null, Asleep: true) && MchoseV9Pro.Battery(Bytes("55 65 00 1A")) == new BatteryRead(null, Asleep: true)
            && MchoseV9Pro.Battery(Bytes("55 65 00 02")) == new BatteryRead(null, Asleep: true) && MchoseV9Pro.Battery(Bytes("55 65 00 00")) == new BatteryRead(null, Asleep: true));
        check("MCHOSE V9 Pro: the request read back, a state no source documents, a level out of range and another command are none",
            MchoseV9Pro.Battery(MchoseV9Pro.Request()) is null && MchoseV9Pro.Battery(Bytes("55 65 50 01")) is null && MchoseV9Pro.Battery(Bytes("55 65 50 0A")) is null
            && MchoseV9Pro.Battery(Bytes("55 65 65 02")) is null && MchoseV9Pro.Battery(Bytes("55 11 14 02")) is null && MchoseV9Pro.Battery(Bytes("55 65 14")) is null);
        static byte[] Keyboard(params byte[] head)
        {
            var frame = new byte[20]; head.CopyTo(frame, 0);
            int sum = 0; for (int i = 0; i < 19; i++) sum += frame[i];
            frame[19] = (byte)sum;
            return frame;
        }
        check("MCHOSE 41E4 keyboards: the high nibble of the byte after the level is charging (10 on the cable), the low one (full) is not",
            Dongles.MchoseKeyboardBattery(Keyboard(0x13, 0x4A, 0x01, 0x00, 0x02, 0x34, 0x10)) == new BatteryRead(52, true)
            && Dongles.MchoseKeyboardBattery(Keyboard(0x13, 0x4A, 0x01, 0x00, 0x02, 0x64, 0x01)) == new BatteryRead(100));
    }

    private static void Fa09Receiver(Action<string, bool> check)
    {
        check("3554:FA09: only the 20-byte 0xFF02 collection without a feature report that declares report 0x13",
            Dongles.Find(0x3554, 0xFA09, 0xFF02, "", 0, 1, 0x0002, 20, 20, -1, new HashSet<byte> { 0x13 }) == Dongles.Protocol.Aula
            && Dongles.Find(0x3554, 0xFA09, 0xFF02, "", 0, 1, 0x0002, 20, 20, -1, new HashSet<byte> { 0x08 }) is null
            && Dongles.Find(0x3554, 0xFA09, 0xFF02, "", 0, 1, 0x0002, 64, 64) is null && Dongles.Find(0x3554, 0xFA09, 0xFF02, "", 65, 1, 0x0002, 20, 20) is null);
        check("3554:FA09: the keyboards named by their answer to 13 05 (the K98's and F99 Pro's as measured), a code not known names none",
            Dongles.AulaModel(Fa09(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x02, 0x12, 0x01)) == "LINGBAO K98"
            && Dongles.AulaModel(Fa09(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x01, 0x58, 0x01)) == "AULA F99 Pro"
            && Dongles.AulaModel(Fa09(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x01, 0x15, 0x01)) == "MCHOSE G75 Pro"
            && Dongles.AulaModel(Fa09(0x13, 0x05, 0x01, 0x00, 0x06, 0x03, 0x00, 0x00, 0x00, 0x01, 0x44)) == "KZZI K75 Lite"
            && Dongles.AulaModel(Fa09(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x09, 0x99)) is null
            && Dongles.AulaModel([0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x02, 0x12, 0x01, 0, 0, 0, 0, 0, 0, 0, 0x00]) is null);
        check("3554:FA09: the 0x0A push (the K98's 100 % with 01) and the 0x4A answer, charging by the high nibble",
            Dongles.AulaBattery(Fa09(0x13, 0x0A, 0x01, 0x00, 0x04, 0x05, 0x64, 0x01)) == new BatteryRead(100)
            && Dongles.AulaBattery(Fa09(0x13, 0x0A, 0x01, 0x00, 0x04, 0x05, 0x0A, 0x10)) == new BatteryRead(10, true)
            && Dongles.AulaBattery(Fa09(0x13, 0x4A, 0x01, 0x00, 0x02, 0x55, 0x00)) == new BatteryRead(85)
            && Dongles.AulaBattery(Fa09(0x13, 0x4A, 0x01, 0x00, 0x02, 0x65, 0x00)) is null && Dongles.AulaBattery(Dongles.AulaRequest(0x4A)) is null);
    }

    private static void Logitech(Action<string, bool> check)
    {
        check("Logitech headsets: the 0xFF43 / 0x0202 collection with the long report is HID++'s, either byte order; nothing else",
            Hidpp.HeadsetCollection(0xFF43, 0x0202, 20, new HashSet<byte> { 0x10, 0x11 }) && Hidpp.HeadsetCollection(0x43FF, 0x0202, 20, new HashSet<byte> { 0x11 })
            && !Hidpp.HeadsetCollection(0xFF43, 0x0202, 20, new HashSet<byte> { 0x10 }) && !Hidpp.HeadsetCollection(0xFF43, 0x0202, 7, new HashSet<byte> { 0x11 })
            && !Hidpp.HeadsetCollection(0x000C, 0x0001, 20, new HashSet<byte> { 0x11 }) && !Hidpp.HeadsetCollection(0xFF00, 0x0002, 20, new HashSet<byte> { 0x11 }));
        check("Logitech G535: register 0x0D asked long, the voltage on its curve and 03 charging; another register is none",
            Hidpp.HeadsetRegisterRequest(0xFF, 0x0D).SequenceEqual(new byte[] { 0x11, 0xFF, 0x05, 0x0D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })
            && Hidpp.G535Battery(Bytes("10 FF 05 0D 10 4F 01"), 0xFF) is { Percent: 100, Charging: false, Coarse: true }
            && Hidpp.G535Battery(Bytes("11 FF 05 0D 0E E9 03 00"), 0xFF) is { Percent: 50, Charging: true }
            && Hidpp.G535Battery(Bytes("10 FF 05 0C 0E E9 01"), 0xFF) is null && Hidpp.G535Battery(Bytes("10 FF 05 0D 00 00 01"), 0xFF) is null
            && Hidpp.G535Battery(Bytes("10 01 05 0D 0E E9 01"), 0xFF) is null);
        var centurion = Shape(0x046D, 0x0AF7, 0xFFA0, 1, 64, 64, [0x51]);
        check("Logitech PRO X 2 LIGHTSPEED: the legacy request on its 0xFFA0 collection (the page as measured on Windows); 51 0B with 04 at 8; a power notice with 00 at 6 the headset off, any other notice, the acknowledgement and the request read back none",
            BrandFamilies.Find(centurion) is { Family.Brand: "Logitech Centurion", Ways.Count: 1 }
            && BrandFamilies.Find(Shape(0x046D, 0x0AF8, 0xFFA0, 1, 64, 64, [0x51])) is null && BrandFamilies.Find(Shape(0x046D, 0x0AF7, 0xFF00, 2, 64, 64, [0x51])) is null
            && BrandFamilies.Find(Shape(0x046D, 0x0AF7, 0xA0FF, 1, 64, 64, [0x51])) is null
            && LogitechCenturion.Request().Take(10).SequenceEqual(Bytes("51 08 00 03 1A 00 03 00 04 0A")) && LogitechCenturion.Request().Length == 64
            && LogitechCenturion.Battery(Bytes("51 0B 00 03 1A 00 03 00 04 0A 2D 00 00")) == new BatteryRead(45)
            && LogitechCenturion.Battery(Bytes("51 0B 00 03 1A 00 03 00 04 0A 2B 00 02")) == new BatteryRead(43, true)
            && LogitechCenturion.Battery(Bytes("51 05 00 00 00 00 00 00 00 00 00 00 00")) == new BatteryRead(null, Asleep: true)
            && LogitechCenturion.Battery(Bytes("51 05 00 03 1A 00 03 00 04 0A 2B 00 02")) is null && LogitechCenturion.Battery(Bytes("51 0B 00 03 1A 00 03 00 05 0A 2B 00 02")) is null
            && LogitechCenturion.Battery(Bytes("51 03 00 03 1A 00 00 00 04 0A 2B 00 02")) is null && LogitechCenturion.Battery(LogitechCenturion.Request()) is null);
    }

    private static void Others(Action<string, bool> check)
    {
        var scope = Asus.Find(0x0B05, 0x1A07)!;
        var nova = Asus.Find(0x0B05, 0x1AD3)!;
        var rx = Asus.Find(0x0B05, 0x1B04)!;
        byte[] Answer(byte id, params byte[] head) { var frame = new byte[64]; frame[0] = id; head.CopyTo(frame, 1); return frame; }
        check("ASUS: the new keyboards on any vendor collection with a 64-byte output, the Falchion RX's percent at 11",
            Asus.Collection(scope, 1, -1, 0xFF00, 65) && Asus.Collection(scope, 2, -1, 0x00FF, 65) && !Asus.Collection(scope, 0, -1, 0x0001, 65) && !Asus.Collection(scope, 1, -1, 0x000C, 65)
            && Asus.Find(0x0B05, 0x196B) is { Family: Asus.Family.Keyboard } && Asus.Reply(rx, Answer(0, 0x12, 0x01, 0, 0, 0, 8, 0, 0, 1, 0, 77)) is BatteryRead { Percent: 77, Charging: true });
        var buds = Answer(0xCC, 0x12, 0x07, 0, 0, 5, 77, 60, 20);
        check("ASUS Cetra SpeedNova: the lower bud's level, the parts left, right and case, charging by the low two bits of 5, earbuds",
            Asus.Reply(nova, buds) is BatteryRead { Percent: 60 } && Asus.Parts(nova, buds).SequenceEqual([new(BatteryPart.Left, 77, false), new(BatteryPart.Right, 60, false), new(BatteryPart.Case, 20, false)])
            && Asus.Charging(nova, Answer(0xCC, 0x12, 0x08, 0, 0, 2)) == true && Asus.Charging(nova, Answer(0xCC, 0x12, 0x08, 0, 0, 4)) == false
            && Asus.Kind(nova) == DeviceKind.Earbuds && Asus.Parts(scope, buds).Count == 0 && Asus.Collection(nova, 3, -1, 0x00FF, 64) && !Asus.Collection(nova, 3, -1, 0x000C, 64));
        check("Razer: the Mouse Dock Pro (0x1F) and the Pro Type Ultra's receiver (0x9F)",
            Razer.Find(0x00A4) is { Transaction: 0x1F, Interface: -1 } && Razer.Find(0x027B) is { Transaction: 0x9F, Kind: DeviceKind.Keyboard });
        var kingston = Headsets.Kind.CloudIIKingston;
        check("HyperX Cloud II Wireless (Kingston 0951:1718): its 62-byte exchange on 0xFF13 and the 0x0B answers",
            Headsets.Find(0x0951, 0x1718) == kingston && Headsets.Channel(kingston) == (-1, 0xFF13) && Headsets.Request(kingston).Length == 62
            && Headsets.Request(kingston).Take(17).SequenceEqual(Bytes("06 00 02 00 9A 00 00 68 4A 8E 0A 00 00 00 BB 02 00"))
            && Headsets.ChargingRequest(kingston)![15] == 0x03
            && Headsets.Battery(kingston, Bytes("0B 00 BB 02 00 0F 50 4B")) == new BatteryRead(75) && Headsets.Battery(kingston, Bytes("0B 00 BB 03 00 0F 50 4B")) is null
            && Headsets.Charging(kingston, Bytes("0B 00 BB 03 02")) == true && Headsets.Charging(kingston, Bytes("0B 00 BB 03 00")) == false && Headsets.Charging(kingston, Bytes("0B 00 BB 03 07")) is null);
        check("Corsair: the K65 PLUS, M75 and the Void v2 family on the bragi exchange; the Void v2's measured 81 %",
            Corsair.Find(0x1B1C, 0x2B07) is { Bare: false, Kind: DeviceKind.Keyboard } && Corsair.Find(0x1B1C, 0x2A08) is { Bare: true, Session: true }
            && Corsair.Find(0x1B1C, 0x0A97) is not null && Corsair.Percent(Bytes("01 01 02 00 2A 03")) == 81 && Corsair.Find(0x1B1C, 0x2B2A) is null);
        check("Corsair Void Elite: C9 64 on its 0xFFC5 collection; the level without the microphone bit, 1 and 2 on battery, 4 and 5 charging, 0 no headset (asleep); another status, another report or the request none",
            BrandFamilies.Find(Shape(0x1B1C, 0x0A55, 0xFFC5, 1, 5, 64, [0xC9])) is { Family.Brand: "Corsair Void", Ways.Count: 1 }
            && BrandFamilies.Find(Shape(0x1B1C, 0x0A55, 0xFF42, 1, 5, 64, [0xC9])) is null
            && CorsairVoid.Battery(Bytes("64 00 C8 B1 01")) == new BatteryRead(72) && CorsairVoid.Battery(Bytes("64 00 32 B1 05")) == new BatteryRead(50, true)
            && CorsairVoid.Battery(Bytes("64 00 14 B1 02")) == new BatteryRead(20) && CorsairVoid.Battery(Bytes("64 00 32 B1 04")) == new BatteryRead(50, true)
            && CorsairVoid.Battery(Bytes("64 00 32 B1 00")) == new BatteryRead(null, Asleep: true) && CorsairVoid.Battery(Bytes("64 00 32 B1 03")) is null
            && CorsairVoid.Battery(Bytes("64 00 32 B2 01")) is null && CorsairVoid.Battery(CorsairVoid.Request()) is null);
        check("Ninjutso Sora V2 and V2 8K: the Nordic read on their 0xFFA0 collection",
            Dongles.Find(0x1915, 0xAE8A, 0xFFA0, "", 32) == Dongles.Protocol.Nordic && Dongles.Find(0x1915, 0xAE8C, 0xFFA0, "", 32) == Dongles.Protocol.Nordic
            && Dongles.Find(0x1915, 0xAE8A, 0xFF02, "", 32) is null);
        check("Usage pages: either byte order; vendor pages only",
            UsagePages.Is(0x00FF, 0xFF00) && UsagePages.Is(0xFF00, 0xFF00) && !UsagePages.Is(0xFF01, 0xFF00) && UsagePages.Vendor(0x00FF) && UsagePages.Vendor(0xFFA0)
            && UsagePages.Vendor(0xA0FF) && !UsagePages.Vendor(0x0001) && !UsagePages.Vendor(0x000C) && !UsagePages.Vendor(0x0085));
    }
}

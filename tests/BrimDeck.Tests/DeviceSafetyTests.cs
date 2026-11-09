using BrimDeck.Core;

// What the devices layer must not do to hardware or to the vendors' own software, through the real protocol and matching code:
// the HID++ software id, which devices a request goes to, and which vendor programs make a brand stand aside.
static class DeviceSafetyTests
{
    public static void Run(Action<string, bool> check)
    {
        SoftwareId(check);
        RequestTargets(check);
        VendorPrograms(check);
    }

    // The brands whose query is two steps stand aside while the vendor's software runs (HidBatteries.Scan asks BrandOf for the
    // device's vendor, then whether that brand is among VendorSoftware.Running of the process names). The process lists here are
    // injected; the matching is the real one.
    private static void VendorPrograms(Action<string, bool> check)
    {
        // Exactly Scan's decision for a device of this vendor, given the executable names that are running.
        bool Aside(int vendor, params string[] processes) => VendorSoftware.BrandOf(vendor) is { } brand && VendorSoftware.Running(processes).Contains(brand);

        string[] desktop = ["explorer.exe", "chrome.exe", "BrimDeck.exe", "steam.exe", "Discord.exe", "svchost.exe"];
        check("Vendor software: nothing of the four brands among ordinary processes, so every brand is read",
            !Aside(Razer.Vendor, desktop) && !Aside(Dongles.EndgameVendor, desktop) && !Aside(Dongles.VaxeeVendor, desktop) && !Aside(Dongles.InphicVendor, desktop) && !Aside(Dongles.InphicWireless, desktop));
        check("Vendor software: Razer stands aside for Synapse 3 and 4 and their background parts",
            Aside(Razer.Vendor, [.. desktop, "Razer Synapse.exe"]) && Aside(Razer.Vendor, [.. desktop, "Razer Synapse 4.exe"])
            && Aside(Razer.Vendor, [.. desktop, "RazerCentralService.exe"]) && Aside(Razer.Vendor, [.. desktop, "Razer Synapse Service.exe"]) && Aside(Razer.Vendor, [.. desktop, "RazerAppEngine.exe"])
            && Aside(Razer.Vendor, ["RAZERSYNAPSE.EXE"]));
        check("Vendor software: Endgame Gear, VAXEE and Inphic stand aside for their own programs and not for each other's",
            Aside(Dongles.EndgameVendor, "EndgameGearConfigurator.exe") && !Aside(Dongles.EndgameVendor, "Razer Synapse.exe", "VAXEE Tool.exe")
            && Aside(Dongles.VaxeeVendor, "VAXEE Mouse Config.exe") && !Aside(Dongles.VaxeeVendor, "EndgameGear.exe")
            && Aside(Dongles.InphicVendor, "InphicDriver.exe") && Aside(Dongles.InphicWireless, "英菲克驱动.exe") && !Aside(Dongles.InphicVendor, "Razer Synapse.exe"));
        check("Vendor software: the one-step brands and HID++ never stand aside",
            VendorSoftware.BrandOf(Hidpp.Vendor) is null && VendorSoftware.BrandOf(Dongles.AtkVendor) is null && VendorSoftware.BrandOf(Dongles.VgnVendor) is null
            && VendorSoftware.BrandOf(Dongles.MchoseVendor) is null && VendorSoftware.BrandOf(0x054C) is null);
        // The brands' vendor ids are the ones their readers are chosen by, so no reader of a two-step brand escapes the check.
        check("Vendor software: every two-step reader's vendor is covered",
            Dongles.Find(Dongles.EndgameVendor, 1, 0xFF01, "", 0) is { } && VendorSoftware.BrandOf(Dongles.EndgameVendor) is not null
            && Dongles.Find(Dongles.VaxeeVendor, 1, 0xFF05, "", 0) is { } && VendorSoftware.BrandOf(Dongles.VaxeeVendor) is not null
            && Dongles.Find(Dongles.InphicVendor, 0x5B2E, 0xFF00, "", 33) is { } && VendorSoftware.BrandOf(Dongles.InphicVendor) is not null
            && Dongles.Find(Dongles.InphicWireless, 1, 0xFF00, "", 33) is { } && VendorSoftware.BrandOf(Dongles.InphicWireless) is not null);
    }

    // Which collections a battery request is sent to (Dongles.Find, which HidBatteries asks). A shared vendor id is not enough.
    private static void RequestTargets(Action<string, bool> check)
    {
        int[] pages = [0xFF00, 0xFF01, 0xFF02, 0xFF04, 0xFF05, 0xFF0B, 0xFF1C, 0xFFA0, 0x01, 0x0C];
        // Every vendor and product of the three shared ids, on every page a protocol here uses: none is sent to, but the products
        // verified for a frame (Dongles.CompxFramed, Dongles.NinjutsoProducts), and those only on their own collection.
        bool Verified(int vendor, int product) => Dongles.IsCompxFramed(vendor, product) || vendor == Dongles.NordicVendor && Dongles.NinjutsoProducts.Contains(product);
        bool shared = true;
        foreach (int vendor in new[] { 0x3554, 0x1915, 0x0BDA })
            for (int product = 0; product <= 0xFFFF && shared; product++)
                foreach (int page in pages)
                    if (!Verified(vendor, product) && (Dongles.Find(vendor, product, page, "Wireless Mouse", 64) is not null
                        || Dongles.Find(vendor, product, page, "Wireless Mouse", 65, 1, 0x0002, 17, 17) is not null)) { shared = false; break; }
        check("Requests: Compx's 0x3554, Nordic's 0x1915 and Realtek's 0x0BDA are sent to on no product or page but the verified ones", shared);
        bool own = true;
        foreach (var (vendor, product) in Dongles.CompxFramed.Where(pair => pair.Vendor == 0x3554).Append((0x1915, 0xAE1C)).Append((0x1915, 0xAE11)))
            foreach (int page in pages)
            {
                var found = Dongles.Find(vendor, product, page, "Wireless Mouse", 64, 1, 0x0002, 64, 64);
                if (found is not null && !(vendor == 0x1915 && page == 0xFFA0 && found == Dongles.Protocol.Nordic)) own = false;
            }
        check("Requests: the verified products of the shared ids only on their own collection (the 17-byte 0xFF02 one, Nordic's 0xFFA0)", own
            && Dongles.Find(0x3554, 0xF58A, 0xFF02, "", 0, 1, 0x0002, 17, 17) == Dongles.Protocol.CompxFramed);
        check("Requests: Realtek 0BDA:4762 and a Nordic 1915 receiver, the cases the review named, are not sent to",
            Dongles.Find(0x0BDA, 0x4762, 0xFF01, "MCHOSE Mouse", 64) is null && Dongles.Find(0x0BDA, 0x4762, 0xFF0B, "2.4G Mouse", 64) is null
            && Dongles.Find(0x1915, 0x0001, 0xFFA0, "Crazyradio", 64) is null && Dongles.Find(0x1915, 0x520F, 0xFFA0, "HP Prime", 64) is null);

        // ATK's 0x373B: Compx's frame only to the two receivers atkctl documents, on the page it documents.
        var compx = new List<(int Product, int Page)>();
        for (int product = 0; product <= 0xFFFF; product++)
            foreach (int page in pages)
                if (Dongles.Find(0x373B, product, page, "", 0) == Dongles.Protocol.Compx) compx.Add((product, page));
        check("Requests: the Compx frame goes to 373B:1119 and 373B:111A on page 0xFF04 and nowhere else",
            compx.SequenceEqual(new[] { (0x1119, 0xFF04), (0x111A, 0xFF04) }));
        check("Requests: the Compx page 0xFF02 on ATK's vendor and the other pages of the A9 are not sent to",
            Dongles.Find(0x373B, 0x1119, 0xFF02, "", 0) is null && Dongles.Find(0x373B, 0x111A, 0xFF01, "", 0) is null && Dongles.Find(0x373B, 0x1120, 0xFF04, "", 0) is null);

        // The vendors' own ids keep their readers.
        check("Requests: ATK's BitMouse page, VGN's keyboard, Endgame Gear and VAXEE are still read",
            Dongles.Find(0x373B, 0x1001, 0xFF05, "", 0) == Dongles.Protocol.BitMouse && Dongles.Find(0x320F, 0x5088, 0xFF1C, "", 0) == Dongles.Protocol.Vgn
            && Dongles.Find(0x320F, 0x5089, 0xFF1C, "", 0) is null && Dongles.Find(0x3367, 0x1234, 0xFF01, "", 0) == Dongles.Protocol.Endgame
            && Dongles.Find(0x3057, 0x1234, 0xFF05, "", 0) == Dongles.Protocol.Vaxee);
        check("Requests: Inphic by product on its cable, by vendor on its receiver, both only with the 33-byte report",
            Dongles.Find(0x248A, 0x5B2E, 0xFF00, "", 33) == Dongles.Protocol.Inphic && Dongles.Find(0x248A, 0x5B2E, 0xFF00, "", 8) is null
            && Dongles.Find(0x248A, 0x0001, 0xFF00, "", 33) is null && Dongles.Find(0x249A, 0x0123, 0xFF00, "", 33) == Dongles.Protocol.Inphic
            && Dongles.Find(0x249A, 0x0123, 0x01, "", 33) is null);
        check("Requests: AJAZZ's frame only to the configuration collection of its receiver and cables, and Inphic's frame never to its receiver",
            Dongles.Find(0x249A, 0x5C2F, 0x01, "", 0, iface: 2, usage: 0x00, inputLength: 33, outputLength: 33) == Dongles.Protocol.Ajazz
            && Dongles.Find(0x248A, 0x5C2E, 0x01, "", 0, iface: 2, usage: 0x00, inputLength: 33, outputLength: 33) == Dongles.Protocol.Ajazz
            && Dongles.Find(0x249A, 0x5C2F, 0x01, "", 0, iface: 0, usage: 0x02, inputLength: 8, outputLength: 0) is null
            && Dongles.Find(0x249A, 0x5C2F, 0xFF00, "", 33, iface: 1, usage: 0x01, inputLength: 8, outputLength: 8) is null);
        check("Requests: MCHOSE's mouse frame only to a MCHOSE mouse on its vendor pages",
            Dongles.Find(0x3837, 0x5011, 0xFF01, "MCHOSE G7 Mouse", 64) == Dongles.Protocol.Mchose && Dongles.Find(0x3837, 0x5011, 0xFF02, "MCHOSE G7 Mouse", 64) is null
            && Dongles.Find(0x3837, 0x1012, 0xFF01, "MCHOSE MagDock Mouse", 64) is null && Dongles.Find(0x3837, 0x4001, 0xFF0B, "MCHOSE K99", 64) is null);
    }

    // A receiver sends every reply to every program that has it open, and only the software id tells a program its own reply.
    // The ids other programs use (Solaar's lib/logitech_receiver/base.py): OpenRGB 0x07, LGSTrayEx 0x0A, Solaar 0x0B, G HUB
    // 0x0D, the firmware 0x0F.
    private static void SoftwareId(Action<string, bool> check)
    {
        byte[] taken = [0x07, 0x0A, 0x0B, 0x0D, 0x0F];
        byte ours = Hidpp.SoftwareId;
        check("HID++: the software id is nobody else's, and a nibble", !taken.Contains(ours) && ours is > 0 and <= 0x0F);
        check("HID++: a request carries it in the low nibble of byte 3", (Hidpp.Request(1, 0x08, 1)[3] & 0x0F) == ours && (Hidpp.Request(1, 0x08, 1)[3] >> 4) == 1);

        byte[] Reply(byte id, bool error = false)
        {
            var report = new byte[20];
            report[0] = 0x11; report[1] = 1;
            if (error) { report[2] = 0xFF; report[3] = 0x08; report[4] = (byte)((1 << 4) | id); report[5] = 0x02; }
            else { report[2] = 0x08; report[3] = (byte)((1 << 4) | id); report[4] = 55; }
            return report;
        }
        check("HID++: a reply with our id is the reply, with an error as well", Hidpp.Match(Reply(ours), 1, 0x08, 1).Reply is [55, ..] && Hidpp.Match(Reply(ours, error: true), 1, 0x08, 1).Error == 2);
        // G HUB asking the same device the same function at the same moment: its reply is not ours, and ours is not its.
        check("HID++: G HUB's reply (its id 0x0D) is not taken for ours, nor are the other programs'",
            taken.All(id => Hidpp.Match(Reply(id), 1, 0x08, 1) == (null, null) && Hidpp.Match(Reply(id, error: true), 1, 0x08, 1) == (null, null)));
    }
}

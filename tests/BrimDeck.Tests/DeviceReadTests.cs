using BrimDeck.Core;

// Which read a device is asked (DeviceReadBook): the kept read alone, a limited search otherwise, a check after many misses,
// reads that went wrong never again, and all of it kept across runs and versions. Then the switches, the read runner's pieces
// and the rules that keep one device one entry.
static class DeviceReadTests
{
    public static void Run(Action<string, bool> check)
    {
        const string device = "3837:1014", mouse = "FF01:0001:2:1", other = "FF0B:0104:2:-1";
        string[] ways = ["mchose.realtek", "mchose.frame4d", "mchose.status"];
        var at = DateTimeOffset.Parse("2026-11-14T12:00:00+08:00");

        var book = new DeviceReadBook();
        var first = book.Plan(device, mouse, ways, 0, keptPresent: false);
        check("Device reads: an unknown device is tried with the reads its collection takes, in order, each a first try",
            first.Kind == DeviceReadBook.Kind.Probe && first.Ways.SequenceEqual(ways) && first.Kept is null);
        book.Silent(device, mouse, first.Kind, 0);
        check("Device reads: an unanswered search waits 5 minutes, then 30, then stops until the device comes again",
            book.Plan(device, mouse, ways, 60_000, false).Kind == DeviceReadBook.Kind.None
            && book.Plan(device, mouse, ways, 5 * 60_000, false).Kind == DeviceReadBook.Kind.Probe);
        book.Silent(device, mouse, DeviceReadBook.Kind.Probe, 5 * 60_000);
        bool thirtyLater = book.Plan(device, mouse, ways, 35 * 60_000, false).Kind == DeviceReadBook.Kind.Probe;
        book.Silent(device, mouse, DeviceReadBook.Kind.Probe, 35 * 60_000);
        bool stopped = book.Plan(device, mouse, ways, 10L * 24 * 3600_000, false).Kind == DeviceReadBook.Kind.None;
        book.Arrived(device);
        check("Device reads: after three searches nothing more is sent, and a device plugged in again is searched again",
            thirtyLater && stopped && book.Plan(device, mouse, ways, 10L * 24 * 3600_000, false).Kind == DeviceReadBook.Kind.Probe);

        book.Answered(device, mouse, "mchose.frame4d", at);
        var kept = book.Plan(device, mouse, ways, 0, keptPresent: true);
        check("Device reads: the read that answered is the only one sent, and only to its collection",
            kept.Kind == DeviceReadBook.Kind.Kept && kept.Ways.SequenceEqual(["mchose.frame4d"]) && kept.Kept == "mchose.frame4d"
            && book.Plan(device, other, ways, 0, keptPresent: true).Kind == DeviceReadBook.Kind.None && book.Dirty
            && book.KeptCollection(device) == mouse);
        var copy = book.Entry(device)!;
        copy.Way = "changed"; copy.Bad.Add("x");
        check("Device reads: an entry handed out is a copy, so nothing outside the book changes it under the worker",
            book.Entry(device)?.Way == "mchose.frame4d" && book.Entry(device)!.Bad.Count == 0);

        var saved = DeviceReadBook.Parse(book.Serialize());
        check("Device reads: what was found is kept across runs (and a new version), and the book is clean once written",
            !book.Dirty && saved.Entry(device)?.Way == "mchose.frame4d" && saved.Entry(device)?.Collection == mouse
            && saved.Plan(device, mouse, ways, 0, true).Ways.SequenceEqual(["mchose.frame4d"]));
        check("Device reads: a kept read this version no longer has is searched for afresh",
            saved.Plan(device, mouse, ["mchose.realtek", "mchose.status"], 0, true).Kind == DeviceReadBook.Kind.Probe);
        var elsewhere = saved.Plan(device, other, ways, 0, keptPresent: false);
        check("Device reads: with the kept collection gone (another receiver of the same id), the device is searched where it is, the kept read a first try there too",
            elsewhere.Kind == DeviceReadBook.Kind.Probe && elsewhere.Kept is null);

        for (int i = 0; i < DeviceReadBook.MissesBeforeRecheck - 1; i++) saved.Silent(device, mouse, DeviceReadBook.Kind.Kept, 0);
        bool notYet = saved.Plan(device, mouse, ways, 0, true).Kind == DeviceReadBook.Kind.Kept;
        saved.Silent(device, mouse, DeviceReadBook.Kind.Kept, 0);
        var recheck = saved.Plan(device, mouse, ways, 0, true);
        saved.Silent(device, mouse, recheck.Kind, 0);
        for (int i = 0; i < 3 * DeviceReadBook.MissesBeforeRecheck; i++) saved.Silent(device, mouse, DeviceReadBook.Kind.Kept, 0);
        check("Device reads: after 20 misses in a row the kept read is checked once against the others, and while it stays quiet not again",
            notYet && recheck.Kind == DeviceReadBook.Kind.Recheck && recheck.Ways[0] == "mchose.frame4d" && recheck.Ways.Count == 3 && recheck.Kept == "mchose.frame4d"
            && saved.Plan(device, mouse, ways, 10L * 24 * 3600_000, true).Kind == DeviceReadBook.Kind.Kept);
        saved.Answered(device, mouse, "mchose.frame4d", at);
        for (int i = 0; i < DeviceReadBook.MissesBeforeRecheck; i++) saved.Silent(device, mouse, DeviceReadBook.Kind.Kept, 0);
        check("Device reads: the kept read answering again lets a later silence be checked once more",
            saved.Plan(device, mouse, ways, 0, true).Kind == DeviceReadBook.Kind.Recheck);
        saved.Answered(device, mouse, "mchose.status", at.AddDays(1));
        check("Device reads: another read that answers replaces the kept one (the old one stops being sent)",
            saved.Plan(device, mouse, ways, 0, true).Ways.SequenceEqual(["mchose.status"]));
        var single = new DeviceReadBook();
        single.Answered("373B:1234", mouse, "only", at);
        for (int i = 0; i < 5 * DeviceReadBook.MissesBeforeRecheck; i++) single.Silent("373B:1234", mouse, DeviceReadBook.Kind.Kept, 0);
        check("Device reads: a device with a single read keeps being asked it alone, never searched",
            single.Plan("373B:1234", mouse, ["only"], 0, true) is { Kind: DeviceReadBook.Kind.Kept, Ways: ["only"] });

        bool stops = saved.Harmed(device, "mchose.status");
        var after = saved.Plan(device, mouse, ways, 0, true);
        check("Device reads: a read that went wrong is never sent again, and the kept one it was is forgotten",
            !stops && after.Kind == DeviceReadBook.Kind.Probe && !after.Ways.Contains("mchose.status") && saved.Entry(device)?.Way is null && saved.KeptCollection(device) is null);
        check("Device reads: a second read going wrong stops the device altogether, kept across runs",
            saved.Harmed(device, "mchose.realtek") && saved.Plan(device, mouse, ways, 0, true).Kind == DeviceReadBook.Kind.None
            && DeviceReadBook.Parse(saved.Serialize()).Stopped(device));
        saved.Resume(device);
        check("Device reads: the user turning the reading on again forgets what went wrong",
            !saved.Stopped(device) && saved.Plan(device, mouse, ways, 0, false).Ways.SequenceEqual(ways));

        check("Device reads: a damaged or empty file is an empty book, never an error",
            DeviceReadBook.Parse("{not json").Entry(device) is null && DeviceReadBook.Parse("").Entry(device) is null
            && DeviceReadBook.Parse("[{\"Device\":\"\"},null]").Entry("") is null && DeviceReadBook.Parse("{\"Device\":\"3837:1014\"}").Entry(device) is null);
        var full = new DeviceReadBook();
        for (int i = 0; i < DeviceReadBook.MaximumEntries + 40; i++) full.Answered($"1234:{i:X4}", mouse, "way", at.AddMinutes(i));
        check("Device reads: the book never holds more than its limit, the oldest found let go first",
            DeviceReadBook.Parse(full.Serialize()).Entry("1234:0000") is null && full.Entry($"1234:{DeviceReadBook.MaximumEntries + 39:X4}") is not null);

        Switches(check);
        Exchange(check);
        Merges(check);
    }

    private static void Switches(Action<string, bool> check)
    {
        var choices = new List<DeviceChoice>
        {
            new() { Key = "compx:3554:F5DF", Name = "VXE R1", Kind = DeviceKind.Mouse, Hardware = "3554:F5DF", Read = false },
            new() { Key = "hid:3554:F5DF", Name = "Receiver", Hardware = "3554:F5DF", Read = false },
            new() { Key = "b", Hardware = "046D:C548" }, new() { Key = "c", Read = false }
        };
        DeviceReadSwitches.Use(choices);
        bool off = DeviceReadSwitches.IsOff("3554:F5DF") && !DeviceReadSwitches.IsOff("046D:C548") && !DeviceReadSwitches.IsOff("3837:100B");
        var remembered = DeviceReadSwitches.Remembered("3554:F5DF");
        var set = DeviceReadSwitches.Off;
        DeviceReadSwitches.Use([.. choices.Select(choice => { var copy = choice.Copy(); copy.Name = "renamed"; return copy; })]);
        bool kept = ReferenceEquals(set, DeviceReadSwitches.Off) && DeviceReadSwitches.Remembered("3554:F5DF")?.Name == "renamed";
        DeviceReadSwitches.Use([]);
        off &= kept && !ReferenceEquals(set, DeviceReadSwitches.Off);
        check("Device reads: the devices the user turned off, by their hardware, each remembered under the key it was read under",
            off && DeviceLists.ReadOff(choices).SequenceEqual(["3554:F5DF"]) && !DeviceReadSwitches.IsOff("3554:F5DF")
            && remembered is { Key: "compx:3554:F5DF", Name: "VXE R1", Kind: DeviceKind.Mouse });
        DeviceLists.SetRead(choices, "3554:f5df", true);
        check("Device reads: the switch is the hardware's, every key of it turned on together",
            choices.Where(choice => choice.Hardware == "3554:F5DF").All(choice => choice.Read) && !choices[3].Read);

        var listed = new List<DeviceChoice> { new() { Key = "bt:AABBCCDDEEFF", Hardware = "046D:B023", Read = false }, new() { Key = "razer:1532:00B7", Hardware = "1532:00B7" } };
        var now = DeviceLists.Remember([new DeviceBattery("bt:AABBCCDDEEFF", "MX Master 3", DeviceKind.Mouse, DeviceSource.Gatt, 80) { Hardware = "046D:B023" },
            new DeviceBattery("razer:1532:00B7", "Razer DeathAdder V3 Pro", DeviceKind.Mouse, DeviceSource.Razer, 70) { Hardware = "1532:00B7" }], listed);
        check("Device reads: only the readers the switch stops give a choice its hardware (Windows' Bluetooth levels have no switch)",
            DeviceLists.Find(now, "bt:AABBCCDDEEFF") is { Hardware: null, Read: true } && DeviceLists.Find(now, "razer:1532:00B7")?.Hardware == "1532:00B7"
            && DeviceLists.ReadSwitchable(DeviceSource.Logitech) && !DeviceLists.ReadSwitchable(DeviceSource.Gaming) && !DeviceLists.ReadSwitchable(DeviceSource.Gatt));
    }

    // WayTransport.OutputGetInput's exchange against a receiver that hands out what it holds one report per fetch and then
    // repeats its last.
    private static void Exchange(Action<string, bool> check)
    {
        static (Func<byte[]?> Fetch, Action<byte[]> Push) Receiver(params byte[][] held)
        {
            var queue = new Queue<byte[]>(held);
            byte[]? last = null;
            return (() => { if (queue.Count > 0) last = queue.Dequeue(); return last; }, report => queue.Enqueue(report));
        }
        BatteryRead? Parse(byte[] r) => r[0] == 0x07 && r[1] == 0xAA ? new BatteryRead(r[2]) : null;
        long clock = 0;
        BatteryRead? Run(Func<byte[]?> fetch, Action send) => GetInputExchange.Run(fetch, send, Parse, 1000, 50, ms => clock += ms, () => clock);

        var (fetch, push) = Receiver([0x07, 0xAA, 40], [0x07, 0x00, 0], [0x07, 0x01, 0], [0x07, 0x00, 0]);
        bool sent = false;
        var drained = Run(fetch, () => { sent = true; push([0x07, 0xAA, 70]); });
        check("Device reads: the old reports a receiver holds are fetched and passed over before the request, the answer taken",
            sent && drained == new BatteryRead(70));

        var (same, pushSame) = Receiver([0x07, 0xAA, 70]);
        int sends = 0;
        var unchanged = Run(same, () => { sends++; pushSame([0x07, 0xAA, 70]); });
        check("Device reads: an answer the same as the report before the request (the level unchanged) is taken at the last fetch",
            unchanged == new BatteryRead(70) && sends == 1);

        var (silent, _) = Receiver();
        check("Device reads: a receiver that hands out nothing gives no reading", Run(() => null, () => { }) is null && silent() is null);

        var gated = new BrandWay("test.feature", WayTransport.Feature, [0x05, 0x81], _ => null) { Gate = [0x05, 0x80], Gated = _ => true, FeatureLength = 8 };
        var wide = new BrandWay("test.wide", WayTransport.Feature, [0x05, 0x81], _ => null);
        check("Device reads: a feature read's gate goes the feature way, set and got back, and every get asks the length the read gives",
            gated.Step == WayStep.SetThenGet && gated.GetLength(gated.Gate!, 65) == 8 && gated.GetLength(gated.Request, 65) == 8
            && wide.GetLength(wide.Request, 65) == 65 && wide.GetLength(new byte[70], 65) == 70
            && new BrandWay("t", WayTransport.FeatureGet, [0x06], _ => null).GetLength([0x06], 33) == 33
            && new BrandWay("t", WayTransport.Output, [0x06], _ => null).Step == WayStep.SendThenListen
            && new BrandWay("t", WayTransport.FeatureListen, [0x06], _ => null).Step == WayStep.SendThenListen
            && new BrandWay("t", WayTransport.OutputGetInput, [0x06], _ => null).Step == WayStep.WriteThenFetch
            && new BrandWay("t", WayTransport.Listen, [], _ => null).Step == WayStep.Listen);

        var asked = new BatteryRead(55);
        check("Device reads: a read's charging question marks the level as charging only when it says so",
            BrandWay.WithCharging(asked, true) == new BatteryRead(55, true) && BrandWay.WithCharging(asked, false) == asked && BrandWay.WithCharging(asked, null) == asked
            && new BrandWay("test.way", WayTransport.Output, [0x01], _ => null) is { Then: null, Charging: null });
    }

    private static void Merges(Action<string, bool> check)
    {
        DeviceBattery Audeze(string product, int? percent, bool asleep = false) => new("audeze:3329:" + product, "Audeze Maxwell", DeviceKind.Headphones, DeviceSource.Hid, percent)
            { Hardware = "3329:" + product, Asleep = asleep };
        var dongle = Audeze("4B19", 80);
        check("Device reads: the Audeze Maxwell on its cable and through its dongle is one entry, the cable's",
            Dongles.Shadowed(dongle, [dongle, Audeze("4B1A", 81)]) && Dongles.Shadowed(Audeze("4B18", 80), [Audeze("4B1A", 81)])
            && !Dongles.Shadowed(dongle, [dongle]) && !Dongles.Shadowed(dongle, [dongle, Audeze("4B1A", null, asleep: true)])
            && !Dongles.Shadowed(Audeze("4B1A", 81), [Audeze("4B1A", 81), dongle]));

        check("Corsair: the charge state is asked only of the Virtuoso SE and XT, not of the Void v2 family",
            Corsair.AsksCharging(Corsair.Find(Corsair.Vendor, 0x0A40)!) && Corsair.AsksCharging(Corsair.Find(Corsair.Vendor, 0x0A62)!)
            && !Corsair.AsksCharging(Corsair.Find(Corsair.Vendor, 0x2A08)!) && !Corsair.AsksCharging(Corsair.Find(Corsair.Vendor, 0x0A97)!)
            && !Corsair.AsksCharging(Corsair.Find(Corsair.Vendor, 0x1B7F)!));

        static byte[] Compx(params byte[] head)
        {
            var frame = new byte[17]; head.CopyTo(frame, 0);
            int sum = 0; for (int i = 0; i < 16; i++) sum += frame[i];
            frame[16] = (byte)(0x55 - sum);
            return frame;
        }
        var dareu = Dongles.CompxSplitFind(0x260D, 0x1074)!;
        var endgame = Dongles.CompxSplitFind(0x3367, 0x1961)!;
        check("Compx split: DAREU's answer is checked as its sources check it (data, not an acknowledgement), the Endgame's lengths too",
            Dongles.CompxSplitBattery(dareu, Compx(0x09, 0x04, 0x00, 0x00, 0x00, 0x0B, 0x5A)) == new BatteryRead(90)
            && Dongles.CompxSplitBattery(dareu, Compx(0x09, 0x04, 0x01, 0x00, 0x00, 0x02, 0x5A)) is null
            && Dongles.CompxSplitBattery(endgame, Compx(0x09, 0x04, 0x00, 0x00, 0x00, 0x0B, 0x5A)) is null
            && Dongles.CompxSplitBattery(endgame, Compx(0x09, 0x04, 0x00, 0x00, 0x00, 0x02, 0x5A)) == new BatteryRead(90));
    }
}

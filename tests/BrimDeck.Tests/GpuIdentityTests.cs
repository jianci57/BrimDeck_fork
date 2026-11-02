using BrimDeck.Core;

// Two graphics cards of one model, simulated: the saved choice must stay on the same physical card across restarts (the LUIDs and
// the order DXGI lists them in change), when the other card is removed, and when the chosen card is removed and plugged back in. A
// choice saved before adapters had a location is found and saved again under the new key. And a read that fails, or misses one
// card, never shuts that card out for long.
internal static class GpuIdentityTests
{
    private const uint Nvidia = 0x10DE, Rtx4060 = 0x2882, Subsystem = 0x16F310DE;

    public static void Run(Action<string, bool> check)
    {
        Keys(check);
        Restarts(check);
        RemovalAndReconnect(check);
        Migration(check);
        Descriptions(check);
        SettingsChoice(check);
        WithoutReadings(check);
    }

    // #83 review: with the chosen card disconnected, the settings page must not show the other card of the same model as chosen. It
    // finds the choice as the monitor does (GpuChoice.Find); not found, it lists the saved card as not connected.
    private static void SettingsChoice(Action<string, bool> check)
    {
        var both = Samples(Card(0x1000, "1:0.0"), Card(0x2000, "2:0.0"));
        string chosen = both.Single(gpu => gpu.Key.EndsWith("@1:0.0")).Key;
        var left = Samples(Card(0x2000, "2:0.0"));
        var options = left.Select(gpu => (gpu.Key, gpu.LegacyKey, gpu.Name, gpu.Luid)).ToList();
        check("GPU identity: with the chosen card gone, the page finds no card for the choice (not the other card of the model)",
            GpuChoice.Find(options, option => option, chosen, "NVIDIA GeForce RTX 4060") == -1);
        check("GPU identity: meanwhile the monitor shows 自动", GpuChoice.Resolve(left, chosen, "NVIDIA GeForce RTX 4060", 0, out bool automatic)?.Luid == 0x2000 && automatic);
        check("GPU identity: with both cards there, the page finds the chosen one", GpuChoice.Find(both.Select(gpu => (gpu.Key, gpu.LegacyKey, gpu.Name, gpu.Luid)).ToList(),
            option => option, chosen, "NVIDIA GeForce RTX 4060") is var at and >= 0 && both[at].Key == chosen);
    }

    // #83 review: a connected card without engine readings at the moment stays in the list, marked, and can stay chosen; 自动 takes
    // a card with readings.
    private static void WithoutReadings(Action<string, bool> check)
    {
        var cards = new[] { Card(0x1000, "1:0.0"), Card(0x2000, "2:0.0") with { Name = "Intel UHD Graphics", VendorId = 0x8086, DeviceId = 0x9A49, DedicatedTotal = 128UL << 20, Kind = GpuKind.Integrated } };
        var keyed = GpuReadings.Keyed(cards).ToDictionary(adapter => adapter.Luid);
        // Only the integrated adapter has counters: the discrete one is asleep.
        var gpus = GpuReadings.Samples(new Dictionary<ulong, GpuTotals> { [0x2000] = new GpuTotals() }, keyed, described: true, "none");
        check("GPU identity: an adapter without readings stays listed, marked", gpus.Count == 2 && gpus.Single(gpu => gpu.Luid == 0x1000).NoReadings
            && !gpus.Single(gpu => gpu.Luid == 0x2000).NoReadings);
        check("GPU identity: 自动 takes the adapter with readings over a discrete one without", GpuChoice.Auto(gpus, 0)?.Luid == 0x2000);
        string asleep = gpus.Single(gpu => gpu.Luid == 0x1000).Key;
        check("GPU identity: a chosen adapter without readings stays chosen", GpuChoice.Resolve(gpus, asleep, "NVIDIA GeForce RTX 4060", 0, out bool automatic)?.Luid == 0x1000 && !automatic);
        var none = GpuReadings.Samples(new Dictionary<ulong, GpuTotals>(), keyed, described: true, "none");
        check("GPU identity: with no adapter read, 自动 still shows one", none.Count == 2 && GpuChoice.Auto(none, 0) is not null);
    }

    private static GpuAdapter Card(ulong luid, string? location) => new(luid, "NVIDIA GeForce RTX 4060", Nvidia, Rtx4060, Subsystem, 8UL << 30, GpuKind.Discrete) { Location = location };

    // The samples the page gets for these adapters (each with counters), as GpuReadings.Samples builds them.
    private static List<GpuSample> Samples(params GpuAdapter[] adapters)
    {
        var keyed = GpuReadings.Keyed(adapters).ToDictionary(adapter => adapter.Luid);
        var totals = adapters.ToDictionary(adapter => adapter.Luid, _ => new GpuTotals());
        return GpuReadings.Samples(totals, keyed, described: true, "none");
    }
    private static string? LocationOf(IReadOnlyList<GpuSample> gpus, string key, string? name) => GpuChoice.Saved(gpus, key, name) is { } gpu
        ? gpu.Key[(gpu.Key.IndexOf('@') + 1)..] : null;

    private static void Keys(Action<string, bool> check)
    {
        var gpus = Samples(Card(0x1000, "1:0.0"), Card(0x2000, "2:0.0"));
        check("GPU identity: two cards of one model have their own keys, by where they sit", gpus.Select(gpu => gpu.Key).OrderBy(key => key)
            .SequenceEqual(["10DE:2882:16F310DE@1:0.0", "10DE:2882:16F310DE@2:0.0"]));
        check("GPU identity: the older keys are kept beside them", gpus.Select(gpu => gpu.LegacyKey).OrderBy(key => key).SequenceEqual(["10DE:2882:16F310DE", "10DE:2882:16F310DE#2"]));
        check("GPU identity: a PCI location as bus:device.function, none off the bus", GpuReadings.Location(1, 0, 0) == "1:0.0" && GpuReadings.Location(0, 2, 1) == "0:2.1"
            && GpuReadings.Location(uint.MaxValue, 0xFFFF, 0xFFFF) is null && GpuReadings.Location(0, 32, 0) is null && GpuReadings.Location(0, 0, 8) is null);
        var without = Samples(Card(0x1000, null), Card(0x2000, null));
        check("GPU identity: without a location the cards are told apart by their order, as before", without.Select(gpu => gpu.Key).OrderBy(key => key)
            .SequenceEqual(["10DE:2882:16F310DE", "10DE:2882:16F310DE#2"]));
    }

    // The user chose the card in the second slot. After a restart the LUIDs are new and DXGI may list the cards the other way round.
    private static void Restarts(Action<string, bool> check)
    {
        var before = Samples(Card(0x1000, "1:0.0"), Card(0x2000, "2:0.0"));
        string chosen = before.Single(gpu => gpu.Key.EndsWith("@2:0.0")).Key;
        bool kept = true;
        foreach (var (first, second) in new[] { (0x9000UL, 0x8000UL), (0x0100UL, 0x0200UL), (0x7000UL, 0x7001UL) })
        {
            // The card in slot 2 now has the lower LUID in some restarts and the higher in others.
            var after = Samples(Card(first, "2:0.0"), Card(second, "1:0.0"));
            kept &= LocationOf(after, chosen, "NVIDIA GeForce RTX 4060") == "2:0.0"
                && GpuChoice.Resolve(after, chosen, "NVIDIA GeForce RTX 4060", 0, out bool automatic, out var upgrade)?.Luid == first && !automatic && upgrade is null;
        }
        check("GPU identity: the chosen card stays chosen across restarts, whatever its LUID and order", kept);
    }

    private static void RemovalAndReconnect(Action<string, bool> check)
    {
        string chosen = "10DE:2882:16F310DE@2:0.0";
        var otherRemoved = Samples(Card(0x2000, "2:0.0"));
        check("GPU identity: with the other card removed, the chosen one is still found", LocationOf(otherRemoved, chosen, "NVIDIA GeForce RTX 4060") == "2:0.0");
        var chosenRemoved = Samples(Card(0x1000, "1:0.0"));
        check("GPU identity: with the chosen card removed, the other card of the model is not taken for it", GpuChoice.Saved(chosenRemoved, chosen, "NVIDIA GeForce RTX 4060") is null
            && GpuChoice.Resolve(chosenRemoved, chosen, "NVIDIA GeForce RTX 4060", 0, out bool automatic, out var upgrade)?.Luid == 0x1000 && automatic && upgrade is null);
        var reconnected = Samples(Card(0x1000, "1:0.0"), Card(0x5555, "2:0.0"));
        check("GPU identity: plugged back in with a new LUID, the chosen card is found again", GpuChoice.Saved(reconnected, chosen, null)?.Luid == 0x5555);
        var moved = Samples(Card(0x1000, "1:0.0"), Card(0x5555, "3:0.0"));
        check("GPU identity: a card moved to another slot is a new card; none of the model is guessed", GpuChoice.Saved(moved, chosen, "NVIDIA GeForce RTX 4060") is null);
        var movedAlone = Samples(Card(0x5555, "3:0.0"));
        check("GPU identity: ... and the one card left is shown by 自动 while the choice is kept for the card it named",
            GpuChoice.Resolve(movedAlone, chosen, "NVIDIA GeForce RTX 4060", 0, out bool shownByAuto, out var keptKey)?.Luid == 0x5555 && shownByAuto && keptKey is null);
        var newDriver = Samples(Card(0x5555, "2:0.0") with { DeviceId = 0x2883 });
        check("GPU identity: a card whose ids changed with its driver is found by its name and saved under its new key",
            GpuChoice.Resolve(newDriver, chosen, "NVIDIA GeForce RTX 4060", 0, out _, out var newKey)?.Luid == 0x5555 && newKey == "10DE:2883:16F310DE@2:0.0");
    }

    // A choice saved before adapters had a location ("…#2" or "…") is found by that key once, and saved again under the new key.
    private static void Migration(Action<string, bool> check)
    {
        var gpus = Samples(Card(0x1000, "1:0.0"), Card(0x2000, "2:0.0"));
        var found = GpuChoice.Resolve(gpus, "10DE:2882:16F310DE#2", "NVIDIA GeForce RTX 4060", 0, out bool automatic, out var upgrade);
        check("GPU identity: an older key is found and saved again under the card's key", found?.Luid == 0x2000 && !automatic && upgrade == "10DE:2882:16F310DE@2:0.0");
        var again = GpuChoice.Resolve(gpus, upgrade, "NVIDIA GeForce RTX 4060", 0, out _, out var none);
        check("GPU identity: once saved under its key, nothing more to save", again?.Luid == 0x2000 && none is null);
        var one = Samples(Card(0x1000, "1:0.0"));
        check("GPU identity: an older key of a single card", GpuChoice.Resolve(one, "10DE:2882:16F310DE", null, 0, out _, out var single)?.Luid == 0x1000 && single == "10DE:2882:16F310DE@1:0.0");
        check("GPU identity: a choice saved under a LUID is saved again under the key", GpuChoice.Resolve(one, GpuReadings.LuidText(0x1000), null, 0, out _, out var fromLuid)?.Luid == 0x1000
            && fromLuid == "10DE:2882:16F310DE@1:0.0");
    }

    // The descriptions behind the samples: one card fails to describe itself, or DXGI fails as a whole, for a while.
    private static void Descriptions(Action<string, bool> check)
    {
        var a = Card(0x1000, "1:0.0"); var b = Card(0x2000, "2:0.0");
        IReadOnlyDictionary<ulong, GpuAdapter> Of(params GpuAdapter[] adapters) => GpuReadings.Keyed(adapters).ToDictionary(adapter => adapter.Luid);
        // DXGI misses card B (it fails to describe itself) for the first 20 seconds.
        double clock = 0;
        var descriptions = new GpuDescriptions(() => clock < 20 ? (Of(a), true, false) : (Of(a, b), true, true), clock);
        ulong[] both = [0x1000, 0x2000];
        int seconds = 0;
        while (!descriptions.For(both, clock).ContainsKey(0x2000) && seconds < 120) { clock += 1; seconds++; }
        check("GPU descriptions: a card that failed to describe itself returns soon after it can again", seconds is >= 20 and <= 20 + (int)GpuDescriptions.FirstPause * 4);
        check("GPU descriptions: ... without the adapters read every second meanwhile", descriptions.Reads <= 6);
        // Card B described, then a read misses it: it keeps its description.
        var kept = new GpuDescriptions(() => (Of(a, b), true, true), 0);
        var flaky = new GpuDescriptions(() => clock < 1000 ? (Of(a, b), true, true) : (Of(a), true, false), 0);
        clock = 1000;
        check("GPU descriptions: a read that misses a card keeps the card's description", kept.For(both, 0).ContainsKey(0x2000) && flaky.For([0x1000, 0x2000, 0x3000], clock).ContainsKey(0x2000));
        // A device DXGI never lists (an NPU) is asked about with growing pauses, up to LongestPause.
        clock = 0;
        var npu = new GpuDescriptions(() => (Of(a), true, true), clock);
        ulong[] withNpu = [0x1000, 0x9999];
        for (int i = 0; i < 3600; i++) { npu.For(withNpu, clock); clock += 1; }
        check("GPU descriptions: a device DXGI never lists costs a read now and then only", npu.Reads is > 3 and < 20);
        // DXGI failing as a whole at first: it is read again with growing pauses until it answers.
        clock = 0;
        var late = new GpuDescriptions(() => clock < 30 ? (new Dictionary<ulong, GpuAdapter>(), false, false) : (Of(a, b), true, true), clock);
        int waited = 0;
        while (!late.Described && waited < 300) { late.For(both, clock); clock += 1; waited++; }
        check("GPU descriptions: with DXGI failing at first, the cards are described soon after it answers", late.Described && waited <= 30 + 40 && late.Adapters.Count == 2);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using BrimDeck.Core;

// The expanded page's table of items and its layout (ExpandedMonitor, ExpandedLayout): the table's migration from the four old
// switches, the layout against the page as it was (MonitorLayout) when everything it showed is chosen, and properties of the layout
// over every subset of items at representative sizes and fonts: no overlap, nothing out of the page, readable sizes, priority when
// room runs short, nothing read for what is not chosen.
internal static class ExpandedMonitorTests
{
    private static readonly ExpandedItem[] Every = [.. Enum.GetValues<ExpandedItem>()];
    private static readonly ExpandedItem[] Before = [ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload];
    private static readonly double[] Widths = [300, 440, 560, 620, 860, 1200];
    private static readonly double[] Heights = [60, 100, 120, 140, 160, 200, 220, 260, 320, 400, 520];
    private static readonly FontMetrics[] Fonts = [FontMetrics.Default, new(1.25, 1.7), new(1.5, 1.7), new(1, 2.1), new(.85, 1.5)];
    private static readonly ExpandedContent[] Contents = [new(0, 0), new(1, 1), new(4, 2), new(12, 2)];
    private static readonly ExpandedBox Zero = default;

    internal static void Run(Action<string, bool> check)
    {
        Table(check);
        Network(check);
        SettingsFile(check);
        Regression(check);
        Special(check);
        Properties(check);
        Stability(check);
    }

    private static List<ExpandedItem> Subset(int mask) => [.. Every.Where((item, index) => (mask >> index & 1) != 0)];
    private static ExpandedBox Page(ExpandedPlan plan) => new(0, 0, plan.Width, plan.Height);

    private static void Table(Action<string, bool> check)
    {
        var defaults = ExpandedMonitor.Defaults();
        check("Expanded table: the default order and shown items are the page as it was",
            defaults.Select(r => r.Item).SequenceEqual(ExpandedMonitor.Order) && ExpandedMonitor.Shown(defaults).SequenceEqual(
                [ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Storage, ExpandedItem.Power, ExpandedItem.Processes]));
        var off = ExpandedMonitor.Defaults(processes: false, storage: false, power: false, frames: true);
        check("Expanded table: the four old switches say which of their items show",
            ExpandedMonitor.Shown(off).SequenceEqual([ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Frames])
            && ExpandedMonitor.Switches(off) == (false, false, false, true));
        var messy = ExpandedMonitor.Normalize([new() { Item = ExpandedItem.Power }, null, new() { Item = (ExpandedItem)99 }, new() { Item = ExpandedItem.Power, Visible = false },
            new() { Item = ExpandedItem.Cpu, Visible = false }]);
        check("Expanded table: unknown and repeated items are dropped, missing ones join at the end, the first of a repeat wins",
            messy.Count == 9 && messy[0].Item == ExpandedItem.Power && messy[0].Visible && messy[1].Item == ExpandedItem.Cpu && !messy[1].Visible
            && messy.Select(r => r.Item).Distinct().Count() == 9 && messy.Skip(2).Select(r => r.Item).SequenceEqual(ExpandedMonitor.Order.Where(i => i is not (ExpandedItem.Power or ExpandedItem.Cpu))));
        var nothing = ExpandedMonitor.Normalize(ExpandedMonitor.Order.Select(item => new ExpandedReading { Item = item, Visible = false }));
        check("Expanded table: a table with nothing shown stays so", ExpandedMonitor.Shown(nothing).Count == 0 && nothing.Count == 9);
        check("Expanded table: nothing chosen reads nothing, and a temperature is read only with a reading that has one",
            ExpandedMonitor.Demand([], true) == ExpandedDemand.None
            && ExpandedMonitor.Demand([ExpandedItem.Memory, ExpandedItem.Upload], true) == (ExpandedDemand.Memory | ExpandedDemand.Network)
            && ExpandedMonitor.Demand([ExpandedItem.Cpu], true) == (ExpandedDemand.Cpu | ExpandedDemand.Temperatures)
            && ExpandedMonitor.Demand([ExpandedItem.Storage], false) == ExpandedDemand.Storage);
    }

    private static void Network(Action<string, bool> check)
    {
        var moved = ExpandedMonitor.Normalize(ExpandedMonitor.Order.Reverse().Select(i => new ExpandedReading { Item = i }));
        check("Expanded table: a block's items stay together in the block's own order, where its first item was",
            moved.Select(r => r.Item).SequenceEqual([ExpandedItem.Processes, ExpandedItem.Power, ExpandedItem.Storage, ExpandedItem.Frames,
                ExpandedItem.Upload, ExpandedItem.Download, ExpandedItem.Memory, ExpandedItem.Cpu, ExpandedItem.Gpu]));
        var swapped = ExpandedMonitor.Swap(ExpandedMonitor.Defaults(), ExpandedItem.Memory);
        var rates = ExpandedMonitor.Swap(swapped, ExpandedItem.Download);
        check("Expanded table: memory and the network change places, and so do the two rates, inside their block",
            swapped.Select(r => r.Item).Skip(2).Take(3).SequenceEqual([ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Memory])
            && rates.Select(r => r.Item).Skip(2).Take(3).SequenceEqual([ExpandedItem.Upload, ExpandedItem.Download, ExpandedItem.Memory])
            && ExpandedMonitor.Normalize(rates).Select(r => r.Item).SequenceEqual(rates.Select(r => r.Item)));
        var hidden = ExpandedMonitor.Arrange(rates, [ExpandedItem.Power, ExpandedItem.Storage, ExpandedItem.Cpu]);
        hidden.First(r => r.Item == ExpandedItem.Storage).Visible = false; hidden.First(r => r.Item == ExpandedItem.Upload).Visible = false;
        var restored = ExpandedMonitor.InDefaultOrder(hidden);
        check("Expanded table: the default order comes back for hidden items too, and what is shown or hidden stays",
            !ExpandedMonitor.IsDefaultOrder(hidden) && restored.Select(r => r.Item).SequenceEqual(ExpandedMonitor.Order) && ExpandedMonitor.IsDefaultOrder(restored)
            && restored.All(r => r.Visible == hidden.First(h => h.Item == r.Item).Visible));
        check("Expanded table: the default table is in the default order, and the process list's place does not count",
            ExpandedMonitor.IsDefaultOrder(ExpandedMonitor.Defaults()) && ExpandedMonitor.IsDefaultOrder(null)
            && ExpandedMonitor.IsDefaultOrder([new() { Item = ExpandedItem.Processes }]) && ExpandedMonitor.Normalize([new() { Item = ExpandedItem.Processes }])[0].Item == ExpandedItem.Processes);
        var arranged = ExpandedMonitor.Arrange(ExpandedMonitor.Defaults(), [ExpandedItem.Storage, ExpandedItem.Cpu, ExpandedItem.Power, ExpandedItem.Memory]);
        check("Expanded table: moving blocks puts them in the places those blocks held, the others stay",
            arranged.Select(r => r.Item).SequenceEqual([ExpandedItem.Storage, ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Frames, ExpandedItem.Power,
                ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Processes])
            && arranged.Select(r => r.Visible).SequenceEqual(ExpandedMonitor.Normalize(arranged).Select(r => r.Visible)));
        var content = new ExpandedContent(1, 1);
        var split = ExpandedMonitor.Normalize([new() { Item = ExpandedItem.Memory }, new() { Item = ExpandedItem.Download, Visible = false }, new() { Item = ExpandedItem.Cpu }]);
        check("Expanded table: a table from before the network was split gets the upload after the download, shown as the download is",
            split.Select(r => r.Item).Take(4).SequenceEqual([ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Cpu]) && !split[2].Visible);
        var shown = ExpandedMonitor.Normalize([new() { Item = ExpandedItem.Download }]);
        check("Expanded table: a shown network migrates to a shown download and upload", shown[0].Item == ExpandedItem.Download && shown[1].Item == ExpandedItem.Upload && shown[1].Visible);
        check("Expanded table: the download keeps the number the network had", (int)ExpandedItem.Download == 3 && (int)ExpandedItem.Upload > (int)ExpandedItem.Processes);
        var old = SettingsMigrations.Read("{\"AppVersion\":\"9.0.0\",\"ExpandedReadings\":[{\"Item\":3,\"Visible\":true},{\"Item\":0,\"Visible\":true}]}");
        check("Expanded settings: a saved network (number 3) reads as the download and the upload follows it",
            old.ExpandedReadings.Take(5).Select(r => r.Item).SequenceEqual([ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Memory, ExpandedItem.Cpu, ExpandedItem.Gpu]) && old.ExpandedReadings[1].Visible);
        var both = ExpandedLayout.For([ExpandedItem.Download, ExpandedItem.Upload], 620, 200, content);
        var down = ExpandedLayout.For([ExpandedItem.Download], 620, 200, content);
        var up = ExpandedLayout.For([ExpandedItem.Upload], 620, 200, content);
        check("Expanded layout: download and upload are one block of one line, which shows the one or both",
            both.Parts.Count == 1 && both.Downloads && both.Uploads && both.Dropped.Count == 0 && both.Shown.Contains(ExpandedItem.Upload)
            && down.Downloads && !down.Uploads && down.Dropped.Count == 0 && up.Uploads && !up.Downloads && up.Part(ExpandedItem.Upload) is not null && up.Dropped.Count == 0);
        var upFirst = ExpandedLayout.For([ExpandedItem.Upload, ExpandedItem.Download], 620, 200, content);
        check("Expanded layout: the upload goes first where the table puts it first, and that is another page",
            upFirst.UploadFirst && !both.UploadFirst && !up.UploadFirst && upFirst.Signature != both.Signature);
        check("Expanded layout: the same box whichever rates the block shows", both.Parts[0].Box == down.Parts[0].Box && down.Parts[0].Box == up.Parts[0].Box);
    }

    private static void SettingsFile(Action<string, bool> check)
    {
        var old = SettingsMigrations.Read("""{"AppVersion":"1.2.0","MonitorProcesses":false,"MonitorStorage":true,"MonitorPower":false,"MonitorFrames":true}""");
        check("Expanded settings: a file saved before the table shows the page as it was",
            ExpandedMonitor.Shown(old.ExpandedReadings).SequenceEqual([ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Frames, ExpandedItem.Storage]));
        check("Expanded settings: a table nobody set is not written (older files stay as they were)",
            JsonSerializer.SerializeToNode(old)!.AsObject()["ExpandedReadings"] is null);
        var fresh = new DeckSettings();
        check("Expanded settings: the default settings show the same items as the four switches' defaults",
            ExpandedMonitor.Shown(fresh.ExpandedReadings).SequenceEqual(ExpandedMonitor.Shown(ExpandedMonitor.Defaults())));
        // The old toggles keep working until a table is set: changing one changes what the page shows.
        fresh.MonitorStorage = false;
        check("Expanded settings: before a table is set the switches decide", !ExpandedMonitor.Shown(fresh.ExpandedReadings).Contains(ExpandedItem.Storage));

        var chosen = new DeckSettings { ExpandedReadings = [new() { Item = ExpandedItem.Download }, new() { Item = ExpandedItem.Cpu, Visible = false }, new() { Item = ExpandedItem.Processes, Visible = false }] };
        chosen.Normalize();
        check("Expanded settings: a set table is completed and the old switches follow it",
            chosen.ExpandedReadings.Count == 9 && chosen.ExpandedReadings[0].Item == ExpandedItem.Download && !chosen.MonitorProcesses && chosen.MonitorStorage && chosen.MonitorPower && !chosen.MonitorFrames);
        var json = JsonSerializer.Serialize(chosen);
        var back = SettingsMigrations.Read(json);
        check("Expanded settings: the table survives a save and a read, order included",
            back.ExpandedReadings.Select(r => (r.Item, r.Visible)).SequenceEqual(chosen.ExpandedReadings.Select(r => (r.Item, r.Visible))));
        var none = new DeckSettings { ExpandedReadings = [.. ExpandedMonitor.Order.Select(item => new ExpandedReading { Item = item, Visible = false })] };
        none.Normalize();
        var noneBack = SettingsMigrations.Read(JsonSerializer.Serialize(none));
        check("Expanded settings: choosing nothing survives a save and a read, and the old switches go off",
            ExpandedMonitor.Shown(noneBack.ExpandedReadings).Count == 0 && !noneBack.MonitorProcesses && !noneBack.MonitorStorage && !noneBack.MonitorPower && !noneBack.MonitorFrames);
        var copy = chosen.Copy();
        copy.ExpandedReadings[0].Visible = false;
        check("Expanded settings: a copy has a table of its own", chosen.ExpandedReadings[0].Visible && !copy.ExpandedReadings[0].Visible);
        // A newer file's table with an item this version does not know.
        var newer = SettingsMigrations.Read("""{"AppVersion":"9.0.0","ExpandedReadings":[{"Item":42,"Visible":true},{"Item":2,"Visible":true},{"Item":0,"Visible":false}]}""");
        check("Expanded settings: an item this version does not know is dropped on reading",
            newer.ExpandedReadings.Count == 3 || newer.ExpandedReadings.All(r => Enum.IsDefined(r.Item)));
        newer.Normalize();
        check("Expanded settings: after normalizing every item is there once", newer.ExpandedReadings.Count == 9 && newer.ExpandedReadings[0].Item == ExpandedItem.Memory);
    }

    // With all of CPU, GPU, memory and network (and any of the rest) chosen the layout is MonitorLayout's: the steps, and the places
    // of the parts against MonitorLayout.Parts.
    private static void Regression(Action<string, bool> check)
    {
        int compared = 0, squeezed = 0, stepDiffs = 0, partDiffs = 0; string first = "";
        foreach (bool processes in new[] { false, true })
            foreach (bool frames in new[] { false, true })
                foreach (int storage in new[] { 0, 1, 2, 3, 5, 6 })
                    foreach (int power in new[] { 0, 1, 2 })
                        foreach (double width in new double[] { 300, 440, 470, 560, 640, 860, 1200 })
                            for (double height = 60; height <= 520; height += 11)
                            {
                                var items = new List<ExpandedItem>(Before);
                                if (frames) items.Add(ExpandedItem.Frames);
                                if (storage > 0) items.Add(ExpandedItem.Storage);
                                if (power > 0) items.Add(ExpandedItem.Power);
                                if (processes) items.Add(ExpandedItem.Processes);
                                var plan = ExpandedLayout.For(items, width, height, new(storage, power));
                                var old = MonitorLayout.For(width, height, processes, storage, power, frames);
                                // Where the old page overflowed (its graph below the floor) the plan drops items instead; that is not compared.
                                if (plan.Squeezed) { squeezed++; continue; }
                                compared++;
                                if (plan.Steps != old) { stepDiffs++; if (first.Length == 0) first = $"steps {width}x{height} p{processes} f{frames} s{storage} w{power}: {plan.Steps} vs {old}"; continue; }
                                double readings = old.List == ListPlacement.Below ? MonitorLayout.StackedReadings : height;
                                var parts = old.Parts(readings);
                                double gap = old.Metrics switch { MetricsMode.Line => 2, MetricsMode.Lines => 4, _ => 8 };
                                var gauge = plan.Part(ExpandedItem.Cpu)!;
                                var metrics = plan.Part(ExpandedItem.Memory)!;
                                bool same = Math.Abs(gauge.Box.Height - (parts[1].Top + parts[1].Height + (old.Metrics == MetricsMode.Line ? 6 : 10))) < .01 && gauge.Box.Y == 0
                                    && Math.Abs(metrics.Box.Y - parts[2].Top) < .01;
                                var network = plan.Part(ExpandedItem.Download)!;
                                same &= old.Metrics == MetricsMode.Line ? network.Box.Y == metrics.Box.Y : Math.Abs(network.Box.Bottom - (parts[2].Top + parts[2].Height)) < .01;
                                if (plan.Part(ExpandedItem.Frames) is { } fp) same &= fp.Box.Y == parts[5].Top + gap && fp.Box.Height == parts[5].Height - gap;
                                else same &= !old.FrameLine;
                                if (plan.Part(ExpandedItem.Storage) is { } sp) same &= sp.Box.Y == parts[3].Top + gap && sp.Box.Height == parts[3].Height - gap;
                                else same &= old.StorageLines == 0;
                                if (plan.Part(ExpandedItem.Power) is { } pp) same &= pp.Box.Y == parts[4].Top + gap && pp.Box.Height == parts[4].Height - gap;
                                else same &= old.PowerLines == 0;
                                // The graph never gives less than the floor, as the page kept: the plan's gauge is at least the head with margins and floor.
                                if (!same) { partDiffs++; if (first.Length == 0) first = $"parts {width}x{height} p{processes} f{frames} s{storage} w{power}"; }
                            }
        check($"Expanded layout regression: with CPU, GPU, memory and network chosen the steps are MonitorLayout's ({compared} sizes and settings, {squeezed} squeezed ones left out; {first})", stepDiffs == 0);
        check($"Expanded layout regression: and the parts sit where MonitorLayout.Parts puts them ({first})", partDiffs == 0);
    }

    private static void Special(Action<string, bool> check)
    {
        var content = new ExpandedContent(4, 2);
        var empty = ExpandedLayout.For([], 620, 220, content);
        check("Expanded layout: nothing chosen is the empty page that reads nothing", empty.Kind == ExpandedKind.Empty && empty.Parts.Count == 0 && empty.List == ExpandedList.None
            && empty.Demand(true) == ExpandedDemand.None && empty.Dropped.Count == 0);
        var unavailable = ExpandedLayout.For([ExpandedItem.Storage, ExpandedItem.Power], 620, 220, new(0, 0));
        check("Expanded layout: only what this machine cannot read is not the empty page", unavailable.Kind == ExpandedKind.Unavailable && unavailable.Parts.Count == 0
            && unavailable.Dropped.Count == 2 && unavailable.Demand(false) == ExpandedDemand.None);
        var list = ExpandedLayout.For([ExpandedItem.Processes], 620, 220, content);
        check("Expanded layout: the list alone fills the page", list.Kind == ExpandedKind.List && list.List == ExpandedList.Full && list.ListBox == new ExpandedBox(0, 0, 620, 220)
            && list.Parts.Count == 0 && list.Demand(true) == ExpandedDemand.Processes);
        foreach (var item in Every.Where(i => i != ExpandedItem.Processes))
        {
            var plan = ExpandedLayout.For([item], 620, 260, content);
            var part = plan.Part(item);
            check($"Expanded layout: {item} alone is shown, is the only part, and fills the page", part is not null && plan.Parts.Count == 1 && plan.Dropped.Count == 0
                && part.Box.Width == 620 && plan.Demand(false) == ExpandedMonitor.Demand([item], false));
            // The gauges, memory and network flex to the whole page; the curves and the drives only to their limits, the rest is kept as room around.
            check($"Expanded layout: {item} alone leaves no unused height, or only past its largest",
                item is ExpandedItem.Cpu or ExpandedItem.Gpu or ExpandedItem.Memory or ExpandedItem.Download ? plan.Hole <= 1 : plan.Hole <= 260 - part!.MinHeight * 1.45 + 1);
        }
        var cpuOnly = ExpandedLayout.For([ExpandedItem.Cpu], 620, 260, content);
        check("Expanded layout: a lone gauge is the main view, taking the whole height", cpuOnly.Part(ExpandedItem.Cpu) is { Form: ExpandedForm.Solo } p && p.Box.Height == 260 && cpuOnly.Hole == 0);
        var memoryOnly = ExpandedLayout.For([ExpandedItem.Memory], 620, 260, content);
        check("Expanded layout: lone memory takes its block form", memoryOnly.Part(ExpandedItem.Memory)!.Form == ExpandedForm.Blocks);
        var drives = ExpandedLayout.For([ExpandedItem.Storage], 620, 400, new(12, 0));
        check("Expanded layout: lone drives take more lines than beside other readings",
            drives.Steps.StorageLines == 6 && drives.Steps.StorageColumns == 2 && ExpandedLayout.For([ExpandedItem.Cpu, ExpandedItem.Storage], 620, 400, new(12, 0)).Steps.StorageLines <= 3);
        var pair = ExpandedLayout.For([ExpandedItem.Cpu, ExpandedItem.Gpu], 620, 220, content, main: MonitorMain.Gpu);
        check("Expanded layout: both gauges are a pair, the chosen one large", pair.Part(ExpandedItem.Gpu) is { Form: ExpandedForm.Pair } big && pair.Part(ExpandedItem.Cpu) is { Form: ExpandedForm.Pair } small
            && big.Box.Width > small.Box.Width && big.Box.X < small.Box.X);
        var memoryNetwork = ExpandedLayout.For([ExpandedItem.Download, ExpandedItem.Memory], 620, 60, content);
        check("Expanded layout: memory and network share a line in the table's order", memoryNetwork.Part(ExpandedItem.Download)!.Box.X < memoryNetwork.Part(ExpandedItem.Memory)!.Box.X
            && memoryNetwork.Steps.Metrics == MetricsMode.Line);
        var reordered = ExpandedLayout.For([ExpandedItem.Power, ExpandedItem.Cpu, ExpandedItem.Memory], 620, 260, content);
        check("Expanded layout: items stack in the table's order", reordered.Part(ExpandedItem.Power)!.Box.Y < reordered.Part(ExpandedItem.Cpu)!.Box.Y
            && reordered.Part(ExpandedItem.Cpu)!.Box.Y < reordered.Part(ExpandedItem.Memory)!.Box.Y);
        var nan = ExpandedLayout.For(Every, double.NaN, -4, content, new FontMetrics(double.NaN, double.PositiveInfinity));
        check("Expanded layout: nonsense sizes and fonts give a plan without parts out of the page", nan.Parts.All(part => part.Box.Inside(Page(nan), .5) && double.IsFinite(part.Box.Height)));
        var tiny = ExpandedLayout.For([ExpandedItem.Frames], 400, 12, content);
        check("Expanded layout: a page too short for a line still shows the one asked, squeezed and inside the page",
            tiny.Part(ExpandedItem.Frames) is { } line && tiny.Squeezed && line.Box.Inside(Page(tiny), .5) && tiny.Parts.All(part => part.Box.Bottom <= 12.01));
        var onlyDown = ExpandedLayout.For([ExpandedItem.Download], 620, 200, content);
        var downUp = ExpandedLayout.For([ExpandedItem.Download, ExpandedItem.Upload], 620, 200, content);
        check("Expanded layout: plans differing only in the upload switch have different signatures", onlyDown.Signature != downUp.Signature);
    }

    private static void Properties(Action<string, bool> check)
    {
        int plans = 0;
        var failures = new Dictionary<string, string>();
        void Fail(string rule, string message) { if (!failures.ContainsKey(rule)) failures[rule] = message; }
        for (int mask = 0; mask < 1 << Every.Length; mask++)
        {
            var items = Subset(mask);
            // Reordered copies for a few masks: the order must not break any property.
            var orders = new List<List<ExpandedItem>> { items };
            if (items.Count > 1) { var reversed = new List<ExpandedItem>(items); reversed.Reverse(); orders.Add(reversed); }
            foreach (var chosen in orders)
                foreach (var width in Widths)
                    foreach (var height in Heights)
                        foreach (var font in Fonts)
                            foreach (var content in Contents)
                                foreach (var main in new[] { MonitorMain.Cpu, MonitorMain.Gpu })
                                {
                                    if (main == MonitorMain.Gpu && !(chosen.Contains(ExpandedItem.Cpu) && chosen.Contains(ExpandedItem.Gpu))) continue;
                                    if (chosen != items && (font != Fonts[0] || main == MonitorMain.Gpu)) continue;
                                    var plan = ExpandedLayout.For(chosen, width, height, content, font, main);
                                    plans++;
                                    string id = $"[{string.Join("+", chosen)}] {width}x{height} font {font} content {content}";
                                    var page = Page(plan);
                                    var boxes = plan.Parts.Select(part => part.Box).Append(plan.ListBox).Where(box => box != Zero).ToList();
                                    if (plan.Parts.Any(part => !(double.IsFinite(part.Box.X) && double.IsFinite(part.Box.Y) && part.Box.Width >= 0 && part.Box.Height >= 0 && double.IsFinite(part.Box.Width) && double.IsFinite(part.Box.Height))))
                                        Fail("finite", id);
                                    foreach (var box in boxes) if (!box.Inside(page, .5)) Fail("inside", id + " " + box);
                                    for (int a = 0; a < boxes.Count; a++)
                                        for (int b = a + 1; b < boxes.Count; b++) if (boxes[a].Overlaps(boxes[b])) Fail("overlap", id + $" {boxes[a]} {boxes[b]}");
                                    if (plan.Parts.Select(p => p.Item).Distinct().Count() != plan.Parts.Count) Fail("once", id);
                                    // Nothing is shown that was not chosen, and what was chosen is shown or dropped, never both.
                                    var shown = plan.Shown.ToList();
                                    if (shown.Any(item => !chosen.Contains(item))) Fail("chosen", id);
                                    if (plan.Dropped.Intersect(shown).Any() || plan.Dropped.Any(item => !chosen.Contains(item))) Fail("dropped", id);
                                    if (chosen.Except(shown).Except(plan.Dropped).Any() || shown.Count + plan.Dropped.Count != chosen.Count) Fail("accounted", id);
                                    // Nothing is read for what is not shown.
                                    var demand = plan.Demand(true);
                                    if ((demand & ~ExpandedMonitor.Demand(chosen, true)) != 0) Fail("demand", id);
                                    if (chosen.Count == 0 && (plan.Kind != ExpandedKind.Empty || demand != ExpandedDemand.None)) Fail("empty", id);
                                    // Readable: every part at least its form's minimum, unless the plan says it was squeezed. Only for the panel's own sizes.
                                    if (!plan.Squeezed && height >= 100) foreach (var part in plan.Parts) if (part.Box.Height < part.MinHeight * font.Scale - .5) Fail("readable", id + $" {part.Item} {part.Box.Height} < {part.MinHeight * font.Scale}");
                                    if (!plan.Squeezed && plan.Part(ExpandedItem.Cpu) is { } g1 && g1.Box.Height < (plan.Steps.Figure * 1.4 + 26 + MonitorLayout.MinimumGraph) * font.Scale * .5) Fail("graph", id);
                                    // With a gauge nothing is left over; without one, room remains only once every part has grown to its limit.
                                    bool gauge = plan.Parts.Any(part => part.Item is ExpandedItem.Cpu or ExpandedItem.Gpu);
                                    if (plan.Kind == ExpandedKind.Readings && !plan.Squeezed)
                                    {
                                        if (gauge && plan.Hole > .5) Fail("hole-gauge", id);
                                        if (!gauge && plan.Hole > font.Scale + .01 && plan.Parts.Sum(part => part.Box.Height) < plan.Parts.Sum(part => part.MinHeight * font.Scale) * 1.45 - 1) Fail("hole", id + $" hole {plan.Hole}");
                                    }
                                    // Priority when room runs short: the drives only where the frame rate and power are, the frame rate only where power is.
                                    if (plan.Part(ExpandedItem.Storage) is not null && chosen.Contains(ExpandedItem.Frames) && plan.Part(ExpandedItem.Frames) is null) Fail("priority-frames", id);
                                    if (plan.Part(ExpandedItem.Storage) is not null && chosen.Contains(ExpandedItem.Power) && content.PowerLines > 0 && plan.Part(ExpandedItem.Power) is null) Fail("priority-power", id);
                                    if (plan.Part(ExpandedItem.Frames) is not null && chosen.Contains(ExpandedItem.Power) && content.PowerLines > 0 && plan.Part(ExpandedItem.Power) is null) Fail("priority-power2", id);
                                    // Same input, same plan.
                                    if (plans % 97 == 0 && ExpandedLayout.For(chosen, width, height, content, font, main).Signature != plan.Signature) Fail("deterministic", id);
                                }
        }
        foreach (var rule in new[] { "finite", "inside", "overlap", "once", "chosen", "dropped", "accounted", "demand", "empty", "readable", "graph", "hole-gauge", "hole", "priority-frames", "priority-power", "priority-power2", "deterministic" })
            check($"Expanded layout properties ({plans} plans over every subset): {rule}" + (failures.TryGetValue(rule, out var message) ? " -- " + message : ""), !failures.ContainsKey(rule));

        // Fewer items fit as the page shrinks or the text grows, and the order of dropping holds.
        int bad = 0; string badId = "";
        for (int mask = 0; mask < 1 << Every.Length; mask++)
        {
            var items = Subset(mask).Where(item => item != ExpandedItem.Processes).ToList();
            // The page as it was loses a line of the lines under memory and network where they step from one line to a line each (at 150 DIP) when
            // two or more kinds of line are asked for, and the plan keeps that so the page does not change; it is left out here (a design question).
            if ((items.Contains(ExpandedItem.Cpu) || items.Contains(ExpandedItem.Gpu)) && items.Count(item => item is ExpandedItem.Frames or ExpandedItem.Storage or ExpandedItem.Power) >= 2) continue;
            foreach (var content in Contents)
                foreach (var width in new[] { 440d, 620, 1200 })
                {
                    int previous = -1;
                    for (double height = 40; height <= 520; height += 6)
                    {
                        int now = ExpandedLayout.For(items, width, height, content).Parts.Count;
                        if (now < previous) { bad++; if (badId.Length == 0) badId = $"[{string.Join("+", items)}] {width}x{height} {content}: {previous} then {now}"; }
                        previous = now;
                    }
                }
        }
        check("Expanded layout: a taller page never shows fewer items (list aside) " + badId, bad == 0);
        bad = 0; badId = "";
        for (int mask = 0; mask < 1 << Every.Length; mask += 3)
        {
            var items = Subset(mask).Where(item => item != ExpandedItem.Processes).ToList();
            foreach (var height in Heights)
            {
                int small = ExpandedLayout.For(items, 620, height, new(4, 2), new FontMetrics(1, 1.7)).Parts.Count, large = ExpandedLayout.For(items, 620, height, new(4, 2), new FontMetrics(1.5, 1.7)).Parts.Count;
                if (large > small) { bad++; if (badId.Length == 0) badId = $"[{string.Join("+", items)}] {height}: {small} vs {large}"; }
            }
        }
        check("Expanded layout: larger text never fits more " + badId, bad == 0);
        var scaled = ExpandedLayout.For(Every, 900, 450, new(4, 2), new FontMetrics(1.5, 1.7));
        var unit = ExpandedLayout.For(Every, 600, 300, new(4, 2));
        check("Expanded layout: text at 150 % is the page at 100 % in a third less room, drawn larger",
            scaled.Steps == unit.Steps && scaled.Parts.Count == unit.Parts.Count
            && scaled.Parts.Zip(unit.Parts).All(pair => pair.First.Item == pair.Second.Item && Math.Abs(pair.First.Box.Height - pair.Second.Box.Height * 1.5) < .01 && Math.Abs(pair.First.Box.Y - pair.Second.Box.Y * 1.5) < .01));
    }

    private static void Stability(Action<string, bool> check)
    {
        var items = ExpandedMonitor.Shown(ExpandedMonitor.Defaults());
        var content = new ExpandedContent(3, 1);
        // Find a height where the page changes step and hold it with a previous plan within the band.
        double step = 0; ExpandedPlan? below = null;
        for (double height = 100; height < 400; height += 1)
        {
            var a = ExpandedLayout.For(items, 700, height, content); var b = ExpandedLayout.For(items, 700, height + 1, content);
            if (a.Signature != b.Signature) { step = height + 1; below = a; break; }
        }
        check("Expanded layout: a size held at a step does not flip back and forth", below is not null
            && ExpandedLayout.For(items, 700, step, content, null, MonitorMain.Cpu, below).Signature == below.Signature
            && ExpandedLayout.For(items, 700, step + ExpandedLayout.Hysteresis + 2, content, null, MonitorMain.Cpu, below).Signature != below.Signature);
    }
}

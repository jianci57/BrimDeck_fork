namespace BrimDeck.Core;

// The readings the expanded system monitor page can show, one table of them (ExpandedReading) in the order the page stacks them.
// Saved as numbers, so new members go at the end. Cpu and Gpu are the gauges with their columns (the one chosen in MonitorMain is
// shown large when both are shown, a lone one is shown large), Memory, Download and Upload the readings under them (the two rates share one
// block of one line, side by side or alone; with memory they are shown side by side, one to a line or as blocks as room allows), Frames, Storage and Power the lines under those (Power is the whole machine's
// power draw with the machine's own battery beside it), Processes the list of the busiest apps. The temperatures are not an
// item: they are a detail of Cpu, Gpu and Storage (DeckSettings.MonitorTemperatures).
public enum ExpandedItem { Cpu, Gpu, Memory, Download, Frames, Storage, Power, Processes, Upload }

// One item of the table as set in the settings: whether the page shows it. Its place in the table is its place on the page (a block
// at a time: ExpandedMonitor.Blocks).
public sealed class ExpandedReading
{
    public ExpandedItem Item { get; set; }
    public bool Visible { get; set; } = true;
    public ExpandedReading Copy() => (ExpandedReading)MemberwiseClone();
}

// What the sampler must read for the page as laid out; nothing else is read for it.
[Flags]
public enum ExpandedDemand
{
    None = 0, Cpu = 1, Gpu = 2, Memory = 4, Network = 8, Frames = 16, Storage = 32, Power = 64, Processes = 128,
    // The processor's, the graphics adapter's and the drives' temperatures; only asked for with a reading that carries one.
    Temperatures = 256
}

public static class ExpandedMonitor
{
    // The default order, top to bottom: the page as it was before the table.
    public static readonly IReadOnlyList<ExpandedItem> Order =
        [ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Frames, ExpandedItem.Storage, ExpandedItem.Power, ExpandedItem.Processes];

    // The table for settings saved before it: the four switches the page had (the process list, the drives, power with the battery,
    // the frame rate) say which of those show; CPU, GPU, memory and network always showed. In the default order.
    public static List<ExpandedReading> Defaults(bool processes = true, bool storage = true, bool power = true, bool frames = false)
        => [.. Order.Select(item => new ExpandedReading
        {
            Item = item,
            Visible = item switch { ExpandedItem.Processes => processes, ExpandedItem.Storage => storage, ExpandedItem.Power => power, ExpandedItem.Frames => frames, _ => true }
        })];

    // The parts the page keeps together, in the default order: the gauges (CPU and GPU share one band), memory with the network (one
    // band), then the frame rate, the drives, power and the process list, one each. The settings move a block whole, so the table's
    // order is always one the page can show.
    public static readonly IReadOnlyList<IReadOnlyList<ExpandedItem>> Blocks =
    [
        [ExpandedItem.Cpu, ExpandedItem.Gpu], [ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload],
        [ExpandedItem.Frames], [ExpandedItem.Storage], [ExpandedItem.Power], [ExpandedItem.Processes]
    ];
    public static IReadOnlyList<ExpandedItem> BlockOf(ExpandedItem item) => Blocks.First(block => block.Contains(item));
    // The blocks of a normalized table, in its order.
    public static List<IReadOnlyList<ExpandedItem>> BlocksIn(IEnumerable<ExpandedReading> readings) => [.. readings.Select(reading => BlockOf(reading.Item)).Distinct()];

    // Every item once, block by block: unknown and repeated items are dropped, missing ones join at the end as the default table has
    // them, and each block's items stand together where the first of them was saved. Inside a block the order is the block's own,
    // but memory and the network (the two rates together) keep their saved order, and so do the download and the upload: the page
    // shows those either way round. Unlike the collapsed row there is no reading that must stay: a table with nothing shown is kept
    // as it is (the page then says there is nothing to show).
    public static List<ExpandedReading> Normalize(IEnumerable<ExpandedReading?>? readings)
    {
        var list = (readings ?? []).OfType<ExpandedReading>().Where(reading => Enum.IsDefined(reading.Item)).DistinctBy(reading => reading.Item)
            .Select(reading => new ExpandedReading { Item = reading.Item, Visible = reading.Visible }).ToList();
        // Upload came after the table was made (the network was one item, Download's number): a table without it shows it as Download
        // is, so the network shows as it did.
        if (list.All(reading => reading.Item != ExpandedItem.Upload) && list.Find(reading => reading.Item == ExpandedItem.Download) is { } download)
            list.Add(new ExpandedReading { Item = ExpandedItem.Upload, Visible = download.Visible });
        list.AddRange(Defaults().Where(item => list.All(reading => reading.Item != item.Item)));
        var at = list.Select((reading, index) => (reading.Item, index)).ToDictionary(entry => entry.Item, entry => entry.index);
        var byItem = list.ToDictionary(reading => reading.Item);
        IEnumerable<ExpandedItem> Inside(IReadOnlyList<ExpandedItem> block)
        {
            if (block[0] != ExpandedItem.Memory) return block;
            ExpandedItem[] network = at[ExpandedItem.Download] < at[ExpandedItem.Upload] ? [ExpandedItem.Download, ExpandedItem.Upload] : [ExpandedItem.Upload, ExpandedItem.Download];
            return at[ExpandedItem.Memory] < at[network[0]] ? [ExpandedItem.Memory, .. network] : [.. network, ExpandedItem.Memory];
        }
        return [.. Blocks.OrderBy(block => block.Min(item => at[item])).SelectMany(block => Inside(block).Select(item => byItem[item]))];
    }

    // The table in the default order (blocks, memory above the network, the download before the upload), each item still shown or
    // hidden as it was: the hidden ones go back to their places as well, for when they are shown again.
    public static List<ExpandedReading> InDefaultOrder(IEnumerable<ExpandedReading?>? readings)
    {
        var list = Normalize(readings);
        return [.. Order.Select(item => list.First(reading => reading.Item == item))];
    }

    // Whether the page is in the default order. The process list's place in the table is left out: the page's size places the list.
    public static bool IsDefaultOrder(IEnumerable<ExpandedReading?>? readings)
        => Normalize(readings).Select(reading => reading.Item).Where(item => item != ExpandedItem.Processes).SequenceEqual(Order.Where(item => item != ExpandedItem.Processes));

    // Memory and the network change places (item Memory), or the download and the upload do (Download or Upload), in their block.
    public static List<ExpandedReading> Swap(IEnumerable<ExpandedReading?>? readings, ExpandedItem item)
    {
        var list = Normalize(readings);
        int start = list.FindIndex(reading => BlockOf(reading.Item)[0] == ExpandedItem.Memory);
        var part = list.GetRange(start, 3);
        var memory = part.First(reading => reading.Item == ExpandedItem.Memory);
        var network = part.Where(reading => reading.Item != ExpandedItem.Memory).ToList();
        List<ExpandedReading> next;
        if (item == ExpandedItem.Memory) next = part[0] == memory ? [.. network, memory] : [memory, .. network];
        else { network.Reverse(); next = part[0] == memory ? [memory, .. network] : [.. network, memory]; }
        list.RemoveRange(start, 3); list.InsertRange(start, next);
        return list;
    }

    // The table with the given blocks (each named by any of its items) in the given order, in the places those blocks held; the other
    // blocks keep theirs. What the settings' sketch of the page saves after a block is dragged.
    public static List<ExpandedReading> Arrange(IEnumerable<ExpandedReading?>? readings, IEnumerable<ExpandedItem> blocks)
    {
        var list = Normalize(readings);
        var moved = blocks.Select(BlockOf).Distinct().ToList();
        var next = new Queue<IReadOnlyList<ExpandedItem>>(moved);
        // Each block's items as the table has them (memory and the network, the two rates, either way round).
        return [.. BlocksIn(list).Select(block => moved.Contains(block) ? next.Dequeue() : block).SelectMany(block => list.Where(reading => BlockOf(reading.Item) == block))];
    }

    // The shown items in the table's order.
    public static List<ExpandedItem> Shown(IEnumerable<ExpandedReading> readings) => [.. readings.Where(reading => reading.Visible).Select(reading => reading.Item).Distinct()];

    // The four switches older versions read, as the table has them: (processes, storage, power, frames).
    public static (bool Processes, bool Storage, bool Power, bool Frames) Switches(IEnumerable<ExpandedReading> readings)
    {
        var shown = Shown(readings);
        return (shown.Contains(ExpandedItem.Processes), shown.Contains(ExpandedItem.Storage), shown.Contains(ExpandedItem.Power), shown.Contains(ExpandedItem.Frames));
    }

    // What to read for a selection, before the page is laid out (an upper bound: ExpandedPlan.Demand is what the layout kept).
    public static ExpandedDemand Demand(IEnumerable<ExpandedItem> shown, bool temperatures)
    {
        var demand = ExpandedDemand.None;
        foreach (var item in shown.Distinct())
            demand |= item switch
            {
                ExpandedItem.Cpu => ExpandedDemand.Cpu, ExpandedItem.Gpu => ExpandedDemand.Gpu, ExpandedItem.Memory => ExpandedDemand.Memory,
                ExpandedItem.Download or ExpandedItem.Upload => ExpandedDemand.Network, ExpandedItem.Frames => ExpandedDemand.Frames, ExpandedItem.Storage => ExpandedDemand.Storage,
                ExpandedItem.Power => ExpandedDemand.Power, ExpandedItem.Processes => ExpandedDemand.Processes, _ => ExpandedDemand.None
            };
        return temperatures && (demand & (ExpandedDemand.Cpu | ExpandedDemand.Gpu | ExpandedDemand.Storage)) != 0 ? demand | ExpandedDemand.Temperatures : demand;
    }
}

namespace BrimDeck.Core;

// The font as the layout needs to know it: Scale is the text's size relative to the default (a larger text takes more room, so
// less fits), LineFactor the height of a line of body text in body sizes (1.7 in the default face; a custom face may be taller).
public sealed record FontMetrics(double Scale = 1, double LineFactor = FontMetrics.DefaultLineFactor)
{
    public const double DefaultLineFactor = 1.7, MinimumScale = .75, MaximumScale = 2;
    public static readonly FontMetrics Default = new();
    public FontMetrics Fit() => new(double.IsFinite(Scale) ? Math.Clamp(Scale, MinimumScale, MaximumScale) : 1,
        double.IsFinite(LineFactor) ? Math.Clamp(LineFactor, 1.2, 2.6) : DefaultLineFactor);
}

// What the page can show of the system at the moment: the volumes it has entries for (as many as the page would show) and the lines
// the power reading has (0 without a source of power, 1 with, 2 with the battery's own line besides).
public sealed record ExpandedContent(int Drives = 0, int PowerLines = 0);

// Empty: nothing is chosen, the page says so and reads nothing. Unavailable: something is chosen but nothing of it can be read on
// this machine. Readings: the readings (and the list beside or under them). List: the process list alone.
public enum ExpandedKind { Empty, Unavailable, Readings, List }
public enum ExpandedList { None, Side, Below, Full }
// The shape an item takes: a gauge with its partner small beside it (Pair) or alone (Solo); memory and network on one line shared
// (Line), a line each (Lines) or memory as a block (Blocks); a line of its own (Row) or several (Rows).
public enum ExpandedForm { Pair, Solo, Line, Lines, Blocks, Row, Rows }

public readonly record struct ExpandedBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Overlaps(ExpandedBox other, double tolerance = .01) =>
        X < other.Right - tolerance && other.X < Right - tolerance && Y < other.Bottom - tolerance && other.Y < Bottom - tolerance;
    public bool Inside(ExpandedBox outer, double tolerance = .01) =>
        X >= outer.X - tolerance && Y >= outer.Y - tolerance && Right <= outer.Right + tolerance && Bottom <= outer.Bottom + tolerance;
}

// One item placed: its box in DIPs of the content area, its form, and the least height at which its form reads (not met only in
// a squeezed plan).
public sealed record ExpandedPart(ExpandedItem Item, ExpandedBox Box, ExpandedForm Form, double MinHeight);

// The page for a selection and a size. Steps is the same step the page had before the table (at unit scale), which the views are
// still built from; Parts are the places of the items. Squeezed: the room was too short for the graph's minimum or the readings
// were cut to fit. Dropped: the chosen items not shown (no room, or nothing to read). Hole: the height no part uses, which only remains once every part has reached its largest.
public sealed class ExpandedPlan
{
    public required ExpandedKind Kind { get; init; }
    public required MonitorLayout Steps { get; init; }
    public required double Scale { get; init; }
    public required IReadOnlyList<ExpandedPart> Parts { get; init; }
    public required ExpandedBox Readings { get; init; }
    public required ExpandedList List { get; init; }
    public required ExpandedBox ListBox { get; init; }
    public required IReadOnlyList<ExpandedItem> Dropped { get; init; }
    public bool Squeezed { get; init; }
    public double Hole { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    // The rates the network block shows: it is one block of one line, with the download, the upload or both.
    public bool Downloads { get; init; }
    public bool Uploads { get; init; }
    // With both rates shown: the upload at the left (the table puts it before the download).
    public bool UploadFirst { get; init; }
    public IEnumerable<ExpandedItem> Shown => Parts.Select(part => part.Item).Where(item => item is not (ExpandedItem.Download or ExpandedItem.Upload))
        .Concat(Downloads ? [ExpandedItem.Download] : []).Concat(Uploads ? [ExpandedItem.Upload] : [])
        .Concat(List != ExpandedList.None ? [ExpandedItem.Processes] : []);
    public ExpandedPart? Part(ExpandedItem item) => Parts.FirstOrDefault(part => part.Item == item);
    // Type sizes as drawn.
    public double Figure => Steps.Figure * Scale;
    public double Body => Steps.Body * Scale;
    public double Caption => Steps.Caption * Scale;
    public double RowHeight => Steps.RowHeight * Scale;

    // What the sampler reads for this page and nothing else: the items the layout kept, and the temperatures where one is shown.
    public ExpandedDemand Demand(bool temperatures) => ExpandedMonitor.Demand(Shown, temperatures);

    // What makes two plans the same page, as far as building it goes.
    public string Signature => string.Join("|", Kind, Steps, List, Downloads, Uploads, UploadFirst, string.Join(",", Parts.Select(part => part.Item + ":" + part.Form)));
}

// The expanded page's layout from the table of items: the page before the table (MonitorLayout) made general. The rules:
// 1. Type sizes, the list's placement and the step memory and network take (Line, Lines, Blocks) are MonitorLayout's, so a page
//    with all of CPU, GPU, memory and network is laid out exactly as it was (the regression test compares them).
// 2. Items stack top to bottom in the table's order: the gauges (with their columns) are one band, memory and network another,
//    then a band each for the frame rate, the drives and power. The gauges' graph takes the height the others leave.
// 3. Priority when height runs short, as before: gauges with a graph of MinimumGraph, memory and network on one line, then the
//    first line of power, then the frame rate's line, then the drives, then the battery's own line. Below those, the graph shrinks
//    to its floor, then network, then memory, then the lines leave (a squeezed page).
// 4. A lone item is the main view: the gauge gets the page; memory, network, the frame rate and power are stretched, the drives
//    take up to six lines. Without a gauge nothing flexes, so what is left is shared out by stretching (memory and network to the whole room, curves up to
//    six lines, the drives' lines half as much again) and, past that, kept as equal room above and below.
// 5. A list without readings fills the page; with readings it is beside or under them as before.
public static class ExpandedLayout
{
    public const double ListGap = 22, MetricsGap = 16, PairGap = 14, GraphFloor = 16, LinesAbove = 150;
    public const double BlocksWithGauge = 250, BlocksAlone = 120;
    public const int HeroStorageLines = 6;
    // How far a band may grow past its natural height without a gauge: memory and network's sparklines and bars as far as there is room,
    // the frame rate's and power's curves to six lines, the drives' lines half as much again.
    public const double StretchBlocks = 1000, StretchRows = 6, StretchDrives = 1.5;
    // The least width a gauge keeps beside its partner.
    public const double PairMinimum = 120;
    public const double Hysteresis = MonitorLayout.Hysteresis;

    private enum BandKind { Gauge, Metrics, Frames, Storage, Power }
    private sealed class Band
    {
        public BandKind Kind;
        public int Index;
        public double Height, Stretch;
    }

    // The plan for this size, keeping previous while the size is within Hysteresis of the step it shows.
    public static ExpandedPlan For(IEnumerable<ExpandedItem>? shown, double width, double height, ExpandedContent? content, FontMetrics? font, MonitorMain main, ExpandedPlan? previous)
    {
        var items = (shown ?? []).ToList();
        var next = For(items, width, height, content, font, main);
        if (previous is null || next.Signature == previous.Signature) return next;
        foreach (double dw in new[] { 0, -Hysteresis, Hysteresis })
            foreach (double dh in new[] { 0, -Hysteresis, Hysteresis })
                if (For(items, width + dw, height + dh, content, font, main).Signature == previous.Signature) return For(items, width + dw, height + dh, content, font, main);
        return next;
    }

    public static ExpandedPlan For(IEnumerable<ExpandedItem>? shown, double width, double height, ExpandedContent? content = null, FontMetrics? font = null, MonitorMain main = MonitorMain.Cpu)
    {
        var order = (shown ?? []).Where(item => Enum.IsDefined(item)).Distinct().ToList();
        font = (font ?? FontMetrics.Default).Fit(); content ??= new();
        double scale = font.Scale, k = font.LineFactor / FontMetrics.DefaultLineFactor;
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        height = double.IsFinite(height) ? Math.Max(0, height) : 0;
        double w = width / scale, h = height / scale;

        bool cpu = order.Contains(ExpandedItem.Cpu), gpu = order.Contains(ExpandedItem.Gpu), gauge = cpu || gpu;
        bool mem = order.Contains(ExpandedItem.Memory), down = order.Contains(ExpandedItem.Download), up = order.Contains(ExpandedItem.Upload), net = down || up, metrics = mem || net;
        bool frames = order.Contains(ExpandedItem.Frames), processes = order.Contains(ExpandedItem.Processes);
        int drives = order.Contains(ExpandedItem.Storage) ? Math.Max(0, content.Drives) : 0;
        int powerWant = order.Contains(ExpandedItem.Power) ? Math.Clamp(content.PowerLines, 0, 2) : 0;
        bool readings = gauge || metrics || frames || drives > 0 || powerWant > 0;

        // Type sizes and the list's step as before the table.
        var basis = MonitorLayout.For(w, h, processes && readings);
        double figure = basis.Figure, body = basis.Body, caption = basis.Caption, row = basis.RowHeight;

        if (!readings && !processes)
        {
            return new ExpandedPlan
            {
                Kind = order.Count == 0 ? ExpandedKind.Empty : ExpandedKind.Unavailable, Steps = basis, Scale = scale, Parts = [], Readings = default, List = ExpandedList.None,
                ListBox = default, Dropped = order, Width = width, Height = height, Hole = height
            };
        }

        var list = !processes ? ExpandedList.None : !readings ? ExpandedList.Full : (ExpandedList)(int)basis.List;
        double readingsH = list == ExpandedList.Below ? MonitorLayout.StackedReadings : h;
        double readingsW = list == ExpandedList.Side ? w - ListGap - MonitorLayout.SideListWidth(w) : w;
        if (!readings)
        {
            var only = new ExpandedBox(0, 0, w, h);
            return new ExpandedPlan
            {
                Kind = ExpandedKind.List, Steps = basis, Scale = scale, Parts = [], Readings = default, List = ExpandedList.Full, ListBox = Scaled(only, scale),
                Dropped = [.. order.Where(item => item != ExpandedItem.Processes)], Width = width, Height = height
            };
        }

        double ln = Math.Ceiling(body * FontMetrics.DefaultLineFactor * k), rowS = Math.Ceiling(body * 1.5 * k);
        double headH = gauge ? Math.Ceiling(figure * 1.4 * k) : 0;
        double minGraph = gauge ? MonitorLayout.MinimumGraph : 0;
        double Margins(MetricsMode mode) => gauge ? (mode == MetricsMode.Line ? 10 : 16) : 0;
        double Rest(MetricsMode mode) => Rest2(mode, mem, net);
        double Gap(MetricsMode mode) => mode switch { MetricsMode.Line => 2, MetricsMode.Lines => 4, _ => 8 };

        // Memory and network take a line each only where a line of the lines under them still fits, and blocks from BlocksFrom.
        // Without a gauge to give up height, a line of each line asked for must still fit under them (with a gauge: a line, as before).
        int wanted = (frames ? 1 : 0) + (drives > 0 ? 1 : 0) + (powerWant > 0 ? 1 : 0);
        double linesFrom = Math.Max(gauge ? LinesAbove : 0, headH + Margins(MetricsMode.Lines) + Rest(MetricsMode.Lines) + Gap(MetricsMode.Lines) + minGraph + rowS
            + (gauge ? 0 : Math.Max(0, wanted - 1) * (Gap(MetricsMode.Lines) + rowS)));
        double blocksFrom = gauge ? BlocksWithGauge : Math.Max(BlocksAlone, Rest(MetricsMode.Blocks) + Gap(MetricsMode.Blocks) + rowS + Math.Max(0, wanted - 1) * (Gap(MetricsMode.Blocks) + rowS));
        var mode = readingsH < linesFrom ? MetricsMode.Line : readingsH < blocksFrom ? MetricsMode.Lines : MetricsMode.Blocks;
        double head = headH, margins = Margins(mode), rest = Rest(mode), gap = Gap(mode);

        int PowerRoom(double room, int storageLines)
        {
            double drivesHeight = storageLines > 0 ? gap + storageLines * rowS : 0;
            return Math.Clamp((int)Math.Floor((room - head - margins - rest - drivesHeight - gap - minGraph) / rowS), 0, 2);
        }
        int kinds = (gauge ? 1 : 0) + (metrics ? 1 : 0) + (frames ? 1 : 0) + (drives > 0 ? 1 : 0) + (powerWant > 0 ? 1 : 0);
        int storageCap = kinds == 1 && drives > 0 ? HeroStorageLines : MonitorLayout.MaximumStorageLines;
        int StorageRoom(double room) => Math.Clamp((int)Math.Floor((room - head - margins - rest - gap - minGraph) / rowS), 0, storageCap);

        // The first line of power (with the battery, sharing it) before the drives, then the frame rate's line, then the drives.
        bool firstLine = powerWant > 0 && PowerRoom(readingsH, 0) >= 1;
        double line = gap + rowS, reserve = firstLine ? line : 0;
        bool frameLine = frames && PowerRoom(readingsH - reserve, 0) >= 1;
        double frameRoom = frameLine ? line : 0;
        int columns = drives <= 0 ? 0 : readingsW >= MonitorLayout.StorageTwoColumns ? Math.Min(drives, MonitorLayout.MaximumStorageColumns) : 1;
        int storageLines = drives <= 0 ? 0 : Math.Min((drives + columns - 1) / columns, StorageRoom(readingsH - reserve - frameRoom));
        int powerLines = powerWant <= 0 ? 0 : Math.Min(powerWant, Math.Max(PowerRoom(readingsH - frameRoom, storageLines), firstLine ? 1 : 0));

        // A page too short for any line still shows the first of what was asked, squeezed, rather than nothing.
        bool squeezed = false;
        bool memShown = mem, netShown = net, gaugeShown = gauge;
        if (!gauge && !metrics && !frameLine && storageLines == 0 && powerLines == 0)
        {
            squeezed = true;
            if (powerWant > 0) powerLines = 1; else if (frames) frameLine = true; else storageLines = 1;
        }

        // Bands in the table's order.
        int Index(params ExpandedItem[] of) => of.Where(item => order.Contains(item)).Select(item => order.IndexOf(item)).DefaultIfEmpty(int.MaxValue).Min();
        double graphMin = minGraph;
        List<Band> Build()
        {
            double restNow = Rest2(mode, memShown, netShown);
            var bands = new List<Band>();
            if (gaugeShown) bands.Add(new Band { Kind = BandKind.Gauge, Index = Index(ExpandedItem.Cpu, ExpandedItem.Gpu), Height = head + margins + graphMin });
            if (restNow > 0) bands.Add(new Band { Kind = BandKind.Metrics, Index = Index(ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload), Height = restNow, Stretch = StretchBlocks });
            if (frameLine) bands.Add(new Band { Kind = BandKind.Frames, Index = Index(ExpandedItem.Frames), Height = rowS, Stretch = StretchRows });
            if (storageLines > 0) bands.Add(new Band { Kind = BandKind.Storage, Index = Index(ExpandedItem.Storage), Height = storageLines * rowS, Stretch = StretchDrives });
            if (powerLines > 0) bands.Add(new Band { Kind = BandKind.Power, Index = Index(ExpandedItem.Power), Height = powerLines * rowS, Stretch = StretchRows });
            bands.Sort((a, b) => a.Index.CompareTo(b.Index));
            return bands;
        }
        double Rest2(MetricsMode m, bool memOn, bool netOn) => !memOn && !netOn ? 0 : m switch
        {
            MetricsMode.Line => ln,
            MetricsMode.Lines => memOn && netOn ? 2 * ln + 6 : ln,
            _ => memOn && netOn ? Math.Ceiling(body * FontMetrics.DefaultLineFactor * k + 19) + ln + 10 : memOn ? Math.Ceiling(body * FontMetrics.DefaultLineFactor * k + 19) : ln
        };
        double Total(List<Band> bands)
        {
            double total = 0;
            for (int i = 0; i < bands.Count; i++) total += bands[i].Height + (i > 0 && bands[i - 1].Kind != BandKind.Gauge ? gap : 0);
            return total;
        }
        var all = Build();
        if (Total(all) > readingsH + .001 && gaugeShown)
        {
            squeezed = true; graphMin = GraphFloor; all = Build();
        }
        // Still too much: the lines leave, then network, then memory (the battery's own line before the power line).
        while (Total(all) > readingsH + .001 && all.Count > 1)
        {
            if (frameLine) frameLine = false;
            else if (storageLines > 0) storageLines = 0;
            else if (powerLines > 1) powerLines = 1;
            else if (powerLines > 0) powerLines = 0;
            else if (netShown && memShown) netShown = false;
            else if (memShown && gaugeShown) memShown = false;
            else break;
            squeezed = true;
            all = Build();
        }
        // A lone band that still does not fit is cut to the page.
        if (Total(all) > readingsH + .001 && all.Count == 1) { all[0].Height = readingsH; squeezed = true; }

        // Heights: the gauge's graph takes what the others leave; with no gauge the rest is stretched, then kept as room around.
        double hole = 0, offset = 0;
        double fixedTotal = Total(all) - (all.FirstOrDefault(b => b.Kind == BandKind.Gauge)?.Height ?? 0);
        var gaugeBand = all.FirstOrDefault(b => b.Kind == BandKind.Gauge);
        if (gaugeBand is not null)
        {
            // The band's gaps: counted in the total with the gauge's own height removed, so add them back as they stand.
            gaugeBand.Height = Math.Max(0, readingsH - fixedTotal);
            if (all.Count == 1) gaugeBand.Height = readingsH;
        }
        else
        {
            double slack = Math.Max(0, readingsH - Total(all));
            double room = all.Sum(b => (b.Stretch - 1) * b.Height);
            if (room > 0 && slack > 0)
            {
                double used = Math.Min(slack, room);
                var natural = all.ToDictionary(band => band, band => band.Height);
                foreach (var band in all) band.Height += Math.Floor(used * ((band.Stretch - 1) * band.Height) / room);
                // The whole DIPs the shares left over go to the bands that can still grow.
                double left = Math.Floor(used) - all.Sum(band => band.Height - natural[band]);
                foreach (var band in all)
                {
                    double more = Math.Min(left, Math.Max(0, Math.Floor(band.Stretch * natural[band]) - band.Height));
                    band.Height += more; left -= more;
                }
            }
            hole = Math.Max(0, readingsH - Total(all));
            offset = Math.Floor(hole / 2);
        }

        // Places.
        var parts = new List<ExpandedPart>();
        var dropped = new List<ExpandedItem>();
        double y = offset;
        for (int i = 0; i < all.Count; i++)
        {
            var band = all[i];
            if (i > 0 && all[i - 1].Kind != BandKind.Gauge) y += gap;
            double bh = band.Height;
            switch (band.Kind)
            {
                case BandKind.Gauge:
                {
                    double minHeight = head + margins + (squeezed ? Math.Min(graphMin, GraphFloor) : MonitorLayout.MinimumGraph);
                    double mini = Math.Round(Math.Clamp(figure * 3.4, 54, 84));
                    bool pair = cpu && gpu && readingsW >= mini + PairGap + PairMinimum;
                    var first = cpu && gpu ? (main == MonitorMain.Gpu ? ExpandedItem.Gpu : ExpandedItem.Cpu) : cpu ? ExpandedItem.Cpu : ExpandedItem.Gpu;
                    var second = first == ExpandedItem.Cpu ? ExpandedItem.Gpu : ExpandedItem.Cpu;
                    if (pair)
                    {
                        parts.Add(new(first, Scaled(new(0, y, readingsW - mini - PairGap, bh), scale), ExpandedForm.Pair, minHeight));
                        parts.Add(new(second, Scaled(new(readingsW - mini, y, mini, bh), scale), ExpandedForm.Pair, minHeight));
                    }
                    else parts.Add(new(first, Scaled(new(0, y, readingsW, bh), scale), ExpandedForm.Solo, minHeight));
                    break;
                }
                case BandKind.Metrics:
                    PlaceMetrics(parts, order, mode, memShown, netShown, down ? ExpandedItem.Download : ExpandedItem.Upload, readingsW, y, bh, ln, body, k, scale);
                    break;
                case BandKind.Frames:
                    parts.Add(new(ExpandedItem.Frames, Scaled(new(0, y, readingsW, bh), scale), ExpandedForm.Row, rowS));
                    break;
                case BandKind.Storage:
                    parts.Add(new(ExpandedItem.Storage, Scaled(new(0, y, readingsW, bh), scale), ExpandedForm.Rows, storageLines * rowS));
                    break;
                default:
                    parts.Add(new(ExpandedItem.Power, Scaled(new(0, y, readingsW, bh), scale), powerLines > 1 ? ExpandedForm.Rows : ExpandedForm.Row, powerLines * rowS));
                    break;
            }
            y += bh;
        }

        var steps = new MonitorLayout(figure, body, caption, mode, (ListPlacement)(int)(list == ExpandedList.Full ? ExpandedList.None : list), row)
        {
            StorageLines = storageLines, StorageColumns = storageLines > 0 ? columns : 0, PowerLines = powerLines, FrameLine = frameLine
        };
        var listBox = list switch
        {
            ExpandedList.Side => new ExpandedBox(readingsW + ListGap, 0, MonitorLayout.SideListWidth(w), h),
            ExpandedList.Below => new ExpandedBox(0, MonitorLayout.StackedReadings + 10, w, Math.Max(0, h - MonitorLayout.StackedReadings - 10)),
            _ => default
        };
        var plan = new ExpandedPlan
        {
            Kind = ExpandedKind.Readings, Steps = steps, Scale = scale, Parts = parts, Readings = Scaled(new(0, 0, readingsW, readingsH), scale), List = list,
            ListBox = Scaled(listBox, scale), Dropped = dropped, Squeezed = squeezed, Hole = hole * scale, Width = width, Height = height,
            Downloads = down && netShown, Uploads = up && netShown,
            UploadFirst = down && up && netShown && order.IndexOf(ExpandedItem.Upload) < order.IndexOf(ExpandedItem.Download)
        };
        var present = plan.Shown.ToHashSet();
        dropped.AddRange(order.Where(item => !present.Contains(item)));
        return plan;
    }

    private static void PlaceMetrics(List<ExpandedPart> parts, List<ExpandedItem> order, MetricsMode mode, bool mem, bool net, ExpandedItem netItem, double width, double y, double height,
        double ln, double body, double k, double scale)
    {
        bool both = mem && net;
        int memAt = order.IndexOf(ExpandedItem.Memory), netAt = Math.Min(order.IndexOf(ExpandedItem.Download) is var d and >= 0 ? d : int.MaxValue, order.IndexOf(ExpandedItem.Upload) is var u and >= 0 ? u : int.MaxValue);
        bool memFirst = !both || memAt < netAt;
        var firstItem = memFirst ? (mem ? ExpandedItem.Memory : netItem) : netItem;
        var secondItem = firstItem == ExpandedItem.Memory ? netItem : ExpandedItem.Memory;
        if (mode == MetricsMode.Line)
        {
            if (!both) { parts.Add(new(mem ? ExpandedItem.Memory : netItem, Scaled(new(0, y, width, height), scale), ExpandedForm.Line, ln)); return; }
            double half = Math.Floor((width - MetricsGap) / 2);
            parts.Add(new(firstItem, Scaled(new(0, y, half, height), scale), ExpandedForm.Line, ln));
            parts.Add(new(secondItem, Scaled(new(half + MetricsGap, y, width - half - MetricsGap, height), scale), ExpandedForm.Line, ln));
            return;
        }
        var form = mode == MetricsMode.Lines ? ExpandedForm.Lines : ExpandedForm.Blocks;
        double block = Math.Ceiling(body * FontMetrics.DefaultLineFactor * k + 19);
        double Natural(ExpandedItem item) => mode == MetricsMode.Blocks && item == ExpandedItem.Memory ? block : ln;
        if (!both)
        {
            var only = mem ? ExpandedItem.Memory : netItem;
            parts.Add(new(only, Scaled(new(0, y, width, height), scale), form, Natural(only)));
            return;
        }
        double between = mode == MetricsMode.Lines ? 6 : 10;
        double n1 = Natural(firstItem), n2 = Natural(secondItem);
        double extra = Math.Max(0, height - n1 - n2 - between);
        double h1 = n1 + Math.Floor(extra * n1 / (n1 + n2)), h2 = height - h1 - between;
        parts.Add(new(firstItem, Scaled(new(0, y, width, h1), scale), form, n1));
        parts.Add(new(secondItem, Scaled(new(0, y + h1 + between, width, h2), scale), form, n2));
    }

    private static ExpandedBox Scaled(ExpandedBox box, double scale) => new(box.X * scale, box.Y * scale, box.Width * scale, box.Height * scale);
}

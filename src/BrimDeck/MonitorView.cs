using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// A monitor color scheme as the panel draws it on the panel theme in use: ftop's palettes in their light or dark variant by the
// theme, or the theme's own colors (its accent and its cyan, pink and lime), or the custom colors over the scheme they start
// from, each moved just far enough to read on the theme's surface. The page is built again when the theme changes
// (Look.Generation is part of its key). The warning and critical colors are the collapsed readings' own settings, or the
// panel theme's.
internal static class MonitorThemeColors
{
    // look: the look of the panel the colors are drawn on; the main look by default (the settings window's previews).
    public static MonitorColors For(DeckSettings settings, PanelLook? look = null) => For(settings.MonitorPalette, settings.MonitorCustomColors, look) with
    {
        Warning = Warning(settings, look), Critical = Critical(settings, look)
    };
    // They color the readings' values too, so they are held to the text minimum (PanelThemes.LevelContrast).
    public static string Warning(DeckSettings settings, PanelLook? look = null)
    { var Look = look ?? PanelTheme.Main; return Drawn(Look, settings.CompactWarningColor ?? Look.WarningDefault, PanelThemes.LevelContrast); }
    public static string Critical(DeckSettings settings, PanelLook? look = null)
    { var Look = look ?? PanelTheme.Main; return Drawn(Look, settings.CompactCriticalColor ?? Look.CriticalDefault, PanelThemes.LevelContrast); }

    public static MonitorColors For(MonitorPalette palette, MonitorCustomColors custom, PanelLook? look = null)
    {
        var Look = look ?? PanelTheme.Main;
        string Shown(string color) => Drawn(Look, color, PanelThemes.GraphicContrast);
        if (palette == MonitorPalette.Custom)
            return custom.Over(For(custom.Base == MonitorPalette.Custom ? MonitorPalette.Theme : custom.Base, custom, Look), Shown);
        var theme = Look.Current;
        if (palette == MonitorPalette.Theme)
            return MonitorColors.FromTheme(Shown(theme["accent"].ToString()),
                [.. new[] { ClockColor.Cyan, ClockColor.Pink, ClockColor.Lime, ClockColor.Amber }.Select(color => Shown(theme.ClockPreset(color).ToString()))],
                Shown(Look.WarningDefault), Shown(Look.CriticalDefault));
        return MonitorColors.For(palette, theme.IsDark).Map(Shown);
    }
    // Opaque "#RRGGBB" as drawn on the theme, which ColorMath needs for the core groups' shades and for telling colors apart.
    private static string Drawn(PanelLook look, string color, double minimum) => ThemeColor.Opaque(look.Readable(ThemeColor.Opaque(color), minimum));

    public static StripColors Strip(DeckSettings settings, PanelLook? look = null) => new(For(settings, look));
}

// The system monitor page, after ftop's panel: the main gauge large with its columns (the processor's cores, or the graphics
// adapter's engines), the other gauge small beside it (a click on either swaps them), memory and network below, and the
// busiest apps beside or under the readings. Every part draws itself; Update hands each its new reading. With more than one
// graphics adapter the large GPU gauge names the shown adapter's kind and a click on it shows the next adapter.
internal sealed class MonitorView : Grid
{
    // The look of the panel this element is drawn in (PanelLook.Of); the main look outside a panel.
    private PanelLook Look => PanelLook.Of(this);
    private readonly MonitorLayout _layout;
    private readonly MonitorColors _colors;
    private readonly MonitorMain _main;
    // Only what the plan has a place for is built.
    private readonly FigureGauge? _cpuGauge, _gpuGauge;
    private readonly ColumnGraph? _cpuGraph, _gpuGraph;
    private readonly BlockGauge? _gpuBlock;
    private readonly MemoryBlock? _memory;
    private readonly NetworkBlock? _network;
    private readonly StorageBlock? _storage;
    private readonly PowerBlock? _power;
    private readonly FramesBlock? _frames;
    // The frame-rate line's low percentiles and frame time follow the settings page's toggles (MonitorFps1Low/MonitorFps01Low/
    // MonitorFpsFrameTime); the panel copies them over before every update.
    public bool Fps1Low { get; set; }
    public bool Fps01Low { get; set; }
    public bool FpsFrameTime { get; set; }
    public bool FpsGpuTime { get; set; }
    private readonly bool _temperatures;
    private readonly Grid _readings = new();
    // How far the readings stop above the page's foot, so their last line ends level with the list's last row (AlignFoot).
    private readonly FootAlignment _foot = new();
    private readonly ProcessListView? _list;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private ProcessCard? _card;
    private ProcessList? _processes;
    private int _cores = Environment.ProcessorCount;
    private bool _motion;
    // The adapter a click on the large GPU gauge shows next; null with one adapter.
    private GpuSample? _nextGpu;

    // plan: the layout of what the page shows (ExpandedLayout): only the readings it has a place for are built, so what is not chosen
    // costs nothing here. The bands (the gauges with their columns, memory and network, the frame rate, the drives, power) stack in the
    // plan's order. temperatures: the gauges carry the processor's and the graphics adapter's temperatures after their figures, and
    // the drives' entries theirs (the layout's StorageLines and StorageColumns are the places kept for the volumes under memory and
    // network); smallTemperature: the gauge shown small may have a temperature, so its line is widened for one.
    public MonitorView(ExpandedPlan plan, MonitorColors colors, MonitorMain main, ProcessSort sort, MonitorProcessView processView,
        Action<MonitorMain> mainChanged, Action<ProcessSort> sortChanged, Action<bool> hold, Action<GpuSample> gpuChanged, bool temperatures = false, bool smallTemperature = false,
        PowerOptions? power = null, FrameworkElement? message = null, CoreDisplay coreDisplay = CoreDisplay.Auto)
    {
        var layout = plan.Steps;
        _layout = layout; _colors = colors; _temperatures = temperatures;
        _main = main;
        // Nothing to show: the message (nothing chosen, or nothing readable), and nothing built or read.
        if (plan.Kind is ExpandedKind.Empty or ExpandedKind.Unavailable)
        {
            if (message is not null) Children.Add(message);
            return;
        }
        bool hasCpu = plan.Part(ExpandedItem.Cpu) is not null, hasGpu = plan.Part(ExpandedItem.Gpu) is not null, pair = hasCpu && hasGpu;
        // A lone gauge is the large one whatever the setting says.
        bool cpuMain = hasCpu && (!hasGpu || main == MonitorMain.Cpu);
        _main = cpuMain ? MonitorMain.Cpu : MonitorMain.Gpu;
        string swapToGpu = Loc.T("切换为以 GPU 为主", "Show the GPU large"), swapToCpu = Loc.T("切换为以 CPU 为主", "Show the CPU large");
        string cores = Loc.T("CPU 各核心占用", "CPU usage by core");
        var readings = _readings;
        // The bands, each with the kind that decides its place and its height: the gauges' band takes what the others leave.
        var bands = new List<(ExpandedItem Kind, FrameworkElement Element)>();

        if (hasCpu || hasGpu)
        {
            var band = new Grid();
            band.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            band.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 16 });
            // The gauge shown small takes a fixed width at the right, the same for either: a block for the GPU, thin columns for the
            // processor. Its line is wider than the column under it, and wider still with a temperature after the figure.
            double mini = Math.Round(Math.Clamp(layout.Figure * 3.4, 54, 84));
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            if (pair)
            {
                double headMini = Math.Max(mini, temperatures && smallTemperature ? SmallLine(layout, gpu: cpuMain) : Math.Round(layout.Figure * 4.6));
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(headMini) });
            }
            if (hasCpu) _cpuGauge = new FigureGauge("C", colors.Performance, cpuMain ? layout.Figure : layout.Body + 1, layout.Caption, right: !cpuMain, button: cpuMain ? null : swapToCpu);
            if (hasGpu) _gpuGauge = new FigureGauge("G", colors.Gpu, cpuMain ? layout.Body + 1 : layout.Figure, layout.Caption, right: cpuMain, button: cpuMain ? swapToGpu : null);
            var big = cpuMain ? _cpuGauge! : _gpuGauge!; var small = pair ? (cpuMain ? _gpuGauge : _cpuGauge) : null;
            head.Children.Add(big);
            if (small is not null)
            {
                small.VerticalAlignment = VerticalAlignment.Center;
                SetColumn(small, 1); head.Children.Add(small);
                small.Clicked += () => mainChanged(cpuMain ? MonitorMain.Gpu : MonitorMain.Cpu);
            }
            if (!cpuMain) _gpuGauge!.Clicked += () => { if (_nextGpu is { } next) gpuChanged(next); };
            band.Children.Add(head);

            var graphs = new Grid { Margin = new Thickness(0, layout.CompactMetrics ? 4 : 6, 0, layout.CompactMetrics ? 6 : 10) };
            graphs.ColumnDefinitions.Add(new ColumnDefinition());
            if (pair)
            {
                graphs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                graphs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(mini) });
            }
            if (cpuMain)
            {
                // Fill the main graph's width even on CPUs with few cores. The fill is the
                // load, the tick the clock, and pointing at a core gives both.
                _cpuGraph = new ColumnGraph(double.PositiveInfinity, layout.Caption, labels: false, name: cores, display: coreDisplay);
                graphs.Children.Add(_cpuGraph);
                if (pair)
                {
                    _gpuBlock = new BlockGauge(colors.Gpu, swapToGpu, layout.Caption);
                    _gpuBlock.Clicked += () => mainChanged(MonitorMain.Gpu);
                    SetColumn(_gpuBlock, 2); graphs.Children.Add(_gpuBlock);
                }
            }
            else
            {
                // The engines spread over the room the graph has, so a wide page leaves no gap between them and the processor's columns.
                _gpuGraph = new ColumnGraph(EngineColumn, layout.Caption, !layout.CompactMetrics, gap: 6, name: Loc.T("GPU 各引擎占用", "GPU usage by engine"));
                graphs.Children.Add(_gpuGraph);
                if (pair)
                {
                    _cpuGraph = new ColumnGraph(6, layout.Caption, false, gap: 1.5, button: swapToCpu, name: cores, display: coreDisplay);
                    _cpuGraph.Clicked += () => mainChanged(MonitorMain.Cpu);
                    SetColumn(_cpuGraph, 2); graphs.Children.Add(_cpuGraph);
                }
            }
            SetRow(graphs, 1); band.Children.Add(graphs);
            bands.Add((ExpandedItem.Cpu, band));
        }

        bool hasMemory = plan.Part(ExpandedItem.Memory) is not null, hasNetwork = plan.Downloads || plan.Uploads;
        if (hasMemory || hasNetwork)
        {
            if (hasMemory) _memory = new MemoryBlock(colors, layout.Metrics != MetricsMode.Blocks, layout.Body, layout.Caption);
            if (hasNetwork) _network = new NetworkBlock(colors, layout.Metrics, layout.Body, layout.Caption, plan.Downloads, plan.Uploads, plan.UploadFirst);
            // In the table's order: whichever of memory and the network comes first is first.
            var order = plan.Parts.Select(part => part.Item).ToList();
            bool memoryFirst = !hasNetwork || hasMemory && order.FindIndex(item => item == ExpandedItem.Memory) < order.FindIndex(item => item is ExpandedItem.Download or ExpandedItem.Upload);
            var first = (FrameworkElement)(memoryFirst ? _memory! : _network!);
            var second = hasMemory && hasNetwork ? (FrameworkElement)(memoryFirst ? _network! : _memory!) : null;
            var metrics = new Grid();
            if (layout.Metrics == MetricsMode.Line)
            {
                metrics.ColumnDefinitions.Add(new ColumnDefinition());
                metrics.Children.Add(first);
                if (second is not null)
                {
                    metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); metrics.ColumnDefinitions.Add(new ColumnDefinition());
                    SetColumn(second, 2); metrics.Children.Add(second);
                }
            }
            else
            {
                metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                metrics.Children.Add(first);
                if (second is not null)
                {
                    metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    second.Margin = new Thickness(0, layout.Metrics == MetricsMode.Blocks ? 10 : 6, 0, 0);
                    SetRow(second, 1); metrics.Children.Add(second);
                }
            }
            bands.Add((ExpandedItem.Memory, metrics));
        }
        // Right under what is above, at the drives' distance; one line of a drive line's height.
        if (plan.Part(ExpandedItem.Frames) is not null && layout.FrameLine)
        {
            _frames = new FramesBlock(colors[MonitorRole.Frames], layout.Body, layout.Caption, layout.StorageRow);
            bands.Add((ExpandedItem.Frames, _frames));
        }
        if (plan.Part(ExpandedItem.Storage) is not null && layout.StorageLines > 0)
        {
            // Its lines are tighter than the list's rows.
            _storage = new StorageBlock(colors, layout.Body, layout.Caption, layout.StorageRow, layout.StorageLines, layout.StorageColumns, temperatures);
            bands.Add((ExpandedItem.Storage, _storage));
        }
        if (plan.Part(ExpandedItem.Power) is not null && layout.PowerLines > 0 && power is not null)
        {
            // One line of a list row's height.
            _power = new PowerBlock(colors, power, layout.Body, layout.Caption, layout.StorageRow);
            bands.Add((ExpandedItem.Power, _power));
        }
        // The bands in the plan's order, each a row; the gauges' band takes what the others leave, and a page without one keeps its
        // bands together in the middle of the room.
        var planned = plan.Parts.Select(part => part.Item).ToList();
        int PlanIndex(ExpandedItem kind) => planned.FindIndex(item => kind switch
        {
            ExpandedItem.Cpu => item is ExpandedItem.Cpu or ExpandedItem.Gpu,
            ExpandedItem.Memory => item is ExpandedItem.Memory or ExpandedItem.Download or ExpandedItem.Upload,
            _ => item == kind
        });
        bands.Sort((a, b) => PlanIndex(a.Kind).CompareTo(PlanIndex(b.Kind)));
        double lineGap = layout.Metrics switch { MetricsMode.Line => 2, MetricsMode.Lines => 4, _ => 8 };
        for (int i = 0; i < bands.Count; i++)
        {
            bool flex = bands[i].Kind == ExpandedItem.Cpu;
            readings.RowDefinitions.Add(new RowDefinition { Height = flex ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            // At the lines' distance from a band that is not the gauges' (whose own margin keeps the distance).
            if (i > 0 && bands[i - 1].Kind != ExpandedItem.Cpu) bands[i].Element.Margin = new Thickness(0, lineGap, 0, 0);
            SetRow(bands[i].Element, i); readings.Children.Add(bands[i].Element);
        }
        if (!bands.Any(band => band.Kind == ExpandedItem.Cpu)) readings.VerticalAlignment = VerticalAlignment.Center;

        if (plan.List == ExpandedList.Full)
        {
            // The list alone: it fills the page.
            _list = new ProcessListView(layout, sort, processView, sortChanged, hold, Open);
            Children.Add(_list);
        }
        else if (layout.List == ListPlacement.None) { if (bands.Count > 0) Children.Add(readings); }
        else
        {
            _list = new ProcessListView(layout, sort, processView, sortChanged, hold, Open);
            if (layout.List == ListPlacement.Side)
            {
                // The list keeps its share of the width within its bounds; the readings keep at least 260 DIP.
                ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 260 });
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MonitorLayout.ListShare / (1 - MonitorLayout.ListShare), GridUnitType.Star),
                    MinWidth = MonitorLayout.ListMinimum, MaxWidth = MonitorLayout.ListMaximum });
                SetColumn(_list, 2);
                // The readings end where the list's last row ends, so both columns share their foot.
                SizeChanged += (_, _) => AlignFoot();
            }
            else
            {
                RowDefinitions.Add(new RowDefinition { Height = new GridLength(MonitorLayout.StackedReadings) });
                RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
                RowDefinitions.Add(new RowDefinition());
                SetRow(_list, 2);
            }
            Children.Add(readings); Children.Add(_list);
        }
        SetColumnSpan(_overlay, 3); SetRowSpan(_overlay, 3);
        Children.Add(_overlay);
    }

    // A press anywhere in the window outside the card and the list's rows closes the card: the page's blank room, the list's own
    // blank room and its sort buttons, the window's top bar. The click still reaches what it was meant for. On a row the row
    // decides (Open): its own row closes the card, another row opens its own.
    public void WindowPressed()
    {
        if (_card is not null && !_card.IsMouseOver && _list?.PointerOnRow != true) CloseCard(animate: true);
    }
    // Whether the page has the power line (the layout had room for it): the power is read only then.
    public bool ShowsPower => _power is not null;
    // Whether the page has the frame rate's line (the layout had room for it): the frame rate is read for it only then.
    public bool ShowsFrames => _frames is not null;
    // Another monitor on the frame rate's line, without building the page again.
    // The widest a graphics engine's column grows on the page with the GPU large: no narrower than the graph's room needs, so the
    // few kinds of engine an adapter has always fill it.
    public const double EngineColumn = 1000;

    // The small gauge's widest line with a temperature, "(G) 100 % 独显 105 °C" (the kind only for the graphics adapter, when its
    // temperature is another adapter's), as FigureGauge lays it out (its dial, the gaps, the figure in the display face, the unit
    // and the detail), so no reading is ever cut.
    private static double SmallLine(MonitorLayout layout, bool gpu)
    {
        double figure = layout.Body + 1, gap = Math.Round(figure * .4);
        double Width(string text, double size, bool display = false) => MonitorDraw.Text(text, size, PanelTheme.Main.Primary, 1, display: display).WidthIncludingTrailingWhitespace;
        string hottest = Temperatures.Detail(Temperatures.Highest - 20);
        double detail = gpu ? Math.Max(Width(Temperatures.AdapterKind(0) + " " + hottest, layout.Caption), Width(Temperatures.AdapterKind(ulong.MaxValue) + " " + hottest, layout.Caption))
            : Width(hottest, layout.Caption);
        return Math.Ceiling(Math.Round(figure + 3) + gap + Width("100", figure, display: true) + 2 + Width("%", layout.Caption) + gap + detail + 2);
    }

    // The list beside the readings fills whole rows from its top, so its last row usually ends above the page's foot; the readings
    // stop there too, their graph giving up the difference. During a resize the foot holds and the readings stretch with it; once
    // the size settles the foot eases to the list's, as the page's values ease.
    private void AlignFoot()
    {
        if (_list is null || ActualHeight <= 0) return;
        var (rows, _) = MonitorLayout.ListGrid(_list.ActualWidth, ActualHeight, _layout.RowHeight);
        double now = Eased.Now;
        // While the page is being resized the foot holds, and follows the list once the size settles (FootAlignment).
        _foot.Resize(rows > 0 ? ActualHeight - MonitorLayout.ListHead - rows * _layout.RowHeight : 0, now);
        PlaceFoot(now);
        Motion.Start(this, frame => { bool moving = _foot.Step(frame, _motion); PlaceFoot(frame); return moving; });
    }
    private void PlaceFoot(double now) => _readings.Margin = new Thickness(0, 0, 0, Math.Round(_foot.At(now)));

    public void Update(MonitorSnapshot snapshot, bool motion)
    {
        _motion = motion;
        _cores = Math.Max(1, snapshot.LogicalProcessors);
        bool cpuMain = _main == MonitorMain.Cpu;
        if (_cpuGauge is null) { }
        else if (snapshot.Cpu.TryGet(out var cpu))
        {
            string frequency = cpu.FrequencyMhz.TryGet(out var mhz) ? MonitorFormat.Gigahertz(mhz) + " GHz" : "";
            // The zone is the firmware's, which may be the board near the processor rather than the processor itself.
            double? heat = _temperatures ? cpu.Temperature : null;
            string temperature = heat is { } celsius ? Temperatures.Format(celsius) + " °C" : "";
            _cpuGauge.Set(cpu.Usage, MonitorFormat.Percent(cpu.Usage), "%", temperature,
                "CPU " + MonitorFormat.Percent(cpu.Usage) + "%" + (frequency.Length > 0 ? " · " + frequency : "") + Loc.T($" · {cpu.Cores.Count} 个逻辑处理器", $" · {cpu.Cores.Count} logical processors")
                + (temperature.Length > 0 ? "\n" + TemperatureLine(cpu, temperature) : ""),
                motion, heat is { } c ? Hot(Temperatures.OfCpu(c)) : null);
            int groups = Math.Max(1, cpu.GroupCount);
            _cpuGraph?.Set(cpu.Cores.Select(core => new GraphColumn(core.Usage, cpuMain ? core.FrequencyFraction : null, _colors.Core(core.Group, groups), core.Group, Describe(core), Hot: _colors.Level(ReadingLevels.OfShare(core.Usage), _colors.Core(core.Group, groups)))).ToList(), motion);
        }
        else { _cpuGauge.Set(null, "—", "", "", Loc.T("暂无读数：", "No reading: ") + snapshot.Cpu.Reason, motion); _cpuGraph?.Set([], motion); }

        if (_gpuGauge is null) { }
        else if (snapshot.Gpu.TryGet(out var gpu))
        {
            string memory = gpu.DedicatedBytes.TryGet(out var used)
                ? Loc.T("显存 ", "VRAM ") + MonitorFormat.Gigabytes(used) + (gpu.DedicatedTotal is { } total ? " / " + MonitorFormat.GigabytesCompact(total) : "") + " GB" : "";
            // With more than one adapter each is named with its kind, and the large gauge says which kind it shows.
            bool several = snapshot.Gpus.Count > 1;
            _nextGpu = several && !cpuMain ? GpuChoice.Next(snapshot.Gpus, gpu) : null;
            double? heat = _temperatures ? gpu.Temperature : null;
            string temperature = heat is { } celsius ? Temperatures.Format(celsius) + " °C" : "";
            // An adapter without engine readings at the moment (NoReadings) shows a dash and says so; it can still be switched from.
            bool idle = gpu.NoReadings;
            // The usage says where it was read when its driver gave it; the engines' columns are always Windows' own.
            string usage = MonitorFormat.Percent(gpu.Usage) + "%" + (gpu.UsageSource is { } source ? Loc.T($"（{source}）", $" ({source})") : "");
            string tip = (several ? GpuChoice.Describe(gpu.Name, gpu.Kind) : gpu.Name ?? "GPU") + " · " + (idle ? Loc.T("暂无读数", "No reading") : usage) + (memory.Length > 0 ? " · " + memory : "")
                + (gpu.SharedBytes.TryGet(out var shared) ? Loc.T(" · 共享 ", " · shared ") + MonitorFormat.Gigabytes(shared) + " GB" : "")
                + (temperature.Length > 0 ? " · " + temperature + (gpu.TemperatureFrom is { } from ? Loc.T($"（{from} 的温度）", $" ({from}'s temperature)") : "") : "");
            string switchTo = _nextGpu is { } next ? Loc.T("点击切换到 ", "Click to show ") + GpuChoice.Describe(next.Name, next.Kind) : "";
            if (!cpuMain) _gpuGauge.Button = switchTo.Length > 0 ? Loc.T("切换显卡", "Switch GPU") : null;
            // The adapter's name and its memory are in the tooltip and in the block's line, not spelled out beside the figure. After the
            // figure: the adapter's kind where there is more than one ("独显"), and its temperature, "独显 · 48 °C"; a temperature read
            // from another adapter (this one has no sensor) carries that adapter's kind instead.
            string kind = several && !cpuMain ? GpuChoice.KindName(gpu.Kind) : "";
            string heatText = temperature.Length > 0 && gpu.TemperatureKind is { } from2 ? from2 + " " + temperature : temperature;
            string detail = kind.Length > 0 && heatText.Length > 0 ? kind + " · " + heatText : kind + heatText;
            // The memory in use, in the large gauge's line after the temperature (the small one's room is kept for the temperature; its
            // block carries the memory), in its own level's color as in the collapsed row, not the temperature's.
            var memoryShare = !cpuMain && !idle ? gpu.DedicatedFraction : null;
            string memoryText = memoryShare is { } m ? Loc.T("显存 ", "VRAM ") + MonitorFormat.Percent(m) + "%" : "";
            _gpuGauge.Set(idle ? null : gpu.Usage, idle ? "—" : MonitorFormat.Percent(gpu.Usage), idle ? "" : "%", detail, switchTo.Length > 0 ? tip + "\n" + switchTo : tip, motion,
                heat is { } c ? Hot(Temperatures.OfGpu(c)) : null, memoryText, memoryShare is { } level ? Hot(ReadingLevels.OfShare(level)) : null);
            _gpuBlock?.Set(idle ? null : gpu.Usage, idle ? null : gpu.DedicatedFraction, tip, motion);
            _gpuGraph?.Set(gpu.Engines.Select(engine => new GraphColumn(engine.Usage, null, _colors.Gpu, 0,
                GpuEngines.Label(engine.Kind) + " · " + MonitorFormat.Percent(engine.Usage) + "%", GpuEngines.Short(engine.Kind))).ToList(), motion);
        }
        else
        {
            string reason = Loc.T("暂无读数：", "No reading: ") + snapshot.Gpu.Reason;
            _nextGpu = null;
            if (!cpuMain) _gpuGauge.Button = null;
            _gpuGauge.Set(null, "—", "", "", reason, motion);
            _gpuBlock?.Set(null, null, reason, motion);
            _gpuGraph?.Set([], motion);
        }

        // The main gauge carries the last minute's usage in the room after its figure.
        if ((cpuMain ? _cpuGauge : _gpuGauge) is { } large) large.Trend = cpuMain ? snapshot.CpuHistory : snapshot.GpuHistory;
        _memory?.Set(snapshot.Memory, motion);
        _network?.Set(snapshot.Network, snapshot.DownHistory, snapshot.UpHistory);
        _storage?.Set(snapshot.Storage, motion);
        _power?.Set(snapshot, motion);
        _frames?.Set(snapshot, Fps1Low, Fps01Low, FpsFrameTime, FpsGpuTime);
        _list?.Set(snapshot.Processes, _cores, motion);
        _processes = snapshot.Processes.OrDefault;
        // An open card follows its app's readings; it closes when the app has left the list.
        if (_card is not null)
        {
            var sample = _processes?.Top.Concat(_processes.ByMemory).FirstOrDefault(s => s.Id == _card.Id);
            if (sample is null) CloseCard(animate: true); else _card.Show(sample, _cores, _layout.Body, _layout.Caption);
        }
    }

    // A temperature's or the graphics memory share's color: the text's while normal, the collapsed readings' warning or critical color above.
    private string? Hot(MemoryLevel level) => level == MemoryLevel.Normal ? null : _colors.Level(level, Look.Tertiary);

    // The processor's temperature with where it was read: its own sensors (with each core's range, or each CCD's), a hardware
    // monitor, or an ACPI zone, which may be the board near the processor.
    private static string TemperatureLine(CpuSample cpu, string temperature)
    {
        string cores = cpu.CoreTemperatures.Count == 0 ? ""
            : cpu.CoreTemperaturesAreCcds ? " · CCD " + string.Join(" / ", cpu.CoreTemperatures.Select(Temperatures.Format)) + " °C"
            : Loc.T(" · 各核心 ", " · cores ") + Temperatures.Format(cpu.CoreTemperatures.Min()) + "–" + Temperatures.Format(cpu.CoreTemperatures.Max()) + " °C";
        return cpu.TemperatureSource switch
        {
            CpuTemperatureSource.Processor => Loc.T($"温度 {temperature}（处理器传感器）", $"Temperature {temperature} (processor sensors)") + cores,
            CpuTemperatureSource.HwInfo => Loc.T($"温度 {temperature}（HWiNFO）", $"Temperature {temperature} (HWiNFO)"),
            CpuTemperatureSource.LibreHardwareMonitor => Loc.T($"温度 {temperature}（LibreHardwareMonitor）", $"Temperature {temperature} (LibreHardwareMonitor)"),
            _ => Loc.T($"温度 {temperature}（ACPI 热区 {cpu.TemperatureZone}，可能是主板而非 CPU 本身）", $"Temperature {temperature} (ACPI thermal zone {cpu.TemperatureZone}, possibly the board rather than the CPU itself)")
        };
    }

    private static string Describe(CoreSample core)
    {
        string kind = core.Tier switch
        {
            CoreTier.Performance => Loc.T("性能核", "Performance core"), CoreTier.Efficiency => Loc.T("能效核", "Efficiency core"), _ => Loc.T("逻辑处理器", "Logical processor")
        };
        string frequency = core.FrequencyMhz.TryGet(out var mhz) ? " · " + MonitorFormat.Gigahertz(mhz) + " GHz" : "";
        return $"{kind} {core.Number} · {MonitorFormat.Percent(core.Usage)}%{frequency}";
    }

    // Opens an app's card beside its row: to the left of a list at the right, otherwise just right of the row's start.
    private void Open(ProcessSample sample, Rect row)
    {
        if (_list is null) return;
        if (_card?.Id == sample.Id) { CloseCard(animate: true); return; }
        CloseCard(animate: true);
        var card = new ProcessCard(_layout.Body, _layout.Caption, Look);
        card.Show(sample, _cores, _layout.Body, _layout.Caption);
        card.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseCard(animate: true); };
        card.Switched += () => CloseCard(animate: true);
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var origin = _list.TranslatePoint(new Point(row.X, row.Y), this);
        double width = card.DesiredSize.Width, height = card.DesiredSize.Height;
        double x = _layout.List == ListPlacement.Side ? origin.X - width - 12 : Math.Min(origin.X + 36, ActualWidth - width);
        double y = Math.Clamp(origin.Y + row.Height / 2 - height / 2, 0, Math.Max(0, ActualHeight - height));
        Canvas.SetLeft(card, Math.Max(0, x)); Canvas.SetTop(card, y);
        _overlay.Children.Add(card); _overlay.IsHitTestVisible = true;
        _card = card;
        card.Appear(_motion);
    }

    // Asked by the user (or when the app leaves the list) the card fades away; when the page goes, it simply goes.
    public void CloseCard(bool animate = false)
    {
        if (_card is not { } card) return;
        _overlay.IsHitTestVisible = false; _card = null;
        if (animate && _motion) card.Leave(() => _overlay.Children.Remove(card));
        else _overlay.Children.Remove(card);
    }

    public void SetSort(ProcessSort sort) => _list?.SetSort(sort, _motion);
}

// The monitor page giving way to a new build of itself (a resize across a layout step, a sensor found or missing, a new main
// gauge): the new page fades in while every page it replaces fades out from where it is, so their opacities always add up to one
// and the page never dims, even when a drag crosses several steps in quick succession. Nothing jumps to its new place in one
// frame; the pages that leave take no input. Without animations the new page simply replaces them.
internal static class MonitorCrossfade
{
    private const double Seconds = Eased.Seconds * .6;

    public static void Swap(Panel host, FrameworkElement next, FrameworkElement old, bool motion)
    {
        if (!motion || !host.Children.Contains(old)) { host.Children.Clear(); host.Children.Add(next); return; }
        var leaving = host.Children.OfType<UIElement>().Select(child => (Element: child, From: child.Opacity)).ToList();
        foreach (var (element, _) in leaving) { element.IsHitTestVisible = false; element.BeginAnimation(UIElement.OpacityProperty, null); }
        // The opacities the leaving pages had, after any animation of their own was taken off.
        leaving = [.. leaving.Select(page => (page.Element, page.Element.Opacity))];
        next.Opacity = 0;
        host.Children.Add(next);
        var fade = new Eased(0, Seconds);
        fade.Set(1, true);
        Motion.Start(host, now =>
        {
            double t = Math.Clamp(fade.At(now), 0, 1);
            next.Opacity = t;
            foreach (var (element, from) in leaving) element.Opacity = from * (1 - t);
            if (fade.Moving(now)) return true;
            next.Opacity = 1;
            foreach (var (element, _) in leaving) host.Children.Remove(element);
            return false;
        });
    }
}

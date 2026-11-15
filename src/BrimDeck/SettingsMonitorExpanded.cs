using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The expanded monitor page's choices ("展开时"): switches for what the page shows, the quick sets, and under them a sketch of the
// page with a block for each part it keeps together (ExpandedMonitor.Blocks: CPU and GPU, memory with the network, the frame rate,
// the drives, power), stacked as the page stacks them. Dragging a block, or Alt+Up/Down on it, moves that part on the page. The
// process list is drawn beside them and does not move: the page's size places it.
public sealed partial class SettingsWindow
{
    private const double SketchGap = 6;

    private static string ExpandedName(ExpandedItem item) => item switch
    {
        ExpandedItem.Gpu => "GPU", ExpandedItem.Memory => Loc.T("内存", "Memory"), ExpandedItem.Download => Loc.T("下载", "Download"), ExpandedItem.Upload => Loc.T("上传", "Upload"),
        ExpandedItem.Frames => Loc.T("帧率", "Frame rate"), ExpandedItem.Storage => Loc.T("硬盘", "Drives"), ExpandedItem.Power => Loc.T("功耗与电池", "Power and battery"),
        ExpandedItem.Processes => Loc.T("进程列表", "Process list"), _ => "CPU"
    };

    private static string ExpandedHelp(ExpandedItem item) => item switch
    {
        ExpandedItem.Cpu => Loc.T("CPU 总占用和各核心的占用。", "The CPU's total usage and each core's."),
        ExpandedItem.Gpu => Loc.T("GPU 占用和各引擎的占用。", "The GPU's usage and each engine's."),
        ExpandedItem.Memory => Loc.T("内存占用与提交量。", "Memory in use and the commit charge."),
        ExpandedItem.Download => Loc.T("下载速率。", "The download rate."),
        ExpandedItem.Upload => Loc.T("上传速率。", "The upload rate."),
        ExpandedItem.Frames => Loc.T("前台程序的帧率、程序名称和所在显示器的刷新率。首次开启时需要授权。",
            "The frame rate of the program in front, with its name and its display's refresh rate. Needs permission the first time."),
        ExpandedItem.Storage => Loc.T("不含 U 盘、移动硬盘、网络盘和光驱。", "USB, external, network and optical drives are not included."),
        ExpandedItem.Power => Loc.T("整机功耗和本机电池。", "System power and this PC's battery."),
        _ => Loc.T("按 CPU 或内存列出占用最高的应用。位置随面板大小，在右侧或下方。", "Lists the apps using the most CPU or memory. The panel's size places it, at the side or below.")
    };

    // The scheme's colors of an item, one dot each: the CPU's fastest and slowest cores, the others one.
    private static MonitorRole[] ExpandedRoles(ExpandedItem item) => item switch
    {
        ExpandedItem.Cpu => [MonitorRole.Cpu, MonitorRole.Efficiency], ExpandedItem.Gpu => [MonitorRole.Gpu], ExpandedItem.Memory => [MonitorRole.Memory],
        ExpandedItem.Download => [MonitorRole.Download], ExpandedItem.Upload => [MonitorRole.Upload], ExpandedItem.Frames => [MonitorRole.Frames],
        ExpandedItem.Storage => [MonitorRole.Storage], ExpandedItem.Power => [MonitorRole.Power, MonitorRole.Battery], _ => []
    };

    private static ExpandedReading ExpandedOf(DeckSettings settings, ExpandedItem item) => settings.ExpandedReadings.First(reading => reading.Item == item);

    // Changes the table and shows the page. The list is the settings' own or, before one is set, the one the old switches give.
    private void EditExpanded(Action<List<ExpandedReading>> edit)
        => Resize(s => { var list = ExpandedMonitor.Normalize(s.ExpandedReadings); edit(list); s.ExpandedReadings = list; }, DeckPage.Monitor);

    private Action? _refreshExpanded;

    // The rows of the "展开时" group that choose what the page shows: the chips and the sketch of the page.
    private (FrameworkElement Chips, FrameworkElement Sketch) ExpandedOptions()
    {
        bool syncing = false;
        var refresh = new List<Action>();
        void Refresh() { syncing = true; try { foreach (var update in refresh) update(); } finally { syncing = false; } }
        _refreshExpanded = Refresh;
        void Edit(Action<List<ExpandedReading>> edit) { if (syncing) return; EditExpanded(edit); Refresh(); }

        // What the page shows: a chip for each item, five to a line, the temperatures last (a detail of the CPU, the GPU and the drives).
        var cells = new UniformGrid { Columns = 5, Margin = new Thickness(0, 8, -8, 4) };
        foreach (var item in ExpandedMonitor.Order) cells.Children.Add(ExpandedChip(item, refresh, Edit));
        var heat = PickChip(Loc.T("温度", "Temperatures"), [],
            Loc.T("CPU、GPU 和硬盘的温度。读不到的温度不显示。CPU 温度由主板固件提供，许多电脑没有这项数据；有的电脑读到的是主板的温度，不是 CPU 本身的温度。",
                "The CPU's, the GPU's and the drives' temperatures. Temperatures that cannot be read are not shown. The CPU temperature comes from the motherboard firmware. Many PCs do not provide it, and on some it is the motherboard's temperature rather than the CPU's."),
            refresh);
        heat.Checked += (_, _) => { if (!syncing) Resize(s => s.MonitorTemperatures = true, DeckPage.Monitor); };
        heat.Unchecked += (_, _) => { if (!syncing) Resize(s => s.MonitorTemperatures = false, DeckPage.Monitor); };
        refresh.Add(() => heat.IsChecked = S.MonitorTemperatures);
        cells.Children.Add(heat);
        var title = SettingLabel(Loc.T("显示的内容", "What the page shows"), null);
        title.Margin = new Thickness(0, 12, 0, 0);
        var switches = new StackPanel();
        switches.Children.Add(title);
        switches.Children.Add(cells);

        var sketch = ExpandedSketch(refresh);
        Refresh();
        return (KeepPreview(switches), sketch);
    }

    // A chip that is on or off: a dot in each of its colors (one neutral dot without any), its name, its help on hover.
    private ToggleButton PickChip(string name, MonitorRole[] roles, string help, List<Action> refresh)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var fills = (roles.Length > 0 ? roles.Length : 1) is var count ? Enumerable.Range(0, count).Select(channel => new Ellipse { Width = 8, Height = 8, Margin = new Thickness(channel == 0 ? 0 : 3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }).ToArray() : [];
        foreach (var fill in fills) content.Children.Add(fill);
        content.Children.Add(new TextBlock { Text = name, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        refresh.Add(() =>
        {
            var colors = MonitorThemeColors.For(S);
            for (int channel = 0; channel < fills.Length; channel++) fills[channel].Fill = UI.Brush(roles.Length > 0 ? RoleColor(colors, roles[channel]) : TextTertiary);
        });
        var chip = new ToggleButton { Style = (Style)FindResource("SettingsPickChip"), Content = content, Margin = new Thickness(0, 0, 8, 8), ToolTip = help };
        AutomationProperties.SetName(chip, Loc.T("展开时显示 ", "Show when expanded: ") + name); AutomationProperties.SetHelpText(chip, help);
        return chip;
    }

    // One item's chip: on shows it on the page.
    private ToggleButton ExpandedChip(ExpandedItem item, List<Action> refresh, Action<Action<List<ExpandedReading>>> edit)
    {
        var chip = PickChip(ExpandedName(item), ExpandedRoles(item), ExpandedHelp(item), refresh);
        chip.Checked += (_, _) =>
        {
            bool was = ExpandedOf(S, item).Visible;
            edit(list => list.First(reading => reading.Item == item).Visible = true);
            // Only a frame rate the user turns on asks for permission.
            if (item == ExpandedItem.Frames && !was) AskFrameAccess();
        };
        chip.Unchecked += (_, _) => { edit(list => list.First(reading => reading.Item == item).Visible = false); if (item == ExpandedItem.Frames) FrameAccessChanged?.Invoke(); };
        refresh.Add(() => chip.IsChecked = ExpandedOf(S, item).Visible);
        return chip;
    }

    // The shown items of a block in the table's order, as its block in the sketch names them.
    private static string BlockName(IReadOnlyList<ExpandedItem> block, IReadOnlyList<ExpandedReading> readings)
        => string.Join(" + ", readings.Where(reading => reading.Visible && ExpandedMonitor.BlockOf(reading.Item) == block).Select(reading => ExpandedName(reading.Item)));

    // Each block's height in the sketch: the gauges are the tallest part of the page, memory and the network next.
    private static double BlockHeight(IReadOnlyList<ExpandedItem> block) => block[0] switch { ExpandedItem.Cpu => 64, ExpandedItem.Memory => 52, _ => 32 };

    // The text of a block or a row of the sketch, in the accent.
    private static TextBlock SketchText(string name)
    {
        var text = new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "SettingsAccentText");
        return text;
    }
    // A row inside the memory and network block: on a deeper wash of the accent than the block's, without a frame of its own, tall
    // enough to take hold of.
    private static Border SketchRow(FrameworkElement child)
    {
        var row = new Border { CornerRadius = new CornerRadius(5), Height = 30, Child = child, Background = Brushes.Transparent };
        row.SetResourceReference(Border.BackgroundProperty, "SettingsAccentTintStrong");
        return row;
    }

    // Two parts side by side (or one above the other: vertical); dragging either past the middle of the other changes their places
    // (swap). As in the list of blocks, the other part makes way while the pointer is past its middle, and the dragged one settles in
    // its new place before the sketch is built again.
    // escape: where the drag leaves the pair (up or down for side by side parts, past the top or the bottom for parts one above the
    // other), it is handed on: side by side parts go back; parts one above the other that have changed places stay so, and the change
    // is handed on with the drag (pending), to be saved when the block is let go. starters: each part with how to start dragging it from
    // a pointer pressed inside it, for a part inside to hand its drag on to it.
    private Grid SketchPair(FrameworkElement first, FrameworkElement second, Action swap, bool vertical = false, Action<MouseEventArgs, Action?>? escape = null,
        Action<FrameworkElement, Action<MouseEventArgs>>? starters = null)
    {
        var pair = new Grid { Background = Brushes.Transparent };
        // Where the press was, so a part that takes over a drag follows the pointer from there rather than from where it is now.
        Point pressed = default;
        pair.PreviewMouseLeftButtonDown += (_, e) => pressed = e.GetPosition(pair);
        if (vertical)
        {
            pair.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); pair.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SketchGap) });
            pair.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(second, 2);
        }
        else
        {
            pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SketchGap) }); pair.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(second, 2);
        }
        pair.Children.Add(first); pair.Children.Add(second);
        var axis = vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        var moves = new Dictionary<FrameworkElement, TranslateTransform> { [first] = new(), [second] = new() };
        first.RenderTransform = moves[first]; second.RenderTransform = moves[second];
        double Along(MouseEventArgs e) { var at = e.GetPosition(pair); return vertical ? at.Y : at.X; }
        double Size(FrameworkElement part) => vertical ? part.ActualHeight : part.ActualWidth;
        double Offset(FrameworkElement part) => (double)moves[part].GetValue(axis);
        void Place(FrameworkElement part, double offset) { moves[part].BeginAnimation(axis, null); moves[part].SetValue(axis, offset); }
        void Slide(FrameworkElement part, double to, Action? done = null)
        {
            double from = Offset(part);
            if (!Soft.Enabled || Math.Abs(from - to) < .1) { Place(part, to); done?.Invoke(); return; }
            var motion = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(Soft.Choose)) { EasingFunction = Soft.Out };
            if (done is not null) motion.Completed += (_, _) => done();
            moves[part].BeginAnimation(axis, motion);
        }
        bool settling = false;
        void Drag(FrameworkElement part, FrameworkElement other, int direction)
        {
            part.Cursor = Cursors.SizeAll;
            double start = double.NaN;
            Point down = default;
            bool past = false;
            double Reach() => Size(other) + SketchGap;
            void Stop(bool swapped)
            {
                if (double.IsNaN(start)) return;
                start = double.NaN;
                if (part.IsMouseCaptured) part.ReleaseMouseCapture();
                if (swapped)
                {
                    // The other part has made way already; the dragged one finishes its move, then the sketch is built in the new order.
                    settling = true;
                    Slide(part, direction * Reach(), swap);
                    return;
                }
                Slide(part, 0, () => Panel.SetZIndex(part, 0)); Slide(other, 0);
            }
            void Begin(MouseEventArgs e)
            {
                if (settling) return;
                Place(part, Offset(part)); Place(other, Offset(other));
                down = pressed; start = (vertical ? down.Y : down.X) - Offset(part); past = false; Panel.SetZIndex(part, 2);
                if (!part.CaptureMouse()) { start = double.NaN; Panel.SetZIndex(part, 0); return; }
                if (e is not MouseButtonEventArgs) Follow(e);
            }
            starters?.Invoke(part, Begin);
            part.MouseLeftButtonDown += (_, e) => { e.Handled = true; Begin(e); };
            void Follow(MouseEventArgs e)
            {
                var at = e.GetPosition(pair);
                bool leaves = vertical ? at.Y < -8 || at.Y > pair.ActualHeight + 8
                    : Math.Abs(at.Y - down.Y) > 6 && Math.Abs(at.Y - down.Y) > Math.Abs(at.X - down.X);
                if (escape is not null && leaves)
                {
                    start = double.NaN;
                    Action? pending = null;
                    if (vertical && past) { settling = true; pending = swap; }
                    else { Slide(part, 0, () => Panel.SetZIndex(part, 0)); Slide(other, 0); }
                    past = false;
                    escape(e, pending);
                    return;
                }
                double moved = Along(e) - start;
                Place(part, direction > 0 ? Math.Clamp(moved, 0, Reach()) : Math.Clamp(moved, -Reach(), 0));
                bool now = Math.Abs(Offset(part)) > Reach() / 2;
                if (now != past) { past = now; Slide(other, now ? -direction * (Size(part) + SketchGap) : 0); }
            }
            part.MouseMove += (_, e) =>
            {
                if (double.IsNaN(start) || !part.IsMouseCaptured) return;
                Follow(e);
                e.Handled = true;
            };
            part.MouseLeftButtonUp += (_, e) => { if (!double.IsNaN(start)) { e.Handled = true; Stop(past); } };
            // LostMouseCapture bubbles: only the part's own loss ends its drag, not a part inside it handing its drag on.
            part.LostMouseCapture += (_, e) => { if (e.OriginalSource == part) Stop(false); };
        }
        Drag(first, second, 1); Drag(second, first, -1);
        return pair;
    }

    // The inside of the memory and network block when it shows more than one thing, as the page shows it at its usual sizes: memory
    // on a row and the network on the next (a very short page puts the two on one line, in the same order), the two rates halves of
    // the network's row. Every part is a whole row's height to take hold of. Dragged across, the two rates change sides; dragged up or
    // down, a rate takes the network's row along. Either row dragged up or down changes places with the other inside the block, and
    // dragged on out of it takes the whole block (grip), the change kept. swap: Memory to change memory and the network round,
    // Download the rates.
    private FrameworkElement MetricsInside(IReadOnlyList<ExpandedReading> readings, Action<ExpandedItem> swap, SketchGrip grip)
    {
        var shown = readings.Where(reading => reading.Visible && ExpandedMonitor.BlockOf(reading.Item)[0] == ExpandedItem.Memory).Select(reading => reading.Item).ToList();
        var rates = shown.Where(item => item != ExpandedItem.Memory).ToList();
        // A rate dragged up or down takes the network's row, which changes places with memory inside the block and, dragged on out
        // of it, takes the whole block; without memory, the rate takes the block at once.
        Action<MouseEventArgs>? row = null;
        FrameworkElement network = rates.Count == 1 ? SketchRow(SketchText(ExpandedName(rates[0])))
            : SketchPair(SketchRow(SketchText(ExpandedName(rates[0]))), SketchRow(SketchText(ExpandedName(rates[1]))), () => swap(ExpandedItem.Download),
                escape: (e, _) => { if (row is not null) row(e); else grip.Start?.Invoke(e, null); });
        if (!shown.Contains(ExpandedItem.Memory)) return network;
        var memory = SketchRow(SketchText(ExpandedName(ExpandedItem.Memory)));
        void Starters(FrameworkElement part, Action<MouseEventArgs> begin) { if (part == network) row = begin; }
        return shown[0] == ExpandedItem.Memory
            ? SketchPair(memory, network, () => swap(ExpandedItem.Memory), vertical: true, escape: (e, pending) => grip.Start?.Invoke(e, pending), starters: Starters)
            : SketchPair(network, memory, () => swap(ExpandedItem.Memory), vertical: true, escape: (e, pending) => grip.Start?.Invoke(e, pending), starters: Starters);
    }

    // The sketch of the page: the shown blocks in the page's order, the process list beside them. Built again when what it shows
    // changes; a drag reorders its blocks in place.
    private FrameworkElement ExpandedSketch(List<Action> refresh)
    {
        var host = new Border { CornerRadius = new CornerRadius(8), Background = UI.Brush(Inset), Padding = new Thickness(8, 8, 8, 8 - SketchGap), Margin = new Thickness(0, 8, 0, 12) };
        string built = "";
        // Puts the page back in the default order; grey while it is in it already. What is shown or hidden stays as it is.
        Button? restore = null;
        void Restorable() { if (restore is null) return; bool moved = !ExpandedMonitor.IsDefaultOrder(S.ExpandedReadings); restore.IsEnabled = moved; restore.Opacity = moved ? 1 : .4; }
        restore = ActionButton(Loc.T("恢复默认顺序", "Restore the default order"), () => { Change(s => s.ExpandedReadings = ExpandedMonitor.InDefaultOrder(s.ExpandedReadings)); Build(); Restorable(); });
        restore.Style = (Style)FindResource("SettingsLink"); restore.VerticalAlignment = VerticalAlignment.Center;
        refresh.Add(Restorable);
        void Build()
        {
            var readings = ExpandedMonitor.Normalize(S.ExpandedReadings);
            var blocks = ExpandedMonitor.BlocksIn(readings).Where(block => block[0] != ExpandedItem.Processes && BlockName(block, readings).Length > 0).ToList();
            bool processes = ExpandedOf(S, ExpandedItem.Processes).Visible;
            string key = string.Join("|", blocks.Select(block => BlockName(block, readings))) + "|" + processes + "|" + Loc.Language;
            if (key == built) return;
            built = key;
            var grid = new Grid();
            if (blocks.Count == 0 && !processes)
            {
                var none = TextLine(Loc.T("没有选择任何内容，展开页会显示提示。", "Nothing is chosen; the expanded page says so."), 12, TextTertiary);
                none.HorizontalAlignment = HorizontalAlignment.Center; none.VerticalAlignment = VerticalAlignment.Center;
                grid.Height = 64; grid.Margin = new Thickness(0, 0, 0, SketchGap); grid.Children.Add(none);
                host.Child = grid;
                return;
            }
            var stack = new StackPanel();
            void Swap(ExpandedItem item) { Change(s => s.ExpandedReadings = ExpandedMonitor.Swap(s.ExpandedReadings, item)); Build(); Restorable(); }
            foreach (var block in blocks)
            {
                // Memory and the network show their parts when there is more than one, to be changed round by dragging.
                var grip = new SketchGrip();
                var inside = block[0] == ExpandedItem.Memory && BlockName(block, readings).Contains('+') ? MetricsInside(readings, Swap, grip) : null;
                stack.Children.Add(SketchBlock(block, BlockName(block, readings), inside, grip, stack,
                    order => { built = SketchKey(order, readings, processes); Change(s => s.ExpandedReadings = ExpandedMonitor.Arrange(s.ExpandedReadings, order)); Restorable(); }));
            }
            if (blocks.Count > 0) { grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.Children.Add(stack); }
            if (processes)
            {
                // The process list: dashed, as it has no fixed place; alone it takes the page.
                var list = new Grid { Margin = new Thickness(blocks.Count > 0 ? 8 : 0, 0, 0, SketchGap), ToolTip = ExpandedHelp(ExpandedItem.Processes) };
                if (blocks.Count == 0) list.Height = 64;
                var outline = new Rectangle { RadiusX = 6, RadiusY = 6, StrokeThickness = 1, StrokeDashArray = [4, 3], Stroke = UI.Brush(TextTertiary) };
                var name = TextLine(ExpandedName(ExpandedItem.Processes), 12.5, TextSecondary);
                name.HorizontalAlignment = HorizontalAlignment.Center; name.VerticalAlignment = VerticalAlignment.Center;
                list.Children.Add(outline); list.Children.Add(name);
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = blocks.Count > 0 ? new GridLength(Loc.IsEnglish ? 140 : 120) : new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(list, grid.ColumnDefinitions.Count - 1); grid.Children.Add(list);
            }
            host.Child = grid;
        }
        refresh.Add(Build);

        var title = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        title.ColumnDefinitions.Add(new ColumnDefinition()); title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(SettingLabel(Loc.T("页面顺序", "Order on the page"), null));
        Grid.SetColumn(restore, 1); title.Children.Add(restore);
        var panel = new StackPanel();
        panel.Children.Add(title); panel.Children.Add(host);
        return KeepPreview(panel);
    }

    private static string SketchKey(IEnumerable<ExpandedItem> order, IReadOnlyList<ExpandedReading> readings, bool processes)
        => string.Join("|", order.Select(item => BlockName(ExpandedMonitor.BlockOf(item), readings))) + "|" + processes + "|" + Loc.Language;

    // How a part inside a block hands its drag on to the block: the block sets Start once it is made.
    // pending: a change made inside the block on the way (memory and the network changed round), saved when the block is let go.
    private sealed class SketchGrip { public Action<MouseEventArgs, Action?>? Start; }

    // A block of the sketch, in the accent's tint and outline, named by what it shows (or showing its parts: inside). moved: the shown
    // blocks' new order (each named by its first item), after a drag or Alt+Up/Down.
    private Border SketchBlock(IReadOnlyList<ExpandedItem> block, string name, FrameworkElement? inside, SketchGrip? grip, StackPanel stack, Action<List<ExpandedItem>> moved)
    {
        var label = new TextBlock { Text = name, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "SettingsAccentText");
        var view = new GripHandle
        {
            Height = inside is null ? BlockHeight(block) : double.NaN, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Padding = new Thickness(inside is null ? 0 : 5),
            Margin = new Thickness(0, 0, 0, SketchGap), Child = inside ?? label, Tag = block[0], Cursor = Cursors.SizeAll, Focusable = true,
            FocusVisualStyle = (Style)FindResource("SettingsFocus"), RenderTransform = new TranslateTransform()
        };
        view.SetResourceReference(Border.BackgroundProperty, "SettingsAccentTint");
        view.SetResourceReference(Border.BorderBrushProperty, "SettingsAccentText");
        AutomationProperties.SetName(view, Loc.T($"{name}：拖动，或按 Alt+↑/↓ 调整顺序", $"{name}: drag, or press Alt+Up/Down, to move it"));
        List<ExpandedItem> Order() => [.. stack.Children.OfType<Border>().Select(child => (ExpandedItem)child.Tag)];

        Border[] blocks = []; double[] tops = [], heights = [];
        // A change made inside the block on its way out of it (memory and the network changed round), saved when the block is let go.
        Action? pending = null;
        int from = -1, target = -1; double grab = 0;
        void Measure()
        {
            blocks = [.. stack.Children.OfType<Border>()];
            heights = [.. blocks.Select(child => child.ActualHeight)];
            tops = new double[blocks.Length];
            for (int i = 1; i < blocks.Length; i++) tops[i] = tops[i - 1] + heights[i - 1] + SketchGap;
        }
        // The others' places with the dragged block at slot `to`.
        void Arrange(int to)
        {
            var others = blocks.Where((_, i) => i != from).ToList();
            others.Insert(to, blocks[from]);
            double y = 0;
            foreach (var other in others)
            {
                int i = Array.IndexOf(blocks, other);
                if (i != from) AnimateRow(other, y - tops[i]);
                y += heights[i] + SketchGap;
            }
        }
        void End(bool commit)
        {
            if (from < 0) return;
            int start = from, end = target; from = -1;
            // A change made inside the block on the way is saved after the block's own move, once it has settled.
            var after = pending; pending = null;
            if (view.IsMouseCaptured) view.ReleaseMouseCapture();
            if (commit && end != start)
            {
                double shown = tops[start] + RowTransform(view).Y;
                var others = blocks.Where((_, i) => i != start).ToList();
                stack.Children.Remove(view); stack.Children.Insert(end, view);
                double settled = others.Take(end).Sum(other => heights[Array.IndexOf(blocks, other)] + SketchGap);
                foreach (var other in others) SetRowOffset(other, 0);
                SetRowOffset(view, shown - settled);
                // The block's place is saved first: the change inside it (after) builds the sketch again from the settings, and may run
                // at once (no motion), so it must find the block's new place already there.
                moved(Order());
                AnimateRow(view, 0, () => { Panel.SetZIndex(view, 0); after?.Invoke(); });
                return;
            }
            foreach (var other in blocks) AnimateRow(other, 0);
            Panel.SetZIndex(view, 0);
            after?.Invoke();
        }
        // Takes the whole block, pressed on it or carried on from a part inside it (grip). Held where it was pressed: a part that hands
        // its drag on after the pointer has moved, the block jumps to the pointer and follows it, rather than trailing behind.
        double press = 0;
        view.PreviewMouseLeftButtonDown += (_, e) => press = e.GetPosition(stack).Y;
        void Begin(MouseEventArgs e)
        {
            Measure();
            from = target = Array.IndexOf(blocks, view);
            if (from < 0 || blocks.Length < 2) { from = -1; return; }
            grab = press - tops[from] - RowTransform(view).Y;
            view.Focus(); Panel.SetZIndex(view, 2);
            if (!view.CaptureMouse()) { from = -1; Panel.SetZIndex(view, 0); }
        }
        if (grip is not null) grip.Start = (e, change) => { Begin(e); if (from >= 0) pending = change; else change?.Invoke(); Follow(e); };
        view.MouseLeftButtonDown += (_, e) => { Begin(e); e.Handled = true; };
        void Follow(MouseEventArgs e)
        {
            if (from < 0 || !view.IsMouseCaptured) return;
            double top = Math.Clamp(e.GetPosition(stack).Y - grab, 0, tops[^1] + heights[^1] - heights[from]);
            SetRowOffset(view, top - tops[from]);
            // A block above stays above until the dragged block's top passes its middle; a block below moves above once the dragged
            // block's bottom passes its middle. (Their middles alone kept a tall block from ever passing a short last one.)
            int to = Enumerable.Range(0, blocks.Length).Count(i => i < from ? top >= tops[i] + heights[i] / 2 : i > from && top + heights[from] > tops[i] + heights[i] / 2);
            if (to != target) { target = to; Arrange(to); }
        }
        view.MouseMove += (_, e) => Follow(e);
        view.MouseLeftButtonUp += (_, e) => { if (from >= 0) { End(true); e.Handled = true; } };
        // LostMouseCapture bubbles: a part inside handing its drag on to the block loses the capture to it, and that must not end the
        // block's drag that has just begun. Only the block's own loss ends it.
        view.LostMouseCapture += (_, e) => { if (e.OriginalSource == view) End(false); };
        view.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Alt || e.Key != Key.System || e.SystemKey is not (Key.Up or Key.Down)) return;
            e.Handled = true;
            int at = stack.Children.IndexOf(view), to = at + (e.SystemKey == Key.Up ? -1 : 1);
            if (to < 0 || to >= stack.Children.Count) return;
            stack.Children.Remove(view); stack.Children.Insert(to, view);
            view.Focus();
            moved(Order());
        };
        return view;
    }
}

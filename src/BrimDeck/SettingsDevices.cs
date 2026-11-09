using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The devices page's settings: what the collapsed row shows of the devices (a preview of it, whether it shows them, how and in
// what order), the level at which a battery shows as low, and the list of devices, read while this page is open: each with a
// handle to reorder it, its name (click to rename it) and where it shows, in the collapsed row and on the page. A device the
// user changed anything for is remembered (DeviceLists.Remember) and stays listed, dimmed, while it is not connected, with a
// button to forget it. Whether the page shows, and its size, are set with the other pages under 展开外观.
public partial class SettingsWindow
{
    private const int DevicesSettingsPage = 12;
    private const double DeviceRowHeight = 56;

    private void DevicesSettings()
    {
        // The devices are read while this page is open, so the list is current even with the panel collapsed.
        var holder = new Border();
        holder.Loaded += (_, _) => _app.WatchDevicesForSettings(true);
        holder.Unloaded += (_, _) => _app.WatchDevicesForSettings(false);
        // The window listens once, and lets go when it closes, unloaded or not; the rows refresh only while this page shows.
        if (!_devicesClosing)
        {
            _devicesClosing = true;
            _app.DevicesChanged += RefreshDeviceRows;
            Closed += (_, _) => { _app.DevicesChanged -= RefreshDeviceRows; _app.WatchDevicesForSettings(false); };
        }
        Target.Children.Add(holder);

        // Whether the notch and the capsule show the devices is set with their other parts (收起外观 › 显示内容).
        SectionTitle(Loc.T("收起时", "When collapsed"));
        _devicePreview = new Border { Height = 96, CornerRadius = new CornerRadius(8), Background = UI.Brush(Inset), BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 12) };
        Target.Children.Add(_devicePreview);
        FillDevicePreview();
        Group(Toggle(Loc.T("显示数字", "Show figures"), S.DeviceFigures, value => { Change(s => s.DeviceFigures = value); FillDevicePreview(); }));

        // The thresholds color the page and the collapsed row alike. Getting low stays above low: its choices start above it.
        SectionTitle(Loc.T("电量阈值", "Battery thresholds"));
        var warning = new ComboBox { Width = 110, Style = (Style)FindResource("SettingsCombo") };
        var low = new ComboBox { Width = 110, Style = (Style)FindResource("SettingsCombo"), ItemsSource = Devices.LowChoices.Select(percent => $"{percent}%").ToList(),
            SelectedIndex = Math.Max(0, Devices.LowChoices.ToList().IndexOf(S.DeviceLowPercent)) };
        bool filling = false;
        void FillWarning()
        {
            filling = true;
            var choices = Devices.WarningChoices.Where(percent => percent > S.DeviceLowPercent).ToList();
            warning.ItemsSource = choices.Select(percent => $"{percent}%").ToList();
            warning.SelectedIndex = Math.Max(0, choices.IndexOf(S.DeviceWarningPercent));
            filling = false;
        }
        FillWarning();
        AutomationProperties.SetName(warning, Loc.T("预警阈值", "Warning threshold")); PopupMotion.Attach(warning);
        AutomationProperties.SetName(low, Loc.T("告警阈值", "Critical threshold")); PopupMotion.Attach(low);
        warning.SelectionChanged += (_, _) =>
        {
            if (filling || warning.SelectedIndex < 0) return;
            int percent = Devices.WarningChoices.Where(choice => choice > S.DeviceLowPercent).ElementAt(warning.SelectedIndex);
            Change(s => s.DeviceWarningPercent = percent);
            FillDevicePreview();
        };
        low.SelectionChanged += (_, _) =>
        {
            if (low.SelectedIndex < 0) return;
            Change(s => { s.DeviceLowPercent = Devices.LowChoices[low.SelectedIndex]; s.Normalize(); });
            FillWarning(); FillDevicePreview();
        };
        Group(Row(Loc.T("预警阈值", "Warning threshold"), warning, help: () => Loc.T("电量不高于此值时显示为偏低，使用预警颜色。", "At or under this level a battery shows as getting low, in its warning color.")),
            Row(Loc.T("告警阈值", "Critical threshold"), low, help: () => Loc.T("电量不高于此值时显示为低电量，使用告警颜色。", "At or under this level a battery shows as low, in its critical color.")));

        SectionTitle(Loc.T("设备", "Devices"), help: () => Loc.T(
            "支持：Windows 读取的蓝牙电量（蓝牙耳机、低功耗蓝牙键鼠与笔，与 Windows 设置中显示的一致）；罗技 Unifying、Bolt、Lightspeed 接收器与有线设备；雷蛇无线鼠标；赛睿 Arctis 与 HyperX Cloud 无线耳机；ATK、VXE、VGN、迈从、Endgame Gear、VAXEE、英菲克的 2.4G 鼠标与键盘；Xbox 与 PlayStation 手柄；AirPods 与 Beats（左右耳与充电盒）；以及带标准 HID 电量的设备。只读取，不会配对、断开或改动设备与系统设置。其他品牌的 2.4G 接收器协议未公开，切到蓝牙模式即可显示。",
            "Supported: the Bluetooth battery Windows reads (Bluetooth headphones and Bluetooth LE keyboards, mice and pens, as Windows Settings shows it); Logitech Unifying, Bolt and Lightspeed receivers and cabled devices; Razer wireless mice; SteelSeries Arctis and HyperX Cloud wireless headsets; 2.4 GHz mice and keyboards by ATK, VXE, VGN, MCHOSE, Endgame Gear, VAXEE and Inphic; Xbox and PlayStation controllers; AirPods and Beats (each bud and the case); and devices with the standard HID battery usages. Read only: nothing is paired, disconnected or changed on the devices or in Windows. Other brands' 2.4 GHz receivers do not publish their protocol; in Bluetooth mode they show."));
        var headings = DeviceColumns(); headings.Height = 34;
        AddHeadings(headings, new[]
        {
            (Loc.T("名称", "Name"), 2, ""), (Loc.T("主题", "Theme"), 4, Loc.T("电量正常时圆环的颜色。", "The ring's color while the battery is normal.")),
            (Loc.T("充电", "Charging"), 5, Loc.T("充电时圆环的颜色。", "The ring's color while charging.")),
            (Loc.T("预警", "Warning"), 6, Loc.T("电量不高于预警阈值时的颜色。", "The color at or under the warning threshold.")),
            (Loc.T("告警", "Critical"), 7, Loc.T("电量不高于告警阈值时的颜色。", "The color at or under the critical threshold.")),
            (Loc.T("收起时", "Collapsed"), 8, ""), (Loc.T("展开页", "Page"), 9, "")
        });
        var nameHeading = headings.Children.OfType<FrameworkElement>().First(heading => Grid.GetColumn(heading) == 2);
        nameHeading.HorizontalAlignment = HorizontalAlignment.Left; nameHeading.Margin = new Thickness(4, 0, 6, 0);
        _deviceRows = new StackPanel();
        var table = new StackPanel();
        table.Children.Add(headings);
        table.Children.Add(new Border { Child = _deviceRows, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0) });
        Target.Children.Add(new Border { Child = table, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 0, 0, 28) });
        _deviceRowsKey = "";
        FillDeviceRows(_deviceRows);
    }
    private StackPanel? _deviceRows;
    private Border? _devicePreview;
    private bool _devicesClosing;
    private void RefreshDeviceRows()
    {
        if (_page != DevicesSettingsPage) return;
        if (_deviceRows is not null) FillDeviceRows(_deviceRows);
        FillDevicePreview();
    }
    // What the rows were built for (each device's key, name and switches, and whether it is connected), and each row's detail line
    // and ring by key, so a new reading only changes those and the switches keep the keyboard's focus.
    private string _deviceRowsKey = "";
    private readonly Dictionary<string, (TextBlock Detail, DeviceRing Ring)> _deviceDetails = [];

    private static Grid DeviceColumns()
    {
        var grid = new Grid { Background = Brushes.Transparent };
        // The handle, the ring, the name and the line under it, the forget button, the four colors, the two switches.
        var swatch = SwatchColumn;
        foreach (var width in new[] { new GridLength(28), new GridLength(44), new GridLength(1, GridUnitType.Star), GridLength.Auto, swatch, swatch, swatch, swatch, new GridLength(68), new GridLength(64) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        return grid;
    }

    // The collapsed row's devices as they would show, in a capsule of the panel's own colors.
    private void FillDevicePreview()
    {
        if (_devicePreview is null) return;
        var all = _app.Devices?.All ?? [];
        var devices = DeviceLists.Collapsed(all, S.DeviceChoices);
        if (devices.Count == 0)
        {
            var words = TextLine(_app.Devices?.Settled == true ? Loc.T("没有要显示的设备", "No devices to show") : Loc.T("正在查找设备…", "Looking for devices…"), 12, TextSecondary);
            words.HorizontalAlignment = HorizontalAlignment.Center; words.VerticalAlignment = VerticalAlignment.Center;
            _devicePreview.Child = words;
            return;
        }
        var inks = new DeviceInks(MonitorThemeColors.For(S), S.DeviceChoices, S.DeviceWarningPercent, S.DeviceLowPercent);
        var strip = new DeviceStrip(new([.. devices.Select(device => StripDevice.Of(device, inks))], S.DeviceFigures), 1);
        strip.Width = strip.PreferredWidth;
        var capsule = new Border { Height = 32, CornerRadius = new CornerRadius(16), Background = UI.Brush(PanelTheme.Hex("surface")), Padding = new Thickness(14, 4, 14, 4),
            Child = strip, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, LayoutTransform = new ScaleTransform(1.5, 1.5) };
        AutomationProperties.SetName(capsule, Loc.T("收起时的预览", "Collapsed preview"));
        _devicePreview.Child = capsule;
    }

    // A row per device read now, and one per device remembered that is not connected.
    private void FillDeviceRows(StackPanel rows)
    {
        var all = _app.Devices?.All ?? [];
        var listed = DeviceLists.Remember(all, S.DeviceChoices);
        string key = string.Join("|", listed.Select(choice => $"{choice.Key}={choice.Name}={choice.Label}={choice.Collapsed}={choice.Expanded}={choice.Color}={choice.ChargeColor}={choice.WarningColor}={choice.CriticalColor}={all.Any(device => device.Key == choice.Key)}"))
            + "#" + (_app.Devices?.Settled == true);
        if (key == _deviceRowsKey && rows.Children.Count > 0)
        {
            foreach (var device in all)
                if (_deviceDetails.TryGetValue(device.Key, out var parts)) { parts.Detail.Text = DeviceDetail(device); parts.Ring.Show(device); }
            return;
        }
        _deviceRowsKey = key;
        rows.Children.Clear(); _deviceDetails.Clear();
        if (listed.Count == 0)
        {
            var empty = TextLine(_app.Devices?.Settled == true ? Loc.T("没有已连接的无线设备。连接后会自动出现在这里。", "No wireless devices connected. They show here once connected.")
                : Loc.T("正在查找设备…", "Looking for devices…"), 12.5, TextSecondary);
            empty.Margin = new Thickness(0, 14, 0, 14); rows.Children.Add(empty);
            return;
        }
        foreach (var choice in listed) rows.Children.Add(DeviceRow(choice, all.FirstOrDefault(device => device.Key == choice.Key), rows));
        UpdateDividers(rows);
    }

    private Border DeviceRow(DeviceChoice choice, DeviceBattery? device, StackPanel rows)
    {
        string name = choice.Label ?? choice.Name;
        if (name.Length == 0) name = choice.Key;
        var row = DeviceColumns();
        var handle = new GripHandle { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        var ring = new DeviceRing(TextPrimary, TextTertiary, Divider) { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        if (device is not null) ring.Show(device); else ring.Show(choice.Kind);
        Grid.SetColumn(ring, 1); row.Children.Add(ring);

        // The name, which a click turns into a box: Enter or leaving it keeps the new name, Esc the old; an empty name goes back to
        // the device's own.
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var title = new Button { Content = name, Style = (Style)FindResource("SettingsCardName"), HorizontalAlignment = HorizontalAlignment.Left, FontSize = 13,
            Foreground = UI.Brush(TextPrimary), Margin = new Thickness(-4, 0, 0, 0), ToolTip = Loc.T("单击重命名", "Click to rename") };
        AutomationProperties.SetName(title, Loc.T("重命名 ", "Rename ") + name);
        var box = new TextBox { Style = (Style)FindResource("SettingsField"), Height = 26, FontSize = 12.5, Padding = new Thickness(6, 0, 6, 0), Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, MaxLength = DeviceLists.MaximumLabel };
        AutomationProperties.SetName(box, Loc.T("设备名称", "Device name"));
        var detail = TextLine(device is null ? Loc.T("未连接", "Not connected") : DeviceDetail(device), 11.5, TextSecondary);
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        bool editing = false;
        void Finish(bool keep)
        {
            if (!editing) return;
            editing = false;
            string text = box.Text.Trim();
            box.Visibility = Visibility.Collapsed; title.Visibility = Visibility.Visible;
            detail.Text = device is null ? Loc.T("未连接", "Not connected") : DeviceDetail(device);
            if (!keep || text == name) return;
            string? label = text.Length == 0 || text == choice.Name ? null : text;
            EditDevices(choices => DeviceLists.Find(choices, choice.Key)!.Label = label);
        }
        title.Click += (_, _) =>
        {
            editing = true; box.Text = name;
            title.Visibility = Visibility.Collapsed; box.Visibility = Visibility.Visible;
            // The device's own name stays in sight while renaming it.
            if (choice.Name.Length > 0 && choice.Name != name) detail.Text = Loc.T("原名 ", "Was ") + choice.Name;
            box.Focus(); box.SelectAll();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        labels.Children.Add(title); labels.Children.Add(box); labels.Children.Add(detail);
        Grid.SetColumn(labels, 2); row.Children.Add(labels);

        // A device not connected can be forgotten: it leaves the list, and shows everywhere again if it comes back.
        if (device is null)
        {
            var forget = ActionButton(Loc.T("忘记", "Forget"), () => EditDevices(choices => choices.RemoveAll(other => other.Key == choice.Key)));
            forget.FontSize = 11; forget.VerticalAlignment = VerticalAlignment.Center; forget.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetName(forget, Loc.T("忘记 ", "Forget ") + name);
            Grid.SetColumn(forget, 3); row.Children.Add(forget);
            ring.Opacity = .55; labels.Opacity = .55;
        }

        CheckBox Switch(bool on, string label, Action<DeviceChoice, bool> set, int column)
        {
            var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, IsChecked = on };
            AutomationProperties.SetName(toggle, label + " " + name);
            toggle.Checked += (_, _) => EditDevices(choices => set(DeviceLists.Find(choices, choice.Key)!, true));
            toggle.Unchecked += (_, _) => EditDevices(choices => set(DeviceLists.Find(choices, choice.Key)!, false));
            Grid.SetColumn(toggle, column); row.Children.Add(toggle);
            return toggle;
        }
        // The device's four colors; "默认" returns one to the default (DeviceInks).
        var colors = MonitorThemeColors.For(S);
        void Swatch(string label, string? own, string standard, Action<DeviceChoice, string?> set, int column)
        {
            var button = ColorSwatch(name, label, own ?? standard, own is null, value => EditDevices(choices => set(DeviceLists.Find(choices, choice.Key)!, value.Length > 0 ? value : null)),
                applied: () => { });
            button.HorizontalAlignment = HorizontalAlignment.Center; button.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(button, column); row.Children.Add(button);
        }
        Swatch(Loc.T("主题", "theme"), choice.Color, UI.Primary, (c, value) => c.Color = value, 4);
        Swatch(Loc.T("充电", "charging"), choice.ChargeColor, DevicesView.Charging, (c, value) => c.ChargeColor = value, 5);
        Swatch(Loc.T("预警", "warning"), choice.WarningColor, colors.Warning, (c, value) => c.WarningColor = value, 6);
        Swatch(Loc.T("告警", "critical"), choice.CriticalColor, colors.Critical, (c, value) => c.CriticalColor = value, 7);
        // Dimmed, its choice kept, while neither the notch nor the capsule shows the devices.
        var collapsed = Switch(choice.Collapsed, Loc.T("收起时显示", "Show when collapsed"), (c, on) => c.Collapsed = on, 8);
        collapsed.Opacity = S.NotchDevices || S.CapsuleDevices ? 1 : .4;
        Switch(choice.Expanded, Loc.T("在展开页显示", "Show on the page"), (c, on) => c.Expanded = on, 9);

        if (device is not null) _deviceDetails[choice.Key] = (detail, ring);
        AttachDrag(handle, name, () => rows, DeviceRowHeight, list =>
        {
            var order = list.Children.OfType<Border>().Where(b => b.Tag is string).Select(b => (string)b.Tag).ToList();
            EditDevices(_ => { }, order);
        });
        return new Border { Child = row, Height = DeviceRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = choice.Key, RenderTransform = new TranslateTransform() };
    }

    // Saves a change to the list: every device read now and every one remembered, in the list's order (or the one given), changed
    // as asked; then shows it.
    private void EditDevices(Action<List<DeviceChoice>> edit, IEnumerable<string>? order = null)
    {
        var all = _app.Devices?.All ?? [];
        Change(s => { var choices = DeviceLists.Remember(all, s.DeviceChoices, order); edit(choices); s.DeviceChoices = choices; });
        // Out of the drag or the click that made it, so the row it came from is not replaced under the pointer.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshDeviceRows);
    }

    private static string DeviceDetail(DeviceBattery device) => string.Join(" · ", new[] { Devices.KindName(device.Kind), Devices.SourceName(device.Source),
        device.Parts.Count > 0 ? string.Join("  ", device.Parts.Select(part => Devices.PartName(part.Part) + " " + Devices.Figure(part.Percent)))
            : device.Lowest is null ? Loc.T("未报告电量", "No battery reported") : Devices.Figure(device.Lowest) }
        .Concat(device.Charging ? [Loc.T("充电中", "Charging")] : []));

    private void DeviceSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.DevicesWidth, 440, 1200, value => Resize(s => s.DevicesWidth = value, DeckPage.Devices)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.DevicesHeight, 140, 400, value => Resize(s => s.DevicesHeight = value, DeckPage.Devices)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
    }

    // A device's ring in the settings' colors: the charge as an arc on a track, the device's icon inside.
    internal sealed class DeviceRing(string ink, string faint, string track) : FrameworkElement
    {
        private DeviceKind _kind = DeviceKind.Other;
        private int? _percent;
        public void Show(DeviceBattery device) { _kind = device.Kind; _percent = device.Lowest; InvalidateVisual(); }
        public void Show(DeviceKind kind) { _kind = kind; _percent = null; InvalidateVisual(); }
        protected override void OnRender(DrawingContext drawing)
        {
            double size = Math.Min(ActualWidth, ActualHeight), stroke = 3, icon = 14;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            DevicesView.Ring(drawing, center, size, stroke, _percent is { } p ? Devices.Clamp(p) / 100.0 : null, ink, track);
            DeviceGlyphs.Draw(drawing, _kind, new Rect(center.X - icon / 2, center.Y - icon / 2, icon, icon), _percent is null ? faint : ink);
        }
    }
}

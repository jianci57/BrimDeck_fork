using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The devices' batteries in the collapsed notch or capsule (CompactPart.Devices), a part of the row of its own: each device a ring
// whose arc is its charge with the device's icon in it, then its figure when the settings show figures (DeckSettings.DeviceFigures).
// The ring takes the device's color for its state (DeviceInks); while charging a bolt sits on it at its lower right; with no reading
// the track alone, the icon faint and a dash. The icon tells the devices apart, so it stays whatever the state. It draws itself once
// per reading; a device more or fewer builds the row again (Holds), as it changes the part's width.
internal sealed class DeviceStrip : FrameworkElement
{
    private StripDevices _devices;
    private readonly double _scale, _font;

    public DeviceStrip(StripDevices devices, double scale)
    {
        _devices = devices;
        _scale = CompactMusicLayout.SafeScale(scale);
        _font = Math.Max(1, CompactMusicLayout.Half(11.5 * _scale));
        Height = Math.Max(1, CompactMusicLayout.Whole(24 * _scale));
        IsHitTestVisible = false;
    }

    private double Ring => Math.Max(4, CompactMusicLayout.Whole(20 * _scale));
    // Each device keeps the room of the widest figure ("100%"), so the part never moves with the readings.
    private double Slot => Ring + (_devices.Figures ? Math.Round(4 * _scale) + Math.Ceiling(MonitorDraw.Text("100%", _font, UI.Primary, 1).WidthIncludingTrailingWhitespace) : 0);
    private double Gap => Math.Round((_devices.Figures ? 8 : 6) * _scale);
    public double PreferredWidth => _devices.Devices.Count == 0 ? 0 : _devices.Devices.Count * Slot + (_devices.Devices.Count - 1) * Gap;

    // New readings, drawn at once; only for as many devices, with or without figures, as the strip was built for.
    public bool Holds(StripDevices devices) => devices.Devices.Count == _devices.Devices.Count && devices.Figures == _devices.Figures;
    public void SetDevices(StripDevices devices)
    {
        if (_devices.Same(devices)) return;
        _devices = devices;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double ppd = MonitorDraw.PixelsPerDip(this), center = Height / 2, ring = Ring, stroke = Math.Max(1, 1.75 * _scale), icon = Math.Round(11 * _scale);
        double x = Math.Max(0, Math.Round((ActualWidth - PreferredWidth) / 2));
        foreach (var device in _devices.Devices)
        {
            var middle = new Point(x + ring / 2, center);
            DevicesView.Ring(drawing, middle, ring, stroke, device.Percent is { } p ? Devices.Clamp(p) / 100.0 : null, device.Ring);
            if (device.Percent is null) drawing.PushOpacity(.5);
            DeviceGlyphs.Draw(drawing, device.Kind, new Rect(middle.X - icon / 2, middle.Y - icon / 2, icon, icon), UI.Primary);
            if (device.Percent is null) drawing.Pop();
            if (device.Charging) DevicesView.Badge(drawing, middle, (ring - stroke) / 2, Math.Round(9 * _scale), device.Ring);
            if (_devices.Figures)
            {
                var figure = MonitorDraw.Text(Devices.Figure(device.Percent), _font, device.Percent is null ? UI.Tertiary : device.Figure, ppd);
                MonitorDraw.Baseline(drawing, figure, x + ring + Math.Round(4 * _scale), Math.Round(center + _font * .36));
            }
            x += Slot + Gap;
        }
    }

    // For screen readers: each device, its charge and its state.
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new SpokenPeer(this, () => string.Join(Loc.T("；", "; "),
        _devices.Devices.Select(device => device.Name + " " + Devices.Figure(device.Percent) + (device.Charging ? Loc.T("，充电中", ", charging")
            : device.Level == MemoryLevel.Critical ? Loc.T("，低电量", ", low") : device.Level == MemoryLevel.Elevated ? Loc.T("，电量偏低", ", getting low") : ""))));
}

// A device as the collapsed row shows it: its name (spoken only), its kind for the icon, its lowest charge, whether it charges (only
// with a charge), its level, and its ring's and figure's colors (DeviceInks).
internal readonly record struct StripDevice(string Name, DeviceKind Kind, int? Percent, bool Charging, MemoryLevel Level, string Ring, string Figure)
{
    public static StripDevice Of(DeviceBattery device, DeviceInks inks)
        => new(device.Name, device.Kind, device.Lowest, device.Charging && device.Lowest is not null, inks.Level(device), inks.Ring(device), inks.Figure(device));
}
// The devices the collapsed row shows, and whether each shows its figure after its ring.
internal sealed record StripDevices(IReadOnlyList<StripDevice> Devices, bool Figures)
{
    public static readonly StripDevices None = new([], true);
    public bool Same(StripDevices other) => Figures == other.Figures && Devices.SequenceEqual(other.Devices);
}

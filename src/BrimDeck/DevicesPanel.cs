using System.Windows;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The devices page and the collapsed devices' reading. The sources (App.Devices) run while any panel shows either; every panel
// draws the same readings. As the monitor page, the page is built once per layout step (DevicesLayout), theme and language,
// stretches within a step and crossfades into a new build of itself (MonitorCrossfade); readings only change what it draws.
public partial class MainWindow
{
    private DevicesView? _devicesView;
    private string _devicesKey = "";
    private DevicesLayout? _devicesLayout;
    private bool _devicesShowing;

    private void InitializeDevices()
    {
        _app.DevicesChanged += DevicesChanged;
        Closed += (_, _) => { _app.DevicesChanged -= DevicesChanged; _app.PresentDevices(this, false, false); };
    }

    // The devices the page shows, in the settings' order and under their names (DeviceLists.Expanded). Made once per roster and
    // per settings (each change of either is a new object), as it is asked several times for each change.
    internal IReadOnlyList<DeviceBattery> ShownDevices
    {
        get
        {
            var all = _app.Devices?.All ?? [];
            var choices = Settings.DeviceChoices;
            if (!ReferenceEquals(all, _shownFrom.All) || !ReferenceEquals(choices, _shownFrom.Choices))
            {
                _shown = DeviceLists.Expanded(all, choices);
                _shownFrom = (all, choices);
            }
            return _shown;
        }
    }
    private IReadOnlyList<DeviceBattery> _shown = [];
    private (IReadOnlyList<DeviceBattery>? All, List<DeviceChoice>? Choices) _shownFrom;
    private bool DevicesSettled => _app.Devices?.Settled == true;

    // Each device's colors and levels as the settings give them.
    private DeviceInks Inks => new(MonitorThemeColors.For(Settings), Settings.DeviceChoices, Settings.DeviceWarningPercent, Settings.DeviceLowPercent);

    // Asked with the monitor's demand (UpdateMonitorDemand): whenever the panel opens, closes, changes page or is hidden, and
    // whenever the collapsed row is built.
    private void UpdateDevicesDemand()
    {
        bool seen = IsVisible && !IsSuppressed && !DisplayPower.Off;
        bool page = _expanded && CurrentPage == DeckPage.Devices && seen;
        // The collapsed part is read for while it is chosen, before it shows: it joins the row only once a device reports.
        bool strip = !_expanded && Settings.ShowsDevices(EffectiveStyle) && seen && _alert is null;
        _app.PresentDevices(this, page, strip);
        // Opened again, the page shows the readings as they are now, not as it left them.
        if (page && !_devicesShowing) { _devicesView?.Update(ShownDevices, DevicesSettled, motion: false); _app.Devices?.Refresh(); }
        _devicesShowing = page;
    }

    private void DevicesChanged()
    {
        if (!_expanded)
        {
            if (!Settings.ShowsDevices(EffectiveStyle)) return;
            // The part joins with the first charge reported and leaves with the last; a device more or fewer changes its width: the
            // row is built again for either. Otherwise the part takes the new readings in place.
            var devices = StripDevices();
            bool shows = devices.Devices.Any(device => device.Percent is not null);
            if (_compactDevices is { } strip && shows && strip.Holds(devices)) strip.SetDevices(devices);
            else if (shows || _compactDevices is not null) RenderCompact();
            return;
        }
        if (CurrentPage != DeckPage.Devices || _devicesView is null) return;
        if (DevicesKey() != _devicesKey) { RenderDevices(); return; }
        _devicesView.Update(ShownDevices, DevicesSettled, Soft.Enabled);
    }

    // The devices the collapsed row shows (DeviceLists.Collapsed), with their colors.
    private StripDevices CurrentStripDevices()
    {
        var inks = Inks;
        return new([.. DeviceLists.Collapsed(_app.Devices?.All ?? [], Settings.DeviceChoices).Select(device => StripDevice.Of(device, inks))], Settings.DeviceFigures);
    }
    // What the collapsed row shows: the devices as they are, kept while they are being listed again (the display came back on, a
    // suppression ended), so the part does not drop out for the moment the sources take to report.
    private StripDevices StripDevices()
    {
        var devices = CurrentStripDevices();
        if (devices.Devices.Any(device => device.Percent is not null) || DevicesSettled || _stripDevices.Figures != devices.Figures) _stripDevices = devices;
        return _stripDevices;
    }
    private StripDevices _stripDevices = BrimDeck.StripDevices.None;
    // The collapsed part as the row is built: none while the surface does not show it or no device reports a charge.
    private DeviceStrip? _compactDevices;
    private DeviceStrip? CompactDevices(double scale)
    {
        _compactDevices = null;
        if (!Settings.ShowsDevices(EffectiveStyle)) return null;
        var devices = StripDevices();
        return devices.Devices.Any(device => device.Percent is not null) ? _compactDevices = new DeviceStrip(devices, scale) : null;
    }

    private DevicesLayout CurrentDevicesLayout()
    {
        double height = ContentCanvas.Height;
        return DevicesLayout.For(ContentCanvas.Width - 2 * _header.Side, height - _header.Row - MonitorBottom(height), ShownDevices.Count, _devicesView is not null ? _devicesLayout : null);
    }
    private string DevicesKey() => string.Join("|", CurrentDevicesLayout(), Settings.MonitorPalette, Settings.MonitorPalette == MonitorPalette.Custom ? Settings.MonitorCustomColors.Key : "",
        Inks.Key, Loc.Language, PanelTheme.Generation, SystemParameters.HighContrast);

    private void RenderDevices()
    {
        bool shown = DevicesContent.Visibility == Visibility.Visible && _devicesView is not null && DevicesContent.Children.Contains(_devicesView);
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed; ShelfContent.Visibility = Visibility.Collapsed;
        DevicesContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        // The header keeps the usage page's controls, as the monitor page does, so the switch does not move between pages.
        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        DevicesContent.Margin = new Thickness(_header.Side, 0, _header.Side, MonitorBottom(height));
        var key = DevicesKey();
        // The same step: the page stays, and only shows the devices as they are now (a device switched off in the settings).
        if (_devicesView is not null && key == _devicesKey && DevicesContent.Children.Contains(_devicesView)) { _devicesView.Update(ShownDevices, DevicesSettled, Soft.Enabled); return; }
        var layout = CurrentDevicesLayout();
        _devicesKey = key; _devicesLayout = layout;
        var old = shown ? _devicesView : null;
        _devicesView = new DevicesView(layout, Inks);
        _devicesView.Update(ShownDevices, DevicesSettled, motion: false);
        if (old is not null) MonitorCrossfade.Swap(DevicesContent, _devicesView, old, MonitorMotion);
        else { DevicesContent.Children.Clear(); DevicesContent.Children.Add(_devicesView); }
    }
}

using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

// The settings' debug page: a switch that puts the collapsed row's readings under manual control, and a slider for each reading
// the row currently shows (the ones its own table chooses), so a layout can be studied with steady figures whatever the machine
// really does.
public partial class SettingsWindow
{
    internal const int DebugPage = 12;

    private void DebugSettings()
    {
        SectionTitle(Loc.T("调试", "Debug"));
        var rows = new List<FrameworkElement>();
        var sliders = new List<Grid>();
        rows.Add(Toggle(Loc.T("覆盖收起行读数", "Override the collapsed row's readings"), S.DebugOverride, v =>
        {
            Change(s => s.DebugOverride = v);
            foreach (var row in sliders) EnableRow(row, v);
        }, help: Loc.T(
            "打开后，收起的胶囊或缺口里的读数显示下面设定的值：真实采样照常进行，展开的页面也保持真实读数。",
            "With this on, the readings in the collapsed notch or capsule show the values set below. The real sampling goes on, and the open pages keep their real readings.")));
        void Add(string title, double value, double max, double tick, string unit, Action<DeckSettings, double> set)
        {
            var row = SliderRow(title, value, 0, max, tick, unit, v => Change(s => set(s, v)));
            EnableRow(row, S.DebugOverride);
            sliders.Add(row); rows.Add(row);
        }
        foreach (var reading in S.CompactReadings)
        {
            if (!reading.Visible) continue;
            switch (reading.Item)
            {
                // The core columns and the CPU dial read the same sample, so one slider stands for both.
                case CompactMonitorItems.Cpu: Add(Loc.T("CPU 占用（核心列同值）", "CPU usage (core columns too)"), S.DebugCpu, 100, 1, "%", (s, v) => s.DebugCpu = v); break;
                case CompactMonitorItems.Memory: Add(Loc.T("内存占用", "Memory usage"), S.DebugMemory, 100, 1, "%", (s, v) => s.DebugMemory = v); break;
                case CompactMonitorItems.Gpu: Add(Loc.T("GPU 占用", "GPU usage"), S.DebugGpu, 100, 1, "%", (s, v) => s.DebugGpu = v); break;
                case CompactMonitorItems.Storage: Add(Loc.T("磁盘占用", "Disk usage"), S.DebugStorage, 100, 1, "%", (s, v) => s.DebugStorage = v); break;
                case CompactMonitorItems.Download: Add(Loc.T("下载速率", "Download rate"), S.DebugDown, 100, 1, Loc.T("MB/s", "MB/s"), (s, v) => s.DebugDown = v); break;
                case CompactMonitorItems.Upload: Add(Loc.T("上传速率", "Upload rate"), S.DebugUp, 100, 1, Loc.T("MB/s", "MB/s"), (s, v) => s.DebugUp = v); break;
                case CompactMonitorItems.Power: Add(Loc.T("整机功率", "Power draw"), S.DebugPower, 500, 1, Loc.T("瓦", "W"), (s, v) => s.DebugPower = v); break;
                case CompactMonitorItems.Charge: Add(Loc.T("电量", "Charge"), S.DebugCharge, 100, 1, "%", (s, v) => s.DebugCharge = v); break;
                case CompactMonitorItems.Frames:
                    Add(Loc.T("帧率", "Frame rate"), S.DebugFrames, 999, 1, Loc.T("帧", "FPS"), (s, v) => s.DebugFrames = v);
                    // The percentiles only show in the row while their own toggles are on; their sliders follow.
                    if (S.MonitorFps1Low) Add(Loc.T("1% low", "1% low"), S.DebugLow1, 999, 1, Loc.T("帧", "FPS"), (s, v) => s.DebugLow1 = v);
                    if (S.MonitorFps01Low) Add(Loc.T("0.1% low", "0.1% low"), S.DebugLow01, 999, 1, Loc.T("帧", "FPS"), (s, v) => s.DebugLow01 = v);
                    break;
            }
        }
        if (sliders.Count == 0)
            rows.Add(TextLine(Loc.T("收起行没有显示任何读数；先在“显示与读数”页勾上要调试的读数。",
                "The collapsed row shows no readings; turn the ones to debug on the Display and readings page on first."), 12.5, TextTertiary));
        Group([.. rows]);
    }

    // The sliders' rows read as off while the override is off (SW-12), as the dependent rows on the other pages do.
    private static void EnableRow(Grid row, bool enabled)
    {
        foreach (var control in row.Children.OfType<FrameworkElement>().Where(control => Grid.GetColumn(control) > 0)) control.IsEnabled = enabled;
        foreach (var part in row.Children.OfType<FrameworkElement>().Where(control => control is not Slider)) part.Opacity = enabled ? 1 : .4;
    }
}

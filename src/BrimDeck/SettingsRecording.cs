using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

// "系统监控 › 数据记录": the machine-wide readings written to one CSV file per day (MonitorRecorder), the pace and the keep
// days, the curves on the system page, and the folder with an export of everything recorded so far.
public sealed partial class SettingsWindow
{
    internal const int RecordingPage = 12;

    private void RecordingSettings()
    {
        SectionTitle(Loc.T("记录", "Recording"));
        Group(
            Toggle(Loc.T("记录系统监控数据", "Record the system readings"), S.MonitorRecording, v => Change(s => s.MonitorRecording = v),
                help: Loc.T("按固定间隔把整机的 CPU、内存、网速和显卡读数写成 CSV 文件，面板没有展开时也记录。",
                            "Writes the machine-wide CPU, memory, network and GPU readings to a CSV file on a fixed pace, recorded even while no deck is open.")),
            MusicChoice(Loc.T("记录间隔", "Recording pace"), S.MonitorRecordInterval,
                [(1, Loc.T("1 秒", "1 s")), (5, Loc.T("5 秒", "5 s")), (15, Loc.T("15 秒", "15 s")), (60, Loc.T("1 分钟", "1 minute"))],
                v => Change(s => s.MonitorRecordInterval = v)),
            MusicChoice(Loc.T("保留天数", "Keep recordings for"), S.MonitorRecordKeepDays,
                [(7, Loc.T("7 天", "7 days")), (30, Loc.T("30 天", "30 days")), (90, Loc.T("90 天", "90 days")), (365, Loc.T("一年", "One year"))],
                v => Change(s => s.MonitorRecordKeepDays = v)),
            Toggle(Loc.T("显示历史曲线", "Show the history chart"), S.MonitorRecordChart, v => Resize(s => s.MonitorRecordChart = v, DeckPage.Monitor),
                help: Loc.T("系统监控页在功耗行下方画最近一小时的趋势曲线。", "The system page draws the recent trend beneath the power line."),
                keepPreview: true));
        SectionTitle(Loc.T("数据", "Data"));
        Group(Row(Loc.T("数据文件夹", "Data folder"), DataButtons()));
    }

    private StackPanel DataButtons()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var open = ActionButton(Loc.T("打开文件夹", "Open folder"), OpenRecordingFolder);
        var export = ActionButton(Loc.T("导出 CSV", "Export CSV"), ExportRecording);
        export.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(open);
        buttons.Children.Add(export);
        return buttons;
    }

    private void OpenRecordingFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_app.Recorder.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SaveStatus(Loc.T("无法打开文件夹：", "Could not open the folder: ") + ex.Message, true);
        }
    }

    private void ExportRecording()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"monitor-export-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            DefaultExt = ".csv",
            Filter = "CSV (Comma delimited)|*.csv",
        };
        if (dialog.ShowDialog() != true) return;
        if (_app.Recorder.Export(dialog.FileName, out var lines) && lines > 0)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
        }
        else
        {
            SaveStatus(Loc.T("还没有可导出的记录。", "There is nothing recorded to export yet."), true);
        }
    }
}

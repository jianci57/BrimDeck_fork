using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

// "系统监控 › 增强传感器": the processor's temperature and power read through the PawnIO driver (Native.SensorSetup). Two steps,
// each with its state: PawnIO, which the user installs (winget in a window of its own, or PawnIO's own download), and the sensor
// service, set up and removed with one administrator prompt each. The states are looked at again whenever the page is shown or the
// window comes back to the front, so returning from PawnIO's installer shows it installed. Without either, the monitor still reads
// what Windows gives and a hardware monitor the user runs (MonitorBridges); one line says so.
public sealed partial class SettingsWindow
{
    internal const int SensorsPage = 10;
    private Action? _refreshSensors;
    private bool _sensorsBusy;
    private string? _sensorsFailure;
    private int _sensorsGeneration;

    private void SensorsSettings()
    {
        var intro = TextLine(Loc.T("安装 PawnIO 并启用后，可以直接从处理器读取 CPU 温度和功耗。适用于读不到这两项的电脑。",
            "With PawnIO installed and this turned on, the CPU's temperature and power are read from the processor itself. For PCs that show neither."), 12.5, TextSecondary);
        intro.TextWrapping = TextWrapping.Wrap; intro.Margin = new Thickness(0, 0, 0, 6);
        _content.Children.Add(intro);
        var guide = Link(Loc.T("了解详情 ↗", "Learn more ↗"), Updates.GitHubUpdates.RepositoryUrl + Loc.T("/blob/main/docs/zh-CN/enhanced-sensors.md", "/blob/main/docs/en-US/enhanced-sensors.md"));
        guide.HorizontalAlignment = HorizontalAlignment.Left; guide.Margin = new Thickness(0, 0, 0, 22);
        _content.Children.Add(guide);

        var pawnIo = new StackPanel { Orientation = Orientation.Horizontal };
        var service = new StackPanel { Orientation = Orientation.Horizontal };
        var pawnIoState = TextLine("", 12.5, TextTertiary);
        var serviceState = TextLine("", 12.5, TextTertiary);
        serviceState.TextWrapping = TextWrapping.Wrap;
        var reading = TextLine("", 12.5, TextSecondary);
        var readingRow = Row(Loc.T("当前读数", "Reading now"), reading);
        SectionTitle(Loc.T("第一步：PawnIO 驱动", "Step 1: PawnIO driver"));
        Group(Row("PawnIO", pawnIo, pawnIoState));
        SectionTitle(Loc.T("第二步：传感器服务", "Step 2: Sensor service"));
        Group(Row(Loc.T("传感器服务", "Sensor service"), service, serviceState), readingRow);
        var fallback = TextLine(Loc.T("未启用时，也会读取正在运行的 HWiNFO（需打开共享内存）或 LibreHardwareMonitor（需打开远程网页服务）。",
            "Until then, a running HWiNFO (with shared memory on) or LibreHardwareMonitor (with its remote web server on) is read too."), 12, TextTertiary);
        fallback.TextWrapping = TextWrapping.Wrap;
        _content.Children.Add(fallback);

        void Fill()
        {
            int generation = ++_sensorsGeneration;
            var state = Native.SensorSetup.State;
            string? version = Native.SensorSetup.PawnIoVersion;
            pawnIo.Children.Clear(); service.Children.Clear();
            if (version is null)
            {
                pawnIoState.Text = Loc.T("未安装", "Not installed");
                var install = ActionButton(Loc.T("安装", "Install"), InstallPawnIo, primary: true);
                AutomationProperties.SetName(install, Loc.T("安装 PawnIO", "Install PawnIO"));
                pawnIo.Children.Add(install);
            }
            else
            {
                pawnIoState.Text = Loc.T("已安装", "Installed") + (version.Length > 0 ? " · " + version : "");
                var remove = ActionButton(Loc.T("卸载", "Uninstall"), UninstallPawnIo);
                AutomationProperties.SetName(remove, Loc.T("卸载 PawnIO", "Uninstall PawnIO"));
                remove.IsEnabled = !_sensorsBusy;
                pawnIo.Children.Add(remove);
            }
            serviceState.Foreground = UI.Brush(_sensorsFailure is not null ? _palette.Negative : TextTertiary);
            readingRow.Visibility = Visibility.Collapsed;
            if (_sensorsBusy) serviceState.Text = Loc.T("正在处理…", "Working…");
            else if (_sensorsFailure is { } failure) serviceState.Text = failure;
            else serviceState.Text = state switch
            {
                Native.SensorSetupState.NoPawnIo when Native.SensorSetup.ServiceInstalled => Loc.T("PawnIO 未安装，服务无法读数", "PawnIO is not installed, so the service reads nothing"),
                Native.SensorSetupState.NoPawnIo => Loc.T("需要先安装 PawnIO", "Install PawnIO first"),
                Native.SensorSetupState.NotEnabled => Loc.T("未启用", "Off"),
                _ => Loc.T("已启用", "On")
            };
            if (Native.SensorSetup.ServiceInstalled)
            {
                var disable = ActionButton(Loc.T("停用", "Turn off"), DisableSensors);
                AutomationProperties.SetName(disable, Loc.T("停用传感器服务", "Turn off the sensor service"));
                disable.IsEnabled = !_sensorsBusy;
                service.Children.Add(disable);
                readingRow.Visibility = Visibility.Visible;
                reading.Text = Loc.T("读取中…", "Reading…");
                // The service is started and asked off the window's thread; a newer fill drops an older answer.
                _ = Task.Run(Native.SensorClient.ReadOnce).ContinueWith(task =>
                {
                    if (generation != _sensorsGeneration || !IsLoaded) return;
                    reading.Text = ReadingText(task.IsCompletedSuccessfully ? task.Result : null);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            else
            {
                var enable = ActionButton(Loc.T("启用", "Turn on"), EnableSensors, primary: true);
                AutomationProperties.SetName(enable, Loc.T("启用传感器服务", "Turn on the sensor service"));
                enable.IsEnabled = !_sensorsBusy && state != Native.SensorSetupState.NoPawnIo;
                service.Children.Add(enable);
            }
        }
        Fill();
        _refreshSensors = Fill;
    }

    // What the service reads now, or why it reads nothing.
    private static string ReadingText(SensorReport? report)
    {
        if (report is null) return Loc.T("服务没有响应", "The service did not answer");
        if (report.Version < SensorReport.CurrentVersion) return Loc.T("服务版本较旧，请停用后重新启用", "The service is out of date: turn it off and on again");
        return report.Status switch
        {
            SensorStatus.NoDriver => Loc.T("PawnIO 驱动未运行", "The PawnIO driver is not running"),
            SensorStatus.NoModule => Loc.T("缺少 PawnIO 模块，请停用后重新启用", "PawnIO's modules are missing: turn it off and on again"),
            SensorStatus.Unsupported => Loc.T("不支持此处理器", "This processor is not supported"),
            SensorStatus.Failed => Loc.T("读取失败", "Could not read"),
            _ => string.Join(" · ", new[]
            {
                report.Package is { } celsius ? "CPU " + Temperatures.Format(celsius) + " °C" : null,
                report.PackageWatts is { } watts ? PowerFormat.Watts(watts) + " W" : null,
                report.Cores.Count > 0 ? (report.CoresAreCcds ? "CCD " : Loc.T("核心 ", "cores ")) + Temperatures.Format(report.Cores.Min()) + "–" + Temperatures.Format(report.Cores.Max()) + " °C" : null
            }.Where(part => part is not null)) is { Length: > 0 } text ? text : Loc.T("处理器没有给出读数", "The processor gave no reading")
        };
    }

    private void InstallPawnIo()
    {
        if (!Native.SensorSetup.RunWinget(true)) UI.OpenUrl(Native.SensorSetup.PawnIoDownload);
    }

    private void UninstallPawnIo()
    {
        if (!Confirm(Loc.T("卸载 PawnIO", "Uninstall PawnIO"),
            Loc.T("FanControl、LibreHardwareMonitor 等软件也可能使用这个驱动，卸载后它们也会受影响。", "Other software such as FanControl or LibreHardwareMonitor may use this driver too, and will be affected."),
            Loc.T("卸载", "Uninstall"))) return;
        if (!Native.SensorSetup.RunWinget(false)) UI.OpenUrl("ms-settings:appsfeatures");
    }

    private void EnableSensors() => _ = RunSensorSetupAsync(Native.SensorSetup.EnableAsync);

    private void DisableSensors()
    {
        if (Confirm(Loc.T("停用传感器服务", "Turn off the sensor service"),
            Loc.T("会删除传感器服务及其文件，Windows 会请求管理员确认。PawnIO 驱动不受影响。", "Removes the sensor service and its files; Windows asks an administrator to confirm. PawnIO is left as it is."),
            Loc.T("停用", "Turn off")))
            _ = RunSensorSetupAsync(Native.SensorSetup.DisableAsync);
    }

    private async Task RunSensorSetupAsync(Func<CancellationToken, Task> step)
    {
        if (_sensorsBusy) return;
        _sensorsBusy = true; _sensorsFailure = null; _refreshSensors?.Invoke();
        try { await step(CancellationToken.None); }
        catch (OperationCanceledException) { _sensorsFailure = Loc.T("操作超时", "Timed out"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
        { _sensorsFailure = ex.Message; }
        finally { _sensorsBusy = false; if (IsLoaded) _refreshSensors?.Invoke(); }
    }
}

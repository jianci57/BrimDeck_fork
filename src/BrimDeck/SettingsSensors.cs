using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Shapes;
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
        // The guide's link follows the sentence it explains, in the same line of text.
        var intro = TextLine(Loc.T("安装 PawnIO 并启用后，可以直接从处理器读取 CPU 温度和功耗。适用于读不到这两项的电脑。",
            "With PawnIO installed and this turned on, the CPU's temperature and power are read from the processor itself. For PCs that show neither."), 12.5, TextSecondary);
        intro.TextWrapping = TextWrapping.Wrap; intro.Margin = new Thickness(0, 0, 0, 24);
        intro.Inlines.Add(new Run(" "));
        intro.Inlines.Add(InlineLink(Loc.T("了解详情 ↗", "Learn more ↗"), Updates.GitHubUpdates.RepositoryUrl + Loc.T("/blob/main/docs/zh-CN/enhanced-sensors.md", "/blob/main/docs/en-US/enhanced-sensors.md")));
        _content.Children.Add(intro);

        // Each step: its name at the left, with a second line only for the version or a hint; at the right, its state as a dot and
        // a word, then the button that changes it.
        var pawnIo = new StepState(this);
        var service = new StepState(this);
        var reading = TextLine("", 12.5, TextSecondary);
        var readingRow = Row(Loc.T("当前读数", "Reading now"), reading);
        SectionTitle(Loc.T("第一步：PawnIO 驱动", "Step 1: PawnIO driver"));
        Group(Row("PawnIO", pawnIo.Root, pawnIo.Detail));
        SectionTitle(Loc.T("第二步：传感器服务", "Step 2: Sensor service"));
        Group(Row(Loc.T("传感器服务", "Sensor service"), service.Root, service.Detail), readingRow);
        var fallback = TextLine(Loc.T("未启用时，也会读取正在运行的 HWiNFO（需打开共享内存）或 LibreHardwareMonitor（需打开远程网页服务）。",
            "Until then, a running HWiNFO (with shared memory on) or LibreHardwareMonitor (with its remote web server on) is read too."), 12, TextTertiary);
        fallback.TextWrapping = TextWrapping.Wrap;
        _content.Children.Add(fallback);

        void Fill()
        {
            int generation = ++_sensorsGeneration;
            var state = Native.SensorSetup.State;
            string? version = Native.SensorSetup.PawnIoVersion;
            if (version is null)
            {
                var install = ActionButton(Loc.T("安装", "Install"), InstallPawnIo, primary: true);
                AutomationProperties.SetName(install, Loc.T("安装 PawnIO", "Install PawnIO"));
                pawnIo.Show(Step.Off, Loc.T("未安装", "Not installed"), install);
            }
            else
            {
                var remove = ActionButton(Loc.T("卸载", "Uninstall"), UninstallPawnIo); remove.Style = (Style)FindResource("SettingsOutlineButton");
                AutomationProperties.SetName(remove, Loc.T("卸载 PawnIO", "Uninstall PawnIO"));
                remove.IsEnabled = !_sensorsBusy;
                pawnIo.Show(Step.Done, Loc.T("已安装", "Installed"), remove,
                    version.Length > 0 ? Loc.T("版本 ", "Version ") + version : null);
            }

            bool installed = Native.SensorSetup.ServiceInstalled;
            Button action;
            if (installed)
            {
                action = ActionButton(Loc.T("停用", "Turn off"), DisableSensors); action.Style = (Style)FindResource("SettingsOutlineButton");
                AutomationProperties.SetName(action, Loc.T("停用传感器服务", "Turn off the sensor service"));
            }
            else
            {
                action = ActionButton(Loc.T("启用", "Turn on"), EnableSensors, primary: true);
                AutomationProperties.SetName(action, Loc.T("启用传感器服务", "Turn on the sensor service"));
            }
            action.IsEnabled = !_sensorsBusy && (installed || state != Native.SensorSetupState.NoPawnIo);
            if (_sensorsBusy) service.Show(Step.Off, Loc.T("正在处理…", "Working…"), action);
            else if (_sensorsFailure is { } failure)
                service.Show(installed ? Step.Done : Step.Off, installed ? Loc.T("已启用", "On") : Loc.T("未启用", "Off"), action, failure, Step.Failed);
            else if (installed && state == Native.SensorSetupState.NoPawnIo)
                service.Show(Step.Caution, Loc.T("无法读数", "Not reading"), action, Loc.T("PawnIO 未安装", "PawnIO is not installed"), Step.Caution);
            else if (installed) service.Show(Step.Done, Loc.T("已启用", "On"), action);
            else if (state == Native.SensorSetupState.NoPawnIo)
                service.Show(Step.Off, Loc.T("未启用", "Off"), action, Loc.T("需要先安装 PawnIO", "Install PawnIO first"));
            else service.Show(Step.Off, Loc.T("未启用", "Off"), action);

            readingRow.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
            if (installed)
            {
                reading.Text = Loc.T("读取中…", "Reading…");
                reading.SetResourceReference(TextBlock.ForegroundProperty, "SettingsTextSecondary");
                // The service is started and asked off the window's thread; a newer fill drops an older answer.
                _ = Task.Run(Native.SensorClient.ReadOnce).ContinueWith(task =>
                {
                    if (generation != _sensorsGeneration || !IsLoaded) return;
                    var (text, read) = ReadingText(task.IsCompletedSuccessfully ? task.Result : null);
                    reading.Text = text;
                    if (!read) reading.Foreground = UI.Brush(_palette.Negative);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }
        Fill();
        _refreshSensors = Fill;
    }

    private enum Step { Off, Done, Caution, Failed }

    // A step's state at the right of its row (a dot and a word, then its button) and the second line under its name, shown only
    // when there is something to add.
    private sealed class StepState
    {
        private readonly SettingsWindow _owner;
        private readonly Ellipse _dot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _word;
        public StackPanel Root { get; } = new() { Orientation = Orientation.Horizontal };
        public TextBlock Detail { get; }

        public StepState(SettingsWindow owner)
        {
            _owner = owner;
            _word = owner.TextLine("", 12.5, owner.TextSecondary); _word.VerticalAlignment = VerticalAlignment.Center;
            Detail = owner.TextLine("", 12, owner.TextTertiary); Detail.TextWrapping = TextWrapping.Wrap; Detail.Margin = new Thickness(0, 4, 0, 0);
            Root.Children.Add(_dot); Root.Children.Add(_word); Root.Children.Add(new Border());
        }

        public void Show(Step step, string word, Button button, string? detail = null, Step detailTone = Step.Off)
        {
            _dot.Fill = UI.Brush(Color(step));
            _word.Text = word;
            // The buttons share one width, so the dots and words of both steps end at the same place.
            button.MinWidth = 64;
            Root.Children.RemoveAt(2); button.Margin = new Thickness(16, 0, 0, 0); button.VerticalAlignment = VerticalAlignment.Center; Root.Children.Add(button);
            Detail.Text = detail ?? "";
            Detail.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
            Detail.Foreground = UI.Brush(detailTone == Step.Off ? _owner.TextTertiary : Color(detailTone));
        }

        private string Color(Step step) => step switch
        {
            Step.Done => _owner._palette.Positive,
            Step.Caution => _owner.CautionColor,
            Step.Failed => _owner._palette.Negative,
            _ => _owner.TextTertiary
        };
    }

    // A link inside a line of text: the accent color, underlined under the pointer or with keyboard focus.
    private Hyperlink InlineLink(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { Foreground = UI.Brush(_palette.Link), TextDecorations = null, ToolTip = url, Cursor = System.Windows.Input.Cursors.Hand };
        void Underline() => link.TextDecorations = link.IsMouseOver || link.IsKeyboardFocused ? TextDecorations.Underline : null;
        link.MouseEnter += (_, _) => Underline(); link.MouseLeave += (_, _) => Underline();
        link.GotKeyboardFocus += (_, _) => Underline(); link.LostKeyboardFocus += (_, _) => Underline();
        link.Click += (_, _) => UI.OpenUrl(url);
        AutomationProperties.SetName(link, text.TrimEnd(' ', '↗'));
        return link;
    }

    // What the service reads now, or why it reads nothing; Read is false for the latter.
    private static (string Text, bool Read) ReadingText(SensorReport? report)
    {
        if (report is null) return (Loc.T("服务没有响应", "The service did not answer"), false);
        if (report.Version < SensorReport.CurrentVersion) return (Loc.T("服务版本较旧，请停用后重新启用", "The service is out of date: turn it off and on again"), false);
        return report.Status switch
        {
            SensorStatus.NoDriver => (Loc.T("PawnIO 驱动未运行", "The PawnIO driver is not running"), false),
            SensorStatus.NoModule => (Loc.T("缺少 PawnIO 模块，请停用后重新启用", "PawnIO's modules are missing: turn it off and on again"), false),
            SensorStatus.Unsupported => (Loc.T("不支持此处理器", "This processor is not supported"), false),
            SensorStatus.Failed => (Loc.T("读取失败", "Could not read"), false),
            _ => string.Join(" · ", new[]
            {
                report.Package is { } celsius ? "CPU " + Temperatures.Format(celsius) + " °C" : null,
                report.PackageWatts is { } watts ? PowerFormat.Watts(watts) + " W" : null,
                report.Cores.Count > 0 ? (report.CoresAreCcds ? "CCD " : Loc.T("核心 ", "cores ")) + Temperatures.Format(report.Cores.Min()) + "–" + Temperatures.Format(report.Cores.Max()) + " °C" : null
            }.Where(part => part is not null)) is { Length: > 0 } text ? (text, true) : (Loc.T("处理器没有给出读数", "The processor gave no reading"), false)
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

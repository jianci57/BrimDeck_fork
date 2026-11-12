using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The notifications page (page 16): the switch for the island's system notifications, the reader's authorization and
// access state, what the island shows, and the chime. It is a settings page, not a mirror of the notification
// center — the reader (the application's one, App.Notifications) is asked only for the access state and, by the
// button, for consent; the page shows no notification content and keeps none. The background reading and the display
// live in NotificationService and the panels.
public sealed partial class SettingsWindow
{
    // Set up with the page and invoked by ShowPageCore (page switch, theme rebuild) and when the window closes:
    // cancels the access request and the audio probe's awaited answer and invalidates their results. Runs at most
    // once — it takes itself out first.
    private Action? _notificationsCleanup;
    private CancellationTokenSource? _notificationCancelling;
    private int _notificationGeneration;
    // One access request at a time; the audio probes count themselves, so only the newest pick may write its path.
    private int _notificationBusy;
    private int _notificationSoundPick;
    private TextBlock _notificationStatus = null!;
    private TextBlock _notificationSoundStatus = null!;
    private TextBlock _notificationSoundSource = null!;
    private Button _notificationAuthorizeButton = null!;

    private void NotificationsSettings()
    {
        _notificationGeneration++;
        SectionTitle(Loc.T("系统消息通知", "System notifications"), help: () => Loc.T(
            "开启后，BrimDeck 在后台每 2.5 秒读取一次 Windows 通知中心，只把新到达的通知显示在灵动岛上；不清除、不改动、不记录任何通知，内容只在显示时使用。",
            "When on, BrimDeck reads Windows' notification center every 2.5 seconds in the background and shows newly arrived notifications on the island. Nothing is removed, changed or recorded; the content is used to be shown, nothing more."));
        var toggle = Toggle(Loc.T("显示系统消息通知", "Show system notifications"), S.NotificationsEnabled,
            v => { Change(s => s.NotificationsEnabled = v); RefreshNotificationAccess(); }, help: Loc.T(
                "关闭时灵动岛不显示系统消息，也不再读取通知中心；已显示的通知（含测试通知）立即消失。",
                "Off, the island shows no system messages and the center is not read; a notification already shown (a test one too) disappears at once."));
        _notificationStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        var authorize = ActionButton(Loc.T("请求授权", "Request access"), RequestNotificationAccess);
        _notificationAuthorizeButton = authorize;
        var systemSettings = ActionButton(Loc.T("打开系统通知设置", "Open system notification settings"), () =>
        {
            try { UI.OpenUrl("ms-settings:privacy-notifications"); }
            catch { ShowNotificationStatus(Loc.T("无法打开系统设置，请手动前往 设置 > 隐私和安全性 > 通知。",
                "The system Settings did not open; go to Settings > Privacy & security > Notifications yourself."), true); }
        });
        var refresh = ActionButton(Loc.T("刷新", "Refresh"), RefreshNotificationAccess);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        authorize.Margin = new Thickness(0, 0, 10, 4); systemSettings.Margin = new Thickness(0, 0, 10, 4);
        buttons.Children.Add(authorize); buttons.Children.Add(systemSettings); buttons.Children.Add(refresh);
        var access = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        access.Children.Add(_notificationStatus); access.Children.Add(buttons);
        Group(toggle, Row(Loc.T("读取授权", "Reader access"), access));

        SectionTitle(Loc.T("通知内容", "Notification content"));
        var modeTitle = Loc.T("内容模式", "Content");
        var modeOptions = new UniformGrid { Columns = 3, Width = 324 };
        foreach (var (mode, label) in new[] {
            (NotificationContentMode.Brief, Loc.T("缩略", "Brief")),
            (NotificationContentMode.Compact, Loc.T("紧凑", "Compact")),
            (NotificationContentMode.Full, Loc.T("完整", "Full")) })
        {
            var option = new RadioButton { Content = label, Style = (Style)FindResource("SettingsSegment"), GroupName = "NotificationContentMode", IsChecked = S.NotificationContentMode == mode };
            AutomationProperties.SetName(option, modeTitle + " · " + label);
            option.Checked += (_, _) => Change(s => s.NotificationContentMode = mode);
            modeOptions.Children.Add(option);
        }
        Group(Row(modeTitle, SegmentBorder(modeOptions), help: () => Loc.T(
            "缩略显示标题与正文；紧凑显示时间与应用名；完整显示应用名、标题与正文（正文过长时截断）。",
            "Brief shows the title and the body; Compact the time and the app name; Full the app name, the title and the body (a long body is cut).")));

        SectionTitle(Loc.T("消息提示音", "Notification sound"));
        bool soundOn = S.NotificationSoundEnabled;
        _notificationSoundStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        _notificationSoundSource = TextLine(NotificationSoundSourceText(), 12, TextSecondary);
        var soundToggle = Toggle(Loc.T("新通知时播放提示音", "Play a sound on new notifications"), soundOn,
            v => { Change(s => s.NotificationSoundEnabled = v); if (!v) _app.NotificationSound().Stop(); ShowPage(NotificationsPage); },
            help: Loc.T("关闭时音源、音量与试听一并停用；正在播放的声音立即停止。", "Off, the source, the volume and the preview are out of use as well; a sound playing stops at once."));
        var builtin = ActionButton(Loc.T("内置提示音", "Built-in chime"), () =>
        {
            Change(s => s.NotificationSoundPath = "");
            _notificationSoundSource.Text = NotificationSoundSourceText();
        });
        builtin.IsEnabled = soundOn;
        var custom = ActionButton(Loc.T("自定义音频…", "Custom audio…"), PickNotificationSound);
        custom.IsEnabled = soundOn;
        builtin.Margin = new Thickness(0, 0, 10, 0);
        var sourceButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sourceButtons.Children.Add(builtin); sourceButtons.Children.Add(custom);
        var volume = SliderRow(Loc.T("提示音音量", "Sound volume"), S.NotificationSoundVolume, 0, 100, 10, "%",
            value => Change(s => s.NotificationSoundVolume = (int)Math.Round(value)));
        volume.IsEnabled = soundOn;
        var preview = ActionButton(Loc.T("试听", "Preview"), () =>
        {
            try { _app.NotificationSound().Play(S.NotificationSoundPath, S.NotificationSoundVolume, failure => ShowNotificationSoundStatus(failure, error: true)); }
            catch (Exception exception) { ShowNotificationSoundStatus(exception.Message, error: true); }
        });
        preview.IsEnabled = soundOn;
        var reset = ActionButton(Loc.T("重置", "Reset"), () =>
        {
            Change(s => { s.NotificationSoundPath = ""; s.NotificationSoundVolume = NotificationSound.DefaultVolume; });
            // The volume's slider does not follow a saved change by itself; the page is rebuilt with the new values.
            ShowPage(NotificationsPage);
        });
        var test = ActionButton(Loc.T("测试通知", "Test notification"), () => _app.ShowTestNotification());
        preview.Margin = new Thickness(0, 0, 10, 4); reset.Margin = new Thickness(0, 0, 10, 4);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(preview); actions.Children.Add(reset); actions.Children.Add(test);
        var play = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        play.Children.Add(_notificationSoundStatus); play.Children.Add(actions);
        Group(soundToggle,
            Row(Loc.T("音源", "Sound source"), sourceButtons, _notificationSoundSource, help: () => Loc.T(
                "内置提示音是 BrimDeck 自己生成的一段短音，不使用任何外部音频；自定义音频支持常见的音频文件，最大 2 MB、最长 5 秒。",
                "The built-in chime is a short sound BrimDeck generates itself and uses no external audio; a custom audio can be any common audio file, at most 2 MB and 5 seconds long.")),
            volume,
            Row(Loc.T("试听与测试", "Preview and test"), play, help: () => Loc.T(
                "试听按当前音源和音量播放一次；“测试通知”在灵动岛显示一条标注为合成的测试内容，不读取真实通知，也不需要先开启本页开关。",
                "Preview plays the current source at the current volume once; “Test notification” shows an item marked as synthetic on the island — it reads no real notifications and needs no switch.")));

        // The page's own answers die with the page: the token the access request and the audio probe carry is
        // cancelled and the generation moves, so a result that lands after the page is gone writes nothing.
        var cancelling = new CancellationTokenSource();
        _notificationCancelling = cancelling;
        _notificationsCleanup = () =>
        {
            _notificationsCleanup = null;
            _notificationGeneration++;
            cancelling.Cancel();
            cancelling.Dispose();
            _notificationCancelling = null;
        };
        RefreshNotificationAccess();
    }

    private string NotificationSoundSourceText()
    {
        var path = S.NotificationSoundPath;
        if (path.Length == 0) return Loc.T("当前：内置提示音", "Current: the built-in chime");
        return Loc.T("当前：", "Current: ") + path;
    }

    private async void PickNotificationSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择提示音音频", "Choose a sound file"),
            Filter = Loc.T("音频文件 (*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac)|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac|所有文件 (*.*)|*.*",
                "Audio files (*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac)|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac|All files (*.*)|*.*")
        };
        if (dialog.ShowDialog(this) is not true) return;
        int generation = _notificationGeneration, pick = ++_notificationSoundPick;
        ShowNotificationSoundStatus(Loc.T("正在检查音频文件…", "Checking the audio file…"), error: false);
        bool usable = false;
        var completed = new TaskCompletionSource();
        _app.NotificationSound().Probe(dialog.FileName, (ok, error) =>
        {
            // Only the newest pick's answer may speak: an earlier probe that lands late (or after the page is gone)
            // writes nothing, so a slow check can never overwrite a file chosen after it.
            if (generation != _notificationGeneration || pick != _notificationSoundPick) { completed.TrySetResult(); return; }
            usable = ok;
            ShowNotificationSoundStatus(error ?? "", error: !ok);
            completed.TrySetResult();
        });
        await completed.Task;
        if (!usable || generation != _notificationGeneration || pick != _notificationSoundPick) return;
        Change(s => s.NotificationSoundPath = dialog.FileName);
        _notificationSoundSource.Text = NotificationSoundSourceText();
        ShowNotificationSoundStatus("", error: false);
    }

    // Where access stands at this moment; the same reader the background service uses, so a consent dialog asked
    // here can never overlap a poll's snapshot.
    private void RefreshNotificationAccess()
    {
        int generation = _notificationGeneration;
        var (state, detail) = _app.Notifications.AccessNow();
        if (generation != _notificationGeneration) return;
        if (state is NotificationReader.Access.Allowed)
        {
            ShowNotificationStatus(S.NotificationsEnabled
                ? Loc.T("已允许读取通知，后台监控已开启。", "Access granted; the background monitor is on.")
                : Loc.T("已允许读取通知；开关打开后开始显示新通知。", "Access granted; new notifications are shown once the switch above is turned on."));
            UpdateNotificationButtons(state);
            return;
        }
        ShowNotificationProblem(state, detail);
    }

    // Runs in the button's click handler, so the consent dialog attaches to this window (the request must be made on
    // the UI thread). The service's timer keeps polling meanwhile; the reader's gate keeps the two apart.
    private async void RequestNotificationAccess()
    {
        int generation = _notificationGeneration;
        var service = _app.Notifications;
        if (Interlocked.CompareExchange(ref _notificationBusy, 1, 0) != 0) return;
        try
        {
            ShowNotificationStatus(Loc.T("正在请求读取系统通知的授权…", "Asking for access to your notifications…"));
            UpdateNotificationButtons(NotificationReader.Access.Unspecified, requesting: true);
            var (state, detail) = await service.RequestAccessAsync(_notificationCancelling?.Token ?? default);
            if (generation != _notificationGeneration) return;
            // The dialog's answer can be out of date by the time it arrives: what stands now is what is shown, so a
            // permission withdrawn while the dialog sat open is not read as granted.
            var (now, _) = service.AccessNow();
            if (now is NotificationReader.Access.Allowed)
            {
                RefreshNotificationAccess();
                return;
            }
            ShowNotificationProblem(now, detail);
        }
        catch (NotificationBusyException)
        {
            if (generation != _notificationGeneration) return;
            ShowNotificationStatus(Loc.T("上一次读取尚未结束，正在等待…", "The previous read has not ended yet; waiting…"));
        }
        catch (OperationCanceledException)
        {
            // The page went away or the window closed while the dialog sat unanswered; the state stays as it was.
        }
        catch (Exception exception)
        {
            if (generation != _notificationGeneration) return;
            ShowNotificationStatus(Loc.T("请求授权出错：", "Requesting access failed: ") + exception.Message, true);
        }
        finally
        {
            Interlocked.Exchange(ref _notificationBusy, 0);
            if (generation == _notificationGeneration) UpdateNotificationButtons(service.AccessNow().State);
        }
    }

    private void ShowNotificationProblem(NotificationReader.Access state, string? detail)
    {
        // The diagnostic rides along in its raw form (exception type, HRESULT, message), so a support conversation
        // can tell a refused permission from a service failure.
        string suffix = string.IsNullOrEmpty(detail) ? "" : Loc.T("（诊断信息：", " (Diagnostic: ") + detail + "）";
        switch (state)
        {
            case NotificationReader.Access.Unsupported:
                ShowNotificationStatus(Loc.T("此系统不支持读取通知（需要 Windows 10 版本 15063 或更高）。",
                        "This system cannot read notifications (Windows 10 build 15063 or newer is required).") + suffix, true);
                break;
            case NotificationReader.Access.Denied:
                ShowNotificationStatus(Loc.T("读取系统通知的权限已被拒绝。可在系统通知设置中重新允许 BrimDeck，再回到本页点击“请求授权”。",
                        "Access to your notifications was denied. Allow BrimDeck again in the system's notification settings, then click “Request access” on this page.") + suffix, true);
                break;
            case NotificationReader.Access.Error:
                ShowNotificationStatus(Loc.T("读取通知的接口暂时出错，可稍后重试。", "The notification API failed for now; try again in a moment.") + suffix, true);
                break;
            default:
                ShowNotificationStatus(Loc.T("尚未授权 BrimDeck 读取系统通知。点击“请求授权”，并在系统弹窗中选择允许。",
                        "BrimDeck has not been given access to your notifications yet. Click “Request access” and choose Allow in the system prompt.") + suffix);
                break;
        }
        UpdateNotificationButtons(state);
    }

    private void UpdateNotificationButtons(NotificationReader.Access state, bool requesting = false)
    {
        bool supported = state != NotificationReader.Access.Unsupported;
        _notificationAuthorizeButton.Visibility = supported && state != NotificationReader.Access.Allowed
            ? Visibility.Visible : Visibility.Collapsed;
        _notificationAuthorizeButton.IsEnabled = supported && !requesting;
    }

    private void ShowNotificationStatus(string text, bool error = false)
    {
        _notificationStatus.Text = text;
        _notificationStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _notificationStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "SettingsError" : "SettingsTextSecondary");
    }

    private void ShowNotificationSoundStatus(string text, bool error)
    {
        _notificationSoundStatus.Text = text;
        _notificationSoundStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _notificationSoundStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "SettingsError" : "SettingsTextSecondary");
    }
}

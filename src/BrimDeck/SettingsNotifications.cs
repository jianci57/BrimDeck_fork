using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The notifications page (page 16): the switch for the island's system notifications, the reader's authorization and
// access state, and what the island shows. It is a settings page, not a mirror of the notification
// center — the reader (the application's one, App.Notifications) is asked only for the access state and, by the
// button, for consent; the page shows no notification content and keeps none. The background reading and the display
// live in NotificationService and the panels.
public sealed partial class SettingsWindow
{
    // Set up with the page and invoked by ShowPageCore (page switch, theme rebuild) and when the window closes:
    // cancels the access request's awaited answer and invalidates its result. Runs at most once — it takes itself
    // out first.
    private Action? _notificationsCleanup;
    private CancellationTokenSource? _notificationCancelling;
    private int _notificationGeneration;
    // One access request at a time.
    private int _notificationBusy;
    private TextBlock _notificationStatus = null!;
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

        var test = ActionButton(Loc.T("测试通知", "Test notification"), () => _app.ShowTestNotification());
        Group(Row(Loc.T("测试通知", "Test notification"), test, help: () => Loc.T(
            "“测试通知”在灵动岛显示一条标注为合成的测试内容，不读取真实通知，也不需要先开启本页开关。",
            "“Test notification” shows an item marked as synthetic on the island — it reads no real notifications and needs no switch.")));

        // The page's own answers die with the page: the token the access request carries is
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
}

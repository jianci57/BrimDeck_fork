using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The notifications page (page 16). It mirrors the toasts Windows already shows: access is requested by an
// explicit button, and while the page is current a 2.5 s timer replaces the list with a fresh snapshot of the
// notification center. Nothing is kept — when a notification disappears from the center it disappears here,
// and when the page is left the timer is stopped and every pending result is dropped by generation.
public sealed partial class SettingsWindow
{
    private const double NotificationPollSeconds = 2.5;
    // One reader for the window's whole life: a fresh one would leave an operation it had in flight
    // unguarded, so page switches reuse it and are fenced by the generation and the page's token instead.
    private readonly NotificationReader _notificationReader = new();
    // Set up with the page and invoked by ShowPageCore (page switch, theme rebuild) and when the window
    // closes: stops the timer, cancels the read in flight and invalidates awaited results. Runs at most
    // once — it takes itself out first.
    private Action? _notificationsCleanup;
    private CancellationTokenSource? _notificationCancelling;
    private int _notificationGeneration;
    // One flight at a time, shared by the access request and the snapshot: a manual refresh never overlaps
    // the timer's read or the authorization dialog's follow-up read.
    private int _notificationBusy;
    private TextBlock _notificationStatus = null!;
    private StackPanel _notificationList = null!;
    private Button _notificationAuthorizeButton = null!;
    // The list as it is drawn; a snapshot identical to it is not drawn again, so an unchanged center
    // never disturbs the reader's scrolling or focus.
    private IReadOnlyList<NotificationItem>? _notificationShown;

    private void NotificationsSettings()
    {
        _notificationGeneration++;
        _notificationShown = null;
        var reader = _notificationReader;
        SectionTitle(Loc.T("通知读取", "Notification reader"), help: () => Loc.T(
            "仅显示通知中心当前保留的通知，不读取更早的历史，也不会清除或改动系统里的通知。本页打开期间每 2.5 秒刷新一次，通知内容只显示在页面上，不写入文件。",
            "Shows only the notifications the center keeps right now, not any older history, and never removes or changes them. While this page is open it refreshes every 2.5 seconds; the content is shown on the page only, never written to a file."));
        _notificationStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        var authorize = ActionButton(Loc.T("请求授权", "Request access"), RequestNotificationAccess);
        _notificationAuthorizeButton = authorize;
        var systemSettings = ActionButton(Loc.T("打开系统通知设置", "Open system notification settings"), () =>
        {
            try { UI.OpenUrl("ms-settings:privacy-notifications"); }
            catch { ShowNotificationStatus(Loc.T("无法打开系统设置，请手动前往 设置 > 隐私和安全性 > 通知。",
                "The system Settings did not open; go to Settings > Privacy & security > Notifications yourself."), true); }
        });
        var refresh = ActionButton(Loc.T("刷新", "Refresh"), () => _ = RefreshNotificationsAsync());
        // The row wraps when the window is narrow: the English labels together are wider than the Chinese ones.
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        authorize.Margin = new Thickness(0, 0, 10, 6); systemSettings.Margin = new Thickness(0, 0, 10, 6);
        buttons.Children.Add(authorize); buttons.Children.Add(systemSettings); buttons.Children.Add(refresh);
        var controls = new StackPanel();
        controls.Children.Add(_notificationStatus); controls.Children.Add(buttons);
        Group(controls);
        SectionTitle(Loc.T("当前通知", "Notifications in the center"));
        _notificationList = new StackPanel();
        Group(_notificationList);
        // The page refreshes itself only while it exists: the timer is born here and stopped by the
        // cleanup when the page is left or the window closes.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(NotificationPollSeconds) };
        timer.Tick += (_, _) => _ = RefreshNotificationsAsync();
        var cancelling = new CancellationTokenSource();
        _notificationCancelling = cancelling;
        _notificationsCleanup = () =>
        {
            _notificationsCleanup = null;
            _notificationGeneration++;
            timer.Stop();
            cancelling.Cancel();
            cancelling.Dispose();
            _notificationCancelling = null;
        };
        timer.Start();
        _ = RefreshNotificationsAsync();
    }

    // One snapshot per flight. The access check is synchronous and runs every time, so a permission the user
    // changes in the system settings is picked up within one poll; the snapshot is taken only when access is
    // allowed, and every awaited result is dropped when the page went away while it was running.
    private async Task RefreshNotificationsAsync()
    {
        int generation = _notificationGeneration;
        var reader = _notificationReader;
        if (Interlocked.CompareExchange(ref _notificationBusy, 1, 0) != 0) return;
        try
        {
            var (state, detail) = reader.AccessNow();
            if (generation != _notificationGeneration) return;
            if (state is not (NotificationReader.Access.Allowed)) { ShowNotificationProblem(state, detail); return; }
            ShowNotificationStatus("");
            UpdateNotificationButtons(state);
            var cancelling = _notificationCancelling?.Token ?? default;
            var items = await reader.SnapshotAsync(cancelling);
            if (generation != _notificationGeneration) return;
            ShowNotifications(items);
        }
        catch (NotificationBusyException)
        {
            // The previous read is still alive; the list stays as it is and the note says the page is waiting.
            if (generation != _notificationGeneration) return;
            ShowNotificationStatus(Loc.T("上一次读取尚未结束，正在等待…", "The previous read has not ended yet; waiting…"));
        }
        catch (Exception exception)
        {
            if (generation != _notificationGeneration) return;
            ShowNotificationStatus(Loc.T("读取通知出错：", "Reading the notifications failed: ") + exception.Message, true);
            ShowNotifications(null);
        }
        finally { Interlocked.Exchange(ref _notificationBusy, 0); }
    }

    // Runs in the button's click handler, so the consent dialog attaches to this window (RequestAccessAsync
    // must be called on the UI thread). The follow-up snapshot happens inside the same flight.
    private async void RequestNotificationAccess()
    {
        int generation = _notificationGeneration;
        var reader = _notificationReader;
        if (Interlocked.CompareExchange(ref _notificationBusy, 1, 0) != 0) return;
        try
        {
            ShowNotificationStatus(Loc.T("正在请求读取系统通知的授权…", "Asking for access to your notifications…"));
            UpdateNotificationButtons(NotificationReader.Access.Unspecified, requesting: true);
            var (state, detail) = await reader.RequestAccessAsync(_notificationCancelling?.Token ?? default);
            if (generation != _notificationGeneration) return;
            if (state is not (NotificationReader.Access.Allowed)) { ShowNotificationProblem(state, detail); return; }
            ShowNotificationStatus("");
            UpdateNotificationButtons(state);
            var cancelling = _notificationCancelling?.Token ?? default;
            var items = await reader.SnapshotAsync(cancelling);
            if (generation != _notificationGeneration) return;
            ShowNotifications(items);
        }
        catch (NotificationBusyException)
        {
            if (generation != _notificationGeneration) return;
            ShowNotificationStatus(Loc.T("上一次读取尚未结束，正在等待…", "The previous read has not ended yet; waiting…"));
        }
        catch (Exception exception)
        {
            if (generation != _notificationGeneration) return;
            ShowNotificationStatus(Loc.T("请求授权出错：", "Requesting access failed: ") + exception.Message, true);
            ShowNotifications(null);
        }
        finally
        {
            Interlocked.Exchange(ref _notificationBusy, 0);
            // A failed request leaves the button disabled; the state as it stands now puts it back.
            if (generation == _notificationGeneration) UpdateNotificationButtons(reader.AccessNow().State);
        }
    }

    private void ShowNotificationProblem(NotificationReader.Access state, string? detail)
    {
        // The diagnostic rides along in its raw form (exception type, HRESULT, message), so a support
        // conversation can tell a refused permission from a service failure.
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
        ShowNotifications(null);
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

    // Replaces the whole list: the page mirrors the center, so a notification that has gone is gone.
    // A snapshot identical to the one drawn changes nothing.
    private void ShowNotifications(IReadOnlyList<NotificationItem>? items)
    {
        if (items is { Count: > 0 })
            // Windows keeps an unpackaged sender's application details to itself (AppInfo fails with
            // E_NOTIMPL without package identity), so those cards fall back to "Unknown app" and the
            // page says so once — set before the identical-snapshot early return, which would other-
            // wise erase the note on the refreshes after it.
            ShowNotificationStatus(items.Any(i => i.AppName.Length == 0 && i.AppId.Length == 0)
                ? Loc.T("部分通知未提供应用名称。", "Some notifications do not provide an application name.") : "");
        if (items is not null && _notificationShown is not null && items.SequenceEqual(_notificationShown)) return;
        _notificationShown = items;
        _notificationList.Children.Clear();
        if (items is null) return;
        if (items.Count == 0)
        {
            // Access is allowed and the center is empty — said in its own words, so it never reads as
            // a failure and never as a silent "nothing happened".
            var empty = TextLine(Loc.T("通知中心当前没有通知。收到新通知后，这里会在几秒内显示。",
                "There are no notifications in the center right now. New ones appear here within a few seconds."), 12, TextTertiary);
            empty.Margin = new Thickness(0, 10, 0, 6);
            _notificationList.Children.Add(empty);
            return;
        }
        foreach (var item in items) _notificationList.Children.Add(NotificationCard(item));
        UpdateDividers(_notificationList);
    }

    private FrameworkElement NotificationCard(NotificationItem item)
    {
        var head = new Grid { Margin = new Thickness(0, 10, 0, 2) };
        head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // A long application name is trimmed, so it never squeezes the time at the right edge.
        var app = TextLine(item.AppName.Length > 0 ? item.AppName
            : item.AppId.Length > 0 ? item.AppId : Loc.T("未知应用", "Unknown app"), 11, TextTertiary);
        app.TextTrimming = TextTrimming.CharacterEllipsis; app.Margin = new Thickness(0, 0, 12, 0); app.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(app);
        var time = TextLine(NotificationTime(item.CreationTime), 11, TextTertiary);
        time.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(time, 1); head.Children.Add(time);
        var title = TextLine(
            !item.HasText ? Loc.T("（无文本）", "(no text)")
            : item.Title.Length > 0 ? item.Title : Loc.T("（无标题）", "(no title)"),
            12.5, TextPrimary, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        var card = new StackPanel();
        card.Children.Add(head); card.Children.Add(title);
        if (item.Body.Length > 0)
        {
            var body = TextLine(item.Body, 12, TextSecondary);
            body.TextWrapping = TextWrapping.Wrap; body.Margin = new Thickness(0, 1, 0, 0);
            card.Children.Add(body);
        }
        card.Children.Add(new Border { Height = 8 });
        // UpdateDividers paints the line between the cards; the brush has to be here for it to show.
        return new Border { Child = card, BorderBrush = UI.Brush(Divider) };
    }

    // Today's notifications show their time, older ones their date and time; one the system no longer
    // timestamps says so instead of a date in year 1.
    private static string NotificationTime(DateTimeOffset time)
    {
        if (time.Year <= 1) return Loc.T("未知时间", "Unknown time");
        var local = time.LocalDateTime;
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Year == today.Year) return local.ToString("MM-dd HH:mm");
        return local.ToString("yyyy-MM-dd HH:mm");
    }
}

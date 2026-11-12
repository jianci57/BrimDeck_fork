using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using BrimDeck.Core;

namespace BrimDeck.Native;

// How the notifications page may read the system's toasts. Unspecified is "never asked", Denied is
// "asked and refused", Error is a failure worth retrying (the button stays); Detail carries the
// diagnostic (exception chain with HRESULT) the page shows, so a failure never reads as an empty list.
internal readonly record struct NotificationAccess(NotificationReader.Access State, string? Detail);

// Thrown instead of starting a second WinRT operation while one is still alive; the page shows it as
// a "reading" note and the next poll tries again.
internal sealed class NotificationBusyException : Exception
{
    public NotificationBusyException() : base("A previous notification read has not ended yet") { }
}

// Reads the toasts Windows already shows, for the notifications page in the settings (SettingsNotifications).
// Modeled on NotchPeninsula's Notify/Toast.cs (Apache License 2.0, see ThirdPartyNotices.txt): the same
// UserNotificationListener access dance, reduced to what the page needs — one snapshot per poll, capped
// waits, per-notification isolation, nothing removed, nothing written down. Only the page's own timer
// polls this while that page is current; there is no background listener and nothing runs afterwards.
internal sealed class NotificationReader
{
    public enum Access { Unsupported, Unspecified, Denied, Allowed, Error }
    private const int SnapshotTimeoutMs = 8000, RequestTimeoutMs = 15000;

    private UserNotificationListener? _listener;
    private string? _listenerError;
    // The WinRT operation in flight, whatever kind — an access request or a notification read — as its own
    // task created without a token, so it ends only when the operation really ends, plus how to cancel it.
    // While it is alive no second operation is started: the poll reports itself busy and tries again.
    private (Task Task, Action Cancel)? _pending;

    // Ends a finished operation's claim (observing its fault); an unfinished one stays for the next poll.
    private async Task ReleasePending(Task pending)
    {
        if (_pending is not { } current || !ReferenceEquals(current.Task, pending) || !pending.IsCompleted) return;
        _pending = null;
        try { await pending; } catch { }
    }
    // One WinRT operation at a time: a still-living previous one makes this call report busy at once —
    // the page keeps its list and shows a short note — instead of queueing behind it indefinitely.
    private async Task AwaitPending()
    {
        if (_pending is not { } previous) return;
        if (previous.Task.IsCompleted)
        {
            _pending = null;
            try { await previous.Task; } catch { }
            return;
        }
        throw new NotificationBusyException();
    }

    // Where the access stands at this moment. GetAccessStatus is synchronous and cheap; the page calls it
    // every poll, so a permission changed in the system settings shows up within seconds.
    public NotificationAccess AccessNow()
    {
        var (listener, missing) = Listener();
        if (listener is null) return new(missing, _listenerError);
        try { return new(Map(listener.GetAccessStatus()), null); }
        catch (Exception exception) { return new(Access.Error, Describe(exception)); }
    }
    // Asks Windows for the user's consent. Must run on the UI thread — the consent dialog attaches to the
    // window the call is made from, which is why only the page's button handler calls this. The dialog can
    // sit unanswered forever, so the wait is capped the way the reference implementation caps its waits;
    // the underlying operation is cancelled, and the wait never outlives the page (the token).
    public async Task<NotificationAccess> RequestAccessAsync(CancellationToken cancelled)
    {
        var (listener, missing) = Listener();
        if (listener is null) return new(missing, _listenerError);
        await AwaitPending();
        cancelled.ThrowIfCancellationRequested();
        try
        {
            var request = listener.RequestAccessAsync();
            var pending = request.AsTask();
            _ = pending.ContinueWith(static abandoned => _ = abandoned.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            _pending = (pending, () => { try { request.Cancel(); } catch { } });
            try
            {
                var finished = await Task.WhenAny(pending, Task.Delay(RequestTimeoutMs, cancelled));
                if (finished != pending)
                {
                    _pending.Value.Cancel();
                    // A page switch reads as its own cancellation, not as a timeout.
                    cancelled.ThrowIfCancellationRequested();
                    return new(Access.Unspecified, $"RequestAccessAsync did not return within {RequestTimeoutMs} ms");
                }
                return new(Map(await pending), null);
            }
            finally { await ReleasePending(pending); }
        }
        catch (OperationCanceledException)
        {
            if (cancelled.IsCancellationRequested) throw;
            return new(Access.Unspecified, $"RequestAccessAsync did not return within {RequestTimeoutMs} ms");
        }
        catch (Exception exception) { return new(Access.Error, Describe(exception)); }
    }
    // One snapshot of the notification center. Throws when the API fails (the page shows that as an error,
    // never as an empty list) and skips a single notification the system can no longer describe.
    public async Task<IReadOnlyList<NotificationItem>> SnapshotAsync(CancellationToken cancelled)
    {
        var (listener, _) = Listener();
        if (listener is null) return [];
        await AwaitPending();
        cancelled.ThrowIfCancellationRequested();
        var operation = listener.GetNotificationsAsync(NotificationKinds.Toast);
        var pending = operation.AsTask();
        _ = pending.ContinueWith(static abandoned => _ = abandoned.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _pending = (pending, () => { try { operation.Cancel(); } catch { } });
        try
        {
            // The wait is capped: past the budget the operation is cancelled and the UI is freed; the task
            // stays in _pending until it really ends, and the next poll reads as busy meanwhile.
            var finished = await Task.WhenAny(pending, Task.Delay(SnapshotTimeoutMs, cancelled));
            if (finished != pending)
            {
                _pending.Value.Cancel();
                // A page switch reads as its own cancellation, not as a timeout.
                cancelled.ThrowIfCancellationRequested();
                throw new TimeoutException($"GetNotificationsAsync did not return within {SnapshotTimeoutMs} ms");
            }
            var notifications = await pending;
            var items = new List<NotificationItem>(notifications.Count);
            foreach (var notification in notifications)
            {
                try { items.Add(Describe(notification)); }
                catch { /* One notification that was withdrawn before it was read is skipped alone. */ }
            }
            return NotificationItem.NewestFirst(items);
        }
        finally { await ReleasePending(pending); }
    }

    // The listener is created once and kept; a failed attempt is retried on the next poll (Error), while a
    // system without the API at all is Unsupported and stays that way. There is nothing to unsubscribe:
    // the page stops polling when it closes, and its cancellations end the operations.
    private (UserNotificationListener? Listener, Access Missing) Listener()
    {
        if (_listener is not null) return (_listener, default);
        if (!ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
            return (null, Access.Unsupported);
        try
        {
            _listener = UserNotificationListener.Current;
            return (_listener, default);
        }
        catch (Exception exception) { _listenerError = Describe(exception); return (null, Access.Error); }
    }
    private static Access Map(UserNotificationListenerAccessStatus status) => status switch
    {
        UserNotificationListenerAccessStatus.Allowed => Access.Allowed,
        UserNotificationListenerAccessStatus.Denied => Access.Denied,
        _ => Access.Unspecified
    };
    // One notification described in display terms. Each accessor is guarded separately, as the reference
    // implementation found the WinRT accessors throw for notifications withdrawn mid-read — one broken
    // accessor must not cost the whole notification. A notification without text (a pure image toast)
    // still comes through with HasText false, so the page can show it as present but silent.
    private static NotificationItem Describe(UserNotification notification)
    {
        string appName = "", appId = "";
        // Read separately: a display name that throws (a notification withdrawn mid-read) must not cost
        // the id with it — the page falls back to the id when the name is missing.
        try { appId = notification.AppInfo?.AppUserModelId ?? ""; } catch { }
        try { appName = NotificationItem.Flatten(notification.AppInfo?.DisplayInfo?.DisplayName); } catch { }
        DateTimeOffset created;
        try { created = notification.CreationTime; }
        catch { created = DateTimeOffset.MinValue; }
        string title = "", body = "";
        bool hasText = false;
        try
        {
            var texts = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
            if (texts is { Count: > 0 })
            {
                title = NotificationItem.Flatten(texts[0]?.Text);
                // The body keeps the lines the app sent it in, one element (or line) per line, only capped in length.
                body = NotificationItem.Limit(string.Join("\n", texts.Skip(1).Select(t => t?.Text?.TrimEnd() ?? "")));
                // A toast whose every text element is blank shows as a present but silent notification.
                hasText = title.Length > 0 || body.Length > 0;
            }
        }
        catch { }
        return new(notification.Id, appName, appId, title, body, created, hasText);
    }
    // The innermost cause with its HRESULT is what tells a refused permission from a service failure.
    private static string Describe(Exception exception)
    {
        var parts = new List<string>(2);
        for (var e = (Exception?)exception; e is not null && parts.Count < 4; e = e.InnerException)
            parts.Add(e is System.Runtime.InteropServices.COMException com
                ? $"{e.GetType().Name} 0x{com.HResult:X8}: {com.Message}"
                : $"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- ", parts);
    }
}

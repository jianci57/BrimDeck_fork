using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The system notification feature's app-level wiring: one service for the whole application, raised and stopped by
// the settings switch, its arrivals shown on every panel's island. The service also serves the settings page's
// reader needs (the access state, the consent dialog) with the same one reader, so a consent dialog and a poll
// never overlap. The test notification goes through the same broadcast without touching the reader.
public partial class App
{
    private NotificationService? _notifications;
    // The service exists from the first need on and stays, with its events wired once: the settings page reaches it
    // for the access state and the consent dialog even while the feature is off, and switching the feature on only
    // starts its timer.
    internal NotificationService Notifications => _notifications ??= CreateNotifications();

    private NotificationService CreateNotifications()
    {
        var service = new NotificationService(Dispatcher);
        service.Arrived += NotificationArrived;
        service.Cleared += () =>
        {
            if (Exiting) return;
            foreach (var deck in Decks.ToList()) deck.ClearSystemNotification();
        };
        return service;
    }

    // Reads the feature's switch (once at startup, and at every settings change). Off, the polling stops and every
    // island drops what it shows — even when no service ever ran, so a test notification goes too. On, the timer
    // starts; a switch that was already on keeps its baseline.
    internal void ApplyNotificationSettings()
    {
        if (Exiting) return;
        if (!Settings.NotificationsEnabled)
        {
            _notifications?.Reconfigure(false);
            foreach (var deck in Decks.ToList()) deck.ClearSystemNotification();
            return;
        }
        Notifications.Reconfigure(true);
    }

    private void NotificationArrived(NotificationItem item)
    {
        foreach (var deck in Decks.ToList()) deck.ShowSystemNotification(item);
    }

    // The settings page's test button: a clearly synthetic item through the same show chain — no reader, no
    // permission, and the feature's switch is not required.
    internal void ShowTestNotification()
    {
        var item = new NotificationItem(uint.MaxValue, Loc.T("测试通知", "Test notification"), "BrimDeck.Test",
            Loc.T("测试通知", "Test notification"),
            Loc.T("这是 BrimDeck 合成的测试内容，不是系统消息。", "This is a synthetic BrimDeck test item, not a system message."),
            DateTimeOffset.Now, true);
        foreach (var deck in Decks.ToList()) deck.ShowSystemNotification(item);
    }
}

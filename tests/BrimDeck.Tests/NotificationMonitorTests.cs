using BrimDeck.Core;

internal static class NotificationMonitorTests
{
    internal static void Run(Action<string, bool> check)
    {
        var start = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        static NotificationItem Item(uint id, string body, DateTimeOffset created) =>
            new(id, "A", "a", "title", body, created, true);

        var monitor = new NotificationMonitor();
        // Seed: the first snapshot is the baseline — everything in it is seen, nothing is raised.
        var seed = new[] { Item(1, "a", start.AddMinutes(1)), Item(2, "b", start.AddMinutes(2)) };
        check("The first snapshot raises nothing", monitor.Diff(seed).Count == 0 && monitor.Seeded);

        // Dup: the same snapshot again — no id/time pair is new, nothing is raised.
        check("The same snapshot raises nothing again", monitor.Diff(seed).Count == 0);

        // Update: same id and creation time, different text — an update is not a new notification.
        var updated = new[] { Item(1, "a-edited", start.AddMinutes(1)), Item(2, "b", start.AddMinutes(2)) };
        check("A text update keeps its notification silent", monitor.Diff(updated).Count == 0);

        // New arrival, then an empty center, then the same toast back: it stays quiet — what was raised once is
        // remembered even while it is gone, so a snapshot that misses it for one poll cannot make it re-raise.
        check("A new id raises one notification", monitor.Diff([Item(3, "c", start.AddMinutes(3))]) is [var arrival] && arrival.Id == 3 && arrival.Body == "c");
        check("An empty snapshot raises nothing", monitor.Diff([]).Count == 0);
        check("The same toast back after an empty center is not raised again",
            monitor.Diff([Item(3, "c", start.AddMinutes(3)), Item(1, "a-edited", start.AddMinutes(1)), Item(2, "b", start.AddMinutes(2))]).Count == 0);

        // Timestamp reuse: Windows reuses ids — the same id with a new creation time is a new notification.
        var reuse = monitor.Diff([Item(3, "c-again", start.AddMinutes(90)), Item(1, "a-edited", start.AddMinutes(1)), Item(2, "b", start.AddMinutes(2))]);
        check("A reused id with a new creation time raises again", reuse.Count == 1 && reuse[0].Id == 3 && reuse[0].Body == "c-again");

        // Two toasts can share one creation time: a new id at the watermark's own time still arrives.
        var tie = monitor.Diff([Item(4, "tie", start.AddMinutes(90)), Item(3, "c-again", start.AddMinutes(90))]);
        check("A new id at the watermark's own time arrives", tie.Count == 1 && tie[0].Id == 4);

        // Several arrivals at once: newest first.
        var batch = monitor.Diff([Item(9, "z", start.AddMinutes(200)), Item(8, "y", start.AddMinutes(210)),
            Item(1, "a-edited", start.AddMinutes(1)), Item(2, "b", start.AddMinutes(2))]);
        check("Several arrivals come newest first", batch.Count == 2 && batch[0].Id == 8 && batch[1].Id == 9);

        // A snapshot longer than the page's cap is clamped to the newest Maximum: nothing in it raises twice, the
        // trimmed ones never raise at all, and the history grows by one cap at most — no loop, however huge.
        var flood = new NotificationMonitor();
        var huge = Enumerable.Range(0, 600).Select(i => Item((uint)(2000 + i), "h", start.AddSeconds(i))).ToList();
        check("A huge first snapshot seeds without raising", flood.Diff(huge).Count == 0);
        var hugePlus = huge.ToList();
        hugePlus.Add(Item(3000, "n", start.AddMinutes(300)));
        check("A huge snapshot raises only the new toast", flood.Diff(hugePlus) is [var one] && one.Id == 3000);
        check("A huge snapshot raises nothing when nothing changed", flood.Diff(huge).Count == 0);

        // An old toast that rolls into the newest-100 window after newer ones left is not an arrival: the watermark
        // — the newest creation time ever seen — is the floor an arrival must reach.
        var window = new NotificationMonitor();
        var oldest = Enumerable.Range(0, NotificationItem.Maximum).Select(i => Item((uint)i, "o", start.AddMinutes(i))).ToList();
        window.Diff(oldest);
        var rolled = oldest.Skip(10).Append(Item(5000, "old-in-window", start.AddMinutes(88)))
            .Append(Item(5001, "new", start.AddMinutes(500))).ToList();
        check("A rolled-in old toast is not raised", window.Diff(rolled) is [var fresh] && fresh.Id == 5001);
        // The center's own toasts stay silent: a raised notification is remembered while it is still there.
        check("The center's own toasts stay silent", window.Diff(rolled).Count == 0);

        // The history is bounded: a long session keeps raising what is new, well past the capacity's churn.
        var churn = new NotificationMonitor();
        churn.Diff([Item(1, "seed", start)]);
        for (int i = 0; i < 3 * NotificationMonitor.HistoryCapacity; i++)
            churn.Diff([Item((uint)(10 + i), "x", start.AddSeconds(i + 1))]);
        check("Detection keeps working after the history churns",
            churn.Diff([Item(99999, "late", start.AddHours(2))]) is [var late] && late.Id == 99999);

        // Disable → enable: a reset forgets the history at once, and the next snapshot is a baseline — the
        // notifications that were there before never replay.
        monitor.Reset();
        check("After a reset the next snapshot is a baseline again", !monitor.Seeded && monitor.Diff(seed).Count == 0);
        check("A toast that arrives after the reset is raised", monitor.Diff([.. seed, Item(6, "d", start.AddMinutes(5))]).Count == 1);

        // Access lost and granted again: the same path.
        monitor.Diff([Item(7, "e", start.AddMinutes(6))]);
        monitor.Reset();
        check("After access returns, nothing replays", monitor.Diff(seed).Count == 0);

        // The settings' own fields normalize: an unknown mode falls back to Compact.
        var settings = new DeckSettings { NotificationContentMode = (NotificationContentMode)99 };
        settings.Normalize();
        check("An unknown content mode reads as Compact", settings.NotificationContentMode == NotificationContentMode.Compact);

        // Backward compatibility: a file saved by a build that still had the sound fields carries them as members
        // this build does not know. The real read entry skips them and keeps the notification settings around them.
        // AppVersion 1.3.0 is the file's own version, so no migration step runs; nothing touches a file on disk.
        var legacy = SettingsMigrations.Read(
            """{"AppVersion":"1.3.0","NotificationsEnabled":true,"NotificationContentMode":2,"NotificationSoundEnabled":true,"NotificationSoundPath":"C:\\old\\chime.wav","NotificationSoundVolume":80}""",
            new Version(1, 3, 0), out var saved);
        check("Old sound fields are ignored and the notification settings survive",
            saved == new Version(1, 3, 0) && legacy.NotificationsEnabled && legacy.NotificationContentMode == NotificationContentMode.Full);
        check("A freshly written settings file carries no sound fields",
            !System.Text.Json.JsonSerializer.Serialize(new DeckSettings()).Contains("NotificationSound"));
    }
}

namespace BrimDeck.Core;

// Decides which notifications are new (BrimDeck.Native.NotificationService). The first snapshot after every
// (re)start of the monitor is the baseline: everything in it is seen, nothing is raised. Every later snapshot is
// diffed against a bounded history of everything ever raised or baselined, keyed by the pair of id and creation
// time — Windows hands out rising ids but reuses them, so the creation time is what tells a reused id from the
// same toast. A toast whose text changed keeps its id and creation time, so an update to a notification the user
// has already been told about is not a new notification.
//
// Two guards keep old toasts quiet. The watermark is the newest creation time any snapshot has shown so far, and
// it only moves forward: an arrival must reach it, so a toast that merely rolls into the newest-100 window as
// newer ones leave the center is not raised, and neither is anything a clock set backwards produces. The history
// keeps the keys of everything seen until it holds more than its capacity, oldest out first — the reader caps a
// snapshot at the newest NotificationItem.Maximum, so a snapshot larger than any bound can never loop: it grows
// the history by at most one cap, and what the center still holds stays seen through the churn.
public sealed class NotificationMonitor
{
    public const int HistoryCapacity = 512;
    private readonly HashSet<(uint Id, DateTimeOffset Created)> _history = [];
    private readonly Queue<(uint Id, DateTimeOffset Created)> _order = [];
    private DateTimeOffset _watermark = DateTimeOffset.MaxValue;
    // Nothing is raised before a baseline exists; Diff seeds it on the first call after a Reset.
    public bool Seeded { get; private set; }

    // Forgets everything at once, releasing the ids it kept: the next Diff seeds a fresh baseline. Used when the
    // monitor is switched off, when access to the center is lost, and after a failed read — in every case what
    // arrives next starts from a baseline, so the notifications that were there before are never replayed.
    public void Reset()
    {
        _history.Clear();
        _order.Clear();
        _watermark = DateTimeOffset.MaxValue;
        Seeded = false;
    }

    // The notifications the center gained since the last call, newest first; empty while seeding and when nothing
    // arrived. The input is clamped to the newest Maximum first, so a snapshot larger than the page's cap can
    // never raise what it trimmed away.
    public IReadOnlyList<NotificationItem> Diff(IReadOnlyList<NotificationItem> snapshot)
    {
        var current = snapshot.Count > NotificationItem.Maximum ? NotificationItem.NewestFirst(snapshot) : snapshot;
        if (!Seeded)
        {
            foreach (var item in current) Remember(item);
            // An empty center has nothing to keep out: everything that appears afterwards is new.
            _watermark = current.Count > 0 ? current.Max(item => item.CreationTime) : DateTimeOffset.MinValue;
            Seeded = true;
            return [];
        }
        List<NotificationItem>? arrivals = null;
        // The batch is judged by the watermark as it stands: an arrival earlier than another in the same snapshot is
        // still newer than everything seen before, and must not be swallowed by the watermark's advance.
        var watermark = _watermark;
        foreach (var item in current)
        {
            // Older than the watermark: an old toast that rolled into the window, or a clock set backwards —
            // both read as old and stay quiet. At the watermark itself a new id counts: two toasts can share one
            // creation time.
            if (item.CreationTime < watermark) continue;
            var key = (item.Id, item.CreationTime);
            if (!_history.Add(key)) continue;
            _order.Enqueue(key);
            while (_order.Count > HistoryCapacity && _order.TryDequeue(out var oldest)) _history.Remove(oldest);
            (arrivals ??= []).Add(item);
        }
        if (current.Count > 0)
        {
            var newest = current.Max(item => item.CreationTime);
            if (newest > _watermark) _watermark = newest;
        }
        return arrivals is null ? [] : NotificationItem.NewestFirst(arrivals);
    }

    private void Remember(NotificationItem item)
    {
        var key = (item.Id, item.CreationTime);
        if (!_history.Add(key)) return;
        _order.Enqueue(key);
        while (_order.Count > HistoryCapacity && _order.TryDequeue(out var oldest)) _history.Remove(oldest);
    }
}

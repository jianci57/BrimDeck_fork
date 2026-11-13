namespace BrimDeck.Core;

// Decides which notifications are new (BrimDeck.Native.NotificationService). The first snapshot after every
// (re)start of the monitor is the baseline: everything in it is seen, nothing is raised. Every later snapshot is
// diffed against a bounded history of everything ever raised or baselined, keyed by the pair of id and creation
// time — Windows hands out rising ids but reuses them, so the creation time is what tells a reused id from the
// same toast. A toast whose text changed keeps its id and creation time, so an update to a notification the user
// has already been told about is not a new notification.
//
// The watermark keeps old toasts that roll into the newest-100 window quiet. A backwards observation clock starts
// a fresh epoch: the current snapshot is a quiet baseline, and future-dated records from the old epoch cannot raise
// the new watermark. History is bounded; normal snapshots add at most NotificationItem.Maximum keys.
public sealed class NotificationMonitor
{
    public const int HistoryCapacity = 512;
    private readonly HashSet<(uint Id, DateTimeOffset Created)> _history = [];
    private readonly Queue<(uint Id, DateTimeOffset Created)> _order = [];
    private DateTimeOffset _watermark = DateTimeOffset.MaxValue;
    private DateTimeOffset? _observedAt;
    private bool _clockRebased;
    // Future records belonging to a previous clock epoch must outlive ordinary history churn. Keys only,
    // bounded by the center's retained records; once the watermark passes one, the watermark protects it instead.
    private readonly HashSet<(uint Id, DateTimeOffset Created)> _previousEpoch = [];
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
        _observedAt = null;
        _clockRebased = false;
        _previousEpoch.Clear();
        Seeded = false;
    }

    // The notifications the center gained since the last call, newest first; empty while seeding and when nothing
    // arrived. The input is clamped to the newest Maximum first, so a snapshot larger than the page's cap can
    // never raise what it trimmed away.
    public IReadOnlyList<NotificationItem> Diff(IReadOnlyList<NotificationItem> snapshot) => Diff(snapshot, DateTimeOffset.Now);

    public IReadOnlyList<NotificationItem> Diff(IReadOnlyList<NotificationItem> snapshot, DateTimeOffset observedAt)
    {
        // Keep the old epoch's ids quiet while the clock catches up. Merely lowering the watermark is insufficient:
        // future-dated toasts still in the center would otherwise raise it again on the next poll.
        bool rolledBack = _observedAt is { } previous && observedAt < previous;
        _observedAt = observedAt;
        if (rolledBack)
        {
            _clockRebased = true;
            _watermark = observedAt;
        }
        // A restarted monitor may first observe the clock only after the rollback. Future records reveal that
        // epoch too; never seed a fresh baseline's watermark beyond the observation clock.
        if (!Seeded && snapshot.Any(item => item.CreationTime > observedAt)) _clockRebased = true;
        if (_clockRebased && (rolledBack || !Seeded))
            foreach (var item in snapshot.Where(item => item.CreationTime > observedAt))
                _previousEpoch.Add((item.Id, item.CreationTime));
        var eligible = _clockRebased
            ? snapshot.Where(item => item.CreationTime <= observedAt && !_previousEpoch.Contains((item.Id, item.CreationTime))).ToList()
            : snapshot;
        var current = eligible.Count > NotificationItem.Maximum ? NotificationItem.NewestFirst(eligible) : eligible;
        if (rolledBack)
        {
            // This snapshot is the baseline in the new clock epoch, including retained old-epoch notifications.
            foreach (var item in snapshot) Remember(item);
            return [];
        }
        if (!Seeded)
        {
            foreach (var item in _clockRebased ? snapshot : current) Remember(item);
            // In a new clock epoch, everything already present is old, including records dated in its future.
            _watermark = _clockRebased ? observedAt : current.Count > 0 ? current.Max(item => item.CreationTime) : DateTimeOffset.MinValue;
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
        _previousEpoch.RemoveWhere(key => key.Created < _watermark);
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

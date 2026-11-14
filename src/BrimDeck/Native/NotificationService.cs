using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Reads the notification center in the background while the feature is switched on (DeckSettings.NotificationsEnabled)
// and raises each new notification once. One service for the whole application, so one reader serves the polling, the
// settings page's status line and its consent dialog: the reader's own one-operation gate covers a poll's snapshot and
// a consent dialog the same way, so neither ever runs behind the other. Nothing here writes a log, removes a
// notification or opens anything: the only output is the item the event carries, which the panels show and drop.
internal sealed class NotificationService : IDisposable
{
    private const double PollSeconds = 2.5;
    private readonly Dispatcher _dispatcher;
    private readonly NotificationReader _reader = new();
    private readonly NotificationMonitor _monitor = new();
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _cancelling;
    private NotificationReader.Access _access;
    private int _busy;
    // Everything a poll computes belongs to the generation it started in; Stop moves to the next one, so a snapshot
    // that lands after a stop-and-start raises nothing from the world it was read in.
    private int _generation;
    private bool _disposed;

    // The newest notification that arrived since the last tick, raised at most once per tick, on the UI thread;
    // several arrivals in one snapshot stand for each other, the newest is raised.
    internal event Action<NotificationItem>? Arrived;
    // The islands must drop what they show because access to the center was lost (denied, or the API stands down).
    internal event Action? Cleared;

    internal NotificationService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    // Where access stands now; the settings page's status line reads this every time it builds.
    internal NotificationAccess AccessNow() => _reader.AccessNow();

    // Asks Windows for the user's consent (the settings page's button). It runs through the reader's one-operation
    // gate, so it never overlaps a poll's snapshot, and an unanswered dialog is cancelled after the reader's cap.
    internal Task<NotificationAccess> RequestAccessAsync(CancellationToken cancelled) => _reader.RequestAccessAsync(cancelled);

    // Switches the background reading on or off (App.ApplyNotificationSettings). Turning it on when it already runs
    // changes nothing: a settings change that does not touch the switch (a volume, a content mode) must not reseed
    // the baseline and miss a notification that arrived meanwhile.
    internal void Reconfigure(bool enabled)
    {
        if (_disposed) return;
        if (!enabled) { Stop(); return; }
        if (_timer is not null) return;
        // The first tick takes the baseline: whatever the center holds then is seen, and only the toasts that arrive
        // after the switch are raised.
        _monitor.Reset();
        _cancelling = new CancellationTokenSource();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(PollSeconds), DispatcherPriority.Background, (_, _) => _ = PollAsync(), _dispatcher);
        // The baseline is taken at once, not after the first interval: a notification arriving in the gap would
        // otherwise be part of the first snapshot and read as if it had been there before the switch.
        _ = PollAsync();
    }

    // Stops the timer, abandons the read in flight (its WinRT operation is cancelled) and forgets the baseline: the
    // feature switched on again starts from what the center holds then. The islands are told to drop what they show
    // by the caller (App), which does so even when no service ever ran.
    private void Stop()
    {
        _generation++;
        _timer?.Stop();
        _timer = null;
        _cancelling?.Cancel();
        _cancelling?.Dispose();
        _cancelling = null;
        _monitor.Reset();
    }

    private async Task PollAsync()
    {
        if (_timer is null || _disposed) return;
        int generation = _generation;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;   // the tick before still reads
        try
        {
            var (state, _) = _reader.AccessNow();
            if (state != NotificationReader.Access.Allowed) { AccessLost(state); return; }
            _access = state;
            var items = await _reader.SnapshotAsync(_cancelling?.Token ?? default);
            if (_disposed || generation != _generation || _timer is null) return;
            // The permission may have been withdrawn while the snapshot was in the air (the user flipped it in the
            // system settings): what it read is not raised then, and the islands drop what they show.
            var (now, _) = _reader.AccessNow();
            if (now != NotificationReader.Access.Allowed) { AccessLost(now); return; }
            var arrivals = _monitor.Diff(items);
            if (arrivals.Count > 0) Arrived?.Invoke(arrivals[0]);
        }
        catch (NotificationBusyException) { /* The previous read is still alive; the next tick tries again. */ }
        catch (OperationCanceledException) { /* Stopped while reading; an abandoned read raises nothing. */ }
        catch
        {
            // A failing API must not end the timer, and must not replay either: the next successful snapshot is a
            // fresh baseline, so a notification that arrived during the failure is not raised. A poll of an older
            // generation — one whose timer was stopped while its read was still going — resets nothing: its world
            // is gone, and the running session's baseline is not its business.
            if (generation == _generation) _monitor.Reset();
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    // Access is not Allowed: the baseline is forgotten (granting it again starts from what the center holds then),
    // and the islands drop what they show — but only when they were allowed to show something a moment ago.
    private void AccessLost(NotificationReader.Access state)
    {
        _monitor.Reset();
        if (_access == NotificationReader.Access.Allowed) Cleared?.Invoke();
        _access = state;
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}

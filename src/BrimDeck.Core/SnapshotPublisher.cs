namespace BrimDeck.Core;

// Hands a growing result to a callback at most once per interval: the first call goes straight through, a call that comes
// too soon is held and sent when the interval ends, replaced by any newer one. Dispose drops what is still held (the caller
// has the whole result by then) and makes later calls do nothing. The callback runs under the lock, so it must be quick.
public sealed class SnapshotPublisher(Action<IReadOnlyList<ProviderSnapshot>> publish, TimeSpan interval) : IDisposable
{
    private readonly object _sync = new();
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private IReadOnlyList<ProviderSnapshot>? _held;
    private Timer? _timer;
    private bool _disposed;

    public void Publish(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        lock (_sync)
        {
            if (_disposed) return;
            // One reading of the clock: read twice, the wait left could come out below zero, which Timer rejects.
            var elapsed = _clock.Elapsed;
            if (!_clock.IsRunning || elapsed >= interval) { Send(snapshots); return; }
            _held = snapshots;
            _timer ??= new Timer(_ => Flush(), null, interval - elapsed, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        lock (_sync)
        {
            _timer?.Dispose(); _timer = null;
            if (_disposed || _held is not { } snapshots) return;
            Send(snapshots);
        }
    }

    private void Send(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        _held = null; _timer?.Dispose(); _timer = null; _clock.Restart();
        publish(snapshots);
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; _held = null; _timer?.Dispose(); _timer = null; }
    }
}

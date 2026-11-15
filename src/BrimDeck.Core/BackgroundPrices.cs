namespace BrimDeck.Core;

// Runs the price refresh beside the usage read instead of in front of it: Start returns at once, a refresh still under way is
// not started a second time, and landed is called when one ends without failing and reports that the prices changed (so costs are
// worked out again; a refresh that found the catalog current changes nothing and tells nobody). A failed refresh leaves the prices as they were; the next Start tries again.
public sealed class BackgroundPrices(Func<CancellationToken, Task<bool>> refresh, Action landed)
{
    private Task? _running;

    public void Start(CancellationToken cancellation)
    {
        if (_running is { IsCompleted: false }) return;
        _running = Task.Run(async () =>
        {
            bool changed;
            try { changed = await refresh(cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("BrimDeck prices: " + ex.GetType().Name); return; }
            if (changed) landed();
        }, CancellationToken.None);
    }

    public Task? Running => _running;
}

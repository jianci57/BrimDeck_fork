using BrimDeck.Core;

// FrameTimeline + GpuWork: deterministic small scenarios, no general framework. Times are ticks at a
// frequency of 1000 (1 ms per tick), so a packet of 10..20 is 10 ms of busy time.
internal static class FrameTimelineTests
{
    private static FrameTraceEvent Present(long t, int pid = 7, long chain = 1) => new(t, FrameTraceKind.Present, Pid: pid, Chain: chain);

    private static void Setup(FrameTimeline t, long at = 1, int pid = 7, ulong ctx = 11, uint node = 0, ulong device = 1)
    {
        t.Add(new(at, FrameTraceKind.DeviceStart, Device: device, Adapter: 1));
        t.Add(new(at + 1, FrameTraceKind.ContextStart, Pid: pid, Context: ctx, Device: device, Node: node));
    }

    // A legacy DMA packet: submitted, running, done.
    private static void Packet(FrameTimeline t, long from, long end, ulong ctx = 11, uint seq = 1, int pid = 7)
    {
        t.Add(new(from, FrameTraceKind.QueueStart, Pid: pid, Context: ctx, Sequence: seq));
        t.Add(new(from, FrameTraceKind.DmaStart, Context: ctx, Sequence: seq));
        t.Add(new(end, FrameTraceKind.DmaStop, Context: ctx, Sequence: seq));
    }

    private static void Near(Action<string, bool> check, string name, double? actual, double expected) =>
        check($"{name}: actual={actual}, expected={expected}", actual is { } v && Math.Abs(v - expected) < 1e-8);

    public static void Run(Action<string, bool> check)
    {
        Ordering(check);
        Windows(check);
        Selection(check);
        Legacy(check);
        Hags(check);
        Recovery(check);
    }

    // Records retained from before a cutoff (a future arrival) must be re-sorted with newer ones; a truly
    // late record is dropped for frames, and for work it must not rewind the paid timeline.
    private static void Ordering(Action<string, bool> check)
    {
        var t = new FrameTimeline(1000);
        t.Read(0);
        t.Add(Present(30));
        t.Read(10);
        t.Add(Present(20));
        check("a retained future present is re-sorted with newer arrivals", t.Read(40)!.Select(7) is { Count: 2 } s && s.Gaps.SequenceEqual([10.0]));
        t.Add(Present(15));
        t.Add(Present(50));
        check("a late present does not rewind its predecessor's gaps", t.Read(60)!.Select(7) is { Count: 1 } p && p.Gaps.SequenceEqual([20.0]));

        // Equal timestamps keep arrival order: a second present at the same time adds a frame but no gap.
        var same = new FrameTimeline(1000);
        same.Read(0);
        same.Add(Present(5));
        same.Add(Present(5));
        check("presents at one timestamp count twice with no gap", same.Read(10)!.Select(7) is { Count: 2 } q && q.Gaps.Count == 0);

        // A stop and a restart of one context at the same timestamp: the later arrival must own the handle.
        var reuse = new FrameTimeline(1000);
        reuse.Read(0);
        Setup(reuse, pid: 7);
        Packet(reuse, 10, 20);
        reuse.Add(new(30, FrameTraceKind.ContextStop, Context: 11));
        reuse.Add(new(30, FrameTraceKind.ContextStart, Pid: 9, Context: 11, Device: 1, Node: 0));
        Packet(reuse, 40, 50, ctx: 11, seq: 2, pid: 9);
        reuse.Add(Present(55, pid: 9));
        var w = reuse.Read(60)!;
        Near(check, "a handle reused at one timestamp is owned by its later start", w.GpuMilliseconds(w.Select(9)), 10);
        check("the earlier owner of a reused handle is gone", w.Select(7) is null);
    }

    // One window and several: nothing lost, nothing counted twice, long work split across cutoffs.
    private static void Windows(Action<string, bool> check)
    {
        var t = new FrameTimeline(1000);
        t.Read(0);
        t.Add(Present(15));
        t.Add(Present(25));
        check("the first window keeps its own frame only", t.Read(20)!.Select(7) is { Count: 1 });
        var second = t.Read(30)!;
        check("the next window keeps the next frame only, with the gap", second.Select(7) is { Count: 1 } s && s.Gaps.SequenceEqual([10.0]));

        // A packet still running at the cutoff pays each window its own slice, once.
        var open = new FrameTimeline(1000);
        open.Read(0);
        Setup(open);
        Packet(open, 10, 25);
        open.Add(Present(12));
        open.Add(Present(20));
        open.Add(Present(27));
        var a = open.Read(15)!;
        var b = open.Read(30)!;
        Near(check, "an open packet pays its first slice", a.GpuMilliseconds(a.Select(7)), 5);
        Near(check, "an open packet pays its second slice", b.GpuMilliseconds(b.Select(7)), 5);
        Near(check, "an open packet is not paid twice across windows", a.GpuMilliseconds(a.Select(7))! + b.GpuMilliseconds(b.Select(7))!, 10);

        // A single PID is read once as the numerator; the denominator is the chain the window selected.
        var one = new FrameTimeline(1000);
        one.Read(0);
        Setup(one);
        Packet(one, 10, 30);
        one.Add(Present(11));
        one.Add(Present(21));
        one.Add(Present(25, chain: 2));
        var w = one.Read(40)!;
        Near(check, "the numerator is read once, over the chosen chain's frames", w.GpuMilliseconds(w.Select(7)), 10);
        check("a chain without frames is not chosen", w.Select(7)!.Chain == 1);
    }

    // Present ownership is parent-first; a busier descendant is not borrowed, and gaps belong to the winner.
    private static void Selection(Action<string, bool> check)
    {
        var t = new FrameTimeline(1000);
        t.Read(0);
        t.Add(Present(1));
        t.Add(Present(2, 8));
        t.Add(Present(3, 8));
        var w = t.Read(10)!;
        check("the owner chain wins without borrowing a busier child", w.Select(7, p => p == 8) is { Pid: 7, Count: 1 } s && s.Gaps.Count == 0);
        check("a child is chosen only through the descendant predicate", w.Select(7) is { Pid: 7 } && w.Select(8, p => p == 8) is { Pid: 8, Count: 2 } c && c.Gaps.SequenceEqual([1.0]));
        check("no owner and no descendant accepted gives nothing", w.Select(9) is null && w.Select(9, p => p == 9) is null);
    }

    // Legacy DMA packets run on an adapter+node FIFO: one head at a time, parallel nodes are a union.
    private static void Legacy(Action<string, bool> check)
    {
        var t = new FrameTimeline(1000);
        t.Read(0);
        Setup(t);
        Packet(t, 10, 20);
        Packet(t, 15, 30, ctx: 11, seq: 2);
        t.Add(Present(35));
        var fifoW = t.Read(40)!;
        Near(check, "a packet behind the FIFO head starts only when the head ends", fifoW.GpuMilliseconds(fifoW.Select(7)), 20);

        // A duplicate submission (same context and sequence) must not double the busy time.
        var dup = new FrameTimeline(1000);
        dup.Read(0);
        Setup(dup);
        dup.Add(new(10, FrameTraceKind.QueueStart, Pid: 7, Context: 11, Sequence: 1));
        dup.Add(new(10, FrameTraceKind.DmaStart, Context: 11, Sequence: 1));
        dup.Add(new(12, FrameTraceKind.DmaStart, Context: 11, Sequence: 1));
        dup.Add(new(20, FrameTraceKind.DmaStop, Context: 11, Sequence: 1));
        dup.Add(Present(25));
        var dupW = dup.Read(30)!;
        Near(check, "a duplicated submission is not counted twice", dupW.GpuMilliseconds(dupW.Select(7)), 10);

        // Two engines of one process run in parallel: the union, not the sum.
        var both = new FrameTimeline(1000);
        both.Read(0);
        Setup(both);
        Setup(both, ctx: 12, node: 1);
        Packet(both, 10, 18);
        Packet(both, 12, 20, ctx: 12);
        both.Add(Present(25));
        var w = both.Read(30)!;
        Near(check, "two engines union to the overlap, not the sum", w.GpuMilliseconds(w.Select(7)), 10);
    }

    // HAGS: a FIFO per ParentDxgHwQueue, queue packets as the execution proxy, waits block without busy time.
    private static void Hags(Action<string, bool> check)
    {
        var t = new FrameTimeline(1000);
        t.Read(0);
        Setup(t);
        // The payload's ParentDxgHwQueue (context 11) names the hContext (22) of later queue packets.
        t.Add(new(2, FrameTraceKind.HwQueueStart, Context: 11, Device: 22));
        // A wait queued first blocks the head; the real work behind it runs only from the wait's exit,
        // so its queued time (12..20) is not busy.
        t.Add(new(10, FrameTraceKind.QueueStart, Pid: 7, Context: 22, Sequence: 1, Wait: true));
        t.Add(new(12, FrameTraceKind.QueueStart, Pid: 7, Context: 22, Sequence: 2));
        t.Add(new(20, FrameTraceKind.QueueStop, Context: 22, Sequence: 1));
        t.Add(new(30, FrameTraceKind.QueueStop, Context: 22, Sequence: 2));
        t.Add(Present(35));
        var hagsW = t.Read(40)!;
        Near(check, "HAGS busy time starts when the wait leaves the head, not when work was queued", hagsW.GpuMilliseconds(hagsW.Select(7)), 10);

        // A wait that stays ahead of real work keeps the FIFO blocked.
        var blocked = new FrameTimeline(1000);
        blocked.Read(0);
        Setup(blocked);
        blocked.Add(new(2, FrameTraceKind.HwQueueStart, Context: 11, Device: 22));
        blocked.Add(new(10, FrameTraceKind.QueueStart, Pid: 7, Context: 22, Sequence: 1, Wait: true));
        blocked.Add(new(12, FrameTraceKind.QueueStart, Pid: 7, Context: 22, Sequence: 2));
        blocked.Add(new(22, FrameTraceKind.QueueStop, Context: 22, Sequence: 2));
        blocked.Add(Present(25));
        var blockedW = blocked.Read(30)!;
        check("a head wait blocks real work behind it", blockedW.GpuMilliseconds(blockedW.Select(7)) is null);

        // A completion with no tracked packet (a signal this session never subscribed to) must not undo
        // the work that completed just before it.
        var signal = new FrameTimeline(1000);
        signal.Read(0);
        Setup(signal);
        signal.Add(new(2, FrameTraceKind.HwQueueStart, Context: 11, Device: 22));
        signal.Add(new(10, FrameTraceKind.QueueStart, Pid: 7, Context: 22, Sequence: 1));
        signal.Add(new(20, FrameTraceKind.QueueStop, Context: 22, Sequence: 1));
        signal.Add(new(21, FrameTraceKind.QueueStop, Context: 22, Sequence: 9999)); // untracked signal
        signal.Add(Present(25));
        var signalW = signal.Read(30)!;
        Near(check, "an untracked signal completion leaves the finished work counted", signalW.GpuMilliseconds(signalW.Select(7)), 10);

        // A tracked packet whose own stop never arrived (a stop lands behind it) still leaves the window
        // unknown, and the next clean window recovers.
        var missing = new FrameTimeline(1000);
        missing.Read(0);
        Setup(missing);
        missing.Add(new(2, FrameTraceKind.HwQueueStart, Context: 11, Device: 22));
        missing.Add(new(10, FrameTraceKind.QueueStart, Pid: 7, Context: 22, Sequence: 1));
        missing.Add(new(11, FrameTraceKind.QueueStart, Pid: 7, Context: 22, Sequence: 2));
        missing.Add(new(20, FrameTraceKind.QueueStop, Context: 22, Sequence: 2)); // stop 1 never came
        missing.Add(Present(25));
        var missingW = missing.Read(30)!;
        check("a tracked packet missing its own stop leaves the window unknown", missingW.GpuMilliseconds(missingW.Select(7)) is null);
        // A HAGS context runs queue packets, not DMA: submit and complete with queue events.
        missing.Add(new(40, FrameTraceKind.QueueStart, Pid: 7, Context: 22, Sequence: 3));
        missing.Add(new(50, FrameTraceKind.QueueStop, Context: 22, Sequence: 3));
        missing.Add(Present(55));
        var recovered = missing.Read(60)!;
        Near(check, "a window after a missed stop recovers with its own clean packet", recovered.GpuMilliseconds(recovered.Select(7)), 10);
    }

    private static void Recovery(Action<string, bool> check)
    {
        // A metadata snapshot arriving after its timestamp's window still allows a future clean window.
        var late = new FrameTimeline(1000);
        late.Read(0);
        late.Read(100);
        Setup(late, 50);
        Packet(late, 110, 120);
        late.Add(Present(125));
        late.Read(150);
        Packet(late, 160, 170, seq: 2);
        late.Add(Present(175));
        var w = late.Read(200)!;
        Near(check, "late metadata recovers a future clean window", w.GpuMilliseconds(w.Select(7)), 10);

        // A packet lost (stale past 3×frequency) makes the process unknown; a later packet completion recovers it.
        var lost = new FrameTimeline(1000);
        lost.Read(0);
        Setup(lost);
        lost.Add(new(10, FrameTraceKind.QueueStart, Pid: 7, Context: 11, Sequence: 1));
        lost.Add(new(10, FrameTraceKind.DmaStart, Context: 11, Sequence: 1));
        // A frame in the same window so the denominator exists; the busy side must be unknown, not zero.
        lost.Add(Present(100));
        // Never completed; the window that expires it must report nothing rather than a guess.
        var unknown = lost.Read(3200)!;
        check("a lost packet leaves the process unknown, not zero", unknown.Select(7) is not null && unknown.GpuMilliseconds(unknown.Select(7)) is null);
        // Recovery: the first completion after the loss re-establishes the queue, the next one is clean.
        Packet(lost, 3210, 3230, seq: 2);
        lost.Add(Present(3235));
        var recovering = lost.Read(3240)!;
        check("the first packet after a loss is still untrusted", recovering.GpuMilliseconds(recovering.Select(7)) is null);
        Packet(lost, 3250, 3260, seq: 3);
        lost.Add(Present(3265));
        var clean = lost.Read(3280)!;
        Near(check, "a packet of its own completion recovers the process", clean.GpuMilliseconds(clean.Select(7)), 10);

        // A stop of a previous owner arriving after the handle was reused must never tear the new owner down.
        var stale = new FrameTimeline(1000);
        stale.Read(0);
        Setup(stale);
        Packet(stale, 10, 20);
        stale.Add(Present(25));
        stale.Read(40);
        stale.Add(new(50, FrameTraceKind.ContextStop, Context: 11));
        stale.Add(new(60, FrameTraceKind.ContextStart, Pid: 8, Context: 11, Device: 1));
        Packet(stale, 70, 80, pid: 8);
        stale.Add(Present(85, pid: 8));
        stale.Read(100);
        stale.Add(new(45, FrameTraceKind.ContextStop, Context: 11)); // the old owner's stop, arriving late.
        Packet(stale, 110, 120, seq: 2, pid: 8);
        stale.Add(Present(125, pid: 8));
        var kept = stale.Read(150)!;
        Near(check, "a stale old-owner stop preserves the reused context", kept.GpuMilliseconds(kept.Select(8)), 10);

        // Reset leaves nothing behind.
        var fresh = new FrameTimeline(1000);
        fresh.Read(0);
        Setup(fresh);
        Packet(fresh, 10, 20);
        fresh.Add(Present(15));
        fresh.Reset();
        fresh.Read(0);
        fresh.Add(Present(5));
        var after = fresh.Read(10)!;
        check("after Reset the timeline starts clean", after.Select(7) is { Count: 1 } r && r.Gaps.Count == 0 && after.GpuMilliseconds(r) is null);
        check("after Reset the first reading still only primes", fresh.Read(20) is not null && after.GpuMilliseconds(after.Select(7)) is null);
    }
}

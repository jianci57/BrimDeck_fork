
using System.Diagnostics;

namespace BrimDeck.Core;

// GPU execution reconstructed from PresentMon 2.5.1's DxgKrnl event model (PresentData/GpuTrace.cpp, MIT).
// Legacy DMA packets share an adapter/node FIFO. HAGS has a FIFO per ParentDxgHwQueue and uses queue packets as
// an execution proxy. Only a FIFO's head runs; waits block it without contributing busy time. Parallel nodes
// contribute the union of each process's execution, rather than the sum. The sampling thread owns this object.
public sealed class GpuWork
{
    private const int MaxMappings = 16_384, MaxPackets = 4096, MaxSpans = 16_384;
    private readonly long _stale;
    private readonly Dictionary<ulong, ulong> _devices = [];
    private readonly Dictionary<ulong, long> _deviceAt = [];
    private readonly Dictionary<ulong, Context> _contexts = [];
    private readonly Dictionary<(ulong Adapter, uint Node), WorkQueue> _nodes = [];
    private readonly Dictionary<int, ProcessWork> _processes = [];
    private readonly HashSet<int> _invalid = [];
    private long _clock = long.MinValue, _paidThrough = long.MinValue;

    private sealed class Context
    {
        public ulong Device, Parent;
        public uint Node;
        public int Pid;
        public bool Metadata, Hw, MissingWork;
        public long MetadataAt = long.MinValue;
        public WorkQueue? Queue;
    }
    private sealed class WorkQueue
    {
        public readonly List<Packet> Packets = [];
        public int RunningPid;
        public long Started;
        public bool Recovering;
        public readonly HashSet<int> UncertainPids = [];
    }
    private sealed class Packet(ulong context, uint sequence, int pid, bool wait)
    {
        public readonly ulong Context = context;
        public readonly uint Sequence = sequence;
        public readonly int Pid = pid;
        public readonly bool Wait = wait;
        public bool Completed;
    }
    private sealed class ProcessWork
    {
        public int Running;
        public long Open;
        public bool Known;
        public readonly List<(long From, long Through)> Closed = [];
    }

    public GpuWork() : this(Stopwatch.Frequency) { }
    public GpuWork(long frequency)
    {
        if (frequency <= 0 || frequency > long.MaxValue / 3) throw new ArgumentOutOfRangeException(nameof(frequency));
        _stale = frequency * 3;
    }

    public void Apply(FrameTraceEvent e)
    {
        if (e.Time < _clock || e.Time < _paidThrough) { Late(e); return; }
        _clock = e.Time;
        switch (e.Kind)
        {
            case FrameTraceKind.DeviceStart:
            case FrameTraceKind.ContextStart:
            case FrameTraceKind.HwQueueStart:
                RegisterMetadata(e, e.Time);
                break;
            case FrameTraceKind.DeviceStop:
                RemoveDevice(e.Device, e.Time);
                break;
            case FrameTraceKind.ContextStop:
                RemoveContext(e.Context, e.Time);
                break;
            case FrameTraceKind.QueueStart:
                var submitted = GetContext(e.Context);
                if (submitted is null) { Invalidate(e.Pid); break; }
                Claim(submitted, e.Pid, e.Time);
                if (submitted.Queue is null) Missing(submitted);
                else if (submitted.Hw) Enqueue(submitted.Queue, e.Context, e.Sequence, submitted.Pid, e.Wait, e.Time);
                break;
            case FrameTraceKind.QueueStop:
                Work(e, dma: false, complete: true);
                break;
            case FrameTraceKind.DmaStart:
                Work(e, dma: true, complete: false);
                break;
            case FrameTraceKind.DmaStop:
                Work(e, dma: true, complete: true);
                break;
        }
    }

    // Events before an already-paid cutoff cannot repair history. Packet events (queue and DMA) only
    // invalidate: their FIFO position is unknowable, so the affected work goes unknown until a packet of
    // its own completes again. Metadata (device, context, hardware queue) carries a timestamp, but a late
    // rundown event says what the state was, not what ran: it is registered as current state at its own
    // event time without touching paid history, so windows that follow can still account busy time — but
    // never over a mapping a newer event has already replaced: each mapping knows the event time it came
    // from (MetadataAt), so a reused handle's late old stop cannot tear the new owner's registration
    // down. A late packet against a reused handle makes the new owner unknown until its next completion,
    // which recovers it.
    public void Late(FrameTraceEvent e)
    {
        long time = Math.Max(e.Time, Math.Max(_clock, _paidThrough));
        switch (e.Kind)
        {
            case FrameTraceKind.DeviceStart or FrameTraceKind.ContextStart or FrameTraceKind.HwQueueStart:
                RegisterMetadata(e, time);
                return;
            case FrameTraceKind.DeviceStop:
                if (!_deviceAt.TryGetValue(e.Device, out var deviceAt) || deviceAt <= e.Time) RemoveDevice(e.Device, time);
                return;
            case FrameTraceKind.ContextStop:
                if (_contexts.TryGetValue(e.Context, out var gone) && gone.MetadataAt <= e.Time) RemoveContext(e.Context, time);
                return;
        }
        // A late packet: the queues its context could have run on go unknown and recover only through a
        // packet completion observed after this point.
        var queues = new HashSet<WorkQueue>();
        if (_contexts.TryGetValue(e.Context, out var owner))
        {
            Invalidate(owner.Pid);
            if (owner.Queue is not null) queues.Add(owner.Queue);
        }
        if (e.Kind is FrameTraceKind.DeviceStart or FrameTraceKind.DeviceStop)
            foreach (var c in _contexts.Values)
                if (c.Device == e.Device && c.Queue is not null) queues.Add(c.Queue);
        foreach (var queue in queues) Lose(queue, time);
        Invalidate(e.Pid);
    }

    // Device, context and hardware-queue mappings, from Apply (events in order) and Late (a rundown or an
    // out-of-order event): registering again replaces the older mapping, tearing the old one down at the
    // teardown time; a registration older than the mapping's own goes nowhere.
    private void RegisterMetadata(in FrameTraceEvent e, long teardown)
    {
        switch (e.Kind)
        {
            case FrameTraceKind.DeviceStart:
                if (_deviceAt.TryGetValue(e.Device, out var at) && at > e.Time) return;
                if (_devices.TryGetValue(e.Device, out var adapter) && adapter != e.Adapter) RemoveDevice(e.Device, teardown);
                if (_devices.Count < MaxMappings || _devices.ContainsKey(e.Device))
                { _devices[e.Device] = e.Adapter; _deviceAt[e.Device] = e.Time; }
                Resolve();
                return;
            case FrameTraceKind.ContextStart:
                var context = GetContext(e.Context);
                if (context is null) { Invalidate(e.Pid); return; }
                if (context.MetadataAt > e.Time) return;
                if (context.Hw || (context.Metadata && (context.Device != e.Device || context.Node != e.Node
                    || (context.Pid != 0 && e.Pid != 0 && context.Pid != e.Pid))))
                {
                    RemoveContext(e.Context, teardown);
                    context = GetContext(e.Context)!;
                }
                context.Metadata = true; context.Device = e.Device; context.Node = e.Node; context.MetadataAt = e.Time;
                Claim(context, e.Pid, teardown);
                Resolve();
                return;
            case FrameTraceKind.HwQueueStart:
                // The payload's ParentDxgHwQueue is the hContext used by subsequent QueuePacket events.
                if (e.Device == 0 || e.Context == e.Device) return;
                GetContext(e.Context);
                var hw = GetContext(e.Device);
                if (hw is null) return;
                if (hw.MetadataAt > e.Time) return;
                if (hw.Metadata && (!hw.Hw || hw.Parent != e.Context))
                {
                    RemoveContext(e.Device, teardown);
                    hw = GetContext(e.Device)!;
                }
                hw.Metadata = true; hw.Hw = true; hw.Parent = e.Context; hw.MetadataAt = e.Time;
                Resolve();
                return;
        }
    }

    // Apply only events through this boundary before reading. Closed and still-open spans are clipped to the
    // same interval as the present count; splitting an open span here keeps long work in its correct windows.
    public Dictionary<int, long> Read(long from, long through)
    {
        if (through < from || through < _clock || from < _paidThrough) throw new ArgumentOutOfRangeException(nameof(through));
        foreach (var queue in Queues()) Expire(queue, through);
        var result = new Dictionary<int, long>();
        foreach (var (pid, process) in _processes)
        {
            long ticks = 0;
            foreach (var span in process.Closed) ticks += Overlap(span.From, span.Through, from, through);
            if (process.Running > 0)
            {
                ticks += Overlap(process.Open, through, from, through);
                process.Open = through;
            }
            bool unresolved = _contexts.Values.Any(c => c.Pid == pid && (c.Queue is null || c.MissingWork))
                || Queues().Any(q => q.Recovering && q.UncertainPids.Contains(pid));
            if (process.Known && !_invalid.Contains(pid) && !unresolved) result[pid] = ticks;
            process.Closed.Clear();
        }
        _invalid.Clear();
        _clock = _paidThrough = through;
        var owners = _contexts.Values.Select(c => c.Pid).ToHashSet();
        foreach (var pid in _processes.Where(p => !p.Value.Known && p.Value.Running == 0 && !owners.Contains(p.Key)).Select(p => p.Key).ToArray())
            _processes.Remove(pid);
        return result;
    }

    public void Reset()
    {
        _devices.Clear(); _deviceAt.Clear(); _contexts.Clear(); _nodes.Clear(); _processes.Clear(); _invalid.Clear();
        _clock = _paidThrough = long.MinValue;
    }

    private static long Overlap(long start, long end, long from, long through) => Math.Max(0, Math.Min(end, through) - Math.Max(start, from));
    private Context? GetContext(ulong handle)
    {
        if (handle == 0) return null;
        if (_contexts.TryGetValue(handle, out var context)) return context;
        if (_contexts.Count == MaxMappings) return null;
        return _contexts[handle] = new Context();
    }
    private ProcessWork? Process(int pid)
    {
        if (pid <= 0) return null;
        if (_processes.TryGetValue(pid, out var process)) return process;
        if (_processes.Count == MaxMappings) return null;
        return _processes[pid] = new ProcessWork();
    }
    private void Invalidate(int pid)
    {
        if (pid <= 0) return;
        var process = Process(pid);
        if (process is null) return;
        process.Known = false;
        _invalid.Add(pid);
    }
    private void Missing(Context context)
    {
        context.MissingWork = true;
        Invalidate(context.Pid);
    }
    private void Claim(Context context, int pid, long time)
    {
        if (pid <= 0) return;
        if (context.Pid != 0 && context.Pid != pid)
        {
            Invalidate(context.Pid);
            if (context.Queue is not null) Lose(context.Queue, time);
        }
        context.Pid = pid;
        if (context.MissingWork || (context.Queue?.Packets.Any(p => p.Pid == 0) ?? false)) Invalidate(pid);
        if (!context.Hw)
            foreach (var child in _contexts.Values)
                if (child.Hw && _contexts.TryGetValue(child.Parent, out var parent) && ReferenceEquals(parent, context) && child.Pid == 0)
                    child.Pid = pid;
    }
    private void Resolve()
    {
        foreach (var context in _contexts.Values)
            if (context.Metadata && !context.Hw && context.Queue is null && _devices.TryGetValue(context.Device, out var adapter))
            {
                var key = (adapter, context.Node);
                if (!_nodes.TryGetValue(key, out var queue))
                {
                    if (_nodes.Count == MaxMappings) continue;
                    _nodes[key] = queue = new WorkQueue();
                }
                context.Queue = queue;
                if (context.MissingWork)
                {
                    Lose(queue, _clock); context.MissingWork = false;
                    if (context.Pid > 0) queue.UncertainPids.Add(context.Pid);
                }
            }
        foreach (var context in _contexts.Values)
            if (context.Hw && _contexts.TryGetValue(context.Parent, out var parent) && parent.Queue is not null)
            {
                context.Queue ??= new WorkQueue();
                if (context.Pid == 0) context.Pid = parent.Pid;
                if (context.MissingWork)
                {
                    Lose(context.Queue, _clock); context.MissingWork = false;
                    if (context.Pid > 0) context.Queue.UncertainPids.Add(context.Pid);
                }
            }
    }
    private IEnumerable<WorkQueue> Queues() => _contexts.Values.Select(c => c.Queue).OfType<WorkQueue>().Distinct();

    private void Work(FrameTraceEvent e, bool dma, bool complete)
    {
        if (dma && e.Sequence == 0) return; // Preemption/quantum notifications are not work.
        var context = GetContext(e.Context);
        if (context is null) return;
        if (context.Queue is null) { Missing(context); return; }
        if (dma == context.Hw) return; // DMA for legacy; queue packets only for confirmed HAGS queues.
        if (complete) Complete(context.Queue, e.Context, e.Sequence, context.Pid, e.Time);
        else Enqueue(context.Queue, e.Context, e.Sequence, context.Pid, false, e.Time);
    }
    private void Enqueue(WorkQueue queue, ulong context, uint sequence, int pid, bool wait, long time)
    {
        Expire(queue, time);
        if (queue.Packets.Any(p => p.Context == context && p.Sequence == sequence)) return;
        if (queue.Packets.Count == MaxPackets) Lose(queue, time);
        queue.Packets.Add(new Packet(context, sequence, pid, wait));
        if (queue.Recovering && pid > 0) queue.UncertainPids.Add(pid);
        if (queue.Packets.Count == 1) { queue.Started = time; Start(queue, time); }
    }
    private void Complete(WorkQueue queue, ulong context, uint sequence, int pid, long time)
    {
        Expire(queue, time);
        // Sequence IDs are not global; exact pairs also handle uint wrap without numeric ordering guesses.
        int index = queue.Packets.FindIndex(p => p.Context == context && p.Sequence == sequence);
        // A completion with no tracked packet may be a signal this session never subscribed to (a sync
        // fence, a monitored fence, an untracked notification) or a completion of work submitted before
        // capture began; on its own an unmatched completion is no evidence that tracked work was lost.
        // PresentMon's CompleteWork returns for it without touching any state. Treat it the same — return
        // without invalidating, since the running packet, the open span and what was already paid stay
        // exactly as they were. Only a completion that lands behind its tracked packet (index > 0, a
        // tracked packet was missed) still invalidates; real loss shows through the tracked packets
        // themselves, and a lost batch as a whole is still Expire's, Late's and the overflow's business.
        if (index < 0) return;
        var packet = queue.Packets[index];
        if (index > 0 && packet.Wait) { packet.Completed = true; return; }
        if (index > 0 || queue.Recovering)
        {
            foreach (var entry in queue.Packets.Take(index + 1)) Invalidate(entry.Pid);
            foreach (int uncertain in queue.UncertainPids) Invalidate(uncertain);
        }
        Stop(queue, time);
        queue.Packets.RemoveRange(0, index + 1);
        queue.Recovering = false; queue.UncertainPids.Clear();
        queue.Started = time;
        Start(queue, time);
    }
    private void Start(WorkQueue queue, long time)
    {
        while (queue.Packets.Count > 0 && queue.Packets[0].Wait && queue.Packets[0].Completed) queue.Packets.RemoveAt(0);
        if (queue.Recovering || queue.Packets.Count == 0) return;
        queue.Started = time;
        if (queue.Packets[0].Wait) return;
        int pid = queue.Packets[0].Pid;
        var process = Process(pid);
        if (process is null) return; // An unknown owner's packet still blocks the FIFO.
        queue.RunningPid = pid;
        queue.Started = time;
        if (process.Running++ == 0) process.Open = time;
        process.Known = true;
    }
    private void Stop(WorkQueue queue, long time)
    {
        int pid = queue.RunningPid;
        queue.RunningPid = 0;
        if (pid == 0 || !_processes.TryGetValue(pid, out var process) || process.Running == 0) return;
        if (--process.Running != 0) return;
        if (process.Closed.Count == MaxSpans) Invalidate(pid);
        else if (time > process.Open) process.Closed.Add((process.Open, time));
    }
    private void Expire(WorkQueue queue, long time)
    {
        if (queue.Packets.Count != 0 && time - queue.Started > _stale) Lose(queue, time);
    }
    private void Lose(WorkQueue queue, long time)
    {
        foreach (var packet in queue.Packets) Invalidate(packet.Pid);
        Stop(queue, time);
        queue.Packets.Clear();
        queue.Recovering = true;
    }
    private void RemoveDevice(ulong device, long time)
    {
        foreach (var handle in _contexts.Where(p => !p.Value.Hw && p.Value.Device == device).Select(p => p.Key).ToArray())
            RemoveContext(handle, time);
        _devices.Remove(device);
        _deviceAt.Remove(device);
    }
    private void RemoveContext(ulong handle, long time)
    {
        if (!_contexts.TryGetValue(handle, out var context)) return;
        foreach (var child in _contexts.Where(p => p.Value.Hw && p.Value.Parent == handle).Select(p => p.Key).ToArray())
            RemoveContext(child, time);
        if (context.Queue is { } queue && queue.Packets.Any(p => p.Context == handle))
        {
            foreach (var packet in queue.Packets) Invalidate(packet.Pid);
            bool head = queue.Packets[0].Context == handle;
            if (head) Stop(queue, time);
            queue.Packets.RemoveAll(p => p.Context == handle);
            if (head) Start(queue, time);
        }
        _contexts.Remove(handle);
        if (context.Pid != 0 && !_contexts.Values.Any(c => c.Pid == context.Pid)) Invalidate(context.Pid);
        foreach (var key in _nodes.Where(p => p.Value.Packets.Count == 0 && !_contexts.Values.Any(c => ReferenceEquals(c.Queue, p.Value))).Select(p => p.Key).ToArray())
            _nodes.Remove(key);
    }
}

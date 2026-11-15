using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Reads the system for the monitor page on a thread of its own, only while a panel shows the page and for a while after (the
// memory mode's ReadingsLinger), so the page costs nothing while it is not looked at. The structure follows ftop's SystemSampler by Nongfsq
// (https://github.com/Nongfsq/ftop, MIT License; see ThirdPartyNotices.txt): one provider per module keeps the counters
// it needs for deltas and returns a Reading; nothing here throws; listing processes, the costliest reading, runs every
// other tick. The readings themselves are the Windows ones Task Manager shows.
// A guarded reading (SensorGuard) that never answers holds the thread it was asked on for good; the sampler then starts another
// thread with providers of its own, and the old one ends should the driver ever answer (ReadingAbandoned).
internal sealed class SystemSampler : IDisposable
{
    // After a pause the counters are read once and again after this short window, so the page has figures almost at once.
    private const int PrimeMilliseconds = 300;
    // The network readings the sparklines draw, oldest first: a minute at one reading a second.
    public const int HistoryLength = 60;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _linger;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    // Starting a replacement and shutting down agree on which threads still own the shared buffers.
    private readonly object _lifetime = new();
    private int _running;
    private bool _cleaned;
    // Raised when a thread is replaced: only the thread of the current generation reads.
    private int _generation;
    private volatile bool _wanted, _disposed, _listProcesses, _primeProcesses, _primeCounters;
    // The memory mode keeps the process list's buffer and the programs' icons while the readings pause (MemoryPlan.KeepCaches).
    private volatile bool _keepCaches;
    private volatile int _intervalMilliseconds = 1000;
    // The providers belong to the sampling thread, which creates them: adding the performance counters can take tens of
    // milliseconds, which the panel must not wait for. Only HoldProcesses reaches them from the UI thread. Each kind of counter has a
    // query of its own: counters just asked for (the adapters', the power meter's) are primed without starting the processor's
    // deltas over, and a stuck collection (the thermal zones', the power meter's) is known to be theirs.
    private PdhQuery? _cpuPdh, _gpuPdh, _zonesPdh, _meterPdh;
    private CpuProvider? _cpu;
    private GpuProvider? _gpu;
    private GpuDrivers? _drivers;
    private SensorClient? _sensors;
    private readonly MonitorBridgeReader _bridges = new();
    // Until when (Now) the processor's temperature counts as coming from its own sensors or a hardware monitor (CpuTemperatureSource),
    // and whether the last reading did: while one does, the zones are not read. The time bridges a reading or two that fails.
    private const double HeatHold = 30;
    private double _processorHeatUntil = double.NegativeInfinity, _driverHeatUntil = double.NegativeInfinity;
    private bool _processorHeat;
    private ThermalZones? _zones;
    private GpuTemperatures? _gpuTemperatures;
    private readonly StorageReader _storage = new();
    // Whether the processor's and the graphics adapter's temperatures are read, the drives, and the drives' temperatures (SetExtras).
    // _powerWanted: the whole machine's power (the Energy Meter counters, NVIDIA's board power); _batteryWanted: the battery alone, which
    // the machine's charge reading needs without any of the rest.
    // _gpuWanted: the graphics adapters' counters, for the page, the row's GPU reading and the power.
    // Whether the processor, memory and the network are read (SetReads): a page that does not show one does not ask for it.
    private volatile bool _readCpu = true, _readMemory = true, _readNetwork = true;
    private volatile bool _temperatures, _storageWanted, _driveTemperatures, _powerWanted, _batteryWanted, _gpuWanted;
    // Where the adapters' usage comes from (DeckSettings.MonitorGpuUsage), set on the UI thread and read on the sampling thread.
    private volatile GpuUsageSource _gpuUsage;
    public GpuUsageSource GpuUsage { set => _gpuUsage = value; }
    // The span the network's traffic is summed over (DeckSettings.MonitorTrafficPeriod), set on the UI thread, read on the sampling thread.
    private volatile TrafficPeriod _trafficPeriod;
    public TrafficPeriod TrafficPeriod { set => _trafficPeriod = value; }
    // The whole machine's power and the machine's battery (PowerProvider, BatteryProvider), made on the sampling thread.
    private PowerProvider? _power;
    private BatteryProvider? _battery;
    // The performance counters join their queries after the first snapshot (CpuProvider.Attach): the first one added takes half a second of
    // CPU, so the first readings are made without them, the processor's from the kernel's times.
    private readonly Queue<double> _powerHistory = new(HistoryLength);
    private readonly PowerStats _powerStats = new(HistoryLength);
    // Where the last whole-machine figure came from: the peak and mean (of the curve's readings) start over when it changes (plugged in
    // or out), so a measurement and an estimate are never counted together. The curve keeps its readings.
    private PowerSource? _powerSource;
    // The processor's and the graphics' own power, read with the whole machine's, for the page's power line when it shows one of them.
    private readonly PowerTrail _cpuPower = new(HistoryLength), _gpuPower = new(HistoryLength);
    // The frame rate of the program in front, while a collapsed row or an open page shows it (SetExtras; FrameRates).
    private readonly FrameRates _frames = new();
    private readonly NetworkProvider _network = new();
    private readonly TrafficRecord _traffic = new();
    private readonly ProcessProvider _processes = new();
    private Reading<ProcessList> _lastProcesses = Reading<ProcessList>.Unavailable(Why.Waiting);
    // Listing processes, the costliest reading, runs about every two seconds whatever the interval, and only while the page is
    // open: the notch and the capsule show no processes. A two-second window also steadies the per-process figures.
    private const double ProcessSeconds = 1.5;
    private double _processesAt = double.NegativeInfinity;
    // Rate counters read over a few milliseconds are far off: "% Processor Time" read 60 % to 100 %, or nothing, over 0 to 20 ms on a
    // machine near idle (300 ms read true). Collections of a query closer together than this are not made (Collect).
    private const double ShortestCounterSeconds = .2;
    private double _cpuAt = double.NegativeInfinity, _gpuAt = double.NegativeInfinity;
    // When the adapters' counters last joined their query (GpuProvider.Want).
    private double _gpuJoinedAt = double.NegativeInfinity;
    private Reading<CpuSample> _lastCpu = Reading<CpuSample>.Unavailable(Why.Waiting);
    private Reading<IReadOnlyList<GpuSample>> _lastGpus = Reading<IReadOnlyList<GpuSample>>.Unavailable(Why.Waiting);
    private readonly Queue<(double Down, double Up)> _history = new(HistoryLength);
    private readonly Queue<double> _cpuHistory = new(HistoryLength);
    // Each graphics adapter's usage, by LUID, so the page shows the history of whichever adapter it shows.
    private readonly Dictionary<ulong, Queue<double>> _gpuHistories = [];
    // The saved choice of graphics adapter (DeckSettings.MonitorGpu and MonitorGpuName), and the adapter 自动 showed last, so that
    // it stays (GpuChoice.Auto). The LUID is written on the sampling thread and read on the UI thread by ShowGpuChoice.
    private readonly Func<(string? Key, string? Name)> _gpuChoice;
    private ulong _autoGpu;

    public SystemSampler(Dispatcher dispatcher, Func<(string? Key, string? Name)> gpuChoice)
    {
        _dispatcher = dispatcher; _gpuChoice = gpuChoice;
        _linger = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = MemoryPlan.For(MemoryMode.Balanced).ReadingsLinger };
        // Once paused the readings grow old, so they are dropped: a page opened later shows dashes until new ones arrive
        // instead of old figures as if they were current.
        _linger.Tick += (_, _) => { _linger.Stop(); _wanted = false; Latest = null; _wake.Set(); };
    }

    // The newest readings while the sampler runs; null once it has paused. Raised on the UI thread.
    public MonitorSnapshot? Latest { get; private set; }
    // Paused: nothing asks for readings and the linger has run out, so the thread, if started, waits.
    public bool Resting => !_wanted;
    public event Action<MonitorSnapshot>? Updated;
    // The saved choice of graphics adapter was found under an older key (the first) and is to be saved under its key now (the
    // second). Raised on the UI thread.
    public event Action<string, string>? GpuKeyUpgraded;
    // While set, rows already in the process lists keep their rank (the pointer is over a list).
    public bool HoldProcesses { get => _processes.Hold; set => _processes.Hold = value; }

    // The memory mode's rules (MemoryPlan): reopening the page within ReadingsLinger finds the sampler still running and the readings
    // current. Called on the UI thread.
    public void UsePlan(MemoryPlan plan)
    {
        if (_disposed) return;
        _linger.Interval = plan.ReadingsLinger;
        bool releaseCaches = _keepCaches && !plan.KeepCaches;
        _keepCaches = plan.KeepCaches;
        // A resting thread has already applied the previous plan and is asleep; let it apply this one too.
        if (releaseCaches && !_wanted) _wake.Set();
    }

    // Starts the sampling thread without reading, so the counters (and the adapters' descriptions, where the adapters are wanted) are set up before the page first
    // opens (the smooth memory mode). Called on the UI thread.
    public void Warm() { if (!_disposed && _thread is null) StartThread(0); }

    // Called on the UI thread whenever a panel opens or closes the page or shows or hides the readings in its collapsed row.
    // processes: the expanded page is open, so the process list is read too. immediate: a sampler no longer wanted stops at once, as it
    // has already lingered (App.LingerPageExtras).
    public void SetDemand(bool wanted, int intervalSeconds, bool processes, bool immediate = false)
    {
        if (_disposed) return;
        _intervalMilliseconds = Math.Clamp(intervalSeconds, 1, 2) * 1000;
        bool listing = _listProcesses;
        _listProcesses = wanted && processes;
        // The page opened while the collapsed row kept the sampler running: the processes are read at once and again shortly,
        // instead of waiting up to two readings for a list.
        if (_listProcesses && !listing && _wanted) { _primeProcesses = true; _wake.Set(); }
        if (!wanted && immediate) { _linger.Stop(); _wanted = false; Latest = null; _wake.Set(); return; }
        if (!wanted) { if (_wanted && !_linger.IsEnabled) _linger.Start(); return; }
        _linger.Stop();
        if (_wanted) return;
        _wanted = true;
        if (_thread is null) StartThread(0);
        else _wake.Set();
    }

    private void StartThread(int generation)
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            var thread = new Thread(() => Run(generation)) { IsBackground = true, Name = "BrimDeck system monitor", Priority = ThreadPriority.BelowNormal };
            // The shell's icon lookup (ProcessIcons) needs a single-threaded apartment.
            thread.SetApartmentState(ApartmentState.STA);
            var previous = _thread;
            _thread = thread; _running++;
            try { thread.Start(); }
            catch { _thread = previous; _running--; throw; }
        }
    }

    // A guarded reading on the thread of this generation did not answer (SensorGuard): another thread takes over, and the reading
    // is not asked again. Called on the guard's thread.
    private void Replace(int generation)
    {
        if (_disposed || Interlocked.CompareExchange(ref _generation, generation + 1, generation) != generation) return;
        StartThread(generation + 1);
    }

    // Called on the UI thread with what the panels show: temperatures, the processor's and the graphics adapter's (the expanded page
    // with temperatures on, or the collapsed row's temperature); storage, the drives (the page with storage on, or the collapsed
    // row's drive); driveTemperatures, the drives' temperatures (the page with both on). The drives are first read at once, so the
    // page knows early how many lines it needs for them. frames: a collapsed row or an open page shows the frame rate. gpu: the graphics
    // adapters (the page, the row's GPU reading, the power).
    // Turning the network off is noted here, not only seen as the flag: off and on again between two collections still starts over.
    // The processor likewise: its counters and times kept their baseline while it was off, so the first reading after would cover the whole time off.
    // A processor that was off for no longer than CpuGapSeconds goes on with the baseline it had: the page was just closed and opened again
    // (the sampler runs on for that long), the number must come at once, and over so short a time off the first one is near enough.
    private const double CpuGapSeconds = 3;
    private volatile bool _networkRestart, _cpuRestart;
    private double _cpuOffAt;
    private void CpuOff(double now) { if (!_cpuRestart) Volatile.Write(ref _cpuOffAt, now); _cpuRestart = true; }
    public void SetReads(bool cpu, bool memory, bool network)
    {
        if (_readNetwork && !network) _networkRestart = true;
        if (_readCpu && !cpu) CpuOff(Now());
        _readCpu = cpu; _readMemory = memory; _readNetwork = network;
    }
    public void SetExtras(bool temperatures, bool storage, bool driveTemperatures, bool power = false, bool battery = false, bool frames = false, bool gpu = false)
    {
        if (!_disposed) _frames.Want(frames);
        bool newly = power && !_powerWanted || gpu && !_gpuWanted;
        _temperatures = temperatures; _storageWanted = storage; _driveTemperatures = storage && driveTemperatures; _powerWanted = power; _batteryWanted = power || battery;
        _gpuWanted = gpu || power;
        // Power or the adapters asked for while the sampler already runs (the collapsed row's readings kept it going): the counters are read
        // at once and again after the short priming window, so the first figure comes with the next snapshot and not a second reading later.
        if (newly && _wanted && _thread is not null) { _primeCounters = true; _wake.Set(); }
        if (storage && (_storage.Drives == SensorState.Unknown || _driveTemperatures && _storage.Temperatures == SensorState.Unknown)) _storage.Start(_driveTemperatures);
    }
    // Whether the processor's zones, the graphics adapters and the drives have given temperatures, and whether drives were found and
    // how many (-1 until read); the page keeps room for what has turned up or has not been asked yet.
    public SensorState CpuSensor => Now() < Volatile.Read(ref _processorHeatUntil) ? SensorState.Found : _zones?.State ?? SensorState.Unknown;
    // Found while an adapter's driver gives a temperature where the graphics kernel gives none (GpuSample.DriverTemperature).
    public SensorState GpuSensor => Now() < Volatile.Read(ref _driverHeatUntil) ? SensorState.Found : _gpuTemperatures?.State ?? SensorState.Unknown;
    public SensorState DriveSensor => _storage.Temperatures;
    public SensorState StorageState => _storage.Drives;
    public int StorageCount => _storage.Count;
    // Whether the machine gives a power reading and has a battery of its own; Unknown until read.
    public SensorState PowerSensor => _power?.State ?? SensorState.Unknown;
    public SensorState BatterySensor => _battery?.State ?? SensorState.Unknown;
    // Whether frame rates can be read: Found while they are, None without permission, Unknown before they are asked for.
    public SensorState FramesSensor => _frames.State;

    // generation: the thread's own; a thread replaced (Replace) leaves its providers to the thread stuck in them.
    private void Run(int generation)
    {
        // Without performance counters (the library is missing or broken) the processor falls back to the kernel's times
        // and the GPU reads as unavailable; nothing here may end the process.
        static PdhQuery? Query()
        {
            try { return new PdhQuery(); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { MonitorLog.Note("performance counters", ex); return null; }
        }
        // Keep this generation's owners locally: a replacement overwrites the published fields while this thread
        // is still inside its driver. Its finally must release these, never the new thread's providers.
        PdhQuery? cpuPdh = null, gpuPdh = null, zonesPdh = null, meterPdh = null;
        PowerProvider? power = null;
        GpuTemperatures? gpuTemperatures = null;
        try
        {
        Action stuck = () => Replace(generation);
        _cpuPdh = cpuPdh = Query(); _gpuPdh = gpuPdh = Query(); _zonesPdh = zonesPdh = Query(); _meterPdh = meterPdh = Query();
        _cpu = new CpuProvider(cpuPdh);
        _gpu = new GpuProvider(gpuPdh);
        _drivers = new GpuDrivers(stuck);
        _sensors = new SensorClient();
        _zones = new ThermalZones(zonesPdh, stuck);
        _power = power = new PowerProvider(meterPdh, stuck);
        _gpuTemperatures = gpuTemperatures = new GpuTemperatures(stuck);
        bool primed = false, first = false, holdGpu = false;
        // A thread that took over from one stuck in a driver (Replace) finds the page showing current readings and curves: they stay.
        bool replacing = generation > 0;
        while (!_disposed && Volatile.Read(ref _generation) == generation)
        {
            try
            {
                if (!_wanted)
                {
                    // Started without a demand (Warm): the counters are set up now, where nothing waits for them.
                    if (!_cpu!.Attached) { AttachCounters(); if (_gpuWanted) _gpu!.Describe(); }
                    primed = false; replacing = false; Rest(); _wake.WaitOne(); continue;
                }
                bool primeCounters = _primeCounters;
                _primeCounters = false;
                if (!primed || _primeProcesses || primeCounters)
                {
                    // The counters are read once to start the deltas; the processes again on the first reading, so their
                    // figures cover the short window and not the whole pause. Power or adapters asked for while the sampler runs are primed the same way,
                    // and only they: the processor's figure keeps counting from the last reading, so the first snapshot does not show
                    // just the panel opening (which alone read 10 to 20 % of the machine over the window).
                    bool primeProcesses = _primeProcesses;
                    _primeProcesses = false;
                    // Readings starting (the monitor opened or came back): a hardware monitor started meanwhile is looked for again.
                    // What the first reading already holds (the memory, the drives, the battery) is shown at once; the rest follows. The
                    // readings that are rates since the last collection cover the whole pause here, so they are not shown.
                    // The figures the reading added to the histories and the power statistics cover the pause as well: they are dropped.
                    // A thread taking over (replacing) only starts its windows here: the page keeps the figures it shows until the next snapshot.
                    if (!primed)
                    {
                        _bridges.Rescan();
                        var early = Collect(listProcesses: true);
                        if (!replacing) { ForgetWindowed(); Publish(early.WithoutWindowed(Why.Waiting), generation); }
                        replacing = false; holdGpu = false;
                    }
                    else
                    {
                        // Adapters already read before this priming keep their last reading in the snapshot after it (Collect): a
                        // collection there would cover little more than the panel opening, which keeps the busiest engine (the
                        // desktop composing the animation) at 40 to 60 %, and the GPU figure read high for a second.
                        holdGpu = !(primeCounters && PrimeCounters());
                        if (_listProcesses && (primeProcesses || !_lastProcesses.HasValue)) _processes.Sample(Now());
                    }
                    primed = true; first = true;
                    Pause(PrimeMilliseconds);
                    continue;
                }
                var snapshot = Collect(listProcesses: first, holdGpu);
                first = false; holdGpu = false;
                if (_disposed) break;
                Publish(snapshot, generation);
                // The programs' names and icons, looked up after the first list was shown with the executables' names.
                if (_processes.Resolve() is { } named)
                {
                    _lastProcesses = named;
                    Publish(snapshot with { Processes = named }, generation);
                }
                // The counters join now, after the first figures: their first collection starts the processor's and the adapters' windows,
                // which the next pass primes as for counters just asked for. Joining takes half a second of this process's CPU, so the
                // processes are primed again too: the next list would otherwise count that time against BrimDeck.
                if (!_cpu!.Attached) { AttachCounters(); _primeCounters = true; if (_listProcesses) _primeProcesses = true; continue; }
                Pause(_intervalMilliseconds);
            }
            // The driver answered after this thread was replaced: the new thread reads now.
            catch (ReadingAbandoned) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A reading that fails in an unexpected way is skipped; the page keeps its last figures.
                MonitorLog.Note("sampler", ex);
                _wake.WaitOne(_intervalMilliseconds);
            }
        }
        }
        finally
        {
            try
            {
                Release(power); Release(gpuTemperatures);
                Release(cpuPdh); Release(gpuPdh); Release(zonesPdh); Release(meterPdh);
            }
            finally
            {
                ThreadFinished();
            }
        }
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // A reading taken just as the sampler paused would bring back figures that then grow old. The adapter shown is resolved again here:
    // a reading taken before the user chose another adapter would otherwise bring the old one back. generation: only the sampling
    // thread's own generation publishes — a snapshot read before the thread was replaced never arrives after the new one's figures.
    private void Publish(MonitorSnapshot snapshot, int generation) => _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (_disposed || !_wanted || Volatile.Read(ref _generation) != generation) return;
        Latest = WithGpu(snapshot, Volatile.Read(ref _autoGpu), out _);
        Updated?.Invoke(Latest);
    });

    // Adds the processor's counters, which makes the process load the performance library (the first counter of a process costs
    // 450 to 880 ms of CPU), and has the query collect once. Called on the sampling thread.
    private void AttachCounters()
    {
        _cpu!.Attach();
        _cpuAt = Now();
    }

    // Waits the whole time unless there is something new to do: the sampler pausing or stopping, or a new priming. Opening the page
    // wakes the thread twice (SetExtras, then SetDemand); a pass that already took both requests left the second wake behind, which
    // ended the priming window at once, so the first snapshot's counters covered a few milliseconds and read 60 % and more or none.
    private void Pause(int milliseconds)
    {
        double end = Now() + milliseconds / 1000.0;
        for (double left; (left = end - Now()) > 0;)
            if (_wake.WaitOne(TimeSpan.FromSeconds(left)) && (_disposed || !_wanted || _primeProcesses || _primeCounters)) return;
    }

    // Paused: the histories start over, and what only the readings need is let go until they start again: the process table's buffer
    // (a megabyte or more), the last list and the programs' icons, read again with the first list. The smooth memory mode keeps the
    // buffer and the icons (MemoryPlan.KeepCaches).
    private void Rest()
    {
        ForgetWindowed();
        // Sampling paused as a whole: the network's baseline goes, so the first rate after it is not an average over the pause.
        _network.Forget();
        _power!.Sample(false, false, 0, [], default, false); _storage.Rest(); _bridges.Rest();
        _lastProcesses = Reading<ProcessList>.Unavailable(Why.Waiting);
        _processes.Forget();
        if (!_keepCaches) { _processes.Dispose(); ProcessIcons.Clear(); }
        _cpu!.Forget();
    }

    // The histories and the power statistics start over: what they hold came from readings over a window that is no more.
    private void ForgetWindowed()
    {
        _history.Clear(); _cpuHistory.Clear(); _gpuHistories.Clear(); _powerHistory.Clear(); _powerStats.Reset(); _powerSource = null; _cpuPower.Clear(); _gpuPower.Clear();
    }

    // The counters just asked for, read once: the power meter's, and the adapters' when they have just joined their query. Whether
    // the adapters' did.
    private bool PrimeCounters()
    {
        if (!_cpu!.Attached) return false;
        _power!.Prepare(_powerWanted);
        // The zones' first collection too, so the next pass holds two and has the temperature.
        _zones!.Sample(_temperatures && !_processorHeat, Now());
        if (!_gpu!.Want(_gpuWanted)) return false;
        _gpuPdh?.Collect(); _gpuJoinedAt = _gpuAt = Now();
        return true;
    }

    // holdGpu: the adapters keep their last reading, as if collected too soon, when it came from a collection after their counters
    // joined the query (one over a whole interval, not the priming's); their next collection then covers the time since that one.
    private MonitorSnapshot Collect(bool listProcesses, bool holdGpu = false)
    {
        double now = Now();
        // Before the counters have joined (the first readings) only what needs none is read: the kernel's times stand in for the processor.
        bool counters = _cpu!.Attached;
        bool meter = _power!.Prepare(_powerWanted && counters);
        bool gpuJoined = _gpu!.Want(_gpuWanted && counters);
        if (gpuJoined) _gpuJoinedAt = now;
        // A collection this soon after the last is skipped, and the processor and the adapters keep the readings they gave then.
        bool cpuSoon = now - _cpuAt < ShortestCounterSeconds;
        bool gpuSoon = now - _gpuAt < ShortestCounterSeconds || holdGpu && !gpuJoined && _gpuWanted && _gpuAt > _gpuJoinedAt && _lastGpus.HasValue;
        // One look at whether the processor is wanted serves the collection and the restart below.
        bool readCpu = _readCpu;
        bool cpuCollected = readCpu && !cpuSoon && counters && _cpuPdh?.Collect() == true;
        bool gpuCollected = !gpuSoon && _gpuWanted && counters && _gpuPdh?.Collect() == true;
        if (!cpuSoon) _cpuAt = now;
        if (!gpuSoon) _gpuAt = now;
        if (!_listProcesses) { _lastProcesses = Reading<ProcessList>.Unavailable(Why.Waiting); _processesAt = double.NegativeInfinity; _processes.Forget(); }
        else if (listProcesses || !_lastProcesses.HasValue || now - _processesAt >= ProcessSeconds)
        {
            // Named after the first list is shown (Resolve): the executables' descriptions and icons take 200 to 480 ms on the first list.
            _lastProcesses = _processes.Sample(now, name: !listProcesses); _processesAt = now;
        }
        // While network is not read the provider forgets its baseline and the history goes, so the first rate after it turns on is not an
        // average over the time off and old readings do not join new ones.
        if (!_readNetwork || _networkRestart) { _networkRestart = false; _network.Forget(); _history.Clear(); }
        var network = _readNetwork ? _network.Sample(now) : Reading<NetworkSample>.Unavailable(Why.Waiting);
        // The traffic over the period chosen, from Windows' record, joins the rates once it has been read for that period.
        if (_readNetwork)
        {
            var period = _trafficPeriod;
            _traffic.Refresh(period, now);
            if (network.TryGet(out var withRates) && _traffic.For(period) is { } moved)
                network = Reading<NetworkSample>.Of(withRates with { DownTotalBytes = moved.Received, UpTotalBytes = moved.Sent, TotalPeriod = period });
        }
        if (network.TryGet(out var rates))
        {
            if (_history.Count == HistoryLength) _history.Dequeue();
            _history.Enqueue((rates.DownBytesPerSecond, rates.UpBytesPerSecond));
        }
        bool temperatures = _temperatures;
        // The processor was off and is read again: this reading only sets the baselines (CpuProvider.Prime) and is shown as waiting.
        if (!readCpu) CpuOff(now);
        bool cpuStart = readCpu && _cpuRestart && !cpuSoon;
        bool cpuPrime = cpuStart && now - Volatile.Read(ref _cpuOffAt) > CpuGapSeconds;
        if (cpuStart) _cpuRestart = false;
        var cpu = !readCpu ? Reading<CpuSample>.Unavailable(Why.Waiting) : cpuPrime ? PrimeCpu(cpuCollected) : cpuSoon ? _lastCpu : _cpu!.Sample(cpuCollected); var gpus = gpuSoon ? _lastGpus : _gpu.Sample(gpuCollected);
        _lastCpu = cpu; _lastGpus = gpus;
        // Asked on every tick, so the zones' counters leave the query once temperatures are no longer wanted or a better source gives them.
        if (_zones!.Sample(temperatures && !_processorHeat && counters, now) is { } zone) cpu = cpu.Map(sample => sample with { Temperature = zone.Celsius, TemperatureZone = zone.Zone });
        // The processor's own sensors through the sensor service where enhanced sensors are set up, else a hardware monitor the user
        // runs; either stands in front of the zones. A hardware monitor is asked only for what the service did not give.
        var report = _sensors!.Read(temperatures || _powerWanted, now) is { Status: SensorStatus.Ok } ok ? ok : null;
        var bridge = temperatures && report?.Package is null || _powerWanted && report?.PackageWatts is null ? _bridges.Read(now) : default;
        if (temperatures && report?.Package is { } package)
            cpu = cpu.Map(sample => sample with
            {
                Temperature = package, TemperatureZone = null, TemperatureSource = CpuTemperatureSource.Processor,
                CoreTemperatures = report.Cores, CoreTemperaturesAreCcds = report.CoresAreCcds
            });
        else if (temperatures && bridge.Celsius is { } bridged)
            cpu = cpu.Map(sample => sample with { Temperature = bridged, TemperatureZone = null, TemperatureSource = bridge.Source });
        _processorHeat = temperatures && cpu.OrDefault?.TemperatureSource is not null and not CpuTemperatureSource.Zone;
        if (_processorHeat) Volatile.Write(ref _processorHeatUntil, now + HeatHold);
        IReadOnlyList<StorageDrive> storage = _storageWanted ? _storage.Sample(now, _driveTemperatures) : [];
        Remember(_cpuHistory, cpu.OrDefault?.Usage);
        // The usage each adapter's driver gives, where it gives one (GpuDrivers); the curves follow the figure shown.
        var adapters = gpus.TryGet(out var read) ? Remember(_drivers!.Read(read, now, temperatures, _gpuUsage)) : [];
        if (!_gpuWanted) _gpuHistories.Clear();
        // The machine's power and battery: read only while something shows them; the history and the peak start over once none does.
        var power = Reading<PowerSample>.Unavailable(Why.Waiting);
        var battery = Reading<BatterySample>.Unavailable(Why.Waiting);
        bool powerWanted = _powerWanted, batteryWanted = _batteryWanted;
        if (batteryWanted)
        {
            // Apart from the rest: a battery or a driver that fails in an unexpected way costs these two readings, never the page's.
            try
            {
                // Made when a reading first asks for it: loading the battery classes costs 17 to 22 ms (more on a first run).
                _battery ??= new BatteryProvider();
                var (batteryInputs, batterySample) = _battery.Sample();
                battery = batterySample;
                // Not before the counters have joined: without the meter the reading would fall back to the battery's discharge and switch
                // source (and start its statistics over) a moment later.
                if (powerWanted && counters)
                {
                    power = _power!.Sample(meter, true, now, adapters, BatteryModel.Discharge(batteryInputs, batterySample), _battery.Present,
                        report?.PackageWatts, report?.MemoryWatts, bridge.Watts);
                    if (power.TryGet(out var drawn))
                    {
                        if (_powerHistory.Count == HistoryLength) _powerHistory.Dequeue();
                        if (_powerSource is { } last && last != drawn.Source) _powerStats.Reset();
                        _powerSource = drawn.Source;
                        _powerHistory.Enqueue(drawn.Watts); _powerStats.Add(drawn.Watts, now);
                        if (PowerResolver.PartWatts(drawn, "cpu") is { } cpuWatts) _cpuPower.Add(cpuWatts, now);
                        if (PowerResolver.PartWatts(drawn, "gpu") is { } gpuWatts) _gpuPower.Add(gpuWatts, now);
                    }
                }
            }
            catch (ReadingAbandoned) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MonitorLog.Note("battery and power", ex);
                power = Reading<PowerSample>.Unavailable(ex.Message); battery = Reading<BatterySample>.Unavailable(ex.Message);
            }
        }
        // Power no longer wanted (the page left, the row dropped the reading, the display went off): the counters leave the query, the
        // NVIDIA helper ends and the history starts over, at this very reading and not after the sampler's pause.
        if (!powerWanted)
        {
            if (_powerHistory.Count > 0) { _powerHistory.Clear(); _powerStats.Reset(); }
            _powerSource = null;
            _cpuPower.Clear(); _gpuPower.Clear();
            _power!.Sample(false, false, now, [], default, false);
        }
        // Unavailable while nothing shows the frame rate, so one shown again starts from a dash, not an old rate.
        var frames = _frames.Sample(now);
        // The program's icon, read here as the process list's are (the shell's lookup wants this thread's apartment).
        if (frames.TryGet(out var drawing) && drawing.Path is { } program) ProcessIcons.Prepare(program);
        var snapshot = new MonitorSnapshot(DateTimeOffset.Now, Environment.ProcessorCount, cpu, _readMemory ? MemoryProvider.Sample() : Reading<MemorySample>.Unavailable(Why.Waiting), network,
            Reading<GpuSample>.Unavailable(gpus.Reason), _lastProcesses)
        {
            DownHistory = _history.Select(reading => reading.Down).ToArray(), UpHistory = _history.Select(reading => reading.Up).ToArray(),
            CpuHistory = _cpuHistory.ToArray(), Gpus = adapters, Storage = storage,
            Power = power, Battery = battery, PowerHistory = _powerHistory.ToArray(),
            PowerSummary = _powerStats.Summary(now),
            CpuPower = _cpuPower.Track(now), GpuPower = _gpuPower.Track(now),
            Frames = frames
        };
        var shown = WithGpu(snapshot, _autoGpu, out bool automatic);
        if (automatic && shown.Gpu.TryGet(out var gpu)) Volatile.Write(ref _autoGpu, gpu.Luid);
        return temperatures ? WithTemperature(shown, now) : shown;
    }
    private Reading<CpuSample> PrimeCpu(bool collected) { _cpu!.Prime(collected); return Reading<CpuSample>.Unavailable(Why.Waiting); }
    // The shown adapter's temperature (GpuTemperatures.ReadFor): the chosen adapter's own, or, where it has no sensor, that of an
    // adapter in use that has one, named and marked with its kind. Only adapters in use are asked for another's sake.
    private MonitorSnapshot WithTemperature(MonitorSnapshot snapshot, double now)
    {
        if (!snapshot.Gpu.TryGet(out var gpu)) return snapshot;
        var working = snapshot.Gpus.Where(adapter => adapter.EngineUsage > 0).Select(adapter => GpuReadings.LuidText(adapter.Luid)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in snapshot.Gpus) names.TryAdd(GpuReadings.LuidText(adapter.Luid), adapter.Name ?? "GPU");
        if (_gpuTemperatures!.ReadFor(GpuReadings.LuidText(gpu.Luid), working, names, now) is not { } heat)
        {
            // The graphics kernel gives none: the temperature the adapter's own driver gave with its usage (GpuDrivers).
            if (gpu.DriverTemperature is not { } own) return snapshot;
            Volatile.Write(ref _driverHeatUntil, now + HeatHold);
            return snapshot with { Gpu = gpu with { Temperature = own } };
        }
        var from = heat.Adapter == GpuReadings.LuidText(gpu.Luid) ? null : snapshot.Gpus.FirstOrDefault(adapter => GpuReadings.LuidText(adapter.Luid) == heat.Adapter);
        return snapshot with
        {
            Gpu = gpu with
            {
                Temperature = heat.Celsius,
                TemperatureFrom = from is null ? null : from.Name ?? "GPU",
                TemperatureKind = from is null ? null : GpuChoice.KindName(from.Kind) is { Length: > 0 } kind ? kind : Temperatures.AdapterKind(from.DedicatedTotal ?? 0)
            }
        };
    }
    // Each adapter with its own history; adapters that went away take theirs with them.
    private List<GpuSample> Remember(IReadOnlyList<GpuSample> gpus)
    {
        var adapters = new List<GpuSample>(gpus.Count);
        foreach (var gpu in gpus)
        {
            if (!_gpuHistories.TryGetValue(gpu.Luid, out var history)) _gpuHistories[gpu.Luid] = history = new Queue<double>(HistoryLength);
            Remember(history, gpu.Usage);
            adapters.Add(gpu with { History = history.ToArray() });
        }
        if (_gpuHistories.Count > adapters.Count)
            foreach (var luid in _gpuHistories.Keys.ToList()) if (!adapters.Exists(gpu => gpu.Luid == luid)) _gpuHistories.Remove(luid);
        return adapters;
    }
    // The snapshot showing the chosen graphics adapter, or why no adapter can be shown.
    // automatic: 自动 chose it, as asked or because the chosen adapter is not there.
    private MonitorSnapshot WithGpu(MonitorSnapshot snapshot, ulong previousAuto, out bool automatic)
    {
        var (key, name) = _gpuChoice();
        if (GpuChoice.Resolve(snapshot.Gpus, key, name, previousAuto, out automatic, out var upgrade) is not { } gpu) return snapshot;
        // A choice found by an older mark (a key saved before adapters had a location) is saved again under the adapter's key.
        if (upgrade is not null) _dispatcher.BeginInvoke(DispatcherPriority.Background, () => GpuKeyUpgraded?.Invoke(key!, upgrade));
        // Only the adapter shown carries a temperature (WithTemperature), not its entry in Gpus: resolved again to the same
        // adapter, the shown one is kept, or the temperature read for it would be lost on the way to the page.
        if (snapshot.Gpu.TryGet(out var shown) && shown.Luid == gpu.Luid) gpu = shown;
        return snapshot with { Gpu = gpu, GpuHistory = gpu.History };
    }
    // Shows the newest readings again for a new choice of graphics adapter, at once instead of with the next reading.
    public void ShowGpuChoice()
    {
        if (_disposed || Latest is not { } latest) return;
        Latest = WithGpu(latest, Volatile.Read(ref _autoGpu), out _);
        Updated?.Invoke(Latest);
    }
    private static void Remember(Queue<double> history, double? usage)
    {
        if (usage is not { } value) return;
        if (history.Count == HistoryLength) history.Dequeue();
        history.Enqueue(Math.Clamp(value, 0, 1));
    }

    private void ThreadFinished()
    {
        lock (_lifetime) { _running--; if (_disposed && _running == 0) Cleanup(); }
    }

    // Called under _lifetime only after every generation has left its readings.
    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        Release(_frames); Release(_processes); Release(_network); Release(_wake);
    }

    private static void Release(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine(ex); }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true; _wanted = false; _linger.Stop();
            _wake.Set();
            thread = _thread;
            if (_running == 0) Cleanup();
        }
        // A blocked driver keeps its own resources. When it answers, Run's finally does the cleanup even after this wait times out.
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(1000);
    }
}

internal static class MemoryProvider
{
    public static Reading<MemorySample> Sample()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) return Reading<MemorySample>.Unavailable(Why.NoMemory);
        // The file cache (standby and modified pages) is free for the taking, so Task Manager counts it as available, not in use.
        var performance = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        ulong cached = GetPerformanceInfo(ref performance, performance.Size) ? (ulong)performance.SystemCache * (ulong)performance.PageSize : 0;
        // TotalPageFile is the commit limit: physical memory plus the page files.
        return new MemorySample(status.TotalPhys, status.TotalPhys - Math.Min(status.TotalPhys, status.AvailPhys),
            status.TotalPageFile - Math.Min(status.TotalPageFile, status.AvailPageFile), status.TotalPageFile, cached);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)] private static extern bool GetPerformanceInfo(ref PerformanceInformation information, uint size);
}

// Download and upload over the physical adapters. Virtual adapters (Hyper-V, VPN, loopback) and filter drivers are left out:
// their traffic is already counted on the adapter that carries it. Each adapter is followed on its own, so one that comes or
// goes changes only its own share.
internal sealed class NetworkProvider : IDisposable
{
    // Listing every interface costs about 10 ms with the many filter and virtual interfaces Windows keeps, so the list of
    // physical adapters is taken now and then and each tick reads only those.
    private const int Relist = 30;
    private Dictionary<ulong, (ulong Received, ulong Sent)> _previous = [];
    private List<ulong> _adapters = [];
    private int _sinceList = Relist;
    private double? _time;
    private readonly IntPtr _row = Marshal.AllocHGlobal(RowSize);

    // Starts over: the next sample only sets the baseline, and the adapters are listed again.
    public void Forget() { _previous = []; _time = null; _sinceList = Relist; }

    public Reading<NetworkSample> Sample(double now)
    {
        var current = _sinceList++ >= Relist ? List() : Read();
        if (current is null) return Reading<NetworkSample>.Unavailable(Why.NoNetwork);
        // No physical adapter is not the same as no traffic.
        if (current.Count == 0) { _previous = []; _time = null; return Reading<NetworkSample>.Unavailable(Why.NoAdapter); }
        var previous = _previous; var then = _time;
        _previous = current; _time = now;
        if (then is not { } start || now - start <= 0) return Reading<NetworkSample>.Unavailable(Why.Waiting);
        double seconds = now - start, down = 0, up = 0;
        foreach (var (luid, (received, sent)) in current)
        {
            if (!previous.TryGetValue(luid, out var before)) continue;
            down += MonitorMetrics.Rate(before.Received, received, seconds) ?? 0;
            up += MonitorMetrics.Rate(before.Sent, sent, seconds) ?? 0;
        }
        return new NetworkSample(down, up);
    }

    // Every interface, keeping the physical adapters and their counters.
    private Dictionary<ulong, (ulong, ulong)>? List()
    {
        if (GetIfTable2(out var table) != 0) return null;
        var current = new Dictionary<ulong, (ulong, ulong)>();
        try
        {
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                var row = table + 8 + i * RowSize;
                if (!Physical(row)) continue;
                current[(ulong)Marshal.ReadInt64(row, 0)] = Counters(row);
            }
        }
        finally { FreeMibTable(table); }
        _adapters = [.. current.Keys]; _sinceList = 0;
        return current;
    }

    // The counters of the adapters found by the last listing. An adapter that went away or changed lists them again.
    private Dictionary<ulong, (ulong, ulong)>? Read()
    {
        var current = new Dictionary<ulong, (ulong, ulong)>(_adapters.Count);
        foreach (var luid in _adapters)
        {
            // GetIfEntry2 finds the row by its LUID when the index is zero.
            Marshal.WriteInt64(_row, 0, (long)luid); Marshal.WriteInt32(_row, 8, 0);
            if (GetIfEntry2(_row) != 0 || !Physical(_row)) return List();
            current[luid] = Counters(_row);
        }
        return current;
    }

    // HardwareInterface is bit 0 of the flags, FilterInterface bit 1; type 24 is the software loopback.
    private static bool Physical(IntPtr row)
    {
        byte flags = Marshal.ReadByte(row, 1152);
        return (flags & 1) != 0 && (flags & 2) == 0 && Marshal.ReadInt32(row, 1128) != 24;
    }
    private static (ulong, ulong) Counters(IntPtr row) => ((ulong)Marshal.ReadInt64(row, 1208), (ulong)Marshal.ReadInt64(row, 1280));

    public void Dispose() => Marshal.FreeHGlobal(_row);

    // MIB_IF_ROW2 on 64-bit Windows: InterfaceLuid at 0, Type at 1128, the status flags at 1152, InOctets at 1208 and
    // OutOctets at 1280. The table's rows start 8 bytes in, after NumEntries and its padding.
    private const int RowSize = 1352;
    [DllImport("iphlpapi.dll")] private static extern int GetIfTable2(out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern int GetIfEntry2(IntPtr row);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr memory);
}

// Every process from one kernel call, which covers other users' processes without opening them. Processes of one
// executable fold into one entry, the way ftop folds an app's helpers into the app. Names and icons are looked up only
// for the entries that can be shown.
internal sealed class ProcessProvider : IDisposable
{
    // Each list carries a few more rows than the page shows at most, so a row leaving at the bottom has one to replace it.
    private const int Kept = MonitorLayout.MaximumRows * MonitorLayout.MaximumColumns + 2;
    private const uint InfoLengthMismatch = 0xC0000004;
    private IntPtr _buffer;
    private int _capacity;
    private Dictionary<int, (long Created, long Time)> _previous = [];
    private double? _time;
    private readonly SteadyRanking _cpuRanking = SteadyRanking.Cpu(), _memoryRanking = SteadyRanking.Memory();
    // By executable name: its path and the name the page shows, once found.
    private readonly Dictionary<string, (string? Path, string Name)> _apps = new(StringComparer.OrdinalIgnoreCase);
    private const int MaximumApps = 256;
    private readonly HashSet<int> _unreadable = [];
    public volatile bool Hold;

    // The rows of the last list that were left without their names and icons (Sample with name false), for Resolve.
    private List<ProcessSample>? _unnamedByCpu, _unnamedByMemory, _plainByCpu, _plainByMemory;

    // The list left unnamed is let go: it is old once the readings pause or the page no longer lists processes.
    public void Forget() => _unnamedByCpu = _unnamedByMemory = _plainByCpu = _plainByMemory = null;

    // name false: the rows carry the executables' names (or the descriptions already found) and no icons, and Resolve names them later.
    public Reading<ProcessList> Sample(double now, bool name = true)
    {
        Forget();
        if (!Read()) return Reading<ProcessList>.Unavailable(Why.NoProcesses);
        var current = new Dictionary<int, (long, long)>();
        var samples = new List<ProcessSample>(512);
        var then = _time;
        double seconds = then is { } start ? now - start : 0;
        for (int offset = 0; ;)
        {
            var entry = _buffer + offset;
            int pid = (int)Marshal.ReadIntPtr(entry, 80);
            if (pid != 0)
            {
                long created = Marshal.ReadInt64(entry, 32), time = Marshal.ReadInt64(entry, 40) + Marshal.ReadInt64(entry, 48);
                ulong memory = (ulong)Math.Max(0, Marshal.ReadInt64(entry, 8));
                int length = Marshal.ReadInt16(entry, 56) & 0xFFFF;
                var image = length > 0 ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, 64), length / 2) : pid == 4 ? "System" : "";
                current[pid] = (created, time);
                double cpu = _previous.TryGetValue(pid, out var before) && before.Created == created ? MonitorMetrics.CpuPercent(before.Time, time, seconds) ?? 0 : 0;
                if (image.Length > 0) samples.Add(new ProcessSample(pid, image, cpu, memory, image.ToLowerInvariant()));
            }
            int next = Marshal.ReadInt32(entry);
            if (next == 0) break;
            offset += next;
        }
        _previous = current; _time = now;
        _unreadable.IntersectWith(current.Keys);
        // One entry for each executable ever seen would grow for as long as the app runs: past the limit, the ones not running are let go.
        NameCache.Trim(_apps, samples.Select(sample => sample.AppKey ?? sample.Name), MaximumApps);
        if (then is null) return Reading<ProcessList>.Unavailable(Why.Waiting);
        double elapsed = seconds;
        var top = _cpuRanking.Arrange(ProcessSample.Folded(samples, ProcessSort.Cpu), sample => sample.CpuPercent, elapsed, Hold);
        var byMemory = _memoryRanking.Arrange(ProcessSample.Folded(samples, ProcessSort.Memory), sample => sample.MemoryBytes, elapsed, Hold);
        if (name) return new ProcessList(Named(top.Take(Kept)), Named(byMemory.Take(Kept)));
        _unnamedByCpu = top.Take(Kept).ToList(); _unnamedByMemory = byMemory.Take(Kept).ToList();
        _plainByCpu = Plain(_unnamedByCpu); _plainByMemory = Plain(_unnamedByMemory);
        return new ProcessList(_plainByCpu, _plainByMemory);
    }

    // The list left unnamed by the last Sample, named now (the descriptions and the icons are looked up here); null when there is none, or
    // when nothing changed from the list shown (every name known, every icon already read), so no second list is raised for it.
    public Reading<ProcessList>? Resolve()
    {
        if (_unnamedByCpu is not { } cpu || _unnamedByMemory is not { } memory || _plainByCpu is not { } plainCpu || _plainByMemory is not { } plainMemory) return null;
        Forget();
        int icons = ProcessIcons.Prepared;
        var byCpu = Named(cpu); var byMemory = Named(memory);
        bool same = ProcessIcons.Prepared == icons && Same(byCpu, plainCpu) && Same(byMemory, plainMemory);
        if (same) return null;
        return new ProcessList(byCpu, byMemory);
    }
    private static bool Same(List<ProcessSample> named, List<ProcessSample> plain)
        => named.Count == plain.Count && named.Zip(plain).All(pair => pair.First.Name == pair.Second.Name && pair.First.Path == pair.Second.Path);

    // Without looking anything up: what an earlier list found, else the executable's file name without ".exe".
    private List<ProcessSample> Plain(List<ProcessSample> entries) => entries.Select(entry =>
    {
        if (_apps.TryGetValue(entry.AppKey ?? entry.Name, out var known)) return entry with { Name = known.Name, Path = known.Path };
        return entry with { Name = entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? entry.Name[..^4] : entry.Name };
    }).ToList();

    private List<ProcessSample> Named(IEnumerable<ProcessSample> entries) => entries.Select(entry =>
    {
        var (path, name) = App(entry);
        // The icon is read here, off the UI thread: the shell can take long for a program on a slow or network drive.
        ProcessIcons.Prepare(path);
        return entry with { Name = name, Path = path };
    }).ToList();

    // The executable's description (as Task Manager names apps) or its file name without ".exe".
    private (string? Path, string Name) App(ProcessSample entry)
    {
        string image = entry.AppKey ?? entry.Name;
        if (_apps.TryGetValue(image, out var known) && known.Path is not null) return known;
        string plain = entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? entry.Name[..^4] : entry.Name;
        string? path = _unreadable.Contains(entry.Pid) ? null : AppIdentity.ImagePath(entry.Pid) is { Length: > 0 } read ? read : null;
        if (path is null) { _unreadable.Add(entry.Pid); return _apps[image] = known.Name is { } kept ? (null, kept) : (null, plain); }
        string name = plain;
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) && description.Length <= 40) name = description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return _apps[image] = (path, name);
    }

    private bool Read()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (_buffer == IntPtr.Zero) { _capacity = 1 << 20; _buffer = Marshal.AllocHGlobal(_capacity); }
            uint status = (uint)NtQuerySystemInformation(5, _buffer, _capacity, out int needed);
            if (status == 0) return true;
            if (status != InfoLengthMismatch) return false;
            // Processes can start between the calls; leave room for more.
            Marshal.FreeHGlobal(_buffer);
            _capacity = Math.Max(_capacity * 2, needed + (256 << 10)); _buffer = Marshal.AllocHGlobal(_capacity);
        }
        return false;
    }

    public void Dispose() { if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; } }

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returnLength);
}

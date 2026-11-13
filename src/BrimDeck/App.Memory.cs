using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Gives memory back while BrimDeck sits collapsed, as far as the memory mode asks (DeckSettings.MemoryMode, MemoryPlan).
// Starting, reading the usage logs, building a page and loading covers take memory in bursts; afterwards the garbage collector
// keeps the room it grew into, and Windows keeps every page ever touched in the working set, which is the figure Task Manager
// shows: a panel that had only started sat at 130 MB, of which it used some 20 MB while collapsed. A clean-up compacts the heap,
// hands the room it no longer needs back to Windows and takes the process's pages out of the working set; what is still used
// comes back as it is used. Compacting alone leaves the figure as it was, as little of it is on the heap.
// What starting leaves behind is given back once in every mode. After that the saver mode cleans up soon after each use and every
// couple of minutes while nothing is used, the balanced mode after a few minutes without use and seldom, and the smooth mode not
// at all, so what it has used since the start stays where it is. Never while a panel is open, a surface is being resized or the
// settings are open. The smooth mode also keeps full collections from running while a panel is open (GCLatencyMode.LowLatency),
// unless memory runs low.
// What a page switched off in the settings built and read with is let go a while after the switch (ReleaseUnused), in every mode.
public partial class App
{
    private DispatcherTimer? _trim, _release;
    // When something was last used, and when memory was last given back (null: a clean-up is owed, MemoryPlan.TrimDue).
    private DateTime _usedAt = DateTime.UtcNow;
    private DateTime? _trimmedAt;
    internal MemoryPlan MemoryPlan => MemoryPlan.For(Settings.MemoryMode);

    // Called whenever a panel collapses, when the settings window closes and when the mode changes: a use, which the clean-up waits
    // a quiet stretch after. What BrimDeck does on its own (reading the usage every minute) is not one: counted as a use, it put
    // the balanced mode's clean-up off for good.
    internal void RequestTrim() { _usedAt = DateTime.UtcNow; ScheduleTrim(); }

    private void ScheduleTrim()
    {
        _trim?.Stop();
        if (Exiting || MemoryPlan.TrimDue(_usedAt, _trimmedAt) is not { } due) return;
        if (_trim is null)
        {
            _trim = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher);
            _trim.Tick += (_, _) => Trim();
        }
        var wait = due - DateTime.UtcNow;
        _trim.Interval = wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1);
        _trim.Start();
    }

    private void Trim()
    {
        _trim?.Stop();
        if (Exiting || MemoryPlan.TrimDue(_usedAt, _trimmedAt) is null) return;
        // Someone is using a panel or the settings: try again after the next quiet stretch.
        if (Decks.Any(deck => deck.IsExpanded || deck.IsResizing) || _settingsWindow is { WindowState: not WindowState.Minimized }) { RequestTrim(); return; }
        _trimmedAt = DateTime.UtcNow;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        // Aggressive also returns the memory the heap no longer uses to the system.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        // While nothing is used, again after the mode's period.
        ScheduleTrim();
    }

    // Called whenever a panel opens or closes and when the mode changes.
    internal void UpdateCollectionLatency()
    {
        bool quiet = !Exiting && MemoryPlan.QuietWhileOpen && Decks.Any(deck => deck.IsExpanded);
        var mode = quiet ? GCLatencyMode.LowLatency : GCLatencyMode.Interactive;
        if (GCSettings.LatencyMode != mode) GCSettings.LatencyMode = mode;
    }

    // The mode changed in the settings: the samplers take its linger, and the clean-up and the collector its rules.
    internal void ApplyMemoryMode()
    {
        var plan = MemoryPlan;
        Monitor?.UsePlan(plan);
        Devices?.UsePlan(plan);
        if (Media is not null) UseMediaPlan(Media, plan);
        if (plan.WarmAtStart) { WarmMonitor(); PageWarm.Start(Dispatcher); }
        UpdateCollectionLatency();
        RequestTrim();
    }

    private static void UseMediaPlan(MediaSessions media, MemoryPlan plan) { media.MeterLinger = plan.MediaLinger; media.RecentCoverCount = plan.RecentCovers; }

    // The smooth mode sets up the system readings' counters ahead (SystemSampler.Warm), at start and when the monitor page is
    // switched on, so the first opening of the page does not wait for them.
    private void WarmMonitor()
    {
        if (Exiting || !DeckPages.Shown(Settings, DeckPage.Monitor)) return;
        Monitor ??= CreateMonitor();
        Monitor.Warm();
    }

    // Called when the settings change. A page switched off keeps what it built and read with for the mode's ReleaseAfter, counted
    // from the last change of the settings: switched on again within it (its switch being tried), the page, its readings and the
    // players are there at once. After that they are let go, and built or started again should the page be switched on later.
    internal void RequestRelease()
    {
        if (Exiting) return;
        if (_release is null)
        {
            _release = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher);
            _release.Tick += (_, _) => ReleaseUnused();
        }
        _release.Stop(); _release.Interval = MemoryPlan.ReleaseAfter; _release.Start();
    }

    // Lets go of what nothing switched on uses any longer: the pages the panels built, the system readings (their thread and
    // counters), the devices' sources, the players, and the clipboard page's pictures. Each is kept while anything still shows it
    // (the collapsed row's readings, its battery, its music), and made again when first asked for.
    private void ReleaseUnused()
    {
        _release?.Stop();
        if (Exiting) return;
        bool released = false, later = false;
        foreach (var deck in Decks) released |= deck.ReleaseHiddenPages();
        if (Monitor is { } monitor && !Settings.MonitorPage && !Settings.ShowsMonitor(Settings.Style) && _presentingMonitor.Count + _stripMonitor.Count + _heldStrip.Count == 0 && !_pageExtras)
        {
            // Still lingering after its last reading: once it has paused, its thread waits and ends at once.
            if (!monitor.Resting) later = true;
            else { Monitor = null; monitor.Dispose(); released = true; }
        }
        if (Devices is { } devices && !Settings.DevicesPage && !Settings.ShowsDevices(Settings.Style) && _devicePages.Count + _deviceStrips.Count == 0 && !_deviceSettings)
        {
            if (devices.Demand != DeviceDemand.None) later = true;
            else { Devices = null; devices.Changed -= DeviceBatteriesChanged; devices.Dispose(); released = true; }
        }
        if (_restingMedia is { } media) { _restingMedia = null; media.Dispose(); released = true; }
        if (!Settings.ClipboardPage) released |= Shelf.Rest();
        if (later) { _release!.Interval = TimeSpan.FromSeconds(15); _release.Start(); }
        // What was let go leaves the figure with the next clean-up, which is owed now in every mode.
        if (released) { _trimmedAt = null; ScheduleTrim(); }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}

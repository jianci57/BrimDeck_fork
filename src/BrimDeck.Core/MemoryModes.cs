namespace BrimDeck.Core;

// How much BrimDeck keeps in memory between uses (DeckSettings.MemoryMode). Saver gives back as much as it can soon after use, keeps
// giving it back while nothing is used, and does less work while the panel is collapsed, saving processor time as well, without
// slowing what is on screen while it is used; Balanced gives back what has not been used for a few minutes, less often; Smooth
// gives nothing back while the panel is collapsed, keeps readings, captures and caches longer and prepares the pages' code at
// start, so the panel and its pages open without waiting; it still stops the readings and captures that have not been used for a
// while. In every mode, what a page switched off in the settings built and read with is let go a while after (ReleaseAfter).
public enum MemoryMode { Saver, Balanced, Smooth }

// What a mode does. App.Memory.cs, the samplers, the media sessions and the panels read it.
public sealed record MemoryPlan
{
    // Whether memory is given back while the panel sits collapsed: the heap is compacted, the room it no longer uses handed back to
    // Windows, and the process's pages taken out of its working set. Most of what BrimDeck holds is not on the heap (the windows'
    // pictures, the code in use, what Windows keeps for it), so compacting alone leaves the figure Task Manager shows as it was;
    // taking the pages out is what lowers it. What is taken out comes back on the next use, page by page, so the first opening
    // after a clean-up can be a little slower: the saver mode cleans up soon and often, the balanced mode only after a few minutes
    // without use, and the smooth mode never.
    public bool TrimWhenIdle { get; init; }
    // The clean-up runs once nothing has been used for TrimAfter, and again every TrimEvery for as long as nothing is.
    public TimeSpan TrimAfter { get; init; }
    public TimeSpan TrimEvery { get; init; }
    // How long what a switched-off page built and read with is kept (its view, the system readings, the devices, the players), so
    // a page switched off and on again while its switch is tried comes back at once.
    public TimeSpan ReleaseAfter { get; init; }
    // How long the system readings and the devices' batteries keep being read after their page closes, so a page opened again within
    // it shows current figures at once.
    public TimeSpan ReadingsLinger { get; init; }
    // While the readings linger, what the monitor page asked for besides the usage (temperatures, drives, power, frame rate, the
    // graphics adapters' counters) stays asked for too, so it is not primed again when the page opens. The adapters' counters are
    // the costliest part of a reading.
    public bool LingerExtras { get; init; }
    // The process list's buffer and the programs' icons are kept while the readings pause instead of being read again.
    public bool KeepCaches { get; init; }
    // How long the equalizer's audio capture keeps running after nothing shows the bars (a song paused, the panel opened on another
    // page). Starting it again waits up to two seconds for the system, with flat bars meanwhile.
    public TimeSpan MediaLinger { get; init; }
    // How many recent songs' downloaded covers are kept, so switching back to one shows its cover without downloading it again.
    public int RecentCovers { get; init; }
    // The longest pause between checks of the pointer while it is far from the collapsed surface. The pause shrinks as the pointer
    // nears, so the panel opens as promptly either way; a longer one checks less often while the pointer is far away.
    public int PointerFarMilliseconds { get; init; }
    // No full collection runs while a panel is open, unless memory runs low (GCLatencyMode.LowLatency).
    public bool QuietWhileOpen { get; init; }
    // The system readings' counters, and the code that builds the pages, are prepared at start, so the first opening does not wait.
    public bool WarmAtStart { get; init; }
    // How often the collapsed row's system readings are taken while no monitor page is open; null keeps the interval chosen in the
    // settings (DeckSettings.MonitorInterval). An open monitor page always takes the chosen interval.
    public int? CollapsedSeconds { get; init; }

    // The interval the system readings are taken at, in seconds.
    public int ReadingSeconds(int chosen, bool pageOpen) => pageOpen || CollapsedSeconds is not { } collapsed ? chosen : Math.Max(chosen, collapsed);

    // A clean-up that is owed in every mode (none since the start, or something was let go since the last) runs this long after the
    // last use at most: what starting leaves behind (the code and data only the start needs, the room the first read of the usage
    // grew into) is most of what an idle panel holds.
    public static readonly TimeSpan OwedAfter = TimeSpan.FromMinutes(1);

    // When the next clean-up is due, or null for none. usedAt: the last use (a panel closed, the settings closed, the mode changed);
    // work BrimDeck does on its own (reading the usage every minute) is not a use and does not put the clean-up off. trimmedAt: the
    // last clean-up, or null while one is owed. An owed one runs OwedAfter after the last use, or sooner in a mode that cleans up
    // sooner; the others TrimAfter after the last use and no sooner than TrimEvery after the last clean-up, in the modes that clean up.
    public DateTime? TrimDue(DateTime usedAt, DateTime? trimmedAt)
    {
        if (trimmedAt is not { } last) return usedAt + (TrimWhenIdle && TrimAfter < OwedAfter ? TrimAfter : OwedAfter);
        if (!TrimWhenIdle) return null;
        DateTime quiet = usedAt + TrimAfter, again = last + TrimEvery;
        return again > quiet ? again : quiet;
    }

    public static readonly MemoryPlan Saver = new()
    {
        TrimWhenIdle = true, TrimAfter = TimeSpan.FromSeconds(45), TrimEvery = TimeSpan.FromMinutes(2), ReleaseAfter = TimeSpan.FromSeconds(30),
        ReadingsLinger = TimeSpan.FromSeconds(3), LingerExtras = false, KeepCaches = false, MediaLinger = TimeSpan.Zero, RecentCovers = 4,
        PointerFarMilliseconds = 320, QuietWhileOpen = false, WarmAtStart = false, CollapsedSeconds = 2
    };
    public static readonly MemoryPlan Balanced = new()
    {
        TrimWhenIdle = true, TrimAfter = TimeSpan.FromMinutes(3), TrimEvery = TimeSpan.FromMinutes(5), ReleaseAfter = TimeSpan.FromMinutes(2),
        ReadingsLinger = TimeSpan.FromSeconds(10), LingerExtras = true, KeepCaches = false, MediaLinger = TimeSpan.FromSeconds(5), RecentCovers = 16,
        PointerFarMilliseconds = 160, QuietWhileOpen = false, WarmAtStart = false
    };
    public static readonly MemoryPlan Smooth = new()
    {
        TrimWhenIdle = false, ReleaseAfter = TimeSpan.FromMinutes(5),
        ReadingsLinger = TimeSpan.FromMinutes(1), LingerExtras = true, KeepCaches = true, MediaLinger = TimeSpan.FromSeconds(30), RecentCovers = 32,
        PointerFarMilliseconds = 160, QuietWhileOpen = true, WarmAtStart = true
    };

    public static MemoryPlan For(MemoryMode mode) => mode switch { MemoryMode.Saver => Saver, MemoryMode.Smooth => Smooth, _ => Balanced };
}

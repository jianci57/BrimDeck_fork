using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core;

// What is kept of one device ("VVVV:PPPP"): the read that answered it (by its stable name) and the collection it answered on
// ("page:usage:interface"), the reads that went wrong on it, and whether it is no longer read at all.
public sealed class DeviceReadEntry
{
    public string Device { get; set; } = "";
    public string? Way { get; set; }
    public string? Collection { get; set; }
    public DateTimeOffset? Found { get; set; }
    public List<string> Bad { get; set; } = [];
    public bool Stopped { get; set; }
}

// Which of a brand's reads each device answers, kept across runs and versions (device-reads.json), so a device is asked
// only the read it answered and is not tried again with every one:
// - No entry: the reads are tried, at most ProbeRounds times (when it is first seen, then RetryMs apart), and again only when
//   it arrives anew (Arrived).
// - An entry: only its read, on its collection. After MissesBeforeRecheck readings in a row without an answer it is checked
//   once (its read first, then the others); another read that answers then replaces it. A check that finds nothing is not
//   made again until the kept read answers again or the device arrives anew, so a device asleep for days is not asked every
//   read it might take over and over. A read the entry names that this version no longer has counts as no entry.
// - A read that went wrong on the device (Harmed) is never sent to it again; after HarmToStop of them the device is not read.
// - The user can turn a device's reading off (DeviceReadSwitches) and on again, which clears what went wrong (Resume).
// Times are Environment.TickCount64 values for the waits, wall-clock values for what is kept.
public sealed class DeviceReadBook
{
    public const int ProbeRounds = 3, MissesBeforeRecheck = 20, HarmToStop = 2, MaximumEntries = 512;
    public static readonly long[] RetryMs = [5 * 60_000, 30 * 60_000];

    private sealed class Run { public int Rounds; public long Next; public int Misses; public bool Rechecked; }
    private readonly Dictionary<string, DeviceReadEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Run> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    // Set when something kept changed; cleared by Serialize.
    public bool Dirty { get; private set; }

    public enum Kind { None, Kept, Recheck, Probe }
    // Kept: the device's kept read where it is asked on its kept collection (already known to that collection); every other
    // way in Ways is a first try there.
    public readonly record struct Ask(Kind Kind, IReadOnlyList<string> Ways, string? Kept = null);

    public static DeviceReadBook Parse(string? json)
    {
        var book = new DeviceReadBook();
        if (string.IsNullOrWhiteSpace(json)) return book;
        try
        {
            var entries = JsonSerializer.Deserialize<List<DeviceReadEntry>>(json, Options) ?? [];
            foreach (var entry in entries.Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Device)).Take(MaximumEntries))
            {
                entry.Bad = [.. (entry.Bad ?? []).Where(way => !string.IsNullOrWhiteSpace(way)).Distinct()];
                book._entries[entry.Device] = entry;
            }
        }
        catch (JsonException) { }
        return book;
    }

    public string Serialize()
    {
        lock (_gate)
        {
            Dirty = false;
            return JsonSerializer.Serialize(_entries.Values.OrderBy(entry => entry.Device, StringComparer.OrdinalIgnoreCase).ToList(), Options);
        }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // A copy: the kept entry itself changes under the worker's reads.
    public DeviceReadEntry? Entry(string device)
    {
        lock (_gate) return _entries.TryGetValue(device, out var entry)
            ? new DeviceReadEntry { Device = entry.Device, Way = entry.Way, Collection = entry.Collection, Found = entry.Found, Bad = [.. entry.Bad], Stopped = entry.Stopped }
            : null;
    }
    // The collection the device's kept read answered on, if it has one.
    public string? KeptCollection(string device) { lock (_gate) return _entries.TryGetValue(device, out var entry) && entry.Way is not null ? entry.Collection : null; }
    public bool Stopped(string device) { lock (_gate) return _entries.TryGetValue(device, out var entry) && entry.Stopped; }

    // What to ask a collection of the device now. ways: the reads that collection takes, in the brand's order. keptPresent: the
    // device's kept collection (KeptCollection) is among its collections now.
    public Ask Plan(string device, string collection, IReadOnlyList<string> ways, long now, bool keptPresent)
    {
        lock (_gate)
        {
            _entries.TryGetValue(device, out var entry);
            if (entry?.Stopped == true) return new(Kind.None, []);
            var usable = entry is null ? ways : [.. ways.Where(way => !entry.Bad.Contains(way))];
            if (usable.Count == 0) return new(Kind.None, []);
            var run = RunOf(device, collection);
            // A device with a kept read is asked on its kept collection only, while that is there.
            if (entry?.Way is { } way && keptPresent)
            {
                if (!string.Equals(entry.Collection, collection, StringComparison.OrdinalIgnoreCase)) return new(Kind.None, []);
                if (ways.Contains(way))
                {
                    if (run.Misses >= MissesBeforeRecheck && !run.Rechecked && usable.Count > 1)
                        return new(Kind.Recheck, [way, .. usable.Where(other => other != way)], way);
                    return new(Kind.Kept, [way], way);
                }
            }
            if (run.Rounds >= ProbeRounds || now < run.Next) return new(Kind.None, []);
            return new(Kind.Probe, usable);
        }
    }

    // The read that answered (an answer that the device is asleep counts).
    public void Answered(string device, string collection, string way, DateTimeOffset at)
    {
        lock (_gate)
        {
            var run = RunOf(device, collection);
            run.Misses = 0; run.Rounds = 0; run.Next = 0; run.Rechecked = false;
            var entry = EntryOf(device);
            if (entry.Way == way && string.Equals(entry.Collection, collection, StringComparison.OrdinalIgnoreCase)) return;
            entry.Way = way; entry.Collection = collection; entry.Found = at;
            Dirty = true;
        }
    }

    // No read answered what Plan gave.
    public void Silent(string device, string collection, Kind kind, long now)
    {
        lock (_gate)
        {
            var run = RunOf(device, collection);
            switch (kind)
            {
                case Kind.Kept: run.Misses++; break;
                case Kind.Recheck: run.Misses = 0; run.Rechecked = true; break;
                case Kind.Probe:
                    run.Rounds++;
                    run.Next = run.Rounds <= RetryMs.Length ? now + RetryMs[run.Rounds - 1] : long.MaxValue;
                    break;
            }
        }
    }

    // The device came anew (plugged in): its reads may be tried again.
    public void Arrived(string device)
    {
        lock (_gate) foreach (var key in _runs.Keys.Where(key => key.StartsWith(device + "|", StringComparison.OrdinalIgnoreCase)).ToList()) _runs.Remove(key);
    }

    // A read went wrong on the device: it is not sent to it again. True when the device is no longer read at all.
    public bool Harmed(string device, string way)
    {
        lock (_gate)
        {
            var entry = EntryOf(device);
            if (!entry.Bad.Contains(way)) entry.Bad.Add(way);
            if (entry.Way == way) { entry.Way = null; entry.Collection = null; entry.Found = null; }
            if (entry.Bad.Count >= HarmToStop) entry.Stopped = true;
            Dirty = true;
            return entry.Stopped;
        }
    }

    // The user turned the device's reading on again: what went wrong is forgotten and its reads may be tried again.
    public void Resume(string device)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(device, out var entry) && (entry.Stopped || entry.Bad.Count > 0)) { entry.Stopped = false; entry.Bad.Clear(); Dirty = true; }
        }
        Arrived(device);
    }

    private Run RunOf(string device, string collection)
    {
        string key = device + "|" + collection;
        if (!_runs.TryGetValue(key, out var run)) _runs[key] = run = new Run();
        return run;
    }
    private DeviceReadEntry EntryOf(string device)
    {
        if (!_entries.TryGetValue(device, out var entry))
        {
            // The oldest found are let go once the book is full.
            if (_entries.Count >= MaximumEntries)
                foreach (var old in _entries.Values.Where(e => !e.Stopped && e.Bad.Count == 0).OrderBy(e => e.Found ?? DateTimeOffset.MinValue).Take(_entries.Count - MaximumEntries + 1).ToList())
                    _entries.Remove(old.Device);
            _entries[device] = entry = new DeviceReadEntry { Device = device };
        }
        return entry;
    }
}

// The devices the user turned reading off for ("VVVV:PPPP"), set by the app from the settings, read by the device readers.
// Known: every device the settings remember with its hardware (its key, name and kind), so a device turned off, or one that
// gives no reading for a while, stays listed under the key it had while it was read rather than turning into a new device
// (whose place, name and switches the user has not set) or disappearing. Off: those turned off.
public static class DeviceReadSwitches
{
    public sealed record Known(string Key, string Name, DeviceKind Kind, string Hardware, bool Read);
    private sealed record State(IReadOnlySet<string> Off, IReadOnlyList<Known> Devices);
    private static volatile State _state = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), []);
    public static IReadOnlySet<string> Off => _state.Off;
    public static IReadOnlyList<Known> Devices => _state.Devices;
    public static bool IsOff(string hardware) => _state.Off.Contains(hardware);
    public static void Use(IEnumerable<DeviceChoice> choices)
    {
        var known = choices.Where(choice => !string.IsNullOrWhiteSpace(choice.Hardware))
            .Select(choice => new Known(choice.Key, choice.Name.Length > 0 ? choice.Name : choice.Hardware!, choice.Kind, choice.Hardware!, choice.Read)).ToList();
        var off = known.Where(device => !device.Read).Select(device => device.Hardware).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The same set while nothing was switched, so a reader can tell a switch by the set alone.
        var was = _state.Off;
        _state = new(was.Count == off.Count && was.SetEquals(off) ? was : off, known);
    }
    // The key a device of this hardware is remembered under: one it was read under before one the unread listing gave it
    // ("hid:"); null when none is.
    public static Known? Remembered(string hardware)
    {
        Known? any = null;
        foreach (var device in _state.Devices)
        {
            if (!string.Equals(device.Hardware, hardware, StringComparison.OrdinalIgnoreCase)) continue;
            if (!device.Key.StartsWith("hid:", StringComparison.Ordinal)) return device;
            any ??= device;
        }
        return any;
    }
}

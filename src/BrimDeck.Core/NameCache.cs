namespace BrimDeck.Core;

// Keeps a cache of looked-up names (by executable) from growing without bound: past limit entries, those not in keep (the programs running
// now) are dropped. What is running stays, so a list never loses a name it is showing; a program that comes back is looked up again.
public static class NameCache
{
    // How many entries were dropped.
    public static int Trim<T>(IDictionary<string, T> cache, IEnumerable<string> keep, int limit)
    {
        if (cache.Count <= limit) return 0;
        var running = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        var drop = cache.Keys.Where(key => !running.Contains(key)).ToList();
        foreach (var key in drop) cache.Remove(key);
        return drop.Count;
    }
}

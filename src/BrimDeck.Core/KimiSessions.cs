using System.Text.Json;

namespace BrimDeck.Core;

// Kimi Code writes each session to sessions\<work dir>\<session>\agents\<agent>\wire.jsonl, one JSON event per line;
// a subagent writes its calls only to its own file. Every model response that came back with usage writes one record:
// { type: "usage.record", agentId, model: "kimi-code/<model>", usage: { inputOther, output, inputCacheRead,
// inputCacheCreation }, usageScope: "turn" | "session", time (ms) }. The four counts do not overlap. Each record is the
// usage of one call ("session" marks a call outside a turn, such as compaction), so every record is counted, and the
// step.end events, which repeat the same usage, are not. A copied session repeats its records, so a record is counted
// once per agent, time, model and usage.
public sealed class KimiSessions
{
    // Files only grow at the end. Each one remembers where its last complete line ended, so a refresh reads only the lines
    // written since. A shorter or rewritten file, or an earlier start date, is read again from the start.
    private sealed class CacheItem(DateTimeOffset cutoff)
    {
        public readonly DateTimeOffset Cutoff = cutoff;
        public long Length, Offset;
        public DateTime Modified;
        public readonly List<TokenEntry> Entries = [];
        public int Errors;
    }
    private readonly Dictionary<string, CacheItem> _cache = new(StringComparer.OrdinalIgnoreCase);
    // No file or start-date change needs another merge while now stays between the latest included record (inclusive)
    // and the earliest future record (exclusive). Both bounds matter when the clock moves backwards; sessions follow the same bounds.
    private (DateTimeOffset Start, DateTimeOffset From, DateTimeOffset? Until, List<TokenEntry> Entries, int Sessions)? _merged;
    // Requests and the system prompt can make a line very long; a line over this length is skipped without being parsed.
    // A usage record is far shorter, so a skipped line that held one counts as unreadable.
    public const int DefaultMaxLine = 1 << 20;
    private const int Chunk = 64 << 10;
    private readonly int _maxLine;

    public KimiSessions(int maxLine = DefaultMaxLine) => _maxLine = Math.Max(1024, maxLine);

    public static string Folder(string home) => Path.Combine(home, "sessions");

    public void Read(ProviderSnapshot snapshot, string home, DateTimeOffset start, DateTimeOffset now)
    {
        var root = Folder(home);
        snapshot.UsageAvailable = Directory.Exists(root);
        if (!snapshot.UsageAvailable) { _cache.Clear(); _merged = null; snapshot.UsageNote = Loc.T("未找到本机 Kimi Code 会话记录。", "No local Kimi Code session records were found."); return; }
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = Directory.EnumerateFiles(root, "wire.jsonl", options)
            .Where(file => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file))) is { } agents && agents.Equals("agents", StringComparison.OrdinalIgnoreCase));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed = _merged is not { } kept || kept.Start != start || now < kept.From || kept.Until is { } until && now >= until;
        int errors = 0;
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < start.UtcDateTime) continue;
                visited.Add(file);
                if (!_cache.TryGetValue(file, out var cached) || start < cached.Cutoff || info.Length < cached.Length ||
                    info.Length == cached.Length && info.LastWriteTimeUtc != cached.Modified)
                    _cache[file] = cached = new CacheItem(start);
                if (info.Length != cached.Length || info.LastWriteTimeUtc != cached.Modified) { Parse(file, cached, info); changed = true; }
                errors += cached.Errors;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; changed |= _cache.Remove(file); }
        }
        foreach (var gone in _cache.Keys.Where(file => !visited.Contains(file)).ToList()) { _cache.Remove(gone); changed = true; }
        if (changed || _merged is null)
        {
            var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new Dictionary<string, TokenEntry>();
            var from = DateTimeOffset.MinValue;
            DateTimeOffset? next = null;
            foreach (var (file, cached) in _cache)
            {
                bool any = false;
                foreach (var entry in cached.Entries)
                {
                    if (entry.Time < start || entry.Total <= 0) continue;
                    if (entry.Time > now) { if (next is null || entry.Time < next.Value) next = entry.Time; continue; }
                    if (entry.Time > from) from = entry.Time;
                    entries.TryAdd(entry.Key, entry); any = true;
                }
                if (any) sessions.Add(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(file)))!);
            }
            _merged = (start, from, next, entries.Values.OrderBy(e => e.Time).ToList(), sessions.Count);
        }
        var merged = _merged.Value;
        snapshot.Entries = merged.Entries;
        snapshot.UsageComplete = errors == 0;
        snapshot.UsageNote = Loc.T($"本机 Kimi Code {start.LocalDateTime:yyyy-MM-dd} 起 · {merged.Sessions} 个会话 · {snapshot.Entries.Count} 次模型请求",
            $"Local Kimi Code since {start.LocalDateTime:yyyy-MM-dd} · " + Loc.Count(merged.Sessions, "session", "sessions") + " · " + Loc.Count(snapshot.Entries.Count, "model request", "model requests"));
        if (errors > 0) snapshot.UsageNote += Loc.T($" · {errors} 处记录未能读取", " · " + Loc.Count(errors, "record", "records") + " could not be read");
    }

    // Reads the complete lines after the remembered offset (AppendedLines); a line over the limit that held a usage record
    // counts as unreadable.
    private void Parse(string file, CacheItem item, FileInfo info)
    {
        using var stream = SharedFile.Open(file);
        AppendedLines.Read(stream, ref item.Offset, Chunk, _maxLine, RecordKeys, line => ParseLine(line.Span, file, item), () => item.Errors++);
        item.Length = info.Length; item.Modified = info.LastWriteTimeUtc;
    }

    private static ReadOnlySpan<byte> RecordType => "\"usage.record\""u8;
    private static readonly byte[][] RecordKeys = [RecordType.ToArray()];

    private static void ParseLine(ReadOnlySpan<byte> line, string file, CacheItem item)
    {
        if (line.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) line = line[3..];
        if (line.IndexOf(RecordType) < 0) return;
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var obj = doc.RootElement;
            if (obj.Get("type").Text() != "usage.record") return;
            var usage = obj.Get("usage");
            if (usage.ValueKind != JsonValueKind.Object || obj.Get("time").Number() is not { } millis) return;
            DateTimeOffset time;
            try { time = DateTimeOffset.FromUnixTimeMilliseconds((long)millis).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { return; }
            if (time < item.Cutoff) return;
            long input = usage.Get("inputOther").Count(), output = usage.Get("output").Count();
            long read = usage.Get("inputCacheRead").Count(), write = usage.Get("inputCacheCreation").Count();
            var model = obj.Get("model").Text();
            // Older versions write no agentId; the agent's folder names it.
            var agent = obj.Get("agentId").Text() is { Length: > 0 } id ? id : Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
            item.Entries.Add(new($"kimi:{agent}:{(long)millis}:{model}:{input}:{read}:{write}:{output}", time,
                KimiUsage.ModelName(model) is { Length: > 0 } name ? name : "unknown", input, read, write, 0, output));
        }
        catch (JsonException) { item.Errors++; }
    }
}

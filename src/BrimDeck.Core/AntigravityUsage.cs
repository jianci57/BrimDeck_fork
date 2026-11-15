using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

// Antigravity 2.x and the Antigravity CLI (agy) keep one SQLite database per conversation, in
// ~/.gemini/antigravity/conversations and ~/.gemini/antigravity-cli/conversations. Reading them directly
// includes CLI conversations, works while Antigravity is closed, and does not depend on which conversations
// the running app has loaded. Field numbers come from the protobuf descriptors embedded in agy.exe:
//   steps.metadata          CortexStepMetadata: 1 created_at (Timestamp), 9 model_usage
//   gen_metadata.data       1 ChatModelMetadata: 4 usage, 19 response_model, 21 model_display_name (the name in the model menu)
//   ModelUsageStats         1 model (an enum), 2 input, 3 output (thinking included), 4 cache write, 5 cache read, 7 message id
// Input excludes cache reads (a cache read can exceed the input of the same request), so both are counted.
public sealed class AntigravityUsage
{
    private sealed record CacheItem(DateTime Modified, long Length, DateTimeOffset Cutoff, List<TokenEntry> Entries, bool Complete);
    private readonly Dictionary<string, CacheItem> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Folders(string home) =>
        [Path.Combine(home, ".gemini", "antigravity", "conversations"), Path.Combine(home, ".gemini", "antigravity-cli", "conversations")];

    // Each Antigravity language server writes its process id, ports and CSRF token to daemon\hub_<http port>.json. The files of
    // servers that have ended stay behind, so a record is only used while that process listens on its ports. Reading the file
    // works when Antigravity runs as administrator, whose command line a normal process cannot read.
    public sealed record LanguageServer(int ProcessId, int HttpsPort, int HttpPort, string Token);
    public static string DaemonFolder(string home) => Path.Combine(home, ".gemini", "antigravity", "daemon");

    public static List<LanguageServer> ReadLanguageServers(string folder)
    {
        var servers = new List<LanguageServer>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "hub_*.json").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return servers; }
        foreach (var file in files)
        {
            JsonDocument? doc;
            try { doc = SharedFile.ReadJson(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
            if (doc is null) continue;
            using var _ = doc;
            var root = doc.RootElement;
            var token = root.Get("csrfToken").Text();
            int Port(string name) => root.Get(name).Number() is { } n && n is > 0 and <= 65535 && n == Math.Floor(n) ? (int)n : 0;
            if (root.Get("pid").Number() is not { } pid || pid <= 0 || pid > int.MaxValue || token.Length == 0) continue;
            var server = new LanguageServer((int)pid, Port("httpsPort"), Port("httpPort"), token);
            if (server.HttpsPort > 0 || server.HttpPort > 0) servers.Add(server);
        }
        return servers;
    }

    public void Read(ProviderSnapshot snapshot, IDesktopSources desktop, IReadOnlyList<string> folders, DateTimeOffset start, DateTimeOffset now)
    {
        var existing = folders.Where(Directory.Exists).ToList();
        snapshot.UsageAvailable = existing.Count > 0;
        if (!snapshot.UsageAvailable) { snapshot.UsageNote = Loc.T("未找到本机 Antigravity 会话记录。", "No local Antigravity session records were found."); return; }
        var entries = new Dictionary<string, TokenEntry>(StringComparer.Ordinal);
        int count = 0, failed = 0;
        // Only conversations inside the current window stay cached, so the cache does not grow for as long as the app runs.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var database in existing.SelectMany(folder => SafeFiles(folder)))
        {
            // Recent messages may still sit in the write-ahead log, so it counts towards the file's age and identity.
            var wal = new FileInfo(database + "-wal");
            var info = new FileInfo(database);
            var modified = wal.Exists && wal.LastWriteTimeUtc > info.LastWriteTimeUtc ? wal.LastWriteTimeUtc : info.LastWriteTimeUtc;
            if (modified < start.UtcDateTime) continue;
            visited.Add(database);
            var length = info.Length + (wal.Exists ? wal.Length : 0);
            if (!_cache.TryGetValue(database, out var cached) || cached.Modified != modified || cached.Length != length || start < cached.Cutoff)
                _cache[database] = cached = Parse(desktop, database, start, modified, length);
            count++;
            if (!cached.Complete) failed++;
            foreach (var entry in cached.Entries.Where(e => e.Time >= start && e.Time <= now)) entries.TryAdd(entry.Key, entry);
        }
        foreach (var gone in _cache.Keys.Where(database => !visited.Contains(database)).ToList()) _cache.Remove(gone);
        snapshot.Entries = entries.Values.OrderBy(e => e.Time).ToList();
        snapshot.UsageComplete = failed == 0;
        snapshot.UsageNote = Loc.T($"本机 Antigravity 与 Antigravity CLI {start.LocalDateTime:yyyy-MM-dd} 起 · {count} 个会话",
            $"Local Antigravity and Antigravity CLI since {start.LocalDateTime:yyyy-MM-dd} · " + Loc.Count(count, "session", "sessions"));
        if (failed > 0) snapshot.UsageNote += Loc.T($" · {failed} 个会话暂时无法读取", " · " + Loc.Count(failed, "session", "sessions") + " cannot be read right now");
    }

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*.db").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static CacheItem Parse(IDesktopSources desktop, string database, DateTimeOffset start, DateTime modified, long length)
    {
        var steps = desktop.QueryDatabase(database, "SELECT hex(metadata) FROM steps WHERE metadata IS NOT NULL");
        if (steps is null) return new(modified, length, start, [], false);
        var models = new Dictionary<string, ModelName>(StringComparer.Ordinal);
        var byEnum = new Dictionary<ulong, ModelName>();
        foreach (var row in desktop.QueryDatabase(database, "SELECT hex(data) FROM gen_metadata WHERE data IS NOT NULL") ?? [])
        {
            var chat = Field(Bytes(row), 1);
            var usage = Field(chat, 4);
            var id = Text(Field(usage, 7));
            if (Name(Text(Field(chat, 19)), Text(Field(chat, 21))) is not { } name) continue;
            if (id.Length > 0) models[id] = name;
            if (Varint(usage, 1) is { } number) byEnum.TryAdd(number, name);
        }
        var entries = new List<TokenEntry>();
        foreach (var row in steps)
        {
            var metadata = Bytes(row);
            var usage = Field(metadata, 9);
            var seconds = Varint(Field(metadata, 1), 1);
            if (usage.Length == 0 || seconds is not { } unix || unix > long.MaxValue) continue;
            var id = Text(Field(usage, 7));
            if (id.Length == 0) continue;
            DateTimeOffset time;
            try { time = DateTimeOffset.FromUnixTimeSeconds((long)unix).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { continue; }
            // A step without a generation record of its own (a checkpoint that condenses the conversation) is named by the
            // model number its usage carries, as the generations of that model in the same conversation are.
            var name = models.GetValueOrDefault(id) ?? (Varint(usage, 1) is { } number ? byEnum.GetValueOrDefault(number) : null);
            var entry = new TokenEntry("ag:" + id, time, name?.Model ?? "", Count(usage, 2), Count(usage, 5), Count(usage, 4), 0, Count(usage, 3)) { PriceModel = name?.Price };
            if (entry.Total > 0 && time >= start) entries.Add(entry);
        }
        return new(modified, length, start, entries, true);
    }

    private sealed record ModelName(string Model, string? Price);

    // A record is shown by the name Antigravity's model menu gives it (model_display_name), and by its response model where it has
    // none. An orchestrated generation names only its role as the response model ("gemini-pro-orchestrator",
    // "gemini-flash-worker-agy"); its display name says which model ran, and that model prices it:
    //   "Gemini 4 Hydrogen (Orchestrator)" → "gemini-4-hydrogen";
    //   "Orchestrator (Carbon)" → "carbon", the family alone, which Pricing reads as the newest Gemini of that name.
    // Other records keep their response model as the price name. No model is listed here, so a new model needs no change.
    private static ModelName? Name(string response, string display)
    {
        display = display.Trim();
        if (display.Length == 0) return response.Length > 0 ? new(response, null) : null;
        var price = response.Length > 0 && response != display ? response : null;
        if (Regex.Match(display, @"^(?<outer>[^()]+?)\s*\((?<inner>[^()]+)\)$") is { Success: true } named)
        {
            string outer = named.Groups["outer"].Value.Trim(), inner = named.Groups["inner"].Value.Trim();
            if (outer.StartsWith("Gemini ", StringComparison.OrdinalIgnoreCase)) price = Slug(outer);
            else if (inner.StartsWith("Gemini ", StringComparison.OrdinalIgnoreCase)) price = Slug(inner);
            else if (!outer.Any(char.IsDigit) && Regex.IsMatch(inner, @"^[A-Za-z]+$")) price = inner.ToLowerInvariant();
        }
        return new(display, price);
    }
    private static string Slug(string name) => Regex.Replace(name.Trim().ToLowerInvariant(), @"\s+", "-");

    private static byte[] Bytes(string?[] row)
    {
        try { return row.Length > 0 && !string.IsNullOrEmpty(row[0]) ? Convert.FromHexString(row[0]!) : []; }
        catch (FormatException) { return []; }
    }

    private static long Count(byte[] message, int field) => (long)Math.Min(Varint(message, field) ?? 0, long.MaxValue);
    private static string Text(byte[] data) => System.Text.Encoding.UTF8.GetString(data);

    // The first length-delimited field with this number (a nested message or a string); empty when absent.
    private static byte[] Field(byte[] message, int field)
    {
        foreach (var (number, wire, _, data) in Fields(message))
            if (number == field && wire == 2) return data;
        return [];
    }

    private static ulong? Varint(byte[] message, int field)
    {
        foreach (var (number, wire, value, _) in Fields(message))
            if (number == field && wire == 0) return value;
        return null;
    }

    // A minimal protobuf reader; malformed input ends the enumeration instead of throwing.
    private static IEnumerable<(int Number, int Wire, ulong Value, byte[] Data)> Fields(byte[] message)
    {
        int i = 0;
        while (i < message.Length)
        {
            if (!TryVarint(message, ref i, out var key)) yield break;
            int number = (int)(key >> 3), wire = (int)(key & 7);
            if (wire == 0) { if (!TryVarint(message, ref i, out var value)) yield break; yield return (number, wire, value, []); }
            else if (wire == 2)
            {
                if (!TryVarint(message, ref i, out var size) || size > (ulong)(message.Length - i)) yield break;
                var data = message.AsSpan(i, (int)size).ToArray(); i += (int)size;
                yield return (number, wire, 0, data);
            }
            else if (wire == 1) i += 8;
            else if (wire == 5) i += 4;
            else yield break;
        }
    }

    private static bool TryVarint(byte[] data, ref int i, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && i < data.Length; shift += 7)
        {
            var b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return true;
        }
        return false;
    }
}

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace BrimDeck.Core;

public sealed class LogReader
{
    // Session logs only grow at the end. Each file remembers where its last complete line ended, together with the
    // Codex parser's running state, so a refresh parses only the lines written since. A shorter or rewritten file,
    // or an earlier start date, is parsed again from the beginning; a later start date drops the entries before it.
    private sealed class CacheItem(DateTimeOffset cutoff)
    {
        public DateTimeOffset Cutoff = cutoff;
        public long Length, Offset;
        public DateTime Modified;
        public readonly List<TokenEntry> Entries = [];
        public JsonElement Quota;
        public DateTimeOffset? QuotaTime;
        public int Errors;
        public string Model = "unknown", LastTotal = "";
    }
    // Claude, CodeAgent and Codex read different folders; each keeps only the files its latest read visited.
    private readonly Dictionary<ProviderId, Dictionary<string, CacheItem>> _caches = [];
    // The merged entries of the latest read, kept while no file changed and the start date stayed: most refreshes find nothing new.
    private readonly Dictionary<ProviderId, (DateTimeOffset Cutoff, List<TokenEntry> Entries)> _merged = [];
    // A line longer than this (a huge tool result) is skipped and counted as unreadable instead of growing the buffer without end.
    public const int DefaultMaxLine = 32 << 20;
    private const int Chunk = 1 << 20;
    private readonly int _maxLine;

    public LogReader(int maxLine = DefaultMaxLine) => _maxLine = Math.Max(1024, maxLine);

    public ProviderSnapshot Read(ProviderId id, string root, DateTimeOffset now, DateTime? startDate = null) => Read(id, [root], now, startDate);

    // roots are data folders of the same tool; place names where they are, for the note shown with the statistics.
    public ProviderSnapshot Read(ProviderId id, IReadOnlyList<string> roots, DateTimeOffset now, DateTime? startDate = null, string? place = null)
    {
        var result = new ProviderSnapshot(id);
        var cutoff = new DateTimeOffset(startDate?.Date ?? now.LocalDateTime.Date.AddDays(-29));
        result.UsageStart = cutoff.LocalDateTime.Date;
        var folders = id == ProviderId.Pi ? new[] { "" } : id == ProviderId.Codex ? new[] { "sessions", "archived_sessions" } : new[] { "projects" };
        if (!_caches.TryGetValue(id, out var cache)) _caches[id] = cache = new(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed = !_merged.TryGetValue(id, out var merged) || merged.Cutoff != cutoff;
        int count = 0, errors = 0;
        JsonElement latestQuota = default;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in roots.SelectMany(dataRoot => folders.Select(name => Path.Combine(dataRoot, name))))
        {
            if (!Directory.Exists(folder)) continue;
            result.UsageAvailable = true;
            // The enumeration already carries each file's size and time, so no file is asked for them again.
            foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*.jsonl", options))
            {
                string file = info.FullName;
                try
                {
                    if (info.LastWriteTimeUtc < cutoff.UtcDateTime) continue;
                    visited.Add(file);
                    if (!cache.TryGetValue(file, out var cached) || cutoff < cached.Cutoff || info.Length < cached.Length ||
                        info.Length == cached.Length && info.LastWriteTimeUtc != cached.Modified)
                        cache[file] = cached = new CacheItem(cutoff);
                    else if (cached.Cutoff < cutoff) { cached.Entries.RemoveAll(entry => entry.Time < cutoff); cached.Cutoff = cutoff; }
                    if (info.Length != cached.Length || info.LastWriteTimeUtc != cached.Modified) { Parse(file, id, cached, info); changed = true; }
                    count++; errors += cached.Errors;
                    if (cached.QuotaTime is { } time && (result.QuotaTime is null || time > result.QuotaTime))
                    { result.QuotaTime = time; latestQuota = cached.Quota; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; changed |= cache.Remove(file); }
            }
        }
        foreach (var gone in cache.Keys.Where(file => !visited.Contains(file)).ToList()) { cache.Remove(gone); changed = true; }
        if (changed)
        {
            var entries = new Dictionary<string, TokenEntry>();
            foreach (var cached in cache.Values)
                foreach (var entry in cached.Entries)
                {
                    if (entry.Time < cutoff || entry.Time > now || entry.Total <= 0) continue;
                    // Streaming Claude records can repeat an id across files; keep its most complete usage.
                    if (!entries.TryGetValue(entry.Key, out var old) || entry.Total >= old.Total) entries[entry.Key] = entry;
                }
            _merged[id] = merged = (cutoff, entries.Values.OrderBy(x => x.Time).ToList());
        }
        result.Entries = merged.Entries;
        var where = place ?? Loc.T("本机", "This PC");
        result.UsageNote = result.UsageAvailable ? Loc.T($"{where} {cutoff.LocalDateTime:yyyy-MM-dd} 起 · {count} 个记录文件", $"{where} since {cutoff.LocalDateTime:yyyy-MM-dd} · " + Loc.Count(count, "record file", "record files"))
            : Loc.T("未找到本机会话记录。", "No local session records were found.");
        result.UsageComplete = errors == 0;
        if (errors > 0) result.UsageNote += Loc.T($" · {errors} 处记录未能读取", " · " + Loc.Count(errors, "record", "records") + " could not be read");
        if (latestQuota.ValueKind == JsonValueKind.Object)
        {
            result.Quotas = QuotaParser.Codex(latestQuota, result.QuotaTime ?? now, false);
            result.Plan = latestQuota.Get("plan_type").Text();
            result.Source = Loc.T("本地配额快照", "Local quota snapshot");
            result.Status = Loc.T("本地记录", "Local records");
        }
        return result;
    }

    // Reads the complete lines after the remembered offset (AppendedLines). Of the lines over the limit, only one that held
    // usage counts as unreadable; others, such as a large tool result or an attached file, are dropped quietly.
    private void Parse(string file, ProviderId id, CacheItem item, FileInfo info)
    {
        using var stream = SharedFile.Open(file);
        AppendedLines.Read(stream, ref item.Offset, Chunk, _maxLine, UsageKeys, line => ParseLine(line, id, item), () => item.Errors++);
        item.Length = info.Length; item.Modified = info.LastWriteTimeUtc;
    }

    // ParseLine reads only lines with one of these keys.
    // WorkBuddy's replies carry "rawUsage", usually with its own "usage" as well.
    private static readonly byte[][] UsageKeys = ["\"token_count\""u8.ToArray(), "\"turn_context\""u8.ToArray(), "\"usage\""u8.ToArray(), "\"rawUsage\""u8.ToArray()];
    private static bool HasUsageKey(ReadOnlySpan<byte> span) => AppendedLines.Holds(span, UsageKeys);

    // Pi records disjoint token categories. Read only accounting metadata, never message content or auth files.
    // Entry ids and timestamps survive forks/clones, so copied history is counted once across session files.
    private static void ParsePi(JsonElement obj, DateTimeOffset? time, CacheItem item)
    {
        var type = obj.Get("type").Text();
        var message = obj.Get("message");
        var source = type == "message" ? message : obj;
        if (type == "message" && message.Get("role").Text() != "assistant") return;
        if (type is not ("message" or "usage" or "compaction" or "branch_summary")) return;
        if (message.Get("stopReason").Text() == "pending") return;
        time = source.Get("timestamp").Date() ?? time;
        if (time is null || time < item.Cutoff) return;
        var usage = source.Get("usage");
        if (usage.ValueKind != JsonValueKind.Object) return;
        long input = usage.Get("input").Count(), read = usage.Get("cacheRead").Count();
        long write = usage.Get("cacheWrite").Count(), output = usage.Get("output").Count();
        long hour = Math.Min(write, usage.Get("cacheWrite1h").Count());
        var model = source.Get("model").Text();
        if (model.Length == 0) model = "unknown";
        var provider = source.Get("provider").Text();
        // Do not mix provider-specific recorded costs into one inferred model price.
        var modelName = string.Intern(provider.Length == 0 ? model : provider + "/" + model);
        decimal? cost = null;
        var reported = usage.Get("cost").Get("total");
        if (reported.ValueKind == JsonValueKind.Number && reported.TryGetDecimal(out var value) && value >= 0) cost = value;
        var identity = obj.Get("id").Text();
        if (identity.Length == 0) identity = usage.GetRawText();
        Add(item, new($"pi:{time:O}:{provider}:{model}:{identity}", time.Value, modelName,
            input, read, write - hour, hour, output, cost));
    }

    // Command Code writes each message as one line {"type":"message","timestamp","message":{"meta":{"messageId"}},"model","usage"}, the
    // usage only on the model's replies: {inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, cacheWriteTokens1h, costUsd}.
    // inputTokens includes the cache reads and writes; Command Code charges only the rest as uncached input, and costUsd is its own
    // estimate at its list prices. The message id is kept by a resumed or forked session, so a copied message is counted once.
    private static void ParseCommandCode(JsonElement obj, DateTimeOffset? time, CacheItem item)
    {
        if (obj.Get("type").Text() != "message" || time is null || time < item.Cutoff) return;
        var usage = obj.Get("usage");
        if (usage.ValueKind != JsonValueKind.Object) return;
        long input = usage.Get("inputTokens").Count(), read = usage.Get("cacheReadTokens").Count(), write = usage.Get("cacheWriteTokens").Count();
        long hour = Math.Min(write, usage.Get("cacheWriteTokens1h").Count());
        decimal? cost = null;
        var reported = usage.Get("costUsd");
        if (reported.ValueKind == JsonValueKind.Number && reported.TryGetDecimal(out var value) && value >= 0) cost = value;
        var key = obj.Get("message").Get("meta").Get("messageId").Text();
        if (key.Length == 0) key = obj.Get("id").Text();
        if (key.Length == 0) key = $"{time:O}:{usage.GetRawText()}";
        var model = obj.Get("model").Text();
        Add(item, new("cmd:" + key, time.Value, string.Intern(model.Length == 0 ? "unknown" : model), Math.Max(0, input - read - write), read,
            write - hour, hour, usage.Get("outputTokens").Count(), cost));
    }

    // WorkBuddy writes each model reply as one line {"type":"message","role":"assistant","timestamp":<ms>,"providerData":{"messageId",
    // "stepSeq","model","rawUsage","usage"}}. rawUsage is the gateway's OpenAI-style usage: prompt_tokens includes the cache reads
    // (prompt_tokens_details.cached_tokens, also given as prompt_cache_hit_tokens) and writes (cache_creation_input_tokens), and
    // completion_tokens includes the reasoning. providerData.usage is WorkBuddy's own summary of the same counts, used when rawUsage is
    // missing. The credits WorkBuddy records are not dollars, so the cost is left to the model price table.
    private static void ParseWorkBuddy(JsonElement obj, DateTimeOffset? time, CacheItem item, string prefix)
    {
        if (obj.Get("type").Text() != "message" || obj.Get("role").Text() != "assistant" || time is null || time < item.Cutoff) return;
        var data = obj.Get("providerData");
        var raw = data.Get("rawUsage");
        long input, read, write, output;
        if (raw.ValueKind == JsonValueKind.Object)
        {
            input = raw.Get("prompt_tokens").Count();
            read = Math.Max(raw.Get("prompt_tokens_details").Get("cached_tokens").Count(),
                Math.Max(raw.Get("prompt_cache_hit_tokens").Count(), raw.Get("cache_read_input_tokens").Count()));
            write = Math.Max(raw.Get("cache_creation_input_tokens").Count(), raw.Get("prompt_cache_write_tokens").Count());
            output = raw.Get("completion_tokens").Count();
        }
        else
        {
            var usage = data.Get("usage");
            if (usage.ValueKind != JsonValueKind.Object) return;
            input = usage.Get("inputTokens").Count();
            read = usage.Get("inputTokensDetails").Items().Select(detail => detail.Get("cached_tokens").Count()).DefaultIfEmpty().Max();
            write = 0;
            output = usage.Get("outputTokens").Count();
        }
        read = Math.Min(read, input);
        write = Math.Min(write, input - read);
        var key = data.Get("messageId").Text();
        key = key.Length > 0 ? $"{key}:{data.Get("stepSeq").Number()}" : obj.Get("id").Text();
        if (key.Length == 0) key = $"{time:O}:{input}:{read}:{write}:{output}";
        var model = data.Get("model").Text();
        // The name shown is the channel's ("hy4-free"); the price is looked up by the model's own name ("Hy4").
        var priced = data.Get("requestModelName").Text().Trim();
        Add(item, new(prefix + key, time.Value, string.Intern(model.Length == 0 ? "unknown" : model), input - read - write, read, write, 0, output)
            { PriceModel = priced.Length == 0 ? null : string.Intern(priced) });
    }

    // Claude writes one line per content block of a message, each with the message's usage so far: a repeat of the entry just added
    // replaces it when at least as complete, so the cache holds each message once. Repeats across files are merged in Read.
    private static void Add(CacheItem item, TokenEntry entry)
    {
        if (item.Entries.Count > 0 && item.Entries[^1].Key == entry.Key) { if (entry.Total >= item.Entries[^1].Total) item.Entries[^1] = entry; }
        else item.Entries.Add(entry);
    }

    private static void ParseLine(ReadOnlyMemory<byte> line, ProviderId id, CacheItem item)
    {
        if (line.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) line = line[3..]; // UTF-8 byte order mark
        if (!HasUsageKey(line.Span)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var obj = doc.RootElement;
            var time = obj.Get("timestamp").Date();
            var type = obj.Get("type").Text();
            if (id == ProviderId.Pi)
            {
                ParsePi(obj, time, item);
            }
            else if (id == ProviderId.CommandCode)
            {
                ParseCommandCode(obj, time, item);
            }
            else if (id is ProviderId.WorkBuddy or ProviderId.WorkBuddyAi)
            {
                ParseWorkBuddy(obj, time, item, id == ProviderId.WorkBuddyAi ? "wbai:" : "wb:");
            }
            else if (id == ProviderId.Codex)
            {
                var payload = obj.Get("payload");
                if (type == "turn_context") { item.Model = string.Intern(payload.Get("model").Text()); return; }
                if (type != "event_msg" || payload.Get("type").Text() != "token_count" || time is null) return;
                var limits = payload.Get("rate_limits");
                if (limits.ValueKind == JsonValueKind.Object && (limits.Get("limit_id").Text() is "" or "codex") && (item.QuotaTime is null || time >= item.QuotaTime))
                { item.Quota = limits.Clone(); item.QuotaTime = time; }
                var usageInfo = payload.Get("info");
                var cumulative = usageInfo.Get("total_token_usage");
                var identity = cumulative.ValueKind == JsonValueKind.Object ? cumulative.GetRawText() : "";
                if (identity.Length > 0)
                {
                    if (identity == item.LastTotal) return;
                    item.LastTotal = identity;
                }
                var usage = usageInfo.Get("last_token_usage");
                if (usage.ValueKind != JsonValueKind.Object || time < item.Cutoff) return;
                long input = usage.Get("input_tokens").Count(), cached = Math.Min(input, usage.Get("cached_input_tokens").Count());
                var write = usage.Get("cache_write_input_tokens").Count();
                var output = usage.Get("output_tokens").Count();
                // Timestamp + cumulative counters deduplicate history copied into a fork. The counters are kept as a digest of their
                // text: the same text gives the same key in every copy, at a tenth of the size.
                Span<byte> digest = stackalloc byte[32];
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    if (identity.Length > 0) hash.AppendData(JsonMarshal.GetRawUtf8Value(cumulative));
                    hash.AppendData(":"u8);
                    hash.AppendData(JsonMarshal.GetRawUtf8Value(usage));
                    hash.GetHashAndReset(digest);
                }
                Add(item, new($"codex:{time:O}:{Convert.ToHexString(digest[..16])}", time.Value, item.Model, input - cached, cached, write, 0, output));
            }
            else if (id == ProviderId.CodeAgent && type == "query-metrics")
            {
                // CodeAgent's observability/obs.jsonl records each model request with its usage. A session record can keep only the
                // empty usage of a message whose request was cut short; the same message id here gives the real counts, and Read keeps
                // the larger of the two. Requests outside the conversation (such as session titles) appear only here.
                if (time is null || time < item.Cutoff) return;
                var usage = obj.Get("usage");
                if (usage.ValueKind != JsonValueKind.Object) return;
                var key = obj.Get("message_id").Text();
                if (key.Length == 0) key = $"{time:O}:{usage.GetRawText()}";
                var creation = usage.Get("cache_creation_input_tokens").Count();
                var hour = Math.Min(creation, usage.Get("cache_creation").Get("ephemeral_1h_input_tokens").Count());
                Add(item, new("cac:" + key, time.Value, string.Intern(obj.Get("model").Text()), usage.Get("input_tokens").Count(),
                    usage.Get("cache_read_input_tokens").Count(), creation - hour, hour, usage.Get("output_tokens").Count()));
            }
            else
            {
                if (type != "assistant" || time is null || time < item.Cutoff) return;
                var message = obj.Get("message");
                var usage = message.Get("usage");
                if (usage.ValueKind != JsonValueKind.Object) return;
                var creation = usage.Get("cache_creation_input_tokens").Count();
                var hour = Math.Min(creation, usage.Get("cache_creation").Get("ephemeral_1h_input_tokens").Count());
                var key = message.Get("id").Text();
                if (key.Length == 0) key = obj.Get("uuid").Text();
                if (key.Length == 0) key = $"{time:O}:{usage.GetRawText()}";
                Add(item, new((id == ProviderId.CodeAgent ? "cac:" : "claude:") + key, time.Value, string.Intern(message.Get("model").Text()), usage.Get("input_tokens").Count(),
                    usage.Get("cache_read_input_tokens").Count(), creation - hour, hour, usage.Get("output_tokens").Count()));
            }
        }
        catch (JsonException) { item.Errors++; }
    }
}

public static class QuotaParser
{
    public static List<Quota> Codex(JsonElement root, DateTimeOffset now, bool online)
    {
        var limits = online ? root.Get("rate_limit") : root;
        var result = new List<Quota>();
        foreach (var field in online ? new[] { "primary_window", "secondary_window" } : new[] { "primary", "secondary" })
        {
            var window = limits.Get(field);
            if (window.Get("used_percent").Number() is not { } used) continue;
            int? minutes = online ? (window.Get("limit_window_seconds").Number() is { } seconds ? (int)(seconds / 60) : null) :
                (window.Get("window_minutes").Number() is { } mins ? (int)mins : null);
            var reset = window.Get(online ? "reset_at" : "resets_at").Date();
            if (reset is null && window.Get("reset_after_seconds").Number() is { } after && after is >= 0 and < 31536000) reset = now.AddSeconds(after);
            string label = minutes switch { 300 => "5 小时额度", 10080 => "每周额度", > 0 => $"{minutes} 分钟额度", _ => field.StartsWith("primary") ? "主要额度" : "次要额度" };
            result.Add(new(label, Math.Clamp(used, 0, 100), reset, minutes));
        }
        return result;
    }
    public static List<Quota> Claude(JsonElement root)
    {
        var result = new List<Quota>();
        void Add(string label, double? percent, DateTimeOffset? reset, int? window)
        {
            if (percent is not { } value || value < 0 || result.Any(q => q.Label == label)) return;
            result.Add(new(label, value, reset, window));
        }
        foreach (var (key, label, window) in new[] { ("five_hour", "5 小时额度", 300), ("seven_day", "每周额度", 10080) })
            Add(label, root.Get(key).Get("utilization").Number(), root.Get(key).Get("resets_at").Date(), window);
        // Current Claude clients read server-driven limits, including Fable, from scope.model.display_name.
        // The server decides entitlement; missing/null meters never become an invented zero for a plan.
        foreach (var limit in root.Get("limits").Items())
        {
            string kind = limit.Get("kind").Text(), group = limit.Get("group").Text();
            string model = limit.Get("scope").Get("model").Get("display_name").Text();
            string surface = limit.Get("scope").Get("surface").Get("display_name").Text();
            if (kind == "session") Add("5 小时额度", limit.Get("percent").Number(), limit.Get("resets_at").Date(), 300);
            else if (kind == "weekly_all") Add("每周额度", limit.Get("percent").Number(), limit.Get("resets_at").Date(), 10080);
            else if (!string.IsNullOrWhiteSpace(model.Length > 0 ? model : surface))
            {
                var name = model.Length > 0 ? model : surface;
                bool weekly = group == "weekly" || kind == "weekly_scoped";
                Add(name + (weekly ? " 每周" : ""), limit.Get("percent").Number(), limit.Get("resets_at").Date(), weekly ? 10080 : null);
            }
        }
        foreach (var (key, name) in new[] { ("seven_day_opus", "Opus"), ("seven_day_sonnet", "Sonnet") })
            if (!result.Any(q => q.Label.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                Add(name + " 每周", root.Get(key).Get("utilization").Number(), root.Get(key).Get("resets_at").Date(), 10080);
        return result;
    }
    // A plan merged with Grok (unified) draws every model from one included allowance that resets weekly, so the Cursor
    // and other-model shares no longer apply: totalPercentUsed is its one figure, and billingCycleStart/End is the week.
    // Older plans keep their billing-period quotas.
    public static List<Quota> Cursor(JsonElement root, bool unified = false)
    {
        var plan = root.Get("planUsage");
        if (plan.ValueKind != JsonValueKind.Object) plan = root.Get("individualUsage").Get("plan");
        var reset = root.Get("billingCycleEnd").Date();
        var result = new List<Quota>();
        if (unified)
        {
            if (plan.Get("totalPercentUsed").Number() is not { } used) return result;
            int? minutes = root.Get("billingCycleStart").Date() is { } begin && reset is { } end && end > begin ? (int)Math.Round((end - begin).TotalMinutes) : null;
            result.Add(new(minutes == 10080 ? "每周额度" : "账期额度", Math.Clamp(used, 0, 100), reset, minutes));
            return result;
        }
        foreach (var (key, label) in new[] { ("autoPercentUsed", "Cursor 模型"), ("apiPercentUsed", "其他模型") })
            if (plan.Get(key).Number() is { } percent) result.Add(new(label, Math.Clamp(percent, 0, 100), reset));
        if (result.Count == 0 && plan.Get("totalPercentUsed").Number() is { } total) result.Add(new("账期额度", Math.Clamp(total, 0, 100), reset));
        return result;
    }
    // GetSandUsageStatus reports one weekly figure for the Grok Bot / Cursor account: how much of the included
    // allowance is used (0-100) and when it resets. usagePercent shares the same scale as the Cursor quotas above.
    // isUnified marks a plan merged with Grok, where this figure is the same allowance as the Cursor quota.
    public static Quota? SandUsage(JsonElement root)
    {
        if (root.Get("usagePercent").Number() is not { } percent) return null;
        return new("Grok Bot", Math.Clamp(percent, 0, 100), root.Get("nextResetTimestampUtc").Date());
    }
    public static List<Quota> Antigravity(JsonElement root)
    {
        var result = new List<Quota>();
        var groups = root.Get("response").Get("groups");
        if (groups.ValueKind != JsonValueKind.Array) groups = root.Get("groups");
        foreach (var group in groups.Items())
            foreach (var bucket in group.Get("buckets").Items())
                if (bucket.Get("remainingFraction").Number() is { } fraction && bucket.Get("disabled").ValueKind != JsonValueKind.True)
                {
                    var (label, minutes) = AntigravityBucket(bucket, group.Get("displayName").Text());
                    result.Add(new(label, (1 - Math.Clamp(fraction, 0, 1)) * 100, bucket.Get("resetTime").Date(), minutes));
                }
        if (result.Count > 0) return result;
        var configs = root.Get("userStatus").Get("cascadeModelConfigData").Get("clientModelConfigs");
        foreach (var model in configs.Items())
            if (model.Get("quotaInfo").Get("remainingFraction").Number() is { } fraction)
                result.Add(new(model.Get("label").Text(), (1 - Math.Clamp(fraction, 0, 1)) * 100, model.Get("quotaInfo").Get("resetTime").Date()));
        return result;
    }

    // A quota summary bucket carries an id ("gemini-weekly", "3p-5h"), a window ("weekly", "5h") and sits in a group named by the
    // service ("Gemini Models", "Claude and GPT models"). Google AI Pro adds a weekly-only Gemini 4 Argon quota whose id has not
    // been seen, so the window decides the period and an id naming Argon gets a group of its own. Other unknown buckets are
    // grouped by the service's group name.
    private static (string Label, int? Minutes) AntigravityBucket(JsonElement bucket, string groupName)
    {
        var id = bucket.Get("bucketId").Text();
        var window = bucket.Get("window").Text().ToLowerInvariant();
        if (window.Length == 0) window = id.EndsWith("-5h", StringComparison.OrdinalIgnoreCase) ? "5h" : id.EndsWith("-weekly", StringComparison.OrdinalIgnoreCase) ? "weekly" : "";
        var (period, minutes) = window switch
        {
            "5h" => ("5 小时", (int?)300),
            "daily" or "1d" => ("每日", 1440),
            "weekly" or "7d" => ("每周", 10080),
            _ => (bucket.Get("displayName").Text() is { Length: > 0 } name ? name : id, (int?)null)
        };
        var group = id.Contains("argon", StringComparison.OrdinalIgnoreCase) ? "Argon"
            : id.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) ? "Gemini"
            : id.StartsWith("3p-", StringComparison.OrdinalIgnoreCase) ? "Claude"
            : groupName.Replace('·', ' ').Trim();
        return (group.Length > 0 ? $"{group} · {period.Replace('·', ' ').Trim()}" : period, minutes);
    }
}

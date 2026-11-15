using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core;

// The last readings of each usage source, kept on disk so that a start shows them at once (marked as the previous run's) instead
// of an empty page, until each source's own reading of this run replaces its own.
//
// Only what the panels already show is kept: the quota metrics (shares, amounts, reset times), the plan, and the token totals by day
// and model that the totals and costs are worked out from. Never a token, a key, an account (id, e-mail or hash), a path, the text
// of a session, a record's key, or any text the source wrote (status, notes, details); custom and hand-written sources keep nothing,
// as their scripts decide what their metrics say. A reading older than MaxAge, or one of a source that is no longer enabled, is not
// shown. A file of another version, a damaged one, or one over MaxFileBytes holds nothing; deleting it is always safe.
public sealed class UsageLastSeen(string path, Func<DateTimeOffset>? clock = null)
{
    public const string FileName = "usage-last-seen.json";
    public const int Version = 1;
    public const int MaxFileBytes = 512 * 1024;
    // A reading older than this is not shown as the last one: it says too little about now.
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    // The file is written again for the same values (apart from the time) only when the last write is this old.
    public static readonly TimeSpan RewriteAfter = TimeSpan.FromMinutes(10);
    // MaxGroups bounds the token totals of one source (a group is one model on one day), so that several sources fit in MaxFileBytes.
    // Whole days are kept, newest first; the start of the totals then moves to the first day kept, so a range reaching further back
    // shows them as incomplete (ModelUsage) instead of counting less.
    private const int MaxTextLength = 200, MaxMetrics = 100, MaxGroups = 400;
    // A restored reading, and its totals, are drawn at this opacity until the new one replaces it.
    public const double Dimmed = .55;

    private sealed record StoredMetric(MetricKind Kind, string Label, string? Id, int? Window, double? Percent, decimal? Amount, decimal? Total, decimal? Used,
        string? Currency, string? Unit, bool Unlimited, DateTimeOffset? ResetAt, DateTimeOffset? ExpiresAt, bool AtLeast, bool TotalIsTarget, CreditPeriod? Period);
    private sealed record StoredDay(DateTime Day, string Model, string? PriceModel, long Input, long CacheRead, long CacheWrite, long CacheWriteHour, long Output, decimal? Cost, bool Fitted);
    private sealed record StoredProvider(ProviderId Id, List<string>? Configurations, DateTimeOffset ReadAt, string? Plan, List<StoredMetric>? Metrics,
        bool UsageAvailable, bool UsageComplete, DateTime? UsageStart, List<StoredDay>? Days)
    {
        // When the quotas were read, where that is not when the rest was (a quota that failed this round keeps its earlier reading); null: ReadAt.
        public DateTimeOffset? QuotaAt { get; init; }
    }
    private sealed record StoredFile(int Version, List<StoredProvider?>? Providers);
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private readonly object _sync = new();
    // What the file holds now (or would, after the last write that failed): the base the next write is merged into.
    private List<StoredProvider> _current = [];
    private string _written = "";
    private DateTimeOffset _writtenAt;

    // Whether a source's snapshot is kept: the built-in and statistics sources, not the custom ones.
    private static bool Persistable(ProviderId id) => ProviderCatalog.IsBuiltIn(id) || ProviderCatalog.IsUsageSource(id);
    private static bool Local(IEnumerable<string>? configurations) => configurations?.Contains(ProviderCatalog.LocalQuota) == true;
    private static (ProviderId, bool) KeyOf(StoredProvider stored) => (stored.Id, Local(stored.Configurations));
    private static (ProviderId, bool) KeyOf(ProviderSnapshot snapshot) => (snapshot.Id, Local(snapshot.Configurations));

    // The snapshots the file holds that are still worth showing for these settings, marked as the previous run's (LastSeenAt).
    public List<ProviderSnapshot> Load(DeckSettings settings)
    {
        var now = _clock();
        lock (_sync)
        {
            var kept = Read(now).Select(stored => Live(stored, now)).OfType<StoredProvider>().Where(stored => Relevant(settings, stored)).ToList();
            // The first round of a run always writes, so the read time of what was read again moves on even when the values are the same.
            _current = kept; _written = Serialize(kept, true); _writtenAt = default;
            return kept.Select(stored => Restore(stored, now)).ToList();
        }
    }

    // The first round's snapshots with the previous run's in place of the sources that have not answered yet: a source with a snapshot
    // of its own (whatever it says) shows that one.
    public static IReadOnlyList<ProviderSnapshot> Overlay(IReadOnlyList<ProviderSnapshot> fresh, IReadOnlyList<ProviderSnapshot> restored)
    {
        if (restored.Count == 0) return fresh;
        var have = fresh.Select(KeyOf).ToHashSet();
        return [.. fresh, .. restored.Where(snapshot => !have.Contains(KeyOf(snapshot)))];
    }

    // What the panels show for a round: the sources read so far with the previous run's for the others, and once the round is whole
    // (a source that did not answer is then simply missing) only what was read.
    public static IReadOnlyList<ProviderSnapshot> Display(IReadOnlyList<ProviderSnapshot> round, IReadOnlyList<ProviderSnapshot> restored, bool whole)
        => whole ? round : Overlay(round, restored);

    // Forgets everything kept, on disk too (the usage is no longer wanted).
    public void Clear()
    {
        lock (_sync)
        {
            _current = []; _written = ""; _writtenAt = default;
            try { File.Delete(path); File.Delete(path + ".tmp"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // The marker's text for a reading taken at `read`, worked out when it is drawn so that it follows the day and the language.
    public static string Label(DateTimeOffset read, DateTimeOffset now) => Loc.T($"上次读取 {Clock(read, now)}", $"Last read {Clock(read, now)}");

    // Keeps the readings of a finished round for the next start. A source that read nothing this time keeps its earlier reading
    // (until MaxAge), a source that is no longer enabled keeps none. Returns whether the file was written; a failed write leaves the
    // file as it was.
    public bool Save(IReadOnlyList<ProviderSnapshot> round, DeckSettings settings)
    {
        var now = _clock();
        lock (_sync)
        {
            string same, text;
            List<StoredProvider> all;
            try
            {
                var fresh = round.Select(snapshot => Slim(snapshot, now)).OfType<StoredProvider>().Where(stored => Relevant(settings, stored)).ToList();
                // A part that was not read this round (the quotas failed while the local records were read, or the other way round)
                // keeps its earlier reading, with that reading's own time.
                fresh = [.. fresh.Select(stored => _current.FirstOrDefault(old => KeyOf(old) == KeyOf(stored)) is { } old ? Merge(stored, old, now) : stored)];
                var have = fresh.Select(KeyOf).ToHashSet();
                all = new List<StoredProvider>(fresh);
                all.AddRange(_current.Where(old => !have.Contains(KeyOf(old)) && Relevant(settings, old)).Select(old => Live(old, now)).OfType<StoredProvider>());
                same = Serialize(all, true);
                text = Serialize(all, false);
                // Over the size limit the token totals go first, and the sources then show no totals rather than zero ones.
                if (text.Length > MaxFileBytes) text = Serialize([.. all.Select(stored => stored with { Days = [], UsageAvailable = false })], false);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
            _current = all;
            // Nothing worth keeping (every source disabled or never read): no file rather than an empty one.
            if (all.Count == 0)
            {
                try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                _written = ""; return false;
            }
            if (same == _written && now >= _writtenAt && now - _writtenAt < RewriteAfter) return false;
            if (text.Length > MaxFileBytes) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, text);
                File.Move(temporary, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            _written = same; _writtenAt = now;
            return true;
        }
    }

    private static StoredProvider Merge(StoredProvider fresh, StoredProvider old, DateTimeOffset now)
    {
        var merged = fresh;
        var quotaAt = old.QuotaAt ?? old.ReadAt;
        if ((fresh.Metrics ?? []).Count == 0 && (old.Metrics ?? []).Count > 0 && InRange(quotaAt, now))
            merged = merged with { Metrics = old.Metrics, Plan = string.IsNullOrEmpty(fresh.Plan) ? old.Plan : fresh.Plan, QuotaAt = quotaAt };
        if (!fresh.UsageAvailable && old.UsageAvailable && InRange(old.ReadAt, now))
            merged = merged with { UsageAvailable = true, UsageComplete = old.UsageComplete, UsageStart = old.UsageStart, Days = old.Days, ReadAt = old.ReadAt };
        return merged;
    }

    // A quota whose window has reset since says nothing about now (its share starts again), so it is left out, as ClaudeQuotaCache does;
    // a source left with neither quotas nor token totals is not shown.
    private static StoredProvider? Live(StoredProvider stored, DateTimeOffset now)
    {
        // Each part is judged by its own time: quotas (QuotaAt) or totals (ReadAt) read longer ago than MaxAge are left out, and the
        // other part stays when it is newer.
        var metrics = !InRange(stored.QuotaAt ?? stored.ReadAt, now) ? []
            : (stored.Metrics ?? []).Where(metric => metric is not null && (metric.ResetAt is not { } reset || reset > now)).ToList();
        bool usage = stored.UsageAvailable && InRange(stored.ReadAt, now);
        if (metrics.Count == 0 && !usage) return null;
        return usage ? stored with { Metrics = metrics } : stored with { Metrics = metrics, UsageAvailable = false, UsageStart = null, Days = [] };
    }

    private static bool Relevant(DeckSettings settings, StoredProvider stored) => settings.RequiredProviders.Contains(stored.Id);
    private static bool InRange(DateTimeOffset at, DateTimeOffset now) => at <= now.AddMinutes(5) && now - at <= MaxAge;
    // A source is worth reading while either part is recent enough; Live then leaves out the part that is not.
    private static bool Fresh(StoredProvider stored, DateTimeOffset now) => InRange(stored.ReadAt, now) || InRange(stored.QuotaAt ?? stored.ReadAt, now);

    // The text of a set of readings; sameValues leaves out the time they were read, to tell a changed reading from a later one.
    private static string Serialize(List<StoredProvider> providers, bool sameValues)
        => JsonSerializer.Serialize(new StoredFile(Version, sameValues ? [.. providers.Select(stored => stored with { ReadAt = default, QuotaAt = null })] : [.. providers]), Options);

    private List<StoredProvider> Read(DateTimeOffset now)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return [];
            var file = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path), Options);
            if (file is null || file.Version != Version || file.Providers is null) return [];
            var seen = new HashSet<(ProviderId, bool)>();
            return [.. file.Providers.OfType<StoredProvider>().Where(stored => Persistable(stored.Id) && Fresh(stored, now) && seen.Add(KeyOf(stored)))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }

    private static string Text(string? text) => text is null ? "" : text.Length > MaxTextLength ? text[..MaxTextLength] : text;

    private static StoredProvider? Slim(ProviderSnapshot snapshot, DateTimeOffset now)
    {
        // A reading that is itself the previous run's is not a reading of this one.
        if (snapshot.LastSeenAt is not null || !Persistable(snapshot.Id)) return null;
        var metrics = snapshot.Metrics.Where(metric => metric.Kind != MetricKind.Text && metric.Label.Length > 0 && (metric.Percent is not { } percent || double.IsFinite(percent))).Take(MaxMetrics).Select(metric => new StoredMetric(
            metric.Kind, Text(metric.Label), Text(metric.Id), metric.Window, metric.Percent, metric.Amount, metric.Total, metric.Used, Text(metric.Currency), Text(metric.Unit),
            metric.Unlimited, metric.ResetAt, metric.ExpiresAt, metric.AtLeast, metric.TotalIsTarget, metric.Period)).ToList();
        if (metrics.Count == 0 && !snapshot.UsageAvailable) return null;
        var groups = snapshot.Entries.GroupBy(entry => (Day: entry.Time.LocalDateTime.Date, entry.Model, entry.PriceModel, entry.FittedCost, Reported: entry.ReportedCostUsd is not null))
            .Select(group => new StoredDay(group.Key.Day, Text(group.Key.Model), group.Key.PriceModel is null ? null : Text(group.Key.PriceModel), group.Sum(entry => entry.Input), group.Sum(entry => entry.CacheRead),
                group.Sum(entry => entry.CacheWrite), group.Sum(entry => entry.CacheWriteHour), group.Sum(entry => entry.Output),
                group.Key.Reported ? group.Sum(entry => entry.ReportedCostUsd ?? 0) : null, group.Key.FittedCost))
            .GroupBy(day => day.Day).OrderByDescending(day => day.Key);
        // Whole days, newest first, up to MaxGroups; a day that does not fit and every earlier one are left out, and the totals then
        // start the day after it. A single day over the limit leaves the source with no totals rather than a part of that day's.
        var days = new List<StoredDay>();
        bool usage = snapshot.UsageAvailable;
        var usageStart = snapshot.UsageStart;
        foreach (var day in groups)
        {
            if (days.Count + day.Count() > MaxGroups)
            {
                if (days.Count == 0) usage = false;
                else usageStart = day.Key.AddDays(1);
                break;
            }
            days.AddRange(day);
        }
        if (!usage) { days = []; usageStart = null; if (metrics.Count == 0) return null; }
        // The plan comes with the quotas; the time is when they were read (a quota kept from earlier keeps its own time).
        var quotaAt = snapshot.IsStale && snapshot.QuotaTime is { } quotaTime ? quotaTime : now;
        return new StoredProvider(snapshot.Id, Local(snapshot.Configurations) ? [ProviderCatalog.LocalQuota] : [], usage ? now : quotaAt, Text(snapshot.Plan), metrics,
            usage, snapshot.UsageComplete, usageStart, days) { QuotaAt = quotaAt };
    }

    private static ProviderSnapshot Restore(StoredProvider stored, DateTimeOffset now)
    {
        var read = stored.ReadAt;
        // The marker names the quotas' time when there are quotas, the totals' time otherwise.
        var shown = stored.Metrics is { Count: > 0 } ? stored.QuotaAt ?? read : read;
        var label = Label(shown, now);
        var metrics = (stored.Metrics ?? []).OfType<StoredMetric>().Where(metric => !string.IsNullOrWhiteSpace(metric.Label)
            && (metric.Percent is not { } percent || double.IsFinite(percent))).Take(MaxMetrics).Select(metric => new UsageMetric(metric.Kind == MetricKind.Text ? MetricKind.Percent : metric.Kind, Text(metric.Label))
            {
                Id = Text(metric.Id), Window = metric.Window, Percent = metric.Percent, Amount = metric.Amount, Total = metric.Total, Used = metric.Used,
                Currency = Text(metric.Currency), Unit = Text(metric.Unit), Unlimited = metric.Unlimited, ResetAt = metric.ResetAt, ExpiresAt = metric.ExpiresAt,
                AtLeast = metric.AtLeast, TotalIsTarget = metric.TotalIsTarget, Period = metric.Period
            }).ToList();
        var entries = (stored.Days ?? []).OfType<StoredDay>().Where(day => day.Input >= 0 && day.CacheRead >= 0 && day.CacheWrite >= 0 && day.CacheWriteHour >= 0 && day.Output >= 0)
            .Select(day => new TokenEntry("last-seen", new DateTimeOffset(DateTime.SpecifyKind(day.Day.Date, DateTimeKind.Local)),
                Text(day.Model), day.Input, day.CacheRead, day.CacheWrite, day.CacheWriteHour, day.Output, day.Cost, day.Fitted) { PriceModel = day.PriceModel }).ToList();
        return new ProviderSnapshot(stored.Id)
        {
            Configurations = [.. stored.Configurations ?? []],
            Metrics = metrics, Quotas = [], Plan = Text(stored.Plan), Entries = entries,
            UsageAvailable = stored.UsageAvailable, UsageComplete = stored.UsageComplete, UsageStart = stored.UsageStart,
            LastSeenAt = shown, IsStale = true, LiveQuota = false, QuotaTime = stored.QuotaAt ?? read,
            StatusLabel = label, Status = Loc.T($"显示 {Clock(shown, now)} 读取的数据，新数据到达后会替换。", $"Showing the data read at {Clock(shown, now)}. New data replaces it when it arrives."),
            UsageNote = stored.UsageAvailable ? Loc.T($"显示 {Clock(read, now)} 读取的数据。", $"Showing the data read at {Clock(read, now)}.") : ""
        };
    }

    // How a time of day reads in the marker: the clock for today, the date too for an earlier day.
    public static string Clock(DateTimeOffset time, DateTimeOffset now)
        => time.LocalDateTime.ToString(time.LocalDateTime.Date == now.LocalDateTime.Date ? "HH:mm" : "MM-dd HH:mm");
}

// The usage levels that were reached (AppPresets.Level) for each quota of each application, to tell a rise from a first look. Only a
// fresh reading counts: one of an earlier run (ProviderSnapshot.LastSeenAt) or kept from before (IsStale) neither seeds a level nor
// rises past one, so the first fresh reading after a start never alerts only because it differs from the one shown at the start.
public sealed class QuotaLevels
{
    public sealed record Rise(AppEntry Entry, UsageMetric Metric, double Used);
    private readonly Dictionary<string, int> _levels = [];
    private bool _seeded;

    // The quota with the highest used share among those that rose to a higher level since the last call, or null. The first call that
    // sees any fresh reading only records the levels.
    public Rise? Update(DeckSettings settings, IReadOnlyList<ProviderSnapshot> snapshots)
    {
        Rise? risen = null;
        bool fresh = false;
        foreach (var entry in settings.EnabledApps)
        {
            var snapshot = DashboardUsage.Quota(entry, snapshots);
            if (snapshot.IsStale || snapshot.LastSeenAt is not null || !snapshot.LiveQuota) continue;
            foreach (var quota in snapshot.Metrics)
            {
                if (quota.UsedPercent is not { } used) continue;
                fresh = true;
                var key = $"{entry.InstanceId}:{entry.ConfigurationKey}:{quota.Id}";
                int level = AppPresets.Level(used);
                bool rose = _seeded && _levels.TryGetValue(key, out var previous) && level > previous;
                _levels[key] = level;
                if (rose && level > 0 && (risen is null || used > risen.Used)) risen = new Rise(entry, quota, used);
            }
        }
        if (fresh) _seeded = true;
        return risen;
    }
}

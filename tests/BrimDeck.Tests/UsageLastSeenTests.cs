using BrimDeck.Core;

// What a start shows from the previous run: the readings that are kept (and the ones that must not be), what is dropped as too old
// or no longer enabled, how a damaged file is met, and that nothing of it can raise a level notice.
static class UsageLastSeenTests
{
    const string SecretKey = "SENTINEL-RECORD-KEY-8812", SecretMail = "SENTINEL-someone@example.com", SecretSource = "SENTINEL-SOURCE-TEXT", SecretNote = "SENTINEL-NOTE-TEXT",
        SecretToken = "SENTINEL-sk-ant-oat01-secret", SecretPath = @"SENTINEL-C:\Users\someone\projects", SecretValue = "SENTINEL-SCRIPT-VALUE";

    static DeckSettings Enabled(params ProviderId[] ids)
    {
        var settings = new DeckSettings { Apps = ids.Select(id => new AppEntry { QuotaSource = id, UsageSource = id }).ToList() };
        settings.Normalize();
        return settings;
    }

    static ProviderSnapshot Read(ProviderId id, DateTimeOffset now, double percent = 40, string model = "test-model", long input = 100)
    {
        var snapshot = new ProviderSnapshot(id)
        {
            LiveQuota = true, Plan = "Max 5x", Source = SecretSource, Scope = SecretPath, PlanSource = SecretNote, UsageNote = SecretNote,
            Status = SecretToken, StatusLabel = SecretMail, UsageAvailable = true, UsageStart = now.LocalDateTime.Date.AddDays(-29),
            Details = new() { ["account"] = SecretMail, ["token"] = SecretToken },
            Metrics =
            [
                new UsageMetric(MetricKind.Percent, "5 小时") { Id = "five", Window = 300, Percent = percent, ResetAt = now.AddHours(2), Note = SecretNote },
                new UsageMetric(MetricKind.Text, "script title") { Id = "0", Percent = 10, Value = SecretValue, Note = SecretNote }
            ],
            Entries =
            [
                new(SecretKey + "1", now.AddHours(-2), model, input, 10, 5, 0, 20, .5m),
                new(SecretKey + "2", now.AddHours(-3), model, 50, 0, 0, 0, 5, .25m),
                new(SecretKey + "3", now.AddDays(-2), "other-model", 7, 0, 0, 0, 3),
            ]
        };
        return snapshot;
    }

    public static void Run(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "usage-last-seen"); Directory.CreateDirectory(folder);
        // A fixed time of day (3 pm today), so no check depends on when it runs.
        var now = new DateTimeOffset(DateTime.Today.AddHours(15));
        string File(string name) => Path.Combine(folder, name);
        var settings = Enabled(ProviderId.Claude, ProviderId.Codex);

        // (a) The round trip keeps what the panels show and nothing that must not be kept.
        {
            var path = File("round-trip.json");
            var store = new UsageLastSeen(path, () => now);
            bool written = store.Save([Read(ProviderId.Claude, now)], settings);
            var text = System.IO.File.ReadAllText(path);
            check("A finished round is written to the file", written && text.Length > 0);
            check("Nothing secret-like or free-form reaches the file (keys, mails, tokens, paths, notes, source text, script values)",
                new[] { SecretKey, SecretMail, SecretSource, SecretNote, SecretToken, SecretPath, SecretValue, "script title" }.All(sentinel => !text.Contains(sentinel)));
            var loaded = new UsageLastSeen(path, () => now).Load(settings);
            var claude = loaded.Single();
            check("The quota, plan and read time come back", claude.Id == ProviderId.Claude && claude.Plan == "Max 5x" && claude.Metrics.Count == 1
                && claude.Metrics[0] is { Id: "five", Window: 300, Percent: 40 } && claude.Metrics[0].ResetAt is not null && claude.LastSeenAt == now);
            check("A restored snapshot is marked stale, not live, with the read time in its label",
                claude is { IsStale: true, LiveQuota: false } && claude.QuotaTime == now && claude.StatusLabel.Contains(now.LocalDateTime.ToString("HH:mm")));
            check("Token totals come back by day and model, with their sums and reported cost",
                claude.UsageAvailable && claude.Entries.Count == 2
                && claude.Entries.Where(e => e.Model == "test-model").Sum(e => e.Total) == 100 + 10 + 5 + 20 + 50 + 5
                && claude.Entries.Where(e => e.Model == "test-model").Sum(e => e.ReportedCostUsd ?? 0) == .75m
                && claude.Entries.Where(e => e.Model == "other-model").Sum(e => e.Total) == 10
                && claude.Entries.All(e => e.Key == "last-seen" && e.Time.LocalDateTime == e.Time.LocalDateTime.Date));
            check("The statistics of a restored snapshot count today's records as today's", claude.Entries.Where(e => e.Time.LocalDateTime.Date == now.LocalDateTime.Date).Sum(e => e.Total) == 190);
        }

        // (b) A custom source and a source that is not enabled keep nothing; a restored snapshot is not kept as a reading.
        {
            var path = File("filter.json");
            var store = new UsageLastSeen(path, () => now);
            var custom = Read(ProviderId.Custom, now); custom.Configurations = ["ABCDEF"];
            var restored = Read(ProviderId.Codex, now); restored.LastSeenAt = now;
            store.Save([custom, Read(ProviderId.Claude, now), restored, Read(ProviderId.Codex, now, 10)], Enabled(ProviderId.Claude, ProviderId.Custom));
            var loaded = new UsageLastSeen(path, () => now).Load(Enabled(ProviderId.Claude, ProviderId.Codex, ProviderId.Custom));
            check("Custom sources, sources that were not enabled and restored snapshots are not written", loaded.Select(s => s.Id).SequenceEqual([ProviderId.Claude]));
        }

        // (c) A source that is no longer enabled is not shown, however recent its reading.
        {
            var path = File("disabled.json");
            new UsageLastSeen(path, () => now).Save([Read(ProviderId.Claude, now), Read(ProviderId.Codex, now)], settings);
            var shown = new UsageLastSeen(path, () => now).Load(Enabled(ProviderId.Claude));
            check("A source disabled since the last run is not shown", shown.Select(s => s.Id).SequenceEqual([ProviderId.Claude]));
            check("Nothing is shown when no source is enabled", new UsageLastSeen(path, () => now).Load(new DeckSettings { Apps = [] }).Count == 0);
            var hidden = Enabled(ProviderId.Claude); hidden.UsagePage = false; hidden.Style = CompactStyle.Notch; hidden.NotchSummary = false;
            check("Nothing is shown when the usage is not wanted anywhere", !hidden.QuotaWanted && new UsageLastSeen(path, () => now).Load(hidden).Count == 0);
        }

        // (d) A reading older than the cap is dropped; the cap is on the time it was read.
        {
            var path = File("age.json");
            new UsageLastSeen(path, () => now).Save([Read(ProviderId.Claude, now)], settings);
            check("A reading within the cap is shown", new UsageLastSeen(path, () => now.AddHours(23)).Load(settings).Count == 1);
            check("A reading older than the cap is not shown", new UsageLastSeen(path, () => now.AddHours(25)).Load(settings).Count == 0);
            check("A reading from the future (a clock set back) is not shown", new UsageLastSeen(path, () => now.AddHours(-2)).Load(settings).Count == 0);
            var kept = Read(ProviderId.Claude, now); kept.IsStale = true; kept.QuotaTime = now.AddHours(-30);
            var oldPath = File("age-kept.json");
            new UsageLastSeen(oldPath, () => now).Save([kept], settings);
            check("A quota kept from long before is dropped by the cap; the newer totals beside it stay", new UsageLastSeen(oldPath, () => now).Load(settings) is [{ Metrics.Count: 0, UsageAvailable: true }]);
            // The other way round: totals that kept failing grow old while the quota beside them is read again.
            var partsPath = File("age-parts.json");
            var clock = now;
            var store = new UsageLastSeen(partsPath, () => clock);
            store.Save([Read(ProviderId.Claude, now)], settings);
            clock = now.AddHours(23);
            var quotaOnly = Read(ProviderId.Claude, clock, 60); quotaOnly.UsageAvailable = false; quotaOnly.Entries = [];
            store.Save([quotaOnly], settings);
            var shown = new UsageLastSeen(partsPath, () => now.AddHours(24.5)).Load(settings);
            check("Totals older than the cap do not take a newer quota with them", shown is [{ Metrics.Count: 1, UsageAvailable: false, Entries.Count: 0 }]
                && shown[0].Metrics[0].Percent == 60 && shown[0].LastSeenAt == now.AddHours(23) && shown[0].UsageNote == "");
            check("A source whose quota and totals are both past the cap is not shown", new UsageLastSeen(partsPath, () => now.AddHours(47.5)).Load(settings).Count == 0);
        }

        // (d2) The totals of one source are kept by whole days up to the group limit, and then start where they were cut.
        {
            var path = File("groups.json");
            var many = Read(ProviderId.Claude, now);
            // 50 models a day over 10 days: 500 groups, of which 8 whole days (400) fit.
            many.Entries = [.. Enumerable.Range(0, 10).SelectMany(day => Enumerable.Range(0, 50).Select(model => new TokenEntry("k", now.AddDays(-day), "m" + model, 10, 0, 0, 0, 1)))];
            many.UsageStart = now.LocalDateTime.Date.AddDays(-29);
            new UsageLastSeen(path, () => now).Save([many], settings);
            var shown = new UsageLastSeen(path, () => now).Load(settings).Single();
            var start = now.LocalDateTime.Date.AddDays(-7);
            check("Whole days are kept, newest first, up to the group limit", shown.UsageAvailable && shown.Entries.Count == 400
                && shown.Entries.Min(e => e.Time.LocalDateTime.Date) == start && shown.Entries.Sum(e => e.Total) == 400 * 11);
            check("The totals then start at the first day kept", shown.UsageStart == start);
            var month = ModelUsage.Filter([shown], new HashSet<ProviderId> { ProviderId.Claude }, UsageDateRange.Recent(30, now.LocalDateTime.Date));
            var week = ModelUsage.Filter([shown], new HashSet<ProviderId> { ProviderId.Claude }, UsageDateRange.Recent(7, now.LocalDateTime.Date));
            check("A range reaching before the cut shows the source as incomplete, one within it does not",
                month.IncompleteProviders.Contains(ProviderId.Claude) && !week.IncompleteProviders.Contains(ProviderId.Claude));
            var crowded = Read(ProviderId.Claude, now);
            crowded.Entries = [.. Enumerable.Range(0, 450).Select(model => new TokenEntry("k", now, "m" + model, 10, 0, 0, 0, 1))];
            var crowdedPath = File("groups-day.json");
            new UsageLastSeen(crowdedPath, () => now).Save([crowded], settings);
            check("A single day over the limit leaves no totals rather than part of them, and keeps the quota",
                new UsageLastSeen(crowdedPath, () => now).Load(settings) is [{ UsageAvailable: false, Entries.Count: 0, Metrics.Count: 1 }]);
        }

        // A quota whose window has reset since is left out; a source left with nothing is not shown.
        {
            var path = File("reset.json");
            var quotaOnly = Read(ProviderId.Codex, now); quotaOnly.UsageAvailable = false; quotaOnly.Entries = [];
            new UsageLastSeen(path, () => now).Save([Read(ProviderId.Claude, now), quotaOnly], settings);
            var later = new UsageLastSeen(path, () => now.AddHours(3)).Load(settings);
            check("A quota whose window has reset since is left out, the token totals stay", later.Count == 1 && later[0].Id == ProviderId.Claude && later[0].Metrics.Count == 0 && later[0].Entries.Count > 0);
            check("A source left with no quota and no totals is not shown", !later.Any(s => s.Id == ProviderId.Codex));
            check("Before the reset the quota is shown", new UsageLastSeen(path, () => now.AddHours(1)).Load(settings).All(s => s.Metrics.Count == 1));
        }

        // (e) Damaged, other-version and oversize files hold nothing and do no harm; a missing file is no error.
        {
            var good = File("good.json");
            new UsageLastSeen(good, () => now).Save([Read(ProviderId.Claude, now)], settings);
            var text = System.IO.File.ReadAllText(good);
            List<ProviderSnapshot> LoadText(string name, string content) { var p = File(name); System.IO.File.WriteAllText(p, content); return new UsageLastSeen(p, () => now).Load(settings); }
            check("A damaged file holds nothing", LoadText("corrupt.json", text[..(text.Length / 2)]).Count == 0 && LoadText("garbage.json", "\0\0not json").Count == 0);
            check("A file of another version holds nothing", LoadText("version.json", text.Replace("\"Version\":1", "\"Version\":2")).Count == 0);
            check("A file without a version holds nothing", LoadText("noversion.json", "{\"Providers\":[]}").Count == 0 && LoadText("array.json", "[1,2,3]").Count == 0);
            check("An unknown source name holds nothing instead of failing", LoadText("unknown.json", text.Replace("\"Claude\"", "\"NoSuchSource\"")).Count == 0);
            check("An oversize file is not read", LoadText("big.json", text + new string(' ', UsageLastSeen.MaxFileBytes)).Count == 0);
            check("A file whose entries are damaged keeps the good ones", LoadText("holes.json", text.Replace("\"Providers\":[", "\"Providers\":[null,")).Count == 1);
            check("A missing file holds nothing", new UsageLastSeen(File("missing.json"), () => now).Load(settings).Count == 0);
            check("A deleted file is simply not there", Delete(good) && new UsageLastSeen(good, () => now).Load(settings).Count == 0);
            static bool Delete(string p) { System.IO.File.Delete(p); return true; }
        }

        // (f) The write is atomic: when it fails, the earlier file stays whole.
        {
            var path = File("atomic.json");
            var store = new UsageLastSeen(path, () => now);
            store.Save([Read(ProviderId.Claude, now, 40)], settings);
            var before = System.IO.File.ReadAllText(path);
            // A directory where the temporary file goes makes the write fail.
            Directory.CreateDirectory(path + ".tmp");
            bool written = store.Save([Read(ProviderId.Claude, now, 90)], settings);
            check("A write that fails reports it and leaves the earlier file as it was", !written && System.IO.File.ReadAllText(path) == before);
            check("The earlier file still loads", new UsageLastSeen(path, () => now).Load(settings).Single().Metrics[0].Percent == 40);
            Directory.Delete(path + ".tmp");
            check("The next round writes it", store.Save([Read(ProviderId.Claude, now, 90)], settings)
                && new UsageLastSeen(path, () => now).Load(settings).Single().Metrics[0].Percent == 90 && !System.IO.File.Exists(path + ".tmp"));
        }

        // (g) Writes are throttled to one a round, and only for changed values or an old file; a failed read keeps the earlier reading.
        {
            var path = File("throttle.json");
            var clock = now;
            var store = new UsageLastSeen(path, () => clock);
            check("The first round is written", store.Save([Read(ProviderId.Claude, now, 40)], settings));
            clock = now.AddMinutes(1);
            check("The same values a minute later are not written again", !store.Save([Read(ProviderId.Claude, now, 40)], settings));
            check("Changed values are written", store.Save([Read(ProviderId.Claude, now, 41)], settings));
            clock = now.AddMinutes(13);
            check("The same values after the rewrite interval refresh the read time", store.Save([Read(ProviderId.Claude, now, 41)], settings)
                && new UsageLastSeen(path, () => clock).Load(settings).Single().LastSeenAt == clock);
            clock = now.AddMinutes(14);
            var failed = new ProviderSnapshot(ProviderId.Claude) { StatusLabel = "读取失败", Status = "读取失败" };
            store.Save([failed, Read(ProviderId.Codex, now)], settings);
            var loaded = new UsageLastSeen(path, () => clock).Load(settings);
            check("A source that read nothing keeps its earlier reading, with its earlier time",
                loaded.Single(s => s.Id == ProviderId.Claude) is { Metrics.Count: 1 } kept && kept.LastSeenAt == now.AddMinutes(13) && loaded.Any(s => s.Id == ProviderId.Codex));
        }

        // (h) The file stays within its size limit: the token totals go first, the quotas stay.
        {
            ProviderId[] many = [ProviderId.Claude, ProviderId.Codex, ProviderId.Cursor, ProviderId.Antigravity, ProviderId.Kimi, ProviderId.OpenCode, ProviderId.ZCode, ProviderId.Dsh, ProviderId.CodeAgent, ProviderId.WorkBuddy, ProviderId.MiMo];
            var all = Enabled(many);
            var path = File("size.json");
            var round = many.Select(id =>
            {
                var snapshot = Read(id, now);
                snapshot.Entries = [.. Enumerable.Range(0, 2000).Select(i => new TokenEntry("k", now.AddDays(-(i % 30)), "model-with-a-long-name-" + new string('x', 60) + id + "-" + i, 1000 + i, 1, 1, 0, 1))];
                return snapshot;
            }).ToList();
            bool written = new UsageLastSeen(path, () => now).Save(round, all);
            var size = new FileInfo(path).Length;
            var loaded = new UsageLastSeen(path, () => now).Load(all);
            check("Trimmed sources show no totals at all instead of zero ones", loaded.All(s => !s.UsageAvailable && s.Entries.Count == 0));
            check("An oversize round is kept within the limit with its quotas", written && size <= UsageLastSeen.MaxFileBytes && loaded.Count == all.RequiredProviders.Count && loaded.Count >= 5 && loaded.All(s => s.Metrics.Count == 1));
        }

        // (g2) A part that was not read this round keeps its earlier reading, with that reading's own time.
        {
            var path = File("parts.json");
            var clock = now;
            var store = new UsageLastSeen(path, () => clock);
            store.Save([Read(ProviderId.Claude, now, 40)], settings);
            clock = now.AddHours(1);
            // The records were read, the quota request stalled: a snapshot with totals and no quota.
            var logsOnly = new ProviderSnapshot(ProviderId.Claude) { UsageAvailable = true, Entries = [new("k", clock, "newer-model", 1000, 0, 0, 0, 1)], StatusLabel = "读取超时", Status = "读取超时" };
            store.Save([logsOnly], settings);
            var shown = new UsageLastSeen(path, () => clock).Load(settings).Single();
            check("Totals read while the quota failed keep the earlier quota, with the quota's own time",
                shown.Metrics.Count == 1 && shown.Metrics[0].Percent == 40 && shown.Plan == "Max 5x" && shown.QuotaTime == now && shown.LastSeenAt == now
                && shown.Entries.Any(e => e.Model == "newer-model") && !shown.Entries.Any(e => e.Model == "test-model"));
            // The other way round: the quota was read, the records were not.
            clock = now.AddHours(2);
            var quotaOnly = Read(ProviderId.Claude, clock, 55); quotaOnly.UsageAvailable = false; quotaOnly.Entries = [];
            store.Save([quotaOnly], settings);
            var again = new UsageLastSeen(path, () => clock).Load(settings).Single();
            check("A quota read while the totals failed keeps the earlier totals", again.Metrics[0].Percent == 55 && again.UsageAvailable && again.Entries.Any(e => e.Model == "newer-model"));
        }

        // (g3) The first round after a start always writes, a clock set back does not hold writes off, and an unreadable metric does not stop them.
        {
            var path = File("restart.json");
            new UsageLastSeen(path, () => now).Save([Read(ProviderId.Claude, now, 40)], settings);
            var clock = now.AddMinutes(5);
            var next = new UsageLastSeen(path, () => clock);
            next.Load(settings);
            check("After a restart the first round writes even for the same values", next.Save([Read(ProviderId.Claude, now, 40)], settings)
                && new UsageLastSeen(path, () => clock).Load(settings).Single().LastSeenAt == clock);
            check("Then the same values a minute later are left alone again", !next.Save([Read(ProviderId.Claude, now, 40)], settings));
            clock = now.AddMinutes(-30);
            check("A clock set back does not hold a write off", next.Save([Read(ProviderId.Claude, now, 40)], settings));

            var broken = Read(ProviderId.Claude, now, 40);
            broken.Metrics = [.. broken.Metrics, new UsageMetric(MetricKind.Percent, "broken") { Id = "x", Percent = double.NaN }, new UsageMetric(MetricKind.Percent, "inf") { Id = "y", Percent = double.PositiveInfinity }];
            var nanPath = File("nan.json");
            var nan = new UsageLastSeen(nanPath, () => now);
            check("A metric that is not a number is left out and the round is still written", nan.Save([broken], settings)
                && new UsageLastSeen(nanPath, () => now).Load(settings).Single().Metrics.Select(m => m.Id).SequenceEqual(["five"]));
            check("Later rounds still write", nan.Save([Read(ProviderId.Claude, now, 70)], settings) && new UsageLastSeen(nanPath, () => now).Load(settings).Single().Metrics[0].Percent == 70);
        }

        // (g4) Usage that is no longer wanted leaves nothing on disk; the display of a round is the overlay until it is whole.
        {
            var path = File("clear.json");
            var store = new UsageLastSeen(path, () => now);
            store.Save([Read(ProviderId.Claude, now)], settings);
            store.Clear();
            check("Clearing removes the file and what was kept", !System.IO.File.Exists(path) && new UsageLastSeen(path, () => now).Load(settings).Count == 0
                && !store.Save([], settings));
            var restored = new UsageLastSeen(File("display.json"), () => now) is var d && d.Save([Read(ProviderId.Claude, now), Read(ProviderId.Codex, now)], settings) ? new UsageLastSeen(File("display.json"), () => now).Load(settings) : [];
            var codex = Read(ProviderId.Codex, now, 5);
            check("Until the round is whole the restored snapshots fill in", UsageLastSeen.Display([codex], restored, false).Count == 2);
            check("A whole round shows only what was read", UsageLastSeen.Display([codex], restored, true).Single() == codex);
            check("The marker follows the day and the language of when it is drawn", UsageLastSeen.Label(now, now).Contains(now.LocalDateTime.ToString("HH:mm"))
                && UsageLastSeen.Label(now, now.AddDays(1)).Contains(now.LocalDateTime.ToString("MM-dd HH:mm")));
        }

        // (i) A restored snapshot gives way to the fresh one of its source, provider by provider.
        {
            var path = File("overlay.json");
            new UsageLastSeen(path, () => now).Save([Read(ProviderId.Claude, now, 40), Read(ProviderId.Codex, now, 40)], settings);
            var restored = new UsageLastSeen(path, () => now).Load(settings);
            var shownAtStart = UsageLastSeen.Overlay([], restored);
            check("At the start both restored snapshots show", shownAtStart.Count == 2 && shownAtStart.All(s => s.LastSeenAt is not null));
            var codex = Read(ProviderId.Codex, now.AddMinutes(1), 12); codex.Quotas = [];
            var partial = UsageLastSeen.Overlay([codex], restored);
            check("A source that has answered shows its fresh snapshot, the other still the restored one",
                partial.Count == 2 && partial.Single(s => s.Id == ProviderId.Codex) is { LastSeenAt: null, IsStale: false } fresh && fresh.Metrics[0].Percent == 12
                && partial.Single(s => s.Id == ProviderId.Claude).LastSeenAt is not null);
            var failedRead = new ProviderSnapshot(ProviderId.Claude) { StatusLabel = "读取失败", Status = "x" };
            check("A source that answered with a failure shows its own state, not the old reading", UsageLastSeen.Overlay([failedRead, codex], restored).Count == 2
                && UsageLastSeen.Overlay([failedRead, codex], restored).Single(s => s.Id == ProviderId.Claude).LastSeenAt is null);
            var columns = DashboardUsage.Columns(settings, partial);
            check("The columns take the restored snapshot only where no fresh one exists", columns.Single(c => c.Entry.QuotaSource == ProviderId.Claude).Quota.LastSeenAt is not null
                && columns.Single(c => c.Entry.QuotaSource == ProviderId.Codex).Quota.LastSeenAt is null);
            check("The local-quota snapshot of Claude is told from the main one", UsageLastSeen.Overlay([new ProviderSnapshot(ProviderId.Claude) { Configurations = [ProviderCatalog.LocalQuota] }], restored).Count == 3);
        }

        // (j) Restored readings never raise a level notice and never seed a level; a fresh one is judged only against fresh ones.
        {
            var path = File("levels.json");
            new UsageLastSeen(path, () => now).Save([Read(ProviderId.Claude, now, 10)], settings);
            var restored = new UsageLastSeen(path, () => now).Load(settings);
            var levels = new QuotaLevels();
            check("A restored reading raises no notice", levels.Update(settings, restored) is null);
            check("Restored readings, even repeated, raise none", levels.Update(settings, restored) is null);
            var high = Read(ProviderId.Claude, now.AddSeconds(5), 97);
            check("The first fresh reading differs from the restored one and still raises no notice (it only seeds)", levels.Update(settings, [high]) is null);
            var lower = Read(ProviderId.Claude, now.AddSeconds(65), 20);
            check("A fresh reading that falls raises none", levels.Update(settings, [lower]) is null);
            var rise = levels.Update(settings, [Read(ProviderId.Claude, now.AddSeconds(125), 96)]);
            check("A fresh rise after fresh readings does raise one", rise is { Used: 96 } && rise.Metric.Id == "five");
            // A restored reading between fresh ones leaves the levels as they were.
            var other = new QuotaLevels();
            other.Update(settings, [Read(ProviderId.Claude, now, 10)]);
            other.Update(settings, restored);
            check("A restored reading between fresh ones neither lowers nor raises a level", other.Update(settings, [Read(ProviderId.Claude, now, 10)]) is null
                && other.Update(settings, [Read(ProviderId.Claude, now, 95)]) is { Used: 95 });
            var kept = Read(ProviderId.Claude, now, 99); kept.IsStale = true;
            check("A quota kept from an earlier read (stale) raises none either", new QuotaLevels() is var fresh && fresh.Update(settings, [Read(ProviderId.Claude, now, 10)]) is null && fresh.Update(settings, [kept]) is null);
        }
    }
}

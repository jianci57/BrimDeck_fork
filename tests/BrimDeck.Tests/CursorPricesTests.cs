using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

static class CursorPricesTests
{
    // Prices in USD per token: input, output, cache read, cache write.
    private static readonly decimal[] Truth = [0.000003m, 0.000015m, 0.0000003m, 0.00000375m];

    public static async Task Run(string root, Action<string, bool> check)
    {
        var start = new DateTimeOffset(2026, 11, 2, 3, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 40).Select(i => Record(i, start.AddMinutes(7 * i + 1))).ToList();
        // A reading 4 minutes after each record, in whole cents rounded down, as usage-status gives them.
        var rows = records.Select((_, i) => (Sum(records.Take(i + 1)), Math.Floor((double)records.Take(i + 1).Sum(Cost) * 100))).ToList();
        var fitted = CursorPrices.Fit(rows);
        decimal truth = records.Sum(Cost), worked = fitted is null ? 0 : records.Sum(r => Price(r, fitted));
        check("Readings of the weekly amount over time give prices that price the week within 2%", fitted is not null && Math.Abs(worked - truth) <= truth * 0.02m);
        check("Readings that stay below 20 cents give no prices", CursorPrices.Fit(rows.Where(r => r.Item2 < 20).ToList()) is null);
        check("Readings that no fixed prices reproduce give no prices", CursorPrices.Fit(rows.Select((r, i) => (r.Item1, i % 2 == 0 ? r.Item2 : r.Item2 * 1.5)).ToList()) is null);

        var path = Path.Combine(root, "cursor-prices", CursorPrices.FileName);
        var kept = new CursorPrices(path);
        kept.Use("auth0|A");
        kept.Add(start, 10, start.AddHours(1));
        kept.Add(start, 10, start.AddHours(2));
        check("A reading of the same amount in the same week extends the last one", kept.Samples.Count == 1 && kept.Samples[0].Last == start.AddHours(2));
        var near = new TokenEntry("near", start.AddHours(2).AddMinutes(-1), "Grok Bot", 1, 0, 0, 0, 1);
        kept.Add(start, 12, start.AddHours(3));
        kept.Update([near], (_, _) => true, start.AddHours(4));
        check("A reading with a record just before its end is taken from before that record",
            kept.Samples[0] is { Checked: true, Tokens: [0, 0, 0, 0] } && kept.Samples[1] is { Checked: false, Tokens: [1, 1, 0, 0] });
        var busy = new CursorSpendSample(start, 30, start.AddHours(5), start.AddHours(5).AddMinutes(2));
        check("A reading with records all through it has no moment to use",
            CursorPrices.Cutoff(busy, [start.AddHours(5).AddMinutes(-2), start.AddHours(5).AddMinutes(-1), start.AddHours(5).AddMinutes(1), start.AddHours(5).AddMinutes(2)], start.AddDays(1)) is null);
        check("The newest reading is taken no later than its records can have arrived",
            CursorPrices.Cutoff(busy with { First = start.AddHours(4) }, [], start.AddHours(5)) == start.AddHours(5));
        kept.Save();
        var again = new CursorPrices(path);
        again.Use("auth0|A");
        check("Readings are kept on disk for the same account, with the moment each was taken at",
            again.Samples.Count == 2 && again.Samples[0].Checked && again.Samples[0].Cutoff is not null && again.Samples[1].Tokens is not null);
        again.Use("auth0|B");
        check("Another account's readings are not used", again.Samples.Count == 0 && again.Prices is null);
        check("The file names no account", !File.ReadAllText(path).Contains("auth0|A", StringComparison.Ordinal));

        // Zero at the week's start, 30 cents at t1 and 50 at t2: the records up to t1 share 30 cents by their tokens, the
        // record between shares 20, and the record after t2 waits for the next reading.
        var shares = new CursorPrices(null);
        DateTimeOffset t1 = start.AddHours(2), t2 = start.AddHours(5);
        shares.Samples.AddRange([new(start, 30, t1, t1) { Tokens = [1, 1, 1, 1], Cutoff = t1 }, new(start, 50, t2, t2) { Tokens = [2, 2, 2, 2], Cutoff = t2 }]);
        var costs = shares.Costs([Entry("a", start.AddHours(1), 100), Entry("b", t1, 300), Entry("c", start.AddHours(3), 1000), Entry("d", t2.AddMinutes(1), 50)]);
        check("The amount added between two readings is shared among the records between them by their tokens",
            costs.Count == 3 && costs["a"] == 0.075m && costs["b"] == 0.225m && costs["c"] == 0.2m);
        check("A week's first records share the amount from zero at the week's start", costs["a"] + costs["b"] == 0.3m);

        await Refreshes(root, check);
    }

    // A merged plan read every 11 minutes, with a record 6 minutes before each refresh, works out prices that then price
    // last week's records after a restart, once the weekly amount has started again from zero.
    private static async Task Refreshes(string root, Action<string, bool> check)
    {
        var today = new DateTime(2026, 11, 2);
        var week = new DateTimeOffset(today.AddHours(3));
        var clock = new DateTimeOffset(today.AddHours(8));
        var cache = Path.Combine(root, "cursor-refresh", CursorPrices.FileName);
        using var handler = new Handler { Week = week, Now = clock };
        using var http = new HttpClient(handler);
        var locations = DataLocations.Resolve(Path.Combine(root, "cursor-prices-home"), Path.Combine(root, "cursor-prices-roaming"), root, null, null);
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Cursor }] };
        settings.Normalize();
        ProviderSnapshot last;
        using (var service = new UsageService(new Desktop(), locations, http, () => clock, cursorPriceCache: cache))
        {
            last = await Refresh(service);
            for (int i = 0; i < 14; i++)
            {
                clock = clock.AddMinutes(11);
                handler.Records.Add(Record(i, clock.AddMinutes(-6)));
                handler.Now = clock;
                last = await Refresh(service);
                if (i == 2)
                    check("Before prices are worked out, the records up to the last usable reading already share its amount",
                        last.Entries.Count == 3 && last.Entries.Take(2).All(e => e.FittedCost && e.ReportedCostUsd > 0) && last.Entries[2].ReportedCostUsd is null);
            }
            decimal truth = handler.Records.Sum(Cost), shown = last.Entries.Sum(e => e.ReportedCostUsd ?? 0);
            check("A merged plan's records are priced from the weekly amount once enough readings are known",
                last.Entries.Count == 14 && last.Entries.All(e => e.FittedCost) && Math.Abs(shown - truth) <= truth * 0.03m && File.Exists(cache));

            // The next day, after a record at 09:00, the amount is read at 10:00 and 10:15. The day before was read in full
            // at 10:00 and is not read again, yet the reading counts.
            var nextDay = new DateTimeOffset(today.AddDays(1));
            handler.Records.Add(Record(20, nextDay.AddHours(9)));
            clock = nextDay.AddHours(10); handler.Now = clock;
            await Refresh(service);
            clock = clock.AddMinutes(15); handler.Now = clock;
            await Refresh(service);
            var kept = new CursorPrices(cache);
            kept.Use("auth0|A");
            check("A reading the day after the week began counts once that day's records have arrived",
                kept.Samples[^1] is { First: var first, Tokens: not null, Cutoff: { } cutoff } && first == nextDay.AddHours(10) && cutoff == nextDay.AddHours(10).AddMinutes(5));
        }

        // A week later the amount is zero again and nothing new was used; the kept prices still price the last one.
        decimal all = handler.Records.Sum(Cost);
        clock = clock.AddDays(7); handler.Week = week.AddDays(7); handler.Now = clock;
        using (var restarted = new UsageService(new Desktop(), locations, http, () => clock, cursorPriceCache: cache))
            last = await Refresh(restarted);
        check("After a restart and the weekly reset, last week's records keep their worked-out prices",
            last.Entries.Count == 15 && last.Entries.All(e => e.FittedCost) && Math.Abs(last.Entries.Sum(e => e.ReportedCostUsd ?? 0) - all) <= all * 0.03m);

        async Task<ProviderSnapshot> Refresh(UsageService service) => (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Cursor);
    }

    private static TokenEntry Entry(string key, DateTimeOffset time, long tokens) => new(key, time, "Grok Bot", 0, tokens, 0, 0, 0);

    // Token mixes that vary from record to record, mostly cache reads as Grok Bot's are.
    private static TokenEntry Record(int i, DateTimeOffset time) => new($"cursor:r{i}", time, "Grok Bot",
        200 + i * 37 % 900, 30000 + i * 7919 % 60000, 500 + i * 3571 % 9000, 0, 100 + i * 211 % 2500);

    private static decimal Cost(TokenEntry e) => e.Input * Truth[0] + e.Output * Truth[1] + e.CacheRead * Truth[2] + e.CacheWrite * Truth[3];
    private static decimal Price(TokenEntry e, TokenPrices p) => e.Input * p.Input + e.Output * p.Output + e.CacheRead * (p.CacheRead ?? 0) + e.CacheWrite * (p.CacheWrite ?? 0);
    private static long[] Sum(IEnumerable<TokenEntry> entries)
    {
        var list = entries.ToList();
        return [list.Sum(e => e.Input), list.Sum(e => e.Output), list.Sum(e => e.CacheRead), list.Sum(e => e.CacheWrite)];
    }

    private static string Token(string subject) =>
        "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = subject }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    private sealed class Desktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => Token("auth0|A");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<TokenEntry> Records { get; } = [];
        public DateTimeOffset Week { get; set; }
        public DateTimeOffset Now { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string Ms(DateTimeOffset time) => time.ToUnixTimeMilliseconds().ToString();
            if (path.EndsWith("/GetSandUsageStatus", StringComparison.Ordinal)) return Reply("""{"usagePercent":1,"cursorPlanName":"Premium","isUnified":true}""");
            if (path.EndsWith("/GetCurrentPeriodUsage", StringComparison.Ordinal))
                return Reply($$$"""{"billingCycleStart":"{{{Ms(Week)}}}","billingCycleEnd":"{{{Ms(Week.AddDays(7))}}}","planUsage":{"totalPercentUsed":1}}""");
            if (path == "/api/dashboard/v2/usage-status")
            {
                // The amount trails the records by a minute and is rounded down to whole cents.
                var cents = Math.Floor(Records.Where(r => r.Time >= Week && r.Time <= Now.AddMinutes(-1)).Sum(Cost) * 100);
                return Reply($$$"""{"weeklyIncludedUsage":{"usedCents":{{{cents}}},"budgetCents":4615,"periodStart":"{{{Week.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}}"}}""");
            }
            if (path == "/api/dashboard/v2/query-billing-items")
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                DateTimeOffset from = DateTimeOffset.Parse(query["startTime"]!), to = DateTimeOffset.Parse(query["endTime"]!);
                var items = Records.Where(r => r.Time >= from && r.Time < to && r.Time <= Now).Select(r =>
                    $$$"""{"billingItemId":"{{{r.Key[7..]}}}","eventTime":"{{{r.Time.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}}","product":"grok-bot","chargeCategory":"usage","usageType":"included","usage":{"prompt-text-tokens":"{{{r.Input}}}","cached-prompt-text-tokens":"{{{r.CacheRead}}}","cache-write-text-tokens":"{{{r.CacheWrite}}}","completion-text-tokens":"{{{r.Output}}}"},"pricing":{"currency":"USD","contracted":"0.0000000000"}}""");
                return Reply("{\"data\":[" + string.Join(",", items) + "],\"pagination\":{\"hasMore\":false}}");
            }
            throw new InvalidOperationException("Unexpected Cursor destination " + path);
        }

        private static Task<HttpResponseMessage> Reply(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

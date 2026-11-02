using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

static class CursorUsageTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var today = new DateTime(2026, 10, 25);
        var clock = new DateTimeOffset(today.AddHours(12));
        var desktop = new Desktop { Token = Token("auth0|A") };
        using var handler = new Handler();
        // Account A: two records today, 1001 yesterday (two pages), and on 10-20 three records alike but for their ids.
        handler.Days["auth0|A"] = new()
        {
            [today] = Records("a", today, 2),
            [today.AddDays(-1)] = Records("a", today.AddDays(-1), 1001),
            [today.AddDays(-5)] = Enumerable.Range(0, 3).Select(i => Record("same-" + i, today.AddDays(-5).AddHours(9))).ToList(),
        };
        handler.Days["auth0|B"] = new() { [today] = Records("b", today, 1) };
        using var http = new HttpClient(handler);
        var locations = DataLocations.Resolve(Path.Combine(root, "cursor-home"), Path.Combine(root, "cursor-roaming"), root, null, null);
        using var service = new UsageService(desktop, locations, http, () => clock);
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Cursor }] };
        settings.Normalize();
        async Task<ProviderSnapshot> Refresh() => (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Cursor);

        // Today, yesterday's two pages, 10-23 and 10-22 are read; the sixth request fails, so 26 of the 30 days are left.
        handler.FailAfter = 5;
        var first = await Refresh();
        check("A Cursor refresh that stops part way keeps the days it read in full",
            first.Quotas.Count > 0 && first.UsageAvailable && !first.UsageComplete && first.Entries.Count == 1003 && first.UsageNote.Contains("其中 26 天", StringComparison.Ordinal));
        check("Cursor records are read a day at a time, newest first, with every page of a full day",
            handler.Calls.Take(3).SequenceEqual([(today, 1), (today.AddDays(-1), 1), (today.AddDays(-1), 2)]));

        handler.FailAfter = null; handler.Calls.Clear(); clock = clock.AddMinutes(1);
        var second = await Refresh();
        check("The next refresh reads today again and only the days still missing",
            second.UsageComplete && second.Entries.Count == 1006 && handler.Calls.Count == 27 && handler.Calls[0] == (today, 1) &&
            !handler.Calls.Any(call => call.Day == today.AddDays(-1) || call.Day == today.AddDays(-2) || call.Day == today.AddDays(-3)));
        check("Cursor records with the same time, model and usage stay apart by their event id",
            second.Entries.Count(entry => entry.Time == new DateTimeOffset(today.AddDays(-5).AddHours(9))) == 3);
        check("An empty day and a page past the last are read as complete", handler.Calls.Count(call => call.Day == today.AddDays(-20)) == 1);

        handler.Calls.Clear(); clock = clock.AddMinutes(1);
        var third = await Refresh();
        check("Once every day is kept, a refresh asks only for today", third.UsageComplete && third.Entries.Count == 1006 && handler.Calls.SequenceEqual([(today, 1)]));

        handler.Calls.Clear(); desktop.Token = Token("auth0|B");
        var switched = await Refresh();
        check("Signing in to another Cursor account drops the kept days and reads the new account's",
            switched.UsageComplete && switched.Entries.Count == 1 && switched.Entries[0].Key == "cursor:b-1025-0" && handler.Calls.Count == 30);
        check("An older Cursor plan keeps its two billing-period quotas and adds the Grok Bot weekly allowance",
            switched.Quotas.Select(q => q.Label).SequenceEqual(["Cursor 模型", "其他模型", "Grok Bot"]) && switched.Plan == "ultra");

        handler.Unified = true;
        var merged = await Refresh();
        check("A plan merged with Grok shows one weekly allowance, its plan name and no separate Grok Bot row",
            merged.Quotas.Count == 1 && merged.Quotas[0] is { Label: "每周额度", UsedPercent: 12.5, Minutes: 10080 } &&
            merged.Quotas[0].ResetAt == DateTimeOffset.FromUnixTimeMilliseconds(1794193934270) && merged.Plan == "Premium");
    }

    private static string Token(string subject) =>
        "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = subject }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    private static List<string> Records(string prefix, DateTime day, int count) =>
        Enumerable.Range(0, count).Select(i => Record($"{prefix}-{day:MMdd}-{i}", day.AddMinutes(i))).ToList();

    private static string Record(string id, DateTime time) =>
        $$"""{"timestamp":"{{new DateTimeOffset(time).ToUnixTimeMilliseconds()}}","model":"composer-2.5","tokenUsage":{"inputTokens":10,"outputTokens":5,"totalCents":0.5},"usageEventId":"{{id}}"}""";

    private sealed class Desktop : IDesktopSources
    {
        public string Token { get; set; } = "";
        public string? ReadCursorToken(string database) => Token;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Dictionary<string, Dictionary<DateTime, List<string>>> Days { get; } = [];
        public List<(DateTime Day, int Page)> Calls { get; } = [];
        public int? FailAfter { get; set; }
        public bool Unified { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/GetCurrentPeriodUsage", StringComparison.Ordinal))
                return Reply(Unified
                    ? """{"billingCycleStart":"1793589134270","billingCycleEnd":"1794193934270","planUsage":{"autoPercentUsed":12.5,"apiPercentUsed":0,"totalPercentUsed":12.5}}"""
                    : """{"planUsage":{"autoPercentUsed":10,"apiPercentUsed":20},"billingCycleEnd":"1793735420000","membershipType":"ultra"}""");
            if (path.EndsWith("/GetSandUsageStatus", StringComparison.Ordinal))
                return Reply(Unified
                    ? """{"nextResetTimestampUtc":"2026-11-09T03:12:14.270Z","usagePercent":12.5,"cursorPlanName":"Premium","isUnified":true}"""
                    : """{"nextResetTimestampUtc":"2026-10-30T03:12:14.270Z","usagePercent":3}""");
            if (!path.EndsWith("/GetFilteredUsageEvents", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected Cursor destination " + path);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            long start = long.Parse(body.RootElement.GetProperty("startDate").GetString()!), end = long.Parse(body.RootElement.GetProperty("endDate").GetString()!);
            int page = body.RootElement.GetProperty("page").GetInt32(), size = body.RootElement.GetProperty("pageSize").GetInt32();
            var day = DateTimeOffset.FromUnixTimeMilliseconds(start).LocalDateTime.Date;
            if (FailAfter is { } limit && Calls.Count >= limit) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            Calls.Add((day, page));
            var token = request.Headers.Authorization?.Parameter ?? "";
            var account = Days.Keys.Single(name => token == Token(name));
            var records = Days[account].Values.SelectMany(list => list)
                .Select(text => (Text: text, Time: long.Parse(JsonDocument.Parse(text).RootElement.GetProperty("timestamp").GetString()!)))
                .Where(item => item.Time >= start && item.Time <= end).OrderByDescending(item => item.Time).Select(item => item.Text).ToList();
            if (records.Count == 0) return Reply("{}");
            var rows = records.Skip((page - 1) * size).Take(size).ToList();
            return Reply(rows.Count == 0 ? $$"""{"totalUsageEventsCount":"{{records.Count}}"}"""
                : $$"""{"totalUsageEventsCount":"{{records.Count}}","usageEventsDisplay":[{{string.Join(",", rows)}}]}""");
        }

        private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

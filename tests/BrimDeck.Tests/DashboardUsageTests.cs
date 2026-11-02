using System.Net;
using System.Text.Json;
using BrimDeck.Core;

internal static class DashboardUsageTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var legacy = SettingsMigrations.Read("""{"Apps":[{"Id":1,"Enabled":true,"ThemeColor":"#123456"},{"Id":0,"Enabled":false}]}""");
        legacy.Normalize();
        check("Legacy app rows migrate both sources and their default names", legacy.Apps[0] is { QuotaSource: ProviderId.Codex, UsageSource: ProviderId.Codex, Name: "Codex", Theme: "#123456" }
            && !legacy.Apps[1].Enabled && legacy.Apps[1].Name == "Claude");
        var oldIdentity = legacy.Apps[0].InstanceId;
        var serialized = JsonSerializer.Serialize(legacy);
        var migrated = JsonSerializer.Deserialize<DeckSettings>(serialized)!;
        migrated.Normalize();
        check("New source settings persist stable identities without the legacy Id field", migrated.Apps[0].InstanceId == oldIdentity
            && serialized.Contains("\"QuotaSource\"") && serialized.Contains("\"UsageSource\"") && !serialized.Contains("\"Id\""));

        var mixed = new AppEntry { QuotaSource = ProviderId.Claude, UsageSource = ProviderId.Codex };
        var duplicate = mixed.Copy(); duplicate.InstanceId = Guid.NewGuid(); duplicate.DisplayName = "工作用量"; duplicate.ThemeColor = "#FF0000";
        var settings = new DeckSettings { Apps = [mixed, duplicate] };
        settings.Normalize();
        check("Repeated quota and statistics sources retain independent columns", settings.Apps.Count == 2 && mixed.InstanceId != duplicate.InstanceId);
        mixed.QuotaSource = ProviderId.Antigravity;
        check("Changing a quota source updates only an automatic name", mixed.Name == "Antigravity" && mixed.UsageSource == ProviderId.Codex);
        duplicate.QuotaSource = ProviderId.Antigravity;
        check("Changing either source preserves a user supplied name", duplicate.Name == "工作用量" && duplicate.Theme == "#FF0000");
        mixed.QuotaSource = duplicate.QuotaSource = ProviderId.Claude;
        check("Required sources include statistics-only applications once", settings.RequiredProviders.SetEquals([ProviderId.Claude, ProviderId.Codex])
            && settings.StatisticsProviders.SequenceEqual([ProviderId.Codex]));
        mixed.Enabled = false;
        check("Disabling the first duplicate does not disable another column's source", settings.Enabled(ProviderId.Claude) && settings.Enabled(ProviderId.Codex));
        mixed.Enabled = true;

        var now = DateTimeOffset.Now;
        var quota = new ProviderSnapshot(ProviderId.Claude) { Plan = "Quota plan", LiveQuota = true, Quotas = [new("每周", 71, now.AddDays(1))],
            UsageAvailable = true, Entries = [new("wrong-source", now, "wrong-model", 9999, 0, 0, 0, 0)] };
        var usage = new ProviderSnapshot(ProviderId.Codex) { UsageAvailable = true, Entries = [new("statistics", now, "test-model", 80, 5, 0, 0, 15, .25m)] };
        var columns = DashboardUsage.Columns(settings, [quota, usage]);
        check("A mixed column uses the chosen quota and original statistics snapshot", columns.Count == 2 && ReferenceEquals(columns[0].Quota, quota)
            && ReferenceEquals(columns[0].Usage, usage) && columns[0].Quota.Quotas.Single().UsedPercent == 71 && columns[0].Usage.Entries.Sum(e => e.Total) == 100);
        var missingQuota = DashboardUsage.Columns(settings, [usage])[0];
        check("Missing quotas do not hide valid statistics from the other application", missingQuota.Quota.Quotas.Count == 0 && missingQuota.Usage.UsageAvailable);
        var missingUsage = DashboardUsage.Columns(settings, [quota])[0];
        check("Missing statistics never fall back to the quota provider's token counts", missingUsage.Quota.Quotas.Count == 1 && !missingUsage.Usage.UsageAvailable && missingUsage.Usage.Entries.Count == 0);
        var statistics = DashboardUsage.Statistics(settings, [quota, usage]);
        var total = ModelUsage.Filter(statistics, settings.StatisticsProviders.ToHashSet(), UsageDateRange.Recent(1, DateTime.Today));
        check("Model totals count a repeated statistics application exactly once", total.Rows.Count == 1 && total.Entries.Sum(e => e.Total) == 100 && total.Rows[0].Provider == ProviderId.Codex);
        settings.UsagePage = false;
        check("Compact rings keep the requested sources while the page is off", settings.RequiredProviders.Count > 0);
        settings.NotchSummary = false;
        check("Disabling the page and the rings removes both kinds of requested sources", settings.RequiredProviders.Count == 0 && DashboardUsage.Columns(settings, [quota, usage]).Count == 0);
        settings.UsagePage = true; settings.NotchSummary = true;

        var copied = settings.Copy();
        copied.Apps.RemoveAt(0);
        copied.Apps.Add(copied.Apps[0].Copy()); copied.Normalize();
        check("Copied instance IDs are repaired without dropping repeated rows", copied.Apps.Count == 2 && copied.Apps.Select(a => a.InstanceId).Distinct().Count() == 2);

        var balanceRow = new AppEntry { QuotaSource = ProviderId.DeepSeek, BalanceTarget = 100 };
        var balanceSnapshot = new ProviderSnapshot(ProviderId.DeepSeek) { LiveQuota = true, Configurations = [balanceRow.ConfigurationKey], Metrics =
            [new(MetricKind.Balance, "账户余额") { Id = "total:CNY", Amount = 20, Currency = "CNY" }, new(MetricKind.Balance, "赠送余额") { Id = "granted:CNY", Amount = 5, Currency = "CNY" }] };
        double? Used(AppEntry row, decimal amount)
        {
            balanceSnapshot.Metrics = [balanceSnapshot.Metrics[0] with { Amount = amount }, balanceSnapshot.Metrics[1]];
            return DashboardUsage.Quota(row, [balanceSnapshot]).Metrics[0].UsedPercent;
        }
        var targeted = DashboardUsage.Quota(balanceRow, [balanceSnapshot]);
        check("A target balance gives the account balance a used share and leaves other balances bare", Used(balanceRow, 20) == 80 && Used(balanceRow, 60) == 40
            && targeted.Metrics[0] is { Total: 100, TotalIsTarget: true } && targeted.Metrics[1].Total is null && targeted.Metrics[1].UsedPercent is null);
        check("A balance above the target is drawn as unused", Used(balanceRow, 150) is < 0 && AppPresets.Level(Math.Clamp(Used(balanceRow, 150)!.Value, 0, 100)) == 0);
        check("The target leaves the shared snapshot unchanged", balanceSnapshot.Metrics[0].Total is null && !ReferenceEquals(DashboardUsage.Quota(balanceRow, [balanceSnapshot]), balanceSnapshot));
        var untargeted = new AppEntry { QuotaSource = ProviderId.DeepSeek, BalanceTarget = null };
        check("Without a target the account balance stays an amount", Used(untargeted, 20) is null && ReferenceEquals(DashboardUsage.Quota(untargeted, [balanceSnapshot]), balanceSnapshot));
        var limited = new ProviderSnapshot(ProviderId.OpenRouter) { Configurations = [new AppEntry { QuotaSource = ProviderId.OpenRouter }.ConfigurationKey],
            Metrics = [new(MetricKind.Balance, "密钥余额") { Amount = 3, Total = 10, Currency = "USD" }] };
        var openRouter = new AppEntry { QuotaSource = ProviderId.OpenRouter, BalanceTarget = 100 };
        check("A balance with its own total keeps it", DashboardUsage.Quota(openRouter, [limited]).Metrics[0] is { Total: 10, TotalIsTarget: false });
        var normalized = new[] { 0m, -5m, 0.001m, 12.345m, 2_000_000_000m }.Select(value => { var row = new AppEntry { QuotaSource = ProviderId.DeepSeek, BalanceTarget = value }; row.Normalize(); return row.BalanceTarget; }).ToList();
        check("Target balances keep positive amounts to the cent", normalized.SequenceEqual([null, null, null, 12.35m, null]));

        var oversized = new DeckSettings { Apps = Enumerable.Range(1, 9).Select(i => new AppEntry
            { QuotaSource = ProviderId.Claude, UsageSource = ProviderId.Claude, DisplayName = "应用 " + i, Enabled = i != 1 }).ToList() };
        var retained = oversized.Apps.Take(7).Select(a => a.InstanceId).ToArray();
        oversized.Normalize();
        check("Application configuration is limited to seven rows including hidden rows", oversized.Apps.Count == 7 && oversized.EnabledApps.Count == 6);
        check("Oversized configurations preserve the first seven identities and their order", oversized.Apps.Select(a => a.InstanceId).SequenceEqual(retained));

        var directory = Path.Combine(root, "source-requests");
        var locations = DataLocations.Resolve(Path.Combine(directory, "home"), Path.Combine(directory, "roaming"), Path.Combine(directory, "local"), null, null);
        Directory.CreateDirectory(locations.CodexHome);
        File.WriteAllText(Path.Combine(locations.CodexHome, "auth.json"), """{"tokens":{"access_token":"fixture-codex"}}""");
        using var handler = new SourceHandler();
        using var client = new HttpClient(handler);
        using var service = new UsageService(new SourceDesktop(), locations, client);
        var snapshots = await service.RefreshAsync(settings);
        check("Mixed duplicate columns fetch both underlying sources only once", snapshots.Count == 2 && handler.Requests.Count == 2
            && handler.Requests.Distinct().Count() == 2 && snapshots.All(s => s.LiveQuota));

        // WSL statistics join the local ones, and a WSL file system that does not answer in time only costs its own share.
        string Line(string id, int input) => JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now.AddMinutes(-3), type = "assistant", message = new { id, model = "claude-sonnet-4-6", usage = new { input_tokens = input, output_tokens = 0 } } });
        Directory.CreateDirectory(Path.Combine(locations.ClaudeHome, "projects"));
        File.WriteAllLines(Path.Combine(locations.ClaudeHome, "projects", "local.jsonl"), [Line("local-message", 100)]);
        var wslClaude = Path.Combine(directory, "wsl", ".claude"); Directory.CreateDirectory(Path.Combine(wslClaude, "projects"));
        File.WriteAllLines(Path.Combine(wslClaude, "projects", "wsl.jsonl"), [Line("wsl-message", 20), Line("local-message", 100)]);
        var wslSettings = settings.Copy(); wslSettings.IncludeWsl = true;
        using (var wslService = new UsageService(new SourceDesktop(), locations, client, null, null, () => [new WslHome("Test", wslClaude, "")]))
        {
            var withWsl = (await wslService.RefreshAsync(wslSettings)).Single(s => s.Id == ProviderId.Claude);
            check("WSL statistics are added to the local ones without counting a shared entry twice", withWsl.Entries.Sum(entry => entry.Total) == 120);
            var withoutWsl = (await wslService.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Claude);
            check("WSL statistics are left out while the option is off", withoutWsl.Entries.Sum(entry => entry.Total) == 100);
        }
        // The WSL read runs beside the network requests of the same refresh, so the round takes the longer of the two, not both.
        // Checked by when each was under way rather than by the round's length, and by a handshake rather than by sleeps: a request
        // waits until the WSL read has begun and the read until a request has begun (each for at most 5 s), so the two overlap
        // exactly when they run at the same time, however slow or busy the machine is. Run one after the other, one of them waits in
        // vain and the spans do not meet.
        var timeline = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan wslStart = default, wslEnd = default;
        using var wslBegun = new ManualResetEventSlim();
        handler.Timeline = timeline; handler.HoldUntil = wslBegun;
        using (var overlapping = new UsageService(new SourceDesktop(), locations, client, null, null,
            () =>
            {
                handler.Begun.Wait(TimeSpan.FromSeconds(5));
                wslStart = timeline.Elapsed; wslBegun.Set(); Thread.Sleep(50); wslEnd = timeline.Elapsed;
                return [new WslHome("Test", wslClaude, "")];
            }, TimeSpan.FromSeconds(15)))
        {
            var round = await overlapping.RefreshAsync(wslSettings);
            check("The WSL read overlaps the network requests", handler.Busy is { } busy && wslStart < busy.End && busy.Start < wslEnd &&
                round.Single(s => s.Id == ProviderId.Claude).Entries.Sum(entry => entry.Total) == 120);
        }
        handler.Timeline = null; handler.HoldUntil = null;
        handler.DelayMs = 0;
        using var release = new ManualResetEventSlim();
        using (var stuck = new UsageService(new SourceDesktop(), locations, client, null, null, () => { release.Wait(TimeSpan.FromSeconds(20)); return [new WslHome("Test", wslClaude, "")]; }, TimeSpan.FromMilliseconds(1000)))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var all = await stuck.RefreshAsync(wslSettings);
            var claude = all.Single(s => s.Id == ProviderId.Claude);
            check("A WSL read that does not answer is skipped and the other sources still refresh",
                clock.Elapsed < TimeSpan.FromSeconds(5) && claude.Entries.Sum(entry => entry.Total) == 100 && !claude.UsageComplete && all.Single(s => s.Id == ProviderId.Codex).LiveQuota);
            clock.Restart();
            var again = (await stuck.RefreshAsync(wslSettings)).Single(s => s.Id == ProviderId.Claude);
            check("A stuck WSL read is not waited for again every round", clock.Elapsed < TimeSpan.FromMilliseconds(700) && again.Entries.Sum(entry => entry.Total) == 100);
            release.Set();
        }
    }

    private sealed class SourceDesktop : IDesktopSources
    {
        public ClaudeDesktopState ReadClaudeDesktop(DataLocations locations) => new(true, [new("fixture-claude", "pro", ClaudeSignIn.Desktop)]);
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unselected Cursor source was read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unselected Antigravity source was read");
    }

    private sealed class SourceHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public int DelayMs { get; set; }
        // While set, the span from the first request's start to the last one's end on this clock (Busy).
        public System.Diagnostics.Stopwatch? Timeline { get; set; }
        public (TimeSpan Start, TimeSpan End)? Busy { get; private set; }
        // Set as a request begins while Timeline is set; a request then waits for HoldUntil, if given, for at most 5 s.
        public ManualResetEventSlim Begun { get; } = new();
        public ManualResetEventSlim? HoldUntil { get; set; }
        private readonly object _gate = new();
        protected override void Dispose(bool disposing) { if (disposing) Begun.Dispose(); base.Dispose(disposing); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var timeline = Timeline;
            var start = timeline?.Elapsed;
            if (timeline is not null) { Begun.Set(); if (HoldUntil is { } hold) await Task.Run(() => hold.Wait(TimeSpan.FromSeconds(5)), cancellationToken); }
            if (DelayMs > 0) await Task.Delay(DelayMs, cancellationToken);
            if (timeline is not null && start is { } began)
                lock (_gate) Busy = Busy is { } busy ? (busy.Start < began ? busy.Start : began, timeline.Elapsed) : (began, timeline.Elapsed);
            var destination = request.RequestUri!.AbsoluteUri;
            // The plan lookup is optional and answered with 404 here; it is not one of the quota sources counted below.
            if (destination == "https://api.anthropic.com/api/oauth/profile") return new HttpResponseMessage(HttpStatusCode.NotFound);
            Requests.Add(destination);
            var json = destination switch
            {
                "https://api.anthropic.com/api/oauth/usage" => """{"five_hour":{"utilization":71}}""",
                "https://chatgpt.com/backend-api/wham/usage" => """{"rate_limit":{"primary_window":{"used_percent":22,"limit_window_seconds":18000}}}""",
                _ => throw new InvalidOperationException("Unexpected destination")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}

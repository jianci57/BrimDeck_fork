using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrimDeck.Core;

// The Claude quota kept on disk by an earlier version (one reading, no organization) after the version that reads an account's
// organizations separately (ClaudeQuotaCache) is installed. Claude Code's account record names the organization it uses, so the
// first read after the upgrade asks for that organization's reading; the file of the earlier version names none. When the quota
// interface cannot be reached then (rate limited, offline), the kept reading is all the collapsed ring has: without it neither
// the usage page nor the ring has a reading until the interface answers again.
static class ClaudeUpgradeCacheTests
{
    private static readonly DateTimeOffset Start = new(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Local));

    // The file the earlier version wrote: the reading's own object, with no "Organization" in it.
    private static string EarlierFile(string account)
    {
        var reading = new ClaudeQuotaSnapshot(Start, "Max 5x", "", "", [new Quota("5 小时额度", 34, Start.AddHours(3), 300), new Quota("每周额度", 61, Start.AddDays(3), 10080)]);
        var node = JsonNode.Parse(ClaudeQuotaCache.Serialize(reading, account, ClaudeSignIn.Code, ClaudePlanOrigin.Account))!.AsObject();
        node.Remove("Organization");
        return node.ToJsonString();
    }

    // The quota interface: limiting every request until it is set to answer.
    private sealed class Interface : HttpMessageHandler
    {
        public int Requests;
        public bool Answers;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            if (request.RequestUri?.AbsoluteUri == "https://api.anthropic.com/api/oauth/profile") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (!Answers) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                five_hour = new { utilization = 40, resets_at = Start.AddHours(4) }, seven_day = new { utilization = 70, resets_at = Start.AddDays(4) }
            })) });
        }
    }

    public static async Task Run(string root, Action<string, bool> check)
    {
        Pure(check);
        await Service(root, check);
    }

    static void Pure(Action<string, bool> check)
    {
        var earlier = EarlierFile("account-a");
        check("Claude upgrade cache: a file of the earlier version holds a reading that has no organization", !earlier.Contains("Organization", StringComparison.Ordinal));
        check("Claude upgrade cache: the first organization asked for takes the earlier version's reading",
            ClaudeQuotaCache.Parse(earlier, Start.AddMinutes(5), "account-a", "org-personal", earlierReading: true) is { Quotas.Count: 2, Plan: "Max 5x" });
        check("Claude upgrade cache: an organization asked for after the first does not take it, it is another organization's",
            ClaudeQuotaCache.Parse(earlier, Start.AddMinutes(5), "account-a", "org-team") is null);
        check("Claude upgrade cache: the earlier version's reading still belongs to its account only",
            ClaudeQuotaCache.Parse(earlier, Start.AddMinutes(5), "account-b", "org-personal", earlierReading: true) is null);
        var saved = ClaudeQuotaCache.Serialize(new(Start, "Pro", "", "", [new Quota("每周额度", 5, null, 10080)]), "account-a", ClaudeSignIn.Code, ClaudePlanOrigin.Account, "org-personal");
        check("Claude upgrade cache: a reading kept under its organization is found by it, and not by another",
            ClaudeQuotaCache.Parse(saved, Start.AddMinutes(5), "account-a", "org-personal") is { Plan: "Pro" } && ClaudeQuotaCache.Parse(saved, Start.AddMinutes(5), "account-a", "org-team", earlierReading: true) is null);

        // The primary organization is rate limited (its reading not yet moved to its own name) while another organization is saved or
        // rejected: the earlier version's reading stays for the primary one, also after a restart (a new read of the file).
        var path = Path.Combine(Path.GetTempPath(), "brimdeck-upgrade-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, earlier);
            var team = new ClaudeQuotaSnapshot(Start.AddMinutes(1), "Team", "", "", [new Quota("每周额度", 9, null, 10080)]);
            ClaudeQuotaCache.Save(path, team, "account-a", ClaudeSignIn.Code, ClaudePlanOrigin.Account, "org-team", primary: false);
            check("Claude upgrade cache: another organization's save leaves the earlier reading for the primary one",
                ClaudeQuotaCache.Load(path, Start.AddMinutes(5), "account-a", "org-personal", earlierReading: true) is { Plan: "Max 5x" });
            ClaudeQuotaCache.Delete(path, "org-team", primary: false);
            check("Claude upgrade cache: another organization rejected leaves it too, and its own reading goes",
                ClaudeQuotaCache.Load(path, Start.AddMinutes(5), "account-a", "org-personal", earlierReading: true) is { Plan: "Max 5x" }
                && ClaudeQuotaCache.Load(path, Start.AddMinutes(5), "account-a", "org-team") is null);
            var personal = new ClaudeQuotaSnapshot(Start.AddMinutes(2), "Max 5x", "", "", [new Quota("每周额度", 50, null, 10080)]);
            ClaudeQuotaCache.Save(path, personal, "account-a", ClaudeSignIn.Code, ClaudePlanOrigin.Account, "org-personal", primary: true);
            check("Claude upgrade cache: the primary organization's own save replaces the earlier reading",
                !File.ReadAllText(path).Contains("\"Organization\":null", StringComparison.Ordinal)
                && ClaudeQuotaCache.Load(path, Start.AddMinutes(5), "account-a", "org-personal") is { Quotas.Count: 1 }
                && ClaudeQuotaCache.Load(path, Start.AddMinutes(5), "account-a", "org-team", earlierReading: true) is null);

            // The earlier version's reading counts as the one of the organization "": a save under "" replaces it, whichever
            // organization comes first, so the new reading is the one found (Parse takes the first that matches).
            File.WriteAllText(path, earlier);
            var unnamed = new ClaudeQuotaSnapshot(Start.AddMinutes(3), "Pro", "", "", [new Quota("每周额度", 12, null, 10080)]);
            ClaudeQuotaCache.Save(path, unnamed, "account-a", ClaudeSignIn.Code, ClaudePlanOrigin.Account);
            check("Claude upgrade cache: a save under the organization \"\" replaces the earlier reading, which no longer stands in",
                ClaudeQuotaCache.Load(path, Start.AddMinutes(5), "account-a") is { Plan: "Pro" }
                && !File.ReadAllText(path).Contains("\"Organization\":null", StringComparison.Ordinal));
            File.WriteAllText(path, earlier);
            ClaudeQuotaCache.Delete(path, "");
            check("Claude upgrade cache: the organization \"\" rejected takes the earlier reading with it",
                ClaudeQuotaCache.Load(path, Start.AddMinutes(5), "account-a") is null);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }


    // The real service: Claude Code names its organization, the quota interface limits the request, and the only reading there is
    // lies in the file of the earlier version. The collapsed ring needs a quota (the row leaves out an application with none).
    static async Task Service(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "upgrade-cache-home");
        var locations = DataLocations.Resolve(home, Path.Combine(root, "upgrade-cache-roaming"), Path.Combine(root, "upgrade-cache-local"), null, "");
        Directory.CreateDirectory(locations.ClaudeHome);
        File.WriteAllText(Path.Combine(locations.ClaudeHome, ".credentials.json"), JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = "token-a", subscriptionType = "max" } }));
        File.WriteAllText(Path.Combine(locations.ClaudeHome, ".claude.json"), JsonSerializer.Serialize(new { oauthAccount = new { accountUuid = "account-a", organizationUuid = "org-personal" } }));
        var cache = Path.Combine(root, "upgrade-cache-file", ClaudeQuotaCache.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, EarlierFile("account-a"));
        var settings = new DeckSettings { Apps = [new AppEntry { Id = ProviderId.Claude }] };
        var clock = Start.AddMinutes(10);
        using var handler = new Interface();
        using var http = new HttpClient(handler);
        using var service = new UsageService(new ClaudeFixtureSources(), locations, http, () => clock, quotaCache: cache);
        var snapshots = await service.RefreshAsync(settings);
        var claude = snapshots.Single();
        check("Claude upgrade cache: the quota interface was asked", handler.Requests > 0);
        check("Claude upgrade cache: limited right after the upgrade, the reading kept by the earlier version is shown (two quotas, from the file)",
            claude.Quotas.Count == 2 && claude.Quotas.Any(q => q.Minutes == 300 && q.UsedPercent == 34) && claude.IsStale);
        // The collapsed ring is drawn for an application that has a reading (MainWindow.HasQuotaReading).
        var entry = settings.EnabledApps.Single();
        check("Claude upgrade cache: the application has a reading, so the collapsed row draws its ring", DashboardUsage.Quota(entry, snapshots).Metrics.Count > 0);
        // The interface answers: the reading is kept under the organization's name, and the earlier version's no longer stands for it.
        handler.Answers = true;
        clock = clock.AddHours(1);
        var online = (await service.RefreshAsync(settings)).Single();
        var file = File.ReadAllText(cache);
        check("Claude upgrade cache: an answer shows the new reading and is kept under the organization, the earlier reading gone",
            online.LiveQuota && !online.IsStale && online.Quotas.Any(q => q.Minutes == 300 && q.UsedPercent == 40) &&
            ClaudeQuotaCache.Parse(file, clock, "account-a", "org-personal") is { Quotas.Count: 2 } &&
            ClaudeQuotaCache.Parse(file, clock, "account-a", "org-team", earlierReading: true) is null);
    }
}

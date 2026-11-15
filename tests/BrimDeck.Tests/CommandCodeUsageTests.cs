using System.Net;
using System.Text.Json;
using BrimDeck.Core;

// Command Code keeps its sign-in (auth.json, apiKey) and session records (projects\<project>\<session>.jsonl) in
// %USERPROFILE%\.commandcode for both its CLI and desktop app. The fixtures follow the files and replies Command Code 1.88 wrote and
// sent on 2026-11-13.
static class CommandCodeUsageTests
{
    private const string CreditsJson = """
        {"credits":{"belowThreshold":false,"creditThreshold":0,"monthlyCredits":9.990768172,"purchasedCredits":0,"freeCredits":0},
         "windowLimits":{"limited":true,"exceeded":null,"fiveHour":{"used":0.75,"cap":3,"exceeded":false,"resetAt":1794578904986},
                         "weekly":{"used":0.009231828,"cap":6,"exceeded":false,"resetAt":1795165704986}},"sandboxAccess":false,"sandboxMinutes":null}
        """;
    private const string SubscriptionJson = """
        {"success":true,"data":{"status":"active","orgId":null,"cancelAtPeriodEnd":true,"currentPeriodStart":"2026-11-13T08:47:14.000Z",
         "currentPeriodEnd":"2026-12-13T08:47:14.000Z","planId":"individual-go-v1"}}
        """;
    private const string SummaryJson = """
        {"totalCount":6,"totalCost":0.009231828,"totalCredits":0.009231828,"totalFreeCredits":0,"totalMonthlyCredits":0.009231828,
         "totalPurchasedCredits":0,"periodBasis":"billing-period","periodStart":"2026-11-13T08:47:14.000Z","daily":[]}
        """;
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }

    public static async Task Run(string root, Action<string, bool> check)
    {
        Catalog(root, check);
        Records(root, check);
        Prices(root, check);
        Quota(check);
        await Service(root, check);
        await KeyEnteredByHand(check);
    }

    private static void Catalog(string root, Action<string, bool> check)
    {
        check("Command Code is an automatic quota and statistics source saved as commandcode",
            ProviderCatalog.IsBuiltIn(ProviderId.CommandCode) && ProviderCatalog.IsUsageSource(ProviderId.CommandCode) && ProviderCatalog.Key(ProviderId.CommandCode) == "commandcode" &&
            ProviderCatalog.Name(ProviderId.CommandCode) == "Command Code" && (int)ProviderId.CommandCode == 22 && PanelThemes.AppKey(ProviderId.CommandCode) == "commandcode");
        var manual = ProviderCatalog.Sources.Where(id => !ProviderCatalog.IsBuiltIn(id)).ToList();
        check("Command Code API is entered by hand right after OpenCode Go, saved as commandcode-api, in the color of sources entered by hand",
            !ProviderCatalog.IsBuiltIn(ProviderId.CommandCodeApi) && !ProviderCatalog.IsUsageSource(ProviderId.CommandCodeApi) && !ProviderCatalog.NeedsSite(ProviderId.CommandCodeApi) &&
            manual.IndexOf(ProviderId.CommandCodeApi) == manual.IndexOf(ProviderId.OpenCodeGo) + 1 && ProviderCatalog.Key(ProviderId.CommandCodeApi) == "commandcode-api" &&
            (int)ProviderId.CommandCodeApi == 23 && AppPresets.ThemeColor(ProviderId.CommandCodeApi) == AppPresets.ManualColor && PanelThemes.AppKey(ProviderId.CommandCodeApi) == "manual" &&
            JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.CommandCodeApi }] }))!.Apps.Single().QuotaSource == ProviderId.CommandCodeApi);
        check("Every built-in theme gives Command Code its own color", PanelThemes.BuiltIns.All(theme =>
            new[] { false, true }.All(night => PanelThemes.Resolve(theme, night).Apps.ContainsKey("commandcode"))));
        var profile = Path.Combine(root, "commandcode-user");
        check("Command Code keeps its data in .commandcode in the user profile", DataLocations.Resolve(profile, root, root, null, null).CommandCodeHome == Path.Combine(profile, ".commandcode"));

        var home = Path.Combine(root, "commandcode-key"); Directory.CreateDirectory(home);
        check("Without auth.json Command Code is not signed in", CommandCodeUsage.ReadKey(home) is null);
        File.WriteAllText(Path.Combine(home, "auth.json"), """{"apiKey":" user_fixture ","userId":"u","userName":"n","keyName":"cli-2026-11-13","authenticatedAt":"2026-11-13T09:04:00.000Z"}""");
        check("The sign-in is the apiKey in auth.json", CommandCodeUsage.ReadKey(home) == "user_fixture");
        File.WriteAllText(Path.Combine(home, "auth.json"), "{bad");
        check("A broken auth.json is not a sign-in", CommandCodeUsage.ReadKey(home) is null);
        File.WriteAllText(Path.Combine(home, "auth.json"), """{"apiKey":""}""");
        check("An empty key is not a sign-in", CommandCodeUsage.ReadKey(home) is null);
    }

    private static void Records(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "commandcode-records");
        var project = Path.Combine(home, "projects", "d-projects-brim-deck");
        Directory.CreateDirectory(project);
        var now = DateTimeOffset.Now;
        var time = now.AddMinutes(-1).ToString("O");
        string Reply(string id, string messageId, long input, long output, long read, long write, long hour, double? cost) => JsonSerializer.Serialize(new
        {
            type = "message", id, parentId = "p", timestamp = time,
            message = new { role = "assistant", content = new[] { new { type = "text", text = "你好" } }, meta = new { source = "model", messageId } },
            usage = new Dictionary<string, object?> { ["inputTokens"] = input, ["outputTokens"] = output, ["cacheReadTokens"] = read, ["cacheWriteTokens"] = write,
                ["cacheWriteTokens1h"] = hour == 0 ? null : hour, ["costUsd"] = cost },
            model = "deepseek/deepseek-v4.1-flash"
        });
        var user = JsonSerializer.Serialize(new { type = "message", id = "u1", timestamp = time, message = new { role = "user", content = new[] { new { type = "text", text = "usage" } }, meta = new { source = "user", messageId = "m-user" } } });
        File.WriteAllText(Path.Combine(project, "s1.jsonl"),
            JsonSerializer.Serialize(new { type = "session", version = 3, id = "s1", timestamp = time, cwd = "D:\\Projects\\BrimDeck" }) + "\n" + user + "\n" +
            Reply("a1", "m1", 21204, 28, 4096, 0, 0, 0.002595288) + "\n" + Reply("a2", "m2", 10000, 100, 2000, 3000, 1000, null) + "\n");
        // A forked session copies the earlier messages with their message ids.
        File.WriteAllText(Path.Combine(project, "s2.jsonl"), Reply("b1", "m1", 21204, 28, 4096, 0, 0, 0.002595288) + "\n");
        File.WriteAllText(Path.Combine(project, "s1.checkpoints.jsonl"), """{"id":"c1","messageId":"c1","turnNumber":1,"createdAt":"2026-11-13T09:08:17.060Z","prompt":"usage","messageCount":0,"files":[]}""" + "\n");

        var data = new LogReader().Read(ProviderId.CommandCode, home, now);
        var first = data.Entries.SingleOrDefault(e => e.Key == "cmd:m1");
        var second = data.Entries.SingleOrDefault(e => e.Key == "cmd:m2");
        check("Command Code counts each reply once, also when a forked session copies it", data.UsageAvailable && data.Entries.Count == 2);
        check("Command Code input includes cache reads and writes, which are counted apart", first is { Input: 17108, CacheRead: 4096, CacheWrite: 0, Output: 28 } &&
            second is { Input: 5000, CacheRead: 2000, CacheWrite: 2000, CacheWriteHour: 1000, Output: 100 } && first.Total == 21232);
        check("Command Code keeps the cost it recorded and the model it used", first!.ReportedCostUsd == 0.002595288m && second!.ReportedCostUsd is null &&
            data.Entries.All(e => e.Model == "deepseek/deepseek-v4.1-flash"));
    }

    // The catalog follows the one Command Code 1.88 cached on 2026-11-13, trimmed to the models the checks need.
    private static void Prices(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "commandcode-prices");
        check("Without a cached catalog Command Code lists no prices", CommandCodeUsage.ReadPrices(home).Count == 0);
        var folder = Path.Combine(home, "cache", "model-catalog", "v1"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "catalog-a813c4ec6551f9c3.json"), """
            {"cacheVersion":3,"scope":{"endpointHash":"a813","schemaVersion":1},"fetchedAt":1794571510081,"renderedAt":1794498763127,"body":{"data":[
             {"id":"deepseek/deepseek-v4.1-flash","aliases":[],"pricing":{"cacheReadCost":0.003,"inputCost":0.15,"outputCost":0.6}},
             {"id":"claude-sonnet-4-6","aliases":["claude-sonnet-4-20250514"],"pricing":{"cacheReadCost":0.3,"cacheWrite1hCost":6,"cacheWriteCost":3.75,"inputCost":3,"outputCost":15}},
             {"id":"Qwen/Qwen3.7-Flash","aliases":[],"pricing":{"cacheReadCost":0.006,"cacheWriteCost":0.038,"inputCost":0.03,"outputCost":0.13,"contextTiers":[
               {"maxContext":32000,"rates":{"cacheReadCost":0.006,"cacheWriteCost":0.038,"inputCost":0.03,"outputCost":0.13}},
               {"maxContext":256000,"rates":{"cacheReadCost":0.02,"cacheWriteCost":0.125,"inputCost":0.1,"outputCost":0.4}},
               {"rates":{"cacheReadCost":0.04,"cacheWriteCost":0.25,"inputCost":0.2,"outputCost":0.8}}]}},
             {"id":"minimax/minimax-m3-free","aliases":[]}]}}
            """);
        File.WriteAllText(Path.Combine(folder, "catalog-0000000000000000.json"), """
            {"fetchedAt":1700000000000,"body":{"data":[{"id":"deepseek/deepseek-v4.1-flash","aliases":[],"pricing":{"cacheReadCost":1,"inputCost":1,"outputCost":1}}]}}
            """);
        File.WriteAllText(Path.Combine(folder, "catalog-ffffffffffffffff.json"), "{bad");
        var prices = CommandCodeUsage.ReadPrices(home);
        var flash = prices.GetValueOrDefault("deepseek/deepseek-v4.1-flash");
        check("Command Code's prices come from the catalog it fetched last, per token", flash is { Tiers.Count: 0 } &&
            flash.Prices == new TokenPrices(0.15m / 1_000_000, 0.6m / 1_000_000, 0.003m / 1_000_000, null, null));
        check("The listed prices reproduce the cost Command Code recorded for a reply",
            (18097 - 6528) * flash!.Prices.Input + 6528 * flash.Prices.CacheRead!.Value + 10 * flash.Prices.Output == 0.001760934m);
        check("A model is also found by its aliases and without regard to case", ReferenceEquals(prices.GetValueOrDefault("claude-sonnet-4-20250514"), prices.GetValueOrDefault("CLAUDE-SONNET-4-6")) &&
            prices["claude-sonnet-4-6"].Prices is { CacheWrite: 3.75m / 1_000_000, CacheWriteHour: 6m / 1_000_000 });
        var qwen = prices.GetValueOrDefault("Qwen/Qwen3.7-Flash");
        check("Context tiers start just above the previous tier's largest input", qwen is not null && qwen.Tiers.Select(t => t.MinimumPromptTokens).SequenceEqual([0L, 32001L, 256001L]) &&
            qwen.Tiers[2].Prices is { Input: 0.2m / 1_000_000, Output: 0.8m / 1_000_000, CacheWriteHour: 0.25m / 1_000_000 });
        check("A model without prices is not listed", !prices.ContainsKey("minimax/minimax-m3-free") && prices.Count == 4);
    }

    private static void Quota(Action<string, bool> check)
    {
        var reading = CommandCodeUsage.Parse(Json(CreditsJson), Json(SubscriptionJson), Json(SummaryJson));
        check("Command Code shows the 5-hour, weekly and monthly limits", reading.Metrics.Select(m => m.Label).SequenceEqual(["5 小时额度", "每周额度", "每月额度"]) &&
            reading.Metrics.Select(m => m.Window).SequenceEqual(new int?[] { 300, 10080, null }) && reading.Metrics.All(m => m.Kind == MetricKind.Percent));
        check("Command Code windows are spent credits out of their cap, reset at the time given", reading.Metrics[0] is { Percent: 25, Note: "$0.75 / $3.00" } &&
            reading.Metrics[0].ResetAt == DateTimeOffset.FromUnixTimeMilliseconds(1794578904986));
        check("The month is what the billing period spent out of that and what is left, reset at the period's end",
            Math.Abs(reading.Metrics[2].Percent!.Value - 0.09231828) < 1e-6 && reading.Metrics[2].Note == "$0.01 / $10.00" &&
            reading.Metrics[2].ResetAt == DateTimeOffset.Parse("2026-12-13T08:47:14Z"));
        check("The plan is named without its version", reading.Plan == "Go" && reading.Warnings.Count == 0 &&
            CommandCodeUsage.PlanName("individual-goat") == "GOAT" && CommandCodeUsage.PlanName("individual-pro-v1") == "Pro" &&
            CommandCodeUsage.PlanName("teams-pro") == "Teams Pro" && CommandCodeUsage.PlanName("individual-provider") == "Provider" && CommandCodeUsage.PlanName("") == "");

        var spent = CommandCodeUsage.Parse(Json(CreditsJson.Replace("\"purchasedCredits\":0", "\"purchasedCredits\":12.5").Replace("\"freeCredits\":0", "\"freeCredits\":2.5")
            .Replace("\"exceeded\":false,\"resetAt\":1794578904986", "\"exceeded\":true,\"resetAt\":1794578904986")), Json(SubscriptionJson), default);
        check("Without the period's spending the month shows what is left; extra credits are shown when there are any",
            spent.Metrics.Single(m => m.Id == "monthly") is { Kind: MetricKind.Balance, Amount: 9.990768172m, Currency: "USD" } &&
            spent.Metrics.Single(m => m.Id == "extra") is { Kind: MetricKind.Balance, Label: "额外积分", Amount: 15m });
        check("A used-up window is told", spent.Warnings.Single().Contains("5 小时"));
        var payAsYouGo = CommandCodeUsage.Parse(Json("""{"credits":{"monthlyCredits":0,"purchasedCredits":0,"freeCredits":0},"windowLimits":{"limited":false}}"""),
            Json("""{"data":{"status":"canceled","planId":"individual-provider"}}"""), Json("""{"totalMonthlyCredits":0}"""));
        check("An account with nothing left still shows its extra credits, and an ended plan is not named",
            payAsYouGo.Metrics.Single() is { Id: "extra", Amount: 0m } && payAsYouGo.Plan == "");
        bool unrecognized; try { CommandCodeUsage.Parse(Json("{}"), default, default); unrecognized = false; } catch (InvalidDataException) { unrecognized = true; }
        check("A reply without credits is not taken for an empty quota", unrecognized);
    }

    private static async Task Service(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "commandcode-service"); Directory.CreateDirectory(home);
        var now = DateTimeOffset.Now;
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.CommandCode, UsageSource = ProviderId.CommandCode }] };
        settings.Normalize();
        async Task<(ProviderSnapshot Snapshot, Api Handler)> Refresh(Api handler)
        {
            using var http = new HttpClient(handler);
            using var service = new UsageService(new Desktop(), new DataLocations("", "", "", "", "") { CommandCodeHome = home }, http, () => now);
            return ((await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.CommandCode), handler);
        }
        var (snapshot, handler) = await Refresh(new Api("user_fixture"));
        check("Without a Command Code sign-in nothing is sent", handler.Paths.Count == 0 && !snapshot.LiveQuota && snapshot.StatusLabel == Loc.T("未连接", "Not connected"));
        check("Without a cached catalog the snapshot lists no prices", snapshot.ListedPrices is null);
        File.WriteAllText(Path.Combine(home, "auth.json"), """{"apiKey":"user_fixture"}""");
        Directory.CreateDirectory(Path.Combine(home, "cache", "model-catalog", "v1"));
        File.WriteAllText(Path.Combine(home, "cache", "model-catalog", "v1", "catalog-a813c4ec6551f9c3.json"),
            """{"fetchedAt":1794571510081,"body":{"data":[{"id":"z-ai/glm-5.3-flash","aliases":[],"pricing":{"cacheReadCost":0.03,"inputCost":0.15,"outputCost":0.5}}]}}""");
        (snapshot, handler) = await Refresh(new Api("user_fixture"));
        check("The snapshot carries the prices Command Code lists for its models", snapshot.ListedPrices?.ContainsKey("z-ai/glm-5.3-flash") == true);
        check("Command Code reads its account endpoints with the signed-in key as a bearer token, asking for the period's spending since it began",
            handler.Paths.Take(1).SequenceEqual(["/alpha/whoami"]) && handler.Paths.Count == 4 && handler.Paths.Last() == "/alpha/usage/summary?since=2026-11-13T08%3A47%3A14.000Z" &&
            handler.Hosts.All(host => host == "api.commandcode.ai") && handler.Keys.All(key => key == "Bearer user_fixture"));
        check("Command Code shows the quota of the signed-in account with its plan", snapshot is { LiveQuota: true, Plan: "Go", Scope: "account" } && snapshot.Metrics.Count == 3);
        (snapshot, handler) = await Refresh(new Api("user_fixture", organization: "org_1"));
        check("A team account is read for its organization", handler.Paths.Skip(1).All(path => path.Contains("orgId=org_1")));
        (snapshot, handler) = await Refresh(new Api("user_other"));
        check("A refused sign-in asks to sign in to Command Code again", handler.Paths.Count == 1 && !snapshot.LiveQuota && snapshot.StatusLabel == Loc.T("登录已失效", "Sign-in expired") &&
            snapshot.Status.Contains("Command Code"));
        (snapshot, _) = await Refresh(new Api("user_fixture", subscriptionStatus: HttpStatusCode.InternalServerError));
        check("A failing plan read still shows the quota", snapshot.LiveQuota && snapshot.Plan == "" && snapshot.Metrics.Count == 3);
    }

    private static async Task KeyEnteredByHand(Action<string, bool> check)
    {
        var entry = new AppEntry { QuotaSource = ProviderId.CommandCodeApi, SecretRevision = Guid.NewGuid() };
        var secrets = new Secrets(); secrets.Write(entry.InstanceId, "user_typed");
        var handler = new Api("user_typed");
        using var http = new HttpClient(handler);
        using var providers = new ConfiguredProviders(secrets, http, () => DateTimeOffset.Now);
        var snapshot = (await providers.TestAsync(entry)).Snapshot;
        check("A Command Code API key reads the same quota from api.commandcode.ai only", snapshot is { LiveQuota: true, Plan: "Go", Scope: "account", Source: "Command Code API" } &&
            snapshot.Metrics.Count == 3 && handler.Hosts.All(host => host == "api.commandcode.ai") && handler.Keys.All(key => key == "Bearer user_typed"));
        secrets.Write(entry.InstanceId, "user_wrong");
        snapshot = (await providers.TestAsync(entry)).Snapshot;
        check("A rejected Command Code API key explains what to check without showing the key", snapshot is { LiveQuota: false, StatusLabel: "密钥无效" } &&
            snapshot.Status.Contains("Command Code Studio") && !snapshot.Status.Contains("user_wrong"));
    }

    // Answers like api.commandcode.ai for the one key it accepts.
    sealed class Api(string key, string? organization = null, HttpStatusCode subscriptionStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public readonly List<string> Paths = [], Hosts = [], Keys = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (Paths) { Paths.Add(path); Hosts.Add(request.RequestUri.Host); Keys.Add(request.Headers.Authorization?.ToString() ?? ""); }
            if (request.Headers.Authorization?.ToString() != "Bearer " + key)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"Unauthorized"}""") });
            var (status, body) = request.RequestUri.AbsolutePath switch
            {
                "/alpha/whoami" => (HttpStatusCode.OK, organization is null ? """{"success":true,"user":{"id":"u"},"org":null}""" : $$$"""{"success":true,"user":{"id":"u"},"org":{"id":"{{{organization}}}"}}"""),
                "/alpha/billing/credits" => (HttpStatusCode.OK, CreditsJson),
                "/alpha/billing/subscriptions" => (subscriptionStatus, SubscriptionJson),
                "/alpha/usage/summary" => (HttpStatusCode.OK, SummaryJson),
                _ => (HttpStatusCode.NotFound, "{}")
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
    sealed class Desktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unexpected credential read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unexpected service discovery");
    }
    sealed class Secrets : IProviderSecrets
    {
        private readonly Dictionary<Guid, string> _values = [];
        public string Read(Guid instanceId) => _values.GetValueOrDefault(instanceId, "");
        public void Write(Guid instanceId, string secret) => _values[instanceId] = secret;
        public void Delete(Guid instanceId) => _values.Remove(instanceId);
    }
}

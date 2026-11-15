using System.Net;
using System.Text.Json;
using BrimDeck.Core;

// Xiaomi MiMo API and Xiaomi MiMo Token Plan: Open Platform keys entered by hand. The fixtures are what api.xiaomimimo.com answered for
// a pay-as-you-go key, and token-plan-cn.xiaomimimo.com for a Token Plan key, on 2026-11-13.
static class MiMoApiTests
{
    private const string BalanceJson = """{"object":"balance","is_available":true,"balance_infos":[{"currency":"CNY","cash_balance":"1.00","gift_balance":"4.99","frozen_balance":"0.00","credit_limit":"0.00","credit_available":"0.00","total_available":"5.99"}]}""";
    private const string DailyJson = """{"object":"list","data":[{"date":"2026-11-13","resource_type":"model","name":"mimo-v2.6-flash","currency":"CNY","consumed_amount":"0.009298","total_tokens":25241,"request_count":2},{"date":"2026-11-12","name":"mimo-v2.6-pro","currency":"CNY","consumed_amount":"0.5"}]}""";
    private const string PlanJson = """{"object":"token_plan","plan_code":"lite","period":{"start_at":"2026-11-13 12:36:00","end_at":"2026-12-13 23:59:59"},"quota":{"total_credits":4100000000,"used_credits":410000000,"remaining_credits":3690000000},"auto_renew":true,"claw_enabled":false}""";
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }

    public static async Task Run(string root, Action<string, bool> check)
    {
        await Linked(root, check);
        var manual = ProviderCatalog.Sources.Where(id => !ProviderCatalog.IsBuiltIn(id)).ToList();
        var menu = ProviderCatalog.Sources.ToList();
        check("Xiaomi MiMo, which needs a sign-in in settings, is listed with the sources entered by hand, right before Xiaomi MiMo API",
            ProviderCatalog.Group(ProviderId.MiMo) == ProviderCatalog.Group(ProviderId.MiMoApi) && menu.IndexOf(ProviderId.MiMoApi) == menu.IndexOf(ProviderId.MiMo) + 1 &&
            menu.IndexOf(ProviderId.MiMo) == menu.IndexOf(ProviderId.GlmGlobal) + 1 && ProviderCatalog.IsBuiltIn(ProviderId.MiMo) && menu.Count(id => id == ProviderId.MiMo) == 1);
        check("Xiaomi MiMo API and Xiaomi MiMo Token Plan are entered by hand after Z.ai GLM and before DeepSeek, in the color of sources entered by hand",
            manual.IndexOf(ProviderId.MiMoApi) == manual.IndexOf(ProviderId.GlmGlobal) + 1 && manual.IndexOf(ProviderId.MiMoTokenPlan) == manual.IndexOf(ProviderId.MiMoApi) + 1 &&
            manual.IndexOf(ProviderId.DeepSeek) == manual.IndexOf(ProviderId.MiMoTokenPlan) + 1 &&
            ProviderCatalog.Name(ProviderId.MiMoTokenPlan) == "Xiaomi MiMo Token Plan" && ProviderCatalog.Key(ProviderId.MiMoTokenPlan) == "mimo-token-plan" && (int)ProviderId.MiMoTokenPlan == 28 &&
            !ProviderCatalog.NeedsSite(ProviderId.MiMoTokenPlan) && PanelThemes.AppKey(ProviderId.MiMoTokenPlan) == "manual" &&
            ProviderCatalog.Name(ProviderId.MiMoApi) == "Xiaomi MiMo API" && ProviderCatalog.Key(ProviderId.MiMoApi) == "mimo-api" && (int)ProviderId.MiMoApi == 27 &&
            !ProviderCatalog.NeedsSite(ProviderId.MiMoApi) && !ProviderCatalog.IsUsageSource(ProviderId.MiMoApi) && ProviderCatalog.HasAccountBalance(ProviderId.MiMoApi) &&
            AppPresets.ThemeColor(ProviderId.MiMoApi) == AppPresets.ManualColor && PanelThemes.AppKey(ProviderId.MiMoApi) == "manual");

        var balance = MiMoUsage.Balance(Json(BalanceJson), out bool available);
        check("The balance shows the total, the topped-up and the granted amounts; a frozen amount of 0 is left out",
            available && balance.Select(m => (m.Label, m.Amount, m.Currency)).SequenceEqual([("账户余额", 5.99m, "CNY"), ("充值余额", 1.00m, "CNY"), ("赠送余额", 4.99m, "CNY")]));
        check("The account balance can take a target balance", ProviderCatalog.IsAccountBalance(balance[0]));
        var now = new DateTimeOffset(2026, 11, 13, 21, 0, 0, TimeSpan.FromHours(8));
        var month = MiMoUsage.MonthSpend(Json(DailyJson), "CNY", now);
        check("This month's charges add up every day and model and reset when the next UTC month starts",
            month is { Kind: MetricKind.Spend, Label: "本月扣费", Amount: 0.509298m } && month.ResetAt == new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        check("The month is asked for in UTC dates", MiMoUsage.UsageQuery(now) == "start_date=2026-11-01&end_date=2026-11-13");
        var (planName, plan) = MiMoUsage.TokenPlan(Json(PlanJson))!.Value;
        check("A Token Plan names its plan and shows its Credits used of the total until the period ends, its time read as UTC",
            planName == "Lite" && plan is { Kind: MetricKind.Count, Label: "套餐额度", Total: 4100000000m, Used: 410000000m, Unit: "Credits" } &&
            Math.Abs(plan.UsedPercent!.Value - 10) < 1e-9 && plan.ResetAt == new DateTimeOffset(2026, 12, 13, 23, 59, 59, TimeSpan.Zero));
        check("Without plan_code there is no Token Plan", MiMoUsage.TokenPlan(Json("""{"plan_code":null}""")) is null);

        var entry = new AppEntry { QuotaSource = ProviderId.MiMoApi, SecretRevision = Guid.NewGuid() };
        var secrets = new Secrets(); secrets.Write(entry.InstanceId, "sk-typed");
        var handler = new Api();
        using var http = new HttpClient(handler);
        using var providers = new ConfiguredProviders(secrets, http, () => now);
        var snapshot = (await providers.TestAsync(entry)).Snapshot;
        check("A pay-as-you-go key reads the balances and this month's charges from api.xiaomimimo.com only",
            snapshot is { LiveQuota: true, Scope: "account" } && snapshot.Metrics.Select(m => m.Label).SequenceEqual(["账户余额", "充值余额", "赠送余额", "本月扣费"]) &&
            handler.Hosts.All(host => host == "api.xiaomimimo.com") && handler.Keys.All(key => key == "Bearer sk-typed") &&
            handler.Paths.Contains("/v1/platform/usage/daily?start_date=2026-11-01&end_date=2026-11-13"));
        handler.Hosts.Clear(); secrets.Write(entry.InstanceId, "tp-typed");
        snapshot = (await providers.TestAsync(entry)).Snapshot;
        check("A Token Plan key entered as Xiaomi MiMo API points to Xiaomi MiMo Token Plan and is not sent", !snapshot.LiveQuota && snapshot.Status.Contains("Xiaomi MiMo Token Plan") && handler.Hosts.Count == 0);
        var plans = new AppEntry { QuotaSource = ProviderId.MiMoTokenPlan, SecretRevision = Guid.NewGuid() };
        secrets.Write(plans.InstanceId, "tp-typed");
        snapshot = (await providers.TestAsync(plans)).Snapshot;
        check("A Token Plan key is tried at each cluster until one takes it, and shows its plan", snapshot is { LiveQuota: true, Plan: "Lite" } && snapshot.Metrics.Single().Label == "套餐额度" &&
            handler.Hosts.SequenceEqual(["token-plan-cn.xiaomimimo.com", "token-plan-sgp.xiaomimimo.com"]));
        handler.Hosts.Clear(); secrets.Write(plans.InstanceId, "sk-typed");
        snapshot = (await providers.TestAsync(plans)).Snapshot;
        check("A pay-as-you-go key entered as Token Plan points to Xiaomi MiMo API and is not sent", !snapshot.LiveQuota && snapshot.Status.Contains("Xiaomi MiMo API") && handler.Hosts.Count == 0);
        secrets.Write(plans.InstanceId, "tp-wrong");
        snapshot = (await providers.TestAsync(plans)).Snapshot;
        check("A Token Plan key no cluster takes is rejected", !snapshot.LiveQuota && snapshot.StatusLabel == "密钥无效" && !snapshot.Status.Contains("tp-wrong"));
        secrets.Write(entry.InstanceId, "sk-wrong");
        snapshot = (await providers.TestAsync(entry)).Snapshot;
        check("A rejected key explains what to check without showing the key", snapshot is { LiveQuota: false, StatusLabel: "密钥无效" } &&
            snapshot.Status.Contains("开放平台") && !snapshot.Status.Contains("sk-wrong"));
    }

    // Answers like api.xiaomimimo.com for sk-typed and like the Singapore Token Plan cluster for tp-typed.
    // Xiaomi MiMo: the keys Xiaomi MiMo Desktop and MiMo Code linked, in auth.json beside mimocode.db, read again on every refresh.
    private static async Task Linked(string root, Action<string, bool> check)
    {
        check("Xiaomi MiMo is read automatically, saved as mimo-linked, in MiMo's color, its statistics MiMo's",
            ProviderCatalog.IsBuiltIn(ProviderId.MiMoLinked) && ProviderCatalog.Group(ProviderId.MiMoLinked) == ProviderCatalog.Group(ProviderId.Claude) &&
            !ProviderCatalog.IsUsageSource(ProviderId.MiMoLinked) && ProviderCatalog.Name(ProviderId.MiMoLinked) == "Xiaomi MiMo" && ProviderCatalog.Key(ProviderId.MiMoLinked) == "mimo-linked" &&
            (int)ProviderId.MiMoLinked == 29 && PanelThemes.AppKey(ProviderId.MiMoLinked) == "mimo" && ProviderCatalog.HasAccountBalance(ProviderId.MiMoLinked) &&
            new AppEntry { QuotaSource = ProviderId.MiMoLinked }.UsageSource == ProviderId.MiMo);
        var data = Path.Combine(root, "mimo-linked"); Directory.CreateDirectory(data);
        var database = Path.Combine(data, "mimocode.db"); var auth = Path.Combine(data, "auth.json");
        check("Without auth.json nothing is linked", MiMoUsage.ReadLinkedKeys(database).Count == 0);
        const string Both = """
            {"xiaomi":{"type":"api","key":"sk-typed","metadata":{"uid":"1","base_url":"https://api.xiaomimimo.com/v1","platform_linked":true}},
             "xiaomi-token-plan-sgp":{"type":"api","key":"tp-typed","metadata":{"uid":"1","base_url":"https://token-plan-sgp.xiaomimimo.com/v1","platform_linked":true}},
             "xiaomi-other":{"type":"api","key":"sk-elsewhere","metadata":{"base_url":"https://example.com/v1"}},
             "xiaomi-plain":{"type":"api","key":"sk-plain","metadata":{"base_url":"http://api.xiaomimimo.com/v1"}},
             "openai":{"type":"api","key":"sk-openai","metadata":{"base_url":"https://api.xiaomimimo.com/v1"}}}
            """;
        File.WriteAllText(auth, Both);
        var keys = MiMoUsage.ReadLinkedKeys(database);
        check("The linked Xiaomi keys are read with their hosts, the Token Plan first; other hosts, plain HTTP and other providers are left out",
            keys.Count == 2 && keys[0] is { Key: "tp-typed", TokenPlan: true } && keys[0].Host.AbsoluteUri == "https://token-plan-sgp.xiaomimimo.com/" &&
            keys[1] is { Key: "sk-typed", TokenPlan: false } && keys[1].Host.AbsoluteUri == "https://api.xiaomimimo.com/" && !keys[0].ToString().Contains("typed"));

        var now = new DateTimeOffset(2026, 11, 13, 21, 0, 0, TimeSpan.FromHours(8));
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.MiMoLinked }] };
        settings.Normalize();
        var handler = new Api();
        using var http = new HttpClient(handler);
        using var service = new UsageService(new NoDesktop(), new DataLocations("", "", "", "", "") { MiMoDatabase = database }, http, () => now);
        async Task<ProviderSnapshot> Refresh() => (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.MiMoLinked);
        var snapshot = await Refresh();
        check("With both keys one column shows the Token Plan, then the balances and this month's charges, with the plan's name",
            snapshot is { LiveQuota: true, Plan: "Lite", Scope: "account" } && snapshot.Metrics.Select(m => m.Label).SequenceEqual(["套餐额度", "账户余额", "充值余额", "赠送余额", "本月扣费"]) &&
            handler.Hosts.SequenceEqual(["token-plan-sgp.xiaomimimo.com", "api.xiaomimimo.com", "api.xiaomimimo.com"]));
        File.WriteAllText(auth, """{"xiaomi":{"type":"api","key":"sk-typed","metadata":{"uid":"1","base_url":"https://api.xiaomimimo.com/v1"}}}""");
        handler.Hosts.Clear();
        snapshot = await Refresh();
        check("The file is read again on every refresh: a Token Plan no longer linked is no longer shown", snapshot is { LiveQuota: true, Plan: "" } &&
            snapshot.Metrics.Select(m => m.Label).SequenceEqual(["账户余额", "充值余额", "赠送余额", "本月扣费"]) && handler.Hosts.All(host => host == "api.xiaomimimo.com"));
        File.WriteAllText(auth, """{"xiaomi":{"type":"api","key":"sk-wrong","metadata":{"base_url":"https://api.xiaomimimo.com/v1"}},"xiaomi-token-plan-sgp":{"type":"api","key":"tp-typed","metadata":{"base_url":"https://token-plan-sgp.xiaomimimo.com/v1"}}}""");
        snapshot = await Refresh();
        check("A refused key is named in the status while the other is still shown", snapshot is { LiveQuota: true } && snapshot.Metrics.Single().Label == "套餐额度" &&
            snapshot.Status.Contains("API") && !snapshot.Status.Contains("sk-wrong"));
        File.Delete(auth);
        snapshot = await Refresh();
        check("Without linked keys Xiaomi MiMo is not connected", !snapshot.LiveQuota && snapshot.StatusLabel == Loc.T("未连接", "Not connected"));
    }

    sealed class NoDesktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unexpected credential read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unexpected service discovery");
    }

    sealed class Api : HttpMessageHandler
    {
        public readonly List<string> Paths = [], Hosts = [], Keys = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!; var key = request.Headers.Authorization?.ToString() ?? "";
            lock (Paths) { Paths.Add(uri.PathAndQuery); Hosts.Add(uri.Host); Keys.Add(key); }
            var body = (uri.Host, uri.AbsolutePath, key) switch
            {
                ("api.xiaomimimo.com", "/v1/platform/balance", "Bearer sk-typed") => BalanceJson,
                ("api.xiaomimimo.com", "/v1/platform/usage/daily", "Bearer sk-typed") => DailyJson,
                ("token-plan-sgp.xiaomimimo.com", "/v1/platform/token_plan", "Bearer tp-typed") => PlanJson,
                _ => null
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"Invalid API Key"}}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    sealed class Secrets : IProviderSecrets
    {
        private readonly Dictionary<Guid, string> _values = [];
        public string Read(Guid instanceId) => _values.GetValueOrDefault(instanceId, "");
        public void Write(Guid instanceId, string secret) => _values[instanceId] = secret;
        public void Delete(Guid instanceId) => _values.Remove(instanceId);
    }
}

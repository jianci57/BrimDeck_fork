using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

static class KimiUsageTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var profile = Path.Combine(root, "kimi-profile");
        var home = Path.Combine(root, "kimi-home");
        Directory.CreateDirectory(home);
        var locations = DataLocations.Resolve(profile, root, root, null, null, null, null, home);
        check("KIMI_CODE_HOME overrides the Kimi Code directory", locations.KimiHome == home &&
            DataLocations.Resolve(profile, root, root, null, null).KimiHome == Path.Combine(profile, ".kimi-code"));
        check("A relative KIMI_CODE_HOME is ignored", DataLocations.Resolve(profile, root, root, null, null, null, null, "relative\\kimi").KimiHome == Path.Combine(profile, ".kimi-code") &&
            DataLocations.Resolve(profile, root, root, null, null, null, null, "  ").KimiHome == Path.Combine(profile, ".kimi-code"));
        check("Kimi Code is an automatic quota and statistics source keyed kimi", ProviderCatalog.IsBuiltIn(ProviderId.Kimi) && ProviderCatalog.IsUsageSource(ProviderId.Kimi) &&
            ProviderCatalog.Key(ProviderId.Kimi) == "kimi" && ProviderCatalog.Name(ProviderId.Kimi) == "Kimi Code" && (int)ProviderId.Kimi == 15 &&
            PanelThemes.AppKey(ProviderId.Kimi) == "kimi" && PanelThemes.AppKeys.Contains("kimi"));

        // Base address: the environment, then the managed provider in config.toml, then the default.
        check("Without configuration the default coding API is used", KimiUsage.BaseUrl(home, null) == KimiUsage.DefaultBaseUrl);
        File.WriteAllText(KimiUsage.Config(home), """
            default_model = "kimi-code/k3.1-pro"
            # [providers."managed:kimi-code"]
            [providers.other]
            base_url = "https://other.example/v1"

            [providers."managed:kimi-code"]
            type = "kimi"
            base_url = "https://api.kimi.ai/coding/v1/" # overseas
            api_key = ""

            [models."kimi-code/k3.1-pro"]
            base_url = "https://wrong.example"
            """);
        check("config.toml names the managed provider's base_url", KimiUsage.BaseUrl(home, null) == "https://api.kimi.ai/coding/v1" &&
            KimiUsage.BaseUrl(home, "") == "https://api.kimi.ai/coding/v1");
        check("KIMI_CODE_BASE_URL takes precedence", KimiUsage.BaseUrl(home, "https://gateway.example/coding/v1") == "https://gateway.example/coding/v1");
        check("A plain HTTP address is passed over, except on this PC", KimiUsage.BaseUrl(home, "http://gateway.example/v1") == "https://api.kimi.ai/coding/v1" &&
            KimiUsage.BaseUrl(home, "http://127.0.0.1:8080/v1") == "http://127.0.0.1:8080/v1" && KimiUsage.BaseUrl(home, "not a url") == "https://api.kimi.ai/coding/v1");
        check("Literal strings and other tables are read correctly", KimiUsage.ConfiguredBaseUrl("[providers.'managed:kimi-code']\nbase_url='https://a.example/v1'") == "https://a.example/v1" &&
            KimiUsage.ConfiguredBaseUrl("[providers.\"managed:kimi-code\".extra]\nbase_url = \"https://b.example\"") is null &&
            KimiUsage.ConfiguredBaseUrl("[[providers]]\nbase_url = \"https://c.example\"") is null);
        File.Delete(KimiUsage.Config(home));
        Regions(home, check);

        // Current plans report shares in usages; limit_month_code is not shown.
        var current = KimiUsage.Usages(Parse("""
            {"limits":[{"window":{"duration":300,"timeUnit":"TIME_UNIT_MINUTE"},"detail":{"limit":"100","used":"31","remaining":"69","resetTime":"2026-10-24T07:48:59.278829Z"}}],
             "usages":{"limit_5h":{"used_ratio":0.306,"reset_time":"2026-10-24T07:48:58Z"},"limit_month_total":{"used_ratio":0.0122,"reset_time":"2026-11-24T00:00:00Z"},"limit_month_code":{"used_ratio":0.5,"reset_time":"2026-11-24T00:00:00Z"}}}
            """));
        check("usages gives the 5-hour and monthly shares", current.Count == 2 &&
            current[0] is { Kind: MetricKind.Percent, Label: "5 小时额度", Window: 300 } && Math.Abs(current[0].Percent!.Value - 30.6) < 1e-9 &&
            current[0].ResetAt == DateTimeOffset.Parse("2026-10-24T07:48:58Z") &&
            current[1] is { Label: "每月额度", Window: 43200 } && Math.Abs(current[1].Percent!.Value - 1.22) < 1e-9 && current[1].ResetAt == DateTimeOffset.Parse("2026-11-24T00:00:00Z"));
        var weekly = KimiUsage.Usages(Parse("""{"usages":{"limit_7d":{"used_ratio":"0.5","reset_time":"2026-10-30T00:00:00Z"},"limit_5h":{"used_ratio":1.2}}}"""));
        check("Older plans' 7-day window is the weekly limit, ordered after 5 hours", weekly.Count == 2 && weekly[0] is { Label: "5 小时额度", Percent: 100 } &&
            weekly[1] is { Label: "每周额度", Window: 10080, Percent: 50 });
        var legacy = KimiUsage.Usages(Parse("""
            {"usage":{"limit":"1000","used":"250","remaining":"750","resetTime":"2026-10-30T00:00:00Z"},
             "limits":[{"window":{"duration":300,"timeUnit":"TIME_UNIT_MINUTE"},"detail":{"limit":"100","remaining":"69","resetTime":"2026-10-24T07:48:59Z"}},
                       {"window":{"duration":2,"timeUnit":"TIME_UNIT_HOUR"},"detail":{"limit":"10","used":"5"}}]}
            """));
        check("Without usages the absolute counts give the shares", legacy.Count == 3 &&
            legacy[0] is { Label: "120 分钟额度", Window: 120, Percent: 50 } &&
            legacy[1] is { Label: "5 小时额度" } && Math.Abs(legacy[1].Percent!.Value - 31) < 1e-9 && legacy[1].ResetAt == DateTimeOffset.Parse("2026-10-24T07:48:59Z") &&
            legacy[2] is { Label: "每周额度", Percent: 25 } && legacy[2].ResetAt == DateTimeOffset.Parse("2026-10-30T00:00:00Z"));
        check("An empty reply means no plan", KimiUsage.Usages(Parse("{}")).Count == 0 && KimiUsage.Usages(Parse("""{"usages":{}}""")).Count == 0 &&
            KimiUsage.Usages(Parse("""{"usages":{"limit_month_code":{"used_ratio":0.1}}}""")).Count == 0);
        bool rejected; try { KimiUsage.Usages(Parse("[]")); rejected = false; } catch (InvalidDataException) { rejected = true; }
        check("A reply that is not an object is a format change", rejected);

        var booster = KimiUsage.Usages(Parse("""
            {"usages":{"limit_5h":{"used_ratio":0.1}},
             "boosterWallet":{"balance":{"type":"BOOSTER","amount":"20000000000","amountLeft":"10000000000"},"monthlyChargeLimitEnabled":true,
               "monthlyChargeLimit":{"currency":"USD","priceInCents":"20000"},"monthlyUsed":{"currency":"USD","priceInCents":"5000"}}}
            """));
        check("The extra-usage wallet shows its balance and the monthly cap", booster.Count == 3 &&
            booster[1] is { Kind: MetricKind.Balance, Label: "加油包", Currency: "USD" } && booster[1].Amount == 100m && booster[1].Total == 200m && booster[1].UsedPercent == 50 &&
            booster[2] is { Kind: MetricKind.Spend, Label: "本月加油包", Currency: "USD" } && booster[2].Amount == 50m && booster[2].Total == 200m);
        var noCap = KimiUsage.Usages(Parse("""{"usages":{"limit_5h":{"used_ratio":0.1}},"boosterWallet":{"balance":{"type":"BOOSTER","amount":"5000000000"},"monthlyChargeLimitEnabled":false}}"""));
        check("A spent wallet without a cap shows a zero balance in the default currency", noCap.Count == 2 && noCap[1] is { Kind: MetricKind.Balance, Currency: "CNY" } && noCap[1].Amount == 0m && noCap[1].Total == 50m);
        check("An empty or foreign wallet is not shown", KimiUsage.Usages(Parse("""{"usages":{"limit_5h":{"used_ratio":0.1}},"boosterWallet":{"balance":{"type":"BOOSTER","amount":"0"}}}""")).Count == 1 &&
            KimiUsage.Usages(Parse("""{"usages":{"limit_5h":{"used_ratio":0.1}},"boosterWallet":{"balance":{"type":"OTHER","amount":"100000000"}}}""")).Count == 1 &&
            KimiUsage.Usages(Parse("""{"boosterWallet":{"balance":{"type":"BOOSTER","amount":"100000000"}}}""")).Count == 0);
        check("The plan name comes from user_level_name only", KimiUsage.Plan(Parse("""{"user_level_name":"Plus","goods_version":"v2","name":"someone"}""")) == "Plus" &&
            KimiUsage.Plan(Parse("{}")) == "");
        check("Kimi Code models are named by their Moonshot API ID when Moonshot prices them under it",
            new[] { ("kimi-code/k3", "kimi-k3"), ("k3", "kimi-k3"), ("kimi-code/k3.1-pro", "kimi-k3.1-pro"), ("K3.1-Pro", "kimi-k3.1-pro"),
                ("kimi-code/k3.1-pro-highspeed", "kimi-k3.1-pro-highspeed"), ("moonshotai/kimi-k3.1-pro", "kimi-k3.1-pro"), (" kimi-k3 ", "kimi-k3") }
            .All(x => KimiUsage.ModelName(x.Item1) == x.Item2));
        check("Kimi Code aliases and models without an API price keep Kimi Code's ID",
            new[] { ("kimi-code/kimi-for-coding", "kimi-for-coding"), ("kimi-code/kimi-for-coding-highspeed", "kimi-for-coding-highspeed"),
                ("kimi-code/k3-256k", "k3-256k"), ("Kimi-Code/K3-256K", "k3-256k"), ("kimi-for-coding", "kimi-for-coding") }
            .All(x => KimiUsage.ModelName(x.Item1) == x.Item2));
        check("An unknown Kimi Code model keeps its ID without the provider", KimiUsage.ModelName("kimi-code/k4-preview") == "k4-preview" &&
            KimiUsage.ModelName("my-provider/Some-Model") == "some-model" && KimiUsage.ModelName("") == "");
        await Prices(root, check);

        Sessions(root, check);
        await Service(root, locations, home, check);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Record(string? agent, string model, long time, long input, long output, long read, long write, string scope = "turn") =>
        "{\"type\":\"usage.record\"" + (agent is null ? "" : ",\"agentId\":\"" + agent + "\"") + ",\"model\":\"" + model +
        $"\",\"usage\":{{\"inputOther\":{input},\"output\":{output},\"inputCacheRead\":{read},\"inputCacheCreation\":{write}}},\"usageScope\":\"{scope}\",\"time\":{time}}}";

    private static void Sessions(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "kimi-sessions");
        // The files are written now, so the records are dated just before.
        var now = DateTimeOffset.Now;
        var start = now.AddDays(-1);
        long t = now.AddMinutes(-30).ToUnixTimeMilliseconds();
        var main = Path.Combine(KimiSessions.Folder(home), "wd_project", "session_a", "agents", "main", "wire.jsonl");
        var sub = Path.Combine(KimiSessions.Folder(home), "wd_project", "session_a", "agents", "sub-1", "wire.jsonl");
        var copy = Path.Combine(KimiSessions.Folder(home), "wd_other", "session_b", "agents", "main", "wire.jsonl");
        var stray = Path.Combine(KimiSessions.Folder(home), "wd_project", "session_a", "wire.jsonl");
        foreach (var file in new[] { main, sub, copy, stray }) Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var first = Record("main", "kimi-code/k3.1-pro", t, 1658, 267, 19712, 0);
        File.WriteAllText(main, string.Join("\n", [
            "\uFEFF{\"type\":\"turn.begin\",\"time\":" + (t - 10) + "}",
            first,
            "{\"type\":\"step.end\",\"usage\":{\"inputOther\":1658,\"output\":267,\"inputCacheRead\":19712,\"inputCacheCreation\":0},\"time\":" + t + "}",
            Record("main", "kimi-code/k3.1-pro", t + 1000, 100, 50, 0, 10, "session"),
            "{\"type\":\"usage.record\",broken",
            ""]));
        File.WriteAllText(sub, Record(null, "kimi-code/k3-mini", t + 2000, 10, 5, 0, 0) + "\n");
        // A copied session repeats the records of the session it came from.
        File.WriteAllText(copy, first + "\n" + Record("main", "kimi-code/k3.1-pro", t - 3 * 86_400_000L, 1, 1, 0, 0) + "\n");
        File.WriteAllText(stray, Record("main", "kimi-code/k3.1-pro", t, 999, 999, 0, 0) + "\n");

        var reader = new KimiSessions(4096);
        var snapshot = new ProviderSnapshot(ProviderId.Kimi);
        reader.Read(snapshot, home, start, now);
        var byModel = snapshot.Entries.GroupBy(e => e.Model).ToDictionary(g => g.Key, g => g.ToList());
        check("Kimi usage records are counted per call, step.end and copies are not", snapshot.Entries.Count == 3 &&
            byModel["kimi-k3.1-pro"].Sum(e => e.Input) == 1758 && byModel["kimi-k3.1-pro"].Sum(e => e.Output) == 317 &&
            byModel["kimi-k3.1-pro"].Sum(e => e.CacheRead) == 19712 && byModel["kimi-k3.1-pro"].Sum(e => e.CacheWrite) == 10 &&
            byModel["k3-mini"].Single().Input == 10);
        check("A broken record makes the statistics incomplete", !snapshot.UsageComplete && snapshot.UsageAvailable && snapshot.UsageNote.Contains("2 个会话"));
        check("Records without agentId take the agent's folder name", snapshot.Entries.Any(e => e.Key.StartsWith("kimi:sub-1:", StringComparison.Ordinal)));

        // New lines are read from where the last read ended; a line still being written waits for its end.
        var added = Record("main", "kimi-code/k3.1-pro", t + 5000, 7, 3, 0, 0);
        File.AppendAllText(main, added[..20]);
        snapshot = new ProviderSnapshot(ProviderId.Kimi); reader.Read(snapshot, home, start, now.AddMinutes(1));
        bool waited = snapshot.Entries.Count == 3;
        File.AppendAllText(main, added[20..] + "\n" + "{\"type\":\"llm.request\",\"body\":\"" + new string('x', 10_000) + "\"}\n" +
            "{\"type\":\"usage.record\",\"padding\":\"" + new string('y', 10_000) + "\"}\n" + Record("main", "kimi-code/k3.1-pro", t + 6000, 1, 1, 0, 0) + "\n");
        snapshot = new ProviderSnapshot(ProviderId.Kimi); reader.Read(snapshot, home, start, now.AddMinutes(1));
        check("Appended lines are read incrementally and a partial line waits", waited && snapshot.Entries.Count == 5 && snapshot.Entries.Count(e => e.Input == 7) == 1);
        check("Oversized lines are skipped; one holding a usage record counts as unreadable", snapshot.UsageNote.Contains("2 处记录未能读取"));
        snapshot = new ProviderSnapshot(ProviderId.Kimi); reader.Read(snapshot, home, start, now.AddMinutes(1));
        check("A second read without changes gives the same result", snapshot.Entries.Count == 5);
        File.WriteAllText(main, first + "\n");
        snapshot = new ProviderSnapshot(ProviderId.Kimi); reader.Read(snapshot, home, start, now.AddMinutes(1));
        check("A shorter file is read again from the start", snapshot.Entries.Count == 2 && snapshot.UsageComplete);
        snapshot = new ProviderSnapshot(ProviderId.Kimi); reader.Read(snapshot, Path.Combine(root, "kimi-missing"), start, now);
        check("Without sessions the statistics are unavailable", !snapshot.UsageAvailable && snapshot.Entries.Count == 0);
    }

    // Kimi Code records take Moonshot AI's own endpoint prices, never the catalog's cheapest host; k3-256k takes K3's; the
    // aliases stay unpriced. Records of other apps keep the catalog price.
    private static async Task Prices(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "kimi-prices"); Directory.CreateDirectory(folder);
        static TokenPrices Price(int n, bool write = false) => new(n * 1e-6m, n * 5e-6m, n * 1e-7m, write ? n * 1.25e-6m : null, null);
        static string Json(TokenPrices p) => $$"""{"prompt":"{{p.Input}}","completion":"{{p.Output}}","input_cache_read":"{{p.CacheRead}}"{{(p.CacheWrite is { } w ? $",\"input_cache_write\":\"{w}\"" : "")}}}""";
        var catalog = new[] { ("moonshotai/kimi-k3", 1), ("moonshotai/kimi-k3:batch", 4), ("moonshotai/kimi-k3.1-pro", 6), ("moonshotai/kimi-k2.7-code", 1) };
        string catalogJson = "{\"data\":[" + string.Join(",", catalog.Select(x => $$"""{"id":"{{x.Item1}}","pricing":{{Json(Price(x.Item2))}}}""")) + "]}";
        static string Endpoint(string provider, string tag, TokenPrices p) => $$"""{"provider_name":"{{provider}}","tag":"{{tag}}","status":0,"pricing":{{Json(p)}}}""";
        static string Endpoints(params string[] items) => "{\"data\":{\"id\":\"x\",\"endpoints\":[" + string.Join(",", items) + "]}}";
        var k3 = Endpoints(Endpoint("Wafer", "wafer", Price(1)), Endpoint("Moonshot AI", "moonshotai/mxfp4", Price(3, true)), Endpoint("Together", "together", Price(3)));
        var k31 = Endpoints(Endpoint("Moonshot AI", "moonshotai/batch", Price(1)), Endpoint("Moonshot AI", "moonshotai/mxfp4", Price(2)),
            Endpoint("Moonshot AI", "moonshotai/highspeed", Price(4)), Endpoint("Fireworks", "fireworks/highspeed", Price(9)));
        bool fail = false, failEndpoints = false;
        using var server = new PriceServer(uri =>
        {
            if (fail || failEndpoints && uri.AbsolutePath.EndsWith("/endpoints", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("") };
            return Reply(uri.AbsolutePath switch
            {
                "/api/v1/models" => catalogJson,
                "/api/v1/models/moonshotai/kimi-k3/endpoints" => k3,
                "/api/v1/models/moonshotai/kimi-k3.1-pro/endpoints" => k31,
                _ => "{}"
            });
        });
        using var http = new HttpClient(server);
        var entry = new TokenEntry("kimi:main:1:x:1000:2000:0:100", DateTimeOffset.Now, "", 1000, 2000, 0, 0, 100);
        static decimal Cost(TokenPrices p, long write = 0) => 1000 * p.Input + 100 * p.Output + 2000 * p.CacheRead!.Value + write * (p.CacheWrite ?? 0);
        string[] unpriced = ["kimi-code/kimi-for-coding", "kimi-code/kimi-for-coding-highspeed", "kimi-code/k4-preview"];

        using (var first = new Pricing(folder, http))
        {
            failEndpoints = true;
            await first.RefreshAsync();
            check("Kimi Code records stay unpriced before Moonshot AI's prices were ever read, while other apps use the catalog",
                first.ModelCount == catalog.Length && first.Estimate(entry with { Model = "kimi-k3" }) is null && first.Estimate(entry with { Model = "k3-256k" }) is null &&
                first.Estimate(new TokenEntry("dsh:x", DateTimeOffset.Now, "moonshotai/kimi-k3", 1000, 2000, 0, 0, 100)) == Cost(Price(1)));
            failEndpoints = false;
            await first.RefreshAsync(force: true);
            decimal? Estimate(string raw, long write = 0) => first.Estimate(entry with { Model = KimiUsage.ModelName(raw), CacheWrite = write });
            check("Kimi K3 takes Moonshot AI's price, not the cheapest host's, with cache writes priced",
                Estimate("kimi-code/k3") == Cost(Price(3, true)) && Estimate("kimi-code/k3", 300) == Cost(Price(3, true), 300));
            check("K3-256k keeps its own name but takes K3's Moonshot AI price", KimiUsage.ModelName("kimi-code/k3-256k") == "k3-256k" &&
                Estimate("kimi-code/k3-256k", 300) == Cost(Price(3, true), 300));
            check("K3.1 Pro and its highspeed endpoint take their own Moonshot AI prices; other tags are left out",
                Estimate("kimi-code/k3.1-pro") == Cost(Price(2)) && Estimate("kimi-code/k3.1-pro-highspeed") == Cost(Price(4)));
            check("Without a cache write price, a record that writes the cache stays unpriced", Estimate("kimi-code/k3.1-pro", 300) is null);
            check("Kimi Code aliases and unknown models are not priced", unpriced.All(raw => Estimate(raw) is null));
            check("Other models keep their catalog prices", first.Estimate(new TokenEntry("dsh:x", DateTimeOffset.Now, "moonshotai/kimi-k2.7-code", 1000, 2000, 0, 0, 100)) == Cost(Price(1)));
            check("K3's price is borrowed only for Kimi Code records", first.Estimate(new TokenEntry("dsh:x", DateTimeOffset.Now, "k3-256k", 1000, 2000, 0, 0, 100)) is null);
            var listed = first.Models.ToDictionary(m => m.Model);
            check("The price list shows Moonshot AI's prices, including a model the catalog lacks", listed["moonshotai/kimi-k3"].Prices == Price(3, true) &&
                listed["moonshotai/kimi-k3.1-pro"].Prices == Price(2) && listed["moonshotai/kimi-k3.1-pro-highspeed"].Prices == Price(4) &&
                listed["moonshotai/kimi-k2.7-code"].Prices == Price(1) && first.ModelCount == catalog.Length + 1);
        }

        // After a restart with no connection, the saved prices still apply; an answer without Moonshot AI keeps them.
        fail = true;
        using var pricing = new Pricing(folder, http);
        await pricing.RefreshAsync();
        decimal? Again(string raw) => pricing.Estimate(entry with { Model = KimiUsage.ModelName(raw) });
        check("Saved Moonshot AI prices apply after a restart when the update fails", Again("kimi-code/k3") == Cost(Price(3, true)) && Again("kimi-code/k3.1-pro-highspeed") == Cost(Price(4)));
        fail = false; k3 = Endpoints(Endpoint("Wafer", "wafer", Price(1)));
        await pricing.RefreshAsync(force: true);
        check("An answer without a Moonshot AI endpoint keeps the saved price", Again("kimi-code/k3") == Cost(Price(3, true)));
        check("Endpoints JSON picks only Moonshot AI endpoints",
            Pricing.ParseOfficial(Parse(k31), "moonshotai/kimi-k3.1-pro").Select(m => m.Model).SequenceEqual(["moonshotai/kimi-k3.1-pro", "moonshotai/kimi-k3.1-pro-highspeed"]) &&
            Pricing.ParseOfficial(Parse("[]"), "moonshotai/kimi-k3").Count == 0);

        check("K3's price edited by hand also applies to k3-256k", pricing.TrySetManual("moonshotai/kimi-k3", Price(7), true, out _) &&
            Again("kimi-code/k3") == Cost(Price(7)) && Again("kimi-code/k3-256k") == Cost(Price(7)));
        check("A price added by hand for k3-256k wins over K3's", pricing.TrySetManual("k3-256k", Price(9), false, out _) &&
            Again("kimi-code/k3-256k") == Cost(Price(9)) && Again("kimi-code/k3") == Cost(Price(7)));
    }

    // What Kimi Code writes when signing in to kimi.ai; the key is the one it derives from auth.kimi.ai and api.kimi.ai
    // (the first 16 hex digits of the SHA-256 of {"oauthHost":"https://auth.kimi.ai","baseUrl":"https://api.kimi.ai/coding/v1"}).
    private const string GlobalKey = "kimi-code-env-0e4f99c69cc27850";
    private const string GlobalConfig = """
        default_model = "kimi-code/kimi-for-coding"
        [providers."managed:kimi-code"]
        base_url = "https://api.kimi.ai/coding/v1"
        type = "kimi"
        api_key = ""
        [providers."managed:kimi-code".oauth]
        storage = "file"
        key = "oauth/kimi-code-env-0e4f99c69cc27850"
        oauth_host = "https://auth.kimi.ai"
        """;
    private const string ChinaConfig = """
        [providers."managed:kimi-code"]
        base_url = "https://api.kimi.com/coding/v1"
        type = "kimi"
        [providers."managed:kimi-code".oauth]
        storage = "file"
        key = "oauth/kimi-code"
        """;

    // kimi.com (mainland China) or kimi.ai (international), as Kimi Code chose when signing in.
    private static void Regions(string home, Action<string, bool> check)
    {
        KimiService Service(KimiEnvironment? environment = null) => KimiUsage.Service(home, environment ?? new());
        check("Without configuration Kimi Code uses kimi.com and its sign-in file", Service() == new KimiService(KimiUsage.DefaultBaseUrl, false, "kimi-code"));
        File.WriteAllText(KimiUsage.Config(home), ChinaConfig);
        check("A kimi.com sign-in is read from kimi-code.json", Service() == new KimiService(KimiUsage.DefaultBaseUrl, false, "kimi-code"));
        File.WriteAllText(KimiUsage.Config(home), GlobalConfig);
        check("A kimi.ai sign-in uses its API and the sign-in file its key names", Service() == new KimiService(KimiUsage.GlobalBaseUrl, true, GlobalKey));
        check("The sign-in file name is derived as Kimi Code derives it", KimiUsage.CredentialKey(KimiUsage.GlobalOAuthHost, KimiUsage.GlobalBaseUrl + "/") == GlobalKey &&
            KimiUsage.CredentialKey(null, KimiUsage.DefaultBaseUrl) == "kimi-code" && KimiUsage.CredentialKey(KimiUsage.DefaultOAuthHost + "/", KimiUsage.DefaultBaseUrl) == "kimi-code");
        File.WriteAllText(KimiUsage.Config(home), "[providers.\"managed:kimi-code\".oauth]\nkey = \"oauth/kimi-code-env-0e4f99c69cc27850\"\noauth_host = \"https://auth.kimi.ai/\"\n");
        check("The kimi.ai sign-in host alone selects kimi.ai's API", Service() == new KimiService(KimiUsage.GlobalBaseUrl, true, GlobalKey));
        File.Delete(KimiUsage.Config(home));
        File.WriteAllText(Path.Combine(home, "region"), "global\n");
        check("Kimi Code's region file selects kimi.ai unless it is turned off", Service() is { BaseUrl: KimiUsage.GlobalBaseUrl, Global: true } &&
            Service(new(RegionMarker: "off")) == new KimiService(KimiUsage.DefaultBaseUrl, false, "kimi-code"));
        File.WriteAllText(KimiUsage.Config(home), ChinaConfig);
        check("A kimi.com sign-in outranks the region file", Service() == new KimiService(KimiUsage.DefaultBaseUrl, false, "kimi-code"));
        File.Delete(KimiUsage.Config(home)); File.Delete(Path.Combine(home, "region"));
        check("KIMI_CODE_OAUTH_HOST selects the region and the sign-in file", Service(new(OAuthHost: "https://auth.kimi.ai")) == new KimiService(KimiUsage.GlobalBaseUrl, true,
            KimiUsage.CredentialKey(KimiUsage.GlobalOAuthHost, KimiUsage.DefaultBaseUrl)));
        check("KIMI_CODE_BASE_URL keeps its address; a Kimi address names its region, another keeps the configured one",
            Service(new("https://api.kimi.ai/coding/v1")) is { BaseUrl: KimiUsage.GlobalBaseUrl, Global: true } &&
            Service(new("https://gateway.example/v1")) is { BaseUrl: "https://gateway.example/v1", Global: false } &&
            Service(new("https://gateway.example/v1", "https://auth.kimi.ai")) is { Global: true, CredentialName: var name } && name.StartsWith("kimi-code-env-", StringComparison.Ordinal));
        check("Only a plain file name is taken from the sign-in key", KimiUsage.CredentialName("oauth/kimi-code") == "kimi-code" && KimiUsage.CredentialName("kimi-code") == "kimi-code" &&
            KimiUsage.CredentialName("oauth/abc") == "abc" && KimiUsage.CredentialName("oauth/../x") is null && KimiUsage.CredentialName("oauth/a/b") is null &&
            KimiUsage.CredentialName("oauth/") is null && KimiUsage.CredentialName("c:\\x") is null);
        check("Extra usage without a currency takes the region's", KimiUsage.Usages(Parse("""{"usages":{"limit_5h":{"used_ratio":0.1}},"boosterWallet":{"balance":{"type":"BOOSTER","amount":"100000000"}}}"""), "USD")
            .Last() is { Kind: MetricKind.Balance, Currency: "USD" });
    }

    // A token shaped like Kimi Code's: a JWT whose payload names the account.
    private static string Jwt(string user, string salt) =>
        "eyJhbGciOiJIUzI1NiJ9." + Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"user_id":"{{user}}","sub":"x","exp":1}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture-" + salt;

    private static async Task Service(string root, DataLocations locations, string home, Action<string, bool> check)
    {
        var sessions = Path.Combine(KimiSessions.Folder(home), "wd_x", "session_x", "agents", "main", "wire.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(sessions)!);
        var now = DateTimeOffset.Parse("2026-10-24T12:00:00+08:00");
        File.WriteAllText(sessions, Record("main", "kimi-code/k3.1-pro", now.AddMinutes(-5).ToUnixTimeMilliseconds(), 100, 20, 1000, 0) + "\n");
        var token = Jwt("user-1", "a");
        void Sign(DateTimeOffset expires, string? value = null) => File.WriteAllText(KimiUsage.Credentials(home),
            $$"""{"access_token":"{{value ?? token}}","refresh_token":"fixture-refresh","expires_at":{{expires.ToUnixTimeSeconds()}},"scope":"kimi-code","token_type":"Bearer","expires_in":900}""");
        check("The account is read from the token's user_id claim", KimiUsage.Account(token) == "user-1" && KimiUsage.Account("not-a-jwt") == "" && KimiUsage.Account("a.!!.b") == "");
        var cache = Path.Combine(root, "kimi-brimdeck", KimiQuotaCache.FileName);

        using var handler = new Handler(); using var http = new HttpClient(handler);
        var service = new UsageService(new NoDesktop(), locations, http, () => now, kimiQuotaCache: cache);
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Kimi }] };
        settings.Normalize();
        check("A Kimi Code row reads its own statistics", settings.Apps[0].UsageSource == ProviderId.Kimi);
        async Task<ProviderSnapshot> Refresh() => (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Kimi);
        const string waiting = "使用 Kimi Code 后自动更新";

        var snapshot = await Refresh();
        check("Without a sign-in Kimi Code asks to sign in and sends nothing", handler.Calls.Count == 0 && snapshot.StatusLabel == Loc.T("未连接", "Not connected") &&
            snapshot.Entries.Count == 1 && snapshot.Entries[0].Model == "kimi-k3.1-pro");

        Directory.CreateDirectory(Path.GetDirectoryName(KimiUsage.Credentials(home))!);
        Sign(now.AddMinutes(-1));
        snapshot = await Refresh();
        check("An expired token with nothing kept sends nothing and waits for Kimi Code, with no bars", handler.Calls.Count == 0 &&
            snapshot.StatusLabel == "等待更新" && snapshot.Status == waiting && snapshot.Metrics.Count == 0 && !snapshot.LiveQuota && !snapshot.Status.Contains("登录"));

        Sign(now.AddMinutes(10));
        handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
            ? Reply("""{"user_level_name":"Plus","goods_version":"2","name":"someone"}""")
            : Reply("""{"usages":{"limit_5h":{"used_ratio":0.306,"reset_time":"2026-10-24T07:48:58Z"},"limit_month_total":{"used_ratio":0.0122,"reset_time":"2026-11-24T00:00:00Z"}},"boosterWallet":{"balance":{"type":"BOOSTER","amount":"5000000000","amountLeft":"2000000000"}}}""");
        snapshot = await Refresh();
        check("The plan quota and membership are read with the Kimi Code sign-in", snapshot.LiveQuota && !snapshot.IsStale && snapshot.Plan == "Plus" && snapshot.Metrics.Count == 3 &&
            snapshot.Metrics[0] is { Label: "5 小时额度" } && snapshot.Metrics[1] is { Label: "每月额度" } && snapshot.Metrics[2] is { Kind: MetricKind.Balance } &&
            handler.Calls.Any(c => c.Uri == KimiUsage.DefaultBaseUrl + "/usages" && c.Authorization == "Bearer " + token) &&
            handler.Calls.Any(c => c.Uri == KimiUsage.DefaultBaseUrl + "/me"));
        check("The token is not written to the snapshot", new[] { snapshot.Status, snapshot.StatusLabel, snapshot.Source, snapshot.Plan, snapshot.PlanSource, snapshot.UsageNote }
            .Concat(snapshot.Metrics.Select(m => m.Description)).All(text => !text.Contains("fixture", StringComparison.Ordinal)));
        check("The Kimi Code sign-in file is left as it was", File.ReadAllText(KimiUsage.Credentials(home)).Contains("fixture-refresh"));
        var stored = File.Exists(cache) ? File.ReadAllText(cache) : "";
        check("The reading is kept on disk without the token, the account id or other /me fields", stored.Contains("Plus") && KimiQuotaCache.Parse(stored, now, KimiQuotaCache.AccountHash(KimiUsage.DefaultBaseUrl, "user-1")) is { Plan: "Plus", Metrics: [{ Label: "5 小时额度" }, _, _] } &&
            !stored.Contains("fixture") && !stored.Contains(token) && !stored.Contains("user-1") && !stored.Contains("someone"));

        int meCalls = handler.Calls.Count(c => c.Uri.EndsWith("/me", StringComparison.Ordinal));
        snapshot = await Refresh();
        check("The membership is kept for a while instead of being read each time", snapshot.Plan == "Plus" &&
            handler.Calls.Count(c => c.Uri.EndsWith("/me", StringComparison.Ordinal)) == meCalls);

        // Expired: the last reading stands in, marked as old, and no request is sent.
        var readAt = now;
        now = now.AddMinutes(30);
        Sign(now.AddMinutes(-5));
        int calls = handler.Calls.Count;
        snapshot = await Refresh();
        check("An expired token shows the kept quota as old, waiting for Kimi Code", handler.Calls.Count == calls && snapshot.IsStale && snapshot.LiveQuota &&
            snapshot.QuotaTime == readAt && snapshot.Metrics.Count == 3 && snapshot.Plan == "Plus" && snapshot.StatusLabel == "等待更新" &&
            snapshot.Status == waiting + $"；显示 {readAt.LocalDateTime:HH:mm} 读取的额度" && !snapshot.Status.Contains("登录"));

        Sign(now.AddMinutes(10));
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
        snapshot = await Refresh();
        check("HTTP 401 shows the kept quota with the same wording", snapshot.IsStale && snapshot.Metrics.Count == 3 && snapshot.Status == waiting + $"；显示 {readAt.LocalDateTime:HH:mm} 读取的额度");
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
        snapshot = await Refresh();
        check("A rate-limited read shows the kept quota and retries", snapshot.IsStale && snapshot.StatusLabel == "稍后重试" && snapshot.Status == $"请求频繁，显示 {readAt.LocalDateTime:HH:mm} 读取的额度，稍后自动重试");
        handler.Respond = _ => Reply("[]");
        snapshot = await Refresh();
        check("A format change shows the kept quota", snapshot.IsStale && snapshot.Status == $"数据格式发生变化，显示 {readAt.LocalDateTime:HH:mm} 读取的额度");

        // After a restart the kept reading comes from the file; the next day the time carries the date.
        service.Dispose();
        now = DateTimeOffset.Parse("2026-10-25T09:00:00+08:00");
        service = new UsageService(new NoDesktop(), locations, http, () => now, kimiQuotaCache: cache);
        Sign(now.AddMinutes(-5));
        snapshot = await Refresh();
        check("After a restart the kept quota is read from disk, without the window that has reset since", snapshot.IsStale && snapshot.Plan == "Plus" &&
            snapshot.Metrics.Count == 2 && snapshot.Metrics[0] is { Label: "每月额度" } && snapshot.Metrics[1] is { Kind: MetricKind.Balance } && snapshot.QuotaTime == readAt &&
            snapshot.Status == waiting + $"；显示 {readAt.LocalDateTime:MM-dd HH:mm} 读取的额度");
        Sign(now.AddMinutes(-5), Jwt("user-2", "b"));
        snapshot = await Refresh();
        check("Another account does not see the kept quota", !snapshot.IsStale && snapshot.Metrics.Count == 0 && snapshot.Status == waiting);
        check("A kept reading that is too old, from the future or reset is not used",
            KimiQuotaCache.Parse(stored, readAt.AddDays(8), KimiQuotaCache.AccountHash(KimiUsage.DefaultBaseUrl, "user-1")) is null &&
            KimiQuotaCache.Parse(stored, readAt.AddHours(-1), KimiQuotaCache.AccountHash(KimiUsage.DefaultBaseUrl, "user-1")) is null &&
            KimiQuotaCache.Parse(stored, DateTimeOffset.Parse("2026-11-25T00:00:00Z"), KimiQuotaCache.AccountHash(KimiUsage.DefaultBaseUrl, "user-1")) is null &&
            KimiQuotaCache.Parse(stored, readAt, KimiQuotaCache.AccountHash(KimiUsage.DefaultBaseUrl, "user-1")) is { Metrics.Count: 3 } &&
            KimiQuotaCache.Parse("{bad", readAt, "x") is null);

        Sign(now.AddMinutes(10));
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.PaymentRequired) { Content = new StringContent("{}") };
        snapshot = await Refresh();
        check("HTTP 402 means no Kimi Code plan, with no bars, and drops the kept quota", snapshot.Status == Loc.T("未订阅 Kimi Code 套餐", "No Kimi Code plan") &&
            snapshot.Metrics.Count == 0 && !File.Exists(cache));
        Sign(now.AddMinutes(-5));
        snapshot = await Refresh();
        check("After 402 an expired token has nothing to show", snapshot.Metrics.Count == 0 && snapshot.Status == waiting);

        Sign(now.AddMinutes(10));
        handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal) ? Reply("{}") : Reply("""{"usages":{}}""");
        snapshot = await Refresh();
        check("An empty usages object means no plan", snapshot.Status == Loc.T("未订阅 Kimi Code 套餐", "No Kimi Code plan") && snapshot.Metrics.Count == 0);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
        snapshot = await Refresh();
        check("HTTP 404 with nothing kept reports the usage API as unavailable", snapshot.StatusLabel == Loc.T("暂不可用", "Unavailable") && snapshot.Metrics.Count == 0);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") };
        snapshot = await Refresh();
        check("Other failures with nothing kept use the shared status", snapshot.StatusLabel == Loc.T("暂不可用", "Unavailable") && snapshot.Metrics.Count == 0 && !snapshot.IsStale);

        // A new sign-in reads the membership again; a failed read leaves the label out.
        Sign(now.AddMinutes(10), Jwt("user-3", "c"));
        handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") }
            : Reply("""{"usages":{"limit_5h":{"used_ratio":0.5}}}""");
        snapshot = await Refresh();
        check("Without the membership the quota still shows, with no plan tag", snapshot.LiveQuota && snapshot.Plan == "" && snapshot.Metrics.Count == 1);

        // Signing in to kimi.ai: config.toml points at its API and names its own sign-in file. The same account id on the
        // other service never shows the reading kept for kimi.com.
        File.WriteAllText(KimiUsage.Config(home), GlobalConfig);
        void SignGlobal(DateTimeOffset expires, string value) => File.WriteAllText(KimiUsage.Credentials(home, GlobalKey),
            $$"""{"access_token":"{{value}}","refresh_token":"fixture-refresh","expires_at":{{expires.ToUnixTimeSeconds()}}}""");
        handler.Calls.Clear();
        snapshot = await Refresh();
        check("A kimi.ai sign-in is not taken from kimi.com's sign-in file", handler.Calls.Count == 0 && snapshot.StatusLabel == Loc.T("未连接", "Not connected"));
        SignGlobal(now.AddMinutes(-1), Jwt("user-3", "g"));
        snapshot = await Refresh();
        check("The reading kept for kimi.com is not shown for the same account on kimi.ai", handler.Calls.Count == 0 && snapshot.Metrics.Count == 0 && snapshot.StatusLabel == "等待更新");
        SignGlobal(now.AddMinutes(10), Jwt("user-3", "g"));
        handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
            ? Reply("""{"user_level_name":"Allegretto","region":"REGION_GLOBAL"}""")
            : Reply("""{"usages":{"limit_5h":{"used_ratio":0.2},"limit_7d":{"used_ratio":0.4}},"boosterWallet":{"balance":{"type":"BOOSTER","amount":"500000000","amountLeft":"100000000"}}}""");
        snapshot = await Refresh();
        check("kimi.ai's API is read with its own sign-in, and the plan names the international service",
            handler.Calls.Count > 0 && handler.Calls.All(c => c.Uri.StartsWith(KimiUsage.GlobalBaseUrl + "/", StringComparison.Ordinal) && c.Authorization!.EndsWith(".fixture-g", StringComparison.Ordinal)) &&
            snapshot.Plan == "Allegretto" && snapshot.Source == "Kimi Code · kimi.ai 账户" && snapshot.PlanSource == snapshot.Source &&
            snapshot.Metrics.Count == 3 && snapshot.Metrics[2] is { Kind: MetricKind.Balance, Currency: "USD" });
        File.Delete(KimiUsage.Config(home));
        handler.Calls.Clear();
        snapshot = await Refresh();
        check("Back on kimi.com, its own sign-in and address are used again", handler.Calls.Count > 0 && handler.Calls.All(c => c.Uri.StartsWith(KimiUsage.DefaultBaseUrl + "/", StringComparison.Ordinal)) &&
            snapshot.Source == "Kimi Code · kimi.com 账户");
        service.Dispose();
    }

    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class PriceServer(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request.RequestUri!));
    }
    private sealed class NoDesktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => null;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Reply("{}");
        public List<(string Uri, string? Authorization)> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host is not ("api.kimi.com" or "api.kimi.ai")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
            lock (Calls) Calls.Add((request.RequestUri.AbsoluteUri, request.Headers.Authorization?.ToString()));
            return Task.FromResult(Respond(request));
        }
    }
}

using System.Net;
using System.Text.Json;
using BrimDeck.Core;

// Xiaomi MiMo: MiMo Code (CLI) and Xiaomi MiMo Desktop share mimocode.db; the desktop membership is read from the MiMo server with
// the cookies of a Xiaomi account signed in to in BrimDeck. The record fixtures follow what MiMo Desktop and MiMo Code wrote on
// 2026-11-13; the subscription answers follow the format the desktop app parses (its own test data).
static class MiMoUsageTests
{
    private const string Member = """
        {"code":0,"data":{"groupCode":null,"current":{"id":1,"planCode":"mimo-cn-basic","title":"MiMo 入门","planTier":1,"status":"ACTIVE",
         "renewalMode":"MONTHLY","source":"ORDER_SUB","startTime":"2026-11-13T20:00:00","endTime":"2026-12-13T20:00:00","percent":78.5,
         "nextResetTime":"2026-11-20T00:00:00"},"subscriptions":[]}}
        """;
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }

    public static async Task Run(string root, Action<string, bool> check)
    {
        Catalog(root, check);
        Records(root, check);
        Membership(check);
        Sessions(check);
        await Renewal(check);
        await Service(root, check);
    }

    private static void Catalog(string root, Action<string, bool> check)
    {
        check("Xiaomi MiMo Desktop subscription is an automatic quota and statistics source saved as mimo",
            ProviderCatalog.IsBuiltIn(ProviderId.MiMo) && ProviderCatalog.IsUsageSource(ProviderId.MiMo) && ProviderCatalog.Key(ProviderId.MiMo) == "mimo" &&
            ProviderCatalog.Name(ProviderId.MiMo) == Loc.T("Xiaomi MiMo 桌面订阅", "Xiaomi MiMo Desktop subscription") && (int)ProviderId.MiMo == 26 && PanelThemes.AppKey(ProviderId.MiMo) == "mimo" &&
            ProviderCatalog.UsageName(ProviderId.MiMo) == "Xiaomi MiMo" && ProviderCatalog.UsageName(ProviderId.Codex) == "Codex");
        check("Every built-in theme gives Xiaomi MiMo its own color", PanelThemes.BuiltIns.All(theme =>
            new[] { false, true }.All(night => PanelThemes.Resolve(theme, night).Apps.ContainsKey("mimo"))));
        var profile = Path.Combine(root, "mimo-user");
        check("MiMo keeps its database in .local\\share\\mimocode in the user profile",
            DataLocations.Resolve(profile, root, root, null, null).MiMoDatabase == Path.Combine(profile, ".local", "share", "mimocode", "mimocode.db"));
        var data = Path.Combine(root, "xdg");
        check("XDG_DATA_HOME moves the MiMo database as it moves OpenCode's",
            DataLocations.Resolve(profile, root, root, null, null).WithOpenCode(profile, data, null).MiMoDatabase == Path.Combine(data, "mimocode", "mimocode.db"));
    }

    private static void Records(string root, Action<string, bool> check)
    {
        string?[] Row(string id, string provider, string model, string input, string read, string output, string reasoning, string cost) =>
            [id, "ses_1", "1794571395524", provider, model, input, read, "0", output, reasoning, cost];
        var entries = MiMoUsage.Parse([
            Row("msg_1", "mimo-desktop", "mimo-v2.6-pro", "6238", "35712", "18", "19", "0"),
            Row("msg_2", "xiaomi", "mimo-v2.6-flash", "8582", "16384", "10", "41", "0.0012616352"),
            Row("msg_3", "xiaomi", "mimo-v2.6-flash", "x", "", "", "", ""),
            ["bad"]]).ToList();
        check("Each MiMo response is one record, its reasoning counted as output", entries.Count == 3 && entries[0] is { Model: "mimo-v2.6-pro", Input: 6238, CacheRead: 35712, Output: 37 });
        check("A desktop membership request recorded at 0 is left to be priced", entries[0].ReportedCostUsd is null);
        check("The cost MiMo Code works out for an Open Platform key is kept", entries[1].ReportedCostUsd == 0.0012616352m);
        check("Unreadable counts are 0 and a missing cost stays unknown", entries[2] is { Total: 0, ReportedCostUsd: null });

        var database = Path.Combine(root, "mimo-records", "mimocode.db"); Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1794573000000);
        var snapshot = new ProviderSnapshot(ProviderId.MiMo);
        MiMoUsage.Read(snapshot, new Desktop(null), database, now.AddDays(-1), now);
        check("Without mimocode.db there are no MiMo statistics", !snapshot.UsageAvailable);
        File.WriteAllText(database, "");
        var desktop = new Desktop(null) { Rows = [Row("msg_1", "mimo-desktop", "mimo-v2.6-pro", "6238", "35712", "18", "19", "0")] };
        MiMoUsage.Read(snapshot, desktop, database, now.AddDays(-1), now);
        check("MiMo statistics read the assistant messages of the period", snapshot.UsageAvailable && snapshot.Entries.Count == 1 &&
            desktop.Queries.Single().Contains("FROM message") && desktop.Queries.Single().Contains($"time_created >= {now.AddDays(-1).ToUnixTimeMilliseconds()}"));
    }

    private static void Membership(Action<string, bool> check)
    {
        var member = MiMoUsage.ParseSubscription(Json(Member));
        check("The membership's tier names the plan", member.Plan == Loc.T("入门", "Starter"));
        check("percent is the share of the weekly allowance left", member.Weekly is { Label: "每周额度", Minutes: 10080 } weekly && Math.Abs(weekly.UsedPercent - 21.5) < 1e-9);
        check("A reset time without an offset is China time", member.Weekly?.ResetAt == new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.FromHours(8)));
        var utc = MiMoUsage.ParseSubscription(Json(Member.Replace("2026-11-20T00:00:00", "2026-11-19T16:00:00Z")));
        check("A reset time with an offset is kept as it is", utc.Weekly?.ResetAt == new DateTimeOffset(2026, 11, 19, 16, 0, 0, TimeSpan.Zero));
        var none = MiMoUsage.ParseSubscription(Json("""{"code":0,"data":{"groupCode":null,"current":null,"subscriptions":[]}}"""));
        check("An account without a membership in force has no plan and no quota", none is { Plan: "", Weekly: null });
        bool Rejected(string json) { try { MiMoUsage.ParseSubscription(Json(json)); return false; } catch (InvalidDataException) { return true; } }
        check("An answer that is not code 0 with data is a changed format", Rejected("""{"code":500,"data":null}""") && Rejected("""{"error":"x","code":401}"""));
    }

    private static void Sessions(Action<string, bool> check)
    {
        (string, string, string)[] cookies = [("serviceToken", "st", "mimo-server-cn.xiaomimimo.com"), ("serviceToken", "old", ".xiaomimimo.com"),
            ("mimopc_ph", "ph", ".xiaomimimo.com"), ("other", "o", "example.com"), ("passToken", "pt", "account.xiaomi.com"), ("userId", "1", ".xiaomi.com"),
            ("deviceId", "pc_1", ".xiaomi.com"), ("bad name", "x", "mimo-server-cn.xiaomimimo.com"), ("empty", "", "mimo-server-cn.xiaomimimo.com")];
        var session = MiMoUsage.Session(cookies);
        check("The MiMo server's cookies make its Cookie header, the closest domain first; other sites' and malformed cookies are left out",
            session.Cookie == "serviceToken=st; mimopc_ph=ph");
        check("The account's cookies, its device included, are kept for renewing", session.Account == "passToken=pt; userId=1; deviceId=pc_1");
        check("A kept session reads back as it was", MiMoUsage.MiMoSession.Parse(session.Serialize()) == session);
        check("A broken or empty session is no sign-in", MiMoUsage.MiMoSession.Parse("{bad") is null && MiMoUsage.MiMoSession.Parse("""{"cookie":""}""") is null && MiMoUsage.MiMoSession.Parse(null) is null);
        check("A session never shows its cookies", session.ToString() == nameof(MiMoUsage.MiMoSession));
        check("A cookie is found by name in a header", MiMoUsage.CookieValue("a=1; passToken=x=y", "passToken") == "x=y" && MiMoUsage.CookieValue("a=1", "b") == "");
        check("Set-Cookie replaces, adds and removes cookies", MiMoUsage.Merge("serviceToken=st; a=1; b=2",
            ["serviceToken=new; Path=/; HttpOnly", "c=3; Domain=.xiaomimimo.com", "a=; Max-Age=0", "b=2; Expires=Thu, 01 Jan 1970 00:00:00 GMT"]) == "serviceToken=new; c=3");
    }

    private static async Task Renewal(Action<string, bool> check)
    {
        var session = new MiMoUsage.MiMoSession("serviceToken=old; mimopc_ph=ph", "passToken=pt; userId=1; deviceId=pc_1", "Chrome/142");
        var flow = new Flow(signedIn: true);
        var renewed = await MiMoUsage.RenewAsync(session, flow, CancellationToken.None);
        check("Renewing follows the sign-in page to account.xiaomi.com with the account's cookies and back through /api/sts",
            flow.Requests.Select(r => r.Path).SequenceEqual(["/api/user/xiaomi/me", "/pass/serviceLogin", "/api/sts"]) &&
            flow.Requests[1].Cookie == session.Account && flow.Requests[0].Cookie == "" && flow.Requests[2].Cookie == "" && flow.Requests.All(r => r.Agent == "Chrome/142"));
        check("A renewal keeps the MiMo server's new cookies and the account's replaced ones",
            renewed is { Cookie: "serviceToken=new; mimopc_ph=ph", Account: "passToken=pt2; userId=1; deviceId=pc_1", Agent: "Chrome/142" });
        renewed = await MiMoUsage.RenewAsync(session, new Flow(signedIn: false), CancellationToken.None);
        check("An account sign-in sent to the sign-in page is not renewed", renewed is null);
        check("Without the account's passToken nothing is sent", await MiMoUsage.RenewAsync(session with { Account = "userId=1" }, new Flow(true), CancellationToken.None) is null);
    }

    sealed class Flow(bool signedIn) : HttpMessageHandler
    {
        public readonly List<(string Path, string Cookie, string Agent)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add((uri.AbsolutePath, request.Headers.TryGetValues("Cookie", out var c) ? string.Join("; ", c) : "", string.Join(" ", request.Headers.UserAgent.Select(a => a.ToString()))));
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            switch (uri.AbsolutePath)
            {
                case "/api/user/xiaomi/me": response.Headers.Location = new Uri("https://account.xiaomi.com/pass/serviceLogin?sid=mimopc&callback=x"); break;
                case "/pass/serviceLogin":
                    response.Headers.Location = signedIn ? new Uri("https://mimo-server-cn.xiaomimimo.com/api/sts?followup=x") : new Uri("https://account.xiaomi.com/fe/service/login");
                    response.Headers.TryAddWithoutValidation("Set-Cookie", signedIn ? "passToken=pt2; Domain=account.xiaomi.com" : "passToken=; Max-Age=0");
                    break;
                case "/api/sts": response.StatusCode = HttpStatusCode.TemporaryRedirect; response.Headers.TryAddWithoutValidation("Set-Cookie", "serviceToken=new; Path=/"); break;
            }
            return Task.FromResult(response);
        }
    }
    private static async Task Service(string root, Action<string, bool> check)
    {
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.MiMo, UsageSource = ProviderId.MiMo }] };
        settings.Normalize();
        var now = DateTimeOffset.Now;
        async Task<(ProviderSnapshot Snapshot, Api Handler, Desktop Desktop)> Refresh(string? session, HttpStatusCode status, string body)
        {
            var handler = new Api(status, body); var desktop = new Desktop(session);
            using var http = new HttpClient(handler);
            using var service = new UsageService(desktop, new DataLocations("", "", "", "", "") { MiMoDatabase = Path.Combine(root, "mimo-none", "mimocode.db") }, http, () => now);
            return ((await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.MiMo), handler, desktop);
        }
        var (snapshot, handler, _) = await Refresh(null, HttpStatusCode.OK, Member);
        check("Without a Xiaomi account signed in nothing is sent", handler.Cookies.Count == 0 && !snapshot.LiveQuota && snapshot.StatusLabel == Loc.T("未连接", "Not connected"));
        var signedIn = new MiMoUsage.MiMoSession("serviceToken=st", "").Serialize();
        (snapshot, handler, _) = await Refresh(signedIn, HttpStatusCode.OK, Member);
        check("The membership is read from the MiMo server with the kept cookies", handler.Cookies.SequenceEqual(["serviceToken=st"]) &&
            handler.Addresses.Single() == MiMoUsage.SubscriptionAddress && snapshot is { LiveQuota: true, Scope: "account" } && snapshot.Quotas.Count == 1);
        (snapshot, _, _) = await Refresh(signedIn, HttpStatusCode.OK, """{"code":0,"data":{"current":null,"subscriptions":[]}}""");
        check("An account without a membership says so", !snapshot.LiveQuota && snapshot.StatusLabel == Loc.T("未订阅会员", "No membership"));
        Desktop desktop;
        (snapshot, handler, desktop) = await Refresh(signedIn, HttpStatusCode.Unauthorized, """{"error":"x","code":401}""");
        check("Refused cookies without the account's sign-in to renew them ask to sign in again", handler.Cookies.Count == 1 && desktop.Saved is null &&
            !snapshot.LiveQuota && snapshot.StatusLabel == Loc.T("登录已失效", "Sign-in expired"));
    }

    sealed class Api(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public readonly List<string> Cookies = [], Addresses = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Addresses.Add(request.RequestUri!.AbsoluteUri);
            Cookies.Add(request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : "");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
    sealed class Desktop(string? session) : IDesktopSources
    {
        public List<string?[]> Rows = [];
        public readonly List<string> Queries = [];
        public string? Saved;
        public string? ReadMiMoSession() => session;
        public void SaveMiMoSession(string value) => Saved = value;
        public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql) { Queries.Add(sql); return Rows; }
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unexpected credential read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unexpected service discovery");
    }
}

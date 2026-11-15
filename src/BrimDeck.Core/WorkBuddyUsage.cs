using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

// WorkBuddy, China edition (www.workbuddy.cn, desktop app "WorkBuddy") and international edition (www.workbuddy.ai, desktop app
// "WorkBuddy AI"). The two have their own accounts and answer the same requests, each at its own website (Edition). The desktop
// apps encrypt their own sign-in, so BrimDeck signs in on its own: the user signs in on the WorkBuddy website in the default
// browser, and the website hands BrimDeck a sign-in of its own (the login used by CodeBuddy plugins). Nothing of the desktop apps is
// read for the quota.
//
// Sign-in: POST /v2/plugin/auth/state?platform=<Edition.Platform> returns {state, authUrl} (<website>/login?platform&state); the
// browser opens authUrl, and GET /v2/plugin/auth/token?state= answers code 11217 until the user has signed in, then {accessToken,
// refreshToken, expiresIn, refreshExpiresIn, domain}. GET /v2/plugin/accounts lists the accounts of that sign-in; the personal one
// (no enterpriseId) gives the uid. An expired token is renewed with POST /v2/plugin/auth/token/refresh (X-Refresh-Token).
//
// Quota: POST /billing/meter/get-user-resource-summary answers {SubscriptionPackageName, TotalCredits, Packages: [{PackageCode,
// CycleTotalCapacity, CycleRemainCapacity, CycleUsedCapacity}], CreditGroups: [{Kind: plan_base | plan_bonus | addon | benefit,
// Name, CycleTotalCapacity, CycleRemainCapacity, CycleUsedCapacity}]}, the numbers as strings, and no times. The packages' own
// records come from get-user-resource-paid-packages and -free-packages, which take the package codes: the current cycle
// (CycleStartTime, CycleEndTime, "yyyy-MM-dd HH:mm:ss" in China Standard Time) and the time the package is valid to
// (DeductionEndTime, milliseconds). Each kind of credits shows one of these times (KindTimesAsync).
public static class WorkBuddyUsage
{
    // An edition's website, the id the secrets store keeps its sign-in under (apart from the keys of the rows entered by hand), which
    // kind of credits each of its packages is (KindsFor), and the platform its sign-in names. After signing in, the international
    // website's page opens the client the platform names, and an app it does not know as vscode://; only "cli" opens nothing, so the
    // international sign-in names it. The China website's page opens nothing by itself, and its sign-in names BrimDeck.
    public sealed record Edition(ProviderId Id, string Origin, Guid SessionKey, IReadOnlyDictionary<string, string> PackageKinds, string Platform);
    public static readonly Edition China = new(ProviderId.WorkBuddy, "https://www.workbuddy.cn", new("6b3f1d2e-8a4c-4f57-9c1e-2d7a5b0e4f81"), KindsFor(international: false), "brimdeck");
    public static readonly Edition International = new(ProviderId.WorkBuddyAi, "https://www.workbuddy.ai", new("c41e7a90-3d52-4b8f-a6e1-95f0b2d7c368"), KindsFor(international: true), "cli");
    public static Edition For(ProviderId id) => id == ProviderId.WorkBuddyAi ? International : China;

    public sealed record Session(string AccessToken, string RefreshToken, string Uid, string Domain)
    {
        public string Serialize() => JsonSerializer.Serialize(this);
        public static Session? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonSerializer.Deserialize<Session>(text) is { AccessToken.Length: > 0, Uid.Length: > 0 } session ? session : null; }
            catch (JsonException) { return null; }
        }
    }

    // The website refused the sign-in (HTTP 401), or a renewal was refused.
    public sealed class RejectedException() : Exception("HTTP 401");

    public sealed record Reading(List<UsageMetric> Metrics, string Plan);

    // Starts a sign-in: the address to open in the browser and the state to wait on. The address must be the website's own
    // login page for this state, so a changed answer never sends the browser elsewhere.
    public static async Task<(string State, string Url)> StartLoginAsync(HttpClient http, Edition edition, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{edition.Origin}/v2/plugin/auth/state?platform={edition.Platform}") { Content = Json("{}") };
        request.Headers.TryAddWithoutValidation("X-No-Authorization", "true");
        request.Headers.TryAddWithoutValidation("X-No-User-Id", "true");
        using var doc = await SendAsync(http, request, ct);
        var data = doc.RootElement.Get("data");
        string state = data.Get("state").Text(), link = data.Get("authUrl").Text();
        if (doc.RootElement.Get("code").Number() != 0 || state.Length == 0) throw new InvalidDataException(Loc.T("WorkBuddy 官网暂时无法创建登录请求", "The WorkBuddy website cannot start a sign-in right now"));
        if (!LoginUrlMatches(edition, link, state)) throw new InvalidDataException(Loc.T("WorkBuddy 官网返回的登录地址无效", "The WorkBuddy website returned an invalid sign-in address"));
        return (state, link);
    }

    public static bool LoginUrlMatches(Edition edition, string link, string state) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Authority) == edition.Origin && uri.AbsolutePath == "/login"
        && uri.Query.TrimStart('?').Split('&').Any(pair => pair == "state=" + Uri.EscapeDataString(state));

    // Waits for the user to finish signing in, up to ten minutes, then picks the personal account. Null when the time ran out.
    public static async Task<Session?> WaitForLoginAsync(HttpClient http, Edition edition, string state, CancellationToken ct, TimeSpan? interval = null)
    {
        var until = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(interval ?? TimeSpan.FromSeconds(2), ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{edition.Origin}/v2/plugin/auth/token?state={Uri.EscapeDataString(state)}");
            request.Headers.TryAddWithoutValidation("X-No-Authorization", "true");
            JsonDocument doc;
            try { doc = await SendAsync(http, request, ct, allowBadRequest: true); }
            // A poll that could not get through, or took too long, is simply tried again.
            catch (HttpRequestException ex) when (ex.StatusCode is null) { continue; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            using (doc)
            {
                if (doc.RootElement.Get("code").Number() == 11217) continue;
                if (doc.RootElement.Get("code").Number() != 0) throw new InvalidDataException(Loc.T("WorkBuddy 登录请求已失效，请重新登录", "The WorkBuddy sign-in request has expired. Sign in again."));
                var auth = doc.RootElement.Get("data");
                string token = auth.Get("accessToken").Text(), refresh = auth.Get("refreshToken").Text(), domain = auth.Get("domain").Text();
                if (token.Length == 0) throw new InvalidDataException(Loc.T("WorkBuddy 官网没有返回登录凭据", "The WorkBuddy website returned no sign-in"));
                var uid = await PersonalAccountAsync(http, edition, token, domain, ct);
                return new Session(token, refresh, uid, domain);
            }
        }
        return null;
    }

    private static async Task<string> PersonalAccountAsync(HttpClient http, Edition edition, string token, string domain, CancellationToken ct)
    {
        using var request = Authorized(edition, HttpMethod.Get, "/v2/plugin/accounts", token, "", domain);
        using var doc = await SendAsync(http, request, ct);
        foreach (var account in doc.RootElement.Get("data").Get("accounts").Items())
            if (account.Get("enterpriseId").Text().Length == 0 && account.Get("isCurrentOneIdEnterprise").ValueKind != JsonValueKind.True
                && account.Get("uid").Text() is { Length: > 0 } uid)
                return uid;
        throw new InvalidDataException(Loc.T("请使用 WorkBuddy 个人账号登录", "Sign in with a personal WorkBuddy account"));
    }

    // Reads the credits with the session; a refused token is renewed once and the new session handed to save before the credits
    // are read again, since the website may replace the refresh token as well.
    public static async Task<Reading> ReadAsync(HttpClient http, Edition edition, Session session, Action<Session> save, CancellationToken ct)
    {
        try { return await ReadOnceAsync(http, edition, session, ct); }
        catch (RejectedException) when (session.RefreshToken.Length > 0)
        {
            var renewed = await RenewAsync(http, edition, session, ct);
            save(renewed);
            return await ReadOnceAsync(http, edition, renewed, ct);
        }
    }

    private static async Task<Session> RenewAsync(HttpClient http, Edition edition, Session session, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{edition.Origin}/v2/plugin/auth/token/refresh") { Content = Json("{}") };
        request.Headers.TryAddWithoutValidation("X-Refresh-Token", session.RefreshToken);
        request.Headers.TryAddWithoutValidation("X-Auth-Refresh-Source", "plugin");
        if (session.Domain.Length > 0) request.Headers.TryAddWithoutValidation("X-Domain", session.Domain);
        // A refresh token the website no longer accepts is answered with HTTP 401 (code 12153, "refresh token failed"); 403 is
        // read the same way. Other failures, such as a 400 for a request without a refresh token, are not a lost sign-in.
        JsonDocument doc;
        try { doc = await SendAsync(http, request, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden) { throw new RejectedException(); }
        using (doc)
        {
            var data = doc.RootElement.Get("data");
            var token = data.Get("accessToken").Text();
            if (doc.RootElement.Get("code").Number() != 0 || token.Length == 0) throw new RejectedException();
            var refresh = data.Get("refreshToken").Text();
            var domain = data.Get("domain").Text();
            return session with { AccessToken = token, RefreshToken = refresh.Length > 0 ? refresh : session.RefreshToken, Domain = domain.Length > 0 ? domain : session.Domain };
        }
    }

    private static async Task<Reading> ReadOnceAsync(HttpClient http, Edition edition, Session session, CancellationToken ct)
    {
        using var request = Authorized(edition, HttpMethod.Post, "/billing/meter/get-user-resource-summary", session.AccessToken, session.Uid, session.Domain, "{}");
        using var summary = await SendAsync(http, request, ct);
        if (summary.RootElement.Get("code").Number() != 0) throw new InvalidDataException(Loc.T("WorkBuddy 没有返回积分", "WorkBuddy returned no credits"));
        var data = summary.RootElement.Get("data");
        var codes = data.Get("Packages").Items().Select(p => p.Get("PackageCode").Text()).Where(edition.PackageKinds.ContainsKey).Distinct().ToList();
        var times = new Dictionary<string, KindTime>();
        // The times are a nicety: when they cannot be read, the credits are shown without them.
        if (codes.Count > 0)
            try { times = await KindTimesAsync(http, edition, session, codes, DateTimeOffset.UtcNow, ct); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or OperationCanceledException && !ct.IsCancellationRequested) { }
        return Parse(data, times);
    }

    // Which kind of credits (CreditGroups' Kind) each package is, as the WorkBuddy 5.9 clients sort them: the plan itself (monthly and
    // yearly plans and their trials, the youth, advanced, flagship and BuddyOne plans, and the international free plan), the plan's
    // bonus, credit packs bought, and growth and activity credits. The China edition counts the trial plan (体验版, 008) as the plan;
    // the international edition counts it, and the gift package (006), as reward credits. Packages sorted into none, such as the
    // China edition's daily free credits, are left out.
    private static Dictionary<string, string> KindsFor(bool international)
    {
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var code in new[] { "TCACA_code_002_AkiJS3ZHF5", "TCACA_code_005_maRGyrHhw1", "TCACA_code_003_FAnt7lcmRT", "TCACA_code_023_4xbGhMrE6q",
            "TCACA_code_026_BaESVICNoi", "TCACA_code_027_0FCGVA6vSa", "TCACA_code_043_6oIkxT7Z0S", "TCACA_code_035_ArVxJcGDsm",
            "TCACA_code_039_KRcQj7wUat", "TCACA_code_040_mi9rCYg46x" }) kinds[code] = "plan_base";
        foreach (var code in new[] { "TCACA_code_028_NtpWi0jzXs", "TCACA_code_037_WxOD3MpI2o" }) kinds[code] = "plan_bonus";
        foreach (var code in new[] { "TCACA_code_009_0XmEQc2xOf", "TCACA_code_038_OhvqZtiPKr", "TCACA_code_036_lupO5WgNdG" }) kinds[code] = "addon";
        foreach (var code in new[] { "TCACA_code_007_nzdH5h4Nl0", "TCACA_code_029_6wCGEWquYy", "TCACA_code_030_BjSt89qTvr" }) kinds[code] = "benefit";
        kinds["TCACA_code_008_cfWoLwvjU4"] = international ? "benefit" : "plan_base";
        if (international) kinds["TCACA_code_006_DbXS0lrypC"] = "benefit";
        return kinds;
    }

    // The one time each kind of credits shows, as the WorkBuddy client shows it: the plan's credits and its bonus renew when their
    // cycle ends, so they show when the earliest cycle ends (one second after its last second, "2026-11-30 23:59:59" → 12-01 00:00);
    // bought and activity credits do not renew, so they show when the first package still in use expires (DeductionEndTime).
    public sealed record KindTime(DateTimeOffset? Reset, DateTimeOffset? Expires);

    public static async Task<Dictionary<string, KindTime>> KindTimesAsync(HttpClient http, Edition edition, Session session, List<string> codes, DateTimeOffset now, CancellationToken ct)
    {
        var today = now.ToOffset(ChinaTime);
        var records = new List<(string Kind, DateTimeOffset? CycleEnd, DateTimeOffset? Expires)>();
        foreach (var (path, extra) in new (string, Dictionary<string, object>)[]
        {
            ("/billing/meter/get-user-resource-paid-packages", new() { ["NeedRenewInfo"] = true }),
            ("/billing/meter/get-user-resource-free-packages", new() { ["SlicePeriodStartTime"] = today.ToString("yyyy-MM-dd 00:00:00", CultureInfo.InvariantCulture),
                ["SlicePeriodEndTime"] = today.ToString("yyyy-MM-dd 23:59:59", CultureInfo.InvariantCulture) }),
        })
        {
            var body = new Dictionary<string, object> { ["PageNumber"] = 1, ["PageSize"] = 200, ["PackageCodes"] = codes, ["Status"] = new[] { 0, 3 } };
            foreach (var (key, value) in extra) body[key] = value;
            using var request = Authorized(edition, HttpMethod.Post, path, session.AccessToken, session.Uid, session.Domain, JsonSerializer.Serialize(body));
            using var doc = await SendAsync(http, request, ct);
            foreach (var account in doc.RootElement.Get("data").Get("Accounts").Items())
                if (edition.PackageKinds.TryGetValue(account.Get("PackageCode").Text(), out var kind))
                    records.Add((kind, CycleEnd(account.Get("CycleEndTime").Text()), Milliseconds(account.Get("DeductionEndTime"))));
        }
        var times = new Dictionary<string, KindTime>();
        foreach (var group in records.GroupBy(r => r.Kind))
        {
            bool renews = group.Key is "plan_base" or "plan_bonus";
            var found = renews ? group.Select(r => r.CycleEnd).Where(end => end > now).Min()?.AddSeconds(1)
                : group.Select(r => r.Expires).Where(end => end > now).Min();
            if (found is { } time) times[group.Key] = renews ? new(time, null) : new(null, time);
        }
        return times;
    }

    private static readonly TimeSpan ChinaTime = TimeSpan.FromHours(8);
    public static DateTimeOffset? CycleEnd(string text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ? new DateTimeOffset(local, ChinaTime) : null;
    private static DateTimeOffset? Milliseconds(JsonElement value) =>
        value.Number() is { } ms && ms > 0 && ms < 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;

    // One metric for each kind of credits the account has: the plan's own, the plan's bonus, bought and rewarded, each with the
    // time its kind shows (KindTimesAsync). Without CreditGroups the packages are added up into one.
    public static Reading Parse(JsonElement data, IReadOnlyDictionary<string, KindTime>? times = null)
    {
        var metrics = new List<UsageMetric>();
        static decimal Amount(JsonElement row, string field) => (decimal)Math.Max(0, row.Get(field).Number() ?? 0);
        void Add(string id, string label, decimal total, decimal left, KindTime? time = null)
        {
            if (total <= 0 && left <= 0) return;
            total = Math.Max(total, left);
            metrics.Add(new UsageMetric(MetricKind.Count, label) { Id = id, Unit = "积分", Total = total, Used = total - left, ResetAt = time?.Reset, ExpiresAt = time?.Expires });
        }
        var groups = data.Get("CreditGroups");
        if (groups.ValueKind == JsonValueKind.Array)
            foreach (var group in groups.EnumerateArray())
            {
                var kind = group.Get("Kind").Text();
                var label = kind switch { "plan_base" => "套餐积分", "plan_bonus" => "套餐赠送积分", "addon" => "购买积分", "benefit" => "奖励积分", _ => group.Get("Name").Text() };
                if (label.Length == 0) continue;
                Add(kind.Length > 0 ? kind : label, label, Amount(group, "CycleTotalCapacity"), Amount(group, "CycleRemainCapacity"), times?.GetValueOrDefault(kind));
            }
        else
        {
            decimal total = 0, left = 0;
            foreach (var row in data.Get("Packages").Items()) { total += Amount(row, "CycleTotalCapacity"); left += Amount(row, "CycleRemainCapacity"); }
            Add("credits", "积分", total, left);
        }
        return new(metrics, data.Get("SubscriptionPackageName").Text());
    }

    private static HttpRequestMessage Authorized(Edition edition, HttpMethod method, string path, string token, string uid, string domain, string? body = null)
    {
        var request = new HttpRequestMessage(method, edition.Origin + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (uid.Length > 0) request.Headers.TryAddWithoutValidation("X-User-Id", uid);
        if (domain.Length > 0) request.Headers.TryAddWithoutValidation("X-Domain", domain);
        request.Headers.TryAddWithoutValidation("Accept-Language", Loc.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en");
        if (body is not null) request.Content = Json(body);
        return request;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // A 401 is a refused sign-in; other failures keep their status. allowBadRequest reads the body of a 400, which the sign-in
    // poll answers while it waits.
    private static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct, bool allowBadRequest = false)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("BrimDeck/1.0");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.SendAsync(request, deadline.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new RejectedException();
        if (!response.IsSuccessStatusCode && !(allowBadRequest && response.StatusCode == HttpStatusCode.BadRequest))
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
        if (bytes.Length > 2 << 20) throw new InvalidDataException(Loc.T("WorkBuddy 返回的数据过大", "WorkBuddy returned too much data"));
        return JsonDocument.Parse(bytes);
    }
}

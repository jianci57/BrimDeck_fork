using System.Globalization;
using System.Text.Json;

namespace BrimDeck.Core;

// Xiaomi MiMo. MiMo Code (the CLI) is a fork of OpenCode, and Xiaomi MiMo Desktop runs MiMo Code as its engine, so both write one
// SQLite database, mimocode.db in $XDG_DATA_HOME\mimocode, otherwise %USERPROFILE%\.local\share\mimocode.
//   - Records: each model response is a row of message whose data is { role: "assistant", providerID, modelID, tokens: { input,
//     output, reasoning, cache: { read, write } }, cost, error? }, as in OpenCode 1.x. input excludes the cache, and reasoning is
//     billed as output but not counted in output. A failed response has no tokens.
//   - providerID "mimo-desktop" marks a request of the desktop membership, which the desktop app sends with the Xiaomi account
//     instead of a key. Its cost is recorded as 0 whatever it used, so it is left unpriced here and estimated at public prices.
//     Other providers ("xiaomi" for an Open Platform key, and the others MiMo Code can connect to) carry the cost MiMo Code works
//     out itself; for MiMo models it matches Xiaomi's overseas list prices in US dollars, so it is kept as it is.
//
// The membership quota has no API key: the desktop app signs in to a Xiaomi account (account.xiaomi.com, service mimopc), and the
// MiMo server then keeps the sign-in in its own cookies. Its API answers { code, data } and refuses a missing or expired sign-in
// with HTTP 401. GET /user/xiaomi/subscription/self returns { current, subscriptions }, current being the plan in force or null:
// { planCode, title, planTier (1 to 4), status, renewalMode, startTime, endTime, percent, nextResetTime, source }. percent is the
// share of the weekly allowance left: the desktop app shows it as "剩余 {percent}%" under its weekly limit, resetting at
// nextResetTime. Times come without an offset; they are taken as China time, the time of the server they come from.
public static class MiMoUsage
{
    public const string Api = "https://mimo-server-cn.xiaomimimo.com/api";
    // The page the desktop app opens to sign in: without a sign-in it sends the browser to account.xiaomi.com, which comes back here.
    public const string SignInAddress = Api + "/user/xiaomi/me";
    public const string SubscriptionAddress = Api + "/user/xiaomi/subscription/self";
    public const string DesktopProvider = "mimo-desktop";
    private static readonly TimeSpan ChinaTime = TimeSpan.FromHours(8);

    public static string Database(string home, string? xdgData) =>
        Path.Combine(!string.IsNullOrWhiteSpace(xdgData) && Path.IsPathFullyQualified(xdgData) ? xdgData : Path.Combine(home, ".local", "share"), "mimocode", "mimocode.db");

    // What BrimDeck keeps of a sign-in (encrypted, IDesktopSources.ReadMiMoSession): Cookie is the Cookie header for the MiMo server,
    // and Account the one for account.xiaomi.com, which holds the Xiaomi account's longer sign-in (passToken, userId, cUserId) next
    // to the device it was made on (deviceId and the others); Agent is the user agent of the browser that signed in. RenewAsync sends\n    // them as they are. Never shown or logged.
    public sealed record MiMoSession(string Cookie, string Account, string Agent = "")
    {
        public override string ToString() => nameof(MiMoSession);
        public string Serialize() => JsonSerializer.Serialize(new { cookie = Cookie, account = Account, agent = Agent });
        public static MiMoSession? Parse(string? json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var session = new MiMoSession(doc.RootElement.Get("cookie").Text(), doc.RootElement.Get("account").Text(), doc.RootElement.Get("agent").Text());
                return session.Cookie.Length > 0 ? session : null;
            }
            catch (JsonException) { return null; }
        }
    }

    private const string AccountHost = "account.xiaomi.com";

    // The Cookie header a browser sends to host, made of its cookies (name, value, domain). Of cookies with the same name, the one
    // set for the closest domain is sent. "" when none applies.
    public static string CookieHeader(IEnumerable<(string Name, string Value, string Domain)> cookies, string host) => string.Join("; ", cookies
        .Where(c => Applies(c.Domain, host) && Valid(c.Name, c.Value))
        .GroupBy(c => c.Name, StringComparer.Ordinal).Select(g => g.OrderByDescending(c => c.Domain.TrimStart('.').Length).First()).Select(c => c.Name + "=" + c.Value));

    // The session a browser holds once the MiMo server took the sign-in.
    public static MiMoSession Session(IReadOnlyCollection<(string Name, string Value, string Domain)> cookies) =>
        new(CookieHeader(cookies, new Uri(Api).Host), CookieHeader(cookies, AccountHost));

    // The value of name in a Cookie header; "" when it holds none.
    public static string CookieValue(string header, string name) => header.Split(';', StringSplitOptions.TrimEntries)
        .Where(pair => pair.StartsWith(name + "=", StringComparison.Ordinal)).Select(pair => pair[(name.Length + 1)..]).FirstOrDefault() ?? "";

    // A Cookie header with the cookies of Set-Cookie headers put in: each replaces the one of its name, and one set to nothing or
    // already expired removes it.
    public static string Merge(string header, IEnumerable<string> setCookies)
    {
        var pairs = header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).Select(parts => (Name: parts[0], Value: parts.Length > 1 ? parts[1] : "")).ToList();
        foreach (var line in setCookies)
        {
            var parts = line.Split(';', StringSplitOptions.TrimEntries);
            var first = parts[0].Split('=', 2);
            if (first.Length < 2 || first[0].Trim().Length == 0) continue;
            string name = first[0].Trim(), value = first[1].Trim();
            bool expired = parts.Skip(1).Any(attribute =>
                attribute.StartsWith("Max-Age=", StringComparison.OrdinalIgnoreCase) && long.TryParse(attribute[8..], out var age) && age <= 0 ||
                attribute.StartsWith("Expires=", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(attribute[8..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at < DateTimeOffset.UtcNow);
            int index = pairs.FindIndex(pair => pair.Name == name);
            var kept = !expired && value.Length > 0 && Valid(name, value);
            if (index >= 0) { if (kept) pairs[index] = (name, value); else pairs.RemoveAt(index); }
            else if (kept) pairs.Add((name, value));
        }
        return string.Join("; ", pairs.Select(pair => pair.Name + "=" + pair.Value));
    }

    public static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    private static bool Applies(string domain, string host)
    {
        var bare = domain.TrimStart('.');
        return bare.Length > 0 && (host.Equals(bare, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + bare, StringComparison.OrdinalIgnoreCase));
    }
    private static bool Valid(string name, string value) =>
        name.Length > 0 && value.Length > 0 && !name.Any(ch => ch is ';' or '=' or ',' || char.IsWhiteSpace(ch) || char.IsControl(ch)) && !value.Any(ch => ch is ';' || char.IsControl(ch));

    // Renews the MiMo server's cookies the way the browser that signed in renews them, with the Xiaomi account's longer sign-in:
    //   1. SignInAddress answers 302 to account.xiaomi.com/pass/serviceLogin?sid=mimopc&callback=<the MiMo server's /api/sts>.
    //   2. That page, sent the account's cookies, answers 302 to the callback while the account's sign-in holds, replacing some of
    //      the account's cookies (passToken among them: the one sent before is no longer valid). Without a valid sign-in it sends
    //      the browser to its sign-in page instead, and the user has to sign in again.
    //   3. /api/sts sets the MiMo server's cookies.
    // Each request carries the user agent of the browser that signed in. Null when the account's sign-in was refused; the session
    // returned holds both the MiMo server's new cookies and the account's, and has to be kept.
    public static async Task<MiMoSession?> RenewAsync(MiMoSession session, HttpMessageHandler? handler, CancellationToken cancellation)
    {
        if (CookieValue(session.Account, "passToken").Length == 0) return null;
        using var http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, handler is null) { Timeout = TimeSpan.FromSeconds(20) };
        async Task<HttpResponseMessage> Get(Uri address, string cookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (session.Agent.Length > 0) request.Headers.TryAddWithoutValidation("User-Agent", session.Agent);
            if (cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);
            return await http.SendAsync(request, cancellation);
        }
        static Uri? Next(HttpResponseMessage response, Uri from) =>
            (int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location ? new Uri(from, location) : null;
        var server = new Uri(Api).Host;
        var start = new Uri(SignInAddress);
        using var first = await Get(start, "");
        if (Next(first, start) is not { Scheme: "https", Host: AccountHost } login) return null;
        using var second = await Get(login, session.Account);
        var account = Merge(session.Account, SetCookies(second));
        if (Next(second, login) is not { Scheme: "https" } sts || sts.Host != server) return null;
        using var third = await Get(sts, "");
        var cookie = Merge(session.Cookie, SetCookies(third));
        return SetCookies(third).Any() ? new MiMoSession(cookie, account, session.Agent) : null;
    }

    // Plan is "" and Weekly null when the account has no membership in force.
    public sealed record Membership(string Plan, Quota? Weekly);

    public static string PlanName(int tier, string title) => tier switch
    {
        1 => Loc.T("入门", "Starter"), 2 => Loc.T("进阶", "Plus"), 3 => Loc.T("高阶", "Pro"), 4 => Loc.T("尊享", "Ultra"),
        _ => title.Trim()
    };

    // The answer to SubscriptionAddress; an answer that is not { code: 0, data } is a changed format.
    public static Membership ParseSubscription(JsonElement root)
    {
        if (root.Get("code").Number() is not 0 || root.Get("data").ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var current = root.Get("data").Get("current");
        if (current.ValueKind != JsonValueKind.Object) return new("", null);
        var plan = PlanName((int)(current.Get("planTier").Number() ?? 0), current.Get("title").Text());
        if (current.Get("percent").Number() is not { } left) return new(plan, null);
        return new(plan, new Quota("每周额度", Math.Clamp(100 - left, 0, 100), ChinaDate(current.Get("nextResetTime")), 7 * 24 * 60));
    }

    private static DateTimeOffset? ChinaDate(JsonElement value)
    {
        var text = value.Text();
        if (text.Length == 0) return null;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) && time.Kind == DateTimeKind.Unspecified)
            return new DateTimeOffset(time, ChinaTime);
        return value.Date();
    }

    public static void Read(ProviderSnapshot snapshot, IDesktopSources desktop, string database, DateTimeOffset start, DateTimeOffset now)
    {
        snapshot.UsageAvailable = File.Exists(database);
        if (!snapshot.UsageAvailable) { snapshot.UsageNote = Loc.T("未找到本机 MiMo 记录。", "No local MiMo records were found."); return; }
        var since = start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var rows = desktop.QueryDatabase(database, "SELECT id, session_id, time_created, json_extract(data, '$.providerID'), json_extract(data, '$.modelID'), " +
            "json_extract(data, '$.tokens.input'), json_extract(data, '$.tokens.cache.read'), json_extract(data, '$.tokens.cache.write'), " +
            "json_extract(data, '$.tokens.output'), json_extract(data, '$.tokens.reasoning'), json_extract(data, '$.cost') " +
            $"FROM message WHERE json_extract(data, '$.role') = 'assistant' AND time_created >= {since}");
        if (rows is null) { snapshot.UsageComplete = false; snapshot.UsageNote = Loc.T("MiMo 记录暂时无法读取。", "The MiMo records cannot be read right now."); return; }
        snapshot.Entries = Parse(rows).Where(e => e.Time >= start && e.Time <= now && e.Total > 0).OrderBy(e => e.Time).ToList();
        int sessions = rows.Where(row => row.Length > 1 && row[1] is not null).Select(row => row[1]).Distinct().Count();
        snapshot.UsageNote = Loc.T($"本机 MiMo {start.LocalDateTime:yyyy-MM-dd} 起 · {sessions} 个会话 · {snapshot.Entries.Count} 次模型请求",
            $"Local MiMo since {start.LocalDateTime:yyyy-MM-dd} · " + Loc.Count(sessions, "session", "sessions") + " · " + Loc.Count(snapshot.Entries.Count, "model request", "model requests"));
    }

    // Xiaomi MiMo API (ProviderId.MiMoApi): a pay-as-you-go Open Platform key (sk-…) entered by hand, read at api.xiaomimimo.com as
    // MiMo Desktop reads one, with the key as a bearer token:
    //   GET /v1/platform/balance: { is_available, balance_infos: [{ currency, total_available, cash_balance (topped up), gift_balance,
    //     frozen_balance, credit_limit, credit_available }] }, amounts as strings.
    //   GET /v1/platform/usage/daily?start_date&end_date (UTC dates): { data: [{ date, name, currency, consumed_amount, … }] }, one row
    //     per day and model; the month is the calendar month in UTC.
    // Xiaomi MiMo Token Plan (ProviderId.MiMoTokenPlan): a Token Plan key (tp-… or ttp-…) entered by hand, read at the token-plan host
    // of its cluster (China, Singapore or Europe); another cluster refuses it with HTTP 401, and the hosts have no balance or usage.
    //   GET /v1/platform/token_plan: { plan_code, period: { start_at, end_at }, quota: { total_credits, used_credits,
    //     remaining_credits }, auto_renew }; no plan_code means no plan in force. Times come without an offset, as
    //     "2026-12-13 23:59:59"; MiMo Desktop reads them as UTC, and so are they here.
    public const string PlatformApi = "https://api.xiaomimimo.com";
    public static readonly IReadOnlyList<string> TokenPlanHosts = ["https://token-plan-cn.xiaomimimo.com", "https://token-plan-sgp.xiaomimimo.com", "https://token-plan-ams.xiaomimimo.com"];
    public static bool IsTokenPlanKey(string key) => key.StartsWith("tp-", StringComparison.Ordinal) || key.StartsWith("ttp-", StringComparison.Ordinal);

    // Xiaomi MiMo (ProviderId.MiMoLinked) reads the keys MiMo Code keeps in auth.json beside mimocode.db, written when MiMo Code CLI
    // signs in to the Open Platform and when Xiaomi MiMo Desktop links an API key or a Token Plan; each writes the whole file, so
    // what it holds changes with either and is read again on every refresh. One entry per provider: { "<provider>": { type: "api",
    // key, metadata: { uid, base_url, platform_linked? } } }. "xiaomi" holds a pay-as-you-go key for api.xiaomimimo.com;
    // "xiaomi-token-plan-cn", "-sgp" and "-ams" a Token Plan key for the token-plan host of that cluster.
    public sealed record LinkedKey(string Key, Uri Host)
    {
        public bool TokenPlan => Host.Host.StartsWith("token-plan-", StringComparison.OrdinalIgnoreCase);
        public override string ToString() => nameof(LinkedKey) + ":" + Host.Host;
    }

    public static IReadOnlyList<LinkedKey> ReadLinkedKeys(string database)
    {
        var keys = new List<LinkedKey>();
        try
        {
            using var doc = SharedFile.ReadJson(Path.Combine(Path.GetDirectoryName(database) ?? "", "auth.json"));
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return keys;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (!entry.Name.StartsWith("xiaomi", StringComparison.Ordinal) || entry.Value.Get("type").Text() != "api") continue;
                var key = entry.Value.Get("key").Text().Trim();
                // Keys are sent only to Xiaomi's own hosts, over HTTPS.
                if (key.Length == 0 || !Uri.TryCreate(entry.Value.Get("metadata").Get("base_url").Text(), UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".xiaomimimo.com", StringComparison.OrdinalIgnoreCase)) continue;
                keys.Add(new(key, new Uri(uri.GetLeftPart(UriPartial.Authority))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return keys.OrderBy(k => k.TokenPlan ? 0 : 1).ToList();
    }

    public static List<UsageMetric> Balance(JsonElement root, out bool available)
    {
        available = root.Get("is_available").ValueKind != JsonValueKind.False;
        var info = root.Get("balance_infos").Items().FirstOrDefault();
        if (info.ValueKind != JsonValueKind.Object || Money(info.Get("total_available")) is not { } total) throw new InvalidDataException();
        var currency = info.Get("currency").Text().ToUpperInvariant();
        var metrics = new List<UsageMetric> { new(MetricKind.Balance, "账户余额") { Id = "total", Amount = total, Currency = currency } };
        if (Money(info.Get("cash_balance")) is { } cash) metrics.Add(new(MetricKind.Balance, "充值余额") { Id = "cash", Amount = cash, Currency = currency });
        if (Money(info.Get("gift_balance")) is { } gift) metrics.Add(new(MetricKind.Balance, "赠送余额") { Id = "gift", Amount = gift, Currency = currency });
        if (Money(info.Get("frozen_balance")) is { } frozen && frozen > 0) metrics.Add(new(MetricKind.Balance, "冻结金额") { Id = "frozen", Amount = frozen, Currency = currency });
        return metrics;
    }

    // What the month spent in currency (the balance's), from the daily usage; it resets when the next UTC month starts.
    public static UsageMetric MonthSpend(JsonElement root, string currency, DateTimeOffset now)
    {
        if (root.Get("data").ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var spent = root.Get("data").Items().Where(day => day.Get("currency").Text().Equals(currency, StringComparison.OrdinalIgnoreCase))
            .Sum(day => Money(day.Get("consumed_amount")) ?? 0);
        var month = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return new(MetricKind.Spend, "本月扣费") { Id = "month", Amount = spent, Currency = currency, ResetAt = month.AddMonths(1) };
    }

    public static string UsageQuery(DateTimeOffset now) => $"start_date={now.UtcDateTime:yyyy-MM}-01&end_date={now.UtcDateTime:yyyy-MM-dd}";

    // The plan in force (its name from plan_code, such as "Lite", and its quota until the period ends); null when there is none.
    public static (string Plan, UsageMetric Quota)? TokenPlan(JsonElement root)
    {
        var code = root.Get("plan_code").Text();
        if (code.Length == 0) return null;
        var quota = root.Get("quota");
        if (Money(quota.Get("total_credits")) is not { } total || total <= 0 || Money(quota.Get("used_credits")) is not { } used) throw new InvalidDataException();
        var plan = string.Join(" ", code.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
        return (plan, new(MetricKind.Count, "套餐额度") { Id = "token-plan", Total = total, Used = used, Unit = "Credits", ResetAt = root.Get("period").Get("end_at").Date() });
    }

    private static decimal? Money(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number :
        value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;

    // Rows of: id, session, time (ms), provider, model, input, cache read, cache write, output, reasoning, cost.
    public static IEnumerable<TokenEntry> Parse(IEnumerable<string?[]> rows)
    {
        foreach (var row in rows)
        {
            if (row.Length < 11 || string.IsNullOrEmpty(row[0]) || !long.TryParse(row[2], out var millis)) continue;
            long Count(int i) => double.TryParse(row[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? (long)Math.Clamp(value, 0, 1e15) : 0;
            DateTimeOffset time;
            try { time = DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { continue; }
            decimal? cost = row[3] != DesktopProvider && decimal.TryParse(row[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount >= 0 ? amount : null;
            yield return new TokenEntry("mimo:" + row[0], time, row[4] ?? "", Count(5), Count(6), Count(7), 0, Count(8) + Count(9), cost);
        }
    }
}

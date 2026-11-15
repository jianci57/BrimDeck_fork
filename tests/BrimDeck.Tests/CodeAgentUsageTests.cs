using System.Net;
using System.Text.Json;
using BrimDeck.Core;

// CodeAgent CLI keeps Claude Code-format session records under <CODEAGENT3_CONFIG_DIR or ~/.cac>/projects, next to
// per-session metric files (model-io, observability) that must not be counted again.
static class CodeAgentUsageTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "codeagent-home");
        var project = Path.Combine(home, "projects", "D--work");
        var session = Path.Combine(project, "session-1");
        Directory.CreateDirectory(Path.Combine(session, "observability"));
        Directory.CreateDirectory(Path.Combine(session, "model-io"));
        Directory.CreateDirectory(Path.Combine(session, "subagents"));
        var now = DateTimeOffset.Now;
        var time = now.AddMinutes(-1).ToString("O");
        string Assistant(string id, long input, long output, long read = 0) => JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = time, _cac_modelId = "maas-test-codeagent",
            message = new { id, type = "message", role = "assistant", model = "maas-test", usage = new { input_tokens = input, cache_read_input_tokens = read, cache_creation_input_tokens = 0, output_tokens = output } }
        });
        // A streamed message is written once per content block: the first with empty usage, the next with the real counts.
        File.WriteAllText(Path.Combine(project, "session-1.jsonl"),
            Assistant("msg_1", 0, 0) + "\n" + Assistant("msg_1", 100, 20, 50) + "\n" +
            JsonSerializer.Serialize(new { type = "user", timestamp = time, message = new { role = "user", content = "hi" } }) + "\n");
        File.WriteAllText(Path.Combine(session, "subagents", "agent-a.jsonl"), Assistant("msg_2", 10, 5) + "\n");
        // The metrics repeat msg_1 (counted once), hold the real counts of msg_3 whose session record kept empty usage, and add a
        // title request (msg_4) that has no session record.
        File.AppendAllText(Path.Combine(project, "session-1.jsonl"), Assistant("msg_3", 0, 0) + "\n");
        string Metrics(string id, long input, long output) => JsonSerializer.Serialize(new
        { type = "query-metrics", timestamp = time, message_id = id, model = "maas-test", usage = new { input_tokens = input, output_tokens = output, cache_read_input_tokens = id == "msg_1" ? 50 : 0, cache_creation_input_tokens = 0 } });
        File.WriteAllText(Path.Combine(session, "observability", "obs.jsonl"),
            Metrics("msg_1", 100, 20) + "\n" + JsonSerializer.Serialize(new { type = "tool-metrics", timestamp = time, usage = new { input_tokens = 9 } }) + "\n" +
            Metrics("msg_3", 300, 30) + "\n" + Metrics("msg_4", 40, 4) + "\n");
        File.WriteAllText(Path.Combine(session, "model-io", "model-io-0.jsonl"),
            JsonSerializer.Serialize(new { type = "model-io", timestamp = time, model = "maas-test", usage = new { input_tokens = 100, output_tokens = 20 } }) + "\n");

        var reader = new LogReader();
        var data = reader.Read(ProviderId.CodeAgent, home, now);
        // msg_1 170 + msg_2 15 + msg_3 330 + msg_4 44
        check("CodeAgent counts each message once with its most complete usage",
            data.Entries.Count == 4 && data.Entries.Sum(e => e.Total) == 559 && data.Entries.All(e => e.Key.StartsWith("cac:", StringComparison.Ordinal)));
        check("CodeAgent ignores model-io copies and other metric kinds", !data.Entries.Any(e => e.Total is 120 or 9));
        check("CodeAgent keeps the model the service reported", data.Entries.All(e => e.Model == "maas-test"));
        check("CodeAgent missing folder is unavailable", !reader.Read(ProviderId.CodeAgent, Path.Combine(root, "no-codeagent"), now).UsageAvailable);
        check("Every built-in theme and the template give CodeAgent its own color, not the fallback for other sources",
            PanelThemes.AppKey(ProviderId.CodeAgent) == "codeagent" && PanelThemes.BuiltIns.All(theme =>
                new[] { false, true }.All(night => PanelThemes.Resolve(theme, night).Apps.ContainsKey("codeagent"))));
        check("CodeAgent is a quota source read automatically and a usage app", ProviderCatalog.IsUsageSource(ProviderId.CodeAgent) &&
            ProviderCatalog.Sources.Contains(ProviderId.CodeAgent) && ProviderCatalog.IsBuiltIn(ProviderId.CodeAgent));
        var userHome = Path.Combine(root, "codeagent-user");
        check("CodeAgent defaults to .cac in the user profile", DataLocations.Resolve(userHome, root, root, null, null).CodeAgentHome == Path.Combine(userHome, ".cac"));
        check("CodeAgent honors an absolute CODEAGENT3_CONFIG_DIR", DataLocations.Resolve(userHome, root, root, null, null, codeAgentOverride: home).CodeAgentHome == home &&
            DataLocations.Resolve(userHome, root, root, null, null, codeAgentOverride: "relative").CodeAgentHome == Path.Combine(userHome, ".cac"));

        await CheckQuota(root, home, now, check);
    }

    private const string QuotaJson = """
        {"quotas":[{"type":"CPD_Peak","used_quota":5,"total_quota":20000,"usage_ratio":0.0003,"reset_at":"2026-11-14 00:00:00"},
                   {"type":"CPM","used_quota":542.03,"total_quota":400000,"usage_ratio":0.0013,"reset_at":"2026-12-01 00:00:00"},
                   {"type":"CPD_Valley","used_quota":0,"total_quota":20000,"usage_ratio":0,"reset_at":"2026-11-14 00:00:00"}],
         "external_quotas":[{"type":"CPM","used_quota":542.03,"total_quota":320000.0,"usage_ratio":0.0016,"reset_at":"2026-12-01 00:00:00"}],
         "internal_quotas":[{"type":"CPM","used_quota":0,"total_quota":0,"usage_ratio":0,"reset_at":"2026-12-01 00:00:00"}],
         "show_link":false}
        """;

    // The definition as the service gave it on 2026-11-13.
    private const string HoursJson = """
        {"busy_period":{"time_range":"工作日10-12点、15-18点","coefficient":"3x","weekday_coeff":[{"min":10,"max":12,"value":3,"label":"忙时"},{"min":15,"max":18,"value":3,"label":"忙时"}],"weekend_coeff":[]},
         "normal_period":{"time_range":"未配置","coefficient":"1x","weekday_coeff":[],"weekend_coeff":[]},
         "idle_period":{"time_range":"工作日0-8点、8-10点、12-15点、18-22点、22-24点、非工作日0-24点","coefficient":"0.2x","weekday_coeff":[{"min":0,"max":8,"value":0.2,"label":"闲时"},{"min":8,"max":10,"value":0.2,"label":"闲时"},{"min":12,"max":15,"value":0.2,"label":"闲时"},{"min":18,"max":22,"value":0.2,"label":"闲时"},{"min":22,"max":24,"value":0.2,"label":"闲时"}],"weekend_coeff":[]},
         "discount_status":"闲时2折将在12:00后开启"}
        """;

    private static void CheckHours(Action<string, bool> check)
    {
        var hours = CreditHours.Parse(JsonDocument.Parse(HoursJson).RootElement)!;
        var china = TimeSpan.FromHours(8);
        DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, china); // 2026-11-13 is a Friday
        check("CodeAgent busy hours are weekday 10-12 and 15-18 in China time", hours.At(At(13, 10)) == CreditPeriod.Busy &&
            hours.At(At(13, 11, 59)) == CreditPeriod.Busy && hours.At(At(13, 12)) == CreditPeriod.Idle && hours.At(At(13, 17, 30)) == CreditPeriod.Busy &&
            hours.At(At(13, 9, 59)) == CreditPeriod.Idle && hours.At(At(14, 11)) == CreditPeriod.Idle && hours.At(At(16, 10)) == CreditPeriod.Busy);
        check("CodeAgent hours follow China time whatever the offset", hours.At(new DateTimeOffset(2026, 11, 13, 3, 0, 0, TimeSpan.Zero)) == CreditPeriod.Busy);
        check("CodeAgent hours read their rates", hours.Rates[CreditPeriod.Busy] == 3 && hours.Rates[CreditPeriod.Idle] == 0.2);
        check("CodeAgent idle hours end when busy hours start", hours.Next(CreditPeriod.Idle, At(13, 9, 20)) == (At(13, 10), true));
        check("CodeAgent idle hours start when busy hours end", hours.Next(CreditPeriod.Idle, At(13, 11)) == (At(13, 12), false));
        check("CodeAgent busy hours after Friday evening start on Monday", hours.Next(CreditPeriod.Busy, At(13, 19)) == (At(16, 10), false) &&
            hours.Next(CreditPeriod.Idle, At(14, 9)) == (At(16, 10), true));
        check("CodeAgent hours without busy hours are not used", CreditHours.Parse(JsonDocument.Parse("{}").RootElement) is null);
        var metrics = CodeAgentUsage.Quotas(JsonDocument.Parse(QuotaJson).RootElement, hours);
        check("CodeAgent daily quotas carry their hours; the monthly quota does not",
            metrics.Single(m => m.Label == "忙时每日额度") is { Period: CreditPeriod.Busy, Hours: not null } &&
            metrics.Single(m => m.Label == "闲时每日额度") is { Period: CreditPeriod.Idle, Hours: not null } &&
            metrics.Single(m => m.Label == "每月额度") is { Period: null });
    }

    private static async Task CheckQuota(string root, string home, DateTimeOffset now, Action<string, bool> check)
    {
        CheckHours(check);
        var metrics = CodeAgentUsage.Quotas(JsonDocument.Parse(QuotaJson).RootElement);
        check("CodeAgent quotas list the whole allowance first, month before day, then the outside-model part",
            metrics.Select(m => m.Label).SequenceEqual(["每月额度", "忙时每日额度", "闲时每日额度", "外部模型 · 每月额度"]));
        check("CodeAgent quotas count credits against their total and skip an empty allowance",
            metrics[0] is { Kind: MetricKind.Count, Used: 542.03m, Total: 400000m, Unit: "积分" } && Math.Abs(metrics[1].UsedPercent!.Value - 0.025) < 1e-9 && metrics[1].Window == 1440);
        check("CodeAgent quota address decodes to an HTTPS quota API", CodeAgentUsage.QuotaUrl.StartsWith("https://", StringComparison.Ordinal) &&
            CodeAgentUsage.QuotaUrl.EndsWith("/api/v1/quota/usage", StringComparison.Ordinal) && Uri.IsWellFormedUriString(CodeAgentUsage.QuotaUrl, UriKind.Absolute));
        check("CodeAgent reset times are China Standard Time", metrics[1].ResetAt == new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.FromHours(8)));

        void Credential(string token, DateTimeOffset expires) => File.WriteAllText(CodeAgentUsage.CredentialFile(home), JsonSerializer.Serialize(new
        { idaasOAuth = new { xAuthToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sealed:" + token)), xAuthExpires = expires.ToUnixTimeMilliseconds(), mtoken = "x" } }));
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.CodeAgent, UsageSource = ProviderId.CodeAgent }] };
        settings.Normalize();
        // The service accepts only the tokens in valid; a renewal, when it works, writes "renewed-token" for three more days.
        async Task<(ProviderSnapshot Snapshot, Answer Handler, Desktop Desktop)> Refresh(bool renewWorks, params string[] valid)
        {
            var handler = new Answer(valid, QuotaJson);
            var desktop = new Desktop(renewWorks ? () => Credential("renewed-token", now.AddDays(3)) : null);
            using var http = new HttpClient(handler);
            using var service = new UsageService(desktop, new DataLocations("", "", "", "", "") { CodeAgentHome = home }, http, () => now);
            return ((await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.CodeAgent), handler, desktop);
        }

        File.Delete(CodeAgentUsage.CredentialFile(home));
        var (snapshot, handler, desktop) = await Refresh(true, "secret-token");
        check("CodeAgent without a sign-in sends nothing, renews nothing and still counts tokens",
            handler.Calls == 0 && desktop.Renewals == 0 && !snapshot.LiveQuota && snapshot.Entries.Count == 4);
        Credential("secret-token", now.AddDays(2));
        (snapshot, handler, desktop) = await Refresh(true, "secret-token");
        check("CodeAgent reads the quota with the decrypted x-auth-token and renews nothing", handler.Calls == 1 && handler.Tokens.Single() == "secret-token" &&
            desktop.Renewals == 0 && handler.Url == CodeAgentUsage.QuotaUrl && snapshot.LiveQuota && snapshot.Metrics.Count == 4 && snapshot.Entries.Count == 4);
        check("CodeAgent reads the busy and idle hours with the same sign-in and gives them to the daily quotas", handler.HoursCalls == 1 &&
            snapshot.Metrics.Single(m => m.Label == "忙时每日额度").Hours is { } read && read.At(new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.FromHours(8))) == CreditPeriod.Busy);

        Credential("secret-token", now.AddMinutes(-1));
        (snapshot, handler, desktop) = await Refresh(true, "renewed-token");
        check("CodeAgent renews an expired sign-in before reading", desktop.Renewals == 1 && handler.Tokens.SequenceEqual(["renewed-token"]) && snapshot.LiveQuota);
        Credential("secret-token", now.AddDays(2));
        (snapshot, handler, desktop) = await Refresh(true, "renewed-token");
        check("CodeAgent renews a refused sign-in and reads again", desktop.Renewals == 1 && handler.Tokens.SequenceEqual(["secret-token", "renewed-token"]) && snapshot.LiveQuota);

        Credential("secret-token", now.AddMinutes(-1));
        (snapshot, handler, desktop) = await Refresh(false, "renewed-token");
        check("CodeAgent shows a sign-in that could not be renewed as expired and sends nothing", desktop.Renewals == 1 && handler.Calls == 0 &&
            !snapshot.LiveQuota && snapshot.StatusLabel == Loc.T("登录已过期", "Sign-in expired"));
        Credential("secret-token", now.AddDays(2));
        (snapshot, handler, desktop) = await Refresh(false, "renewed-token");
        check("CodeAgent shows a refused sign-in that could not be renewed as expired", desktop.Renewals == 1 && handler.Calls == 1 &&
            !snapshot.LiveQuota && snapshot.Metrics.Count == 0 && snapshot.StatusLabel == Loc.T("登录已过期", "Sign-in expired"));

        // One service refreshing twice within ten minutes tries a failing renewal once.
        Credential("secret-token", now.AddMinutes(-1));
        var throttled = new Desktop(null);
        using (var http = new HttpClient(new Answer(["renewed-token"], QuotaJson)))
        using (var service = new UsageService(throttled, new DataLocations("", "", "", "", "") { CodeAgentHome = home }, http, () => now))
        {
            await service.RefreshAsync(settings); await service.RefreshAsync(settings);
        }
        check("CodeAgent does not retry a failed renewal within ten minutes", throttled.Renewals == 1);

        var install = Path.Combine(root, "codeagent-install");
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        File.WriteAllText(Path.Combine(install, "codeagent.bat"), "");
        File.WriteAllText(Path.Combine(install, "bin", "codeagentcli.exe"), "");
        check("CodeAgent CLI is found through the launcher on PATH", CodeAgentUsage.FindCli(Path.Combine(root, "no-program-files"),
            string.Join(Path.PathSeparator, Path.Combine(root, "elsewhere"), install)) == Path.Combine(install, "bin", "codeagentcli.exe"));
        check("CodeAgent CLI is missing without an installation", CodeAgentUsage.FindCli(Path.Combine(root, "no-program-files"), Path.Combine(root, "elsewhere")) is null);
    }

    // Answers the quota with body and the busy and idle hours with HoursJson, for the tokens in valid; counts the two apart.
    sealed class Answer(string[] valid, string body) : HttpMessageHandler
    {
        public int Calls, HoursCalls; public string Url = ""; public readonly List<string> Tokens = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.TryGetValues("X-Auth-Token", out var values) ? values.Single() : "";
            bool hours = request.RequestUri!.ToString() == CodeAgentUsage.HoursUrl;
            if (hours) HoursCalls++; else { Calls++; Url = request.RequestUri!.ToString(); Tokens.Add(token); }
            return Task.FromResult(new HttpResponseMessage(valid.Contains(token) ? HttpStatusCode.OK : HttpStatusCode.Unauthorized) { Content = new StringContent(hours ? HoursJson : body) });
        }
    }
    sealed class Desktop(Action? renew) : IDesktopSources
    {
        public int Renewals;
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unexpected credential read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unexpected service discovery");
        // Stands in for DPAPI: the stored value is the base64 of "sealed:" and the token.
        public string? UnprotectUserData(string base64)
        { var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64)); return text.StartsWith("sealed:") ? text[7..] : null; }
        // Stands in for the CLI: a renewal that works writes a new sign-in.
        public Task<bool> RenewCodeAgentAsync(string home, CancellationToken cancellation)
        { Renewals++; renew?.Invoke(); return Task.FromResult(renew is not null); }
    }
}

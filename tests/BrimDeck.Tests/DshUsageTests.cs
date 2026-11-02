using System.Net;
using BrimDeck.Core;

static class DshUsageTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "dsh-home");
        Directory.CreateDirectory(home);
        var locations = DataLocations.Resolve(Path.Combine(root, "home"), root, root, null, null, null, home);
        check("DSH_HOME overrides the DeepSeek Harness directory", locations.DshHome == home &&
            DataLocations.Resolve(Path.Combine(root, "home"), root, root, null, null).DshHome == Path.Combine(root, "home", ".dsh"));
        check("Without DeepSeek Harness credentials there is no account", DshUsage.ReadAccount(home) is null);

        // The layout DeepSeek Harness writes: refs for keys, records for the signed-in account.
        void Store(string text) => File.WriteAllText(DshUsage.Credentials(home), text);
        Store("""
            # credentials
            version: 1
            refs:
              DEEPSEEK_API_KEY: "sk-fixture-key"
            records:
              client-connection/browser-session:
                kind: grant
                payload:
                  version: 1
                  secret: other-secret
              deepseek-account-platform/default:
                kind: grant
                payload:
                  version: 1
                  token: fixture+token/A==
                  issuer: https://platform.deepseek.com
            """);
        check("The account grant and the saved API key are read from .credentials.yaml", DshUsage.ReadAccount(home) == new DshAccount("fixture+token/A==", "sk-fixture-key"));
        Store("""
            version: 1
            records:
              'deepseek-account-platform/default':
                kind: grant
                payload: { version: 1 }
              deepseek-account-platform/device:
                kind: grant
                payload:
                  id: device
            """);
        check("Flow mappings are skipped without hiding the rest of the document", DshUsage.ReadAccount(home) == new DshAccount("", ""));
        Store("""
            version: 1
            records:
              deepseek-account-platform/default:
                kind: grant
                payload:
                  version: 1
                  token: dev-token
                  issuer: http://localhost:8080
            """);
        check("A grant issued by another origin is never sent to the public platform", DshUsage.ReadAccount(home) == new DshAccount("", ""));

        var summary = DshUsage.Summary(System.Text.Json.JsonDocument.Parse("""
            {"code":0,"msg":"","data":{"biz_code":0,"biz_msg":"","biz_data":{"normal_wallets":[{"currency":"CNY","balance":"39.7834282000000000"}],"bonus_wallets":[{"currency":"CNY","balance":"5.9919260000000000"}],"total_costs":[{"currency":"CNY","amount":"70.7431410000000000"}]}}}
            """).RootElement);
        check("The account balance adds the topped-up and granted wallets in whole cents, with the account's total cost", summary is { } s && s.Count == 3 &&
            s[0] is { Kind: MetricKind.Balance, Label: "账户余额", Currency: "CNY" } && s[0].Amount == 45.77m &&
            s[1] is { Label: "赠送余额" } && s[1].Amount == 5.99m && s[2] is { Kind: MetricKind.Spend, Label: "累计消费" } && s[2].Amount == 70.74m);
        check("A business error is not shown as a zero balance", DshUsage.Summary(System.Text.Json.JsonDocument.Parse("""{"code":0,"data":{"biz_code":1,"biz_data":null}}""").RootElement) is null);

        await Sessions(root, check);

        using var handler = new Handler(); using var http = new HttpClient(handler);
        var now = DateTimeOffset.Parse("2026-10-05T12:00:00+08:00");
        using var service = new UsageService(new NoDesktop(), locations, http, () => now);
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Dsh }] };
        settings.Normalize();
        check("A DSH row reads its own statistics", settings.Apps[0].UsageSource == ProviderId.Dsh && ProviderCatalog.IsUsageSource(ProviderId.Dsh));
        async Task<ProviderSnapshot> Refresh() => (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Dsh);

        Store("""
            version: 1
            records:
              deepseek-account-platform/default:
                kind: grant
                payload:
                  version: 1
                  token: fixture-token
                  issuer: https://platform.deepseek.com
            """);
        handler.Respond = _ => Reply("""{"code":0,"data":{"biz_code":0,"biz_data":{"normal_wallets":[{"currency":"USD","balance":"2.5"}],"bonus_wallets":[]}}}""");
        var account = await Refresh();
        check("A signed-in DeepSeek Harness reads the platform wallet with its own token header",
            account.LiveQuota && account.Metrics[0] is { Currency: "USD" } && account.Metrics[0].Amount == 2.5m && account.Metrics[1].Amount == 0 &&
            handler.Calls.Last() == ("https://platform.deepseek.com/api/v0/users/get_user_summary", "fixture-token", null));
        handler.Respond = _ => Reply("""{"code":40003,"msg":"unauthorized"}""");
        check("Code 40003 asks for a new sign-in", (await Refresh()) is { LiveQuota: false, StatusLabel: "登录已失效" });
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") };
        check("HTTP 401 asks for a new sign-in", (await Refresh()) is { LiveQuota: false, StatusLabel: "登录已失效" });

        Store("""
            version: 1
            refs:
              DEEPSEEK_API_KEY: sk-fixture-key
            """);
        handler.Respond = _ => Reply("""{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"45.77","granted_balance":"5.99","topped_up_balance":"39.78"}]}""");
        var key = await Refresh();
        check("Without a signed-in account the saved API key reads /user/balance", key.LiveQuota && key.Metrics.Count == 2 &&
            key.Metrics[0].Amount == 45.77m && key.Metrics[1].Amount == 5.99m &&
            handler.Calls.Last() == ("https://api.deepseek.com/user/balance", null, "Bearer sk-fixture-key"));
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"Authentication Fails"}}""") };
        check("A rejected saved key is reported as invalid", (await Refresh()) is { LiveQuota: false, StatusLabel: "密钥无效" });
        File.Delete(DshUsage.Credentials(home));
        check("Without credentials DSH asks the user to sign in", (await Refresh()) is { StatusLabel: "未连接" });
    }

    // Session records in DeepSeek Harness's layout: one header line, then events appended in batches of Zstandard frames.
    private static async Task Sessions(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "dsh-sessions");
        var start = new DateTimeOffset(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Local));
        long At(int hours) => start.AddHours(hours).ToUnixTimeMilliseconds();
        static string Message(string id, long time, int input, int output, int read = 0) =>
            $$$"""{"type":"assistant/message","seq":7,"time":{{{time}}},"data":{"turn":1,"step":1,"message":{"role":"assistant","id":"{{{id}}}","source":{"kind":"model","provider":"deepseek-account","model":"deepseek-flash"}},"usage":{"inputTokens":{{{input}}},"outputTokens":{{{output}}},"cacheReadTokens":{{{read}}},"cacheWriteTokens":0,"totalTokens":{{{input + output + read}}}},"stream":[]}}""";
        const string header = """{"type":"session","version":4,"id":"session-a","createdAt":1,"isSeeded":false,"delegationDepth":0}""";
        static byte[] Frame(params string[] lines) { using var compressor = new ZstdSharp.Compressor(); return compressor.Wrap(System.Text.Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")))).ToArray(); }
        var session = Path.Combine(DshSessions.Folder(home), "--C-work--", "session-a");
        Directory.CreateDirectory(session);
        var log = Path.Combine(session, "session.v4.jsonl.zstd");
        var second = Frame(Message("m2", At(2), 10, 1));
        // The second batch is still being written: only part of its frame is on disk.
        File.WriteAllBytes(log, [.. Frame(header, Message("m1", At(1), 5000, 200, 20000)), .. second[..(second.Length - 5)]]);
        // An older format generation in the same directory holds the same messages again.
        File.WriteAllBytes(Path.Combine(session, "session.jsonl.zstd"), Frame(Message("old", At(1), 99, 99)));
        // A fork copies its parent's events with their ids, then adds its own.
        var fork = Path.Combine(DshSessions.Folder(home), "--C-work--", "session-b");
        Directory.CreateDirectory(fork);
        File.WriteAllText(Path.Combine(fork, "session.v4.jsonl"), header + "\n" + Message("m1", At(1), 5000, 200, 20000) + "\n" + Message("m3", At(3), 7, 3) + "\n");
        var reader = new DshSessions();
        var snapshot = new ProviderSnapshot(ProviderId.Dsh);
        reader.Read(snapshot, home, start, start.AddDays(1));
        check("DSH statistics count each message once, exclude cached input and skip a frame still being written",
            snapshot.UsageAvailable && snapshot.UsageComplete && snapshot.Entries.Select(e => e.Key).SequenceEqual(["dsh:m1", "dsh:m3"]) &&
            snapshot.Entries[0] is { Model: "deepseek-flash", Input: 5000, CacheRead: 20000, Output: 200, Total: 25200 });
        File.WriteAllBytes(log, [.. Frame(header, Message("m1", At(1), 5000, 200, 20000)), .. second, .. Frame(Message("m4", At(4), 1, 1))]);
        snapshot = new ProviderSnapshot(ProviderId.Dsh);
        reader.Read(snapshot, home, start, start.AddDays(1));
        check("Frames appended later are read on the next refresh", snapshot.Entries.Select(e => e.Key).SequenceEqual(["dsh:m1", "dsh:m2", "dsh:m3", "dsh:m4"]));
        File.WriteAllBytes(log, [1, 2, 3, 4, 5, 6, 7, 8]);
        snapshot = new ProviderSnapshot(ProviderId.Dsh);
        reader.Read(snapshot, home, start, start.AddDays(1));
        check("An unreadable session file marks the statistics as incomplete", !snapshot.UsageComplete && snapshot.Entries.Select(e => e.Key).SequenceEqual(["dsh:m1", "dsh:m3"]));
        // A session used for a long time: batches before the start date are not decompressed at all. The early batch here
        // holds a line that would count as an unreadable record if it were read.
        var longHome = Path.Combine(root, "dsh-long");
        var longSession = Path.Combine(DshSessions.Folder(longHome), "--C-work--", "session-c");
        Directory.CreateDirectory(longSession);
        long Day(int days) => start.AddDays(days).ToUnixTimeMilliseconds();
        string broken = $$$"""{"type":"assistant/message","time":{{{Day(-40)}}},"usage":""";
        File.WriteAllBytes(Path.Combine(longSession, "session.v4.jsonl.zstd"),
            [.. Frame(header), .. Frame(broken), .. Frame(Message("early", Day(-35), 1, 1)), .. Frame(Message("before", Day(-1), 2, 2)), .. Frame(Message("inside", Day(1), 3, 3))]);
        snapshot = new ProviderSnapshot(ProviderId.Dsh);
        var longReader = new DshSessions();
        longReader.Read(snapshot, longHome, start, start.AddDays(2));
        check("A first read skips the batches written before the start date", snapshot.UsageComplete && snapshot.Entries.Select(e => e.Key).SequenceEqual(["dsh:inside"]));
        snapshot = new ProviderSnapshot(ProviderId.Dsh);
        longReader.Read(snapshot, longHome, start.AddDays(-36), start.AddDays(2));
        check("An earlier start date reads the earlier batches", snapshot.Entries.Select(e => e.Key).SequenceEqual(["dsh:early", "dsh:before", "dsh:inside"]));

        // C03: a line over the length limit is dropped up to its end instead of growing the buffer without end. Only a dropped
        // line that held usage counts as unreadable; a large tool result is dropped quietly. Lines after it are read.
        var bigHome = Path.Combine(root, "dsh-long-lines");
        var bigSession = Path.Combine(DshSessions.Folder(bigHome), "--C-work--", "session-d");
        Directory.CreateDirectory(bigSession);
        var bigLog = Path.Combine(bigSession, "session.v4.jsonl");
        string tool = $$$"""{"type":"tool/result","time":{{{At(1)}}},"data":"{{{new string('x', 5000)}}}"}""";
        string hugeUsage = Message("huge", At(2), 1, 1).Replace("\"stream\":[]", "\"stream\":[\"" + new string('y', 5000) + "\"]", StringComparison.Ordinal);
        File.WriteAllText(bigLog, header + "\n" + Message("a", At(1), 1, 1) + "\n" + tool + "\n" + Message("b", At(2), 2, 2) + "\n");
        var limited = new DshSessions(maxLine: 1024);
        snapshot = new ProviderSnapshot(ProviderId.Dsh);
        limited.Read(snapshot, bigHome, start, start.AddDays(1));
        check("A line over the limit without usage is dropped quietly and the lines after it are read",
            snapshot.UsageComplete && snapshot.Entries.Select(e => e.Key).SequenceEqual(["dsh:a", "dsh:b"]));
        File.AppendAllText(bigLog, hugeUsage + "\n" + Message("c", At(3), 3, 3) + "\n");
        snapshot = new ProviderSnapshot(ProviderId.Dsh);
        limited.Read(snapshot, bigHome, start, start.AddDays(1));
        check("A dropped line that held usage marks the statistics incomplete, and reading goes on after it",
            !snapshot.UsageComplete && snapshot.Entries.Select(e => e.Key).SequenceEqual(["dsh:a", "dsh:b", "dsh:c"]));
        // Half of a long line is on disk: it is dropped across the two refreshes and counted once, when its end arrives.
        var halfHome = Path.Combine(root, "dsh-half-line");
        var halfSession = Path.Combine(DshSessions.Folder(halfHome), "--C-work--", "session-e");
        Directory.CreateDirectory(halfSession);
        var halfLog = Path.Combine(halfSession, "session.v4.jsonl");
        File.WriteAllText(halfLog, header + "\n" + hugeUsage[..3000]);
        var halfReader = new DshSessions(maxLine: 1024);
        snapshot = new ProviderSnapshot(ProviderId.Dsh);
        halfReader.Read(snapshot, halfHome, start, start.AddDays(1));
        bool waiting = snapshot.UsageComplete;
        File.AppendAllText(halfLog, hugeUsage[3000..] + "\n" + Message("d", At(4), 4, 4) + "\n");
        snapshot = new ProviderSnapshot(ProviderId.Dsh);
        halfReader.Read(snapshot, halfHome, start, start.AddDays(1));
        check("A long line written in two parts is dropped once, when its end arrives", waiting && !snapshot.UsageComplete
            && snapshot.Entries.Select(e => e.Key).SequenceEqual(["dsh:d"]));
        // The "usage" of a dropped line lies across the limit, half in each dropped piece.
        var splitHome = Path.Combine(root, "dsh-split-usage");
        var splitSession = Path.Combine(DshSessions.Folder(splitHome), "--C-work--", "session-f");
        Directory.CreateDirectory(splitSession);
        string splitHead = "{\"type\":\"assistant/message\",\"data\":\"";
        string split = splitHead + new string('z', 1024 - 3 - splitHead.Length - 2) + "\",\"usage\":{\"totalTokens\":1}}";
        File.WriteAllText(Path.Combine(splitSession, "session.v4.jsonl"), split + "\n" + Message("e", At(5), 5, 5) + "\n");
        snapshot = new ProviderSnapshot(ProviderId.Dsh);
        new DshSessions(maxLine: 1024).Read(snapshot, splitHome, start, start.AddDays(1));
        check("A dropped line whose usage lies across the limit still marks the statistics incomplete",
            split.IndexOf("\"usage\"", StringComparison.Ordinal) == 1021 && !snapshot.UsageComplete && snapshot.Entries.Select(e => e.Key).SequenceEqual(["dsh:e"]));
        // Two batches in a row whose first line is over the limit: the search for the first batch does not pass over either.
        var firstHome = Path.Combine(root, "dsh-long-first");
        var firstSession = Path.Combine(DshSessions.Folder(firstHome), "--C-work--", "session-g");
        Directory.CreateDirectory(firstSession);
        File.WriteAllBytes(Path.Combine(firstSession, "session.v4.jsonl.zstd"),
            [.. Frame(header), .. Frame(Message("before", Day(-1), 2, 2)), .. Frame(tool, Message("x1", At(1), 1, 1)), .. Frame(tool, Message("x2", At(2), 1, 1))]);
        snapshot = new ProviderSnapshot(ProviderId.Dsh);
        new DshSessions(maxLine: 1024).Read(snapshot, firstHome, start, start.AddDays(1));
        check("Batches whose first line is over the limit are not skipped on a first read",
            snapshot.UsageComplete && snapshot.Entries.Select(e => e.Key).SequenceEqual(["dsh:x1", "dsh:x2"]));

        snapshot = new ProviderSnapshot(ProviderId.Dsh);
        new DshSessions().Read(snapshot, Path.Combine(root, "dsh-missing"), start, start.AddDays(1));
        check("Without DeepSeek Harness sessions the statistics are unavailable", !snapshot.UsageAvailable && snapshot.Entries.Count == 0);
        await Task.CompletedTask;
    }

    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class NoDesktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => null;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Reply("{}");
        public List<(string Uri, string? Token, string? Authorization)> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host is not ("platform.deepseek.com" or "api.deepseek.com")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
            string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
            lock (Calls) Calls.Add((request.RequestUri.AbsoluteUri, Header("x-dsh-auth-token"), Header("Authorization")));
            return Task.FromResult(Respond(request));
        }
    }
}

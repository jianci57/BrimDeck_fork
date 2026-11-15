using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BrimDeck.Core;

// What a launch waits for: the usage of a quick source reaches the panel while a slow one is still being read, a stalled source keeps
// the statistics it had, the price catalog never holds the usage back and a fresh price cache sends nothing at launch.
static class UsageStartupTests
{
    // A request that is held until the test lets it go, or until its round is cancelled; counts what reached it.
    sealed class Gate : HttpMessageHandler
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            await Release.Task.WaitAsync(cancellation);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    sealed class Desktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => null;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
        // Stands in for DPAPI: the stored value is the base64 of "sealed:" and the token.
        public string? UnprotectUserData(string base64)
        { var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64)); return text.StartsWith("sealed:") ? text[7..] : null; }
    }

    sealed class Replies(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Interlocked.Increment(ref Calls); return Task.FromResult(answer(request.RequestUri!)); }
    }

    public static async Task Run(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "usage-startup"); Directory.CreateDirectory(folder);
        // Pi has no network: its statistics are ready as soon as its folder is read. CodeAgent has a record and a quota request to wait for
        // (one row reads the quota of the one and the statistics of the other).
        var pi = Path.Combine(folder, "pi"); Directory.CreateDirectory(pi);
        var home = Path.Combine(folder, "codeagent");
        var project = Path.Combine(home, "projects", "D--work"); Directory.CreateDirectory(project);
        var now = DateTimeOffset.Now;
        File.WriteAllText(Path.Combine(project, "session-1.jsonl"), JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = now.AddMinutes(-1).ToString("O"), _cac_modelId = "maas-test",
            message = new { id = "msg_1", type = "message", role = "assistant", model = "maas-test", usage = new { input_tokens = 100, cache_read_input_tokens = 0, cache_creation_input_tokens = 0, output_tokens = 20 } }
        }) + "\n");
        File.WriteAllText(CodeAgentUsage.CredentialFile(home), JsonSerializer.Serialize(new
        { idaasOAuth = new { xAuthToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sealed:secret-token")), xAuthExpires = now.AddDays(2).ToUnixTimeMilliseconds(), mtoken = "x" } }));
        var locations = new DataLocations("", "", "", "", "") { CodeAgentHome = home, PiSessions = pi };
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.CodeAgent, UsageSource = ProviderId.Pi }] };
        settings.Normalize();

        // (a) A quick source is published before a slow one ends, and the whole result still follows.
        using (var gate = new Gate())
        {
            using var http = new HttpClient(gate);
            using var service = new UsageService(new Desktop(), locations, http, () => now);
            var published = new List<(TimeSpan At, List<ProviderId> Ids)>();
            var timer = Stopwatch.StartNew();
            var round = service.RefreshAsync(settings, default, null, list => { lock (published) published.Add((timer.Elapsed, list.Select(s => s.Id).ToList())); });
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(10) && !published.Any(p => p.Ids.Contains(ProviderId.Pi))) await Task.Delay(10);
            TimeSpan first = published.FirstOrDefault(p => p.Ids.Contains(ProviderId.Pi)).At;
            bool early = published.Count > 0 && !round.IsCompleted && !published.Any(p => p.Ids.Contains(ProviderId.CodeAgent));
            // The slow source is held for a fixed time, so the gain is measured against a result that waits for it.
            await Task.Delay(1500);
            gate.Release.SetResult();
            var result = await round;
            var whole = timer.Elapsed;
            check("A quick source's snapshot is published while a slow one is still being read", early);
            check("The round that was published in parts still returns every source whole",
                result.Select(s => s.Id).Order().SequenceEqual(new[] { ProviderId.Pi, ProviderId.CodeAgent }.Order()) && result.Single(s => s.Id == ProviderId.CodeAgent).Entries.Count == 1);
            Console.WriteLine($"MEASURE: fake round with a 1.5 s source: first snapshot at {first.TotalMilliseconds:F0} ms, whole result at {whole.TotalMilliseconds:F0} ms");
            check("The first snapshot comes long before the whole result", first < whole - TimeSpan.FromSeconds(1));
        }

        // The progress callback is only for the first round; later rounds reach the panel whole, as before.
        using (var gate = new Gate())
        {
            gate.Release.SetResult();
            using var http = new HttpClient(gate);
            using var service = new UsageService(new Desktop(), locations, http, () => now);
            int calls = 0;
            await service.RefreshAsync(settings, default, null, _ => Interlocked.Increment(ref calls));
            int afterFirst = calls;
            await service.RefreshAsync(settings, default, null, _ => Interlocked.Increment(ref calls));
            check("Only the first round publishes in parts", afterFirst >= 1 && calls == afterFirst);
        }

        // (b) A source that does not end by the deadline keeps the statistics it read and shows only its quota as not read.
        using (var gate = new Gate())
        {
            using var http = new HttpClient(gate);
            using var service = new UsageService(new Desktop(), locations, http, () => now) { Deadline = TimeSpan.FromMilliseconds(400) };
            var timer = Stopwatch.StartNew();
            var result = await service.RefreshAsync(settings);
            var stalled = result.Single(s => s.Id == ProviderId.CodeAgent);
            check("A source cut off by the deadline keeps its local statistics", timer.Elapsed < TimeSpan.FromSeconds(8) && gate.Calls >= 1 && stalled.UsageAvailable && stalled.Entries.Count == 1 && stalled.Entries.Sum(e => e.Total) == 120);
            check("The cut-off source says its quota was not read, and has none", stalled.StatusLabel == Loc.T("读取超时", "Timed out") && !stalled.LiveQuota && stalled.Quotas.Count == 0);
            check("The source that had finished is unaffected", result.Single(s => s.Id == ProviderId.Pi).UsageAvailable);
        }

        // (b3) A configured source still being read at the deadline shows a timeout; the other sources' results stay.
        using (var gate = new Gate())
        {
            using var http = new HttpClient(gate);
            using var service = new UsageService(new Desktop(), locations, http, () => now) { Deadline = TimeSpan.FromMilliseconds(400) };
            var custom = new AppEntry { QuotaSource = ProviderId.Custom, Script = """async function fetchUsage(ctx) { await ctx.http.get("https://example.test/usage"); return [{ title: "x", percent: 1 }]; }""" };
            custom.Normalize();
            var mixed = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.CodeAgent, UsageSource = ProviderId.Pi }, custom] };
            mixed.Normalize();
            var result = await service.RefreshAsync(mixed);
            var shown = result.FirstOrDefault(s => s.Id == ProviderId.Custom);
            check("A configured source cut off by the deadline shows a timeout", shown is not null && shown.StatusLabel == Loc.T("读取超时", "Timed out") && shown.Configurations.Contains(custom.ConfigurationKey));
            check("The other sources' results are kept when a configured source is cut off", result.Any(s => s.Id == ProviderId.Pi) && result.Single(s => s.Id == ProviderId.CodeAgent).Entries.Count == 1);
        }

        // A cancelled lifetime still cancels the round instead of returning a timeout snapshot.
        using (var gate = new Gate())
        {
            using var http = new HttpClient(gate);
            using var service = new UsageService(new Desktop(), locations, http, () => now);
            using var lifetime = new CancellationTokenSource(300);
            bool cancelled = false;
            try { await service.RefreshAsync(settings, lifetime.Token); } catch (OperationCanceledException) { cancelled = true; }
            check("Closing the application ends a round with a cancellation, not a snapshot", cancelled);
        }

        // (b2) A source that takes long is named in the trace output, with its time and nothing else.
        using (var gate = new Gate())
        {
            var lines = new List<string>();
            var listener = new Listener(lines);
            Trace.Listeners.Add(listener);
            try
            {
                using var http = new HttpClient(gate);
                using var service = new UsageService(new Desktop(), locations, http, () => now) { SlowProviderLog = TimeSpan.FromMilliseconds(200) };
                var round = service.RefreshAsync(settings);
                await Task.Delay(400); gate.Release.SetResult(); await round;
            }
            finally { Trace.Listeners.Remove(listener); }
            lock (lines)
                check("A source slower than the limit is logged with its name and milliseconds", lines.Count(l => l.StartsWith("BrimDeck usage: CodeAgent took ", StringComparison.Ordinal) && l.EndsWith(" ms", StringComparison.Ordinal)) == 1 &&
                    !lines.Any(l => l.Contains("Pi took", StringComparison.Ordinal)) && !lines.Any(l => l.Contains("secret", StringComparison.Ordinal)));
        }

        // (c) A price refresh that never ends does not hold the usage back, is not started twice, and a refresh that ends is announced once.
        {
            int starts = 0, landed = 0;
            var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var prices = new BackgroundPrices(async cancellation => { Interlocked.Increment(ref starts); await hang.Task.WaitAsync(cancellation); return true; }, () => Interlocked.Increment(ref landed));
            var timer = Stopwatch.StartNew();
            prices.Start(default); prices.Start(default);
            bool quick = timer.Elapsed < TimeSpan.FromMilliseconds(500);
            using var gate = new Gate(); gate.Release.SetResult();
            using var http = new HttpClient(gate);
            using var service = new UsageService(new Desktop(), locations, http, () => now);
            var result = await service.RefreshAsync(settings);
            check("The usage is ready while the price refresh is still hanging", quick && result.Count == 2 && !prices.Running!.IsCompleted && landed == 0);
            for (var spin = Stopwatch.StartNew(); starts == 0 && spin.Elapsed < TimeSpan.FromSeconds(5);) await Task.Delay(5);
            await Task.Delay(50);
            check("A refresh still under way is not started again", starts == 1);
            hang.SetResult(); await prices.Running!;
            check("A refresh that ends tells the panel to work the costs out again", landed == 1);
            prices.Start(default); await prices.Running!;
            check("The next Start refreshes again", starts == 2 && landed == 2);
            int quietLanded = 0;
            var quiet = new BackgroundPrices(_ => Task.FromResult(false), () => quietLanded++);
            quiet.Start(default); await quiet.Running!;
            check("A refresh that changed nothing does not tell the panel to redraw", quietLanded == 0);

            using var stop = new CancellationTokenSource();
            int cancelledLanded = 0;
            var stopped = new BackgroundPrices(async cancellation => { await Task.Delay(Timeout.Infinite, cancellation); return true; }, () => cancelledLanded++);
            stopped.Start(stop.Token); stop.Cancel(); await stopped.Running!;
            var failing = new BackgroundPrices(Task<bool> (_) => throw new InvalidOperationException("boom"), () => cancelledLanded++);
            failing.Start(default); await failing.Running!;
            check("A cancelled or failed price refresh announces nothing", cancelledLanded == 0);
        }

        // (d) A price cache younger than the refresh interval sends no request at launch; an old one does.
        {
            var cache = Path.Combine(folder, "prices"); Directory.CreateDirectory(cache);
            static string Price(int n) => $$"""{"prompt":"{{n * 1e-6m}}","completion":"{{n * 5e-6m}}","input_cache_read":"{{n * 1e-7m}}"}""";
            var catalog = "{\"data\":[" + string.Join(",", new[] { "acme/model-a", "acme/model-b" }.Select(id => $$"""{"id":"{{id}}","pricing":{{Price(1)}}}""")) + "]}";
            var endpoints = "{\"data\":{\"endpoints\":[{\"provider_name\":\"x\",\"tag\":\"moonshotai\",\"pricing\":" + Price(7) + "}]}}";
            var server = new Replies(uri => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(uri.AbsolutePath == "/api/v1/models" ? catalog : endpoints) });
            var at = DateTimeOffset.Parse("2026-11-10T08:00:00Z");
            using (var http = new HttpClient(server))
            using (var pricing = new Pricing(cache, http, () => at))
            {
                await pricing.RefreshAsync();
                check("With no cache the first refresh asks for the catalog and the Kimi prices", server.Calls == 1 + Pricing.OfficialModels.Count && File.Exists(Path.Combine(cache, Pricing.OfficialFile)));
            }
            server.Calls = 0;
            using (var http = new HttpClient(server))
            using (var pricing = new Pricing(cache, http, () => at.AddHours(3)))
            {
                await pricing.RefreshAsync();
                check("A cache from three hours ago sends no request at launch", server.Calls == 0 && pricing.ModelCount > 0);
            }
            using (var http = new HttpClient(server))
            using (var pricing = new Pricing(cache, http, () => at.AddHours(23)))
            {
                await pricing.RefreshAsync();
                check("A cache just inside the interval still sends none", server.Calls == 0);
            }
            using (var http = new HttpClient(server))
            using (var pricing = new Pricing(cache, http, () => at.AddDays(2)))
            {
                await pricing.RefreshAsync();
                check("A cache older than the interval is refreshed at launch, catalog and Kimi prices", server.Calls == 1 + Pricing.OfficialModels.Count);
            }
            // An entry that is null in the saved official prices is ignored; the others still seed, and the prices still load.
            var officialText = File.ReadAllText(Path.Combine(cache, Pricing.OfficialFile));
            File.WriteAllText(Path.Combine(cache, Pricing.OfficialFile), officialText.Replace("\"models\":[", "\"models\":[null,", StringComparison.Ordinal));
            check("The test's official prices really hold a null entry", File.ReadAllText(Path.Combine(cache, Pricing.OfficialFile)).Contains("[null,", StringComparison.Ordinal));
            server.Calls = 0;
            using (var http = new HttpClient(server))
            using (var pricing = new Pricing(cache, http, () => at.AddDays(2).AddHours(1)))
            {
                await pricing.RefreshAsync();
                check("A null entry in the saved official prices does not stop the prices loading, and the valid ones seed", pricing.ModelCount > 0 && server.Calls == 0);
            }
            // A catalog written by another version of the program (no stamp) is used, but fetched again at launch.
            var catalogFile = Path.Combine(cache, "model-prices.json");
            var stampedText = File.ReadAllText(catalogFile);
            var legacy = System.Text.RegularExpressions.Regex.Replace(stampedText, "\"stamp\":\"[^\"]*\",?", "");
            check("The test's catalog really carries a stamp", stampedText.Contains("\"stamp\"", StringComparison.Ordinal) && legacy != stampedText);
            File.WriteAllText(catalogFile, legacy);
            File.SetLastWriteTimeUtc(catalogFile, DateTime.UtcNow);
            server.Calls = 0;
            using (var http = new HttpClient(server))
            using (var pricing = new Pricing(cache, http, () => DateTimeOffset.UtcNow))
            {
                check("A catalog without the stamp is still read", pricing.ModelCount > 0);
                await pricing.RefreshAsync();
                check("A catalog without this version's stamp is fetched again at launch", server.Calls >= 1);
            }
            // The Kimi prices a failed read left out of the file are asked for again, though the file is new.
            var official = Path.Combine(cache, Pricing.OfficialFile);
            File.WriteAllText(official, JsonSerializer.Serialize(new { updatedAt = DateTimeOffset.UtcNow, models = Array.Empty<ModelPrice>() }));
            server.Calls = 0;
            using (var http = new HttpClient(server))
            using (var pricing = new Pricing(cache, http, () => DateTimeOffset.UtcNow))
            {
                await pricing.RefreshAsync();
                check("Models the saved official prices hold nothing for are asked for at launch", server.Calls == Pricing.OfficialModels.Count);
            }
        }

        // The throttle hands a burst over once at once and once more when the interval ends, with the newest list.
        {
            var seen = new List<int>();
            using (var publisher = new SnapshotPublisher(list => { lock (seen) seen.Add(list.Count); }, TimeSpan.FromMilliseconds(150)))
            {
                for (int i = 1; i <= 6; i++) publisher.Publish(Enumerable.Range(0, i).Select(_ => new ProviderSnapshot(ProviderId.Pi)).ToList());
                int immediate; lock (seen) immediate = seen.Count;
                await Task.Delay(400);
                lock (seen) check("A burst is sent at once, then once more with the newest list", immediate == 1 && seen.SequenceEqual([1, 6]));
            }
            var late = new List<int>();
            var held = new SnapshotPublisher(list => { lock (late) late.Add(list.Count); }, TimeSpan.FromMilliseconds(150));
            held.Publish([new ProviderSnapshot(ProviderId.Pi)]); held.Publish([new ProviderSnapshot(ProviderId.Pi), new ProviderSnapshot(ProviderId.Codex)]);
            held.Dispose(); await Task.Delay(350);
            lock (late) check("A list still held when the round ends is dropped (the whole result follows it)", late.SequenceEqual([1]));
        }

        // (5) Organizations are read side by side: an account with several of them waits for the slowest, not for all.
        {
            var claude = Path.Combine(folder, "claude"); Directory.CreateDirectory(claude);
            var desktop = new ClaudeFixtureSources
            {
                State = new(true, [new ClaudeCredential("token-a", "pro", ClaudeSignIn.Desktop, "acct", "org-a"), new ClaudeCredential("token-b", "team", ClaudeSignIn.Desktop, "acct", "org-b"),
                    new ClaudeCredential("token-c", "team", ClaudeSignIn.Desktop, "acct", "org-c")])
            };
            var delay = TimeSpan.FromMilliseconds(600);
            var started = new List<DateTimeOffset>();
            string Body(int used) => JsonSerializer.Serialize(new { five_hour = new { utilization = used, resets_at = now.AddHours(4) }, seven_day = new { utilization = used + 20, resets_at = now.AddDays(5) } });
            var server = new Delayed(request => { if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/usage", StringComparison.Ordinal)) { lock (started) started.Add(DateTimeOffset.UtcNow); return delay; } return TimeSpan.Zero; },
                request => request.RequestUri!.AbsolutePath.EndsWith("/oauth/usage", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body(10)) } : new HttpResponseMessage(HttpStatusCode.NotFound));
            using var http = new HttpClient(server);
            var claudeLocations = new DataLocations(claude, "", "", "", "");
            using var service = new UsageService(desktop, claudeLocations, http, () => now);
            var timer = Stopwatch.StartNew();
            var snapshot = (await service.RefreshAsync(new DeckSettings { Apps = [new AppEntry { Id = ProviderId.Claude }] })).Single();
            // The first organization is read first (it names the others), then the two others together: about two delays, not three.
            check("The other organizations of an account are read together", started.Count == 3 && timer.Elapsed < delay * 2.7 && timer.Elapsed >= delay * 1.8);
            check("Every organization's quotas are shown", snapshot.LiveQuota && snapshot.Quotas.Count == 6);
        }
    }

    sealed class Delayed(Func<HttpRequestMessage, TimeSpan> delay, Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { var wait = delay(request); if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellation); return answer(request); }
    }

    sealed class Listener(List<string> lines) : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message is not null) lock (lines) lines.Add(message); }
    }
}

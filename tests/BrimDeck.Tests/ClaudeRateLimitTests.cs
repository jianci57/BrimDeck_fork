using System.Net;
using System.Text.Json;
using BrimDeck.Core;

// The pause after a 429 from the Claude quota API, and the reading kept from before it. Time is a clock the test sets, and the
// share added to a pause is fixed or bracketed: nothing here waits or depends on the wall clock.
static class ClaudeRateLimitTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        Pure(check);
        await Service(root, check);
    }

    static HttpResponseMessage Limited(string? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    static void Pure(Action<string, bool> check)
    {
        var at = new DateTimeOffset(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
        check("Retry-After in seconds is read from the response", RateLimitedException.From(Limited("120")).RetryAfter(at) == TimeSpan.FromSeconds(120));
        check("Retry-After as an HTTP date is read relative to the clock given", RateLimitedException.From(Limited("Wed, 21 Oct 2026 07:28:00 GMT")).RetryAfter(at) == TimeSpan.FromMinutes(28));
        check("A response without Retry-After, or with an unreadable one, gives no wait", RateLimitedException.From(Limited(null)).RetryAfter(at) is null &&
            RateLimitedException.From(Limited("soon")).RetryAfter(at) is null);
        check("A 429 is still an HttpRequestException with its status", RateLimitedException.From(Limited(null)) is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests });

        TimeSpan Delay(int failures, TimeSpan? told = null, double jitter = 0) => RateLimitBackoff.Delay(failures, told, jitter);
        var grown = Enumerable.Range(1, 8).Select(failures => Delay(failures)).ToList();
        check("Without Retry-After the pause doubles with each failure, then stops at the longest", grown.Take(5).SequenceEqual(
            [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(16), TimeSpan.FromMinutes(30)]) &&
            grown.Skip(4).All(delay => delay == RateLimitBackoff.MaxGrowth));
        check("Failures far beyond any count keep the longest pause, with no overflow", Delay(int.MaxValue) == RateLimitBackoff.MaxGrowth && Delay(0) == RateLimitBackoff.FirstDelay);
        check("A Retry-After inside the limits is followed as sent", Delay(1, TimeSpan.FromMinutes(10)) == TimeSpan.FromMinutes(10));
        check("A Retry-After that is zero, negative or very short is raised to the shortest pause",
            Delay(1, TimeSpan.Zero) == RateLimitBackoff.MinDelay && Delay(1, TimeSpan.FromSeconds(-5)) == RateLimitBackoff.MinDelay && Delay(1, TimeSpan.FromSeconds(3)) == RateLimitBackoff.MinDelay);
        check("A Retry-After longer than an hour is kept as the server sent it", Delay(1, TimeSpan.FromHours(2)) == TimeSpan.FromHours(2) &&
            Delay(1, TimeSpan.FromDays(3)) == TimeSpan.FromDays(3) && Delay(4, TimeSpan.FromHours(2)) == TimeSpan.FromHours(2) && Delay(1, RateLimitBackoff.MaxPlausibleRetryAfter) == RateLimitBackoff.MaxPlausibleRetryAfter);
        check("A Retry-After beyond a week is taken for a mistake and the growth decides", Delay(1, RateLimitBackoff.MaxPlausibleRetryAfter + TimeSpan.FromSeconds(1)) == Delay(1) &&
            Delay(3, TimeSpan.FromDays(400)) == Delay(3) && Delay(5, TimeSpan.MaxValue) == RateLimitBackoff.MaxGrowth);
        check("A repeated short Retry-After does not hold the pause below the growth", Delay(1, TimeSpan.FromSeconds(30)) == RateLimitBackoff.MinDelay &&
            Delay(3, TimeSpan.FromSeconds(30)) == TimeSpan.FromMinutes(8) && Delay(3, TimeSpan.FromMinutes(20)) == TimeSpan.FromMinutes(20));
        var spread = Enumerable.Range(0, 11).Select(step => Delay(2, null, step / 10d)).ToList();
        check("The share added to a pause is never taken from it and never more than a tenth", spread[0] == TimeSpan.FromMinutes(4) &&
            spread.Zip(spread.Skip(1)).All(pair => pair.Second >= pair.First) && spread[^1] == TimeSpan.FromMinutes(4) * 1.1 &&
            Delay(2, null, -3) == spread[0] && Delay(2, null, 9) == spread[^1] && Delay(2, null, double.NaN) == spread[0]);

        var limit = new RateLimitBackoff();
        var first = limit.Fail(at, null, 0);
        check("A 429 starts a pause that holds until its end", first == at + TimeSpan.FromMinutes(2) && limit.RetryAt(at.AddMinutes(1)) == first && limit.RetryAt(first.AddTicks(-1)) == first);
        check("The pause ends at its time", limit.RetryAt(first) is null && limit.RetryAt(first.AddMinutes(5)) is null);
        var second = limit.Fail(first, null, 0);
        check("A further 429 pauses longer, and the count survives the pause that ended", limit.Failures == 2 && second == first + TimeSpan.FromMinutes(4));
        limit.Reset();
        check("An answer that is not a 429 clears the pause and the count", limit.Failures == 0 && limit.RetryAt(at) is null && limit.Fail(at, null, 0) == first);
        // The clock was set back after the pause began (a time sync, a manual change): the wait left is cut to the pause that was
        // decided, counted from the new time, however far the clock went back.
        var told = limit.Fail(at, TimeSpan.FromHours(2), 0);
        check("A Retry-After of two hours holds for two hours", told == at.AddHours(2) && limit.RetryAt(at.AddMinutes(119)) == told && limit.RetryAt(at.AddMinutes(121)) is null);
        var back = at.AddDays(-2);
        check("A clock set back far cannot hold requests for longer than the pause itself", limit.RetryAt(back) == back.AddHours(2) && limit.RetryAt(back.AddMinutes(119)) == back.AddHours(2) &&
            limit.RetryAt(back.AddMinutes(121)) is null);
        limit.Fail(at, TimeSpan.FromHours(2), 0);
        check("A clock set back by an hour leaves the two hours counted from the new time", limit.RetryAt(at.AddHours(-1)) == at.AddHours(1));
    }

    // Answers the usage endpoint from a script and counts the requests; the account endpoint is not counted.
    sealed class Handler : HttpMessageHandler
    {
        public List<string> Usage { get; } = [];
        public Func<string, HttpResponseMessage> Answer { get; set; } = _ => new(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == "https://api.anthropic.com/api/oauth/profile") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var token = request.Headers.Authorization?.Parameter ?? "";
            Usage.Add(token);
            return Task.FromResult(Answer(token));
        }
    }

    static async Task Service(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "limit-home");
        var locations = DataLocations.Resolve(home, Path.Combine(root, "limit-roaming"), Path.Combine(root, "limit-local"), null, "");
        Directory.CreateDirectory(locations.ClaudeHome);
        var credentials = Path.Combine(locations.ClaudeHome, ".credentials.json");
        var profile = Path.Combine(locations.ClaudeHome, ".claude.json");
        void SignIn(string token, string account)
        {
            File.WriteAllText(credentials, JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = token, subscriptionType = "max" } }));
            File.WriteAllText(profile, JsonSerializer.Serialize(new { oauthAccount = new { accountUuid = account } }));
        }
        SignIn("token-a", "account-a");
        var cache = Path.Combine(root, "limit-cache", ClaudeQuotaCache.FileName);
        var settings = new DeckSettings { Apps = [new AppEntry { Id = ProviderId.Claude }] };
        // A fixed morning, so no time shown here crosses midnight (the status adds the date then).
        var start = new DateTimeOffset(new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Local));
        var clock = start;
        string Body(int used) => JsonSerializer.Serialize(new
        {
            five_hour = new { utilization = used, resets_at = start.AddHours(4) },
            seven_day = new { utilization = used + 20, resets_at = start.AddDays(5) }
        });
        string Hm(DateTimeOffset time) => time.LocalDateTime.ToString("HH:mm");
        var desktop = new ClaudeFixtureSources { State = new(false, []) };
        // No share is added to a pause unless a test says so.
        double added = 0;

        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using (var service = new UsageService(desktop, locations, http, () => clock, quotaCache: cache, jitter: () => added))
        {
            Task<ProviderSnapshot> Read() => service.RefreshAsync(settings).ContinueWith(task => task.Result.Single());
            handler.Answer = _ => new(HttpStatusCode.OK) { Content = new StringContent(Body(12)) };
            var online = await Read();
            check("An online reading connects and is kept for the account", online.LiveQuota && !online.IsStale && handler.Usage.Count == 1 && File.Exists(cache));
            var kept = File.ReadAllBytes(cache);

            // A 429 with Retry-After pauses requests; the reading from before stays on show and the status names the time.
            clock = start.AddMinutes(1);
            handler.Answer = _ => Limited("600");
            var limited = await Read();
            var retryAt = clock.AddSeconds(600);
            check("A 429 shows the last reading as old, with the time it was read and the time of the next request", handler.Usage.Count == 2 && limited.IsStale && limited.Quotas.Count == 2 &&
                limited.Quotas.Single(q => q.Minutes == 300).UsedPercent == 12 && limited.QuotaTime == start && limited.StatusLabel == "稍后重试" &&
                limited.Status == $"请求频繁，显示 {Hm(start)} 读取的配额，{Hm(retryAt)} 后自动重试");
            check("A 429 does not rewrite the kept reading", File.ReadAllBytes(cache).SequenceEqual(kept));

            var requests = handler.Usage.Count;
            bool silent = true, stillShown = true;
            foreach (var minute in new[] { 2, 3, 5, 8 })
            {
                clock = start.AddMinutes(minute);
                var paused = await Read();
                silent &= handler.Usage.Count == requests;
                stillShown &= paused.IsStale && paused.Quotas.Count == 2 && paused.Status.Contains(Hm(retryAt));
            }
            check("During the pause no request goes out, even on the 60-second cycle", silent);
            check("During the pause the reading from before stays on show with the same time of the next request", stillShown);
            // A manual refresh and a refresh after a setting changed call the same method, at the same minute.
            await Read(); await Read();
            check("A manual refresh does not go round the pause", handler.Usage.Count == requests);

            // The wait ended: one request goes out, and a success clears the pause.
            clock = start.AddMinutes(12);
            handler.Answer = _ => new(HttpStatusCode.OK) { Content = new StringContent(Body(30)) };
            var resumed = await Read();
            check("When the pause ends the next refresh asks again and shows the new reading", handler.Usage.Count == requests + 1 && !resumed.IsStale && resumed.Quotas.Single(q => q.Minutes == 300).UsedPercent == 30);
            clock = start.AddMinutes(13);
            await Read();
            check("A success clears the pause: the refresh after it asks again", handler.Usage.Count == requests + 2);
        }

        // Without a Retry-After the pause grows over several 429s, and a success sets it back to the first.
        File.Delete(cache);
        handler.Usage.Clear();
        using (var service = new UsageService(desktop, locations, http, () => clock, quotaCache: cache, jitter: () => added))
        {
            Task<ProviderSnapshot> Read() => service.RefreshAsync(settings).ContinueWith(task => task.Result.Single());
            async Task<int> Asked(double minutes) { clock = start.AddMinutes(minutes); var before = handler.Usage.Count; await Read(); return handler.Usage.Count - before; }
            handler.Answer = _ => Limited(null);
            var cases = new List<(double Minute, int Asked, string Why)>();
            cases.Add((0, await Asked(0), "first request"));              // 429 → paused 2 minutes, until minute 2
            cases.Add((1, await Asked(1), "held"));
            cases.Add((2, await Asked(2), "after 2 minutes"));            // 429 → paused 4 minutes, until minute 6
            cases.Add((5, await Asked(5), "held"));
            cases.Add((6, await Asked(6), "after 4 more minutes"));       // 429 → paused 8 minutes, until minute 14
            cases.Add((13, await Asked(13), "held"));
            cases.Add((14, await Asked(14), "after 8 more minutes"));     // 429 → paused 16 minutes, until minute 30
            check("Without Retry-After each 429 pauses for longer than the one before", cases.Select(c => c.Asked).SequenceEqual([1, 0, 1, 0, 1, 0, 1]));
            var noCache = (await service.RefreshAsync(settings)).Single();
            check("A 429 with nothing kept says when the next request goes out, in the status the column shows", !noCache.LiveQuota && noCache.Quotas.Count == 0 && noCache.StatusLabel == "暂不可用" &&
                noCache.Status == $"请求频繁，{Hm(start.AddMinutes(30))} 后自动重试");
            Loc.Use(Loc.English);
            try { check("The status is shown in the language of the day", (await service.RefreshAsync(settings)).Single().Status == $"Too many requests. Retrying automatically after {Hm(start.AddMinutes(30))}."); }
            finally { Loc.Use(Loc.Chinese); }
            handler.Answer = _ => new(HttpStatusCode.OK) { Content = new StringContent(Body(12)) };
            var recovered = await Asked(30);
            handler.Answer = _ => Limited(null);
            var afterSuccess = new List<int> { await Asked(31), await Asked(32), await Asked(33) };
            check("After a success the first pause is the short one again", recovered == 1 && afterSuccess.SequenceEqual([1, 0, 1]));
        }

        // A restart while the API is still limiting: the reading kept on disk is shown, bound to the account that was signed in.
        File.Delete(cache);
        handler.Usage.Clear();
        clock = start;
        using (var before = new UsageService(desktop, locations, http, () => clock, quotaCache: cache))
        {
            handler.Answer = _ => new(HttpStatusCode.OK) { Content = new StringContent(Body(12)) };
            await before.RefreshAsync(settings);
        }
        var text = File.ReadAllText(cache);
        check("The kept reading holds neither the token, the account id nor any status text", !text.Contains("token-a") && !text.Contains("account-a") && !text.Contains("请求频繁") &&
            text.Contains(ClaudeQuotaCache.AccountHash("account-a")));
        handler.Answer = _ => Limited("900");
        clock = start.AddMinutes(20);
        using (var after = new UsageService(desktop, locations, http, () => clock, quotaCache: cache, jitter: () => added))
        {
            var shown = (await after.RefreshAsync(settings)).Single();
            check("After a restart a 429 shows the reading kept before it, with its time and the time of the next request", shown.LiveQuota && shown.IsStale && shown.Quotas.Count == 2 &&
                shown.QuotaTime == start && shown.Status == $"请求频繁，显示 {Hm(start)} 读取的配额，{Hm(clock.AddSeconds(900))} 后自动重试");
        }
        // Signed in to another account: the other account's reading is never shown, and its requests are not held by the first's pause.
        SignIn("token-b", "account-b");
        using (var other = new UsageService(desktop, locations, http, () => clock, quotaCache: cache, jitter: () => added))
        {
            var requests = handler.Usage.Count;
            var shown = (await other.RefreshAsync(settings)).Single();
            check("Another account does not show the reading kept for the first", !shown.LiveQuota && shown.Quotas.Count == 0 && handler.Usage.Count == requests + 1);
        }
        SignIn("token-a", "account-a");

        // The pause counts from the moment the answer arrived. The request is slow here (the clock moves 10 seconds while it is
        // answered), so a Retry-After of a minute ends a minute after the answer, not a minute after the round began.
        File.Delete(cache);
        using (var service = new UsageService(desktop, locations, http, () => clock, quotaCache: cache, jitter: () => added))
        {
            Task<ProviderSnapshot> Read() => service.RefreshAsync(settings).ContinueWith(task => task.Result.Single());
            async Task<int> Asked(DateTimeOffset at) { clock = at; var before = handler.Usage.Count; await Read(); return handler.Usage.Count - before; }
            var round = start.AddDays(10);
            handler.Answer = _ => { clock = clock.AddSeconds(10); return Limited("60"); };
            var sent = await Asked(round);
            var arrival = round.AddSeconds(10);
            var heldUntilEnd = await Asked(arrival.AddSeconds(59));
            handler.Answer = _ => { clock = clock.AddSeconds(10); return Limited("7200"); };
            var after = await Asked(arrival.AddSeconds(60));
            var twoHours = clock;
            var heldTwoHours = await Asked(twoHours.AddMinutes(119));
            var resumedAfter = await Asked(twoHours.AddMinutes(121));
            check("A Retry-After counts from the answer's arrival: 60 seconds received 10 seconds into the round ends 70 seconds in", sent == 1 && heldUntilEnd == 0 && after == 1);
            check("A Retry-After of two hours is kept for two hours, not cut to an hour", heldTwoHours == 0 && resumedAfter == 1);
        }
        using (var service = new UsageService(desktop, locations, http, () => clock, quotaCache: cache, jitter: () => added))
        {
            Task<ProviderSnapshot> Read() => service.RefreshAsync(settings).ContinueWith(task => task.Result.Single());
            async Task<int> Asked(DateTimeOffset at) { clock = at; var before = handler.Usage.Count; await Read(); return handler.Usage.Count - before; }
            handler.Answer = _ => Limited(DateTimeOffset.UtcNow.AddYears(5).ToString("R"));
            var late = start.AddDays(20);
            var mistaken = await Asked(late);
            var heldOneMinute = await Asked(late.AddMinutes(1));
            var afterGrowth = await Asked(late.AddMinutes(3));
            check("A Retry-After years away is taken for a mistake: the pause is the growth, not years", mistaken == 1 && heldOneMinute == 0 && afterGrowth == 1);
        }

        // Accounts and sign-ins are paused separately within one run.
        using (var service = new UsageService(desktop, locations, http, () => clock, quotaCache: cache, jitter: () => added))
        {
            Task<ProviderSnapshot> Read() => service.RefreshAsync(settings).ContinueWith(task => task.Result.Single());
            async Task<int> Asked() { var before = handler.Usage.Count; await Read(); return handler.Usage.Count - before; }
            handler.Answer = _ => Limited("1800");
            var firstAsk = await Asked(); clock = clock.AddMinutes(1);
            var held = await Asked();
            SignIn("token-b", "account-b");
            var otherAccount = await Asked();
            SignIn("token-a", "account-a");
            var back = await Asked();
            check("A pause belongs to its account: another account asks at once, and the first stays paused when it returns", firstAsk == 1 && held == 0 && otherAccount == 1 && back == 0);
            SignIn("token-a2", "account-a");
            var newToken = await Asked();
            check("A new sign-in of the same account starts without the old pause", newToken == 1);
        }

        // The tokens of one Desktop account are tried in turn when the first is refused; the pause of the one that was limited stays.
        File.Delete(credentials); File.Delete(profile);
        desktop.State = new(true, [new("token-old", "max", ClaudeSignIn.Desktop, "account-d"), new("token-new", "max", ClaudeSignIn.Desktop, "account-d")]);
        using (var service = new UsageService(desktop, locations, http, () => clock, jitter: () => added))
        {
            Task<ProviderSnapshot> Read() => service.RefreshAsync(settings).ContinueWith(task => task.Result.Single());
            handler.Answer = token => token == "token-old" ? new(HttpStatusCode.Unauthorized) : Limited("1800");
            clock = start.AddDays(2);
            handler.Usage.Clear();
            await Read();
            var firstRound = handler.Usage.ToList();
            handler.Usage.Clear(); clock = clock.AddMinutes(1);
            await Read();
            check("A refused token before a limited one does not lift the limited one's pause", firstRound.SequenceEqual(["token-old", "token-new"]) && handler.Usage.SequenceEqual(["token-old"]));
        }
        desktop.State = new(false, []);
        SignIn("token-a", "account-a");

        // Failures that are not rate limiting do not start a pause.
        using (var service = new UsageService(desktop, locations, http, () => clock, quotaCache: cache, jitter: () => added))
        {
            Task<ProviderSnapshot> Read() => service.RefreshAsync(settings).ContinueWith(task => task.Result.Single());
            var counts = new List<int>();
            foreach (var answer in new Func<string, HttpResponseMessage>[] { _ => new(HttpStatusCode.ServiceUnavailable), _ => throw new TaskCanceledException("timeout"), _ => new(HttpStatusCode.OK) { Content = new StringContent("not json") } })
            {
                handler.Answer = answer;
                clock = clock.AddMinutes(1);
                var before = handler.Usage.Count; await Read(); await Read();
                counts.Add(handler.Usage.Count - before);
            }
            check("A server error, a timeout and an unreadable answer are asked again on the next cycle", counts.SequenceEqual([2, 2, 2]));
        }

        // A clock set back while paused does not hold requests back for the whole of the time it went back.
        using (var service = new UsageService(desktop, locations, http, () => clock, quotaCache: cache, jitter: () => added))
        {
            Task<ProviderSnapshot> Read() => service.RefreshAsync(settings).ContinueWith(task => task.Result.Single());
            handler.Answer = _ => Limited("3600");
            clock = start.AddDays(1);
            await Read();
            var asked = handler.Usage.Count;
            clock = start.AddDays(-1);
            await Read();
            var held = handler.Usage.Count == asked;
            clock = start.AddDays(-1).AddHours(2);
            await Read();
            check("A clock set back cuts the pause to the longest one", held && handler.Usage.Count == asked + 1);
        }

        // Two refreshes at once ask once: the second is held behind the first and then paused.
        using (var service = new UsageService(desktop, locations, http, () => clock, quotaCache: cache, jitter: () => added))
        {
            clock = start.AddDays(3);
            handler.Answer = _ => Limited(null);
            var asked = handler.Usage.Count;
            await Task.WhenAll(service.RefreshAsync(settings), service.RefreshAsync(settings), service.RefreshAsync(settings));
            check("Refreshes that overlap send one request while the API is limiting", handler.Usage.Count == asked + 1);
        }

        // A kept reading that is damaged, from another account or past its age is not shown, and never stops the pause from working.
        File.WriteAllText(cache, "{ damaged");
        using (var service = new UsageService(desktop, locations, http, () => clock, quotaCache: cache, jitter: () => added))
        {
            clock = start.AddDays(4);
            handler.Answer = _ => Limited(null);
            var shown = (await service.RefreshAsync(settings)).Single();
            check("A damaged kept reading shows nothing and the 429 still reads as a pause", !shown.LiveQuota && shown.Quotas.Count == 0 && shown.Status.Contains("后自动重试"));
        }
        var aged = ClaudeQuotaCache.Serialize(new ClaudeQuotaSnapshot(start, "Max 5x", "", "", [new("5 小时额度", 12, start.AddHours(4), 300), new("每周额度", 40, start.AddDays(5), 10080)]),
            "account-a", ClaudeSignIn.Code, ClaudePlanOrigin.Account);
        check("A kept reading is used within its age and its weekly window", ClaudeQuotaCache.Parse(aged, start.AddHours(3), "account-a") is { Quotas.Count: 2 } &&
            ClaudeQuotaCache.Parse(aged, start.AddHours(5), "account-a") is { Quotas: [{ Label: "每周额度" }] });
        check("A kept reading is not used past its age or after every window has reset", ClaudeQuotaCache.Parse(aged, start + ClaudeQuotaCache.MaxAge + TimeSpan.FromMinutes(1), "account-a") is null &&
            ClaudeQuotaCache.Parse(aged, start.AddDays(6), "account-a") is null);
    }
}

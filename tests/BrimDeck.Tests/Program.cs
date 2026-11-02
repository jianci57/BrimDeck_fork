using System.Text.Json;
using BrimDeck.Core;

var passed = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
DeckSettings Only(params ProviderId[] ids) => new() { Apps = ids.Select(id => new AppEntry { Id = id }).ToList() };
var root = Path.Combine(Path.GetTempPath(), "BrimDeck-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await PiUsageTests.Run(root, Check);
    ModelUsageTests.Run(Check);
    AntigravityUsageTests.Run(root, Check);
    await MusicTests.Run(Check);
    await AppleMusicLyricsTests.Run(root, Check);
    await KugouTests.Run(Check);
    EqualizerSignalTests.Run(Check);
    EqualizerSpectrumTests.Run(Check);
    EqualizerModeTests.Run(root, Check);
    ColorMathTests.Run(Check);
    CompactSpacingTests.Run(Check);
    CompactDragTests.Run(Check);
    RingLetterTests.Run(root, Check);
    PanelThemeTests.Run(root, Check);
    PanelThemeEditorTests.Run(root, Check);
    PixelBlocksTests.Run(root, Check);
    ThemeCoverageTests.Run(Check, Environment.GetEnvironmentVariable("BRIMDECK_THEME_REPORT") == "1");
    DisplayLayoutTests.Run(Check);
    MediaAppTargetTests.Run(Check);
    CollapsedHoverTests.Run(Check);
    EdgeFrameTests.Run(Check);
    CollapsedMoveTests.Run(Check);
    HotkeyTests.Run(Check);
    KeyboardResizeTests.Run(Check);
    CollapsedGeometryTests.Run(root, Check);
    PanelHeaderTests.Run(Check);
    SystemMonitorTests.Run(Check);
    MonitorSpeechTests.Run(Check);
    CompactReadingTests.Run(Check);
    TemperatureTests.Run(Check);
    StorageTests.Run(Check);
    SensorStopTests.Run(Check);
    StripAdmissionTests.Run(Check);
    PowerTests.Run(Check);
    FrameRateTests.Run(Check);
    ResizeMotionTests.Run(Check);
    CurvesTests.Run(Check);
    ExpandBounceTests.Run(Check);
    ExpandStillTests.Run(Check);
    ExpandSnapTests.Run(Check);
    MotionReviewTests.Run(Check);
    IdleCostTests.Run(Check);
    MusicCostTests.Run(Check);
    MusicTitleMarqueeTests.Run(Check);
    DisplayWatchTests.Run(Check);
    DotMotionTests.Run(Check);
    DatePickerMotionTests.Run(Check);
    SystemMonitorTests.Store(root, Check);
    GpuAdapterTests.Run(root, Check);
    CpuSensorTests.Run(Check);
    GpuIdentityTests.Run(Check);
    NeteaseLogTests.Run(Check);
    await DashboardUsageTests.Run(root, Check);
    await AppUpdateTests.Run(root, Check);
    ReleaseHistoryTests.Run(root, Check);
    await InstallerUpdateTests.Run(root, Check);
    await PortableUpdateTests.Run(root, Check);
    SettingsMigrationTests.Run(root, Check);
    OffPeakTests.Run(root, Check);
    FullscreenRuleTests.Run(Check);
    LanguageTests.Run(root, Check);
    await ConfiguredProviderTests.Run(Check);
    await ZCodeUsageTests.Run(root, Check);
    await DshUsageTests.Run(root, Check);
    await KimiUsageTests.Run(root, Check);
    await ClaudeRateLimitTests.Run(root, Check);
    await OpenCodeUsageTests.Run(root, Check);
    await OfficialPriceTests.Run(root, Check);
    await GrokBotUsageTests.Run(root, Check);
    await CursorUsageTests.Run(root, Check);
    var priceTime = DateTimeOffset.UtcNow;
    const string catalogJson = """
        {"data":[
          {"id":"openai/gpt-6-astra","pricing":{"prompt":"0.00001","completion":"0.00005","input_cache_read":"0.000001","input_cache_write":"0.0000125","overrides":[{"min_prompt_tokens":272000,"prompt":"0.00002","completion":"0.000075","input_cache_read":"0.000002","input_cache_write":"0.000025"}]}},
          {"id":"anthropic/claude-fable-5.1","pricing":{"prompt":"0.00001","completion":"0.00005","input_cache_read":"0.00000025","input_cache_write":"0.0000125","input_cache_write_1h":"0.00002"}},
          {"id":"anthropic/claude-sonnet-4.5","pricing":{"prompt":"0.000003","completion":"0.000015"}},
          {"id":"vendor/free-model:free","pricing":{"prompt":"0","completion":"0"}},
          {"id":"vendor/dynamic-price","pricing":{"prompt":"-1","completion":"0.00001"}}
        ]}
        """;
    using var handler = new CatalogHandler(catalogJson);
    using var priceClient = new HttpClient(handler);
    var priceDirectory = Path.Combine(root, "prices");
    using var pricing = new Pricing(priceDirectory, priceClient, () => priceTime);
    await pricing.RefreshAsync();
    Check("Live catalog loaded without credentials", pricing.ModelCount == 4 && handler.Calls == 1 && !handler.SawAuthorization);
    await pricing.RefreshAsync();
    Check("Catalog refresh is throttled within 24 hours", handler.Calls == 1);
    Check("Provider-qualified model and Claude version aliases match", pricing.Find("claude-fable-5-1")?.Model == "anthropic/claude-fable-5.1" && pricing.Find("openai/gpt-6-astra") is not null);
    using (var cached = new Pricing(priceDirectory, priceClient, () => priceTime))
    {
        Check("Downloaded catalog survives restart", cached.ModelCount == 4 && cached.UpdatedAt == pricing.UpdatedAt);
        await cached.RefreshAsync();
        Check("Restart refreshes online even with fresh cache", handler.Calls == 2);
    }

    // The editable layer is independent of downloaded prices and must survive refresh and restart.
    var manualDirectory = Path.Combine(root, "manual-prices");
    using var manualHandler = new CatalogHandler(catalogJson);
    using var manualHttp = new HttpClient(manualHandler);
    using var editable = new Pricing(manualDirectory, manualHttp, () => priceTime);
    await editable.RefreshAsync();
    var ownPrice = new TokenPrices(.000002m, .000009m, .0000002m, .0000025m, .000004m);
    Check("A new manual model is persisted", editable.TrySetManual("local/new-model", ownPrice, false, out _));
    Check("Manual model is immediately available for cost calculation", editable.Estimate(new TokenEntry("manual", priceTime, "new-model", 1_000_000, 0, 0, 0, 1_000_000, null)) == 11m);
    Check("Duplicate model creation is case insensitive", !editable.TrySetManual("LOCAL/NEW-MODEL", ownPrice, false, out _));
    Check("Remote aliases cannot be added as duplicate manual models", !editable.TrySetManual("claude-sonnet-4-5", ownPrice, false, out _));
    Check("Manual editing uses the exact existing model", editable.TrySetManual("openai/gpt-6-astra", ownPrice, true, out _));
    manualHandler.Json = catalogJson.Replace("0.00001", "0.00008", StringComparison.Ordinal);
    await editable.RefreshAsync(true);
    Check("Remote conflicts do not overwrite manually edited values", editable.Find("gpt-6-astra")!.Prices == ownPrice && editable.IsManual("openai/gpt-6-astra"));
    Check("Manual additions remain after a remote refresh", editable.Find("local/new-model")!.Prices == ownPrice);
    Check("Manual prices are not overridden by a remote long-context tier", editable.Find("gpt-6-astra")!.Tiers.Count == 0);
    Check("Negative and malformed manual values are rejected", !editable.TrySetManual("local/invalid", ownPrice with { Input = -1 }, false, out _) && !editable.TrySetManual("bad model id", ownPrice, false, out _));
    Check("Unavailable cache prices remain unspecified", editable.TrySetManual("local/without-cache", new(0, 0, null, null, null), false, out _) && editable.Find("local/without-cache")!.Prices.CacheRead is null);
    // 1M tokens of each cache kind; input 2 USD/M, output 8 USD/M.
    var cacheUse = (string model) => new TokenEntry("manual", priceTime, model, 0, 1_000_000, 1_000_000, 1_000_000, 0, null);
    Check("Blank manual cache prices: reads cost nothing, both writes cost the input price",
        editable.TrySetManual("local/blank-cache", new(.000002m, .000008m, null, null, null), false, out _)
        && editable.Find("local/blank-cache")!.Prices.CacheRead is null && editable.Estimate(cacheUse("local/blank-cache")) == 4m);
    Check("A blank one-hour cache write price follows the cache write price",
        editable.TrySetManual("local/write-only", new(.000002m, .000008m, null, .000003m, null), false, out _) && editable.Estimate(cacheUse("local/write-only")) == 6m);
    Check("Manual cache prices entered as zero charge nothing",
        editable.TrySetManual("local/zero-cache", new(.000002m, .000008m, 0, 0, 0), false, out _) && editable.Estimate(cacheUse("local/zero-cache")) == 0m);
    Check("A bare manual ID can be added before online discovery", editable.TrySetManual("future-model", ownPrice, false, out _));
    manualHandler.Json = """{"data":[{"id":"vendor/future-model","pricing":{"prompt":"0.0009","completion":"0.0009"}},{"id":"vendor/another-model","pricing":{"prompt":"0.000001","completion":"0.000002"}}]}""";
    await editable.RefreshAsync(true);
    Check("Online discovery respects an existing bare manual ID", editable.Find("vendor/future-model")!.Prices == ownPrice && editable.Models.Count(m => m.Model.Contains("future-model", StringComparison.Ordinal)) == 1);
    Check("Incremental refresh retains other catalog rows and adds new models", editable.Find("claude-sonnet-4.5") is not null && editable.Find("vendor/another-model") is not null);
    using (var restarted = new Pricing(manualDirectory, manualHttp, () => priceTime))
    {
        Check("Manual values and models survive restart", restarted.Find("openai/gpt-6-astra")!.Prices == ownPrice && restarted.Find("local/new-model")!.Prices == ownPrice);
        await restarted.RefreshAsync();
        Check("First refresh after restart still protects manual changes", restarted.Find("gpt-6-astra")!.Prices == ownPrice);
    }
    var damagedDirectory = Path.Combine(root, "damaged-manual"); Directory.CreateDirectory(damagedDirectory);
    File.WriteAllText(Path.Combine(damagedDirectory, "manual-model-prices.json"), "broken");
    using (var damaged = new Pricing(damagedDirectory, manualHttp))
        Check("A corrupt manual file is preserved and cannot be overwritten by an empty replacement", damaged.ManualLoadWarning is not null && !damaged.TrySetManual("local/new", ownPrice, false, out _) && File.ReadAllText(Path.Combine(damagedDirectory, "manual-model-prices.json")) == "broken");
    var blockedDirectory = Path.Combine(root, "blocked-manual"); Directory.CreateDirectory(blockedDirectory);
    Directory.CreateDirectory(Path.Combine(blockedDirectory, "manual-model-prices.json.tmp"));
    using (var blocked = new Pricing(blockedDirectory, manualHttp))
        Check("A failed manual write does not change the active catalog", !blocked.TrySetManual("local/new", ownPrice, false, out _) && blocked.Find("local/new") is null);

    var defaults = new DeckSettings();
    Check("Borderless fullscreen classified independently", ScreenPolicy.Classify(false, true, false, true) == ScreenContext.Borderless);
    Check("Auto-hidden taskbar does not turn captioned maximization into fullscreen", ScreenPolicy.Classify(false, true, true, true) == ScreenContext.Maximized);
    Check("Exclusive mode wins over window shape", ScreenPolicy.Classify(true, false, true, true) == ScreenContext.Exclusive);
    defaults.Borderless = WindowBehavior.Normal;
    Check("Fullscreen override independent", defaults.Behavior(ScreenContext.Borderless) == WindowBehavior.Normal && defaults.Behavior(ScreenContext.Exclusive) == WindowBehavior.Hide);
    defaults.SetEnabled(ProviderId.Codex, false);
    Check("Provider toggles independent", defaults.Enabled(ProviderId.Claude) && !defaults.Enabled(ProviderId.Codex) && defaults.EnabledApps.Count == 1);
    defaults.Width = double.NaN; defaults.Height = 99999; defaults.OpenDelay = -10; defaults.Style = (CompactStyle)99; defaults.Normalize();
    Check("Invalid settings clamped", double.IsFinite(defaults.Width) && defaults.Height < 99999 && defaults.OpenDelay == 0 && Enum.IsDefined(defaults.Style));
    var narrow = new DeckSettings { Width = 500 }; foreach (var app in narrow.Apps) app.Enabled = true; narrow.Normalize();
    Check("Width below the per-column minimum is raised", narrow.Width == narrow.MinimumWidth);
    var four = new DeckSettings(); foreach (var app in four.Apps) app.Enabled = true;
    var two = Only(ProviderId.Claude, ProviderId.Codex);
    Check("Quick sizes never drop below the minimum width", Enum.GetValues<PanelSize>().All(size => four.PresetSize(size).Width >= four.MinimumWidth && two.PresetSize(size).Width >= two.MinimumWidth));
    var chosen = new DeckSettings { QuickSize = PanelSize.Spacious, Width = 1100, Height = 310 };
    chosen.SetEnabled(ProviderId.Cursor, false);
    Check("A chosen quick size keeps its width after the application count changes", chosen.Width == 1100 && chosen.QuickSize == PanelSize.Spacious);
    var moved = new DeckSettings(); var first = moved.Apps[0]; moved.Apps.RemoveAt(0); moved.Apps.Add(first);
    moved.Apps[0].ThemeColor = "ff8800"; moved.Apps[1].WarningColor = "#zzzzzz"; moved.Apps.Add(new AppEntry { QuotaSource = ProviderId.Codex, ThemeColor = "#000000" });
    moved.Normalize();
    Check("Order and duplicate sources are kept while colors are normalized", moved.Apps.Select(app => app.QuotaSource).SequenceEqual([ProviderId.Codex, ProviderId.Antigravity, ProviderId.Cursor, ProviderId.Claude, ProviderId.Codex]) &&
        moved.Apps[0].ThemeColor == "#FF8800" && moved.Apps[1].WarningColor == "" && moved.Apps[1].Warning == AppPresets.WarningColor);
    var copied = moved.Copy(); copied.Apps[0].ThemeColor = "#123456";
    Check("Copies do not share application entries", moved.Apps[0].ThemeColor == "#FF8800");
    var store = new SettingsStore(Path.Combine(root, "settings")); store.Save(moved);
    var loaded = store.Load();
    Check("Settings roundtrip keeps order and colors", loaded.Apps.Select(app => app.QuotaSource).SequenceEqual(moved.Apps.Select(app => app.QuotaSource)) && loaded.Apps[0].ThemeColor == "#FF8800");
    File.WriteAllText(store.FilePath, "broken json");
    Check("Corrupt settings recover visibly", store.Load().Style == CompactStyle.Notch && store.LoadWarning is not null);
    var unreadableCopies = Directory.GetFiles(store.DirectoryPath, "settings.unreadable-*.json");
    Check("Corrupt settings are copied aside before defaults replace them", unreadableCopies.Length == 1 && File.ReadAllText(unreadableCopies[0]) == "broken json" &&
        store.LoadWarning!.Contains(Path.GetFileName(unreadableCopies[0])));

    var now = DateTimeOffset.Now;
    var locations = DataLocations.Resolve(Path.Combine(root, "home"), Path.Combine(root, "roaming"), Path.Combine(root, "local"), null, "");
    Check("Provider paths resolve without saved configuration", locations.ClaudeHome == Path.Combine(root, "home", ".claude") &&
        locations.CodexHome == Path.Combine(root, "home", ".codex") && locations.CursorDatabase == Path.Combine(root, "roaming", "Cursor", "User", "globalStorage", "state.vscdb"));
    var relocated = DataLocations.Resolve(root, root, root, Path.Combine(root, "custom-claude"), Path.Combine(root, "custom-codex"));
    Check("Existing provider environment overrides are respected", relocated.ClaudeHome.EndsWith("custom-claude") && relocated.CodexHome.EndsWith("custom-codex"));
    var classicProfile = Path.Combine(locations.Roaming, "Claude");
    var storeProfile = Path.Combine(locations.Local, "Packages", "Claude_testfamily", "LocalCache", "Roaming", "Claude");
    Directory.CreateDirectory(classicProfile); Directory.CreateDirectory(storeProfile);
    Directory.CreateDirectory(Path.Combine(locations.Local, "Packages", "Unrelated_testfamily", "LocalCache", "Roaming", "Claude"));
    Check("Classic and Store Claude profiles discovered automatically", locations.ClaudeDesktopProfiles().Count == 2 && locations.ClaudeDesktopProfiles().Contains(storeProfile));
    var movedProfile = Path.Combine(locations.Local, "Claude-Data", "claude_testfamily");
    Directory.CreateDirectory(movedProfile);
    Directory.CreateDirectory(Path.Combine(locations.Local, "Claude-Data", "claude_unwritten"));
    File.WriteAllText(Path.Combine(locations.Local, "Claude-Data", "README.txt"), "");
    File.WriteAllText(Path.Combine(movedProfile, "config.json"), "{}");
    Check("Claude profile moved to Claude-Data is discovered and read first, without folders the app has not written",
        locations.ClaudeDesktopProfiles() is { Count: 3 } withMoved && withMoved[0] == movedProfile);
    File.WriteAllText(Path.Combine(storeProfile, ".user-data-left.claude_testfamily"), "");
    Check("A profile folder the desktop app moved away from is no longer used",
        locations.ClaudeDesktopProfiles() is { Count: 2 } afterMove && !afterMove.Contains(storeProfile) && afterMove[0] == movedProfile);
    File.WriteAllText(Path.Combine(storeProfile, "config.json"), "{}");
    File.SetLastWriteTimeUtc(Path.Combine(storeProfile, "config.json"), DateTime.UtcNow.AddDays(-1));
    Check("A marked folder that another install still keeps its config in remains a profile",
        locations.ClaudeDesktopProfiles() is { Count: 3 } shared && shared.Contains(storeProfile) && shared[0] == movedProfile);
    File.Delete(Path.Combine(storeProfile, "config.json"));
    var publisherStoreProfile = Path.Combine(locations.Local, "Packages", "AnthropicPBC.Claude_fnn82j28hfe8t", "LocalCache", "Roaming", "Claude");
    Directory.CreateDirectory(publisherStoreProfile);
    File.WriteAllText(Path.Combine(publisherStoreProfile, "config.json"), "{}");
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(publisherStoreProfile)!, ".user-data-leaving.anthropicpbc.claude_fnn82j28hfe8t"), "");
    Check("Publisher-prefixed Claude Store profile is discovered while its config remains during migration",
        locations.ClaudeDesktopProfiles() is { Count: 3 } withPublisher && withPublisher.Contains(publisherStoreProfile) && withPublisher.Contains(movedProfile));
    var oldSettings = SettingsMigrations.Read("""{"Width":890,"Claude":false,"CodexHome":"obsolete","ClaudeHome":"obsolete","CursorDatabase":"obsolete","OnlineQuota":false}""");
    oldSettings.Normalize();
    var migrated = JsonSerializer.Serialize(oldSettings);
    Check("Old switches migrate into the application list and are not written back", oldSettings.Width == 890 && !oldSettings.Enabled(ProviderId.Claude) && oldSettings.Enabled(ProviderId.Codex) &&
        migrated.Contains("\"Apps\"") && !migrated.Contains("\"Claude\"") && !migrated.Contains("Home") && !migrated.Contains("CursorDatabase") && !migrated.Contains("OnlineQuota"));
    object CachedToken(string token, long expires) => new { token, expiresAt = expires, subscriptionType = "max", rateLimitTier = "default_claude_max_20x" };
    string CacheKey(string account, string scope, string host = "https://api.anthropic.com") => $"acct:{account}|client:org:{host}:{scope}";
    var future = now.AddHours(1).ToUnixTimeMilliseconds();
    var cacheFixture = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
    {
        [CacheKey("current", "user:inference user:profile")] = CachedToken("test-inference", future),
        [CacheKey("current", "user:profile")] = CachedToken("test-profile", future),
        [CacheKey("previous", "user:profile")] = CachedToken("test-other-account", future),
        [CacheKey("current", "user:profile", "https://example.invalid")] = CachedToken("test-other-host", future),
        [CacheKey("current", "user:inference")] = CachedToken("test-no-profile", future),
        [CacheKey("current", "user:profile expired")] = CachedToken("test-expired", now.AddSeconds(-1).ToUnixTimeMilliseconds()),
        [CacheKey("current", "user:profile cleared")] = null
    });
    var desktopCredentials = ClaudeDesktopCache.Parse(cacheFixture, "current", now);
    Check("Desktop selects active account, provider host, valid expiry and profile scope", desktopCredentials.Count == 2 && desktopCredentials[0].Token == "test-profile");
    Check("Scoped desktop tokens require a known current account", ClaudeDesktopCache.Parse(cacheFixture, "", now).Count == 0);
    Check("Credential formatting does not expose secrets", !desktopCredentials[0].ToString()!.Contains("test-profile"));
    var desktopFixture = new ClaudeFixtureSources { State = new(true, desktopCredentials) };
    var usageClock = now;
    using (var quotaHandler = new ClaudeQuotaHandler())
    using (var quotaClient = new HttpClient(quotaHandler))
    using (var service = new UsageService(desktopFixture, locations, quotaClient, () => usageClock))
    {
        // Match the panel's shared 60-second refresh cycle without waiting on the wall clock.
        Task<List<ProviderSnapshot>> Refresh(DeckSettings settings, CancellationToken ct = default) { usageClock = usageClock.AddSeconds(60); return service.RefreshAsync(settings, ct); }
        var onlyClaude = Only(ProviderId.Claude);
        var result = (await Refresh(onlyClaude)).Single();
        Check("Desktop-only login connects without Claude Code credentials", result.LiveQuota && result.Quotas.Count == 2 && result.Plan == "Max 20x");
        Check("Desktop quota resets come from server", result.Quotas.All(quota => quota.ResetAt is not null) && result.Quotas.Single(quota => quota.Minutes == 300).UsedPercent == 9);
        Directory.CreateDirectory(locations.ClaudeHome);
        var credentialPath = Path.Combine(locations.ClaudeHome, ".credentials.json");
        File.WriteAllText(credentialPath, """{"claudeAiOauth":{"accessToken":"test-rejected","subscriptionType":"pro"}}""");
        result = (await Refresh(onlyClaude)).Single();
        Check("Rejected Code login falls back to Desktop and replaces its plan", quotaHandler.Rejected == 1 && result.LiveQuota && result.Plan == "Max 20x");
        File.WriteAllText(credentialPath, "broken");
        result = (await Refresh(onlyClaude)).Single();
        Check("Damaged Code login does not prevent Desktop connection", result.LiveQuota);

        File.WriteAllText(credentialPath, """{"claudeAiOauth":{"accessToken":"test-code","subscriptionType":"pro"}}""");
        desktopFixture.State = new(false, []);
        quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        Check("CLI-only installation connects and displays its own plan", result.LiveQuota && result.Plan == "pro" && quotaHandler.Requests.Count == 1);
        // A token that still carries the old plan must not hide a Max account: the account endpoint is asked first,
        // and without it the local account record outranks the token's copy.
        var profilePath = Path.Combine(locations.ClaudeHome, ".claude.json");
        File.WriteAllText(profilePath, """{"oauthAccount":{"organizationRateLimitTier":"default_claude_max_20x"}}""");
        result = (await Refresh(onlyClaude)).Single();
        Check("Local account record outranks a stale sign-in plan", result.LiveQuota && result.Plan == "Max 20x");
        quotaHandler.Profile = """{"organization":{"organization_type":"claude_max","rate_limit_tier":"default_claude_max_5x"}}""";
        result = (await Refresh(onlyClaude)).Single();
        Check("Account endpoint decides the Claude plan", result.Plan == "Max 5x");
        quotaHandler.Profile = null; File.Delete(profilePath);
        Check("Claude organization type names the plan without a tier", PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_pro"}}""")) == "Pro");
        var readCalls = desktopFixture.ReadCalls;
        desktopFixture.ReadFailure = new IOException("Test desktop cache unavailable");
        result = (await Refresh(onlyClaude)).Single();
        Check("A successful CLI result does not depend on Desktop discovery", result.LiveQuota && desktopFixture.ReadCalls == readCalls);
        desktopFixture.ReadFailure = null;
        desktopFixture.State = new(true, desktopCredentials);

        foreach (var fault in new[] { "timeout", "malformed", "limited" })
        {
            quotaHandler.Requests.Clear();
            quotaHandler.Override = (token, _) => token != "test-code" ? null : fault switch
            {
                "timeout" => throw new TaskCanceledException("Test timeout"),
                "malformed" => new(System.Net.HttpStatusCode.OK) { Content = new StringContent("invalid json") },
                _ => new(System.Net.HttpStatusCode.TooManyRequests)
            };
            result = (await Refresh(onlyClaude)).Single();
            Check($"CLI {fault} failure falls back to one successful Desktop result", result.LiveQuota && result.Quotas.Count == 2 &&
                result.Plan == "Max 20x" && quotaHandler.Requests.SequenceEqual(["test-code", "test-profile"]));
        }

        quotaHandler.Override = null; quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        var requestsSoFar = quotaHandler.Requests.Count;
        result = (await Refresh(onlyClaude)).Single();
        Check("Claude requests fresh account quotas on the next 60-second cycle", result.LiveQuota && result.Quotas.Count == 2 && result.QuotaTime == usageClock && quotaHandler.Requests.Count == requestsSoFar + 1);
        result = (await service.RefreshAsync(onlyClaude)).Single();
        Check("Manual refresh immediately requests account quotas", result.LiveQuota && quotaHandler.Requests.Count == requestsSoFar + 2);
        var previousTime = result.QuotaTime;
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.ServiceUnavailable);
        quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        Check("Transient failure keeps the last account quotas with their time", result.LiveQuota && result.Quotas.Count == 2 && result.QuotaTime == previousTime && quotaHandler.Requests.Count == 2);
        quotaHandler.Profile = """{"organization":{"organization_type":"claude_max","rate_limit_tier":"default_claude_max_20x"}}""";
        result = (await Refresh(onlyClaude)).Single();
        Check("Plan follows the account endpoint while the quota request fails", result.Quotas.Count == 2 && result.Plan == "Max 20x");
        quotaHandler.Profile = null; quotaHandler.Requests.Clear();
        var polled = true;
        for (int cycle = 1; cycle <= 4; cycle++)
        {
            quotaHandler.Requests.Clear();
            result = (await Refresh(onlyClaude)).Single();
            polled &= result.Quotas.Count == 2 && quotaHandler.Requests.Count == 2;
        }
        Check("Repeated failures still request quotas every 60-second cycle", polled);
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.TooManyRequests);
        quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        Check("Rate limiting does not cycle every Desktop scope", result.Quotas.Count == 2 && quotaHandler.Requests.Count == 2);
        // The 429s above started a pause; a rejected login is only learned from a request that goes out.
        usageClock = usageClock.AddHours(2);
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.Unauthorized);
        quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        Check("Rejected logins do not keep showing earlier account quotas", !result.LiveQuota && result.Quotas.Count == 0);
        using (var fresh = new UsageService(desktopFixture, locations, quotaClient, () => usageClock))
        {
            quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.ServiceUnavailable);
            quotaHandler.Requests.Clear();
            var unavailable = (await fresh.RefreshAsync(onlyClaude)).Single();
            Check("Both sources failing leaves quota unavailable instead of inventing a value", !unavailable.LiveQuota && unavailable.Quotas.Count == 0 && quotaHandler.Requests.Count == 2);
        }

        desktopFixture.State = new(true, [new("test-code", "pro", ClaudeSignIn.Desktop), desktopCredentials[0]]);
        quotaHandler.Override = (token, _) => token == "test-code" ? new(System.Net.HttpStatusCode.Unauthorized) : null;
        quotaHandler.Requests.Clear();
        result = (await Refresh(onlyClaude)).Single();
        Check("The same credential shared by CLI and Desktop is queried once", result.LiveQuota && quotaHandler.Requests.SequenceEqual(["test-code", "test-profile"]));
        desktopFixture.State = new(true, desktopCredentials);

        using (var cancelled = new CancellationTokenSource())
        {
            readCalls = desktopFixture.ReadCalls;
            quotaHandler.Override = (_, ct) => { cancelled.Cancel(); ct.ThrowIfCancellationRequested(); return null; };
            bool propagated = false;
            try { await Refresh(onlyClaude, cancelled.Token); } catch (OperationCanceledException) { propagated = true; }
            Check("User cancellation stops reading instead of starting the other source", propagated && desktopFixture.ReadCalls == readCalls);
        }
        quotaHandler.Override = null;
        File.Delete(credentialPath);
        desktopFixture.State = new(true, [], true);
        result = (await Refresh(onlyClaude)).Single();
        Check("Unreadable Desktop login is not reported as missing installation", !result.LiveQuota && result.StatusLabel == "读取失败" && result.Status.Contains("桌面版"));
        desktopFixture.State = new(true, []);
        result = (await Refresh(onlyClaude)).Single();
        Check("Installed Desktop without valid cache prompts original app login", result.StatusLabel == "等待登录" && !result.LiveQuota);

        desktopFixture.State = new(true, desktopCredentials);
        var historyPath = Path.Combine(classicProfile, "plan-usage-history.json");
        string History(DateTimeOffset time, int fiveHour, int sevenDay) => JsonSerializer.Serialize(new { version = 2, samples = new[] {
            new { t = time.AddMinutes(-30).ToUnixTimeMilliseconds(), org = "org", u = new { fh = 60, sd = 50 } },
            new { t = time.ToUnixTimeMilliseconds(), org = "org", u = new { fh = fiveHour, sd = sevenDay } } } });
        Check("Desktop usage history yields its newest sample", ClaudeUsageHistory.Parse(Json(History(now, 37, 7))) is { FiveHour: 37, SevenDay: 7 } parsedSample && parsedSample.Time == DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()));
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.TooManyRequests);
        File.WriteAllText(historyPath, History(usageClock.AddMinutes(2), 37, 7));
        result = (await Refresh(onlyClaude)).Single();
        Check("Rate limiting without any earlier reading falls back to the desktop application's local history", result.LiveQuota &&
            result.Quotas.Single(q => q.Minutes == 300).UsedPercent == 37 && result.Quotas.Single(q => q.Minutes == 10080).UsedPercent == 7 && result.Plan == "Max 20x");
        quotaHandler.Override = null;
        usageClock = usageClock.AddHours(2);
        result = (await Refresh(onlyClaude)).Single();
        Check("A successful account reading replaces the local history sample", result.LiveQuota && result.Quotas.Single(q => q.Minutes == 300).UsedPercent == 9);
        var readingTime = result.QuotaTime;
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.TooManyRequests);
        File.WriteAllText(historyPath, History(usageClock.AddMinutes(2), 41, 8));
        result = (await Refresh(onlyClaude)).Single();
        Check("A newer local sample wins over the stored reading and keeps the reset time still ahead", result.Quotas.Single(q => q.Minutes == 300).UsedPercent == 41 &&
            result.Quotas.Single(q => q.Minutes == 10080).ResetAt is not null && result.QuotaTime > readingTime);

        // The last online reading survives a restart, so a start while rate limited still shows the per-model quotas.
        var quotaFile = Path.Combine(root, "claude-quota-cache", ClaudeQuotaCache.FileName);
        string Fable(DateTimeOffset reset) => JsonSerializer.Serialize(new
        {
            five_hour = new { utilization = 12, resets_at = reset.AddDays(-3).AddHours(4) },
            seven_day = new { utilization = 30, resets_at = reset },
            limits = new[] { new { kind = "weekly_scoped", group = "weekly", percent = 44, resets_at = reset, scope = new { model = new { display_name = "Fable" } } } }
        });
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(Fable(usageClock.AddDays(3))) };
        using (var beforeRestart = new UsageService(desktopFixture, locations, quotaClient, () => usageClock, quotaCache: quotaFile))
        {
            var online = (await beforeRestart.RefreshAsync(onlyClaude)).Single();
            Check("An online Claude reading with a per-model quota is kept on disk", online.Quotas.Any(q => q.Label == "Fable 每周") && File.Exists(quotaFile));
        }
        var cacheText = File.ReadAllText(quotaFile);
        Check("The kept Claude reading holds no sign-in token", !cacheText.Contains("test-code") && !cacheText.Contains("test-desktop") && !cacheText.Contains("accessToken", StringComparison.OrdinalIgnoreCase));
        Check("The kept Claude reading names its account by a hash, never the id", !cacheText.Contains("current") && cacheText.Contains(ClaudeQuotaCache.AccountHash("current")));
        Check("The kept Claude reading keeps its sources without the text of the language of the day", !cacheText.Contains("账户配额") && !cacheText.Contains("桌面版"));
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.TooManyRequests);
        File.WriteAllText(historyPath, History(usageClock.AddMinutes(10), 41, 8));
        // A switch of account while BrimDeck was closed: the other account's reading is not shown as this one's.
        desktopFixture.State = new(true, ClaudeDesktopCache.Parse(cacheFixture, "previous", now));
        using (var switched = new UsageService(desktopFixture, locations, quotaClient, () => usageClock.AddMinutes(5), quotaCache: quotaFile))
        {
            var limited = (await switched.RefreshAsync(onlyClaude)).Single();
            Check("After a switch of account, rate limiting does not show the previous account's kept quotas", !limited.Quotas.Any(q => q.Label == "Fable 每周") && File.Exists(quotaFile));
        }
        desktopFixture.State = new(true, desktopCredentials);
        var restartedAt = usageClock.AddMinutes(5);
        using (var restarted = new UsageService(desktopFixture, locations, quotaClient, () => restartedAt, quotaCache: quotaFile))
        {
            var limited = (await restarted.RefreshAsync(onlyClaude)).Single();
            Check("After a restart, rate limiting still shows the per-model quota read before it", limited.LiveQuota && limited.Quotas.Any(q => q.Label == "Fable 每周" && q.UsedPercent == 44));
            Check("The desktop app's newer local sample still decides the 5-hour and weekly shares", limited.Quotas.Single(q => q.Minutes == 300).UsedPercent == 41);
            Check("The status says when the carried-over per-model quotas were read", limited.Status.Contains("按模型的额度为") && limited.Status.Contains(usageClock.LocalDateTime.ToString("HH:mm")));
            // A sign-in that cannot be read for a moment drops only this run's reading; the kept one comes back with the sign-in.
            desktopFixture.State = new(true, [], true);
            var unreadable = (await restarted.RefreshAsync(onlyClaude)).Single();
            desktopFixture.State = new(true, desktopCredentials);
            var readable = (await restarted.RefreshAsync(onlyClaude)).Single();
            Check("After the sign-in was unreadable for a moment, rate limiting shows the kept per-model quotas again",
                !unreadable.LiveQuota && File.Exists(quotaFile) && readable.Quotas.Any(q => q.Label == "Fable 每周" && q.UsedPercent == 44));
            restartedAt = restartedAt.AddHours(2);
            quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.Unauthorized);
            await restarted.RefreshAsync(onlyClaude);
            Check("A rejected sign-in removes the kept Claude reading", !File.Exists(quotaFile));
        }
        var keptReading = new ClaudeQuotaSnapshot(usageClock, "Max 20x", "Claude 账户", "Claude Code", [new("Fable 每周", 44, usageClock.AddDays(2), 10080), new("5 小时额度", 12, usageClock.AddHours(2), 300)]);
        var keptText = ClaudeQuotaCache.Serialize(keptReading, "current", ClaudeSignIn.Code, ClaudePlanOrigin.Account);
        Check("A kept Claude reading reads back whole", ClaudeQuotaCache.Parse(keptText, usageClock.AddMinutes(1), "current") is { Plan: "Max 20x", Quotas.Count: 2 });
        Check("A quota whose window has reset since is left out", ClaudeQuotaCache.Parse(keptText, usageClock.AddHours(3), "current") is { Quotas: [{ Label: "Fable 每周" }] });
        Check("A kept reading older than a week is not used", ClaudeQuotaCache.Parse(keptText, usageClock.AddDays(8), "current") is null);
        Check("A kept reading from the future is not used", ClaudeQuotaCache.Parse(keptText, usageClock.AddHours(-1), "current") is null);
        Check("A damaged kept reading is ignored", ClaudeQuotaCache.Parse("{ not json", usageClock, "current") is null && ClaudeQuotaCache.Parse("null", usageClock, "current") is null);
        Check("A kept reading of another account is not used", ClaudeQuotaCache.Parse(keptText, usageClock.AddMinutes(1), "previous") is null);
        Check("A kept reading is not used while the current account cannot be told", ClaudeQuotaCache.Parse(keptText, usageClock.AddMinutes(1), "") is null);
        Check("A kept reading that names no account, as earlier versions wrote it, is not used",
            ClaudeQuotaCache.Parse(JsonSerializer.Serialize(keptReading), usageClock.AddMinutes(1), "current") is null);
        // A reading kept while BrimDeck showed Chinese names its sources in English after a change of language and a restart.
        ClaudeQuotaSnapshot? readInEnglish;
        Loc.Use(Loc.English);
        try { readInEnglish = ClaudeQuotaCache.Parse(keptText, usageClock.AddMinutes(1), "current"); }
        finally { Loc.Use(Loc.Chinese); }
        Check("A kept reading names its sources in the language shown now", readInEnglish is { Source: "Claude Code · account quota", PlanSource: "Claude account" } &&
            ClaudeQuotaCache.Parse(keptText, usageClock.AddMinutes(1), "current") is { Source: "Claude Code · 账户配额", PlanSource: "Claude 账户" });
        // A Claude Code sign-in names its account in the local account record that Claude Code writes when signing in.
        File.WriteAllText(credentialPath, """{"claudeAiOauth":{"accessToken":"test-code","subscriptionType":"pro"}}""");
        File.WriteAllText(profilePath, """{"oauthAccount":{"accountUuid":"code-account"}}""");
        quotaHandler.Override = (_, _) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(Fable(usageClock.AddDays(3))) };
        using (var codeSignIn = new UsageService(desktopFixture, locations, quotaClient, () => usageClock, quotaCache: quotaFile))
        {
            await codeSignIn.RefreshAsync(onlyClaude);
            Check("A Claude Code reading is kept for the account its local record names", File.ReadAllText(quotaFile).Contains(ClaudeQuotaCache.AccountHash("code-account")));
        }
        File.Delete(credentialPath); File.Delete(profilePath); File.Delete(quotaFile);
        // The kept reading's time follows the latest online reading even when the values stay the same, without a write every refresh.
        var cacheClock = usageClock;
        using (var steady = new UsageService(desktopFixture, locations, quotaClient, () => cacheClock, quotaCache: quotaFile))
        {
            DateTimeOffset? KeptTime() => ClaudeQuotaCache.Load(quotaFile, cacheClock, "current", "org")?.Time;
            await steady.RefreshAsync(onlyClaude);
            var firstKept = KeptTime();
            cacheClock = usageClock.AddMinutes(5);
            await steady.RefreshAsync(onlyClaude);
            Check("A refresh that reads the same values soon after does not write the kept reading again", firstKept == usageClock && KeptTime() == usageClock);
            cacheClock = usageClock.AddMinutes(12);
            await steady.RefreshAsync(onlyClaude);
            Check("Once the kept reading is ten minutes old, a refresh with the same values updates its time", KeptTime() == usageClock.AddMinutes(12));
        }
        File.Delete(quotaFile);
        quotaHandler.Override = null;

        // An account with a personal subscription and a Team seat: the desktop app keeps a sign-in for each organization, and one
        // Claude row shows both, the personal one first.
        object Seat(string token, string subscription, string tier) => new { token, expiresAt = future, subscriptionType = subscription, rateLimitTier = tier };
        var seats = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["acct:current|client:org-team:https://api.anthropic.com:user:profile"] = Seat("test-team", "team", "default_claude_max_5x"),
            ["acct:current|client:org-pro:https://api.anthropic.com:user:profile"] = Seat("test-pro", "pro", "default_claude_ai")
        });
        var seatCredentials = ClaudeDesktopCache.Parse(seats, "current", now);
        Check("Desktop sign-ins name their organization, and a Team seat is not taken for Max",
            seatCredentials.Count == 2 && seatCredentials.Any(c => c is { Organization: "org-team", Plan: "Team Premium" }) && seatCredentials.Any(c => c is { Organization: "org-pro", Plan: "pro" }));
        Check("The account endpoint names Team Standard and Team Premium seats",
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_team","rate_limit_tier":"default_claude_max_5x","seat_tier":"team_tier_1"}}""")) == "Team Premium" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_team","rate_limit_tier":"default_raven","seat_tier":"team_standard"}}""")) == "Team Standard" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_team","rate_limit_tier":"default_claude_max_5x"}}""")) == "Team Premium" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_team","rate_limit_tier":"default_raven"}}""")) == "Team");
        Check("The local record names the seat by its own tier, not the Team's default",
            PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationType":"claude_team","organizationRateLimitTier":"default_raven","userRateLimitTier":"default_claude_max_5x","seatTier":"team_tier_1"}}""")) == "Team Premium" &&
            PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationType":"claude_team","organizationRateLimitTier":"default_raven"}}""")) == "Team" &&
            PlanParser.ClaudeTier("default_raven", "team") == "Team");
        Check("The local record names Pro and Max by the organization's type and tier",
            PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationType":"claude_pro","organizationRateLimitTier":"default_claude_ai"}}""")) == "Pro" &&
            PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationType":"claude_max","organizationRateLimitTier":"default_claude_max_20x"}}""")) == "Max 20x" &&
            PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationType":"claude_max"}}""")) == "Max");
        string Usage(int fiveHour, int weekly, int? fable = null) => JsonSerializer.Serialize(new
        {
            five_hour = new { utilization = fiveHour, resets_at = usageClock.AddHours(3) },
            seven_day = new { utilization = weekly, resets_at = usageClock.AddDays(3) },
            limits = fable is { } share ? new object[] { new { kind = "weekly_scoped", group = "weekly", percent = share, resets_at = usageClock.AddDays(3), scope = new { model = new { display_name = "Fable" } } } } : []
        });
        desktopFixture.State = new(true, seatCredentials);
        // Claude Code itself uses the Team, as Claude Code in the desktop app does when the app has the Team selected.
        File.WriteAllText(profilePath, """{"oauthAccount":{"accountUuid":"current","organizationUuid":"org-team","organizationType":"claude_team","seatTier":"team_tier_1"}}""");
        quotaHandler.Override = (token, _) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(token == "test-team" ? Usage(4, 1, 0) : Usage(20, 30)) };
        using (var seatsService = new UsageService(desktopFixture, locations, quotaClient, () => usageClock, quotaCache: quotaFile))
        {
            var statistics = (await seatsService.RefreshAsync(new DeckSettings { Apps = [new AppEntry { Id = ProviderId.Claude, QuotaOnline = false }] }))
                .Single(snapshot => !snapshot.Configurations.Contains(ProviderCatalog.LocalQuota));
            Check("Without an online Claude row, the statistics snapshot names the local account record's plan for a row switched on to show before its first reading",
                statistics.Plan == "Team Premium" && statistics.PlanSource == "Claude 本地账户记录");
            var both = (await seatsService.RefreshAsync(onlyClaude)).Single();
            Check("Each organization of the account is read, the personal one first and each under its name", both.LiveQuota && both.Plan == "Pro + Team Premium" &&
                both.Quotas.Select(q => q.Label).SequenceEqual(["Pro · 5 小时额度", "Pro · 每周额度", "Team · 5 小时额度", "Team · 每周额度", "Team · Fable 每周"]) &&
                both.Quotas.Single(q => q.Label == "Team · 5 小时额度").UsedPercent == 4 && both.Quotas.Single(q => q.Label == "Pro · 5 小时额度").UsedPercent == 20);
            Check("Each organization's reading is kept apart on disk", ClaudeQuotaCache.Load(quotaFile, usageClock, "current", "org-team") is { Quotas.Count: 3 } &&
                ClaudeQuotaCache.Load(quotaFile, usageClock, "current", "org-pro") is { Quotas.Count: 2 });
            quotaHandler.Override = (token, _) => token == "test-team" ? new(System.Net.HttpStatusCode.ServiceUnavailable)
                : new(System.Net.HttpStatusCode.OK) { Content = new StringContent(Usage(21, 30)) };
            var held = (await seatsService.RefreshAsync(onlyClaude)).Single();
            Check("An organization that fails for a moment keeps showing its last reading beside the other's new one",
                held.Quotas.Count == 5 && held.Quotas.Single(q => q.Label == "Pro · 5 小时额度").UsedPercent == 21 && held.Quotas.Single(q => q.Label == "Team · 5 小时额度").UsedPercent == 4);
        }
        using (var withoutKept = new UsageService(desktopFixture, locations, quotaClient, () => usageClock))
        {
            var partial = (await withoutKept.RefreshAsync(onlyClaude)).Single();
            Check("An organization that cannot be read is named in the status while the other's quotas show",
                partial.LiveQuota && partial.Quotas.Count == 2 && partial.Quotas.All(q => q.Label.StartsWith("Pro · ", StringComparison.Ordinal)) && partial.Status.Contains("Team 暂时无法读取"));
        }
        File.Delete(profilePath); File.Delete(quotaFile);
        desktopFixture.State = new(true, desktopCredentials);
        quotaHandler.Override = null;

        var savedRow =System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Only(ProviderId.Claude)))!.AsObject();
        foreach (var app in savedRow["Apps"]!.AsArray()) app!.AsObject().Remove("QuotaOnline");
        Check("Claude rows read online by default, including in files saved before the setting existed",
            new AppEntry().QuotaOnline && SettingsMigrations.Read(savedRow.ToJsonString()).Apps.Single().QuotaOnline);
        DeckSettings ClaudeRows(params bool[] online) => new() { Apps = online.Select(o => new AppEntry { Id = ProviderId.Claude, QuotaOnline = o }).ToList() };
        quotaHandler.Override = null;
        usageClock = usageClock.AddHours(2);
        quotaHandler.Requests.Clear();
        await Refresh(ClaudeRows(true));
        int oneRowRequests = quotaHandler.Requests.Count;
        quotaHandler.Requests.Clear();
        var twoOnline = await Refresh(ClaudeRows(true, true));
        Check("Two online Claude rows share one online reading", quotaHandler.Requests.Count == oneRowRequests && twoOnline.Count(s => s.Id == ProviderId.Claude) == 1);

        File.WriteAllText(historyPath, History(usageClock.AddMinutes(2), 44, 9));
        var bothLocal = ClaudeRows(false, false);
        quotaHandler.Requests.Clear(); readCalls = desktopFixture.ReadCalls;
        var localResults = await Refresh(bothLocal);
        var localColumns = DashboardUsage.Columns(bothLocal, localResults);
        Check("Offline Claude rows send no request and read no sign-in", quotaHandler.Requests.Count == 0 && desktopFixture.ReadCalls == readCalls);
        Check("Two offline Claude rows share one local reading", localResults.Count(s => s.Configurations.Contains(ProviderCatalog.LocalQuota)) == 1 &&
            localColumns.All(c => c.Quota.LiveQuota && c.Quota.IsStale && c.Quota.StatusLabel == "本地记录" && c.Quota.Quotas.Single(q => q.Minutes == 300).UsedPercent == 44));
        Check("An offline Claude reading is timed when BrimDeck read it and names the time of the desktop record", localColumns.All(c => c.Quota.QuotaTime == usageClock &&
            c.Quota.Status.StartsWith("来自 Claude 桌面版，最新记录于 ", StringComparison.Ordinal)));

        var mixed = ClaudeRows(true, false);
        quotaHandler.Requests.Clear();
        var mixedColumns = DashboardUsage.Columns(mixed, await Refresh(mixed));
        Check("An online and an offline Claude row are configured separately", quotaHandler.Requests.Count == oneRowRequests &&
            !mixedColumns[0].Quota.IsStale && mixedColumns[0].Quota.Quotas.Single(q => q.Minutes == 300).UsedPercent == 9 &&
            mixedColumns[1].Quota.IsStale && mixedColumns[1].Quota.Quotas.Single(q => q.Minutes == 300).UsedPercent == 44);
        Check("Token statistics never use the local quota snapshot", DashboardUsage.Statistics(mixed, await Refresh(mixed)).All(s => !s.Configurations.Contains(ProviderCatalog.LocalQuota)) &&
            mixedColumns.All(c => !c.Usage.Configurations.Contains(ProviderCatalog.LocalQuota)));
        File.Delete(historyPath);
        quotaHandler.Requests.Clear();
        var emptyColumn = DashboardUsage.Columns(bothLocal, await Refresh(bothLocal))[0].Quota;
        Check("Offline Claude without a local record says so instead of inventing a value", !emptyColumn.LiveQuota && emptyColumn.Quotas.Count == 0 &&
            emptyColumn.StatusLabel == "无本地记录" && quotaHandler.Requests.Count == 0);

    }
    var failingDesktop = new FailingDesktopSources();
    using (var service = new UsageService(failingDesktop))
    {
        var onlyAntigravity = Only(ProviderId.Antigravity);
        var failed = (await service.RefreshAsync(onlyAntigravity)).Single();
        Check("Process discovery failure is not mislabeled as signed out", failed.StatusLabel == "检测失败" && !failed.Status.Contains("登录", StringComparison.Ordinal));
        failingDesktop.Fail = false;
        var recovered = (await service.RefreshAsync(onlyAntigravity)).Single();
        Check("Discovery errors do not prevent subsequent refresh", recovered.StatusLabel != failed.StatusLabel);
    }
    var quotas = QuotaParser.Codex(Json("""
        {"rate_limit":{"primary_window":{"used_percent":16,"limit_window_seconds":604800,"reset_after_seconds":3600}}}
        """), now, true);
    Check("Codex primary can be weekly", quotas.Single().Minutes == 10080);
    Check("Codex reset relative", Math.Abs((quotas[0].ResetAt!.Value - now).TotalSeconds - 3600) < .1);
    var plusQuotas = QuotaParser.Codex(Json("""
        {"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":42,"limit_window_seconds":18000,"reset_after_seconds":1800},"secondary_window":{"used_percent":16,"limit_window_seconds":604800,"reset_after_seconds":3600}}}
        """), now, true);
    Check("Codex Plus retains both five-hour and weekly windows", plusQuotas.Count == 2 &&
        plusQuotas.Single(x => x.Minutes == 300).UsedPercent == 42 && plusQuotas.Single(x => x.Minutes == 10080).UsedPercent == 16);
    var localQuotas = QuotaParser.Codex(Json("""
        {"primary":{"used_percent":16,"window_minutes":10080},"secondary":{"used_percent":42,"window_minutes":300}}
        """), now, false);
    Check("Codex local windows use duration rather than primary/secondary position", localQuotas.Count == 2 &&
        localQuotas.Single(x => x.Minutes == 300).UsedPercent == 42 && localQuotas.Single(x => x.Minutes == 10080).UsedPercent == 16);
    var weeklyOnly = QuotaParser.Codex(Json("""
        {"plan_type":"pro","rate_limit":{"primary_window":null,"secondary_window":{"used_percent":26,"limit_window_seconds":604800}}}
        """), now, true);
    Check("Codex weekly-only account does not invent a five-hour window", weeklyOnly.Count == 1 && weeklyOnly[0].Minutes == 10080);
    Check("Missing quota stays unknown", QuotaParser.Codex(Json("{}"), now, true).Count == 0);
    Check("Zero quota is known", QuotaParser.Codex(Json("""{"primary":{"used_percent":0,"window_minutes":300}}"""), now, false).Single().Remaining == 100);
    Check("Claude fractional reset", QuotaParser.Claude(Json("""{"five_hour":{"utilization":25,"resets_at":"2026-09-06T12:33:44.123456Z"}}""")).Single().ResetAt is not null);
    var cursor = QuotaParser.Cursor(Json("""{"individualUsage":{"plan":{"autoPercentUsed":"12.5","apiPercentUsed":20}},"billingCycleEnd":"1789000000000"}"""));
    Check("Cursor fractional percent preserved", cursor.Count == 2 && cursor[0].UsedPercent == 12.5 && cursor[0].ResetAt is not null);
    var grokBot = QuotaParser.SandUsage(Json("""{"usagePercent":0.856,"nextResetTimestampUtc":"2026-10-18T07:21:38.721Z"}"""));
    Check("Grok Bot usage becomes a quota row with its reset", grokBot is { Label: "Grok Bot", UsedPercent: 0.856 } && grokBot.ResetAt is not null);
    Check("Grok Bot usage without a percent is skipped", QuotaParser.SandUsage(Json("""{"nextResetTimestampUtc":"2026-10-18T07:21:38.721Z"}""")) is null);
    Check("Antigravity missing fraction is unknown", QuotaParser.Antigravity(Json("""{"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":[{"label":"A","quotaInfo":{}}]}}}""")).Count == 0);
    Check("Antigravity zero fraction means depleted", QuotaParser.Antigravity(Json("""{"groups":[{"buckets":[{"bucketId":"gemini-5h","remainingFraction":0}]}]}""")).Single().Remaining == 0);
    Check("Cursor free membership is displayed as a real plan", PlanParser.Cursor(Json("""{"membershipType":"free"}""")) == "free");
    Check("Cursor quota alone does not imply a paid plan", PlanParser.Cursor(Json("""{"planUsage":{"totalPercentUsed":0}}""")) == "");
    Check("Antigravity uses the named user tier before its service plan", PlanParser.Antigravity(Json("""{"userStatus":{"userTier":{"name":"Example Tier"},"planStatus":{"planInfo":{"planName":"Pro"}}}}""")) == "Example Tier");
    Check("Missing Antigravity subscription is not inferred from quota", PlanParser.Antigravity(Json("""{"groups":[{"buckets":[{"bucketId":"gemini-weekly","remainingFraction":1}]}]}""")) == "");
    Check("Unknown Claude profile does not invent a plan", PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationRateLimitTier":"unknown"}}""")) == "");
    var eventItem = UsageService.ParseCursorEvent(Json("""{"timestamp":"1789000000000","model":"composer-2.5","tokenUsage":{"inputTokens":100,"cacheReadTokens":900,"cacheWriteTokens":20,"outputTokens":30,"totalCents":3.45131}}"""));
    Check("Cursor token categories are disjoint", eventItem!.Total == 1050);
    Check("Cursor API cost cents converted", pricing.Estimate(eventItem!) == .0345131m);
    Check("Reported cost is not marked as an estimate", pricing.Summarize([eventItem!]) is { Reported: 1, Estimated: 0, IsEstimate: false });
    TokenEntry Charged(int i, long input, long output, long write, long read, decimal perInput = 2, decimal perOutput = 6) =>
        new($"cursor:{i}", now, "grok-bot-default", input, read, write, 0, output, Math.Round((input * perInput + output * perOutput + write * 2 + read * .5m) / 1_000_000m, 7));
    var charged = new[] { Charged(1, 4, 215, 5954, 0), Charged(2, 120, 3000, 800, 90_000), Charged(3, 7, 40, 0, 12_000), Charged(4, 2000, 900, 15_000, 400_000), Charged(5, 1, 1, 1, 1), Charged(6, 50, 7000, 300, 1_000_000), Charged(7, 900, 20, 10, 5) };
    Check("Prices behind API amounts are recovered", Pricing.FromReported(charged) is { Status: RecoveryStatus.Recovered, Records: 7 } recovery &&
        recovery.Prices == new TokenPrices(.000002m, .000006m, .0000005m, .000002m, null));
    Check("Too few records are reported with their count", Pricing.FromReported(charged[..5]) is { Status: RecoveryStatus.TooFewRecords, Prices: null, Records: 5, Needed: 7 });
    Check("A category without tokens has no recovered price", Pricing.FromReported(charged.Select(t => t with { CacheWrite = 0, ReportedCostUsd = t.ReportedCostUsd - t.CacheWrite * 2 / 1_000_000m })).Prices is { CacheWrite: null, CacheRead: .0000005m });
    Check("Amounts that fixed prices cannot reproduce are reported as inconsistent",
        Pricing.FromReported(charged.Select((t, i) => i == 3 ? Charged(4, 2000, 900, 15_000, 400_000, 4, 12) : t)) is { Status: RecoveryStatus.Inconsistent, Prices: null });
    Check("Repeated records cannot determine each price", Pricing.FromReported(Enumerable.Range(1, 8).Select(i => Charged(i, 10, 20, 30, 40))) is { Status: RecoveryStatus.Undetermined, Prices: null });
    Check("Estimated records give no recovered prices", Pricing.FromReported(charged.Select(t => t with { ReportedCostUsd = null })) is { Status: RecoveryStatus.TooFewRecords, Records: 0 });

    var entry = new TokenEntry("test", now, "claude-fable-5-1", 1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000);
    Check("Cache read and both cache durations priced", pricing.Estimate(entry) == 92.75m);
    Check("Unknown model unpriced", pricing.Estimate(entry with { Model = "unreleased-unknown" }) is null);
    Check("Model variant not assigned base price", pricing.Find("gpt-6-astra-pro") is null);
    Check("Dated model matches exact base", pricing.Find("claude-sonnet-4-5-20250929")?.Prices.Input == .000003m);
    Check("Unknown cache price is not invented", pricing.Estimate(entry with { Model = "claude-sonnet-4-5" }) is null);
    Check("Token calculation is marked as an estimate", pricing.Summarize([entry]) is { Estimated: 1, IsEstimate: true });
    Check("Mixed known and unknown costs retain incompleteness", pricing.Summarize([entry, entry with { Model = "unknown" }]) is { Estimated: 1, Unpriced: 1 });
    using (var agPricing = new Pricing(Path.Combine(root, "antigravity-prices")))
    {
        var agPrice = new TokenPrices(.000001m, .000002m, .0000001m, null, null);
        foreach (var name in new[] { "google/gemini-3.8-flash", "google/gemini-3.1-pro-preview", "anthropic/claude-sonnet-4.6", "openai/gpt-oss-120b" })
            agPricing.TrySetManual(name, agPrice, false, out _);
        var agEntry = new TokenEntry("ag:test", now, "gemini-3.8-flash", 1000, 100, 0, 0, 200);
        Check("Antigravity reasoning suffixes use the base price", new[] { "gemini-3.8-flash-low", "google/gemini-3.1-pro-preview-high", "claude-sonnet-4.6-thinking", "openai/gpt-oss-120b-medium" }
            .All(name => agPricing.Estimate(agEntry with { Model = name }) == .00141m));
        Check("Distinct or unknown variants remain unpriced", new[] { "gemini-3.8-flash:free-high", "other/gemini-3.8-flash-high" }
            .All(name => agPricing.Estimate(agEntry with { Model = name }) is null));
        Check("Reasoning fallback is confined to Antigravity", agPricing.Estimate(agEntry with { Key = "other:test", Model = "gemini-3.8-flash-high" }) is null);
        Check("Reasoning aliases do not change catalog lookup", agPricing.Find("gemini-3.8-flash-high") is null);
        Check("Reported amount takes precedence", agPricing.Estimate(agEntry with { Model = "gemini-3.8-flash-high", ReportedCostUsd = 7m }) == 7m);
        Check("Base price updates reach reasoning variants", agPricing.TrySetManual("google/gemini-3.8-flash", agPrice with { Input = .000002m }, true, out _) && agPricing.Estimate(agEntry with { Model = "gemini-3.8-flash-high" }) == .00241m);
        Check("Explicit variant price takes precedence", agPricing.TrySetManual("google/gemini-3.8-flash-high", agPrice with { Input = .000003m }, false, out _) && agPricing.Estimate(agEntry with { Model = "gemini-3.8-flash-high" }) == .00341m);
        Check("A blank cache price in a manual base price charges the input price", agPricing.Estimate(agEntry with { Model = "gemini-3.8-flash-low", CacheWrite = 1 }) == .002412m);
        var empty = new TokenEntry("ag:empty", now, "", 0, 0, 0, 0, 0);
        Check("Empty steps do not mark totals incomplete", agPricing.Summarize([empty, agEntry]) is { Estimated: 1, Unpriced: 0 });
        Check("Unknown model with real usage stays incomplete", agPricing.Summarize([empty with { Input = 1 }]) is { Unpriced: 1 });
        Check("Empty model with reported cost is retained", agPricing.Summarize([empty with { ReportedCostUsd = 2m }]) is { Amount: 2m, Reported: 1 });
    }
    var longContext = new TokenEntry("long", now, "gpt-6-astra", 300_000, 0, 0, 0, 100);
    Check("Remote long-context tier used", pricing.Estimate(longContext) == 6.0075m);
    Check("Missing one-hour cache price stays unknown", pricing.Estimate(longContext with { CacheWriteHour = 10 }) is null);
    Check("Zero-cost models remain valid", pricing.Estimate(longContext with { Model = "free-model:free" }) == 0);
    Check("Free suffix never substituted for a paid model", pricing.Find("free-model") is null);

    priceTime = priceTime.AddHours(23);
    handler.Json = catalogJson.Replace("0.000003", "0.000004", StringComparison.Ordinal).Replace("vendor/dynamic-price", "vendor/new-catalog-model", StringComparison.Ordinal).Replace("\"-1\"", "\"0.000001\"", StringComparison.Ordinal);
    priceTime = priceTime.AddMinutes(59).AddSeconds(59);
    await pricing.RefreshAsync();
    Check("Daily catalog request waits for the full 24 hours", handler.Calls == 2 && !pricing.IsStale);
    priceTime = priceTime.AddSeconds(1);
    await pricing.RefreshAsync();
    Check("Price changes update without a new build", handler.Calls == 3 && pricing.Find("claude-sonnet-4-5")?.Prices.Input == .000004m);
    Check("New remote models require no local list update", pricing.ModelCount == 5 && pricing.Find("new-catalog-model") is not null);
    handler.Fail = true;
    await pricing.RefreshAsync(true);
    Check("Network failure keeps last successful catalog", pricing.ModelCount == 5 && pricing.LastError is not null && pricing.Find("claude-sonnet-4-5")?.Prices.Input == .000004m);
    await pricing.RefreshAsync();
    Check("Network failure does not trigger an immediate catalog retry", handler.Calls == 4);
    priceTime = priceTime.AddMinutes(6); handler.Fail = false;
    await pricing.RefreshAsync();
    Check("Network failure does not add a five-minute catalog retry", handler.Calls == 4 && pricing.LastError is not null);
    priceTime = priceTime.AddHours(23).AddMinutes(54);
    await pricing.RefreshAsync();
    Check("Next daily catalog refresh recovers", handler.Calls == 5 && pricing.LastError is null);
    var cacheBefore = File.ReadAllText(Path.Combine(priceDirectory, "model-prices.json"));
    handler.Json = "{\"data\":[]}";
    await pricing.RefreshAsync(true);
    Check("Empty remote response cannot erase valid prices", pricing.ModelCount == 5 && File.ReadAllText(Path.Combine(priceDirectory, "model-prices.json")) == cacheBefore);
    using (var missing = new Pricing(Path.Combine(root, "no-cache"), priceClient, () => priceTime))
    {
        await missing.RefreshAsync();
        Check("Offline first start has no invented prices", missing.ModelCount == 0 && missing.Estimate(entry) is null && missing.LastError is not null);
        Check("Reported costs do not depend on catalog availability", missing.Estimate(eventItem!) == .0345131m);
    }

    var codexRoot = Path.Combine(root, "codex"); var sessions = Path.Combine(codexRoot, "sessions"); Directory.CreateDirectory(sessions);
    string Event(DateTimeOffset timestamp, long total, long input, long cache, long output) => JsonSerializer.Serialize(new
    {
        timestamp, type = "event_msg", payload = new { type = "token_count", info = new
        { total_token_usage = new { total_tokens = total }, last_token_usage = new { input_tokens = input, cached_input_tokens = cache, cache_write_input_tokens = 10, output_tokens = output, reasoning_output_tokens = 5 } },
        rate_limits = new { limit_id = "codex", primary = new { used_percent = 10, window_minutes = 10080, resets_at = now.AddDays(1).ToUnixTimeSeconds() } } }
    });
    var first2 = Event(now.AddMinutes(-5), 120, 100, 80, 20);
    var duplicate = Event(now.AddMinutes(-4), 120, 100, 80, 20);
    var second = Event(now.AddMinutes(-3), 250, 110, 90, 20);
    string context = """{"type":"turn_context","payload":{"model":"gpt-6-astra"}}""";
    var quotaOnly = JsonSerializer.Serialize(new { timestamp = now.AddMinutes(-4), type = "event_msg", payload = new { type = "token_count", info = (object?)null } });
    File.WriteAllLines(Path.Combine(sessions, "session.jsonl"), [context, first2, duplicate, quotaOnly, duplicate, second, "{\"type\":\"token_count\",broken"]);
    File.WriteAllLines(Path.Combine(sessions, "fork.jsonl"), [context, first2]);
    var logs = new LogReader();
    var parsed = logs.Read(ProviderId.Codex, codexRoot, now);
    Check("Codex duplicate snapshots and forks deduplicated", parsed.Entries.Count == 2);
    Check("Quota-only event does not reset deduplication", parsed.Entries.Sum(x => x.Output) == 40);
    Check("Codex cache input not double counted; reasoning not added", parsed.Entries.Sum(x => x.Total) == 270);
    Check("Codex model context retained", parsed.Entries.All(x => x.Model == "gpt-6-astra"));
    Check("Malformed JSONL reported, valid rows retained", parsed.UsageNote.Contains("未能读取") && parsed.Entries.Count == 2);
    Check("Cached reads stable", logs.Read(ProviderId.Codex, codexRoot, now).Entries.Count == 2);
    var older = Event(now.AddDays(-45), 90, 30, 10, 5);
    var oldFile = Path.Combine(sessions, "older.jsonl");
    File.WriteAllLines(oldFile, [context, older]);
    File.SetLastWriteTimeUtc(oldFile, now.AddDays(-45).UtcDateTime);
    Check("Default history skips files outside 30 days", logs.Read(ProviderId.Codex, codexRoot, now).Entries.Count == 2);
    Check("Custom dates read older files", logs.Read(ProviderId.Codex, codexRoot, now, now.LocalDateTime.Date.AddDays(-60)).Entries.Count == 3);
    File.AppendAllText(Path.Combine(sessions, "fork.jsonl"), Event(now.AddDays(-40), 80, 25, 10, 5) + Environment.NewLine);
    _ = logs.Read(ProviderId.Codex, codexRoot, now);
    Check("An earlier date reparses cached files without dropping older entries", logs.Read(ProviderId.Codex, codexRoot, now, now.LocalDateTime.Date.AddDays(-60)).Entries.Count == 4);
    File.AppendAllText(Path.Combine(sessions, "session.jsonl"), Event(now.AddMinutes(-1), 370, 100, 80, 20) + Environment.NewLine);
    Check("Appended logs invalidate cache", logs.Read(ProviderId.Codex, codexRoot, now).Entries.Count == 3);
    var claudeRoot = Path.Combine(root, "claude"); var projects = Path.Combine(claudeRoot, "projects"); Directory.CreateDirectory(projects);
    string ClaudeEvent(long output) => JsonSerializer.Serialize(new { timestamp = now.AddMinutes(-1), type = "assistant", message = new { id = "same-message", model = "claude-sonnet-4-6", usage = new { input_tokens = 100, cache_read_input_tokens = 200, cache_creation_input_tokens = 30, cache_creation = new { ephemeral_1h_input_tokens = 10 }, output_tokens = output } } });
    File.WriteAllLines(Path.Combine(projects, "session.jsonl"), [ClaudeEvent(10), ClaudeEvent(40)]);
    var claude = logs.Read(ProviderId.Claude, claudeRoot, now);
    Check("Claude streamed messages use complete counters once", claude.Entries.Count == 1 && claude.Entries.Single().Total == 370);
    Check("Claude 1-hour writes separated", claude.Entries.Single().CacheWriteHour == 10 && claude.Entries.Single().CacheWrite == 20);
    // Lines over the reader's limit are skipped and counted once, when complete; lines under it grow the buffer.
    string Padded(string id, int padding) => JsonSerializer.Serialize(new { timestamp = now.AddMinutes(-1), type = "assistant", pad = new string('x', padding),
        message = new { id, model = "claude-sonnet-4-6", usage = new { input_tokens = 1, output_tokens = 1 } } });
    var longRoot = Path.Combine(root, "long-lines"); var longProjects = Path.Combine(longRoot, "projects"); Directory.CreateDirectory(longProjects);
    var longFile = Path.Combine(longProjects, "session.jsonl");
    File.WriteAllLines(longFile, [Padded("before", 10), Padded("too-long", 20_000), Padded("after", 10)]);
    var smallReader = new LogReader(4096);
    var skippedRead = smallReader.Read(ProviderId.Claude, longRoot, now);
    Check("A line over the limit is skipped and the lines around it are read",
        skippedRead.Entries.Select(e => e.Key).SequenceEqual(["claude:before", "claude:after"]) && skippedRead.UsageNote.Contains("1 处记录未能读取"));
    File.AppendAllText(longFile, Padded("unfinished", 20_000)[..15_000]);
    var unfinished = smallReader.Read(ProviderId.Claude, longRoot, now);
    Check("A long line still being written is not counted yet", unfinished.Entries.Count == 2 && unfinished.UsageNote.Contains("1 处记录未能读取"));
    File.AppendAllText(longFile, Padded("unfinished", 20_000)[15_000..] + "\n" + Padded("last", 10) + "\n");
    var finished = smallReader.Read(ProviderId.Claude, longRoot, now);
    Check("A long line is counted once it is complete and reading goes on after it",
        finished.Entries.Count == 3 && finished.Entries.Any(e => e.Key == "claude:last") && finished.UsageNote.Contains("2 处记录未能读取"));
    File.WriteAllLines(Path.Combine(longProjects, "large.jsonl"), [Padded("large", 3_000_000)]);
    Check("A line longer than the first buffer but under the limit is read whole",
        new LogReader().Read(ProviderId.Claude, longRoot, now).Entries.Any(e => e.Key == "claude:large"));
    // A long line without usage, such as a tool result or an attached file, is dropped without counting.
    var quietRoot = Path.Combine(root, "quiet-lines"); Directory.CreateDirectory(Path.Combine(quietRoot, "projects"));
    File.WriteAllLines(Path.Combine(quietRoot, "projects", "session.jsonl"),
        [Padded("q1", 10), JsonSerializer.Serialize(new { type = "user", content = new string('x', 20_000) }), Padded("q2", 10)]);
    var quiet = new LogReader(4096).Read(ProviderId.Claude, quietRoot, now);
    Check("A long line without usage is dropped quietly", quiet.Entries.Count == 2 && quiet.UsageComplete && !quiet.UsageNote.Contains("未能读取"));
    // "usage" starts 4 bytes before the end of the first 4096-byte read, so the key is split between two reads.
    File.WriteAllLines(Path.Combine(quietRoot, "projects", "session.jsonl"),
        ["{\"p\":\"" + new string('x', 4084) + "\",\"usage\":1,\"q\":\"" + new string('y', 10_000) + "\"}"]);
    var split = new LogReader(4096).Read(ProviderId.Claude, quietRoot, now);
    Check("A dropped line whose usage key is split between two reads is still counted", !split.UsageComplete && split.UsageNote.Contains("1 处记录未能读取"));
    // The limit is the one given, not the size of the array the pool hands out (8192 for 5000).
    string near = Padded("near", 4_700), over = Padded("over", 5_900);
    File.WriteAllLines(Path.Combine(quietRoot, "projects", "session.jsonl"), [near, over]);
    var exact = new LogReader(5000).Read(ProviderId.Claude, quietRoot, now);
    Check("The line limit is exactly the one given",
        near.Length < 5000 && over.Length is > 5000 and < 8192 && exact.Entries.Select(e => e.Key).SequenceEqual(["claude:near"]) && exact.UsageNote.Contains("1 处记录未能读取"));
    // A WSL distribution's file system: a user's .claude and .codex folders, and a user without any.
    var wslRoot = Path.Combine(root, "wsl-distribution");
    var wslClaude = Path.Combine(wslRoot, "home", "dev", ".claude"); Directory.CreateDirectory(Path.Combine(wslClaude, "projects"));
    Directory.CreateDirectory(Path.Combine(wslRoot, "home", "dev", ".codex")); Directory.CreateDirectory(Path.Combine(wslRoot, "home", "guest"));
    var wslHomes = WslLocations.Homes("Test", wslRoot);
    Check("WSL homes list only users with Claude or Codex records", wslHomes.Count == 1 && wslHomes[0].Claude == wslClaude && wslHomes[0].Codex.EndsWith(".codex"));
    File.WriteAllLines(Path.Combine(wslClaude, "projects", "wsl.jsonl"), [JsonSerializer.Serialize(new { timestamp = now.AddMinutes(-2), type = "assistant", message = new { id = "wsl-message", model = "claude-sonnet-4-6", usage = new { input_tokens = 7, output_tokens = 3 } } })]);
    var withWsl = logs.Read(ProviderId.Claude, [wslHomes[0].Claude], now, null, "WSL");
    Check("WSL records are read from their own folders", withWsl.Entries.Count == 1 && withWsl.Entries.Single().Total == 10 && withWsl.UsageNote.StartsWith("WSL"));
    Check("WSL records are left out unless asked for", logs.Read(ProviderId.Claude, claudeRoot, now).Entries.Count == 1);
    // The parsed Claude entry: input 100, cache read 200, cache write 20, one-hour cache write 10, output 40.
    using (var claudePricing = new Pricing(Path.Combine(root, "claude-prices")))
    {
        const decimal M = 1_000_000m;
        var claudeEntry = claude.Entries.Single();
        bool Priced(TokenPrices prices, bool replace, decimal perMillion) =>
            claudePricing.TrySetManual("claude-sonnet-4-6", prices, replace, out _) && claudePricing.Estimate(claudeEntry) == perMillion / M;
        Check("A parsed Claude one-hour write follows a manual cache write price when left blank",
            Priced(new(3 / M, 15 / M, null, 3.75m / M, null), false, 100 * 3 + 20 * 3.75m + 10 * 3.75m + 40 * 15));
        Check("A parsed Claude one-hour write falls back to the input price when both writes are blank",
            Priced(new(3 / M, 15 / M, null, null, null), true, 100 * 3 + 20 * 3 + 10 * 3 + 40 * 15));
        Check("A parsed Claude one-hour write uses its own manual price when filled",
            Priced(new(3 / M, 15 / M, null, 3.75m / M, 6 / M), true, 100 * 3 + 20 * 3.75m + 10 * 6 + 40 * 15));
    }
    Check("Missing source is unavailable, not zero", !logs.Read(ProviderId.Claude, Path.Combine(root, "missing"), now).UsageAvailable);
    var retired = SettingsMigrations.Read("""{"Apps":[{"QuotaSource":"claude","UsageSource":8},{"QuotaSource":"codex","UsageSource":9},{"QuotaSource":"claude","UsageSource":10}]}""");
    retired.Normalize();
    Check("Retired split Claude statistics return to Claude while later sources keep their saved numbers",
        retired.Apps.Select(app => app.UsageSource).SequenceEqual([ProviderId.Claude, ProviderId.Claude, ProviderId.ZCode]));
    Console.WriteLine($"\n{passed} checks passed.");
}

finally
{
    // A unique test-owned directory, verified before recursive cleanup.
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("BrimDeck-Tests-")) Directory.Delete(root, true);
}

sealed class CatalogHandler(string json) : HttpMessageHandler
{
    public string Json { get; set; } = json;
    public bool Fail { get; set; }
    public int Calls { get; private set; }
    public bool SawAuthorization { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Only catalog requests are counted; the Moonshot AI endpoint requests that follow each update are not.
        if (request.RequestUri!.AbsolutePath == "/api/v1/models") Calls++;
        SawAuthorization |= request.Headers.Authorization is not null;
        return Task.FromResult(new HttpResponseMessage(Fail ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK) { Content = new StringContent(Json) });
    }
}

sealed class FailingDesktopSources : IDesktopSources
{
    public bool Fail { get; set; } = true;
    public string? ReadCursorToken(string database) => null;
    public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation)
        => Fail ? Task.FromException<IReadOnlyList<LocalEndpoint>>(new System.Runtime.InteropServices.COMException()) : Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
}

sealed class ClaudeFixtureSources : IDesktopSources
{
    public ClaudeDesktopState State { get; set; } = new(false, []);
    public Exception? ReadFailure { get; set; }
    public int ReadCalls { get; private set; }
    public ClaudeDesktopState ReadClaudeDesktop(DataLocations locations)
    { ReadCalls++; if (ReadFailure is not null) throw ReadFailure; return State; }
    public string? ReadCursorToken(string database) => null;
    public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
}

sealed class ClaudeQuotaHandler : HttpMessageHandler
{
    public int Rejected { get; private set; }
    public List<string> Requests { get; } = [];
    public Func<string, CancellationToken, HttpResponseMessage?>? Override { get; set; }
    // Body of the account endpoint; without it the endpoint answers 404 and is not counted in Requests.
    public string? Profile { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsoluteUri == "https://api.anthropic.com/api/oauth/profile")
            return Task.FromResult(Profile is null ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Profile) });
        if (request.RequestUri?.AbsoluteUri != "https://api.anthropic.com/api/oauth/usage" || !request.Headers.Contains("anthropic-beta"))
            throw new InvalidOperationException("Unexpected quota destination or headers");
        var token = request.Headers.Authorization?.Parameter ?? "";
        Requests.Add(token);
        if (Override?.Invoke(token, cancellationToken) is { } response) return Task.FromResult(response);
        if (token == "test-rejected")
        { Rejected++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)); }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
        {
            five_hour = new { utilization = 9, resets_at = DateTimeOffset.UtcNow.AddHours(4) },
            seven_day = new { utilization = 4, resets_at = DateTimeOffset.UtcNow.AddDays(5) }
        })) });
    }
}

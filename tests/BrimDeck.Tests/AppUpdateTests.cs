using System.Net;
using BrimDeck.Core;

internal static class AppUpdateTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var time = DateTimeOffset.UtcNow;
        var backend = new UpdateFixture();
        var folder = Path.Combine(root, "updates");
        var service = new AppUpdates(backend, folder, () => time);
        check("updater starts without prompting or making network requests", backend.Checks == 0 && !service.ShouldPrompt(true, true));
        var pendingCheck = new TaskCompletionSource<AppRelease?>(); backend.CheckResult = pendingCheck.Task;
        var first = service.CheckAsync(false); var second = service.CheckAsync(true);
        check("concurrent version checks share one request", ReferenceEquals(first, second) && backend.Checks == 1);
        pendingCheck.SetResult(backend.Release); await first;
        check("update detection never downloads automatically", service.HasUpdate && backend.Downloads == 0 && service.Stage == AppUpdateStage.Available);
        check("closed settings do not prompt", !service.ShouldPrompt(false, true));
        check("inactive settings do not prompt", !service.ShouldPrompt(true, false));
        check("active settings can prompt", service.ShouldPrompt(true, true));
        service.MarkPromptShown();
        check("canceling dialog retains update without another prompt", service.HasUpdate && !service.ShouldPrompt(true, true));
        await service.CheckAsync(false);
        check("automatic checks are throttled", backend.Checks == 1);
        backend.CheckResult = null;
        var reopened = new AppUpdates(backend, folder); await reopened.CheckAsync(true);
        check("dismissed version is remembered after restart", reopened.HasUpdate && !reopened.ShouldPrompt(true, true));
        backend.Release = new("0.3.0", "New release", 200); await reopened.CheckAsync(true);
        check("a newer version can prompt again", reopened.ShouldPrompt(true, true));
        backend.FailCheck = true; await reopened.CheckAsync(true);
        check("network failure preserves the known update", reopened.HasUpdate && reopened.Error is not null);
        backend.FailCheck = false;
        var downloadGate = new TaskCompletionSource(); backend.DownloadGate = downloadGate.Task;
        var downloading = reopened.DownloadAsync(); await reopened.DownloadAsync();
        backend.Report?.Invoke(47);
        check("download progress is reported and duplicate downloads are blocked", reopened.Progress == 47 && backend.Downloads == 1);
        check("cannot install an incomplete download", !reopened.PrepareRestart(() => { }) && backend.Installs == 0);
        reopened.CancelDownload(); await downloading;
        check("download cancellation leaves update available", reopened.Stage == AppUpdateStage.Available && reopened.Error is null);
        backend.DownloadGate = null; backend.FailDownload = true; await reopened.DownloadAsync();
        check("failed download cannot become installable", reopened.Stage == AppUpdateStage.Available && reopened.Error is not null && backend.Installs == 0);
        backend.FailDownload = false; await reopened.DownloadAsync();
        check("verified download waits for explicit restart", reopened.Stage == AppUpdateStage.Ready && backend.Installs == 0);
        var checksBeforeReady = backend.Checks; await reopened.CheckAsync(true);
        check("checking again preserves an already prepared update", backend.Checks == checksBeforeReady && reopened.Stage == AppUpdateStage.Ready);
        check("failed settings save prevents installation", !reopened.PrepareRestart(() => throw new IOException()) && backend.Installs == 0);
        bool saved = false; backend.BeforeInstall = () => check("settings are saved before updater starts", saved);
        check("explicit install starts updater", reopened.PrepareRestart(() => saved = true) && backend.Installs == 1);
        check("repeat install requests are blocked", !reopened.PrepareRestart(() => { }) && backend.Installs == 1);
        backend.PendingRelease = backend.Release;
        var restored = new AppUpdates(backend, Path.Combine(root, "pending"));
        check("downloaded update is restored offline after restart", restored.Stage == AppUpdateStage.Ready && restored.Release == backend.Release);
        backend.PendingRelease = null; backend.Release = null; var empty = new AppUpdates(backend, Path.Combine(root, "empty"));
        await empty.CheckAsync(true);
        check("no newer release displays current version normally", !empty.HasUpdate && empty.LastChecked is not null && empty.Error is null);
        backend.CanInstall = false; backend.Release = new("0.5.0", "", 100); await empty.CheckAsync(true); await empty.DownloadAsync();
        check("unpackaged app can check but cannot overwrite its running executable", empty.HasUpdate && empty.Stage == AppUpdateStage.Available);
        var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await empty.CheckAsync(true, cancelled.Token);
        check("canceled check is quiet and preserves available release", empty.HasUpdate && empty.Error is null);
        await ScheduleTests(Path.Combine(root, "schedule"), check);
    }

    private static async Task ScheduleTests(string folder, Action<string, bool> check)
    {
        var start = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero); var now = start;
        var backend = new UpdateFixture();
        var service = new AppUpdates(backend, folder, () => now);
        await service.CheckInBackgroundAsync(true);
        check("no background check before 90 seconds", backend.Checks == 0);
        now = start.AddSeconds(90); await service.CheckInBackgroundAsync(false);
        check("offline skips a due background check", backend.Checks == 0);
        await service.CheckInBackgroundAsync(true);
        check("first background check runs 90 seconds after startup", backend.Checks == 1 && service.NoticeVersion == "0.2.0");
        now = start.AddHours(6); await service.CheckInBackgroundAsync(true);
        check("the next background check waits 6 hours after a success", backend.Checks == 1);
        now = start.AddSeconds(90).AddHours(6); await service.CheckInBackgroundAsync(true);
        check("background checks repeat every 6 hours", backend.Checks == 2);
        backend.FailCheck = true;
        now = now.AddHours(6); await service.CheckInBackgroundAsync(true);
        check("background failures are silent", backend.Checks == 3 && service.Error is null && service.HasUpdate);
        var first = service.NextBackgroundCheck - now;
        now += first; await service.CheckInBackgroundAsync(true);
        var second = service.NextBackgroundCheck - now;
        now += second; await service.CheckInBackgroundAsync(true);
        var third = service.NextBackgroundCheck - now;
        now += third; await service.CheckInBackgroundAsync(true);
        var fourth = service.NextBackgroundCheck - now;
        check("failed checks retry after 15 minutes, 1 hour, then every 4 hours", first == TimeSpan.FromMinutes(15) && second == TimeSpan.FromHours(1) &&
            third == TimeSpan.FromHours(4) && fourth == TimeSpan.FromHours(4) && backend.Checks == 6);
        now += TimeSpan.FromMinutes(30); await service.CheckInBackgroundAsync(true);
        check("no retry before the backoff ends", backend.Checks == 6);
        backend.FailCheck = false; backend.Failure = new UpdateCheckException(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(10));
        now = now - TimeSpan.FromMinutes(30) + fourth; await service.CheckInBackgroundAsync(true);
        check("Retry-After postpones the next check", backend.Checks == 7 && service.NextBackgroundCheck == now.AddHours(10));
        now = now.AddHours(5); await service.CheckAsync(false);
        check("opening settings also honors Retry-After", backend.Checks == 7);
        backend.Failure = null; now = now.AddHours(5); await service.CheckInBackgroundAsync(true);
        check("a success resets the backoff", backend.Checks == 8 && service.NextBackgroundCheck == now + UpdateSchedule.Interval);
        var restarted = new AppUpdates(backend, folder, () => now);
        await restarted.CheckInBackgroundAsync(true); now = now.AddSeconds(90); await restarted.CheckInBackgroundAsync(true);
        check("the last success survives a restart and skips the startup check", backend.Checks == 8 && restarted.LastChecked == service.LastChecked && restarted.NoticeVersion == "0.2.0");
        restarted.AutoCheck = false; now = now.AddHours(7); await restarted.CheckInBackgroundAsync(true); await restarted.CheckAsync(false);
        check("turning automatic checks off stops background and open-settings checks", backend.Checks == 8);
        await restarted.CheckAsync(true); restarted.SkipVersion();
        check("a skipped version hides the notice and the prompt", backend.Checks == 9 && restarted.NoticeVersion is null && !restarted.ShouldPrompt(true, true));
        check("the skipped version and the automatic setting persist", new AppUpdates(backend, folder, () => now) is { AutoCheck: false, NoticeVersion: null });
        backend.Release = new("0.3.0", "", 100); await restarted.CheckAsync(true);
        check("a newer version than the skipped one shows the notice again", restarted.NoticeVersion == "0.3.0");
        backend.Release = null; await restarted.CheckAsync(true);
        check("no newer version clears the notice", restarted.NoticeVersion is null && new AppUpdates(backend, folder, () => now).NoticeVersion is null);
        var legacy = Path.Combine(folder, "legacy"); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "update-prompt.txt"), "0.2.0");
        backend.Release = new("0.2.0", "Update notes", 100);
        var migrated = new AppUpdates(backend, legacy, () => now); await migrated.CheckAsync(true);
        check("update-prompt.txt is migrated once into update-state.json", migrated.HasUpdate && !migrated.ShouldPrompt(true, true) &&
            !File.Exists(Path.Combine(legacy, "update-prompt.txt")) && File.Exists(Path.Combine(legacy, "update-state.json")));

        var betaBackend = new UpdateFixture { Release = new("0.2.0-beta.1", "Beta notes", 100) };
        var betaFolder = Path.Combine(folder, "beta");
        var betas = new AppUpdates(betaBackend, betaFolder); await betas.CheckAsync(true);
        check("checks ask for releases only until betas are turned on", betaBackend.LastBeta == false && !betas.Beta);
        betas.Beta = true; await betas.CheckAsync(true);
        check("turning betas on asks for them and is remembered", betaBackend.LastBeta == true && betas.HasUpdate && new AppUpdates(betaBackend, betaFolder).Beta);
        betas.Beta = false;
        check("turning betas off withdraws a beta that was not downloaded", !betas.HasUpdate && betas.Stage == AppUpdateStage.Idle &&
            betas.NoticeVersion is null && new AppUpdates(betaBackend, betaFolder).NoticeVersion is null);
        betas.Beta = true; await betas.CheckAsync(true); await betas.DownloadAsync(); betas.Beta = false;
        check("a downloaded beta stays ready when betas are turned off", betas.Stage == AppUpdateStage.Ready && betas.Release?.Version == "0.2.0-beta.1");
    }
}

internal sealed class UpdateFixture : IAppUpdateBackend
{
    public string CurrentVersion { get; set; } = "0.1.0";
    public bool CanInstall { get; set; } = true;
    public AppRelease? PendingRelease { get; set; }
    public AppRelease? Release { get; set; } = new("0.2.0", "Update notes", 100);
    public Task<AppRelease?>? CheckResult { get; set; }
    public Task? DownloadGate { get; set; }
    public bool FailCheck { get; set; }
    public Exception? Failure { get; set; }
    public bool FailDownload { get; set; }
    public int Checks { get; private set; }
    public bool? LastBeta { get; private set; }
    public int Downloads { get; private set; }
    public int Installs { get; private set; }
    public Action<int>? Report { get; private set; }
    public Action? BeforeInstall { get; set; }
    public Task<AppRelease?> CheckAsync(CancellationToken cancellation)
    {
        Checks++;
        if (FailCheck) throw new HttpRequestException();
        if (Failure is not null) throw Failure;
        return CheckResult ?? Task.FromResult(Release);
    }
    public Task<AppRelease?> CheckAsync(bool beta, CancellationToken cancellation) { LastBeta = beta; return CheckAsync(cancellation); }
    public async Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation)
    {
        Downloads++; Report = progress;
        if (FailDownload) throw new IOException("Checksum mismatch");
        if (DownloadGate is not null) await DownloadGate.WaitAsync(cancellation);
        progress(100);
    }
    public void PrepareRestart() { BeforeInstall?.Invoke(); Installs++; }
}

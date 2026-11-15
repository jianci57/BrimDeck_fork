using System.Security.Cryptography;
using BrimDeck.Core;
using BrimDeck.Updates;

// Started by scripts/Test-AppUpdate.ps1 from an installed test copy, or by scripts/Test-PortableUpdate.ps1 from a portable
// one, with its own identity and data folder, so the test never touches the user's BrimDeck installation or settings.
// Like BrimDeck.exe, the new portable version also runs as the helper that replaces the old version's files.
if (PortableUpdate.TryRun(args, out var helperExit)) { Environment.ExitCode = helperExit; return; }
var testRoot = Environment.GetEnvironmentVariable("BRIMDECK_UPDATE_TEST_ROOT");
var appId = Environment.GetEnvironmentVariable("BRIMDECK_UPDATE_TEST_APPID");
var mutexPrefix = Environment.GetEnvironmentVariable("BRIMDECK_UPDATE_TEST_MUTEX");
if (string.IsNullOrWhiteSpace(testRoot) || !File.Exists(Path.Combine(testRoot, ".update-test-root")) || appId is null || mutexPrefix is null) return;
// Held like the app's single-instance mutex; the installer waits until this process exits.
using var instance = new Mutex(true, @"Local\" + mutexPrefix + Environment.UserName);
var result = Path.Combine(testRoot, "result.txt");
try
{
    var data = Path.Combine(testRoot, "user-data"); var store = new SettingsStore(data);
    using var http = new HttpClient(new FeedHandler(Path.Combine(testRoot, "feed")));
    var backend = GitHubUpdates.Create(new Uri("https://feed.test/" + UpdateManifest.FileName), (_, package) => new Uri("https://feed.test/" + package.File), http, appId, data);
    var updates = new AppUpdates(backend, data);
    var portable = backend.Mode == UpdateMode.Portable;
    if (!updates.CanInstall) throw new Exception("Installed or portable copy was not recognized: " + AppContext.BaseDirectory);
    // The portable helper or the installer could not put the new version in place, restored the old one and started it again.
    if (updates.RestartFailed) { File.WriteAllText(result, $"KEPT {updates.CurrentVersion} {updates.Stage}: {updates.Error}"); return; }
    if (updates.CurrentVersion == "0.1.1")
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(store.FilePath)));
        if (hash != File.ReadAllText(Path.Combine(testRoot, "settings.sha256"))) throw new Exception("Settings changed across the update.");
        // The installer may still be exiting and hold its file; the folder is then removed on the next start.
        if (updates.Stage != AppUpdateStage.Idle || updates.Release is not null) throw new Exception("The applied update is offered again.");
        File.WriteAllText(result, portable
            ? "PASS portable 0.1.0 downloaded and verified 0.1.1, the helper replaced files, app restarted, settings unchanged"
            : "PASS installed 0.1.0, downloaded and verified 0.1.1, installer replaced files, app restarted, settings unchanged");
        return;
    }
    if (updates.CurrentVersion != "0.1.0") throw new Exception("Unexpected installed test version " + updates.CurrentVersion);
    var settings = new DeckSettings { Width = 910, Height = 270, Theme = SettingsTheme.Light };
    store.Save(settings);
    File.WriteAllText(Path.Combine(testRoot, "settings.sha256"), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(store.FilePath))));
    await updates.CheckAsync(true);
    if (updates.Release?.Version != "0.1.1") throw new Exception("New test version not detected: " + updates.Error);
    await updates.DownloadAsync();
    if (updates.Stage != AppUpdateStage.Ready) throw new Exception("Download failed: " + updates.Error);
    if (!updates.PrepareRestart(() => store.Save(settings))) throw new Exception("Update launch failed: " + updates.Error);
    // Exit normally, like App.InstallUpdate. The installer waits for the mutex, or the portable helper for this process,
    // then replaces the files and starts the new version.
}
catch (Exception ex) { File.WriteAllText(result, "FAIL " + ex); Environment.ExitCode = 1; }

internal sealed class FeedHandler(string folder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, Path.GetFileName(request.RequestUri!.AbsolutePath));
        return Task.FromResult(File.Exists(path)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) }
            : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using BrimDeck.Core;

internal static class InstallerUpdateTests
{
    private static readonly Uri ManifestUri = new("https://updates.test/latest/update.json");
    private static readonly Uri BetaManifestUri = new("https://updates.test/beta-channel/update.json");

    public static async Task Run(string root, Action<string, bool> check)
    {
        var installer = RandomNumberGenerator.GetBytes(300_000);
        var manifest = new UpdateManifest("0.2.0", "Release notes", "BrimDeck-0.2.0-Setup.exe", installer.Length,
            Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant());
        var server = new ReleaseServer { Manifest = manifest.ToJson(), Installer = installer };
        using var http = new HttpClient(server);
        var directory = Path.Combine(root, "installer-updates");
        var launches = new List<ProcessStartInfo>();
        InstallerUpdateBackend Backend(string version = "0.1.0", UpdateMode mode = UpdateMode.Installer) => new(http, ManifestUri,
            (m, p) => new Uri($"https://updates.test/v{m.Version}/{p.File}"), version, directory, mode, start => { launches.Add(start); return true; });

        var backend = Backend();
        var release = await backend.CheckAsync(CancellationToken.None);
        check("installer manifest reports a newer release with notes and size", release == new AppRelease("0.2.0", "Release notes", installer.Length));
        var localized = UpdateManifest.Parse((manifest with { Notes = new([("zh-CN", "中文说明"), ("en-US", "English notes")]) }).ToJson()).Notes;
        check("localized notes survive the manifest round trip", localized == new ReleaseNotes([("zh-CN", "中文说明"), ("en-US", "English notes")]));
        check("notes follow the interface language", localized.For("zh-CN") == "中文说明" && localized.For("en-US") == "English notes");
        check("another region of the same language uses that language", localized.For("zh-TW") == "中文说明");
        check("an unknown language falls back to English", localized.For("ja-JP") == "English notes");
        check("notes without English fall back to the first language", new ReleaseNotes([("zh-CN", "中文说明")]).For("en-US") == "中文说明");
        check("plain-text notes show in every language", UpdateManifest.Parse(manifest.ToJson()).Notes.For("ja-JP") == "Release notes");
        check("the same version is not offered again", await Backend("0.2.0").CheckAsync(CancellationToken.None) is null);
        check("a newer running version ignores an older manifest", await Backend("0.10.0").CheckAsync(CancellationToken.None) is null);
        check("a prerelease is offered its final release", await Backend("0.2.0-beta.3").CheckAsync(CancellationToken.None) is not null);
        check("prereleases order by label and before their release", ApplicationVersion.Compare("0.2.0-alpha.2", "0.2.0-beta.1") < 0
            && ApplicationVersion.Compare("0.2.0-beta.2", "0.2.0-beta.10") < 0 && ApplicationVersion.Compare("0.2.0-beta.10", "0.2.0") < 0
            && ApplicationVersion.Compare("0.2.0", "0.3.0-alpha.1") < 0 && ApplicationVersion.Compare("0.2.0-beta.1", "0.2.0-beta.1+abc") == 0);

        foreach (var (name, json) in new[]
        {
            ("path in installer name", (manifest with { File = @"..\BrimDeck-0.2.0-Setup.exe" }).ToJson()),
            ("non-executable installer", (manifest with { File = "BrimDeck-0.2.0-Setup.zip" }).ToJson()),
            ("short checksum", (manifest with { Sha256 = "abc" }).ToJson()),
            ("four-part version", (manifest with { Version = "0.2.0.1" }).ToJson()),
            ("path in version", (manifest with { Version = "0.2.0-beta/../x" }).ToJson()),
            ("missing size", (manifest with { Size = 0 }).ToJson()),
        })
        {
            server.Manifest = json; bool rejected = false;
            try { await Backend().CheckAsync(CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            check("manifest with " + name + " is rejected", rejected);
        }
        server.Manifest = manifest.ToJson();

        async Task<bool> DownloadFails(byte[] served)
        {
            server.Installer = served; var attempt = Backend(); var found = await attempt.CheckAsync(CancellationToken.None);
            try { await attempt.DownloadAsync(found!, _ => { }, CancellationToken.None); return false; }
            catch (InvalidDataException) { return true; }
        }
        var corrupt = (byte[])installer.Clone(); corrupt[1234] ^= 0xFF;
        check("an installer with a wrong checksum is rejected", await DownloadFails(corrupt));
        check("an installer larger than announced is rejected", await DownloadFails([.. installer, 1]));
        check("a rejected download leaves no pending installer", Backend().PendingRelease is null && !Directory.Exists(directory));

        server.Installer = installer; backend = Backend(); release = await backend.CheckAsync(CancellationToken.None);
        var reported = new List<int>();
        await backend.DownloadAsync(release!, reported.Add, CancellationToken.None);
        var saved = Path.Combine(directory, manifest.File);
        check("verified installer is stored with progress up to 100%", File.Exists(saved) && reported.LastOrDefault() == 100 &&
            reported.SequenceEqual(reported.Order()) && !File.Exists(saved + ".partial"));
        var requests = server.Requests;
        check("a verified download is restored after restart without network", Backend().PendingRelease == release && server.Requests == requests);

        var restarted = Backend(); _ = restarted.PendingRelease; restarted.PrepareRestart();
        var start = launches.SingleOrDefault();
        check("install runs the verified installer silently, with its error prompts, and relaunches the app", start is not null && start.FileName == saved &&
            start.Arguments == "/SILENT /NORESTART /RELAUNCH" && start.UseShellExecute);
        // The installer could not install 0.2.0, put 0.1.0 back, started it and left the note beside the download.
        File.WriteAllText(restarted.RestartFailureFile, PortableUpdate.Failed);
        var kept = new AppUpdates(Backend(), Path.Combine(root, "installer-updates-kept"));
        check("a failed installation is reported once after the old version starts again, with the download still ready", kept.RestartFailed
            && kept.Error is not null && kept.Stage == AppUpdateStage.Ready && !File.Exists(restarted.RestartFailureFile)
            && Backend().LastRestartFailure is null);
        File.WriteAllText(restarted.RestartFailureFile, PortableUpdate.Failed);
        check("an installed copy names the installer as the reason", Backend().LastRestartFailure == RestartFailure.Installer);

        File.WriteAllBytes(saved, corrupt); launches.Clear(); bool refused = false;
        try { Backend().PrepareRestart(); } catch (InvalidDataException) { refused = true; }
        check("an installer changed after download is never run", refused && launches.Count == 0 && !Directory.Exists(directory));

        backend = Backend(); await backend.DownloadAsync((await backend.CheckAsync(CancellationToken.None))!, _ => { }, CancellationToken.None);
        check("the downloaded installer is removed once that version is running", Backend("0.2.0").PendingRelease is null && !Directory.Exists(directory));

        var service = new AppUpdates(Backend(mode: UpdateMode.None), Path.Combine(root, "installer-updates-data"));
        await service.CheckAsync(true); await service.DownloadAsync();
        check("a copy that was not installed by the installer only reports the release", service.HasUpdate && service.Stage == AppUpdateStage.Available && !Directory.Exists(directory));
        service = new AppUpdates(Backend(), Path.Combine(root, "installer-updates-data"));
        await service.CheckAsync(true); await service.DownloadAsync(); launches.Clear();
        check("the update service prepares and starts the installer", service.Stage == AppUpdateStage.Ready && service.PrepareRestart(() => { }) && launches.Count == 1);

        var betaBackend = new InstallerUpdateBackend(http, ManifestUri, (m, p) => new Uri($"https://updates.test/v{m.Version}/{p.File}"), "0.1.0",
            Path.Combine(root, "installer-updates-beta"), UpdateMode.Installer, betaManifest: BetaManifestUri);
        server.BetaManifest = (manifest with { Version = "0.3.0-beta.1" }).ToJson();
        check("without betas the beta channel is not read", (await betaBackend.CheckAsync(false, CancellationToken.None))?.Version == "0.2.0" && server.BetaRequests == 0);
        check("a beta newer than the release is offered", (await betaBackend.CheckAsync(true, CancellationToken.None))?.Version == "0.3.0-beta.1");
        server.BetaManifest = (manifest with { Version = "0.2.0-beta.2" }).ToJson();
        check("a release newer than the beta is offered", (await betaBackend.CheckAsync(true, CancellationToken.None))?.Version == "0.2.0");
        server.BetaManifest = null;
        check("a missing beta channel leaves the release", (await betaBackend.CheckAsync(true, CancellationToken.None))?.Version == "0.2.0" && server.BetaRequests == 3);
        server.BetaManifest = "broken";
        check("an unreadable beta manifest leaves the release", (await betaBackend.CheckAsync(true, CancellationToken.None))?.Version == "0.2.0");
    }

    private sealed class ReleaseServer : HttpMessageHandler
    {
        public string Manifest { get; set; } = "";
        public byte[] Installer { get; set; } = [];
        // Null answers the beta manifest with 404, as before the first beta.
        public string? BetaManifest { get; set; }
        public int Requests { get; private set; }
        public int BetaRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri == BetaManifestUri) BetaRequests++;
            HttpContent? content = request.RequestUri == ManifestUri ? new StringContent(Manifest)
                : request.RequestUri == BetaManifestUri && BetaManifest is not null ? new StringContent(BetaManifest)
                : request.RequestUri?.AbsolutePath == "/v0.2.0/BrimDeck-0.2.0-Setup.exe" ? new ByteArrayContent(Installer) : null;
            return Task.FromResult(new HttpResponseMessage(content is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = content ?? new StringContent("") });
        }
    }
}

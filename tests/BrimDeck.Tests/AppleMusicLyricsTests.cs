using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class AppleMusicLyricsTests
{
    private const string Ttml = "<tt xmlns='http://www.w3.org/ns/ttml'><body dur='4:37.500'><div><p begin='1.500' end='3.000'><span>前</span><span>一句</span></p><p begin='00:08.000' end='10s'>下一句</p></div></body></tt>";
    private static readonly MediaTrack Track = new() { Source = "Apple Music", Title = "唱月", Artist = "李耀陽", Album = "唱月 - Single", End = TimeSpan.FromSeconds(277.5) };

    public static async Task Run(string root, Action<string, bool> check)
    {
        var parsed = LyricsService.ParseAppleTtml(Ttml);
        check("Apple TTML preserves line timing, word spans and a long gap", parsed.Synced && parsed.At(2).Current == "前一句" && parsed.At(4).Current == "♪" && parsed.At(9).Current == "下一句");
        var reported = Track with { PositionKnown = true, ReportedPosition = TimeSpan.FromSeconds(1.35) };
        check("Apple Music's lines are shown 0.2 seconds early", parsed.At(reported, DateTimeOffset.UtcNow).Current == "前一句"
            && parsed.At(reported with { Source = "网易云音乐" }, DateTimeOffset.UtcNow).Current == "♪");
        var shortGap = LyricsService.ParseAppleTtml("<tt xmlns='http://www.w3.org/ns/ttml'><body><div><p begin='1' end='3'>前一句</p><p begin='5' end='7'>下一句</p></div></body></tt>");
        check("A gap under five seconds keeps the line", shortGap.At(4).Current == "前一句" && shortGap.At(6).Current == "下一句");
        var plain = LyricsService.ParseAppleTtml("<tt xmlns='http://www.w3.org/ns/ttml'><body><div><p>第一行</p><p>第二行</p></div></body></tt>");
        check("Apple untimed lyrics remain plain text", !plain.Synced && plain.Lines.Count == 2);
        bool rejected = false;
        try { LyricsService.ParseAppleTtml("<!DOCTYPE tt [<!ENTITY x SYSTEM 'file:///missing'>]><tt>&x;</tt>"); }
        catch (System.Xml.XmlException) { rejected = true; }
        check("Apple TTML refuses external entities", rejected);

        // Apple Music's cache folders are hidden system folders.
        string folder = Path.Combine(root, "apple-cache"), hidden = Path.Combine(folder, "15ZD3MVJ"); Directory.CreateDirectory(hidden);
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
        var now = DateTime.UtcNow;
        string Write(string name, string ttml, DateTime written)
        {
            string path = Path.Combine(hidden, name);
            File.WriteAllText(path, JsonSerializer.Serialize(new { lyricsId = "AP_1632686041", status = "success", ttml }));
            File.SetCreationTimeUtc(path, written); File.SetLastWriteTimeUtc(path, written);
            return path;
        }
        string Other(string text, string length) => $"<tt xmlns='http://www.w3.org/ns/ttml'><body dur='{length}'><div><p begin='1.000'>{text}</p></div></body></tt>";
        // The lyric ID names an older release, as for a reissued song.
        string file = Write("ttmlLyrics[1].json", Ttml, now);
        Write("ttmlLyrics[2].json", Other("另一首同时写入", "3:25.333"), now);
        Write("ttmlLyrics[3].json", Other("十分钟前的同长歌曲", "4:37.400"), now.AddMinutes(-10));
        Write("ttmlLyrics[4].json", Other("下一首", "4:38.000"), now.AddSeconds(8));
        Write("ttmlLyrics[5].json", Other("稍早写入的同长歌曲", "4:37.500"), now.AddSeconds(-12));

        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [folder]))
        {
            var result = await service.FindAsync(Track, true, default);
            check("Apple Music's cached lyrics are matched by write time and length, not by lyric ID", result.Source == "Apple Music" && result.At(2).Current == "前一句");
            check("Apple Music lyrics need no request to Apple", web.Urls.All(u => !u.Host.EndsWith("apple.com")));
        }
        // Halfway through the song its file was written about 120 seconds ago.
        var playing = Track with { ReportedPosition = TimeSpan.FromSeconds(120), PositionAt = DateTimeOffset.UtcNow };
        string midway = Path.Combine(root, "apple-midway"); Directory.CreateDirectory(midway);
        File.Copy(file, Path.Combine(midway, "ttmlLyrics[1].json"));
        File.SetCreationTimeUtc(Path.Combine(midway, "ttmlLyrics[1].json"), now.AddSeconds(-120)); File.SetLastWriteTimeUtc(Path.Combine(midway, "ttmlLyrics[1].json"), now.AddSeconds(-120));
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [midway]))
            check("A song looked up midway finds the file written as it started", (await service.FindAsync(playing, true, default)).Source == "Apple Music");
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [midway]))
            check("A file written long before the song started is not used", (await service.FindAsync(Track, true, default)).Lines.Count == 0);
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [folder]))
            check("A file whose length differs from the song's is not used", (await service.FindAsync(Track with { End = TimeSpan.FromSeconds(240) }, true, default)).Lines.Count == 0);

        // Untimed lyrics carry no length.
        const string Untimed = "<tt xmlns='http://www.w3.org/ns/ttml'><body><div><p>我寻过山</p><p>诗中桃花</p></div></body></tt>";
        foreach (var (name, written, used) in new[] { ("apple-untimed", now.AddSeconds(2), true), ("apple-untimed-earlier", now.AddSeconds(-6), false) })
        {
            string untimed = Path.Combine(root, name); Directory.CreateDirectory(untimed);
            string path = Path.Combine(untimed, "ttmlLyrics[1].json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { lyricsId = "CP_1681962363", status = "success", ttml = Untimed }));
            File.SetCreationTimeUtc(path, written); File.SetLastWriteTimeUtc(path, written);
            using var web = new Web();
            using var service = new LyricsService(web.Http, () => [untimed]);
            var result = await service.FindAsync(Track, true, default);
            check(used ? "Untimed lyrics written as the song started are used" : "Untimed lyrics written before the song started are not used",
                used ? result is { Source: "Apple Music", Synced: false, Lines.Count: 2 } : result.Lines.Count == 0);
        }

        string Lrc(bool synced) => JsonSerializer.Serialize(new { trackName = Track.Title, artistName = "李耀阳", duration = 277.5,
            syncedLyrics = synced ? "[00:01]LRCLIB同步" : "", plainLyrics = "LRCLIB纯文本" });
        string QqSearch = JsonSerializer.Serialize(new { code = 0, data = new { song = new { list = new[] { new {
            songmid = "000000000000AA", songname = Track.Title, singer = new[] { new { name = "李耀阳" } }, albumname = "唱月", interval = 278 } } } } });
        string QqRpc = JsonSerializer.Serialize(new { code = 0, lyric = new { code = 0, data = new { lyric = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]QQ同步")) } } });
        foreach (bool qqWorks in new[] { true, false })
        {
            using var web = new Web { Others = u => u.Host switch {
                "lrclib.net" => Lrc(false),
                "c.y.qq.com" => qqWorks ? QqSearch : "{}",
                "u.y.qq.com" => QqRpc,
                "music.163.com" when u.AbsolutePath.Contains("search") => JsonSerializer.Serialize(new { result = new { songs = new[] { new { id = 1, name = Track.Title, artists = new[] { new { name = "李耀阳" } }, duration = 277500 } } } }),
                "music.163.com" => JsonSerializer.Serialize(new { lrc = new { lyric = "[00:01]网易同步" } }),
                _ => "{}" } };
            using var service = new LyricsService(web.Http, () => []);
            var result = await service.FindAsync(Track, true, default);
            check("Apple plain LRCLIB continues to " + (qqWorks ? "QQ before NetEase" : "NetEase after QQ fails"), result.Source == (qqWorks ? "QQ 音乐" : "网易云音乐") && result.Synced &&
                (!qqWorks || web.Urls.All(u => u.Host != "music.163.com")));
        }
        using (var web = new Web { Others = u => u.Host == "lrclib.net" ? Lrc(true) : "{}" })
        using (var service = new LyricsService(web.Http, () => []))
        {
            var result = await service.FindAsync(Track, true, default);
            check("Apple synced LRCLIB stops before QQ", result.Synced && result.Source == "LRCLIB" && web.Urls.All(u => !u.Host.EndsWith("qq.com") && u.Host != "music.163.com"));
        }
        using (var web = new Web { Others = u => u.Host == "lrclib.net" ? Lrc(false) : "{}" })
        using (var service = new LyricsService(web.Http, () => []))
            check("Apple keeps plain lyrics when every synced provider fails", (await service.FindAsync(Track, true, default)) is { Synced: false, Lines.Count: > 0 });
        foreach (string source in new[] { "QQ 音乐", "网易云音乐", "Google Chrome" })
        {
            using var web = new Web();
            using var service = new LyricsService(web.Http, () => throw new InvalidOperationException("Non-Apple source touched Apple cache"));
            await service.FindAsync(Track with { Source = source }, true, default);
            check("Only Apple desktop gets the new lookup chain: " + source,
                web.Urls.All(u => !u.Host.EndsWith("apple.com")) &&
                (source == "QQ 音乐" ? web.Urls.All(u => u.Host.EndsWith("qq.com")) : web.Urls.All(u => !u.Host.EndsWith("qq.com"))));
        }

        // A song played again gets no new file; the lyric ID it was matched to is kept on disk.
        string remembered = Path.Combine(root, "apple-remembered"), saved = Path.Combine(root, "apple-music-lyrics.json"); Directory.CreateDirectory(remembered);
        string firstPlay = Path.Combine(remembered, "ttmlLyrics[1].json");
        File.Copy(file, firstPlay); File.SetCreationTimeUtc(firstPlay, now); File.SetLastWriteTimeUtc(firstPlay, now);
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [remembered], saved))
            check("A song played for the first time is matched by time", (await service.FindAsync(Track, true, default)).Source == "Apple Music" && File.Exists(saved));
        File.SetCreationTimeUtc(firstPlay, now.AddMinutes(-30)); File.SetLastWriteTimeUtc(firstPlay, now.AddMinutes(-30));
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [remembered], saved))
            check("A song played again after a restart finds the lyric ID it was matched to", (await service.FindAsync(Track, true, default)).Source == "Apple Music");
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [remembered], Path.Combine(root, "apple-music-lyrics-none.json")))
            check("Without the kept lyric ID a file from long before is not used", (await service.FindAsync(Track, true, default)).Lines.Count == 0);
        File.WriteAllText(Path.Combine(root, "apple-music-lyrics-damaged.json"), "{");
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [remembered], Path.Combine(root, "apple-music-lyrics-damaged.json")))
            check("A damaged file of lyric IDs is started over", (await service.FindAsync(Track, true, default)).Lines.Count == 0);

        // Looked up midway after a pause, the song started well before the estimate.
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [remembered]))
            check("A song looked up midway takes a file as long as the song from before a pause",
                (await service.FindAsync(Track with { ReportedPosition = TimeSpan.FromSeconds(60), PositionAt = DateTimeOffset.UtcNow }, true, default)).Source == "Apple Music");
        // Seen as it started and looked up again a minute in, the song played before has no new file; another song's is not taken.
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [remembered]))
            check("A song seen from its start does not take a file from long before when looked up again later",
                (await service.FindAsync(Track with { State = MediaState.Playing, PositionAt = DateTimeOffset.UtcNow.AddSeconds(-60) }, true, default)).Lines.Count == 0);
        string twoEarlier = Path.Combine(root, "apple-two-earlier"); Directory.CreateDirectory(twoEarlier);
        string ownSong = Path.Combine(twoEarlier, "ttmlLyrics[1].json"), otherSong = Path.Combine(twoEarlier, "ttmlLyrics[2].json");
        File.Copy(firstPlay, ownSong); File.SetCreationTimeUtc(ownSong, now.AddMinutes(-30)); File.SetLastWriteTimeUtc(ownSong, now.AddMinutes(-30));
        File.WriteAllText(otherSong, JsonSerializer.Serialize(new { lyricsId = "AP_1524793900", status = "success", ttml = Other("同长的另一首歌", "4:37.500") }));
        File.SetCreationTimeUtc(otherSong, now.AddMinutes(-5)); File.SetLastWriteTimeUtc(otherSong, now.AddMinutes(-5));
        var midwayTrack = Track with { ReportedPosition = TimeSpan.FromSeconds(60), PositionAt = DateTimeOffset.UtcNow };
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [twoEarlier]))
            check("A song looked up midway takes no file from long before when two of its length differ", (await service.FindAsync(midwayTrack, true, default)).Lines.Count == 0);
        string claimed = Path.Combine(root, "apple-music-lyrics-claimed.json");
        File.WriteAllText(claimed, JsonSerializer.Serialize(new Dictionary<string, string> { ["exile\nTaylor Swift\nfolklore\n278"] = "AP_1632686041" }));
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [remembered], claimed))
            check("A song looked up midway does not take a file matched to a song of another title", (await service.FindAsync(midwayTrack, true, default)).Lines.Count == 0);
        // Kept from an earlier version: the same lyrics matched to songs of different titles are dropped when loaded.
        string shared = Path.Combine(root, "apple-music-lyrics-shared.json");
        File.WriteAllText(shared, JsonSerializer.Serialize(new Dictionary<string, string> { [Track.SongKey] = "AP_1632686041", ["exile\nTaylor Swift\nfolklore\n278"] = "AP_1632686041" }));
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [remembered], shared))
            check("Lyrics kept for songs of different titles are matched again", (await service.FindAsync(Track, true, default)).Lines.Count == 0);
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [remembered], saved))
            check("Lyrics kept for one song are still used", (await service.FindAsync(Track, true, default)).Source == "Apple Music");
        // Apple Music once wrote a song's lyrics minutes into the song.
        string late2 = Path.Combine(root, "apple-written-late"); Directory.CreateDirectory(late2);
        string lateFile = Path.Combine(late2, "ttmlLyrics[1].json");
        File.Copy(file, lateFile); File.SetCreationTimeUtc(lateFile, now.AddMinutes(3)); File.SetLastWriteTimeUtc(lateFile, now.AddMinutes(3));
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [late2]))
            check("A file as long as the song written minutes after it started is used", (await service.FindAsync(Track, true, default)).Source == "Apple Music");

        // Apple Music writing a lyric file is reported, inside its hidden folders too.
        string watched = Path.Combine(root, "apple-watched"), watchedHidden = Path.Combine(watched, "SAXCYR7O"); Directory.CreateDirectory(watchedHidden);
        File.SetAttributes(watchedHidden, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
        using (var web = new Web())
        using (var service = new LyricsService(web.Http, () => [watched]))
        {
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.AppleLyricsWritten += () => written.TrySetResult();
            await service.FindAsync(Track, true, default);
            File.Copy(file, Path.Combine(watchedHidden, "ttmlLyrics[1].json"));
            check("A lyric file Apple Music writes is reported", await Task.WhenAny(written.Task, Task.Delay(5000)) == written.Task);
        }

        // A third-party result found first does not hide Apple's lyrics once Apple Music caches them.
        string late = Path.Combine(root, "apple-late"); Directory.CreateDirectory(late);
        using (var web = new Web { Others = u => u.Host == "lrclib.net" ? Lrc(true) : "{}" })
        using (var service = new LyricsService(web.Http, () => [late]))
        {
            check("Before Apple Music caches the lyrics a third-party result is used", (await service.FindAsync(Track, true, default)).Source == "LRCLIB");
            File.Copy(file, Path.Combine(late, "ttmlLyrics.json"));
            File.SetCreationTimeUtc(Path.Combine(late, "ttmlLyrics.json"), DateTime.UtcNow); File.SetLastWriteTimeUtc(Path.Combine(late, "ttmlLyrics.json"), DateTime.UtcNow);
            check("Lyrics Apple Music caches later replace the third-party result", (await service.FindAsync(Track, true, default)).Source == "Apple Music");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool cancelled = false;
            try { await service.FindAsync(Track, true, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            check("Cancelled Apple lookup does not deliver an old song", cancelled);
        }
    }

    // The other providers, answered through Others.
    private sealed class Web : HttpMessageHandler
    {
        public Func<Uri, string> Others { get; init; } = _ => "{}";
        public List<Uri> Urls { get; } = [];
        public HttpClient Http { get; }
        public Web() => Http = new HttpClient(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Others(request.RequestUri!)) });
        }
    }
}

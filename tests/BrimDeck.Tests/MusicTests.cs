using System.Net;
using BrimDeck.Core;

internal static class MusicTests
{
    public static async Task Run(Action<string, bool> check)
    {
        await MediaRoutingTests.Run(check);
        MediaSeekPreviewTests.Run(check);
        MediaPlaybackStackTests.Run(check);
        var modes = new MediaTrack { CanRepeat = true, CanShuffle = true, Repeat = MediaRepeat.List };
        check("Modes cycle list, single, shuffle, order, list", MusicPlaybackModes.Next(modes) == MusicPlaybackMode.Track
            && MusicPlaybackModes.Next(modes with { Repeat = MediaRepeat.Track }) == MusicPlaybackMode.Shuffle
            && MusicPlaybackModes.Next(modes with { Shuffle = true }) == MusicPlaybackMode.Order
            && MusicPlaybackModes.Next(modes with { Repeat = MediaRepeat.None }) == MusicPlaybackMode.List);
        check("Repeat-only sources omit shuffle", MusicPlaybackModes.Supported(modes with { CanShuffle = false }).SequenceEqual([MusicPlaybackMode.List, MusicPlaybackMode.Track, MusicPlaybackMode.Order]));
        check("Sources without modes omit the mode control", MusicPlaybackModes.Supported(new MediaTrack()).Count == 0);
        PageLayout(check);
        var narrow = CompactMusicLayout.Calculate(115, true, 4, true);
        check("A narrow compact row keeps one rotating ring, at full size, beside a scrolling lyric", narrow.VisibleRings == 1 && narrow.ScrollRings && narrow.RingSize == 12 && narrow.TextWidth == 41);
        var capsule = CompactMusicLayout.Calculate(91.3, true, 4, true);
        check("The capsule beside the widest clock cuts its first ring at the region's edge instead of shrinking it", capsule.VisibleRings == 0 && capsule.RingSize == 12
            && Math.Abs(capsule.RingWidth - 11.3) < 1e-9 && capsule.TextWidth == 18 && capsule.TextScrolls);
        var tight = CompactMusicLayout.Calculate(84, true, 4, true);
        check("Short of one ring, the lyric keeps its minimum, then the space between, then the first ring is cut", tight.VisibleRings == 0 && !tight.ScrollRings
            && tight.TextWidth == 18 && tight.Between == 8 && tight.RingWidth == 4 && tight.TextScrolls && tight.RingSize == 12);
        check("Without the clock the default notch and capsule rotate two full-size rings beside the lyric", CompactMusicLayout.Calculate(178, true, 4, true) is { VisibleRings: 2, ScrollRings: true, RingSize: 12, RingGap: 8 }
            && CompactMusicLayout.Calculate(164, true, 4, true) is { VisibleRings: 2, ScrollRings: true, RingSize: 12, RingGap: 8 });
        CompactLayout(check);
        var now = DateTimeOffset.UtcNow;
        var track = new MediaTrack { Title = "测试曲目", Artist = "测试歌手", Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(210), ReportedPosition = TimeSpan.FromSeconds(20), PositionAt = now, State = MediaState.Playing, Rate = 2 };
        check("Playback position applies rate and timeline origin", track.Position(now.AddSeconds(3)).TotalSeconds == 16);
        check("Paused position does not advance", (track with { State = MediaState.Paused }).Position(now.AddSeconds(30)).TotalSeconds == 10);
        check("Position never passes the media duration", track.Position(now.AddDays(1)) == track.Duration);
        check("Known duration without playback position is not a timeline", !(track with { PositionKnown = false }).HasTimeline && (track with { PositionKnown = false }).Position(now.AddMinutes(2)) == TimeSpan.Zero);
        check("Seeking respects advertised bounds and tick units", (track with { MinSeek = TimeSpan.FromSeconds(30), MaxSeek = TimeSpan.FromSeconds(150) }).SeekTicks(1) == TimeSpan.FromSeconds(150).Ticks);
        var lrc = Lyrics.Parse("[offset:-500]\n[00:02.00][00:06.25]同一行\n[00:20]后一行", null, 210, "test");
        check("LRC multiple timestamps and offsets are parsed", lrc.Synced && lrc.Lines.Count == 3 && lrc.Lines[0].Seconds == 1.5 && lrc.Lines[1].Seconds == 5.75);
        check("Lyrics show an instrumental introduction before the first timestamp", lrc.At(0).Current == "♪");
        check("Long lyric lines stay visible until the next timestamp", lrc.At(10.75).Current == "同一行" && lrc.At(12).Current == "同一行" && lrc.At(19.499).Current == "同一行" && lrc.At(19.5).Current == "后一行");
        var words = Lyrics.Parse("[03:17.552]<03:17.552>我<03:17.855>也<03:18.143>可<03:18.479>以 <03:19.000>", null, 252, "test");
        check("Word timing tags are removed from LRC lines", words.Lines.Count == 1 && words.Lines[0].Text == "我也可以" && Math.Abs(words.Lines[0].Seconds - 197.552) < 0.001);
        var gap = Lyrics.Parse("[00:02]长句\n[00:14]\n[00:20]下一句", null, 40, "网易云音乐");
        check("Only timestamped blank lines start an instrumental gap", gap.At(13.999).Current == "长句" && gap.At(14).Current == "♪" && gap.At(19.999).Current == "♪" && gap.At(20).Current == "下一句");
        check("Seeking backwards from an explicit gap restores the lyric", gap.At(16).Current == "♪" && gap.At(8).Current == "长句");
        check("Timestamped lyrics require a real playback position to synchronize", lrc.CanSynchronize(track) && !lrc.CanSynchronize(track with { PositionKnown = false }) && !lrc.CanSynchronize(track with { End = TimeSpan.Zero }));
        var plain = Lyrics.Parse(null, "甲\n乙", 100, "test");
        check("Plain lyrics retain text without fabricating timestamps", !plain.Synced && plain.Lines.Select(l => l.Text).SequenceEqual(["甲", "乙"]) && plain.Lines.All(l => l.Seconds == 0));
        check("Plain lyrics never select a timed line", !plain.CanSynchronize(track) && plain.At(0).Current == "" && plain.At(60).Current == "");
        var netease = Lyrics.Parse("[00:00.00] 作曲 : Yiruma\n[00:05.00]纯音乐，请欣赏\n", null, 200, "网易云音乐");
        var qq = Lyrics.Parse("[00:00:00]此歌曲为没有填词的纯音乐，请您欣赏", null, 200, "QQ 音乐");
        check("Credits with an instrumental notice mark the song instrumental, without lines", netease is { Instrumental: true, Lines.Count: 0 } && qq is { Instrumental: true, Lines.Count: 0 }
            && netease.At(30).Current == "♪" && Lyrics.Parse(null, "作曲：某人\n纯音乐，请欣赏", 200, "test").Instrumental);
        var sung = Lyrics.Parse("[00:00.00] 作曲 : 某人\n[00:05.00]纯音乐，请欣赏\n[00:12.00]第一句歌词", null, 200, "test");
        check("A notice among sung lines is a lyric, and a long interlude stays a gap", !sung.Instrumental && sung.Lines.Count == 3 && !gap.Instrumental && gap.At(16).Current == "♪");
        var song = Lyrics.Parse("[00:01]一\n[00:02]二\n[00:03]三\n[00:04]四\n[00:05]五", null, 10, "test");
        check("The lyric window gives the lines around the current one in the order they are sung, as many as there are",
            song.Around(3.5, 2, 2) is { Current: "三", Before: ["一", "二"], After: ["四", "五"] }
            && song.Around(1.5, 2, 3) is { Current: "一", Before: [], After: ["二", "三", "四"] }
            && song.Around(5.5, 1, 2) is { Current: "五", Before: ["四"], After: [] }
            && song.Around(0, 2, 1) is { Current: "♪", Before: [], After: ["一"] }
            && song.At(3.5) == ("二", "三", "四") && song.At(0) == ("", "♪", "一") && plain.Around(0, 2, 2) == LyricWindow.Empty);
        using var handler = new LyricsHandler(); using var http = new HttpClient(handler); using var service = new LyricsService(http);
        check("Lyrics disabled sends no request", (await service.FindAsync(track, false, default)).Lines.Count == 0 && handler.Calls == 0);
        var result = await service.FindAsync(track, true, default);
        check("Lyrics provider uses identified requests without credentials", result.Lines.Count == 1 && handler.Calls == 1 && handler.Identified && !handler.Authorization);
        await service.FindAsync(track, true, default);
        check("Repeated tracks are served from the lyrics cache", handler.Calls == 1);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); bool cancelled = false;
        try { await service.FindAsync(track with { Title = "另一首" }, true, canceled.Token); } catch (OperationCanceledException) { cancelled = true; }
        check("Cancelled lyrics queries do not issue requests", cancelled && handler.Calls == 1);
        var nativeLyrics = await service.FindAsync(track with { EmbeddedLyrics = "[00:02]播放器提供的完整歌词" }, true, default);
        check("Native full lyrics bypass online lookup", nativeLyrics.At(2).Current == "播放器提供的完整歌词" && handler.Calls == 1);
        using var versioned = new NeteaseLyricHandler(); using var versionedHttp = new HttpClient(versioned); using var versionedService = new LyricsService(versionedHttp);
        var firstVersion = await versionedService.FindAsync(track with { Source = "NetEase", SongId = "netease:2692035029" }, true, default);
        check("A NetEase lyric still at its first version is requested in full", firstVersion.At(1.5).Current == "first" && versioned.Requested.All(u => !u.Contains("lv=1")));
        async Task<(Lyrics Lyrics, List<Uri> Requested)> Served(Func<Uri, HttpResponseMessage> reply)
        {
            using var served = new ReplyHandler(reply); using var servedHttp = new HttpClient(served); using var servedService = new LyricsService(servedHttp);
            return (await servedService.FindAsync(track, true, default), served.Requested);
        }
        const string LrclibReply = "{\"trackName\":\"测试曲目\",\"artistName\":\"测试歌手\",\"duration\":200,\"syncedLyrics\":\"[00:01]歌词\"}";
        HttpResponseMessage Moved(string location) { var moved = new HttpResponseMessage(HttpStatusCode.Found); moved.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute); return moved; }
        var sameHost = await Served(uri => uri.AbsolutePath == "/moved" ? new(HttpStatusCode.OK) { Content = new StringContent(LrclibReply) } : Moved("/moved"));
        check("A lyrics redirect to HTTPS on the same host is followed", sameHost.Lyrics.Lines.Count == 1 && sameHost.Requested[1].ToString() == "https://lrclib.net/moved");
        var elsewhere = await Served(uri => uri.Host == "lrclib.net" ? Moved("https://lyrics.example/get") : new(HttpStatusCode.OK) { Content = new StringContent(LrclibReply) });
        check("A lyrics redirect to another host or to HTTP is not followed",
            elsewhere.Lyrics.Lines.Count == 0 && elsewhere.Requested.All(uri => uri.Host != "lyrics.example")
            && (await Served(uri => uri.Scheme == "https" ? Moved("http://lrclib.net/get") : new(HttpStatusCode.OK) { Content = new StringContent(LrclibReply) })).Requested.All(uri => uri.Scheme == "https"));
        var loop = await Served(uri => Moved("/again"));
        check("Lyrics redirects stop after a few hops", loop.Lyrics.Lines.Count == 0 && loop.Requested.Count(uri => uri.Host == "lrclib.net") <= 3 * 2);
        string padded = LrclibReply.Insert(1, "\"pad\":\"" + new string('x', LyricsService.MaxResponseBytes) + "\",");
        var large = await Served(_ => new(HttpStatusCode.OK) { Content = new StringContent(padded) });
        check("A lyrics reply over the size limit is given up", large.Lyrics.Lines.Count == 0);
        var undeclared = await Served(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(padded))) });
        check("A lyrics reply without a declared length is cut off at the limit", undeclared.Lyrics.Lines.Count == 0);
        var bom = await Served(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(LrclibReply)]) });
        check("A lyrics reply under the limit is still read, byte order mark included", bom.Lyrics.Lines.Count == 1);
        await QqLyricsTests.Run(check);
    }
    private sealed class ReplyHandler(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public readonly List<Uri> Requested = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requested) Requested.Add(request.RequestUri!);
            return Task.FromResult(reply(request.RequestUri!));
        }
    }
    private static void PageLayout(Action<string, bool> check)
    {
        MusicLayout At(PanelSize size) { var (w, h) = MusicSizes.For(size); return MusicLayout.Calculate(w, h, true, true); }
        var compact = At(PanelSize.Compact); var standard = At(PanelSize.Standard); var spacious = At(PanelSize.Spacious);
        check("The presets are densities 0, 1 and 2, the largest panel 3", compact.Density == 0 && standard.Density == 1 && spacious.Density == 2
            && MusicLayout.Calculate(1200, 400, true, true).Density == 3);
        check("The compact preset puts the artist on the title line to keep a lyric line", compact.MergeTitle && compact.LyricLines == 1);
        check("The standard preset gives the artist its own line above one lyric line", !standard.MergeTitle && standard.LyricLines == 1);
        check("The spacious preset shows the lines before and after the current lyric", !spacious.MergeTitle && spacious.LyricLines == 3);
        check("Without lyrics the artist keeps its own line at the compact preset", !MusicLayout.Calculate(440, 140, false, true).MergeTitle);
        bool steadyTitles = true;
        foreach (var (w, h) in new[] { (440.0, 140.0), (440.0, 200.0), (560.0, 280.0), (1200.0, 400.0) })
        {
            var waiting = MusicLayout.Calculate(w, h, false, true, lyricsExpected: true); var found = MusicLayout.Calculate(w, h, true, true, lyricsExpected: true);
            steadyTitles &= waiting.MergeTitle == found.MergeTitle && waiting.HeaderHeight == found.HeaderHeight && waiting.Cover == found.Cover
                && waiting.CoverY == found.CoverY && waiting.LyricRoom == found.LyricRoom && waiting.LyricLines == 0 && found.LyricLines == found.LyricRoom;
        }
        check("With lyrics expected, the titles keep their place and the room its lines until lyrics arrive", steadyTitles);
        check("A tall, narrow panel stays one step above the narrow preset", MusicLayout.DensityFor(440, 400) == 1);
        check("Without a timeline the cover takes the progress row's room",
            MusicLayout.Calculate(440, 200, true, false).Cover > standard.Cover && MusicLayout.Calculate(440, 200, true, false).ProgressHeight == 0);
        // Without the cover (DeckSettings.MusicPageCover) the titles and the lyrics take two columns.
        var columnsOk = true;
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (w, h) = MusicSizes.For(size);
            foreach (var (width, height) in new[] { (w, h), (1200.0, 400.0), (440.0, 140.0) })
            {
                var l = MusicLayout.Calculate(width, height, true, true); var cols = l.Columns(true);
                double lyricsHeight = cols.CurrentLine + (cols.LyricLines - 1) * l.Metrics.LyricLine;
                double titles = l.Metrics.TitleLine + (cols.ShowAlbum ? 2 : 1) * l.Metrics.ArtistLine + (cols.ShowEqualizer ? cols.EqualizerGap + l.Metrics.Equalizer : 0);
                columnsOk &= cols.LyricLines is >= 1 and <= 7 && lyricsHeight <= cols.Height + 1e-9 && titles <= cols.Height + 1e-9
                    && cols.InfoWidth + 2 * cols.DividerGap + 1 + cols.LyricsWidth == l.BodyWidth && cols.LyricsWidth > cols.InfoWidth
                    && cols.Before <= cols.After && cols.After - cols.Before <= 1 && cols.CurrentLyric > l.Metrics.Lyric;
            }
        }
        var wideColumns = MusicLayout.Calculate(1200, 400, true, true).Columns(true);
        check("Without the cover the titles take the left column and the lyrics the rest, as many lines as fit around the larger current one",
            columnsOk && wideColumns.LyricLines > MusicLayout.Calculate(1200, 400, true, true).LyricLines
            && MusicLayout.Calculate(440, 200, true, true).Columns(false) is { InfoWidth: 440 - 40, LyricsWidth: 0, LyricLines: 0 });
        check("The bottom margin below the transport equals the side margin", standard.Metrics.Bottom == standard.Metrics.Side && spacious.Metrics.Bottom == spacious.Metrics.Side);
        var failures = new List<string>(); double title = 0;
        for (double h = 140; h <= 400; h += 2)
        {
            var growing = MusicLayout.Calculate(1200, h, true, true).Metrics;
            if (growing.Title < title) failures.Add($"title shrinks at {h}");
            title = growing.Title;
            for (double w = 440; w <= 1200; w += 20)
                foreach (bool timeline in new[] { true, false })
                foreach (bool lyrics in new[] { true, false })
                {
                    var l = MusicLayout.Calculate(w, h, lyrics, timeline, fullControlEntry: true); var c = l.Metrics;
                    string at = $"{w}x{h} timeline={timeline} lyrics={lyrics}";
                    double barTop = l.ProgressY + (c.SeekRow - c.Bar) / 2, inkTop = l.ControlsY + c.Play / 8;
                    double upperBottom = (timeline ? barTop : inkTop) - c.SectionGap;
                    if (l.Cover < 40 || l.CoverY < 0 || l.CoverY + l.Cover > upperBottom + .5) failures.Add("cover " + at);
                    if (l.TextX + l.TextWidth != l.BodyWidth || l.TextWidth < c.MinColumn) failures.Add("text column " + at);
                    if (l.HeaderHeight + (l.LyricLines > 0 ? c.MiddleGap + l.LyricLines * c.LyricLine : 0) > l.Cover) failures.Add("text height " + at);
                    if (lyrics && l.LyricLines == 0) failures.Add("no lyric line " + at);
                    if (Math.Abs(l.ControlsY + c.Play * 7 / 8 - l.BodyHeight) > .5) failures.Add("transport bottom " + at);
                    if (timeline && (l.TimesY < l.ProgressY + (c.SeekRow + c.BarHover) / 2 || l.TimesY + c.TimeLine + c.ControlsGap > inkTop + .5)) failures.Add("times " + at);
                    if (l.ModeX < 0 || l.ModeX + c.Mode + 16 > l.TripletX || l.TripletX + l.TripletWidth > l.BodyWidth || Math.Abs(l.TripletX + l.TripletWidth / 2 - l.BodyWidth / 2) > .5) failures.Add("transport " + at);
                    if (!l.ShowFullControl) failures.Add("full control entry " + at);
                }
        }
        check("The music page fits every size from 440x140 to 1200x400" + (failures.Count > 0 ? ": " + string.Join("; ", failures.Take(5)) : ""), failures.Count == 0);
    }
    // NetEase's lyric endpoint answers lv=<held version> with an empty lyric unless its own version is newer.
    private sealed class NeteaseLyricHandler : HttpMessageHandler
    {
        public readonly List<string> Requested = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString(); Requested.Add(url);
            if (!url.StartsWith("https://music.163.com/api/song/lyric?", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var held = System.Text.RegularExpressions.Regex.Match(url, @"[?&]lv=(-?\d+)");
            string lyric = held.Success && int.Parse(held.Groups[1].Value) >= 1 ? "" : "[00:01]first";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"lrc\":{\"version\":1,\"lyric\":\"" + lyric + "\"},\"code\":200}") });
        }
    }
    private sealed class LyricsHandler : HttpMessageHandler
    {
        public int Calls; public bool Identified, Authorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Identified = request.Headers.UserAgent.ToString().Contains("BrimDeck"); Authorization |= request.Headers.Authorization is not null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"trackName\":\"测试曲目\",\"artistName\":\"测试歌手\",\"duration\":200,\"syncedLyrics\":\"[00:01]歌词\"}") });
        }
    }
    // The collapsed row for any width and height: regions that clip instead of hiding, sizes that follow the height.
    private static void CompactLayout(Action<string, bool> check)
    {
        check("The whole-row overload at the default height equals the old one", CompactMusicLayout.Calculate(178, 1, true, 4, true, 0) == CompactMusicLayout.Calculate(178, true, 4, true));
        var standard = CompactMusicLayout.Calculate(178, 1, true, 4, true, 33);
        check("The default notch with a clock keeps the old regions", standard.CoverWidth == 28 && standard.EqualizerWidth == 26 && standard.ClockWidth == 41
            && standard.MiddleWidth == 83 && standard.CoverSize == 20 && standard.EqualizerSize == 18 && standard.EqualizerBar == 3 && standard.TextFont == 11.5
            && standard.TextHeight == 22 && standard.RowHeight == 24 && standard.RingThickness == 2 && standard.TextEdge == 6 && standard.RingEdge == 3);
        var wide = CompactMusicLayout.Calculate(400, 1, true, 4, true, 33);
        check("A wider row only widens the middle, and the lyric takes the rest", wide.CoverWidth == standard.CoverWidth && wide.EqualizerWidth == standard.EqualizerWidth
            && wide.ClockWidth == standard.ClockWidth && wide.MiddleWidth - standard.MiddleWidth == 222 && wide.VisibleRings == 4 && wide.RingSize == 12 && !wide.ScrollRings
            && wide.TextWidth == wide.MiddleWidth - wide.RingWidth - wide.Between);
        var tall = CompactMusicLayout.Calculate(400, 1.5, true, 4, true, 50);
        check("A taller row scales the cover, equalizer, rings and type, in whole and half DIPs", tall.CoverSize == 30 && tall.CoverWidth == 42 && tall.EqualizerSize == 27
            && tall.EqualizerHeight == 21 && tall.EqualizerWidth == 39 && tall.ClockWidth == 62 && tall.TextFont == 17.5 && tall.TextHeight == 33 && tall.RowHeight == 36
            && tall.RingSize == 18 && tall.RingThickness == 3 && tall.TextEdge == 9);

        // The middle gives way first; then the equalizer, the cover and the clock, each to nothing before the next.
        var sides = CompactMusicLayout.Calculate(95, 1, true, 4, true, 33);
        check("With no room left for the middle every side region is whole", sides.MiddleWidth == 0 && sides.CoverWidth == 28 && sides.EqualizerWidth == 26 && sides.ClockWidth == 41);
        var noEq = CompactMusicLayout.Calculate(85, 1, true, 4, true, 33);
        check("The equalizer region shrinks first", noEq.EqualizerWidth == 16 && noEq.CoverWidth == 28 && noEq.ClockWidth == 41);
        var noCover = CompactMusicLayout.Calculate(55, 1, true, 4, true, 33);
        check("The cover region shrinks next", noCover.EqualizerWidth == 0 && noCover.CoverWidth == 14 && noCover.ClockWidth == 41);
        var clockOnly = CompactMusicLayout.Calculate(20, 1, true, 4, true, 33);
        check("The clock region shrinks last", clockOnly.EqualizerWidth == 0 && clockOnly.CoverWidth == 0 && clockOnly.ClockWidth == 20 && clockOnly.TextWidth == 0 && clockOnly.RingWidth == 0);
        var resting = CompactMusicLayout.Calculate(54 + 10, 1, true, 4, true, 0);
        check("A lyric region under its minimum rests, cut at the edge", resting.TextWidth == 10 && !resting.TextScrolls && resting.RingWidth == 0);
        var ringsOnly = CompactMusicLayout.Calculate(54 + 6, 1, true, 4, false, 0);
        check("Rings alone in less than one ring show the first ring cut", ringsOnly.VisibleRings == 0 && ringsOnly.RingWidth == 6 && !ringsOnly.ScrollRings && ringsOnly.TextWidth == 0);
        var alone = CompactMusicLayout.Calculate(20, 1, false, 0, false, 33);
        check("A lone clock has no gap and is cut to the row", alone.ClockAlone && alone.ClockWidth == 20);
        // The equalizer shown apart from the media.
        check("The equalizer without media keeps its region, with the gap toward the middle only when something is there",
            CompactMusicLayout.Calculate(200, 1, false, 0, false, 33, 0, true) is { EqualizerWidth: 18, ClockAlone: false, ClockWidth: 41, CoverWidth: 0 }
            && CompactMusicLayout.Calculate(200, 1, false, 2, false, 33, 0, true) is { EqualizerWidth: 26 }
            && CompactMusicLayout.Calculate(200, 1, false, 0, false, 0, 0, true) is { EqualizerWidth: 18 }
            && CompactMusicLayout.Calculate(200, 1, false, 0, false, 0, 120, true) is { MonitorAlone: false });
        check("The equalizer's length follows its bars, four by default, and the count stays in its range",
            CompactMusicLayout.BarsWidth(4, 1) == 18 && CompactMusicLayout.BarsWidth(20, 1) == 98 && CompactMusicLayout.BarsWidth(3, 2) == 26
            && CompactMusicLayout.BarsWidth(1, 1) == 13 && CompactMusicLayout.BarsWidth(99, 1) == 98 && new DeckSettings { EqualizerBars = 0 }.EqualizerBars == 3
            && new DeckSettings { MusicEqualizerBars = 30 }.MusicEqualizerBars == 20 && CompactMusicLayout.Calculate(300, 1, true, 0, true, 0, 0, true, 8) is { EqualizerWidth: 8 + 38, EqualizerSize: 38 }
            && CompactMusicLayout.NaturalWidth(1, false, 0, false, 0, 0, 0, true, 6) == 28);
        check("Media without the equalizer has no equalizer region", CompactMusicLayout.Calculate(200, 1, true, 0, true, 33, 0, false) is { EqualizerWidth: 0, CoverWidth: 28 });
        check("The width the row asks for counts the equalizer apart from the media",
            CompactMusicLayout.NaturalWidth(1, false, 0, false, 0, 0, 0, true) == 18 && CompactMusicLayout.NaturalWidth(1, false, 0, false, 33, 0, 0, true) == 18 + 8 + 33
            && CompactMusicLayout.NaturalWidth(1, true, 0, false, 0, 0, 0, false) == 20 + 8
            && CompactMusicLayout.RequiredWidth(1, false, false, 33, 0, 0, true) == 18 + 8 + 33 && CompactMusicLayout.RequiredWidth(1, true, true, 0, 0, 0, false) == 20 + 8 + 18);
        // The cover switched off (DeckSettings.MusicCompactCover): the lyric and the equalizer keep the media's place in the row.
        check("Media without the cover has no cover region, and the lyric keeps the clock from standing alone",
            CompactMusicLayout.Calculate(200, 1, true, 0, true, 33, 0, true, CompactMusicLayout.DefaultBars, showCover: false) is { CoverWidth: 0, EqualizerWidth: 26, ClockWidth: 41, ClockAlone: false, TextWidth: 200 - 26 - 41 }
            && CompactMusicLayout.Calculate(200, 1, true, 0, false, 33, 0, false, CompactMusicLayout.DefaultBars, showCover: false) is { CoverWidth: 0, ClockAlone: true }
            && CompactMusicLayout.NaturalWidth(1, true, 0, true, 0, 0, 0, true, CompactMusicLayout.DefaultBars, cover: false) == CompactMusicLayout.TextSlot + 18 + 8
            && CompactMusicLayout.RequiredWidth(1, true, true, 0, 0, 0, true, CompactMusicLayout.DefaultBars, cover: false) == 18 + 8 + 18);

        // Any width, scale and clock width gives finite regions that are never negative and never add up to more than the row.
        double[] widths = [0, .4, 1, 7, 17.9, 18, 36, 54, 84, 91.3, 115, 164, 178, 400, 1e6, double.NaN, -5, double.PositiveInfinity];
        double[] scales = [.75, 1, 1.25, 1.5, 2, 3, 0, -1, double.NaN, double.PositiveInfinity, 1e-9];
        double[] clocks = [0, 33, 80, double.NaN, -3];
        bool sane = true;
        foreach (var w in widths) foreach (var s in scales) foreach (var clock in clocks) foreach (var media in new[] { true, false })
        foreach (var rings in new[] { 0, 1, 4, 12, -2 }) foreach (var text in new[] { true, false })
        {
            var l = CompactMusicLayout.Calculate(w, s, media, rings, text, clock * (double.IsFinite(s) && s > 0 ? s : 1));
            double[] values = [l.MiddleWidth, l.RingWidth, l.TextWidth, l.Between, l.CoverWidth, l.EqualizerWidth, l.ClockWidth, l.RingSize, l.RingGap, l.TextFont, l.TextHeight, l.RowHeight, l.Scale];
            double row = double.IsFinite(w) && w > 0 ? w : 0;
            sane &= values.All(v => double.IsFinite(v) && v >= 0) && l.VisibleRings >= 0 && l.VisibleRings <= Math.Max(0, rings)
                && l.CoverWidth + l.MiddleWidth + l.EqualizerWidth + (l.ClockAlone ? 0 : l.ClockWidth) <= row + 1e-9
                && l.RingWidth + l.Between + l.TextWidth <= l.MiddleWidth + 1e-9 && (!l.ClockAlone || l.ClockWidth <= row + 1e-9);
        }
        check("Every width and scale gives finite, non-negative regions within the row", sane);

        // One DIP more width changes each side region by at most one DIP and never makes one smaller.
        bool steady = true;
        foreach (var s in new[] { .75, 1, 1.5, 2 })
        {
            var previous = CompactMusicLayout.Calculate(0, s, true, 4, true, 33 * s);
            for (int w = 1; w <= 600; w++)
            {
                var next = CompactMusicLayout.Calculate(w, s, true, 4, true, 33 * s);
                foreach (var (a, b) in new[] { (previous.CoverWidth, next.CoverWidth), (previous.EqualizerWidth, next.EqualizerWidth), (previous.ClockWidth, next.ClockWidth), (previous.MiddleWidth, next.MiddleWidth) })
                    steady &= b >= a - 1e-9 && b - a <= 1 + 1e-9;
                previous = next;
            }
        }
        check("Regions follow the width without jumps", steady);

        // The 23:20 / 23:21 report: six rings beside a lyric, a few DIPs short of fitting them all, used to rotate three rings
        // in 40 % of the middle, and one DIP wider showed all six beside the lyric's 56 DIP: 60 DIP moved at once.
        double sideRegions = 28 + 26;
        var before = CompactMusicLayout.Calculate(sideRegions + 175, 1, true, 6, true, 0);
        var after = CompactMusicLayout.Calculate(sideRegions + 176, 1, true, 6, true, 0);
        check("One DIP across the old all-rings threshold moves the rings and the lyric by at most one DIP",
            Math.Abs(after.RingWidth - before.RingWidth) <= 1 && Math.Abs(after.TextWidth - before.TextWidth) <= 1 && before.ScrollRings && after.ScrollRings);
        check("All rings stand still once the middle holds them in its 40 % share", CompactMusicLayout.Calculate(sideRegions + 280, 1, true, 6, true, 0) is { RingWidth: 112, ScrollRings: false, VisibleRings: 6 });

        // Every region, for every style, height, clock, ring count, lyric and cover, moves by at most one DIP per DIP of
        // width from 0 to 800, also between rotating and static rings. Snapped to device pixels the way the row lays them out
        // (cumulatively from the middle's left edge, kept inside its column), adjacent regions never overlap.
        var screen = new DipRect(0, 0, 3840, 2160);
        bool continuous = true, apart = true, sameInputsSameRow = true; string firstJump = "", firstOverlap = "";
        foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Capsule, CompactStyle.Line })
        foreach (var height in new[] { 24d, 32, 40, 48, 64 })
        foreach (var clock in new[] { 0d, 33 })
        foreach (var media in new[] { true, false })
        foreach (var text in new[] { true, false })
        for (int rings = 0; rings <= 8; rings++)
        {
            CompactMusicLayout? last = null;
            for (int w = 0; w <= 800; w++)
            {
                var settings = new DeckSettings { NotchWidth = w, NotchHeight = height, CapsuleWidth = w, CapsuleHeight = height, LineLength = w, LineThickness = height / 8 };
                var shape = CollapsedGeometry.Resolve(settings, style, screen, screen);
                // Straight from the raw width as well, since the settings clamp the shape's width.
                foreach (var (content, raw) in new[] { (shape.ContentWidth, false), ((double)w, true) })
                {
                    var l = CompactMusicLayout.Calculate(content, shape.Scale, media, rings, text, clock * shape.Scale);
                    // Nothing else feeds the row: the same inputs give the same regions, whatever the lyric or the carousel do.
                    sameInputsSameRow &= l == CompactMusicLayout.Calculate(content, shape.Scale, media, rings, text, clock * shape.Scale);
                    if (!raw) continue;
                    if (last is not null)
                    {
                        double[] a = [last.CoverWidth, last.MiddleWidth, last.RingWidth, last.Between, last.TextWidth, last.EqualizerWidth, last.ClockWidth];
                        double[] b = [l.CoverWidth, l.MiddleWidth, l.RingWidth, l.Between, l.TextWidth, l.EqualizerWidth, l.ClockWidth];
                        for (int i = 0; i < a.Length; i++)
                            if (Math.Abs(a[i] - b[i]) > 1 + 1e-9) { if (continuous) firstJump = $"{style} h{height} clock{clock} rings{rings} text{text} w{w} region{i}: {a[i]} -> {b[i]}"; continuous = false; }
                    }
                    last = l;
                    foreach (var dpi in new[] { 1, 1.25, 1.5, 1.75, 2 })
                    {
                        double Down(double v) => double.IsFinite(v) && v > 0 ? Math.Floor(v * dpi + .001) / dpi : 0;
                        double row = Down(content), coverCol = Down(l.CoverWidth), eqCol = Down(l.EqualizerWidth), clockCol = l.ClockAlone ? 0 : Down(l.ClockWidth);
                        double middleCol = Math.Max(0, row - coverCol - eqCol - clockCol);
                        double ringEnd = Math.Min(middleCol, Down(l.RingWidth)), textStart = Math.Min(middleCol, Down(l.RingWidth + l.Between));
                        double textEnd = Math.Min(middleCol, Down(l.RingWidth + l.Between + l.TextWidth));
                        // The carousel draws up to its own snapped width; its left fade lies over the cover's whole gap.
                        double carousel = Math.Floor(l.RingWidth * dpi + .001) / dpi;
                        bool ok = coverCol + eqCol + clockCol <= row + 1e-9 && ringEnd <= textStart && textStart <= textEnd && carousel <= textStart + 1e-9
                            && (l.RingWidth == 0 || l.CoverWidth == 0 || l.CoverWidth - l.CoverSize >= l.RingEdge);
                        if (!ok && apart) { firstOverlap = $"{style} h{height} clock{clock} rings{rings} w{w} dpi{dpi}"; apart = false; }
                    }
                }
            }
        }
        check("Every region follows the width continuously, for every style, height, clock and ring count" + (continuous ? "" : ": " + firstJump), continuous);
        check("Adjacent regions never overlap at any width, height or DPI" + (apart ? "" : ": " + firstOverlap), apart);
        check("The row depends on its width, height, ring count and toggles alone", sameInputsSameRow);
        // The carousel's arithmetic (MusicVisuals.QuotaCarousel): the right fade spans at most one ring gap and never reaches
        // the first ring, and the glide by whole ring steps ends with the last ring whole before the fade, inside the region.
        bool carouselFits = true;
        foreach (var s in new[] { .75, 1, 1.3125, 1.5, 2 }) foreach (var dpi in new[] { 1, 1.25, 1.5, 2 })
        for (int rings = 2; rings <= 8; rings++) for (int w = 0; w <= 800; w++)
        {
            var l = CompactMusicLayout.Calculate(w, s, true, rings, true, 33 * s);
            double step = Math.Round((l.RingSize + l.RingGap) * dpi, MidpointRounding.AwayFromZero) / dpi;
            double span = Math.Floor(l.RingWidth * dpi + .001) / dpi, allRings = (rings - 1) * step + l.RingSize;
            if (!(l.ScrollRings && span >= l.RingSize && span < allRings)) continue;
            double fade = Math.Clamp(span - l.RingSize, 0, Math.Round(l.RingGap * dpi, MidpointRounding.AwayFromZero) / dpi);
            double k = Math.Ceiling((allRings + fade - span) / step - 1e-6);
            carouselFits &= k >= 1 && k <= rings - 1 && allRings - k * step <= span - fade + 1e-9 && fade <= span - l.RingSize + 1e-9;
        }
        check("The rotating rings' fade and glide keep the first and last rings whole at rest", carouselFits);

        // The quota notice: the reset time gives way first, then the name, then the ring; the used text last.
        var full = CompactAlertLayout.Calculate(300, 1, 60, 70, 40);
        check("A notice with room shows every part at the default sizes", full.RingWidth == 22 && full.NameWidth == 68 && full.TextWidth == 70 && full.ResetWidth == 48
            && full.RingSize == 14 && full.FontSize == 11.5 && full.LineHeight == 16 && Math.Abs(full.RingThickness - 2.2) < 1e-9);
        var longName = CompactAlertLayout.Calculate(300, 1, 120, 70, 0);
        check("The name keeps its 76 DIP cap", longName.NameWidth == 84 && longName.ResetWidth == 0);
        var noReset = CompactAlertLayout.Calculate(150, 1, 60, 70, 40);
        check("The reset time gives way before the name", noReset.ResetWidth == 0 && noReset.NameWidth == 58 && noReset.RingWidth == 22 && noReset.TextWidth == 70);
        var textOnly = CompactAlertLayout.Calculate(60, 1, 60, 70, 40);
        check("The name, then the ring give way before the used text", textOnly.NameWidth == 0 && textOnly.RingWidth == 0 && textOnly.TextWidth == 60 && textOnly.Total == 60);
        var bigAlert = CompactAlertLayout.Calculate(1000, 2, 60, 70, 40);
        check("A taller notice scales its ring and type", bigAlert.RingSize == 28 && bigAlert.FontSize == 23 && bigAlert.LineHeight == 32 && bigAlert.Gap == 16 && bigAlert.NameWidth == 76);
        bool alertSane = true;
        foreach (var w in widths) foreach (var s in scales) foreach (var part in new[] { 0, 30, double.NaN, -4, 1e6 })
        {
            var a = CompactAlertLayout.Calculate(w, s, part, part, part);
            double row = double.IsFinite(w) && w > 0 ? w : 0;
            alertSane &= new[] { a.RingWidth, a.NameWidth, a.TextWidth, a.ResetWidth, a.RingSize, a.FontSize }.All(v => double.IsFinite(v) && v >= 0) && a.Total <= row + 1e-9;
        }
        check("Every notice width and scale gives finite, non-negative regions within the row", alertSane);
        var shortScroll = CompactAlertLayout.Scroll(24, 5);
        var longScroll = CompactAlertLayout.Scroll(500, 5);
        check("A notice longer than its row scrolls at the lyric's pace, or faster to finish with a rest at the end",
            shortScroll == (1, 2) && longScroll == (1, 4) && CompactAlertLayout.Scroll(double.NaN, 5) == (1, 1) && CompactAlertLayout.Scroll(50, 0) == (0, 0));

        // Every size in the row follows the height alone: across widths 0 to 800, for each style and height, the rings, cover,
        // equalizer, clock and type keep one size; only the regions change.
        var monitor = new DipRect(0, 0, 3840, 2160);
        bool fixedSizes = true, regionsMove = false;
        foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Capsule, CompactStyle.Line })
        foreach (var height in new[] { 24d, 28, 32, 40, 44, 50, 64 })
        {
            string? first = null, firstRaw = null;
            for (int w = 0; w <= 800; w++)
            {
                var settings = new DeckSettings { NotchWidth = w, NotchHeight = height, CapsuleWidth = w, CapsuleHeight = height, LineLength = w, LineThickness = height / 8 };
                var shape = CollapsedGeometry.Resolve(settings, style, monitor, monitor);
                // Through the shape (whose width the settings clamp) and straight from the raw width.
                foreach (var (l, raw) in new[] { (CompactMusicLayout.Calculate(shape.ContentWidth, shape.Scale, true, 4, true, 33 * shape.Scale), false),
                    (CompactMusicLayout.Calculate(w, shape.Scale, true, 4, true, 33 * shape.Scale), true) })
                {
                    string key = string.Join("|", l.RingSize, l.RingGap, l.RingThickness, l.RingEdge, l.CoverSize, l.CoverRadius, l.EqualizerSize, l.EqualizerHeight,
                        l.EqualizerBar, l.ClockFace, l.TextFont, l.TextHeight, l.TextEdge, l.RowHeight);
                    if (raw) { firstRaw ??= key; fixedSizes &= key == firstRaw; } else { first ??= key; fixedSizes &= key == first; }
                    fixedSizes &= l.RingSize == Math.Max(1, CompactMusicLayout.Whole(12 * l.Scale)) && l.RingGap == CompactMusicLayout.Whole(8 * l.Scale)
                        && l.CoverSize == Math.Max(1, CompactMusicLayout.Whole(20 * l.Scale)) && l.EqualizerSize == Math.Max(1, CompactMusicLayout.Whole(18 * l.Scale));
                    // A region narrower than one ring cuts it; the ring keeps its size.
                    if (l.VisibleRings == 0 && l.RingWidth > 0 && l.RingWidth < l.RingSize) regionsMove = true;
                }
            }
        }
        check("Ring, cover, equalizer, clock and type sizes depend on the height alone, for every width and style", fixedSizes);
        check("A short row cuts the ring region instead of shrinking the rings", regionsMove);
    }
}

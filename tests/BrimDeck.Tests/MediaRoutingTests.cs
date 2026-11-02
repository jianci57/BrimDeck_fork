using BrimDeck.Core;

internal static class MediaRoutingTests
{
    public static async Task Run(Action<string, bool> check)
    {
        int directCalls = 0, systemCalls = 0;
        async Task<bool> Route(MediaDispatchResult result) => await MediaRouting.DispatchAsync(
            () => { directCalls++; return Task.FromResult(result); },
            () => { systemCalls++; return Task.FromResult(true); });
        check("Dedicated control succeeds without sending a system command", await Route(MediaDispatchResult.Sent) && directCalls == 1 && systemCalls == 0);
        check("Unsent unsupported operation falls back exactly once", await Route(MediaDispatchResult.Unavailable) && directCalls == 2 && systemCalls == 1);
        check("Uncertain dispatch never repeats a next-track command", !await Route(MediaDispatchResult.Uncertain) && directCalls == 3 && systemCalls == 1);
        var primary = new MediaTrack { Id = "netease:1", Source = "网易云音乐", Title = "Song A", Artist = "Artist", PositionKnown = false,
            TimelinePending = true, State = MediaState.Unknown, CanPrevious = true };
        var controls = new MediaTrack { Title = "Song B", Artist = "Other", Album = "Wrong album", End = TimeSpan.FromMinutes(3),
            ReportedPosition = TimeSpan.FromSeconds(30), State = MediaState.Playing, CanToggle = true, CanNext = true, CanSeek = true,
            CanShuffle = true, CanRepeat = true, Shuffle = true, Repeat = MediaRepeat.Track };
        var merged = MediaRouting.Merge(primary, controls);
        check("A supplement adds only basic playback controls", merged.CanToggle && merged.CanNext && merged.CanPrevious && !merged.CanSeek && !merged.CanShuffle && !merged.CanRepeat);
        check("Supplement metadata and state cannot replace the primary", merged.Title == "Song A" && merged.Artist == "Artist" && merged.Album == "" && merged.State == MediaState.Unknown && merged.Id == primary.Id);
        check("Pending primary timeline never borrows system data", !merged.HasTimeline && merged.TimelinePending && merged.Duration == TimeSpan.Zero);
        check("Supplement modes cannot leak into the primary", !merged.Shuffle && merged.Repeat == MediaRepeat.None);
        check("A primary without a supplement is unchanged", MediaRouting.Merge(primary, null) == primary);
        check("FM disables previous even when SMTC advertises it", !MediaRouting.Merge(primary with { PreviousRestricted = true }, controls with { CanPrevious = true }).CanPrevious);

        var log = new MediaTrack { Id = "netease:7", Source = "网易云音乐", Title = "旧歌", Artist = "A / B", Album = "专辑", SongId = "netease:1", State = MediaState.Playing,
            End = TimeSpan.FromMinutes(3), ReportedPosition = TimeSpan.FromSeconds(100), PositionKnown = true, IsNeteaseLog = true };
        var smtc = new MediaTrack { Id = "system:x", Title = "新歌", Artist = "C", State = MediaState.Playing, CanToggle = true, CanNext = true, CanPrevious = true,
            PositionKnown = false, Shuffle = true, CanSeek = true, CanShuffle = true };
        var ahead = MediaRouting.PreferSystem(log, smtc);
        check("NetEase's session leads while the log is still on the previous song",
            ahead is { Title: "新歌", Artist: "C", Id: "netease:7", Source: "网易云音乐", SongId: "", IsNeteaseLog: true, State: MediaState.Playing } && !ahead.HasTimeline && ahead.TimelinePending);
        check("The leading session adds no seeking or modes in log mode", !ahead.CanSeek && !ahead.CanShuffle && !ahead.Shuffle && ahead.CanNext && ahead.CanToggle);
        var caught = MediaRouting.PreferSystem(log with { Title = "新歌", Artist = "C", SongId = "netease:2", ReportedPosition = TimeSpan.FromSeconds(1) }, smtc with { State = MediaState.Unknown });
        check("Once the log reaches the song it supplies id, album, timeline and a known state",
            caught is { SongId: "netease:2", Album: "专辑", State: MediaState.Playing } && caught.HasTimeline && caught.ReportedPosition == TimeSpan.FromSeconds(1));
        check("The session's state wins over the log's for the same song",
            MediaRouting.PreferSystem(log with { Title = "新歌", Artist = "C" }, smtc with { State = MediaState.Paused }).State == MediaState.Paused);
        check("A session without a title leaves the log in charge", MediaRouting.PreferSystem(log, smtc with { Title = " " }) is { Title: "旧歌", SongId: "netease:1" });
        check("The FM restriction from the log still disables previous", !MediaRouting.PreferSystem(log with { PreviousRestricted = true }, smtc).CanPrevious);
        check("Artist separators do not make a different song", MediaRouting.SameSong(log, log with { Artist = "A/B" }) && MediaRouting.SameSong(log, log with { Artist = "A、B" })
            && !MediaRouting.SameSong(log, log with { Artist = "A / C" }));
        check("A list that only adds artists is the same song", MediaRouting.SameSong(log with { Artist = "A" }, log) && MediaRouting.SameSong(log, log with { Artist = "B" })
            && !MediaRouting.SameSong(log with { Artist = "C" }, log));
        // Measured: NetEase's SMTC session names only the first artist ("\u5468\u6DF1") of a song its log lists as "\u5468\u6DF1 / <featured>".
        var logFull = log with { Title = "\u6625\u96EA", Artist = "\u5468\u6DF1 / Terry Zhong", SongId = "netease:2137074704", State = MediaState.Paused,
            End = TimeSpan.FromSeconds(208), ReportedPosition = TimeSpan.FromSeconds(6) };
        var smtcFirst = smtc with { Title = "\u6625\u96EA", Artist = "\u5468\u6DF1", State = MediaState.Paused };
        var spring = MediaRouting.PreferSystem(logFull, smtcFirst);
        check("The log's timeline is kept when the session names only the first artist",
            spring.HasTimeline && spring.Duration == TimeSpan.FromSeconds(208) && spring.ReportedPosition == TimeSpan.FromSeconds(6) && !spring.TimelinePending && spring.SongId == "netease:2137074704");
        check("The log names the matched song with its complete artist list", spring is { Title: "\u6625\u96EA", Artist: "\u5468\u6DF1 / Terry Zhong", State: MediaState.Paused });
        check("Width and spacing differences and a version suffix still match the log",
            MediaRouting.PreferSystem(logFull, smtcFirst with { Title = "\u6625\u96EA (Live)" }).HasTimeline
            && MediaRouting.PreferSystem(logFull with { Title = "Song\u3000A" }, smtcFirst with { Title = "\uFF33\uFF4F\uFF4E\uFF47 A" }).HasTimeline);
        check("A different song keeps the log's timeline out", !MediaRouting.PreferSystem(logFull, smtcFirst with { Title = "\u6625\u96EA\u4E4B\u540E" }).HasTimeline
            && !MediaRouting.PreferSystem(logFull, smtcFirst with { Artist = "\u5176\u4ED6" }).HasTimeline);

        // Measured: Apple Music reports "<artist> \u2014 <album>" as the artist with an empty album.
        const string apple = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
        check("Apple Music's artist field is split into artist and album",
            MediaRouting.SessionArtistAlbum(apple, "\u4F0D\u4F70 & China Blue \u2014 \u6CEA\u6865 (\"Rock Star 1\" Live) - Single", "")
                == ("\u4F0D\u4F70 & China Blue", "\u6CEA\u6865 (\"Rock Star 1\" Live) - Single"));
        check("Only the first dash ends Apple Music's artist",
            MediaRouting.SessionArtistAlbum(apple, "Artist \u2014 Album \u2014 Deluxe", "") == ("Artist", "Album \u2014 Deluxe"));
        check("Apple Music's artist without a dash or with an album is kept",
            MediaRouting.SessionArtistAlbum(apple, "Artist", "") == ("Artist", "")
            && MediaRouting.SessionArtistAlbum(apple, "Artist \u2014 Album", "Album") == ("Artist \u2014 Album", "Album"));
        check("Other players' artists are not split",
            MediaRouting.SessionArtistAlbum("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Artist \u2014 Album", "") == ("Artist \u2014 Album", ""));

        // Measured: Apple Music reported 2.000 at :57.677, again 2.000 at :58.219, then 3.000 at :58.433.
        var t0 = new DateTimeOffset(2026, 10, 25, 23, 40, 57, 677, TimeSpan.Zero);
        var known = new MediaTrack { Title = "Song", Artist = "Artist", State = MediaState.Playing, PositionKnown = true,
            End = TimeSpan.FromSeconds(245), ReportedPosition = TimeSpan.FromSeconds(2), PositionAt = t0 };
        MediaTrack Report(MediaTrack track, int seconds, DateTimeOffset at) => track with { ReportedPosition = TimeSpan.FromSeconds(seconds), PositionAt = at };
        var (first, span) = MediaRouting.FromWholeSeconds(null, known);
        check("A first whole-second report is taken from the middle of its second", first.Position(t0) == TimeSpan.FromSeconds(2.5));
        (_, span) = MediaRouting.FromWholeSeconds(span, Report(known, 2, t0.AddMilliseconds(542)));
        var (third, narrowed) = MediaRouting.FromWholeSeconds(span, Report(known, 3, t0.AddMilliseconds(756)));
        // The three reports place the start between :55.219 and :55.433.
        check("Whole-second reports narrow where the song started", narrowed!.Earliest == t0.AddMilliseconds(-2458) && narrowed.Latest == t0.AddMilliseconds(-2244)
            && Math.Abs((third.Position(t0.AddSeconds(1)) - TimeSpan.FromSeconds(3.351)).TotalMilliseconds) < 1);

        // Measured 2026-10-26: the reports of 122 to 130 seconds place the song at 124 between 11:53:32.352 and 11:53:32.629.
        var day = new DateTimeOffset(2026, 10, 26, 11, 53, 0, TimeSpan.Zero);
        (int Seconds, double At)[] reports = [(122, 31.301), (123, 31.844), (123, 32.125), (124, 32.665), (124, 33.212), (125, 33.737), (125, 34.272), (126, 34.811),
            (126, 35.352), (127, 35.629), (127, 36.169), (128, 36.712), (128, 37.240), (129, 37.786), (129, 38.324), (130, 38.870)];
        MediaRouting.WholeSecondStart? measured = null; MediaTrack estimate = known;
        foreach (var (seconds, at) in reports) (estimate, measured) = MediaRouting.FromWholeSeconds(measured, Report(known, seconds, day.AddSeconds(at)));
        var at124 = day.AddSeconds(32.352) + (day.AddSeconds(32.629) - day.AddSeconds(32.352)) / 2;
        check("Measured whole-second reports place the song within a seventh of a second",
            (measured!.Latest - measured.Earliest) <= TimeSpan.FromSeconds(0.28) && Math.Abs((estimate.Position(at124.AddSeconds(7)) - TimeSpan.FromSeconds(131)).TotalSeconds) < 0.15);
        check("A report outside the span is a seek and starts a new one",
            MediaRouting.FromWholeSeconds(measured, Report(known, 10, day.AddSeconds(39))).Start is { } seek && seek.Latest - seek.Earliest == TimeSpan.FromSeconds(1));
        check("A paused, different, untimed or fractional report is taken as reported",
            MediaRouting.FromWholeSeconds(measured, known with { State = MediaState.Paused }) is ({ } stopped, null) && stopped == known with { State = MediaState.Paused }
            && MediaRouting.FromWholeSeconds(measured, Report(known with { Title = "Other" }, 130, day.AddSeconds(39))).Start!.Latest - day.AddSeconds(39 - 130) == TimeSpan.Zero
            && MediaRouting.FromWholeSeconds(measured, known with { PositionKnown = false }).Start is null
            && MediaRouting.FromWholeSeconds(measured, known with { ReportedPosition = TimeSpan.FromSeconds(2.4) }).Start is null);
        // Measured on the Traditional Chinese interface: off and all share a glyph, one has its own.
        string loop = AppleMusicRepeat.RepeatGlyph, one = AppleMusicRepeat.RepeatOneGlyph;
        var traditional = new AppleMusicRepeat();
        check("Apple Music's repeat states are read from the measured names and glyphs",
            traditional.Observe(loop, "不要重複") == MediaRepeat.None && traditional.Observe(loop, "全部重複") == MediaRepeat.List
            && traditional.Observe(one, "重複單首歌曲") == MediaRepeat.Track && traditional.Observe("?", "x") is null);
        var english = new AppleMusicRepeat();
        bool unknownFirst = english.Observe(loop, "Repeat Off") is null;
        english.Observe(loop, "Repeat All"); english.Observe(one, "Repeat One");
        check("Apple Music's repeat names in another language are learnt from the cycle",
            unknownFirst && english.Observe(loop, "Repeat Off") == MediaRepeat.None && english.Observe(loop, "Repeat All") == MediaRepeat.List);
        var paused = known with { State = MediaState.Paused, ReportedPosition = TimeSpan.FromSeconds(1), PositionAt = t0.AddMilliseconds(80) };
        check("The pause Apple Music reports right after a seek is passed over",
            MediaRouting.SettlingAfterSeek(known, paused, t0, t0.AddMilliseconds(100)));
        check("A pause after the settling time, without a seek, or on another song is shown",
            !MediaRouting.SettlingAfterSeek(known, paused, t0, t0 + MediaRouting.SeekSettle) && !MediaRouting.SettlingAfterSeek(known, paused, null, t0)
            && !MediaRouting.SettlingAfterSeek(known, paused with { Title = "Other" }, t0, t0.AddMilliseconds(100))
            && !MediaRouting.SettlingAfterSeek(known with { State = MediaState.Paused }, paused, t0, t0.AddMilliseconds(100)));
    }
}

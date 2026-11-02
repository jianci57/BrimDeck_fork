using BrimDeck.Core;

internal static class MediaPlaybackStackTests
{
    public static void Run(Action<string, bool> check)
    {
        var t0 = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        static MediaTrack T(string id, MediaState state, string? source = null) => new() { Id = id, Source = source ?? id, State = state };
        const MediaState P = MediaState.Playing, S = MediaState.Paused;

        var stack = new MediaPlaybackStack();
        check("A single playing source is shown", stack.Update([T("A", P)], t0).Current == "A");
        var b = stack.Update([T("A", P), T("B", P)], t0.AddSeconds(10));
        check("A later source is shown above an earlier one", b.Current == "B" && b.Started == "B");
        var bPaused = stack.Update([T("A", P), T("B", S)], t0.AddSeconds(20));
        check("A source that just stopped is held briefly", bPaused.Current == "B" && bPaused.RecheckAt == t0.AddSeconds(20) + MediaPlaybackStack.Grace);
        check("After the hold the earlier playing source returns", stack.Update([T("A", P), T("B", S)], t0.AddSeconds(24)).Current == "A");
        var c = stack.Update([T("A", P), T("B", S), T("C", P)], t0.AddSeconds(30));
        check("A new source goes on top of the returned one", c.Current == "C" && c.Started == "C");
        stack.Update([T("A", S), T("B", S), T("C", P)], t0.AddSeconds(40));
        check("Stopping a lower source leaves the top unchanged", stack.Update([T("A", S), T("B", S), T("C", P)], t0.AddSeconds(50)).Current == "C");
        check("Resuming an older source moves it to the top", stack.Update([T("A", P), T("B", S), T("C", P)], t0.AddSeconds(60)).Current == "A");

        var gap = new MediaPlaybackStack();
        gap.Update([T("A", P)], t0); gap.Update([T("A", P), T("B", P)], t0.AddSeconds(1));
        gap.Update([T("A", MediaState.Unknown), T("B", P)], t0.AddSeconds(100));
        var songChange = gap.Update([T("A", P), T("B", P)], t0.AddSeconds(101));
        check("A song change in an older player does not move it above a newer one", songChange.Current == "B" && songChange.Started is null);
        gap.Update([T("A", P), T("B", MediaState.Unknown)], t0.AddSeconds(200));
        check("A song change in the top player does not show the older one", gap.Update([T("A", P), T("B", MediaState.Unknown)], t0.AddSeconds(201)).Current == "B");

        var closed = new MediaPlaybackStack();
        closed.Update([T("A", P)], t0); closed.Update([T("A", P), T("B", P)], t0.AddSeconds(1));
        check("Closing the top player shows the next playing one at once", closed.Update([T("A", P)], t0.AddSeconds(2)).Current == "A");

        var idle = new MediaPlaybackStack();
        idle.Update([T("A", P)], t0); idle.Update([T("A", P), T("B", P)], t0.AddSeconds(1));
        idle.Update([T("A", P), T("B", S)], t0.AddSeconds(2));
        idle.Update([T("A", S), T("B", S)], t0.AddSeconds(10));
        check("With nothing playing the last shown source stays", idle.Update([T("A", S), T("B", S)], t0.AddSeconds(20), previous: "A").Current == "A");
        check("A paused source that appears later does not take over", idle.Update([T("A", P), T("B", S), T("D", S)], t0.AddSeconds(30)).Current == "A");

        var startup = new MediaPlaybackStack();
        check("At startup the Windows current session is treated as most recent",
            startup.Update([T("A", P), T("B", P), T("C", P)], t0, systemCurrent: "B").Current == "B");

        var renamed = new MediaPlaybackStack();
        renamed.Update([T("qq:1", P, "QQ 音乐")], t0); renamed.Update([T("qq:1", P, "QQ 音乐"), T("B", P)], t0.AddSeconds(1));
        var fallback = renamed.Update([T("system:q", P, "QQ 音乐"), T("B", P)], t0.AddSeconds(2));
        check("A player whose route changes identity keeps its place", fallback.Current == "B" && fallback.Started is null);

        MediaTrack[] before = [T("qq:1", P, "QQ 音乐"), T("B", P)];
        check("A pin stays on a source that is still present", MediaPlaybackStack.FollowPin("qq:1", "QQ 音乐", before, before) == "qq:1");
        check("A pin follows the same player to its new route",
            MediaPlaybackStack.FollowPin("qq:1", "QQ 音乐", [T("system:q", S, "QQ 音乐"), T("B", P)], before) == "system:q");
        check("A pin is released when the pinned player closes", MediaPlaybackStack.FollowPin("qq:1", "QQ 音乐", [T("B", P)], before) is null);
        check("A pin never moves to a different player that was already open",
            MediaPlaybackStack.FollowPin("qq:1", "QQ 音乐", [T("B", P, "QQ 音乐")], before) is null);

        // A browser that replaces its session between songs keeps its route through MediaSessionHandover.
        var handover = new MediaSessionHandover();
        handover.Depart("system:a", "Chrome", t0);
        check("A new session of the same app continues the departed route", handover.Claim("chrome", t0.AddSeconds(0.5)) == "system:a" && !handover.Holds("system:a", t0.AddSeconds(0.5)));
        handover.Depart("system:a", "Chrome", t0);
        check("A session of another app does not take over a departed route", handover.Claim("cloudmusic.exe", t0.AddSeconds(0.5)) is null && handover.Holds("system:a", t0.AddSeconds(0.5)));
        check("The hold ends, so a player closed for good leaves",
            !handover.Holds("system:a", t0 + MediaSessionHandover.Hold) && handover.Claim("Chrome", t0 + MediaSessionHandover.Hold) is null && handover.NextExpiry is null);
        var skip = new MediaPlaybackStack();
        skip.Update([T("netease", S), T("chrome", P)], t0);
        check("A held browser route between songs keeps the panel from another player", skip.Update([T("netease", S), T("chrome", S)], t0.AddSeconds(1)).Current == "chrome");
        var continued = skip.Update([T("netease", S), T("chrome", P)], t0.AddSeconds(1.2));
        check("The successor session continues without counting as a new start", continued.Current == "chrome" && continued.Started is null);

        var gate = new TrackNoticeGate();
        static MediaTrack Song(MediaState state, bool log = false) => new() { Title = "Song", State = state, IsNeteaseLog = log };
        gate.Arm(t0);
        check("A song that starts at once shows its title", gate.Observe(Song(P), t0.AddMilliseconds(100)) && !gate.Armed);
        gate.Arm(t0);
        check("A NetEase log song keeps the notice armed until its state is known",
            !gate.Observe(Song(MediaState.Unknown, true), t0.AddMilliseconds(100)) && gate.Observe(Song(P, true), t0.AddSeconds(5.3)));
        gate.Arm(t0);
        check("A song started by hand long after the change does not show its title",
            !gate.Observe(Song(S), t0.AddSeconds(1)) && !gate.Observe(Song(P), t0.AddSeconds(6)) && !gate.Armed);
        gate.Arm(t0); gate.Observe(Song(MediaState.Stopped), t0.AddSeconds(0.1));
        check("A brief stop before the next song still shows it", gate.Observe(Song(P), t0.AddSeconds(1.5)));
        gate.Arm(t0); gate.Disarm();
        check("A disarmed notice never shows", !gate.Observe(Song(P), t0.AddSeconds(0.1)));

        // Chrome's one session briefly carries another tab's media while the playing tab changes songs.
        MediaTrack Clip(string title, MediaState state, double at, double length = 200) => new()
        { Id = "system:c", Title = title, Artist = title + " artist", State = state, PositionKnown = true, End = TimeSpan.FromSeconds(length), ReportedPosition = TimeSpan.FromSeconds(at), PositionAt = t0 };
        var swap = new MediaSwapGuard();
        var playing = Clip("Die With A Smile", P, 120);
        check("Another tab's paused video does not replace a playing song", swap.Holds(playing, Clip("Antigravity Dashboard", S, 9, 40), t0) && swap.RecheckAt == t0 + MediaSwapGuard.Hold);
        check("Another tab's video playing from its middle is held as well", swap.Holds(playing, Clip("Antigravity Dashboard", P, 9, 40), t0.AddSeconds(0.2)));
        check("An empty interim session is held", swap.Holds(playing, Clip("", MediaState.Unknown, 0, 0) with { PositionKnown = false }, t0.AddSeconds(0.3)));
        check("The next song starting from its beginning is taken at once", !swap.Holds(playing, Clip("Espresso", P, 0.2), t0.AddSeconds(0.4)) && swap.RecheckAt is null);
        check("Pausing the same song is not held", !new MediaSwapGuard().Holds(playing, playing with { State = S }, t0));
        check("A paused song gives way at once", !new MediaSwapGuard().Holds(playing with { State = S }, Clip("Antigravity Dashboard", S, 9, 40), t0));
        var real = new MediaSwapGuard();
        real.Holds(playing, Clip("Other tab", S, 30), t0);
        check("A lasting switch to another tab is shown once the hold ends",
            real.Holds(playing, Clip("Other tab", S, 30), t0.AddSeconds(1)) && !real.Holds(playing, Clip("Other tab", S, 30), t0 + MediaSwapGuard.Hold) && real.RecheckAt is null);

        // The music page keeps the progress bar's room while the next song's timeline is still to come.
        var timeline = new MusicTimelineHold();
        var next = Clip("Espresso", P, 0) with { PositionKnown = false };
        timeline.Observe(playing, t0); timeline.Observe(next, t0.AddSeconds(0.1));
        check("A song reported before its timeline keeps the bar's room", timeline.Holds(t0.AddSeconds(0.5)) && timeline.Until == t0.AddSeconds(0.1) + MusicTimelineHold.Hold);
        check("The room is given up once the hold ends", !timeline.Holds(t0.AddSeconds(0.1) + MusicTimelineHold.Hold));
        timeline.Observe(playing, t0); timeline.Observe(next with { TimelinePending = true }, t0.AddSeconds(0.1));
        check("A timeline the player reports as pending is waited for longer", timeline.Holds(t0.AddSeconds(5)));
        timeline.Observe(next, t0.AddSeconds(5));
        check("A timeline no longer pending is waited for no longer than the short hold", timeline.Until == t0.AddSeconds(5) + MusicTimelineHold.Hold);
        timeline.Observe(Clip("Espresso", P, 0.5), t0.AddSeconds(5.5));
        check("The timeline's arrival ends the hold", !timeline.Holds(t0.AddSeconds(5.5)) && timeline.Until is null);
        timeline.Observe(playing, t0); timeline.Observe(next with { Id = "system:other" }, t0.AddSeconds(0.1));
        check("Another player without a timeline is laid out at once", !timeline.Holds(t0.AddSeconds(0.1)));
        var never = new MusicTimelineHold();
        never.Observe(next, t0); never.Observe(next with { Title = "Other" }, t0.AddSeconds(1));
        check("A player that never had a timeline gets no room for one", !never.Holds(t0.AddSeconds(1)));
    }
}

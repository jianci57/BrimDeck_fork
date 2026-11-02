using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Native;

internal static class KugouConnectionTests
{
    internal static void Run(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        DesktopMediaSnapshot? Parse(string json) { using var doc = JsonDocument.Parse(json); return KugouCdpConnection.ParseSnapshot(doc.RootElement, now); }
        // A reply of 864 as KuGou 20.1.51 gives it, shortened.
        var playing = Parse("""{"filename":"张妙格、某人 - 把回忆拼好给你","play_status":"playing","progress":"196688005","duration":"181000","hash":"D62A437719ABA2B0DA0A5D2AD5E14E51","cover":"http://imge.kugou.com/stdmusic/120/20240809/20240809211902364136.jpg"}""");
        check("KuGou's play info gives the song, its artists and position in KuGou's units",
            playing is { Title: "把回忆拼好给你", Artist: "张妙格、某人", State: MediaState.Playing, CanToggle: true, CanPrevious: true, CanNext: true, CanSeek: false, TimelinePending: false }
            && playing.Position == TimeSpan.FromMilliseconds(19668.8005) && playing.Duration == TimeSpan.FromSeconds(181) && playing.PositionAt == now
            && playing.SongId == "kugou:d62a437719aba2b0da0a5d2ad5e14e51");
        check("KuGou's artwork is asked at 480 pixels, over HTTPS, from its own hosts only",
            playing?.CoverUrl == "https://imge.kugou.com/stdmusic/480/20240809/20240809211902364136.jpg"
            && KugouCdpConnection.Cover("https://example.com/stdmusic/120/a.jpg") is null && KugouCdpConnection.Cover("file:///c:/a.jpg") is null);
        check("KuGou's misspelt pause and its stop are understood",
            Parse("""{"filename":"A - B","play_status":"pasued","progress":"0","duration":"1000"}""")?.State == MediaState.Paused
            && Parse("""{"filename":"A - B","play_status":"stopped","progress":"0","duration":"1000"}""")?.State == MediaState.Stopped);
        check("Nothing loaded gives no song, and a position past the end is not used",
            Parse("""{"filename":"","play_status":"stopped"}""") is null
            && Parse("""{"filename":"A - B","play_status":"playing","progress":"99999999999","duration":"1000"}""") is { Position: null, TimelinePending: true }
            && Parse("""{"filename":"无歌手的标题","play_status":"playing","progress":"0","duration":"1000","hash":"zz"}""") is { Title: "无歌手的标题", Artist: "", SongId: null });

        check("The page call carries its fields and the callback",
            KugouCdpConnection.Call(940, new { songs = new[] { new { hash = "h" } } }).Contains("external.SuperCall(940, JSON.stringify(Object.assign({\"songs\":[{\"hash\":\"h\"}]}, { callback: name })))")
            && KugouCdpConnection.Call(864).Contains("external.SuperCall(864, JSON.stringify(Object.assign({}, { callback: name })))"));

        var later = playing! with { Position = playing.Position + TimeSpan.FromSeconds(1), PositionAt = now.AddSeconds(1) };
        check("A reading where the song should be by now is steady; a jump or a new state is not",
            KugouCdpConnection.Steady(playing, later) && !KugouCdpConnection.Steady(playing, later with { Position = TimeSpan.FromSeconds(60) })
            && !KugouCdpConnection.Steady(playing, later with { State = MediaState.Paused }) && !KugouCdpConnection.Steady(playing, later with { Title = "下一首" }));
    }
}

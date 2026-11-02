using BrimDeck.Core;

internal static class MediaAppTargetTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("Media session ids map to player process names",
            MediaAppTarget.ProcessName("cloudmusic.exe") == "cloudmusic" && MediaAppTarget.ProcessName("D:\\Tencent\\QQMusic\\QQMusic.exe") == "QQMusic"
            && MediaAppTarget.ProcessName("Chrome") == "chrome" && MediaAppTarget.ProcessName("MSEdge") == "msedge"
            && MediaAppTarget.ProcessName("foobar2000.exe") == "foobar2000"
            && MediaAppTarget.ProcessName("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App") == "AMPLibraryAgent");
        check("An application id of the application's own choosing names no process",
            MediaAppTarget.ProcessName("BroNekoX.QueMusic") == "" && MediaAppTarget.ProcessName("Vendor.Product.Player") == ""
            && MediaAppTarget.ProcessName("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") == "");

        MediaAppProcess P(int pid, string name, string appId = "", string? family = null) => new(pid, name, family, appId);
        var queMusic = new MediaAppOwner(null, MediaAppTarget.ProcessName("BroNekoX.QueMusic"), "BroNekoX.QueMusic");
        check("A player is found by the id Windows resolves for its process, not by a name cut from the id",
            MediaAppTarget.Plays(queMusic, P(1, "QueMusic", "BroNekoX.QueMusic"))
            && !MediaAppTarget.Plays(queMusic, P(2, "BroNekoX")) && !MediaAppTarget.Plays(queMusic, P(3, "QueMusic", "Probe.Other.Player")));
        var browser = new MediaAppOwner(null, "chrome", "Chrome");
        check("A browser's audio process is found by its id or its executable",
            MediaAppTarget.Plays(browser, P(4, "chrome", "Chrome")) && MediaAppTarget.Plays(browser, P(5, "chrome"))
            && !MediaAppTarget.Plays(browser, P(6, "msedge", "MSEdge")));
        var foobar = new MediaAppOwner(null, MediaAppTarget.ProcessName("foobar2000.exe"), "foobar2000.exe");
        check("A player reporting its executable is found by its executable, not by the path id Windows derives",
            MediaAppTarget.Plays(foobar, P(7, "foobar2000", @"{6D809377-6AF0-444B-8957-A3773F02200E}\foobar2000\foobar2000.exe"))
            && !MediaAppTarget.Plays(foobar, P(8, "foobar")));
        var spotifyStore = new MediaAppOwner(null, MediaAppTarget.ProcessName("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"), "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify");
        check("A packaged player is found by its package, whichever of its processes plays",
            MediaAppTarget.Plays(spotifyStore, P(9, "Spotify", family: "SpotifyAB.SpotifyMusic_zpdnekdrzrea0"))
            && !MediaAppTarget.Plays(spotifyStore, P(10, "SpotifyAB")));
        check("A known process plays, and so do the other processes of its executable",
            MediaAppTarget.Plays(new(42, "cloudmusic", "cloudmusic.exe"), P(42, "cloudmusic"))
            && MediaAppTarget.Plays(new(42, "cloudmusic", "cloudmusic.exe"), P(43, "cloudmusic"))
            && !MediaAppTarget.Plays(new(42, "cloudmusic", "cloudmusic.exe"), P(44, "chrome")));

        string? Listed(string id) => id == "BroNekoX.QueMusic" ? "QueMusic" : null;
        check("A player is named by its well-known name, the name Windows lists it under, or its executable",
            MediaAppTarget.DisplayName("cloudmusic.exe", Listed) == "网易云音乐" && MediaAppTarget.DisplayName("Chrome", Listed) == "Google Chrome"
            && MediaAppTarget.DisplayName("BroNekoX.QueMusic", Listed) == "QueMusic" && MediaAppTarget.DisplayName("foobar2000.exe", Listed) == "foobar2000"
            && MediaAppTarget.DisplayName("Vendor.Product.Player", Listed) == "Vendor.Product.Player");
        check("A packaged application id names its package family",
            new MediaAppOwner(null, "x", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify").PackageFamily == "SpotifyAB.SpotifyMusic_zpdnekdrzrea0"
            && new MediaAppOwner(null, "chrome", "Chrome").PackageFamily is null);

        MediaAppWindow W(int handle, int pid, string name, string title = "Window", bool visible = true, bool minimized = false, bool tool = false,
            bool owned = false, bool cloaked = false, string appId = "", string? family = null) =>
            new(handle, pid, name, family, appId, visible, minimized, tool, owned, cloaked, title);
        var chrome = new MediaAppOwner(null, "chrome", "Chrome");
        check("The topmost ordinary window of the player is chosen",
            MediaAppTarget.Choose(chrome, [W(1, 9, "notepad"), W(2, 5, "chrome"), W(3, 6, "chrome")])?.Handle == 2);
        check("Hidden, untitled, tool and owned windows are skipped",
            MediaAppTarget.Choose(chrome, [W(1, 5, "chrome", visible: false), W(2, 5, "chrome", title: ""), W(3, 5, "chrome", tool: true), W(4, 5, "chrome", owned: true), W(5, 5, "chrome")])?.Handle == 5);
        check("A minimized window is still a target", MediaAppTarget.Choose(chrome, [W(7, 5, "chrome", minimized: true)])?.Handle == 7);
        check("A window cloaked by the shell is chosen only without another",
            MediaAppTarget.Choose(chrome, [W(1, 5, "chrome", cloaked: true), W(2, 5, "chrome")])?.Handle == 2
            && MediaAppTarget.Choose(chrome, [W(1, 5, "chrome", cloaked: true)])?.Handle == 1);
        check("A player with only a tray icon has no target",
            MediaAppTarget.Choose(new(null, "cloudmusic", "cloudmusic.exe"), [W(1, 4, "cloudmusic", visible: false), W(2, 9, "chrome")]) is null);
        check("A known process id wins over the process name",
            MediaAppTarget.Choose(new(42, "cloudmusic", "cloudmusic.exe"), [W(1, 41, "cloudmusic"), W(2, 42, "cloudmusic")])?.Handle == 2);
        var spotify = new MediaAppOwner(null, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify");
        check("A packaged application is found by its window's id or its package family",
            MediaAppTarget.Choose(spotify, [W(1, 3, "ApplicationFrameHost", appId: "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")])?.Handle == 1
            && MediaAppTarget.Choose(spotify, [W(2, 8, "Spotify", family: "SpotifyAB.SpotifyMusic_zpdnekdrzrea0")])?.Handle == 2
            && MediaAppTarget.Choose(spotify, [W(3, 8, "Spotify")]) is null);
        check("A player whose process carries its id is found by that id",
            MediaAppTarget.Choose(queMusic, [W(1, 3, "BroNekoX"), W(2, 4, "QueMusic", appId: "BroNekoX.QueMusic")])?.Handle == 2);
    }
}

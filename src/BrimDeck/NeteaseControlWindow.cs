using BrimDeck.Core;

namespace BrimDeck;

// What confirming does: restart a player in normal mode, start one that is not running,
// restart one that already has full control so the connection is made again, or switch on
// the setting that restarts every player started the normal way.
internal enum NeteaseControlAction { Restart, Start, Reconnect, Auto }

// Asks before NetEase Cloud Music is restarted with its debugging port (ControlDialog).
internal sealed class NeteaseControlWindow : ControlDialog
{
    internal static string SecurityNotice => Loc.T("完整控制依赖网易云的调试端口。端口打开期间，这台电脑上的其他程序和浏览器网页有可能借此操控网易云，读取它显示的内容和登录状态。端口会一直开到网易云以普通方式重新启动为止，退出 BrimDeck 或连接失败都不会关闭它。",
        "Full control relies on the debugging port of NetEase Cloud Music. While the port is open, other programs and web pages on this PC could use it to control NetEase Cloud Music and read what it shows and its sign-in state. The port stays open until NetEase Cloud Music is started again the normal way; quitting BrimDeck or a failed connection does not close it.");

    public NeteaseControlWindow(SettingsTheme theme, NeteaseControlAction action = NeteaseControlAction.Restart)
        : base(theme, action == NeteaseControlAction.Auto ? Loc.T("自动启用网易云完整控制", "Enable full control of NetEase Cloud Music automatically") : Loc.T("启用网易云完整控制", "Enable full control of NetEase Cloud Music"))
    {
        bool auto = action == NeteaseControlAction.Auto, start = action == NeteaseControlAction.Start;
        Add(action switch
        {
            NeteaseControlAction.Start => Loc.T("启用完整控制后，可以在面板里拖动网易云音乐的进度、切换随机与循环播放，进度也会实时同步。",
                "With full control, you can drag the playback position of NetEase Cloud Music in the panel and switch shuffle and repeat, and the position stays in sync."),
            NeteaseControlAction.Auto => Loc.T("打开后，每当网易云音乐以普通方式启动，BrimDeck 会自动将它重启为完整控制。完整控制下，可以在面板里拖动进度、切换随机与循环播放，进度也会实时同步。",
                "Once this is on, whenever NetEase Cloud Music is started the normal way, BrimDeck restarts it with full control. With full control, you can drag the playback position in the panel and switch shuffle and repeat, and the position stays in sync."),
            NeteaseControlAction.Reconnect => Loc.T("网易云音乐已处于完整控制。继续操作会重新启动网易云音乐，并重新建立连接。",
                "NetEase Cloud Music already has full control. Continuing restarts NetEase Cloud Music and connects to it again."),
            _ => Loc.T("BrimDeck 现在只能读取网易云正在播放的内容。启用完整控制后，可以在面板里拖动进度、切换随机与循环播放，进度也会实时同步。",
                "BrimDeck can now only read what NetEase Cloud Music is playing. With full control, you can drag the playback position in the panel and switch shuffle and repeat, and the position stays in sync.")
        }, 10);

        Add(auto ? Loc.T("网易云每次启动后会重启一次", "NetEase Cloud Music restarts once each time it starts")
            : start ? Loc.T("会启动网易云音乐", "NetEase Cloud Music will start") : Loc.T("会重启一次网易云", "NetEase Cloud Music will restart once"), 22, 13.5, Palette.Primary, true);
        Add(auto ? Loc.T("网易云的窗口会关闭再打开，并回到原来的歌曲和播放位置，通常需要几秒钟。网易云如果此刻正以普通方式运行，点击“打开”后会立即重启一次。",
                "Its window closes and opens again and returns to the same song and position, which usually takes a few seconds. If NetEase Cloud Music is running the normal way now, it restarts once as soon as you click \"Turn on\".")
            : start ? Loc.T("点击“启动并启用”后，网易云音乐会以完整控制方式打开。整个过程通常需要几秒钟。",
                "After you click \"Start and enable\", NetEase Cloud Music opens with full control. This usually takes a few seconds.")
            : Loc.T("点击“重启并启用”后，网易云音乐会先关闭再重新打开，然后自动回到当前这首歌和播放位置；如果此刻正在播放，会接着播放。整个过程通常需要几秒钟。",
                "After you click \"Restart and enable\", NetEase Cloud Music closes and opens again, then returns to the current song and position; if it is playing now, it keeps playing. This usually takes a few seconds."), 6);

        var notice = AddNotice(Loc.T("只在您信任的电脑上启用", "Enable it only on a PC you trust"));
        Add(auto ? SecurityNotice + Loc.T("打开此开关后，网易云每次启动都会打开调试端口，直到关闭此开关。",
            " With this setting on, the port is opened each time NetEase Cloud Music starts, until the setting is turned off.") : SecurityNotice, 6, into: notice);

        Finish(auto ? Loc.T("打开", "Turn on") : start ? Loc.T("启动并启用", "Start and enable") : Loc.T("重启并启用", "Restart and enable"));
    }
}

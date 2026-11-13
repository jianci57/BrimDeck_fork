using BrimDeck.Core;

namespace BrimDeck;

// Asks before NetEase Cloud Music's libcef.dll is changed (NeteaseInstall): what it adds, what happens to the player, and the
// debugging port, in a few lines; the details are in the user documentation (ControlDialog).
internal sealed class NeteaseFileControlWindow : ControlDialog
{
    public NeteaseFileControlWindow(SettingsTheme theme) : base(theme, Loc.T("启用网易云完整控制", "Turn on NetEase Cloud Music full control"))
    {
        Add(Loc.T("启用后，可以在面板里拖动网易云的播放进度、切换随机与循环播放，进度也会实时同步。",
            "When on, you can drag NetEase Cloud Music's playback position in the panel and switch shuffle and repeat, and the position stays in sync."), 10);
        Add(Loc.T("•  网易云正在运行时会重启一次，然后回到当前这首歌和播放位置", "•  A running player restarts once, then returns to the current song and position"), 10, color: Palette.Primary);
        Add(Loc.T("•  之后网易云每次启动都是完整控制，不必再逐次启用", "•  NetEase Cloud Music then starts with full control every time, with nothing to enable again"), 10, color: Palette.Primary);
        AddFileChange(Loc.T("•  会修改网易云的 libcef.dll", "•  NetEase Cloud Music's libcef.dll is changed"),
            Loc.T("只替换文件中 2 处原有的字节（共约 12 字节），让网易云启动时打开调试端口。不添加内容，文件大小不变；停用时写回原来的字节。网易云更新后若还原了文件，会自动再次修改。",
                "Only 2 existing places in the file are replaced (about 12 bytes in all), so that NetEase Cloud Music opens its debugging port when it starts. Nothing is added and the file keeps its size; turning it off writes the original bytes back. If an update restores the file, it is changed again."));

        var notice = AddNotice(Loc.T("只在您信任的电脑上启用", "Turn it on only on a PC you trust"));
        Add(NeteaseControlWindow.SecurityNotice + Loc.T("因为文件已修改，网易云每次启动都会打开调试端口，直到关闭此开关。",
            " Because the file is changed, NetEase Cloud Music opens the port every time it starts, until this switch is turned off."), 6, into: notice);

        Finish(Loc.T("启用", "Turn on"));
    }
}

using BrimDeck.Core;

namespace BrimDeck;

// Asks before KuGou's libcef.dll is changed (KugouInstall): what it adds, what happens to KuGou, and the debugging port, in a
// few lines; the details are in the user documentation (ControlDialog).
internal sealed class KugouControlWindow : ControlDialog
{
    public KugouControlWindow(SettingsTheme theme) : base(theme, Loc.T("启用酷狗完整控制", "Turn on KuGou full control"))
    {
        Add(Loc.T("启用后，面板显示酷狗的播放进度，歌词随进度滚动。", "When on, the panel shows KuGou's position and scrolls the lyrics with it."), 10);
        Add(Loc.T("•  酷狗正在运行时会重启一次，正在播放的歌曲会停止", "•  A running KuGou restarts once, and the playing song stops"), 10, color: Palette.Primary);
        AddFileChange(Loc.T("•  会修改酷狗的 libcef.dll，之后酷狗每次启动都是完整控制", "•  KuGou's libcef.dll is changed; KuGou then starts with full control every time"),
            Loc.T("只替换文件中 9 处原有的字节（共约 50 字节），让酷狗启动时打开调试端口。不添加内容，文件大小不变；停用时写回原来的字节。",
                "Only 9 existing places in the file are replaced (about 50 bytes in all), so that KuGou opens its debugging port when it starts. Nothing is added and the file keeps its size; turning it off writes the original bytes back."));

        var notice = AddNotice(Loc.T("只在您信任的电脑上启用", "Turn it on only on a PC you trust"));
        Add(Loc.T("酷狗会打开一个本机调试端口，其他程序可能借此操控酷狗。", "KuGou opens a local debugging port that other programs could use to control it."), 6, into: notice);

        Finish(Loc.T("启用", "Turn on"));
    }
}

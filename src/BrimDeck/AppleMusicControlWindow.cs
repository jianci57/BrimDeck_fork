using BrimDeck.Core;

namespace BrimDeck;

// Explains Apple Music's full control before it is enabled: what it adds, how it works, and what it does to the
// Apple Music window (ControlDialog).
internal sealed class AppleMusicControlWindow : ControlDialog
{
    public AppleMusicControlWindow(SettingsTheme theme) : base(theme, Loc.T("启用 Apple Music 完整控制", "Enable full control of Apple Music"))
    {
        Add(Loc.T("Apple Music 只向 Windows 提供播放、暂停和切换歌曲的控制。启用完整控制后，BrimDeck 会通过 Windows 的界面自动化功能，直接操作 Apple Music 窗口中的按钮和进度条，在面板中增加以下三项功能：",
            "Apple Music only lets Windows play, pause and change songs. With full control, BrimDeck uses the UI Automation feature of Windows to operate the buttons and the progress bar in the Apple Music window directly, and adds three functions to the panel:"), 10);
        Add(Loc.T("•  拖动播放进度\n•  开启或关闭随机播放\n•  切换重复模式：关闭、全部重复、单曲重复",
            "•  Drag the playback position\n•  Turn shuffle on or off\n•  Switch the repeat mode: off, repeat all, repeat one"), 8, color: Palette.Primary);

        Add(Loc.T("使用前请了解", "Before you enable it"), 22, 13.5, Palette.Primary, true);
        Add(Loc.T("•  如果 Apple Music 的主窗口没有最小化，使用这三项功能时，它的窗口会被带到最前面。\n•  这三项功能需要 Apple Music 的主窗口存在。关闭主窗口会退出 Apple Music，功能也随之不可用。\n•  Apple Music 更新后，如果窗口中的按钮发生变化，这三项功能可能暂时无法使用。",
            "•  If the Apple Music main window is not minimized, using these functions brings it to the front.\n•  These functions need the Apple Music main window. Closing it quits Apple Music, and the functions stop working.\n•  If an Apple Music update changes the buttons in its window, these functions may stop working for a while."), 6);

        Finish(Loc.T("启用", "Enable"));
    }
}

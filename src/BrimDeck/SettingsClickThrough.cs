using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

// Mouse pass-through (DeckSettings.ClickThrough) in the General page's Compatibility group: its switch, and under it, while it is on, whether the open
// panel takes clicks (ClickThroughPanel) and the keyboard (ClickThroughKeyboard). Its shortcut and the tray menu also turn it on and off; the switch follows them.
public sealed partial class SettingsWindow
{
    private CheckBox? _clickThroughBox;

    private FrameworkElement ClickThroughRows()
    {
        var clickable = Toggle(Loc.T("展开后的面板可以点击", "Expanded panel takes clicks"), S.ClickThroughPanel, value => Change(s => s.ClickThroughPanel = value),
            help: Loc.T("开启后，展开的面板可以正常点击，鼠标进入再离开后收起。关闭时，面板不接收鼠标，一直保持展开，直到再按展开快捷键收起。",
                "When on, the expanded panel takes clicks as usual and collapses once the pointer has entered and left it. When off, it takes no mouse input and stays open until the expand shortcut collapses it."));
        var keyboard = Toggle(Loc.T("面板接收键盘", "Panel takes keyboard input"), S.ClickThroughKeyboard, value => Change(s => s.ClickThroughKeyboard = value),
            help: Loc.T("辅助操作，与 Windows 中的键盘操作相同。开启后，用快捷键展开的面板可以用 Tab、方向键、空格、回车操作，按 Esc 收起，这期间游戏收不到这些按键。",
                "An assistive option that works like Windows keyboard navigation. When on, a panel expanded by a shortcut takes Tab, the arrow keys, Space and Enter, and Esc collapses it. Your game does not get these keys meanwhile."));
        var more = new StackPanel { Visibility = S.ClickThrough ? Visibility.Visible : Visibility.Collapsed };
        more.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
        more.Children.Add(clickable);
        more.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
        more.Children.Add(keyboard);
        var main = Toggle(Loc.T("鼠标穿透模式", "Mouse pass-through"), S.ClickThrough, value =>
        {
            if (S.ClickThrough != value) Change(s => s.ClickThrough = value);
            more.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }, help: Loc.T("开启后，鼠标经过不会展开面板，点击会直接落到下方窗口，适合玩游戏时使用。快捷键在“设置 › 快捷键”中设置。",
            "When on, the pointer passing over does not expand the panel and clicks go to the window beneath, which suits games. Set shortcuts in Settings › Shortcuts."));
        _clickThroughBox = ((Panel)main).Children.OfType<CheckBox>().FirstOrDefault();
        var group = new StackPanel();
        group.Children.Add(main); group.Children.Add(more);
        return group;
    }

    // The shortcut or the tray menu turned pass-through on or off.
    internal void ClickThroughChanged()
    {
        if (_clickThroughBox is { IsLoaded: true } box && box.IsChecked != S.ClickThrough) box.IsChecked = S.ClickThrough;
    }
}

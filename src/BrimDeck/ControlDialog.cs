using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The window a player's full control asks in before it is switched on (Apple Music, KuGou, and NetEase Cloud Music by a restart
// or by its changed file): a title, what the control adds and does to the player in a few lines, a notice about the debugging
// port where one is opened, and Cancel beside the button that confirms. In the settings window's voice: plain sentences,
// neutral surfaces, the accent only on the primary action. DialogResult is true once confirmed.
internal abstract class ControlDialog : Window
{
    protected readonly SettingsPalette Palette;
    protected readonly StackPanel Body = new();

    protected ControlDialog(SettingsTheme theme, string title)
    {
        Palette = new SettingsPalette(SettingsPalette.IsDark(theme));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck;component/SettingsResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck;component/NeteaseControlResources.xaml", UriKind.Relative) });
        Palette.Apply(Resources); Style = (Style)FindResource(typeof(Window));
        Title = title; Width = 480; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        FontFamily = UI.PanelFont; Background = UI.Brush(Palette.Surface); Foreground = UI.Brush(Palette.Primary);
        Native.SettingsChrome.Apply(this); ResizeMode = ResizeMode.NoResize;
        SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(this, Palette.Dark);
        KeyboardNavigation.SetTabNavigation(Body, KeyboardNavigationMode.Cycle);
        Body.Children.Add(Text(Title, 20, Palette.Primary, true, 28));
    }

    protected static TextBlock Text(string value, double size, string color, bool bold = false, double lineHeight = 20)
    {
        var text = UI.Line(value, size, color); text.TextWrapping = TextWrapping.Wrap; text.LineHeight = lineHeight;
        text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        if (bold) text.FontWeight = FontWeights.SemiBold;
        return text;
    }

    // A paragraph of the body, or of the notice, top below what stands above it.
    protected TextBlock Add(string value, double top, double size = 13, string? color = null, bool bold = false, Panel? into = null)
    {
        var text = Text(value, size, color ?? Palette.Secondary, bold, bold ? 20 : 21); text.Margin = new Thickness(0, top, 0, 0);
        (into ?? Body).Children.Add(text); return text;
    }

    // The line that says the player's file is changed, with how it is changed behind an info icon drawn like the settings window's.
    protected void AddFileChange(string line, string how)
    {
        var label = Text(line, 13, Palette.Primary, lineHeight: 21);
        var info = new Border { Width = 22, Height = 21, Margin = new Thickness(3, 0, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Help, Focusable = true,
            Child = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 7,1 A 6,6 0 1 1 6.99,1 M 7,6.5 V 10 M 7,4 V 4.1"), Width = 14, Height = 14,
                Stroke = UI.Brush(Palette.Tertiary), StrokeThickness = 1.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
        info.ToolTip = new ToolTip { Content = new TextBlock { Text = how, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 } };
        ToolTipService.SetInitialShowDelay(info, 0);
        AutomationProperties.SetName(info, Loc.T("修改方式说明", "How the file is changed")); AutomationProperties.SetHelpText(info, how);
        Body.Children.Add(new HelpHeading(label, info) { HorizontalAlignment = HorizontalAlignment.Left });
    }

    // The notice about the debugging port: a shield and a heading on an inset surface. Its text is added into the panel returned.
    protected StackPanel AddNotice(string heading)
    {
        var notice = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new System.Windows.Shapes.Path { Width = 15, Height = 15, Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M8,1.5 L13.5,3.6 V7.4 C13.5,10.7 11.2,13.3 8,14.5 C4.8,13.3 2.5,10.7 2.5,7.4 V3.6 Z M5.6,8 L7.4,9.8 L10.6,6.4"),
            Stroke = UI.Brush(Palette.Secondary), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(Text(heading, 13.5, Palette.Primary, true)); notice.Children.Add(head);
        Body.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 13), Margin = new Thickness(0, 20, 0, 0),
            Background = UI.Brush(Palette.Inset), BorderBrush = UI.Brush(Palette.RowDivider), BorderThickness = new Thickness(1), Child = notice });
        return notice;
    }

    // The buttons, which end the window's content. Cancelling stays the safe initial keyboard action, without drawing the focus
    // indicator that is reserved for Tab navigation.
    protected void Finish(string confirm)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = Loc.T("取消", "Cancel"), Style = (Style)FindResource("ControlDialogButton"), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var enable = new Button { Content = confirm, Style = (Style)FindResource("ControlDialogPrimaryButton") };
        AutomationProperties.SetName(cancel, Loc.T("取消", "Cancel")); AutomationProperties.SetName(enable, confirm);
        cancel.Click += (_, _) => DialogResult = false; enable.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(enable); Body.Children.Add(buttons);
        Content = new Border { Background = UI.Brush(Palette.Surface), Padding = new Thickness(28, 24, 28, 24), Child = Body };
        Loaded += (_, _) => cancel.Focus();
    }
}

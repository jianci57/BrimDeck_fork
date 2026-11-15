using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// A diagram for the arrangement cards in the settings ("页面布局"): not a picture of the panel, only how its two columns are laid
// out, drawn large enough to read at a glance in the settings' own colors, and standing still. Side by side, the clipboard and the
// file transfer are two blocks with the divider between them, always split at the default share: the diagram says what the
// arrangement is, not how it is set. Stacked, the clipboard is a card
// in front and the file transfer the side strip behind it at the right, a little lower, with its icon and its name written downward.
internal static class ShelfArrangementPreview
{
    // Wider than the collapsed style's diagrams (240 by 136): two cards fill the row that three of those do, at the same height.
    private const double Width = 372, Height = 136, Left = 14, Right = 358, Top = 16, Bottom = 120;
    // Stacked: what shows of the strip beside the card, what lies under the card, and how much shorter it is at the top and bottom.
    private const double Peek = 40, Under = 12, Inset = 4;
    private const string ClipboardIcon = "M 8.5,4.5 H 6.5 A 2,2 0 0 0 4.5,6.5 V 19.5 A 2,2 0 0 0 6.5,21.5 H 17.5 A 2,2 0 0 0 19.5,19.5 V 6.5 A 2,2 0 0 0 17.5,4.5 H 15.5 M 9.5,2.5 H 14.5 A 1,1 0 0 1 15.5,3.5 V 5.5 A 1,1 0 0 1 14.5,6.5 H 9.5 A 1,1 0 0 1 8.5,5.5 V 3.5 A 1,1 0 0 1 9.5,2.5 Z M 8.5,11.5 H 15.5 M 8.5,15.5 H 13";
    private const string TrayIcon = "M 3,13 L 6,5 H 18 L 21,13 V 18 A 1.5,1.5 0 0 1 19.5,19.5 H 4.5 A 1.5,1.5 0 0 1 3,18 Z M 3,13 H 8.5 L 10,15 H 14 L 15.5,13 H 21";

    // Ground: the diagram's background. Plain: a column side by side. Card and Strip: the card in front and the strip behind it, each
    // with its edge. Line: what stands for an entry. Rule: the divider.
    private sealed record Tones(string Ground, string Plain, string Card, string CardEdge, string Strip, string StripEdge, string Line, string Rule, string Primary, string Secondary, string Clip, string File)
    {
        public static Tones For(SettingsPalette palette) => palette.Dark
            ? new("#151517", "#222226", "#2C2C31", "#46464D", "#202024", "#35353B", "#45454D", "#5A5A63", palette.Primary, palette.Secondary, "#7FB2FF", "#E8BE6A")
            : new("#ECEDF2", "#E0E2E9", "#FFFFFF", "#D3D6DE", "#DEE0E7", "#CDD0D9", "#CFD2DA", "#B9BCC6", palette.Primary, palette.Secondary, "#2F74DA", "#B9820F");
    }

    public static FrameworkElement Create(ShelfArrangement arrangement, SettingsPalette palette)
    {
        var tones = Tones.For(palette);
        var scene = new Grid
        {
            Width = Width, Height = Height, Background = UI.Brush(tones.Ground), IsHitTestVisible = false,
            Clip = new RectangleGeometry(new Rect(0, 0, Width, Height), 9, 9), UseLayoutRounding = false, SnapsToDevicePixels = false
        };
        // Drawn small and enlarged: ideal text layout keeps the names sharp (CompactStylePreview).
        TextOptions.SetTextFormattingMode(scene, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(scene, TextRenderingMode.Grayscale);
        var canvas = new Canvas { Width = Width, Height = Height };
        scene.Children.Add(canvas);
        var box = new Viewbox { Child = scene, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
        double tall = Bottom - Top;
        if (arrangement == ShelfArrangement.Split)
        {
            const double gap = 16;
            double room = Right - Left - gap, files = Math.Round(room * ShelfLayout.DefaultShare / 100), clips = room - files;
            Place(canvas, Block(true, clips, tall, tones.Plain, null, tones), Left, Top);
            Place(canvas, Block(false, files, tall, tones.Plain, null, tones), Left + clips + gap, Top);
            Place(canvas, new Rectangle { Width = 1, Height = tall - 8, Fill = UI.Brush(tones.Rule) }, Left + clips + gap / 2 - .5, Top + 4);
            return box;
        }
        double front = Right - Left - Peek;
        // The strip, then the card over its inner edge.
        Place(canvas, StripBlock(Peek + Under, tall - 2 * Inset, tones), Right - Peek - Under, Top + Inset);
        Place(canvas, Block(true, front, tall, tones.Card, tones.CardEdge, tones), Left, Top);
        return box;
    }

    // A column: its icon in its own color and its name at the top, three lines standing for its entries below. In English the file
    // transfer is "Files" here: the full name would not fit a narrow block. A block too narrow for its name shows the icon alone.
    private static Border Block(bool clips, double width, double height, string fill, string? edge, Tones tones)
    {
        string name = clips ? Loc.T("剪贴板", "Clipboard") : Loc.T("文件中转", "Files");
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Icon(clips, clips ? tones.Clip : tones.File, 13));
        var title = new TextBlock { Text = name, FontFamily = UI.PanelFont, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(tones.Primary), TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (13 + title.DesiredSize.Width <= width - 16) head.Children.Add(title);
        stack.Children.Add(head);
        foreach (var length in clips ? new[] { .78, .56, .68 } : [.64, .8, .5])
            stack.Children.Add(new Rectangle { Width = Math.Max(8, (width - 16) * length), Height = 4, RadiusX = 2, RadiusY = 2, Fill = UI.Brush(tones.Line), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 11, 0, 0) });
        return new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(6), Background = UI.Brush(fill), ClipToBounds = true,
            BorderBrush = edge is null ? null : UI.Brush(edge), BorderThickness = new Thickness(edge is null ? 0 : 1), Padding = new Thickness(8, 9, 8, 0), Child = stack
        };
    }

    // The file transfer's side strip: its icon and, under it, its name written downward (Chinese upright, English turned a quarter
    // clockwise), in the middle of the part that shows beside the card.
    private static Border StripBlock(double width, double height, Tones tones)
    {
        string name = Loc.T("文件中转", "Files");
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(Icon(false, tones.File, 13));
        if (name.Any(letter => letter >= 0x2E80))
            foreach (char letter in name)
                stack.Children.Add(new TextBlock { Text = letter.ToString(), FontFamily = UI.PanelFont, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(tones.Secondary),
                    TextAlignment = TextAlignment.Center, Height = 12, LineHeight = 12, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new Thickness(0, stack.Children.Count == 1 ? 6 : 0, 0, 0) });
        else
            stack.Children.Add(new TextBlock { Text = name, FontFamily = UI.PanelFont, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(tones.Secondary),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), LayoutTransform = new RotateTransform(90) });
        return new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(6), Background = UI.Brush(tones.Strip), BorderBrush = UI.Brush(tones.StripEdge), BorderThickness = new Thickness(1),
            Padding = new Thickness(Under, 8, 0, 0), Child = stack
        };
    }

    // An icon of a 24 unit grid in a fixed box, so it is centered by its box.
    private static Viewbox Icon(bool clips, string color, double size) => new()
    {
        Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Child = new Canvas
        {
            Width = 24, Height = 24,
            Children = { new Path { Data = Geometry.Parse(clips ? ClipboardIcon : TrayIcon), Stroke = UI.Brush(color), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round } }
        }
    };

    private static void Place(Canvas canvas, UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left); Canvas.SetTop(element, top);
        canvas.Children.Add(element);
    }
}

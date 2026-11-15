using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The diagrams on the cards that choose how each column of the clipboard and file transfer page shows its entries ("页面布局":
// ClipView, FileView). As the arrangement's diagrams (ShelfArrangementPreview) they are not pictures of the panel: bars stand for
// text and squares for icons, in the settings' own colors, and they stand still. The clipboard's three fill a third of the row
// each and the file transfer's five a fifth, so the two kinds are drawn on fields of different widths and one height.
internal static class ShelfViewPreview
{
    private const double Height = 84, ClipWidth = 232, FileWidth = 136;

    // Ground: the diagram's background. Line: text. Faint: the facts beside or under it. Rule: the line between entries.
    // File: a file's icon. Block: a block of the clipboard.
    private sealed record Tones(string Ground, string Line, string Faint, string Rule, string File, string Block)
    {
        public static Tones For(SettingsPalette palette) => palette.Dark
            ? new("#151517", "#5E5E67", "#3A3A41", "#2C2C31", "#E8BE6A", "#26303F")
            : new("#ECEDF2", "#B4B8C2", "#D0D3DB", "#DCDFE6", "#C8922A", "#D6E1F3");
    }

    public static FrameworkElement Create(ClipView view, SettingsPalette palette)
    {
        var tones = Tones.For(palette);
        var (box, canvas) = Scene(ClipWidth, tones);
        const double left = 16, right = ClipWidth - 16;
        if (view == ClipView.Single)
        {
            double[] lengths = [130, 92, 148, 110];
            for (int i = 0; i < 4; i++)
            {
                Bar(canvas, left, 17 + i * 15, lengths[i], tones.Line);
                Bar(canvas, right - 20, 17 + i * 15, 20, tones.Faint);
            }
        }
        else if (view == ClipView.Three)
        {
            // Two entries, each three lines high: the first with three lines of text, the second with one; three facts beside each.
            double[] facts = [34, 22, 28];
            foreach (var (top, lengths) in new[] { (12d, new[] { 140d, 140, 90 }), (51d, new[] { 96d }) })
            {
                for (int i = 0; i < lengths.Length; i++) Bar(canvas, left, top + i * 10, lengths[i], tones.Line);
                for (int i = 0; i < 3; i++) Bar(canvas, right - facts[i], top + i * 10, facts[i], tones.Faint);
            }
            Place(canvas, new Rectangle { Width = right - left, Height = 1, Fill = UI.Brush(tones.Rule) }, left, 42);
        }
        else
        {
            const double gap = 8, tall = 24;
            double width = (right - left - 2 * gap) / 3;
            double[][] lines = [[.8, .5], [.8, .66], [.6], [.8, .4], [.5], [.8, .6]];
            for (int i = 0; i < 6; i++)
            {
                double x = left + i % 3 * (width + gap), y = 14 + i / 3 * (tall + gap);
                Place(canvas, new Rectangle { Width = width, Height = tall, RadiusX = 4, RadiusY = 4, Fill = UI.Brush(tones.Block) }, x, y);
                for (int j = 0; j < lines[i].Length; j++) Bar(canvas, x + 6, y + 6 + j * 8, (width - 12) * lines[i][j], tones.Line);
            }
        }
        return box;
    }

    public static FrameworkElement Create(FileView view, SettingsPalette palette)
    {
        var tones = Tones.For(palette);
        var (box, canvas) = Scene(FileWidth, tones);
        const double left = 14;
        void Icon(double x, double y, double size) => Place(canvas, new Rectangle { Width = size, Height = size, RadiusX = size / 6, RadiusY = size / 6, Fill = UI.Brush(tones.File) }, x, y);
        // An icon with its name under it, both about the middle x.
        void Tile(double middle, double top, double icon, double name) { Icon(middle - icon / 2, top, icon); Bar(canvas, middle - name / 2, top + icon + 5, name, tones.Line); }
        switch (view)
        {
            case FileView.Single:
                double[] lengths = [70, 50, 80, 60];
                for (int i = 0; i < 4; i++) { Icon(left, 16 + i * 15, 6); Bar(canvas, left + 12, 17 + i * 15, lengths[i], tones.Line); }
                break;
            case FileView.Two:
                foreach (var (top, name) in new[] { (15d, 70d), (49d, 52d) })
                {
                    Icon(left, top, 18);
                    Bar(canvas, left + 26, top + 3, name, tones.Line);
                    Bar(canvas, left + 26, top + 12, 40, tones.Faint);
                }
                break;
            case FileView.Grid:
                double cell = (FileWidth - 2 * left) / 4;
                // Two rows of four; the last has two, standing at the left.
                for (int i = 0; i < 6; i++) Tile(left + cell * (i % 4 + .5), 13 + i / 4 * 32, 12, 16);
                break;
            case FileView.Row:
                // One row that runs on past the right edge.
                for (int i = 0; i < 5; i++) Tile(left + 14 + i * 34, 26, 16, 20);
                break;
            default:
                // One column that runs on past the bottom edge.
                for (int i = 0; i < 3; i++) Tile(FileWidth / 2, 10 + i * 34, 16, 34);
                break;
        }
        return box;
    }

    // The field, clipped to its rounded frame, in a box that scales it to the card's width.
    private static (Viewbox Box, Canvas Canvas) Scene(double width, Tones tones)
    {
        var canvas = new Canvas { Width = width, Height = Height };
        var scene = new Grid
        {
            Width = width, Height = Height, Background = UI.Brush(tones.Ground), IsHitTestVisible = false,
            Clip = new RectangleGeometry(new Rect(0, 0, width, Height), 9, 9), UseLayoutRounding = false, SnapsToDevicePixels = false, Children = { canvas }
        };
        return (new Viewbox { Child = scene, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch }, canvas);
    }
    private static void Bar(Canvas canvas, double x, double y, double width, string color) =>
        Place(canvas, new Rectangle { Width = Math.Max(4, width), Height = 4, RadiusX = 2, RadiusY = 2, Fill = UI.Brush(color) }, x, y);
    private static void Place(Canvas canvas, FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x); Canvas.SetTop(element, y);
        canvas.Children.Add(element);
    }
}

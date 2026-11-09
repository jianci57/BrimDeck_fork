using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrimDeck;

// A dot before a line of text, centered on the text's capitals as the screen draws them. Layout and glyphs both snap to
// device pixels, by amounts that change with the monitor's scale and the panel's, so no fixed offset centers the dot at
// every scale. The capitals' centre is measured instead, by drawing an "H" in the text's font at the scale in use, and the
// dot is drawn that far below the text's top as laid out, without rounding. The text is this element's own child, so it is
// always laid out before the dot is drawn, and the dot is drawn again whenever the text moves.
internal sealed class CapDot : FrameworkElement
{
    private static readonly Dictionary<(string Font, double Size, int Weight, double Pixels), double> Centers = [];
    private readonly TextBlock _text;
    private readonly Brush _fill;
    private readonly double _diameter, _indent;
    private double? _textTop;

    // indent is the width the dot's column takes before the text.
    public CapDot(TextBlock text, Brush fill, double diameter, double indent)
    {
        _text = text; _fill = fill; _diameter = diameter; _indent = indent;
        AddVisualChild(text);
        AddLogicalChild(text);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _text;
    protected override IEnumerator LogicalChildren => new object[] { _text }.GetEnumerator();

    protected override Size MeasureOverride(Size available)
    {
        _text.Measure(new Size(Math.Max(0, available.Width - _indent), available.Height));
        return new Size(_text.DesiredSize.Width + _indent, Math.Max(_text.DesiredSize.Height, _diameter));
    }

    protected override Size ArrangeOverride(Size size)
    {
        _text.Arrange(new Rect(_indent, 0, Math.Max(0, size.Width - _indent), size.Height));
        var top = VisualTreeHelper.GetOffset(_text).Y;
        if (_textTop != top) { _textTop = top; InvalidateVisual(); }
        return size;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var center = VisualTreeHelper.GetOffset(_text).Y + CapCenter(_text, UI.PixelsPerDip(this));
        drawing.DrawEllipse(_fill, null, new Point(_diameter / 2, center), _diameter / 2, _diameter / 2);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateVisual();

    // How far below the top of a line in text's font its capitals' centre is drawn at pixels device pixels per DIP, in DIP.
    // Each edge is placed within its pixel row by how much of the row the glyph covers.
    private static double CapCenter(TextBlock text, double pixels)
    {
        var key = (text.FontFamily.Source, text.FontSize, text.FontWeight.ToOpenTypeWeight(), pixels);
        if (Centers.TryGetValue(key, out var center)) return center;
        var probe = new TextBlock { Text = "H", FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight, Foreground = Brushes.White, UseLayoutRounding = true };
        VisualTreeHelper.SetRootDpi(probe, new DpiScale(pixels, pixels));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        probe.Arrange(new Rect(probe.DesiredSize));
        int width = Math.Max(1, (int)Math.Ceiling(probe.ActualWidth * pixels)), height = Math.Max(1, (int)Math.Ceiling(probe.ActualHeight * pixels));
        var bitmap = new RenderTargetBitmap(width, height, 96 * pixels, 96 * pixels, PixelFormats.Pbgra32);
        bitmap.Render(probe);
        var bytes = new byte[width * height * 4];
        bitmap.CopyPixels(bytes, width * 4, 0);
        double top = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            int cover = 0;
            for (int x = 0; x < width; x++) cover = Math.Max(cover, bytes[(y * width + x) * 4 + 3]);
            if (cover == 0) continue;
            if (top < 0) top = y + 1 - cover / 255.0;
            bottom = y + cover / 255.0;
        }
        center = top < 0 ? UI.Baseline(text.FontSize) - UI.CapHeight(text.FontSize) / 2 : (top + bottom) / 2 / pixels;
        Centers[key] = center;
        return center;
    }
}

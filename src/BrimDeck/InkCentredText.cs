using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrimDeck;

// A one-line text placed so that its letters, as the screen draws them, are centred in the height this element is given. A
// TextBlock centres its line box, which holds the font's ascent and descent: with the panel font, Chinese drawn from Microsoft
// YaHei UI sat about 1 DIP below the middle of an 11.5 line, and other fonts the user may choose sit higher or lower by their
// own amounts. Where the letters land also depends on the display scale, since glyphs snap to device pixels, and fonts such
// as SimSun draw small sizes from bitmaps rather than from their outlines. So, as CapDot does, the text is drawn once at the
// scale in use and its ink measured: Chinese text by its own ink, other text by the centre of its capitals (an "H"), so
// that descenders do not lift it. The text is placed on whole device pixels, where the measurement was made.
//
// The ink is measured as it is seen: a row at the top or bottom that holds only a few pixels, such as the tips of the hooks
// in "时" and "除", counts for as much of a row as its ink is of a usual row's. Counted fully, those tips made "退出时清除" sit
// half a pixel high. And where the room left above and below the letters is an odd number of pixels, no whole-pixel place
// centres them, so the label they sit in (FitHeight) is made one pixel taller. A label that shows one of several words keeps
// one height for all of them, the one that leaves the word furthest from the middle nearest it, so it does not change size
// when its words change.
internal sealed class InkCentredText : FrameworkElement
{
    private static readonly Dictionary<(string Text, string Font, double Size, int Weight, bool Italic, TextFormattingMode Mode, double Pixels), (double Top, double Bottom)> Spans = [];
    private readonly TextBlock _text;
    private FrameworkElement? _box;
    private double _boxHeight;
    private string[] _boxWords = [];

    public InkCentredText(TextBlock text)
    {
        _text = text;
        AddVisualChild(text);
        AddLogicalChild(text);
        IsVisibleChanged += (_, _) => FitBox();
    }

    public TextBlock Text => _text;

    // box, the label these words are centred in, is height tall, or one device pixel taller where that leaves the letters nearer
    // the middle. words are everything the label may show (in this font), these words among them, and they all decide its
    // height; words laid out but hidden leave it alone.
    public void FitHeight(FrameworkElement box, double height, params string[] words)
    {
        _box = box;
        _boxHeight = height;
        _boxWords = words;
        FitBox();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _text;
    protected override IEnumerator LogicalChildren => new object[] { _text }.GetEnumerator();

    protected override Size MeasureOverride(Size available)
    {
        _text.Measure(available);
        return _text.DesiredSize;
    }

    protected override Size ArrangeOverride(Size size)
    {
        FitBox();
        double pixels = UI.PixelsPerDip(this), height = _text.DesiredSize.Height;
        var (inkTop, inkBottom) = InkSpan(_text, pixels);
        double top = Math.Round((size.Height - inkTop - inkBottom) / 2 * pixels) / pixels;
        _text.Arrange(new Rect(0, top, size.Width, height));
        return size;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { FitBox(); InvalidateArrange(); }

    // The box is as tall as asked or one pixel taller, whichever leaves the room above and below the letters nearer a whole
    // number of pixels on each side, for the word that comes out worst. The words fill the box but for its border, the same
    // width at the top and the bottom.
    private void FitBox()
    {
        if (_box is null || Visibility != Visibility.Visible) return;
        double pixels = UI.PixelsPerDip(this);
        if (pixels <= 0) return;
        var inks = _boxWords.Append(_text.Text).Select(word => InkSpan(_text, word, pixels)).Select(span => (span.Bottom - span.Top) * pixels).ToArray();
        int asked = (int)Math.Round(_boxHeight * pixels);
        double Off(int box) => inks.Max(ink => { double side = (box - ink) / 2; return Math.Abs(side - Math.Round(side)); });
        double height = (Off(asked + 1) < Off(asked) - .05 ? asked + 1 : asked) / pixels;
        if (Math.Abs(_box.Height - height) > 1e-6) _box.Height = height;
    }

    internal static (double Top, double Bottom) InkSpan(TextBlock text, double pixels) => InkSpan(text, text.Text, pixels);

    // Where word's letters start and end below the top of its line when drawn in text's font at pixels device pixels per DIP,
    // in DIP. Each row of the drawing weighs its ink against the usual row's (the median of the rows with ink), at most fully;
    // the rows at either end that weigh less than fully are counted as that much of a row.
    internal static (double Top, double Bottom) InkSpan(TextBlock text, string word, double pixels)
    {
        string probeText = word.Any(IsChinese) ? word : "H";
        var mode = TextOptions.GetTextFormattingMode(text);
        var key = (probeText, text.FontFamily.Source, text.FontSize, text.FontWeight.ToOpenTypeWeight(), text.FontStyle != FontStyles.Normal, mode, pixels);
        if (Spans.TryGetValue(key, out var span)) return span;
        var probe = new TextBlock { Text = probeText, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight, FontStyle = text.FontStyle,
            Foreground = Brushes.White, UseLayoutRounding = true };
        TextOptions.SetTextFormattingMode(probe, mode);
        VisualTreeHelper.SetRootDpi(probe, new DpiScale(pixels, pixels));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        probe.Arrange(new Rect(probe.DesiredSize));
        int width = Math.Max(1, (int)Math.Ceiling(probe.ActualWidth * pixels)), height = Math.Max(1, (int)Math.Ceiling(probe.ActualHeight * pixels));
        var bitmap = new RenderTargetBitmap(width, height, 96 * pixels, 96 * pixels, PixelFormats.Pbgra32);
        bitmap.Render(probe);
        var bytes = new byte[width * height * 4];
        bitmap.CopyPixels(bytes, width * 4, 0);
        var rows = new double[height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) rows[y] += bytes[(y * width + x) * 4 + 3];
        var inked = rows.Where(row => row > 0).Order().ToArray();
        if (inked.Length == 0) span = (probe.ActualHeight / 2, probe.ActualHeight / 2);
        else
        {
            double usual = inked[inked.Length / 2];
            double Weight(int y) => Math.Min(1, rows[y] / usual);
            int first = Array.FindIndex(rows, row => row > 0), last = Array.FindLastIndex(rows, row => row > 0);
            double top = first, bottom = last + 1;
            for (int y = first; y <= last && Weight(y) < 1; y++) top += 1 - Weight(y);
            for (int y = last; y >= first && Weight(y) < 1; y--) bottom -= 1 - Weight(y);
            span = top < bottom ? (top / pixels, bottom / pixels) : ((first + last + 1) / 2.0 / pixels, (first + last + 1) / 2.0 / pixels);
        }
        if (Spans.Count > 64) Spans.Clear();
        Spans[key] = span;
        return span;
    }

    // CJK radicals, symbols and punctuation, kana, the unified ideographs, compatibility ideographs and full-width forms.
    private static bool IsChinese(char c) => c is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';
}

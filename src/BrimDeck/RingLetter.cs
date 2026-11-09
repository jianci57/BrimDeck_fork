using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The letter inside a collapsed quota ring, drawn as the glyph's outline and centred on that outline's own ink box. A TextBlock
// centres its line box, which holds the font's ascent and descent, so a capital sat a device pixel or two below the ring's centre.
// Here the outline (unhinted, stroked a little to give back the weight hinting would) is scaled to a box of whole device pixels
// whose width and height have the parity of the ring's, so that the box is centred on the ring's centre exactly
// (CompactMusicLayout.RingLetterBox); at 12 pixels across, a letter 5 pixels wide could only be half a pixel off.
// The box is centred, but what the eye sees is the drawn pixels: a stem that falls between two columns draws as two faint ones and
// the letter looks moved toward its solid side. So the outline is drawn a few quarter pixels either way and kept where its drawn
// pixels are centred (Settle).
internal static class RingLetter
{
    private static readonly Dictionary<(string Letter, double Size, double Cell, double Inner, double Ppd, string Font), (Geometry Glyph, double Stroke)> Made = [];

    // The letter's path, to put at the ring's top left corner (cell: the ring's size, inner: its inside diameter); null when the font
    // draws nothing for it. Sizes in DIP; the ring is expected on whole device pixels.
    public static Path? Create(string letter, double fontSize, double cell, double inner, double pixelsPerDip, FontFamily font, Brush ink)
    {
        if (letter.Length == 0) return null;
        double ppd = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        var key = (letter, fontSize, cell, inner, ppd, font.Source ?? "");
        if (!Made.TryGetValue(key, out var made))
        {
            var text = new FormattedText(letter, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), fontSize, ink, ppd);
            var outline = text.BuildGeometry(new Point());
            var bounds = outline.Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;
            double pixels = fontSize * ppd, stroke = Math.Min(.06 * pixels, .17 * Math.Sqrt(pixels)), strokeDip = stroke / ppd;
            var (cx, cy) = Mass(outline, bounds);
            // The box counts the stroke, which reaches half its width past the outline on every side.
            var box = CompactMusicLayout.RingLetterBox(bounds.Width * ppd + stroke, bounds.Height * ppd + stroke, (int)Math.Round(cell * ppd), cx, cy, Math.Round(inner * ppd) - 1);
            double sx = (box.Width - stroke) / (bounds.Width * ppd), sy = (box.Height - stroke) / (bounds.Height * ppd);
            // Outline to its origin, scaled to the box less the stroke, moved to the box's corner plus half the stroke; all in DIP.
            var place = new Matrix(sx, 0, 0, sy, (box.Left + stroke / 2) / ppd - bounds.X * sx, (box.Top + stroke / 2) / ppd - bounds.Y * sy);
            place = Settle(outline, place, strokeDip, ppd, (int)Math.Round(cell * ppd));
            outline.Transform = new MatrixTransform(place);
            outline.Freeze();
            if (Made.Count > 64) Made.Clear();
            Made[key] = made = (outline, strokeDip);
        }
        return new Path { Data = made.Glyph, Fill = ink, Stroke = ink, StrokeThickness = made.Stroke, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    }

    // The placement moved by up to half a device pixel on each axis so that the drawn letter is centred in the cell (pixels
    // across): each edge counts as far in as its outermost row or column is faint, so a faint column counts for little. Of
    // placements equally centred, the one nearest the box is kept.
    private static Matrix Settle(Geometry outline, Matrix place, double stroke, double ppd, int pixels)
    {
        if (pixels < 2) return place;
        double[] steps = [0, -.25, .25, -.5, .5];
        double Best(bool across)
        {
            double best = 0, miss = double.MaxValue;
            foreach (double step in steps)
            {
                var moved = place;
                if (across) moved.OffsetX += step / ppd; else moved.OffsetY += step / ppd;
                double off = Math.Abs(Centre(Draw(outline, moved, stroke, ppd, pixels), pixels, across) - pixels / 2.0);
                if (off < miss - .02) { miss = off; best = step; }
            }
            return best;
        }
        double x = Best(true), y = Best(false);
        place.OffsetX += x / ppd; place.OffsetY += y / ppd;
        return place;
    }

    // The letter drawn as the ring draws it, on a square of whole device pixels: each pixel's coverage, 0 to 1.
    private static double[] Draw(Geometry outline, Matrix place, double stroke, double ppd, int pixels)
    {
        var glyph = outline.Clone(); glyph.Transform = new MatrixTransform(place);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(ppd, ppd));
            context.DrawGeometry(Brushes.White, new Pen(Brushes.White, stroke) { LineJoin = PenLineJoin.Round }, glyph);
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bytes = new byte[pixels * pixels * 4];
        bitmap.CopyPixels(bytes, pixels * 4, 0);
        var coverage = new double[pixels * pixels];
        for (int i = 0; i < coverage.Length; i++) coverage[i] = bytes[i * 4 + 3] / 255.0;
        return coverage;
    }

    // Where the drawn letter's middle lies across (or down), in pixels from the cell's edge: each column's (row's) strongest
    // pixel, against the letter's strongest; the first and last that show count from their inner side as far as they are faint.
    private static double Centre(double[] coverage, int pixels, bool across)
    {
        var line = new double[pixels];
        for (int y = 0; y < pixels; y++)
            for (int x = 0; x < pixels; x++)
            {
                int at = across ? x : y;
                line[at] = Math.Max(line[at], coverage[y * pixels + x]);
            }
        double strongest = line.Max();
        if (strongest <= 0) return pixels / 2.0;
        int first = Array.FindIndex(line, value => value / strongest > .05), last = Array.FindLastIndex(line, value => value / strongest > .05);
        return (first + 1 - line[first] / strongest + last + line[last] / strongest) / 2;
    }

    // Where the glyph's mass lies in its box, 0 to 1 from the left and from the top: the centroid of the outline's polygon (the
    // counters, wound the other way, take their area off), so an A's is below the middle and a V's above it.
    private static (double X, double Y) Mass(Geometry outline, Rect bounds)
    {
        double area = 0, sumX = 0, sumY = 0;
        foreach (var figure in outline.GetFlattenedPathGeometry(.01, ToleranceType.Absolute).Figures)
        {
            var from = figure.StartPoint;
            var points = new List<Point> { from };
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment line) points.AddRange(line.Points);
                else if (segment is LineSegment single) points.Add(single.Point);
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                double cross = a.X * b.Y - b.X * a.Y;
                area += cross; sumX += (a.X + b.X) * cross; sumY += (a.Y + b.Y) * cross;
            }
        }
        if (Math.Abs(area) < 1e-9) return (.5, .5);
        return (Math.Clamp((sumX / (3 * area) - bounds.X) / bounds.Width, 0, 1), Math.Clamp((sumY / (3 * area) - bounds.Y) / bounds.Height, 0, 1));
    }
}

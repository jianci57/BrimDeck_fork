using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BrimDeck;

// The letter inside a collapsed quota ring, rendered offscreen at 100 to 200 %: where its ink lands against the ring's centre, drawn
// as it is now (RingLetter) and as it was (a TextBlock centred in the ring). Nothing is shown. Rings are sized in device pixels
// (10 to 20 across, whole, on whole pixels, as the carousel places them). The ink is measured two ways, as the eye does: its box
// (every pixel with a thirty-second of full alpha or more) and its mass (the pixels' alpha-weighted centre), in device pixels
// against the ring's centre. With a folder given, the enlarged pictures are saved there too.
internal static class RingLetterChecks
{
    private static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xC8, 0xC8, 0xC8));
    private const int Pad = 4;

    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    // A ring cells pixels across with a track of thick pixels, at scale ppd; the letter 7/12 of the ring across, as in the row.
    private static FrameworkElement Ring(int cell, int thick, string letter, double ppd, bool now, bool drawn)
    {
        double size = cell / ppd, thickness = thick / ppd, font = 7.0 / 12 * cell / ppd;
        var ring = new Grid { Width = size, Height = size };
        if (drawn)
        {
            ring.Children.Add(new Ellipse { Stroke = Frozen(Color.FromRgb(0x44, 0x44, 0x44)), StrokeThickness = thickness });
            double radius = size / 2 - thickness / 2, c = size / 2, angle = .65 * Math.PI * 2;
            var figure = new PathFigure { StartPoint = new Point(c, c - radius), IsClosed = false };
            figure.Segments.Add(new ArcSegment(new Point(c - radius * Math.Sin(angle), c - radius * Math.Cos(angle)), new Size(radius, radius), 0, true, SweepDirection.Counterclockwise, true));
            ring.Children.Add(new System.Windows.Shapes.Path { Data = new PathGeometry([figure]), Stroke = Frozen(Color.FromRgb(0xE0, 0x8A, 0x4A)), StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        if (letter.Length == 0) return ring;
        if (now) { if (RingLetter.Create(letter, font, size, size - 2 * thickness, ppd, Font, Ink) is { } path) ring.Children.Add(path); }
        else ring.Children.Add(new TextBlock { Text = letter, FontFamily = Font, FontWeight = FontWeights.SemiBold, FontSize = font, Foreground = Ink,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center });
        return ring;
    }

    // The picture (BGRA, device pixels): the ring Pad pixels in from the corner.
    private static (byte[] Pixels, int Size) Render(int cell, int thick, string letter, double ppd, bool now, bool drawn, bool dark)
    {
        int pixels = cell + 2 * Pad;
        double side = pixels / ppd;
        // No layout rounding: an element outside a window rounds to the system scale, not to the one rendered at; every size here is whole device pixels already.
        var host = new Grid { Width = side, Height = side, UseLayoutRounding = false, Background = dark ? Frozen(Color.FromRgb(0x20, 0x20, 0x20)) : null };
        var ring = Ring(cell, thick, letter, ppd, now, drawn);
        ring.HorizontalAlignment = HorizontalAlignment.Left; ring.VerticalAlignment = VerticalAlignment.Top; ring.Margin = new Thickness(Pad / ppd);
        host.Children.Add(ring);
        host.Measure(new Size(side, side)); host.Arrange(new Rect(0, 0, side, side)); host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96 * ppd, 96 * ppd, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var data = new byte[pixels * pixels * 4]; bitmap.CopyPixels(data, pixels * 4, 0);
        return (data, pixels);
    }

    // The ink's box centre and its mass centre against the ring's centre, device pixels (x, y of each); NaN with no ink.
    private static (double BoxX, double BoxY, double MassX, double MassY) Deviation(int cell, int thick, string letter, double ppd, bool now)
    {
        var (data, n) = Render(cell, thick, letter, ppd, now, drawn: false, dark: false);
        int x0 = n, y0 = n, x1 = -1, y1 = -1;
        double sum = 0, sx = 0, sy = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int alpha = data[(y * n + x) * 4 + 3];
                if (alpha >= 8) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
                sum += alpha; sx += alpha * (x + .5); sy += alpha * (y + .5);
            }
        if (x1 < 0) return (double.NaN, double.NaN, double.NaN, double.NaN);
        double centre = Pad + cell / 2.0;
        return ((x0 + x1 + 1) / 2.0 - centre, (y0 + y1 + 1) / 2.0 - centre, sx / sum - centre, sy / sum - centre);
    }

    public static void Run(Action<string, bool> check, string? folder = null)
    {
        double[] scales = [1.0, 1.25, 1.5, 1.75, 2.0];
        int[] cells = [10, 12, 14, 16, 20];
        string[] checked_ = ["A", "C", "K", "O", "1", "文"], heavy = ["V", "W", "T", "Y", "L", "7"];
        int thick(int cell) => Math.Max(1, (int)Math.Round(cell / 6.0));
        double worstBox = 0, worstMass = 0, worstSmall = 0;
        string worstWhere = "";
        foreach (double ppd in scales)
            foreach (int cell in cells)
                foreach (string letter in checked_)
                {
                    var now = Deviation(cell, thick(cell), letter, ppd, now: true);
                    double box = Math.Max(Math.Abs(now.BoxX), Math.Abs(now.BoxY)), mass = Math.Max(Math.Abs(now.MassX), Math.Abs(now.MassY));
                    worstBox = Math.Max(worstBox, box);
                    if (cell <= 14) worstSmall = Math.Max(worstSmall, mass);
                    if (mass > worstMass) { worstMass = mass; worstWhere = $"{letter} in {cell} px at {ppd * 100:0}%"; }
                    if (box > 1e-9) Console.WriteLine($"  box off: {letter} in {cell} px at {ppd * 100:0}%: dx={now.BoxX:+0.00;-0.00} dy={now.BoxY:+0.00;-0.00}");
                    if (mass > .4 && ppd is 1.0 or 1.5) Console.WriteLine($"  mass off: {letter} in {cell} px at {ppd * 100:0}%: dx={now.MassX:+0.00;-0.00} dy={now.MassY:+0.00;-0.00}");
                }
        // The row's own ring (12 px at its scale 1, with a 2 px track): what the maintainer saw before and what is drawn now.
        double beforeBox = 0, beforeMass = 0, nowBox = 0, nowMass = 0;
        foreach (double ppd in scales)
            foreach (string letter in checked_.Concat(heavy))
            {
                var then = Deviation(12, 2, letter, ppd, now: false); var now = Deviation(12, 2, letter, ppd, now: true);
                beforeBox = Math.Max(beforeBox, Math.Max(Math.Abs(then.BoxX), Math.Abs(then.BoxY))); beforeMass = Math.Max(beforeMass, Math.Max(Math.Abs(then.MassX), Math.Abs(then.MassY)));
                nowBox = Math.Max(nowBox, Math.Max(Math.Abs(now.BoxX), Math.Abs(now.BoxY))); nowMass = Math.Max(nowMass, Math.Max(Math.Abs(now.MassX), Math.Abs(now.MassY)));
                if (letter is "A" or "V" or "T" && ppd is 1.0 or 1.5)
                    Console.WriteLine($"ring letter {letter}, 12 px ring at {ppd * 100:0}%: box dx/dy before {then.BoxX:+0.00;-0.00}/{then.BoxY:+0.00;-0.00} now {now.BoxX:+0.00;-0.00}/{now.BoxY:+0.00;-0.00}; mass before {then.MassX:+0.00;-0.00}/{then.MassY:+0.00;-0.00} now {now.MassX:+0.00;-0.00}/{now.MassY:+0.00;-0.00}");
            }
        Console.WriteLine($"ring letter, 12 px ring, 12 letters, 5 scales, worst device px: box before {beforeBox:0.00} now {nowBox:0.00}; mass before {beforeMass:0.00} now {nowMass:0.00}");
        Console.WriteLine($"ring letter, rings 10 to 20 px, A C K O 1 and a CJK character: worst box {worstBox:0.00}, worst mass {worstMass:0.00} ({worstWhere})");
        check("Ring letter, rendered: the ink box's centre is the ring's centre exactly (rings 10 to 20 px, 100 to 200 %, A C K O 1 and a CJK character)", worstBox < 1e-9);
        // Beyond 14 px the box and the mass cannot both be nearer: moving the box a whole pixel toward the mass would put the box a
        // pixel off to bring the mass in by less than that (the rule in RingLetterBox), so the mass keeps up to 0.7 there.
        check("Ring letter, rendered: the ink's mass is within 0.4 device pixel of the ring's centre in rings of 10 to 14 px, within 0.7 in 16 and 20 px", worstSmall <= .4 && worstMass <= .7);
        check("Ring letter, rendered: a TextBlock centred in the ring (before) was further off, box and mass, than the letter placed by its ink", beforeBox > nowBox && beforeMass > nowMass);
        if (folder is null) return;

        Directory.CreateDirectory(folder);
        // 100 % and 150 %, before (left) and now (right), the ring 12 px across, enlarged 14 times; the pink lines are the ring's centre.
        const int Zoom = 14, Gap = 12;
        var cellsDrawn = new List<(byte[] Pixels, int Size)>();
        foreach (double ppd in new[] { 1.0, 1.5 })
            foreach (string letter in new[] { "A", "C" })
                foreach (bool now in new[] { false, true })
                    cellsDrawn.Add(Render(12, 2, letter, ppd, now, drawn: true, dark: true));
        int rows = 4, columns = 2, edge = cellsDrawn[0].Size * Zoom;
        int width = columns * edge + (columns + 1) * Gap, height = rows * edge + (rows + 1) * Gap;
        var sheet = new byte[width * height * 4];
        for (int i = 0; i < sheet.Length; i += 4) { sheet[i] = 0x10; sheet[i + 1] = 0x10; sheet[i + 2] = 0x10; sheet[i + 3] = 0xFF; }
        for (int n = 0; n < cellsDrawn.Count; n++)
        {
            var (p, size) = cellsDrawn[n];
            int left = Gap + (n % columns) * (edge + Gap), top = Gap + (n / columns) * (edge + Gap);
            double centre = (Pad + 6.0) * Zoom;
            for (int y = 0; y < size * Zoom; y++)
                for (int x = 0; x < size * Zoom; x++)
                {
                    int src = ((y / Zoom) * size + x / Zoom) * 4, dst = ((top + y) * width + left + x) * 4;
                    Array.Copy(p, src, sheet, dst, 4);
                    if (Math.Abs(y + .5 - centre) < 1 || Math.Abs(x + .5 - centre) < 1) { sheet[dst] = 0xFF; sheet[dst + 1] = 0x40; sheet[dst + 2] = 0xFF; }
                }
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, sheet, width * 4)));
        using var file = File.Create(System.IO.Path.Combine(folder, "ring-letter-compare.png"));
        encoder.Save(file);
    }
}

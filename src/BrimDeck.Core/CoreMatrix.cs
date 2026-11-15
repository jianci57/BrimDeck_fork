namespace BrimDeck.Core;

// How the processor's cores are drawn: as columns, as a dot matrix (one square per logical processor), or by Auto, columns while
// they stay readable and the matrix once the count would make them thinner than that (a machine with 192 threads).
// Saved as a number; a missing or unknown value is Auto.
public enum CoreDisplay { Auto = 0, Columns = 1, Matrix = 2 }

// One placement of a core matrix: the squares fill column by column (top to bottom, then the next column to the right), so the
// kinds of core, fastest first, read as stripes from left to right just as the columns do. Every size is in DIPs on whole device
// pixels. Origin is where the grid starts inside the rect it was fitted to (the grid is centred vertically, left-aligned).
// Fits is false when even the smallest squares asked for would not fit the rect: the grid is then wider than the rect.
public readonly record struct MatrixGrid(int Count, int Rows, int Columns, double Cell, double Gap, double Width, double Height, double X, double Y, bool Fits)
{
    public static readonly MatrixGrid Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, true);
    public double Pitch => Cell + Gap;

    // The left and top edges of a core's square.
    public (double X, double Y) Origin(int index) => (X + index / Rows * Pitch, Y + index % Rows * Pitch);

    // The core under a point (in the rect's own coordinates), or -1. The gap belongs to the square before it, so no point of the
    // grid is dead; the empty places of the last column and anything outside the grid give -1.
    public int CellAt(double x, double y)
    {
        if (Count == 0 || Rows == 0 || Pitch <= 0 || !double.IsFinite(x) || !double.IsFinite(y)) return -1;
        double px = x - X, py = y - Y;
        if (px < 0 || py < 0 || px >= Columns * Pitch || py >= Rows * Pitch) return -1;
        int index = (int)(px / Pitch) * Rows + (int)(py / Pitch);
        return index < Count ? index : -1;
    }
}

public static class CoreMatrix
{
    // Auto keeps the columns while each is at least this many device pixels wide.
    public const double ReadableColumn = 2;
    // The load is shown in this many brightness steps (0 .. Steps - 1) of the core's color over the empty cell: a cell steps
    // between them as the eased load moves, so no brush is made per frame (the strip draws every frame while values ease).
    public const int Steps = 8;
    // The share of the color shown at each step: the idle step is still visibly a cell, the busiest the whole color.
    public const double IdleAlpha = .22;

    public static int Step(double share) => (int)Math.Round(Math.Clamp(double.IsFinite(share) ? share : 0, 0, 1) * (Steps - 1));
    public static byte Alpha(int step) => (byte)Math.Round(255 * (IdleAlpha + (1 - IdleAlpha) * Math.Clamp(step, 0, Steps - 1) / (Steps - 1)));

    // The columns are drawn at least this many DIPs wide (the graph clamps to it), so narrower than this they would overflow.
    public const double LeastColumnDip = 1;
    // The same for a width in DIPs and the device pixels per DIP: Auto also takes the matrix where the clamp would overflow the graph.
    public static bool Use(CoreDisplay display, double columnDip, double pixelsPerDip) =>
        Use(display, columnDip * pixelsPerDip) || display == CoreDisplay.Auto && columnDip < LeastColumnDip;

    // Whether the matrix draws the cores, for the setting and the width, in device pixels, the columns would have.
    public static bool Use(CoreDisplay display, double columnDevicePixels) => display switch
    {
        CoreDisplay.Matrix => true,
        CoreDisplay.Columns => false,
        _ => columnDevicePixels < ReadableColumn
    };

    // The gap between squares of this pitch (device pixels): none below 3, so the smallest squares touch; one up to 7; then
    // a seventh of the pitch.
    public static int GapPixels(int pitch) => pitch < 3 ? 0 : pitch < 8 ? 1 : (int)Math.Round(pitch / 7d);

    // The largest squares for count cores in a width by height rect at dpi device pixels per DIP: rows and columns follow from the
    // count and the room (the biggest whole-pixel pitch for which the columns fit across and the rows down, then as few columns as
    // that allows with the rows balanced between them). maxCell (DIPs) stops a few cores from growing into slabs; minPitch (device
    // pixels) is the smallest the squares may shrink to before the grid is allowed to be wider than the rect.
    public static MatrixGrid Fit(int count, double width, double height, double dpi, double maxCell = double.PositiveInfinity, int minPitch = 1)
    {
        if (count <= 0) return MatrixGrid.Empty;
        if (!double.IsFinite(dpi) || dpi <= 0) dpi = 1;
        minPitch = Math.Max(1, minPitch);
        int wp = Pixels(width, dpi), hp = Pixels(height, dpi);
        int capCell = double.IsFinite(maxCell) ? Math.Max(1, (int)Math.Floor(maxCell * dpi + 1e-6)) : int.MaxValue;
        if (wp > 0 && hp > 0)
            for (int pitch = (int)Math.Min(Math.Max(wp, hp), (long)capCell + 8); pitch >= minPitch; pitch--)
            {
                int gap = GapPixels(pitch);
                if (pitch - gap > capCell || pitch - gap > hp) continue;
                var (rows, columns) = Arrange(count, pitch, gap, hp);
                if (columns * pitch - gap <= wp)
                    return Make(count, rows, columns, pitch, gap, dpi, wp, hp, fits: true);
            }
        int small = minPitch, smallGap = GapPixels(small);
        var (r, c) = Arrange(count, small, smallGap, hp);
        return Make(count, r, c, small, smallGap, dpi, wp, hp, fits: false);
    }

    // The strip's matrix: the room the columns have (56 wide and 14 high at the strip's scale), widening only when the squares
    // would otherwise be under 2 device pixels.
    public static MatrixGrid Strip(int count, double scale, double dpi)
        => Fit(count, Math.Round(CompactMonitor.ColumnsRoom * SafeScale(scale)), Math.Round(CompactMonitor.ColumnsHeight * SafeScale(scale)), dpi, minPitch: 2);

    private static double SafeScale(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1;
    private static int Pixels(double size, double dpi) => double.IsFinite(size) && size > 0 ? (int)Math.Min(Math.Floor(size * dpi + 1e-6), 1 << 20) : 0;

    // Rows and columns for squares of this pitch down a rect hp device pixels high: the most rows that fit (at least one), the
    // columns they need, then the fewest rows those columns need.
    private static (int Rows, int Columns) Arrange(int count, int pitch, int gap, int hp)
    {
        int rows = Math.Clamp((hp + gap) / pitch, 1, count);
        int columns = (count + rows - 1) / rows;
        return ((count + columns - 1) / columns, columns);
    }

    private static MatrixGrid Make(int count, int rows, int columns, int pitch, int gap, double dpi, int wp, int hp, bool fits)
    {
        int height = rows * pitch - gap, width = columns * pitch - gap;
        int top = fits || height <= hp ? (hp - height) / 2 : 0;
        return new(count, rows, columns, (pitch - gap) / dpi, gap / dpi, width / dpi, height / dpi, 0, top / dpi, fits);
    }
}

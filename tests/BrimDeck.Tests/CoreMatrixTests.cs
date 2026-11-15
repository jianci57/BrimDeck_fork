using System.Text.Json;
using BrimDeck.Core;

// The core dot matrix: the placement for any count and room, hit testing, the Auto rule, the strip's width and the setting.
internal static class CoreMatrixTests
{
    private static readonly int[] Counts = [1, 2, 4, 8, 16, 32, 64, 128, 192, 256, 512];
    private static readonly double[] Dpis = [1, 1.5, 2];
    private static readonly (double W, double H)[] Rects = [(56, 14), (84, 21), (250, 40), (450, 60), (120, 120), (300, 18), (24, 90), (600, 200)];

    internal static void Run(Action<string, bool> check)
    {
        Placement(check);
        Hits(check);
        Rule(check);
        Strip(check);
        Setting(check);
    }

    private static void Placement(Action<string, bool> check)
    {
        int bad = 0, small = 0, unsquare = 0, unstable = 0, tried = 0, unsnapped = 0;
        foreach (var dpi in Dpis)
            foreach (var (w, h) in Rects)
                foreach (var count in Counts)
                {
                    var g = CoreMatrix.Fit(count, w, h, dpi, 16);
                    tried++;
                    if (g != CoreMatrix.Fit(count, w, h, dpi, 16)) unstable++;
                    if (g.Rows * g.Columns < count || (g.Rows - 1) * g.Columns >= count && g.Rows > 1 || g.Cell <= 0) bad++;
                    // Whenever the room can hold one device pixel per core, nothing overflows or is clipped.
                    bool room = Math.Floor(w * dpi) * Math.Floor(h * dpi) >= count * 2;
                    if (room && (!g.Fits || g.Width > w + 1e-9 || g.Height > h + 1e-9 || g.X < 0 || g.Y < 0 || g.Y + g.Height > h + 1e-9)) bad++;
                    if (g.Fits && g.Cell * dpi < 2 - 1e-9 && Math.Floor(w * dpi) * Math.Floor(h * dpi) >= count * 4) small++;
                    // Squares on whole device pixels.
                    if (Math.Abs(g.Cell * dpi - Math.Round(g.Cell * dpi)) > 1e-6 || Math.Abs(g.Gap * dpi - Math.Round(g.Gap * dpi)) > 1e-6) unsnapped++;
                    if (g.Cell > 16 + 1e-9) unsquare++;
                }
        check($"Core matrix: {tried} placements never overflow, keep every core and stay whole-pixel", bad == 0 && unsnapped == 0);
        check("Core matrix: squares are at least 2 device pixels where the room allows and no larger than asked", small == 0 && unsquare == 0);
        check("Core matrix: the same input gives the same placement", unstable == 0);

        var g192 = CoreMatrix.Fit(192, 56, 14, 1, minPitch: 2);
        check("Core matrix: 192 cores fit the strip's 56 x 14 room in 7 rows of 28 touching 2-pixel squares", g192 is { Rows: 7, Columns: 28, Cell: 2, Gap: 0, Fits: true });
        var g192x = CoreMatrix.Fit(192, 84, 21, 1.5, minPitch: 2);
        check("Core matrix: at 150 % the squares keep a one-pixel gap", g192x is { Fits: true } && g192x.Gap * 1.5 == 1 && g192x.Width <= 84);
        var page = CoreMatrix.Fit(192, 450, 60, 1, 14);
        check("Core matrix: 192 cores on the page are real squares with a gap", page is { Fits: true } && page.Cell >= 6 && page.Gap >= 1 && page.Width <= 450 && page.Height <= 60);
        var many = CoreMatrix.Fit(512, 56, 14, 1, minPitch: 2);
        check("Core matrix: more cores than the strip's room widen it instead of shrinking under 2 pixels", many is { Fits: false, Cell: 2 } && many.Width > 56 && many.Height <= 14);
        var one = CoreMatrix.Fit(1, 400, 80, 1, 14);
        check("Core matrix: one core is one square of the largest size, centred down the rect", one is { Rows: 1, Columns: 1, Cell: 14, Fits: true } && one.Y == 33);
        check("Core matrix: degenerate rects give a placement without throwing",
            CoreMatrix.Fit(8, 0, 0, 1).Count == 8 && CoreMatrix.Fit(8, double.NaN, -3, 0).Count == 8 && CoreMatrix.Fit(0, 50, 50, 1) == MatrixGrid.Empty && CoreMatrix.Fit(-4, 50, 50, 1).Count == 0
            && CoreMatrix.Fit(8, 5, 5, 1) is { Cell: >= 1 });
        // Group order: cells run down a column, then right.
        var order = CoreMatrix.Fit(10, 100, 30, 1, 10);
        var (x0, y0) = order.Origin(0); var (x1, y1) = order.Origin(1); var (xr, yr) = order.Origin(order.Rows);
        check("Core matrix: cores fill a column from the top, then the next column", x1 == x0 && y1 > y0 && yr == y0 && xr > x0);
    }

    private static void Hits(Action<string, bool> check)
    {
        bool every = true;
        foreach (var count in new[] { 1, 7, 12, 192, 512 })
        {
            var g = CoreMatrix.Fit(count, 450, 60, 1.5, 14);
            for (int i = 0; i < count; i++)
            {
                var (x, y) = g.Origin(i);
                every &= g.CellAt(x + g.Cell / 2, y + g.Cell / 2) == i && g.CellAt(x + .01, y + .01) == i;
            }
        }
        check("Core matrix: pointing at the middle of a cell names that core", every);
        var grid = CoreMatrix.Fit(10, 100, 30, 1, 10);
        check("Core matrix: outside the grid and the empty places of the last column name no core",
            grid.CellAt(-1, 5) == -1 && grid.CellAt(5, -1) == -1 && grid.CellAt(1000, 5) == -1 && grid.CellAt(double.NaN, 5) == -1
            && grid.CellAt(grid.Columns * grid.Pitch - .1, grid.Rows * grid.Pitch - .1) == (grid.Rows * grid.Columns > 10 ? -1 : 9));
    }

    private static void Rule(Action<string, bool> check)
    {
        check("Core display: Auto takes columns while they are 2 device pixels wide, the matrix below", !CoreMatrix.Use(CoreDisplay.Auto, 2) && CoreMatrix.Use(CoreDisplay.Auto, 1.99));
        check("Core display: Auto takes the matrix where the columns would be under a DIP wide even when they are 2 device pixels (300 % scale)", CoreMatrix.Use(CoreDisplay.Auto, 0.85, 3) && !CoreMatrix.Use(CoreDisplay.Auto, 1, 3) && !CoreMatrix.Use(CoreDisplay.Columns, 0.5, 3));
        check("Core display: Columns and Matrix ignore the room", !CoreMatrix.Use(CoreDisplay.Columns, 0.1) && CoreMatrix.Use(CoreDisplay.Matrix, 50));
        bool usual = true;
        foreach (var scale in new[] { 1, 1.25, 1.5, 2 })
            foreach (var cores in new[] { 1, 4, 8, 12, 16, 20, 24, 32 })
                usual &= !CompactMonitor.UsesMatrix(CoreDisplay.Auto, cores, scale);
        check("Core display: Auto leaves 1 to 32 cores as columns on the strip from scale 1 up", usual);
        check("Core display: Auto turns 64 and more cores into the matrix on the strip", CompactMonitor.UsesMatrix(CoreDisplay.Auto, 64, 1) && CompactMonitor.UsesMatrix(CoreDisplay.Auto, 192, 1.5));
        check("Core display: the strip matrix stays within the columns' room for 192 cores at every scale and dpi",
            Dpis.All(dpi => new[] { 1d, 1.5, 2 }.All(scale => CompactMonitor.CoreWidth(CoreDisplay.Auto, 192, scale, dpi) <= Math.Round(56 * scale) + 1e-9)));
        check("Core display: the columns' width is unchanged for the columns", CompactMonitor.CoreWidth(CoreDisplay.Columns, 8, 1, 1) == CompactMonitor.CoreColumns(8, 1).Width);
    }

    private static void Strip(Action<string, bool> check)
    {
        check("Core matrix: brightness steps run from idle to full, in order",
            CoreMatrix.Step(0) == 0 && CoreMatrix.Step(1) == CoreMatrix.Steps - 1 && CoreMatrix.Step(double.NaN) == 0 && CoreMatrix.Step(5) == CoreMatrix.Steps - 1
            && Enumerable.Range(0, CoreMatrix.Steps).Select(CoreMatrix.Alpha).SequenceEqual(Enumerable.Range(0, CoreMatrix.Steps).Select(CoreMatrix.Alpha).Order())
            && CoreMatrix.Alpha(0) > 40 && CoreMatrix.Alpha(CoreMatrix.Steps - 1) == 255);
        // The levels color the cell as they color a column (ReadingLevels.OfShare), so a busy core is a different color from an idle one.
        check("Core matrix: the warning and critical loads are the columns' levels", ReadingLevels.OfShare(.5) == MemoryLevel.Normal && ReadingLevels.OfShare(.85) == MemoryLevel.Elevated && ReadingLevels.OfShare(.95) == MemoryLevel.Critical);
    }

    private static void Setting(Action<string, bool> check)
    {
        check("Core display: a new settings file shows Auto", new DeckSettings().CoreDisplay == CoreDisplay.Auto);
        var missing = SettingsMigrations.Read("""{"AppVersion":"0.1.0"}""");
        check("Core display: an older settings file without it reads as Auto", missing.CoreDisplay == CoreDisplay.Auto);
        var saved = new DeckSettings { CoreDisplay = CoreDisplay.Matrix };
        var back = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(saved));
        check("Core display: the choice survives saving and loading", back?.CoreDisplay == CoreDisplay.Matrix);
        var odd = SettingsMigrations.Read("""{"AppVersion":"0.1.0","CoreDisplay":42}""");
        check("Core display: an unknown value falls back to Auto", odd.CoreDisplay == CoreDisplay.Auto);
    }
}

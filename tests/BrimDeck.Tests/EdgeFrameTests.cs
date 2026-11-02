using BrimDeck.Core;

internal static class EdgeFrameTests
{
    internal static void Run(Action<string, bool> check)
    {
        // A 1920 x 1080 monitor with a 48 DIP taskbar at the bottom.
        var work = new DipRect(0, 0, 1920, 1032);
        var top = EdgeFrame.For(ScreenEdge.Top, 1920, 1080, work);
        var bottom = EdgeFrame.For(ScreenEdge.Bottom, 1920, 1080, work);
        var left = EdgeFrame.For(ScreenEdge.Left, 1920, 1080, work);
        var right = EdgeFrame.For(ScreenEdge.Right, 1920, 1080, work);
        check("Top: the frame is the monitor, unchanged", top.Monitor == new DipRect(0, 0, 1920, 1080) && top.FromScreen(work) == work && !top.Across);
        check("Bottom: the frame starts at the taskbar's top and runs up from it",
            bottom.Line == 1032 && bottom.Monitor == new DipRect(0, 0, 1920, 1032) && bottom.FromScreen(work) == new DipRect(0, 0, 1920, 1032)
            && bottom.ToScreen(new DipRect(841, 0, 238, 32)) == new DipRect(841, 1000, 238, 32));
        check("Left and right: the frame runs down the edge, as long as the screen is high",
            left.Across && right.Across && left.Monitor == new DipRect(0, 0, 1080, 1920) && right.Monitor == new DipRect(0, 0, 1080, 1920)
            && left.FromScreen(work) == new DipRect(0, 0, 1032, 1920) && right.FromScreen(work) == new DipRect(0, 0, 1032, 1920));
        check("Left: a notch at the frame's top hangs from the screen's left edge",
            left.ToScreen(new DipRect(400, 0, 238, 56)) == new DipRect(0, 400, 56, 238));
        check("Right: a notch at the frame's top hangs from the screen's right edge",
            right.ToScreen(new DipRect(400, 0, 238, 56)) == new DipRect(1864, 400, 56, 238));
        var sample = new DipRect(120, 30, 300, 90);
        check("Screen and frame rectangles map back to themselves on every edge",
            new[] { top, bottom, left, right }.All(frame => frame.ToScreen(frame.FromScreen(sample)) == sample && frame.FromScreen(frame.ToScreen(sample)) == sample));
        check("Movements map like the rectangles: away from the edge is +y in the frame",
            bottom.FromScreen(0, -10) == (0, 10) && left.FromScreen(10, 0) == (0, 10) && right.FromScreen(-10, 0) == (0, 10) && top.FromScreen(3, 4) == (3, 4)
            && new[] { top, bottom, left, right }.All(frame => frame.ToScreen(frame.FromScreen(5, -7).X, frame.FromScreen(5, -7).Y) == (5, -7)));
        static (double X, double Y) Apply((double M11, double M12, double M21, double M22) m, double x, double y) => (x * m.M11 + y * m.M21, x * m.M12 + y * m.M22);
        check("The matrix turns frame directions into screen directions, and Upright turns them back",
            new[] { top, bottom, left, right }.All(frame => Apply(frame.Matrix, 2, 3) == frame.ToScreen(2, 3)
                && Apply(frame.Upright, Apply(frame.Matrix, 2, 3).X, Apply(frame.Matrix, 2, 3).Y) == (2, 3)));
        // A taskbar at the left: the left frame starts at its right edge.
        var leftBar = EdgeFrame.For(ScreenEdge.Left, 1920, 1080, new DipRect(60, 0, 1860, 1080));
        check("A side taskbar moves the frame's edge to the work area", leftBar.Line == 60 && leftBar.Monitor == new DipRect(0, 0, 1080, 1860)
            && leftBar.ToScreen(new DipRect(0, 0, 10, 20)) == new DipRect(60, 0, 20, 10));
        check("A missing work area uses the monitor's edge; an unknown edge is the top",
            EdgeFrame.For(ScreenEdge.Bottom, 1920, 1080, new DipRect(double.NaN, 0, 0, 0)).Line == 1080
            && EdgeFrame.For((ScreenEdge)9, 1920, 1080, work).Edge == ScreenEdge.Top);

        // The side notch: its depth fits the column, its scale stays its height's.
        var settings = new DeckSettings { NotchWidth = 300, NotchHeight = 32 };
        var frameMonitor = left.Monitor; var frameWork = left.FromScreen(work);
        var side = CollapsedGeometry.Resolve(settings, CompactStyle.Notch, frameMonitor, frameWork, column: 44);
        var thin = CollapsedGeometry.Resolve(settings, CompactStyle.Notch, frameMonitor, frameWork, column: 10);
        var flat = CollapsedGeometry.Resolve(settings, CompactStyle.Notch, frameMonitor, frameWork);
        check("A side notch is as deep as its column with 8 DIP either side, never less than its height, at its height's scale",
            side.Surface.Height == 60 && side.Scale == 1 && side.Surface.Width == 300 && side.Surface.Y == 0
            && thin.Surface.Height == 32 && flat.Surface.Height == 32);
        var big = CollapsedGeometry.Resolve(new DeckSettings { NotchWidth = 300, NotchHeight = 48 }, CompactStyle.Notch, frameMonitor, frameWork, column: 66);
        check("At a larger height the pads grow with the scale", big.Scale == 1.5 && big.Surface.Height == 66 + 24);
        check("A column wider than the monitor's depth stops at it",
            CollapsedGeometry.Resolve(settings, CompactStyle.Notch, new DipRect(0, 0, 1080, 50), new DipRect(0, 0, 1080, 50), column: 400).Surface.Height == 50);

        // The column itself.
        check("The column is as wide as its widest fixed part, at least 36, the readings up to 84",
            CompactColumn.Width(1, [20, 33.2], []) == 36 && CompactColumn.Width(1, [50.4], [30]) == 51 && CompactColumn.Width(1, [20], [120]) == 84
            && CompactColumn.Width(1, [20], [78.3]) == 79
            && CompactColumn.Width(2, [20], []) == 72 && CompactColumn.Width(1, [double.NaN], [double.PositiveInfinity]) == 36);
        check("Rings stand side by side as far as the column allows, at least one per line",
            CompactColumn.Rings(5, 12, 6, 44) == (2, 3) && CompactColumn.Rings(3, 12, 6, 48) == (3, 1) && CompactColumn.Rings(2, 12, 6, 5) == (1, 2)
            && CompactColumn.Rings(0, 12, 6, 44) == (0, 0));
        var stack = CompactColumn.Stack([20, 24, 12], 8);
        check("Parts stack with the gap between them", stack.Tops.SequenceEqual([0d, 28, 60]) && stack.Length == 72 && CompactColumn.Stack([], 8).Length == 0);
        check("A column that fits is centred; a longer one glides by what does not fit",
            CompactColumn.Place(72, 100) == (14, 0) && CompactColumn.Place(130, 100) == (0, 30) && CompactColumn.Place(double.NaN, 10) == (5, 0));
    }
}

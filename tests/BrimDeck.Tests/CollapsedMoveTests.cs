using BrimDeck.Core;

// Moving the collapsed notch or capsule by dragging it (CollapsedMove): which edge the notch takes, where the surfaces are held,
// and the settings that put them back where they were dropped.
internal static class CollapsedMoveTests
{
    internal static void Run(Action<string, bool> check)
    {
        // A 1920 x 1080 monitor with a 48 DIP taskbar at the bottom.
        var work = new DipRect(0, 0, 1920, 1032);
        const double W = 1920, H = 1080;

        check("The notch hangs from the edge nearest the pointer, the bottom one at the taskbar's top",
            CollapsedMove.NearestEdge(900, 10, W, H, ScreenEdge.Top) == ScreenEdge.Top
            && CollapsedMove.NearestEdge(900, 1040, W, H, ScreenEdge.Top) == ScreenEdge.Bottom
            && CollapsedMove.NearestEdge(5, 500, W, H, ScreenEdge.Top) == ScreenEdge.Left
            && CollapsedMove.NearestEdge(1915, 500, W, H, ScreenEdge.Bottom) == ScreenEdge.Right);
        check("It keeps its edge until another is nearer by the hysteresis",
            CollapsedMove.NearestEdge(110, 140, W, H, ScreenEdge.Top) == ScreenEdge.Top
            && CollapsedMove.NearestEdge(140, 110, W, H, ScreenEdge.Left) == ScreenEdge.Left
            && CollapsedMove.NearestEdge(60, 140, W, H, ScreenEdge.Top) == ScreenEdge.Left);

        var (centre, held) = CollapsedMove.NotchAlong(965, 238, W);
        check("Along an edge the notch is held at its middle within reach", held && centre == 960);
        (centre, held) = CollapsedMove.NotchAlong(990, 238, W);
        check("Out of reach it follows the pointer", !held && centre == 990);
        (centre, _) = CollapsedMove.NotchAlong(-50, 238, W);
        var (end, _) = CollapsedMove.NotchAlong(5000, 238, H);
        check("It stops at the ends of the edge", centre == 119 && end == H - 119);
        check("A notch longer than the edge stays at its middle", CollapsedMove.NotchAlong(10, 2000, W).Centre == 960);

        double offset = CollapsedMove.NotchOffset(400, CollapsedMove.Span(ScreenEdge.Left, W, H));
        var settings = new DeckSettings { NotchEdge = ScreenEdge.Left, NotchOffsetX = offset, NotchAutoWidth = false };
        var frame = EdgeFrame.For(ScreenEdge.Left, W, H, work);
        var shape = CollapsedGeometry.Resolve(settings, CompactStyle.Notch, frame.Monitor, frame.FromScreen(work), column: 40);
        check("The saved offset puts the notch's centre where it was dropped, on a side edge too",
            Math.Abs(shape.Surface.X + shape.Surface.Width / 2 - 400) < 1e-9 && frame.ToScreen(shape.Surface).X == 0);

        check("Over a taskbar the surface hangs from the screen's edge, elsewhere from the taskbar's; an edge without one keeps the choice",
            CollapsedMove.AnchorAt(ScreenEdge.Bottom, 500, 1050, W, H, work) == EdgeAnchor.Screen
            && CollapsedMove.AnchorAt(ScreenEdge.Bottom, 500, 1020, W, H, work) == EdgeAnchor.Taskbar
            && CollapsedMove.AnchorAt(ScreenEdge.Top, 500, 5, W, H, work) is null
            && CollapsedMove.Line(ScreenEdge.Bottom, true, W, H, work) == H && CollapsedMove.Line(ScreenEdge.Bottom, false, W, H, work) == 1032);
        var topBarWork = new DipRect(0, 48, 1920, 1032);
        var under = EdgeFrame.For(ScreenEdge.Top, W, H, topBarWork, overTaskbar: false);
        check("At the top the frame can start below a top taskbar, and still starts at the screen's edge unless told otherwise",
            under.Line == 48 && under.Monitor == new DipRect(0, 0, W, H - 48) && under.ToScreen(new DipRect(841, 0, 238, 32)) == new DipRect(841, 48, 238, 32)
            && under.FromScreen(topBarWork) == new DipRect(0, 0, 1920, 1032) && EdgeFrame.For(ScreenEdge.Top, W, H, topBarWork).Line == 0);
        var leftBar = new DipRect(60, 0, 1860, 1032);
        check("The line moves within the work area's part of an edge, or the whole edge over the taskbar; the notch always the whole edge",
            CollapsedMove.AlongRange(CompactStyle.Line, ScreenEdge.Top, false, W, H, leftBar) == (60, 1920)
            && CollapsedMove.AlongRange(CompactStyle.Line, ScreenEdge.Left, false, W, H, leftBar) == (0, 1032)
            && CollapsedMove.AlongRange(CompactStyle.Line, ScreenEdge.Top, true, W, H, leftBar) == (0, 1920)
            && CollapsedMove.AlongRange(CompactStyle.Notch, ScreenEdge.Top, false, W, H, leftBar) == (0, 1920));
        double lineOffset = CollapsedMove.Offset(300, 60, 1920);
        var line = CollapsedGeometry.Resolve(new DeckSettings { LineOffsetX = lineOffset, LineLength = 56 }, CompactStyle.Line, new DipRect(0, 0, W, H), leftBar);
        check("The saved offset puts the line's centre where it was dropped", Math.Abs(line.Surface.X + line.Surface.Width / 2 - 300) < 1e-9);
        var over = CollapsedGeometry.Resolve(new DeckSettings { LineTop = 4 }, CompactStyle.Line, new DipRect(0, 0, W, H), topBarWork, lineOverTaskbar: true);
        check("A line over the taskbar is measured from the screen's edge", over.Surface.Y == 4);

        var capsule = CollapsedMove.Capsule(865, 300, 196, 32, H, work);
        check("The capsule is held on the work area's middle, with a vertical guide there", capsule.X == 862 && capsule.GuideX == 960 && capsule.GuideY is null);
        capsule = CollapsedMove.Capsule(700, 9, 196, 32, H, work);
        check("It is held at its usual distance from the top", capsule.Y == CollapsedMove.Gap && capsule.GuideY == 0 && capsule.GuideX is null);
        capsule = CollapsedMove.Capsule(700, 1032 - 32 - 8, 196, 32, H, work);
        check("Above the taskbar it is held just clear of its top", capsule.Y == 1032 - 32 - CollapsedMove.Gap && capsule.GuideY == 1032);
        capsule = CollapsedMove.Capsule(700, 2000, 196, 32, H, work);
        check("Over the taskbar it goes down to the monitor's bottom and is held clear of it", capsule.Y == H - 32 - CollapsedMove.Gap && capsule.GuideY == H);
        capsule = CollapsedMove.Capsule(-40, 400, 196, 32, H, work);
        var right = CollapsedMove.Capsule(5000, 400, 196, 32, H, work);
        check("At the work area's sides it keeps the same gap, with no guide", capsule.X == CollapsedMove.Gap && right.X == 1920 - 196 - CollapsedMove.Gap && capsule.GuideX is null);
        capsule = CollapsedMove.Capsule(500, 506, 196, 32, H, work);
        check("It is held on the work area's middle down the screen, with a horizontal guide there", capsule.Y == 500 && capsule.GuideY == 516);
        capsule = CollapsedMove.Capsule(500, 600, 196, 32, H, work);
        check("Elsewhere it follows the pointer", capsule.X == 500 && capsule.Y == 600 && capsule.GuideX is null && capsule.GuideY is null);
        var topBar = new DipRect(0, 48, 1920, 1032);
        check("Below a top taskbar it cannot go above the work area",
            CollapsedMove.Capsule(500, 0, 196, 32, H, topBar).Y == 48 + CollapsedMove.Gap && CollapsedMove.Capsule(500, 300, 196, 32, H, topBar).Y == 300);

        var (offsetX, top) = CollapsedMove.CapsulePlace(500, 600, 196, work);
        var placed = CollapsedGeometry.Resolve(new DeckSettings { CapsuleOffsetX = offsetX, CapsuleTop = top, CapsuleWidth = 196, CapsuleAutoWidth = false }, CompactStyle.Capsule, new DipRect(0, 0, W, H), work);
        check("The saved offset and top put the capsule where it was dropped", placed.Surface.X == 500 && placed.Surface.Y == 600);
        var low = CollapsedMove.Capsule(700, 2000, 196, 32, H, work);
        (offsetX, top) = CollapsedMove.CapsulePlace(low.X, low.Y, 196, work);
        placed = CollapsedGeometry.Resolve(new DeckSettings { CapsuleOffsetX = offsetX, CapsuleTop = top, CapsuleWidth = 196, CapsuleAutoWidth = false }, CompactStyle.Capsule, new DipRect(0, 0, W, H), work);
        check("A capsule dropped over the taskbar stays there", placed.Surface.Y == low.Y);
    }
}

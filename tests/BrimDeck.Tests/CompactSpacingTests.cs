using BrimDeck.Core;

// The collapsed row's spacing (CompactSpacing): one gap between the parts, spare width to the title, then those gaps, then both
// ends; the carousels' windows of whole items and how the rings and the readings share their room; and the narrowest surface,
// which holds what the row must show.
internal static class CompactSpacingTests
{
    internal static void Run(Action<string, bool> check)
    {
        Arrange(check);
        Windows(check);
        Share(check);
        Required(check);
    }

    private static bool Even(CompactSpacing.Placement place, double width, double dpi)
    {
        int n = place.Left.Length;
        bool whole = place.Left.Concat(place.Room).Append(place.Gap).All(v => Math.Abs(v * dpi - Math.Round(v * dpi)) < 1e-6);
        bool even = Enumerable.Range(0, Math.Max(0, n - 1)).All(i => Math.Abs(place.Left[i + 1] - place.Left[i] - place.Room[i] - place.Gap) < 1e-6);
        bool inside = n == 0 || place.Left[0] >= -1e-9 && place.Left[^1] + place.Room[^1] <= width + 1e-6;
        return whole && even && inside;
    }

    private static void Arrange(Action<string, bool> check)
    {
        // Cover, title, three rings (their own spacing inside), equalizer and clock.
        CompactSpacing.Part[] parts = [new(20), new(0), new(52), new(18), new(40)];
        var place = CompactSpacing.Arrange(parts, 1, 100, 18, 400, 8, 1, 1);
        check("Compact spacing: every gap between two parts is the same", Even(place, 400, 1));
        check("Compact spacing: a short title takes its text, the gaps between parts widen to twice, the rings keep their spacing, and the rest is split at both ends",
            place.Room[1] == 100 && place.Gap == 16 && place.Left[0] == 53 && 400 - place.Left[^1] - place.Room[^1] == 53 && place.Room[2] == 52);
        var shortRow = CompactSpacing.Arrange(parts, 1, 100, 18, 260, 8, 1, 1);
        check("Compact spacing: the title comes before the gaps", shortRow.Room[1] == 98 && shortRow.Gap == 8 && shortRow.Left[0] == 0 && Even(shortRow, 260, 1));
        var longTitle = CompactSpacing.Arrange(parts, 1, 500, 18, 400, 8, 1, 1);
        check("Compact spacing: a long title takes what the widest gaps leave", longTitle.Room[1] == 206 && longTitle.Gap == 16 && longTitle.Left[0] == 0
            && Math.Abs(longTitle.Left[^1] + longTitle.Room[^1] - 400) < 1e-9);
        var noTitle = CompactSpacing.Arrange([new(84), new(40)], -1, 0, 0, 300, 8, 1, 1);
        check("Compact spacing: without a title the readings and the clock move apart, then centre; the readings keep their width",
            noTitle.Gap == 16 && Even(noTitle, 300, 1) && noTitle.Room[0] == 84 && Math.Abs(noTitle.Left[0] - (300 - (84 + 16 + 40)) / 2) <= .5);
        var fraction = CompactSpacing.Arrange([new(97), new(40)], -1, 0, 0, 300, 8, 1, 1.25);
        check("Compact spacing: a part's room is rounded up to whole device pixels, so nothing in it is cut", fraction.Room[0] * 1.25 >= 97 * 1.25 - 1e-6);
        check("Compact spacing: one part alone is centred", CompactSpacing.Arrange([new(40)], -1, 0, 0, 100, 8, 1, 1) is { } alone && alone.Left[0] == 30 && alone.Room[0] == 40);
        bool scaled = true;
        foreach (double dpi in new[] { 1, 1.25, 1.5, 1.75, 2 })
            for (double width = 120; width <= 700; width += 7)
            {
                var p = CompactSpacing.Arrange(parts, 1, 230, 18, width, 8, 1, dpi);
                scaled &= Even(p, width, dpi) && p.Room[1] >= Math.Min(18, Math.Floor(width * dpi) / dpi) - 1e-9 || width < 160;
            }
        check("Compact spacing: on whole device pixels at every scale, the gaps stay equal and the row stays inside", scaled);
        var tight = CompactSpacing.Arrange(parts, 1, 100, 18, 150, 8, 1, 1);
        check("Compact spacing: a row too short for its gaps narrows them alike and keeps the shortest title", Even(tight, 150, 1) && tight.Room[1] >= 18 && tight.Gap < 8);
        // A lyric that follows its line: the surface is as wide as the line's room, and the line gets all of it before the gaps widen.
        CompactSpacing.Part[] lyricRow = [new(20), new(0), new(18)];
        var line = CompactSpacing.Arrange(lyricRow, 1, 300, 18, 354, 8, 1, 1, 300);
        var fixedFirst = CompactSpacing.Arrange(lyricRow, 1, 300, 18, 354, 8, 1, 1);
        check("Compact spacing: a lyric given its line's room takes it before the gaps widen", line.Room[1] == 300 && line.Gap == 8 && Even(line, 354, 1)
            && fixedFirst.Room[1] == 284 && fixedFirst.Gap == 16);
    }

    private static void Windows(Action<string, bool> check)
    {
        double[] widths = [30, 40, 20, 50];
        check("Compact carousel: the window holds whole items from the first, and the strip moves to the item from which the rest fits",
            CompactSpacing.Window(widths, 8, 100) == (2, 78, 1, 86) && CompactSpacing.Window(widths, 8, 164) == (4, 164, 3, 0)
            && CompactSpacing.Window(widths, 8, 29) == (0, 0, 0, 0) && CompactSpacing.Window([], 8, 100) == (0, 0, 0, 0) && CompactSpacing.Window(widths, 8, double.NaN) == (0, 0, 0, 0));
        check("Compact carousel: a last item a little wider than the first ones widens the window to it; a span too short to show the last item whole shows nothing",
            CompactSpacing.Window([30, 40, 20, 36], 8, 60) == (1, 36, 0, 30 + 8 + 40 + 8 + 20 + 8) && CompactSpacing.Window(widths, 8, 60) == (0, 0, 0, 0)
            && CompactSpacing.Window([30, 50, 20, 20], 8, 40) == (1, 30, 0, 30 + 8 + 50 + 8 + 20 + 8));
        check("Compact carousel: the narrowest window shows the first item and, at the other rest, the last",
            CompactSpacing.Minimum(widths, 8) == 78 && CompactSpacing.Minimum([30, 40, 20, 36], 8) == 36 && CompactSpacing.Minimum([30, 50, 20, 20], 8) == 30
            && CompactSpacing.Minimum([12, 12, 12], 8) == 12 && CompactSpacing.Minimum([50], 8) == 50 && CompactSpacing.Minimum([], 8) == 0);
        check("Compact carousel: the travel follows a wider gap", CompactSpacing.Travel(widths, 16, 30 + 16 + 40) == 30 + 16 + 40 + 16 && CompactSpacing.Travel(widths, 8, 164) == 0);
        // At both rests every item is wholly inside the window or wholly outside it.
        var random = new Random(86);
        bool whole = true;
        for (int round = 0; round < 2000; round++)
        {
            var items = Enumerable.Range(0, random.Next(1, 9)).Select(_ => (double)random.Next(8, 70)).ToArray();
            double gap = random.Next(4, 17), span = random.Next(0, 400);
            var (count, window, _, travel) = CompactSpacing.Window(items, gap, span);
            var lefts = items.Select((_, i) => items.Take(i).Sum() + i * gap).ToArray();
            bool Rest(double shift) => lefts.Select((x, i) => (x - shift, x - shift + items[i])).All(item => item.Item2 <= 1e-6 || item.Item1 >= window - 1e-6 || item.Item1 >= -1e-6 && item.Item2 <= window + 1e-6);
            // What shows at the two rests reaches the last item, so every item shows over the cycle.
            bool last = count == 0 || count == items.Length || lefts[^1] - travel + items[^1] <= window + 1e-6;
            double minimum = CompactSpacing.Minimum(items, gap);
            bool narrowest = (span < minimum - 1e-6) == (count == 0) && CompactSpacing.Window(items, gap, minimum).Window == minimum;
            whole &= window <= span + 1e-6 && Rest(0) && Rest(travel) && (travel == 0 || lefts.Contains(travel)) && last && narrowest;
        }
        check("Compact carousel: no item is cut at either rest, the last item shows at the second, and Minimum is the narrowest such window", whole);
    }

    private static void Share(Action<string, bool> check)
    {
        double[] rings = [12, 12, 12, 12], readings = [42, 50, 42, 45, 45, 70];
        var tight = CompactSpacing.Share(rings, 8, readings, 6, 12 + 98);
        check("Compact share: at the narrowest both carousels show their narrowest window", tight.First == (1, 12, 0, 60) && tight.Second == (2, 98, 1, 254));
        var some = CompactSpacing.Share(rings, 8, readings, 6, 200);
        check("Compact share: short of room the rings do not take all theirs before the readings widen; both scroll the rest",
            some.First.Count == 3 && some.First.Travel > 0 && some.Second.Count == 3 && some.Second.Travel > 0 && some.First.Window + some.Second.Window <= 200);
        var all = CompactSpacing.Share(rings, 8, readings, 6, 72 + 324);
        check("Compact share: with room for everything nothing scrolls", all.First == (4, 72, 3, 0) && all.Second == (6, 324, 5, 0));
        check("Compact share: too short for both, the first alone at its narrowest, else the second, else none",
            CompactSpacing.Share(rings, 8, readings, 6, 50).First.Count == 1 && CompactSpacing.Share(rings, 8, readings, 6, 50).Second.Count == 0
            && CompactSpacing.Share([], 8, readings, 6, 100).Second == (2, 98, 1, 254) && CompactSpacing.Share(rings, 8, readings, 6, 0) == ((0, 0, 0, 0), (0, 0, 0, 0)));
        // Every width from nothing to everything: the two windows fit the room, both show once it holds their narrowest
        // windows, and each that shows any scrolls through the rest. Once both show, a wider room never shows fewer of either.
        bool steady = true, growing = true;
        int lastA = 0, lastB = 0;
        for (double room = 0; room <= 420; room++)
        {
            var (a, b) = CompactSpacing.Share(rings, 8, readings, 6, room);
            steady &= a.Window + b.Window <= room + 1e-6 && (room < 110 || a.Count > 0 && b.Count > 0)
                && (a.Count is 0 or 4 || a.Travel > 0) && (b.Count is 0 or 6 || b.Travel > 0);
            if (room >= 110) { growing &= a.Count >= lastA && b.Count >= lastB; lastA = a.Count; lastB = b.Count; }
        }
        check("Compact share: both show once they can, fit the room, and scroll what they do not hold", steady);
        check("Compact share: a wider room never shows fewer rings or fewer readings", growing);
    }

    private static void Required(Action<string, bool> check)
    {
        check("Compact required: the clock, and while media plays the cover, equalizer and shortest title, with their gaps",
            CompactMusicLayout.RequiredWidth(1, true, true, 40) == 20 + 8 + 18 + 8 + 18 + 8 + 40 && CompactMusicLayout.RequiredWidth(1, false, false, 40) == 40
            && CompactMusicLayout.RequiredWidth(1, true, false, 0) == 20 + 8 + 18 && CompactMusicLayout.RequiredWidth(1, false, true, 0) == 0
            && CompactMusicLayout.RequiredWidth(2, true, true, 80) == 2 * (20 + 8 + 18 + 8 + 18 + 8) + 80);
        check("Compact required: a clock alone, or rings or readings alone, need only themselves; one ring and the readings' narrowest window join what else shows",
            CompactMusicLayout.RequiredWidth(1, false, false, 40, 0, 56) == 56 + 8 + 40 && CompactMusicLayout.RequiredWidth(1, false, false, 0, 12, 0) == 12
            && CompactMusicLayout.RequiredWidth(1, false, false, 0, 12, 56) == 12 + 8 + 56
            && CompactMusicLayout.RequiredWidth(1, false, false, 0, 0, 56) == 56 && CompactMusicLayout.RequiredWidth(1, false, false, 0) == 0
            && CompactMusicLayout.RequiredWidth(1, false, false, 40, 12, 56) == 12 + 8 + 56 + 8 + 40
            && CompactMusicLayout.RequiredWidth(1, true, true, 0, 12, 56) == 20 + 8 + 18 + 8 + 18 + 8 + 12 + 8 + 56
            && CompactMusicLayout.RequiredWidth(1, false, false, 31.83) == 32);
        var ringOnly = new DeckSettings { CapsuleWidth = 48, NotchWidth = 72 };
        var screenAll = new DipRect(0, 0, 1920, 1080); var areaAll = new DipRect(0, 0, 1920, 1032);
        check("Compact required: a short surface for a clock alone or a ring alone",
            CollapsedGeometry.Resolve(ringOnly, CompactStyle.Capsule, screenAll, areaAll, 1, null, 40).Surface.Width == 72
            && CollapsedGeometry.Resolve(ringOnly, CompactStyle.Capsule, screenAll, areaAll, 1, null, 12).Surface.Width == 48
            && CollapsedGeometry.Resolve(ringOnly, CompactStyle.Notch, screenAll, areaAll, 1, null, 40).Surface.Width == 100
            && CollapsedGeometry.Resolve(ringOnly, CompactStyle.Notch, screenAll, areaAll, 1, null, 12).Surface.Width == 72);
        var layout = CompactMusicLayout.Calculate(CompactMusicLayout.RequiredWidth(1, true, true, 40), 1, true, 3, true, 40, 120);
        check("Compact required: at that width nothing the row must show is cut", layout.CoverWidth == 28 && layout.EqualizerWidth == 26 && layout.ClockWidth == 48
            && layout.TextWidth == 18 && layout.MonitorWidth == 0 && layout.RingWidth == 0);
        var screen = new DipRect(0, 0, 1920, 1080); var area = new DipRect(0, 0, 1920, 1032);
        var capsule = new DeckSettings { CapsuleWidth = 140, CapsuleHeight = 32 };
        check("Compact required: the surface grows to what it must show, never below the saved width",
            CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, null, 152).Surface.Width == 184
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, null, 50).Surface.Width == 140
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, 10, 152).Surface.Width == 184
            && CollapsedGeometry.Resolve(new DeckSettings { NotchHeight = 24 }, CompactStyle.Notch, screen, area, 1, null, 300).Surface.Width == 356
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Line, screen, area, 1, null, 300).Surface.Width == CollapsedLimits.LineLengthDefault
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area).Surface.Width == 140);
        check("Compact required: the height the surface takes, within its range and the screen or work area",
            CollapsedGeometry.SurfaceHeight(CompactStyle.Notch, 64, new DipRect(0, 0, 800, 40), new DipRect(0, 0, 800, 30)) == 40
            && CollapsedGeometry.SurfaceHeight(CompactStyle.Capsule, 64, new DipRect(0, 0, 800, 40), new DipRect(0, 0, 800, 30)) == 30
            && CollapsedGeometry.SurfaceHeight(CompactStyle.Notch, 10, screen, area) == CollapsedLimits.NotchHeightMin
            && CollapsedGeometry.SurfaceHeight(CompactStyle.Capsule, 40, screen, area) == 40);
        check("Compact required: surface width for a content width", CollapsedGeometry.SurfaceWidth(CompactStyle.Capsule, 32, 100) == 132
            && CollapsedGeometry.SurfaceWidth(CompactStyle.Notch, 32, 100) == 160 && CollapsedGeometry.SurfaceWidth(CompactStyle.Notch, 24, 100) == 156
            && CollapsedGeometry.SurfaceWidth(CompactStyle.Line, 4, 100) == 0 && CollapsedGeometry.SurfaceWidth(CompactStyle.Capsule, 32, 0) == 0
            && CollapsedGeometry.SurfaceWidth(CompactStyle.Capsule, 32, double.NaN) == 0);
        // At 150 % the shoulders and corners are 42 DIP, wider than the 33.75 DIP inset of a 24 DIP notch drawn at that scale.
        check("Compact required: the notch's shoulders and corners follow the scale", CollapsedGeometry.SurfaceWidth(CompactStyle.Notch, 36, 100, 1.5) == 184
            && CollapsedGeometry.SurfaceWidth(CompactStyle.Notch, 36, 100) == 167.5);
        // The content area the window measures from the shape (MainWindow.CompactContentWidth) holds what the content asked for, so an
        // auto-length column on a side edge does not glide by the difference.
        bool holds = true;
        foreach (double scale in new[] { 1, 1.5 })
            foreach (double height in new[] { 24d, 27, 28, 32, 45 })
            {
                var shape = CollapsedGeometry.Resolve(new DeckSettings { NotchHeight = height, NotchAutoWidth = true }, CompactStyle.Notch,
                    new DipRect(0, 0, 1152, 2048), new DipRect(0, 0, 1152, 2048), 1, 300, 0, scale, 60);
                holds &= Math.Min(shape.ContentWidth, shape.Surface.Width - 2 * (shape.Shoulder + shape.BottomRadius)) >= 300 - 1e-9;
            }
        check("Compact required: an auto-width notch's content area holds the content at every scale and height", holds);
    }
}

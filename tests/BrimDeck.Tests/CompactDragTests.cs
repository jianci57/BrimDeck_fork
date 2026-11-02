using BrimDeck.Core;

// The collapsed row while its surface is dragged (CompactSpacing.ShareFlowing, CutFade): the windows of the rings and the readings
// follow the room one for one instead of stepping by whole items, so the row moves with the pointer, as 1.2.1's did, and what
// jumped (a window by an item, the gaps and the centring taking the difference back) does not. At the rooms where the whole-item
// windows stand (Share), the flowing ones are the same.
internal static class CompactDragTests
{
    private static readonly double[] Rings = [12, 12, 12, 12];
    private static readonly double[] Readings = [48, 58, 52, 50, 58, 52, 56];
    private const double RingGap = 8, ReadingGap = 10;

    internal static void Run(Action<string, bool> check)
    {
        Flowing(check);
        Cut(check);
        Drag(check);
        Entries(check);
    }

    // The ways the collapsed width changes, as sequences of frames. What the user sees is where the readings' window starts and ends
    // and where the clock begins. A frame's jump is how far the window grew or shrank beyond what the surface itself moved that frame,
    // or how far the readings' start or the clock moved: the row jumping or centring again.
    // The row at a width, the flow (eased) and the window the rest had before the flow rose (rising: the flow is held or rising, else it falls).
    private static (double Left, double Window, double Clock, double Whole) Row(double width, double flow, double rest, bool rising)
    {
        double clock = 36, unit = 8, pool = Math.Max(0, width - clock - unit);
        double whole = CompactSpacing.Share([], RingGap, Readings, ReadingGap, pool).Second.Window;
        double flowing = CompactSpacing.ShareFlowing([], RingGap, Readings, ReadingGap, pool).Second.Window;
        double window = flow <= 0 ? whole : RowFlow.Window(rising && rest >= 0 ? rest : whole, flowing, flow);
        var place = CompactSpacing.Arrange([new(window), new(clock)], -1, 0, 0, width, unit, 1, 1);
        return (place.Left[0], window, place.Left[1], whole);
    }

    // seconds of frames at fps. surface(t): the width then (called once per frame, in order); dragging(t): the surface is dragged;
    // changes: when the settings changed the width.
    private static (double Worst, int Jerks, double FlowAtEnd) Run(double seconds, double fps, Func<double, double> surface, Func<double, bool> dragging, double[] changes)
    {
        var flow = new RowFlow();
        double frame = 1 / fps, worst = 0, previousWidth = Math.Round(surface(0));
        int jerks = 0, next = 0;
        var previous = Row(previousWidth, 0, -1, false);
        double rest = previous.Whole;
        bool held = true;
        for (double t = frame; t <= seconds; t += frame)
        {
            double width = Math.Round(surface(t));
            while (next < changes.Length && changes[next] <= t) { flow.NoteChange(changes[next]); next++; }
            bool drag = dragging(t);
            flow.Advance(t, drag, true);
            bool rising = flow.Target(drag) > 0, active = flow.Eased > 0;
            if (active) { if (!rising) held = false; else if (!held) { rest = Row(width, 0, -1, false).Whole; held = true; } }
            var row = Row(width, active ? flow.Eased : 0, rest, rising && held);
            if (!active) { rest = row.Whole; held = true; }
            double moved = Math.Abs(width - previousWidth);
            double jump = Math.Max(Math.Max(Math.Abs(row.Window - previous.Window) - moved, Math.Abs(row.Left - previous.Left)), Math.Abs(row.Clock - previous.Clock) - moved);
            worst = Math.Max(worst, jump); if (jump > 3) jerks++;
            previous = row; previousWidth = width;
        }
        return (worst, jerks, flow.Value);
    }

    private static void Entries(Action<string, bool> check)
    {
        foreach (double fps in new[] { 144.0, 60.0 })
        {
            double limit = fps > 100 ? 3 : 6.5;
            // The pointer or the resize shortcut: the surface follows a ramp on the drag's spring and is dragged until it rests.
            var spring = new Spring(220);
            var drag = Run(3.5, fps, t => { spring.Target = 220 + 360 * Math.Clamp((t - .1) / 1.2, 0, 1); spring.Step(1 / fps); return spring.Value; },
                t => t >= .1 && (t < 1.4 || spring.Value != spring.Target), []);
            check($"Entries ({fps:0} fps): dragging the surface moves no frame's row by more than {limit} DIPs beyond the surface's own move", drag.Worst <= limit);

            // The settings slider: a value every 16 ms, 6 DIPs at a time, then 1 DIP every 45 ms; then left alone.
            foreach (var (name, step, every) in new[] { ("quick slider", 6.0, .016), ("slow slider", 1.0, .045) })
            {
                int count = (int)(420 / step);
                double[] changes = Enumerable.Range(0, count + 1).Select(k => k * every).ToArray();
                double Width(double t) => 150 + Math.Min(Math.Floor(t / every + 1e-9) * step, 420);
                double seconds = count * every + 1.5;
                var flowing = Run(seconds, fps, Width, _ => false, changes);
                // A value every 16 ms moves the surface 6 DIPs in some frames and none in the next: the row follows in the same lumps.
                double sliderLimit = limit + 2;
                check($"Entries ({fps:0} fps): the {name} moves no frame's row by more than {sliderLimit} DIPs beyond the surface's own move, and the flow is back to 0 after", flowing.Worst <= sliderLimit && flowing.FlowAtEnd == 0);
            }

            // A slider dragged again after the last one stopped, while its flow is still falling: the flow rises again from where it is.
            {
                double gap = 1.52, start = 216 + 420;
                double[] first = Enumerable.Range(0, 71).Select(k => k * .016).ToArray();
                double[] second = Enumerable.Range(0, 300).Select(k => gap + k * .045).ToArray();
                double Width(double t) => t < gap ? 216 + Math.Min(Math.Floor(t / .016 + 1e-9) * 6, 420) : start - Math.Min(Math.Floor((t - gap) / .045 + 1e-9), 299);
                var again = Run(gap + 300 * .045 + 1.5, fps, Width, _ => false, [.. first, .. second]);
                check($"Entries ({fps:0} fps): a slider dragged again while the flow of the last one falls moves no frame's row by more than {limit + 2} DIPs beyond the surface's own move", again.Worst <= limit + 2 && again.FlowAtEnd == 0);
            }

            // One change (a number typed, a reset): a new layout, not a motion; the flow stays 0.
            var once = Run(2, fps, t => t < .5 ? 396 : 480, _ => false, [.5]);
            check($"Entries ({fps:0} fps): a width changed once does not flow", once.FlowAtEnd == 0);
        }
        var held = new RowFlow(); held.NoteChange(1);
        check("Entries: the first change of a series holds nothing", !held.Holding);
        check("Entries: the second within a quarter second holds the flow", held.NoteChange(1.1) && held.Holding);
        held.Advance(1.1 + RowFlow.Hold + .01, false, true);
        check("Entries: quiet for a quarter second, the hold ends", !held.Holding);
        var animations = new RowFlow(); animations.NoteChange(0); animations.NoteChange(.1); animations.Advance(.1, false, false);
        check("Entries: without animations the flow is at its target at once", animations.Value == 1);
        animations.Advance(.1 + RowFlow.Hold + .01, false, false);
        check("Entries: without animations it is back at 0 at once when the changes stop", animations.Value == 0 && !animations.Holding);
        var rise = new RowFlow(); rise.Restart(0); rise.Advance(.05, true, true);
        check("Entries: the flow rises over about a third of a second, not at once", rise.Value > 0 && rise.Value < .5);
    }

    private static void Flowing(Action<string, bool> check)
    {
        bool grows = true, continuous = true, fits = true, wider = true, same = true, stepped = false;
        double previousFirst = 0, previousSecond = 0, previousWhole = 0;
        for (double room = 0; room <= 520; room += .5)
        {
            var flowing = CompactSpacing.ShareFlowing(Rings, RingGap, Readings, ReadingGap, room);
            var whole = CompactSpacing.Share(Rings, RingGap, Readings, ReadingGap, room);
            double a = flowing.First.Window, b = flowing.Second.Window;
            grows &= a >= previousFirst - 1e-9 && b >= previousSecond - 1e-9;
            // One for one: together the windows never move by more than the room did.
            continuous &= room == 0 || a + b - previousFirst - previousSecond <= .5 + 1e-9;
            fits &= a + b <= room + 1e-9 && a >= 0 && b >= 0;
            // Wider than the whole-item windows, never narrower.
            wider &= a + b >= whole.First.Window + whole.Second.Window - 1e-9;
            // Where the whole-item windows fill the room exactly, they are the flowing ones.
            if (whole.First.Count > 0 && whole.Second.Count > 0 && Math.Abs(whole.First.Window + whole.Second.Window - room) < 1e-9)
                same &= Math.Abs(a - whole.First.Window) < 1e-9 && Math.Abs(b - whole.Second.Window) < 1e-9;
            stepped |= room > 0 && whole.First.Window + whole.Second.Window - previousWhole > 20;
            previousFirst = a; previousSecond = b; previousWhole = whole.First.Window + whole.Second.Window;
        }
        check("Dragging: the windows only grow as the room grows", grows);
        check("Dragging: the windows grow one for one with the room, never by a step", continuous);
        check("Dragging: the windows fit in the room", fits);
        check("Dragging: the windows are never narrower than the whole-item windows of the rest", wider);
        check("Dragging: where the whole-item windows fill the room exactly, the windows are the same", same);
        check("Dragging: the whole-item windows do step by an item as the room grows (what the flowing windows spare the row)", stepped);
        var none = CompactSpacing.ShareFlowing([], 8, [], 8, 100);
        check("Dragging: nothing to show takes no room", none.First.Window == 0 && none.Second.Window == 0);
        var only = CompactSpacing.ShareFlowing([], 8, Readings, ReadingGap, 90);
        check("Dragging: readings alone take the room", Math.Abs(only.Second.Window - 90) < 1e-9 && only.First.Window == 0);
        var all = CompactSpacing.ShareFlowing(Rings, RingGap, Readings, ReadingGap, 5000);
        double ringsAll = Rings.Sum() + (Rings.Length - 1) * RingGap, readingsAll = Readings.Sum() + (Readings.Length - 1) * ReadingGap;
        check("Dragging: with room to spare every item shows whole", Math.Abs(all.First.Window - ringsAll) < 1e-9 && Math.Abs(all.Second.Window - readingsAll) < 1e-9
            && all.First.Count == Rings.Length && all.Second.Count == Readings.Length);
    }

    private static void Cut(Action<string, bool> check)
    {
        double[] items = [40, 30, 50];
        check("Cut fade: none where the window ends in a gap or at an item's edge", CompactSpacing.CutFade(items, 10, 40, 6) == 0 && CompactSpacing.CutFade(items, 10, 45, 6) == 0
            && CompactSpacing.CutFade(items, 10, 80, 6) == 0 && CompactSpacing.CutFade(items, 10, 0, 6) == 0);
        check("Cut fade: the width of the cut item's part shown or hidden when that is less than the edge",
            Math.Abs(CompactSpacing.CutFade(items, 10, 53, 6) - 3) < 1e-9 && Math.Abs(CompactSpacing.CutFade(items, 10, 78, 6) - 2) < 1e-9);
        check("Cut fade: the edge's width in the middle of an item", Math.Abs(CompactSpacing.CutFade(items, 10, 70, 6) - 6) < 1e-9);
        double worst = 0, last = 0;
        for (double window = 0; window <= 190; window += .25) { double fade = CompactSpacing.CutFade(items, 10, window, 6); worst = Math.Max(worst, Math.Abs(fade - last)); last = fade; }
        check("Cut fade: it never appears or goes in a step, only as fast as the window moves", worst <= .25 + 1e-9);
    }

    // The collapsed row of readings and the clock as the surface is dragged: the surface follows a ramp on the drag's spring, the
    // readings take the room that is left, and the clock stands at the row's end. What the user sees is where the readings' window ends and
    // where the clock starts, frame by frame.
    private static void Drag(Action<string, bool> check)
    {
        double clock = 36, unit = 8;
        (double ReadingsRight, double ClockLeft) Row(double width)
        {
            double pool = Math.Max(0, width - clock - unit);
            var shared = CompactSpacing.ShareFlowing([], RingGap, Readings, ReadingGap, pool).Second;
            var place = CompactSpacing.Arrange([new(shared.Window), new(clock)], -1, 0, 0, width, unit, 1, 1);
            return (place.Left[0] + place.Room[0], place.Left[1]);
        }
        foreach (var (name, rampMs) in new[] { ("slow", 1500), ("quick", 300) })
        {
            var spring = new Spring(220);
            var previous = Row(220); double previousWidth = 220, worst = 0;
            double frame = 1.0 / 144;
            for (int i = 0; i < 2 * 144 * 3; i++)
            {
                double t = i * frame, phase = t % (2 * rampMs / 1000.0);
                double ramp = phase < rampMs / 1000.0 ? phase / (rampMs / 1000.0) : 2 - phase / (rampMs / 1000.0);
                spring.Target = 220 + 360 * ramp;
                spring.Step(frame);
                var now = Row(Math.Round(spring.Value));
                double moved = Math.Abs(Math.Round(spring.Value) - previousWidth);
                worst = Math.Max(worst, Math.Max(Math.Abs(now.ReadingsRight - previous.ReadingsRight), Math.Abs(now.ClockLeft - previous.ClockLeft)) - moved);
                previous = now; previousWidth = Math.Round(spring.Value);
            }
            check($"Dragging ({name}): from frame to frame the readings' end and the clock move no more than the surface did, within 2 DIPs (the window follows the room)", worst <= 2);
        }
    }
}

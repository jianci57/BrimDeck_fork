using BrimDeck.Core;

// What the collapsed row costs while nothing changes: the scrolling title and the carousels wait out their rests on a timer and
// run a clock only while they move, with the values of the storyboards they replace; the readings are sampled only while they
// have room; the strip's measuring and arranging allocate nothing per reading.
internal static class IdleCostTests
{
    public static void Run(Action<string, bool> check)
    {
        Marquee(check);
        Carousel(check);
        Turns(check);
        Readings(check);
    }

    // The keyframes the old storyboards had, evaluated as WPF does: at a keyframe's time its value (the last one, when several
    // share the time); between two, a discrete frame keeps the earlier value and a linear or spline frame moves toward its own.
    private enum Kind { Discrete, Linear, Spline }
    private readonly record struct Frame(double Time, double Value, Kind Kind = Kind.Discrete);
    private static double Evaluate(Frame[] frames, double t)
    {
        int i = 0;
        while (i < frames.Length && frames[i].Time < t) i++;
        if (i == frames.Length) return frames[^1].Value;
        if (frames[i].Time == t) { while (i + 1 < frames.Length && frames[i + 1].Time == t) i++; return frames[i].Value; }
        if (i == 0) return frames[0].Value;
        var (before, frame) = (frames[i - 1], frames[i]);
        double p = (t - before.Time) / (frame.Time - before.Time);
        return frame.Kind switch
        {
            Kind.Linear => before.Value + (frame.Value - before.Value) * p,
            Kind.Spline => before.Value + (frame.Value - before.Value) * Curves.Bezier(GlideCycle.CurveX1, GlideCycle.CurveY1, GlideCycle.CurveX2, GlideCycle.CurveY2, p),
            _ => before.Value
        };
    }
    // Every point of a cycle at a fine step, its keyframe times and a little either side of them.
    private static IEnumerable<double> Points(double period, params double[] keys)
    {
        for (int i = 0; i < 4000; i++) yield return period * i / 4000;
        foreach (var key in keys.Where(key => key >= 0 && key < period)) { yield return key; yield return Math.Max(0, key - 1e-7); yield return key + 1e-7; }
    }
    private static bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) <= tolerance;

    private static void Marquee(Action<string, bool> check)
    {
        // MusicMarquee's old ParallelTimeline, for a few overflows and edge widths (the edge stop as Draw computes it).
        foreach (var (overflow, edgeWidth, region) in new[] { (100.0, 6.0, 160.0), (3.0, 6.0, 40.0), (37.5, 9.0, 220.0), (250.0, 0.0, 120.0) })
        {
            double speed = 24, edge = Math.Min(.5, edgeWidth / region), travel = overflow / speed, ramp = Math.Min(travel, edgeWidth / speed);
            var cycle = new MarqueeCycle(overflow, speed, edgeWidth, edge);
            Frame[] shift = [new(0, 0), new(1.5, 0), new(1.5 + travel, -overflow, Kind.Linear), new(3 + travel, -overflow)];
            Frame[] left = [new(0, 0), new(1.5, 0), new(1.5 + ramp, edge, Kind.Linear), new(3 + travel, edge)];
            Frame[] right = [new(0, 1 - edge), new(1.5 + travel - ramp, 1 - edge), new(1.5 + travel, 1, Kind.Linear), new(3 + travel, 1)];
            Frame[] unshift = [new(0, 0), new(1.5, 0), new(1.5 + travel, overflow, Kind.Linear), new(3 + travel, overflow)];
            string name = $"Idle cost: the scrolling title over {overflow} DIP (edge {edgeWidth})";
            var keys = new[] { 0, 1.5, 1.5 + ramp, 1.5 + travel - ramp, 1.5 + travel };
            bool same = Points(cycle.Period, keys).All(t => Near(cycle.Shift(t), Evaluate(shift, t), 1e-7) && Near(cycle.Left(t), Evaluate(left, t), 1e-7)
                && Near(cycle.Right(t), Evaluate(right, t), 1e-7) && Near(cycle.Unshift(t), Evaluate(unshift, t), 1e-7));
            check(name + " keeps the storyboard's period and matches it point by point", Near(cycle.Period, 3 + travel) && same);
            // Only the glide runs a clock: the two rests (3 s of every cycle) are a timer's.
            check(name + ": the rests run no clock", cycle.StretchAt(.7) == new CycleStretch(false, 0, 1.5) && cycle.StretchAt(1.5 + travel + .1) == new CycleStretch(false, 1.5 + travel, cycle.Period)
                && cycle.StretchAt(1.5 + travel / 2) == new CycleStretch(true, 1.5, 1.5 + travel));
        }
        var wrap = new MarqueeCycle(48, 24, 6, .05);
        check("Idle cost: the scrolling title's cycle wraps", Near(wrap.Wrap(wrap.Period + .25), .25) && Near(wrap.Wrap(-.25), wrap.Period - .25) && wrap.Wrap(double.NaN) == 0);
    }

    private static void Carousel(Action<string, bool> check)
    {
        foreach (var travel in new[] { 20.0, 34, 117.5 })
        {
            // GlideCarousel's old storyboard with animations on: shift and fade.
            var cycle = new GlideCycle(travel);
            double end = 2.5 + travel / 12, reset = end + 2, fade = .25;
            Frame[] shift = [new(0, 0), new(2.5, 0), new(end, -travel, Kind.Spline), new(reset + fade, -travel), new(reset + fade, 0)];
            Frame[] opacity = [new(0, 1), new(reset, 1), new(reset + fade, 0, Kind.Linear), new(reset + 2 * fade, 1, Kind.Linear)];
            string name = $"Idle cost: a carousel travelling {travel} DIP";
            var keys = new[] { 0, 2.5, end, reset, reset + fade, reset + 2 * fade };
            check(name + " keeps the storyboard's period and matches it point by point", Near(cycle.Period, reset + .5)
                && Points(cycle.Period, keys).All(t => Near(cycle.Shift(t), Evaluate(shift, t), 1e-7) && Near(cycle.Opacity(t), Evaluate(opacity, t), 1e-7)));
            check(name + ": only the glide and the fade back run a clock", cycle.StretchAt(1) == new CycleStretch(false, 0, 2.5) && cycle.StretchAt(end + .5) == new CycleStretch(false, end, reset)
                && cycle.StretchAt(3) == new CycleStretch(true, 2.5, end) && cycle.StretchAt(reset + .1) == new CycleStretch(true, reset, reset + .5));

            // Without animations: discrete keyframes, no fade, and the cycle ends with the last rest.
            var still = new GlideCycle(travel, Discrete: true);
            Frame[] jumps = [new(0, 0), new(2.5, 0), new(end, -travel), new(reset, -travel), new(reset, 0)];
            check(name + " without animations matches its discrete storyboard", Near(still.Period, reset)
                && Points(still.Period, 0, 2.5, end).All(t => Near(still.Shift(t), Evaluate(jumps, t)) && still.Opacity(t) == 1));
            check(name + " without animations runs no clock at all", Points(still.Period).All(t => !still.StretchAt(t).Moving));
        }
    }

    // A running cycle followed turn by turn, waking on a timer that lands a little late, as PromptTimer can: rests never hold a
    // clock, every move is handed over before it starts or while it runs (never after), and the clock it gets is lined up
    // with the cycle, so what shows is the cycle's value at every moment.
    private static void Turns(Action<string, bool> check)
    {
        check("Idle cost: a moving stretch goes to the clock at once", CycleTurn.Plan(new(true, 2, 5), new(false, 5, 7), 3) is { Move: { } m, Into: 1, Wait: 2 } && m == new CycleStretch(true, 2, 5));
        check("Idle cost: early in a rest the turn only waits, to just before the move", CycleTurn.Plan(new(false, 0, 2.5), new(true, 2.5, 5), 1) is { Move: null } t1 && Near(t1.Wait, 1.5 - CycleTurn.Lead));
        check("Idle cost: at the end of a rest the move is handed over to start on time", CycleTurn.Plan(new(false, 0, 2.5), new(true, 2.5, 5), 2.47) is { Move: { } m2 } t2
            && m2.Start == 2.5 && Near(t2.Into, -.03) && Near(t2.Wait, 2.53));
        check("Idle cost: a rest before a rest waits to its very end", CycleTurn.Plan(new(false, 4, 7), new(false, 0, 1.5), 5) is { Move: null, Wait: 2 });

        // The move's clock is what shows: the time counted follows it, when held, when woken and when retargeted.
        var glide = new CycleStretch(true, 1.5, 5.5);
        check("Idle cost: a running clock gives the position", Near(CycleTurn.Where(3.512, glide, MoveClock.Active, 2.0), 3.5)
            && Near(CycleTurn.Where(5.51, glide, MoveClock.Active, 3.98), 5.48));
        check("Idle cost: a clock is held to its move", Near(CycleTurn.Where(3, glide, MoveClock.Active, 9), 5.5) && Near(CycleTurn.Where(3, glide, MoveClock.Active, -1), 1.5)
            && CycleTurn.Where(3, glide, MoveClock.Active, double.NaN) == 3);
        check("Idle cost: a finished clock puts the cycle at least at the move's end", CycleTurn.Where(5.49, glide, MoveClock.Filling, 4) == 5.5
            && CycleTurn.Where(5.6, glide, MoveClock.Filling, 4) == 5.6 && CycleTurn.Where(.01, glide, MoveClock.Filling, 4) == .01);
        check("Idle cost: a clock not yet begun leaves the planned time", CycleTurn.Where(1.47, glide, MoveClock.Waiting, double.NaN) == 1.47 && CycleTurn.Where(2, glide, MoveClock.None, 0) == 2);
        // Woken at the planned end while the clock still runs a little behind, the turn waits for the clock rather than cutting it.
        check("Idle cost: a move still running on its clock is left to finish", CycleTurn.Holds(MoveClock.Active, 5.48, glide)
            && !CycleTurn.Holds(MoveClock.Filling, 5.5, glide) && !CycleTurn.Holds(MoveClock.Active, 5.5, glide) && !CycleTurn.Holds(MoveClock.Waiting, 1.48, glide));

        foreach (var (name, period, stretchAt) in new (string, double, Func<double, CycleStretch>)[]
        {
            ("the scrolling title", new MarqueeCycle(100, 24, 6, .04).Period, new MarqueeCycle(100, 24, 6, .04).StretchAt),
            ("a carousel", new GlideCycle(40).Period, new GlideCycle(40).StretchAt),
        })
        {
            double now = 0, clockTime = 0, movingTime = 0;
            bool lined = true, timely = true;
            int moves = 0;
            var random = new Random(7);
            while (now < 10 * period)
            {
                double at = now % period;
                var stretch = stretchAt(at);
                var turn = CycleTurn.Plan(stretch, stretchAt(stretch.End), at);
                if (turn.Move is { } move)
                {
                    moves++;
                    // The clock starts the move at its own start: as far into it as the cycle is, or later by as much.
                    lined &= Near(at - turn.Into, move.Start, 1e-9);
                    timely &= turn.Into >= -CycleTurn.Lead - 1e-9 && turn.Into <= move.End - move.Start;
                    clockTime += turn.Wait + .005;
                }
                // The timer lands 5 ms past the turn and up to 16 ms later (a timer tick).
                now += turn.Wait + .005 + random.NextDouble() * .016;
            }
            for (double t = 0; t < period; t += .001) if (stretchAt(t).Moving) movingTime += .001;
            double perCycle = clockTime / 10;
            check($"Idle cost: {name}'s moves line up with the cycle", lined && moves >= 10);
            check($"Idle cost: {name}'s moves are handed over in time", timely);
            check($"Idle cost: {name} runs a clock only around its moves ({perCycle:0.00} s of {period:0.00} s, moving {movingTime:0.00} s)",
                perCycle <= movingTime + 2 * (CycleTurn.Lead + .03) && perCycle >= movingTime - .05);
        }
    }

    private static void Readings(Action<string, bool> check)
    {
        // The figure kept for each whole percent is the one the format gives.
        check("Idle cost: every whole percent's figure is the formatted one", Enumerable.Range(0, 2001).Select(i => i / 2000d - .0001)
            .Append(double.NaN).Append(2).Append(-1).All(share => MonitorFormat.WholePercent(share).ToString(System.Globalization.CultureInfo.InvariantCulture) == MonitorFormat.Percent(share)));

        // Arranging into a kept list gives what the old arrangement gave, and allocates nothing.
        CompactMonitorItems[] order = [CompactMonitorItems.Download, CompactMonitorItems.Cpu, CompactMonitorItems.Columns, CompactMonitorItems.Upload, CompactMonitorItems.Memory, CompactMonitorItems.Gpu];
        var shown = CompactMonitorItems.Cpu | CompactMonitorItems.Memory | CompactMonitorItems.Download | CompactMonitorItems.Columns;
        double now = .3;
        Func<CompactMonitorItems, double> width = item => item == CompactMonitorItems.Columns ? 30 : 41, presence = item => item == CompactMonitorItems.Memory ? now : 1;
        var (old, oldTotal) = CompactMonitor.Arrange(order, shown, width, 6, presence);
        var kept = new List<(CompactMonitorItems Item, double X, double Width, double Presence)>();
        double total = CompactMonitor.Arrange(order, shown, width, 6, presence, kept);
        check("Idle cost: the kept arrangement is the old one", total == oldTotal && kept.SequenceEqual(old));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { total = CompactMonitor.Arrange(order, shown, width, 6, presence, kept); total += CompactMonitor.Width(shown, width, 6); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check($"Idle cost: arranging and measuring the readings allocates nothing ({allocated} B for 1000)", allocated == 0);

        // The busiest core sets the columns' level as Max did, a share that is not a number passed over.
        static MonitorSnapshot Cores(params double[] usage) => MonitorSnapshot.Empty(usage.Length, "") with
        {
            Cpu = new CpuSample([.. usage.Select((u, i) => new CoreSample(i, 0, CoreTier.Unknown, i + 1, u, Reading<double>.Unavailable(""), null))], Reading<double>.Unavailable(""))
        };
        check("Idle cost: the columns take the busiest core's level", ReadingLevels.Of(CompactMonitorItems.Columns, Cores(.1, .97, .3)) == ReadingLevels.OfShare(.97)
            && ReadingLevels.Of(CompactMonitorItems.Columns, Cores(double.NaN, .1)) == ReadingLevels.OfShare(.1)
            && ReadingLevels.Of(CompactMonitorItems.Columns, Cores(.2, double.NaN, .9)) == ReadingLevels.OfShare(.9));
        // The processor's share and its kinds of core, added up by index, are what Average and Max gave.
        var random = new Random(3);
        bool sameCpu = true;
        for (int n = 1; n < 40; n++)
        {
            var cores = Enumerable.Range(0, n).Select(i => new CoreSample(i, random.Next(3), CoreTier.Unknown, i + 1, random.NextDouble(), Reading<double>.Unavailable(""), null)).ToList();
            var cpu = new CpuSample(cores, Reading<double>.Unavailable(""));
            sameCpu &= cpu.Usage == cores.Average(core => core.Usage) && cpu.GroupCount == cores.Max(core => core.Group) + 1;
        }
        check("Idle cost: the processor's share and core kinds are as before", sameCpu && new CpuSample([], Reading<double>.Unavailable("")) is { Usage: 0, GroupCount: 0 });
    }
}

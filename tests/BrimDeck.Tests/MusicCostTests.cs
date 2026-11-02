using BrimDeck.Core;

// What the collapsed music row costs while a song plays: the title rests after its laps, the bars draw only what moves a
// half pixel, and the audio is analysed 30 times a second.
internal static class MusicCostTests
{
    public static void Run(Action<string, bool> check)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

        // The title scrolls Laps whole cycles and then rests at its start.
        var cycle = new MarqueeCycle(48, 24, 6, .05);
        double period = cycle.Period;
        check("the title is still scrolling during its last lap", !cycle.Done(3 * period - .01) && Near(cycle.Played(3 * period - .01), cycle.Wrap(3 * period - .01)));
        check("the title is done after three whole cycles", cycle.Done(3 * period) && cycle.Done(10 * period));
        check("a title that is done rests at the start of its first rest", Near(cycle.Played(3 * period), 0) && Near(cycle.Shift(cycle.Played(3 * period)), 0)
            && Near(cycle.Left(cycle.Played(3 * period)), 0) && Near(cycle.Right(cycle.Played(3 * period)), 1 - .05));
        check("the last lap ends in the end rest, and the next stretch is the start", !cycle.StretchAt(cycle.Wrap(3 * period - .01)).Moving);

        // The bars stand on half device pixels, so a level that moves them by less draws the same.
        foreach (double dpi in new[] { 1, 1.25, 1.5, 2 })
        {
            double step = .5 / dpi;
            bool snapped = true, same = true;
            for (int i = 0; i < 4; i++)
                for (double level = 0; level <= 1; level += .013)
                {
                    var (x, y, w, h) = EqualizerLayout.Bar(i, 4, level, 20, 14, 3, dpi);
                    snapped &= Near(Math.Round(x / step) * step, x) && Near(Math.Round(y / step) * step, y) && Near(Math.Round(h / step) * step, h);
                    same &= Math.Abs(EqualizerLayout.Bar(i, 4, level + 1e-4, 20, 14, 3, dpi).Height - h) <= step + 1e-9;
                }
            check($"the bars stand on half device pixels at {dpi * 100:0}%", snapped);
            check($"a hair of level moves a bar at most half a pixel at {dpi * 100:0}%", same);
        }
        var low = EqualizerLayout.Bar(0, 4, 0, 20, 14, 3, 1); var high = EqualizerLayout.Bar(3, 4, 1, 20, 14, 3, 1.5);
        check("a silent bar is as tall as it is wide and centred", Near(low.Height, 3) && Near(low.Top, 5.5) && Near(low.Left, 0));
        check("a full bar fills the box at the right end", Near(high.Height, 14) && Near(high.Top, 0) && Near(high.Left, 17));
        check("bad input draws a resting bar", EqualizerLayout.Bar(1, 4, double.NaN, 20, 14, 3, double.NaN).Height == 3);

        // Analysed 30 times a second, a song still lifts the bars and silence lets them fall.
        const int rate = 44100;
        var signal = new EqualizerSignal(rate);
        var block = new float[441 * 2];
        double peak = 0, t = 0;
        for (int n = 0; n < 400; n++)
        {
            t = n / 100d;
            for (int i = 0; i < 441; i++)
            {
                double s = t + i / (double)rate, since = s % .5;
                float v = (float)(.3 * Math.Sin(2 * Math.PI * 220 * s) + .7 * Math.Exp(-since / .08) * Math.Sin(2 * Math.PI * 60 * since));
                block[i * 2] = block[i * 2 + 1] = v;
            }
            signal.Push(block, 2, t + .01);
            if (n % 3 == 0) { signal.Advance(t + .01); peak = Math.Max(peak, signal.Head); }
        }
        for (int n = 1; n <= 30; n++) signal.Advance(t + .01 + n / 30d);
        check("analysed 30 times a second, the bars still rise with the music and fall in silence", peak > .2 && signal.Head < .05);
    }
}

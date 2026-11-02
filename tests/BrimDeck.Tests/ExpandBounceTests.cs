using BrimDeck.Core;

// The give at the end of the panel's opening. Every theme opens on the one spring (Curves.Bounce, as Soft.Bounce) whatever
// its look, so the bounds below are the softening for every theme at once. The curve is a unit curve:
// what the surface passes its end by is the curve's overshoot times the distance of the move, so the checks are made at the
// largest moves the panel can make (DeckPages.MaxHeight, half of MaxWidth per side) and at the fastest setting (LengthMin).
internal static class ExpandBounceTests
{
    // The animation duration setting's range and default (Settings.AnimationDuration).
    private const int LengthMin = 100, LengthMax = 1000, LengthDefault = 360;
    // The curve as it was before the softening (damping .85, not divided by its end), for showing that the bounds do reject it.
    private static double Before(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        const double decay = 6.2, damping = .85;
        double omega = decay / damping * Math.Sqrt(1 - damping * damping);
        return 1 - Math.Exp(-decay * x) * Math.Cos(omega * x);
    }

    private const int Steps = 20000;

    // What the softened opening must be. Returns the name of the first bound a curve breaks, or null.
    private static string? Breaks(Func<double, double> curve)
    {
        var y = new double[Steps + 1];
        for (int i = 0; i <= Steps; i++) y[i] = curve((double)i / Steps);
        double peak = y.Max() - 1;
        int at = Array.IndexOf(y, y.Max());
        // At most half a percent, so the tallest move's bottom edge passes by well under 2 DIPs (and the usual move by under 1)...
        if (peak > .005) return "peak";
        // ...but a give is left (a curve that never passes its end is the plain ease-out, not the island's own gesture).
        if (peak < .001) return "no give";
        // One rise and one fall: it rises to its peak, comes back to its end without passing below it again, and never wobbles.
        for (int i = 1; i <= at; i++) if (y[i] < y[i - 1] - 1e-12) return "rises steadily";
        for (int i = at + 1; i <= Steps; i++) if (y[i] > y[i - 1] + 1e-12 || y[i] < 1 - 1e-9) return "single return";
        // The way back is slow enough that no 60 Hz frame of the tallest move at the fastest length moves the edge back by more than a DIP.
        double back = 0;
        for (int i = at + 1; i <= Steps; i++) back = Math.Max(back, (y[i - 1] - y[i]) * Steps);
        double perFrame = back * DeckPages.MaxHeight / (LengthMin / 1000.0) / 60;
        if (perFrame > 1) return "retreat speed";
        // It lands on its end, with nothing left to jump on the last frame and no stop from speed.
        if (curve(0) != 0 || curve(1) != 1 || Math.Abs(curve(1 - 1e-9) - 1) * DeckPages.MaxHeight > .01) return "ends";
        double last = (curve(1 - 1e-9) - curve(1 - 1e-3)) / 1e-3;
        if (Math.Abs(last) > .015) return "landing speed";
        // The attack stays: half way within a neighbourhood of a ninth of the move, nine tenths within a third.
        if (curve(.11) < .5 || curve(.32) < .9 || curve(.02) < .1) return "attack";
        return null;
    }

    public static void Run(Action<string, bool> check)
    {
        check("Expand bounce: the opening spring keeps every bound (give, single return, landing, attack)", Breaks(Curves.Bounce) is null);
        check("Expand bounce: the curve before the softening broke them (the bounds can fail)", Breaks(Before) is "peak");
        double peakNow = Enumerable.Range(0, Steps + 1).Max(i => Curves.Bounce((double)i / Steps)) - 1;
        check("Expand bounce: BounceOvershoot is the sampled peak, and is covered by the region held while the panel moves (2 percent)",
            Math.Abs(peakNow - Curves.BounceOvershoot) < 2e-5 && Curves.BounceOvershoot < .02 / 4);

        // Heights and widths: the distance the edge passes its end by, from small page changes to the largest move.
        bool heights = true, widths = true;
        foreach (double move in new double[] { 6, 20, 60, 120, 200, 300, DeckPages.MaxHeight })
            heights &= move * Curves.BounceOvershoot <= (move <= 200 ? .75 : 1.5);
        foreach (double move in new double[] { 40, 200, 600, DeckPages.MaxWidth })
            widths &= move * Curves.BounceOvershoot / 2 <= 2;
        check("Expand bounce: the bottom edge passes its end by under 1.5 DIPs on the tallest move (under .75 up to 200), each side by under 2 DIPs on the widest", heights && widths);

        // The move's own length does not change what it passes its end by, but the speed it comes back at: at every setting it stays
        // under a DIP a frame (60 Hz) on the tallest move, and it is slower still at the default.
        double back = 0;
        for (int i = 1; i <= Steps; i++) back = Math.Max(back, (Curves.Bounce((i - 1.0) / Steps) - Curves.Bounce((double)i / Steps)) * Steps);
        bool frames = true;
        for (int length = LengthMin; length <= LengthMax; length += 50)
            frames &= back * DeckPages.MaxHeight / (length / 1000.0) / 60 <= 1;
        check("Expand bounce: at no animation length does the tallest move's edge come back faster than a DIP a frame", frames && back * DeckPages.MaxHeight / (LengthDefault / 1000.0) / 60 < .3);
    }
}

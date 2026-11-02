using BrimDeck.Core;
using static EqualizerAudio;

// The level bars as a spectrum: every bar is a band of pitch, for every length the settings allow (3 to 20 bars).
internal static class EqualizerSpectrumTests
{
    private static readonly int[] Lengths = Enumerable.Range(CompactMusicLayout.MinimumBars, CompactMusicLayout.MaximumBars - CompactMusicLayout.MinimumBars + 1).ToArray();

    private static List<(double Time, double[] Levels)> Play(EqualizerSignal signal, Func<double, float> wave, double seconds, int bars, double start = 0) =>
        EqualizerAudio.Play(signal, wave, seconds, bars, EqualizerMode.Spectrum, start);
    private static int Loudest(double[] levels) => Array.IndexOf(levels, levels.Max());
    private static double Mean(IEnumerable<double> values) => values.Average();
    // The mean level of each bar over the last seconds of the run.
    private static double[] Averages(List<(double Time, double[] Levels)> run, double lastSeconds, int bars)
    {
        double end = run[^1].Time; var tail = run.Where(s => s.Time > end - lastSeconds).ToList();
        return Enumerable.Range(0, bars).Select(b => tail.Average(s => s.Levels[b])).ToArray();
    }

    public static void Run(Action<string, bool> check)
    {
        // The bars of every length split 40 Hz to 16 kHz into equal shares of the log axis with no gap or overlap.
        bool contiguous = true, monotone = true, coversAll = true, equalShares = true;
        foreach (int rate in new[] { 44100, 48000, 22050 })
        {
            double top = EqualizerSignal.TopHz(rate);
            for (int bars = 1; bars <= EqualizerSignal.MaxBars; bars++)
            {
                double previousHigh = EqualizerSignal.LowHz;
                for (int i = 0; i < bars; i++)
                {
                    var (low, high) = EqualizerSignal.BarRange(bars, i, rate);
                    contiguous &= Math.Abs(low - previousHigh) < 1e-9; monotone &= high > low; previousHigh = high;
                    equalShares &= Math.Abs(Math.Log(high / low) - Math.Log(top / EqualizerSignal.LowHz) / bars) < 1e-9;
                }
                coversAll &= Math.Abs(previousHigh - top) < 1e-6;
            }
        }
        check("Spectrum bars: for 1 to 60 bars the bands are in order, share the log axis equally and meet with no gap or overlap", contiguous && monotone && equalShares);
        check("Spectrum bars: the bands cover the whole range from 40 Hz up to 16 kHz (less at a low sample rate), whatever the length", coversAll && EqualizerSignal.TopHz(44100) == 16000 && EqualizerSignal.TopHz(22050) < 11025);

        // A steady tone at the middle of a bar lights that bar and the ones next to it only, for every bar of every length.
        int wrongBar = 0, tooWide = 0, tested = 0;
        string firstWrong = "";
        foreach (int bars in Lengths)
        {
            for (int bar = 0; bar < bars; bar++)
            {
                var (low, high) = EqualizerSignal.BarRange(bars, bar, Rate); double hz = Math.Sqrt(low * high);
                var run = Play(new EqualizerSignal(Rate), t => Sine(t, hz, .5), .7, bars);
                var levels = run[^1].Levels; tested++;
                if (Loudest(levels) != bar) { wrongBar++; if (firstWrong.Length == 0) firstWrong = $"{bars} bars, bar {bar} ({hz:F0} Hz) lit bar {Loudest(levels)}"; }
                // The bars more than three away stay at rest; where the bars are narrower than the window can tell apart, the next ones may share.
                else if (Enumerable.Range(0, bars).Where(b => Math.Abs(b - bar) > 3).Any(b => levels[b] > .1)) tooWide++;
            }
        }
        check($"Spectrum bars: a tone in the middle of a bar lights that very bar, for each of {tested} bars of the lengths 3 to 20" + (firstWrong.Length > 0 ? " (" + firstWrong + ")" : ""), wrongBar == 0);
        check("Spectrum bars: a tone does not move bars more than three places from its own", tooWide == 0);

        // A tone sweeping up lights the bars in turn, left to right, whatever the length.
        bool sweepsUp = true, reachesEnds = true;
        foreach (int bars in new[] { 3, 4, 5, 8, 12, 17, 20 })
        {
            // Phase of a logarithmic sweep from 50 Hz to 15 kHz in 6 s.
            const double From = 50, To = 15000, Seconds = 6;
            double k = Math.Log(To / From) / Seconds;
            var run = Play(new EqualizerSignal(Rate), t => (float)(.5 * Math.Sin(2 * Math.PI * From * (Math.Exp(k * t) - 1) / k)), Seconds, bars);
            var lit = run.Skip(2).Select(s => Loudest(s.Levels)).ToList();
            int back = 0;
            for (int i = 1; i < lit.Count; i++) back = Math.Max(back, lit[i - 1] - lit[i]);
            sweepsUp &= back <= 1; reachesEnds &= lit[0] <= 1 && lit[^1] >= bars - 2;
            // At each moment the pitch of the sweep and the lit bar agree.
            int off = 0;
            foreach (var s in run.Skip(2))
            {
                double hz = From * Math.Exp(k * (s.Time - .06));
                int expected = Enumerable.Range(0, bars).First(b => hz <= EqualizerSignal.BarRange(bars, b, Rate).High || b == bars - 1);
                if (Math.Abs(Loudest(s.Levels) - expected) > 1 + bars / 8) off++;
            }
            sweepsUp &= off == 0;
        }
        check("Spectrum bars: a rising sweep lights the bars from left to right, for several lengths", sweepsUp && reachesEnds);

        // A low and a high tone together light the bars at both ends and none between.
        bool twoTones = true;
        foreach (int bars in new[] { 3, 4, 5, 8, 12, 20 })
        {
            var run = Play(new EqualizerSignal(Rate), t => Sine(t, 90, .35) + Sine(t, 7000, .35), 1, bars);
            var levels = run[^1].Levels;
            int low = Enumerable.Range(0, bars).First(b => EqualizerSignal.BarRange(bars, b, Rate).High > 90), high = Enumerable.Range(0, bars).First(b => EqualizerSignal.BarRange(bars, b, Rate).High > 7000);
            bool ends = levels[low] > .3 && levels[high] > .3;
            bool between = Enumerable.Range(0, bars).Where(b => Math.Abs(b - low) > 2 && Math.Abs(b - high) > 2).All(b => levels[b] < .1);
            twoTones &= ends && between && Loudest(levels) is var best && (best == low || best == high);
        }
        check("Spectrum bars: a low and a high tone together light a bar at each end and leave the ones between at rest", twoTones);

        // Kick drums only lift the lowest bars.
        bool kicks = true;
        foreach (int bars in Lengths)
        {
            var run = Play(new EqualizerSignal(Rate), t => Hit(t, 60, .5, .8, .08), 3, bars);
            var average = Averages(run, 2, bars);
            int lowBars = Math.Max(1, (int)Math.Ceiling(bars / 4d)), above = bars / 2;
            // The kick is at 60 Hz: in the first bar for 3 bars, among the first few for 20; the upper half stays far below it.
            kicks &= Loudest(average) < lowBars + 1 && Enumerable.Range(above, bars - above).All(b => average[b] < .25 * average.Max());
        }
        check("Spectrum bars: a 60 Hz kick drum lifts the lowest bars and leaves the upper half far below, for every length", kicks);

        // Noise has the same share of energy in every band once scaled, so no bar is left far lower than another.
        var random = new Random(11);
        bool white = true, pink = true;
        foreach (int bars in new[] { 3, 4, 5, 10, 20 })
        {
            var whiteRun = Play(new EqualizerSignal(Rate), _ => (float)(random.NextDouble() * .6 - .3), 3, bars);
            var average = Averages(whiteRun, 2, bars);
            white &= average.Min() > .2 && average.Max() < .9 && average.Max() / average.Min() < 2.5;
            // Pink noise, shaped by a filter whose spectrum falls 3 dB an octave.
            double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
            var pinkRun = Play(new EqualizerSignal(Rate), _ =>
            {
                double w = random.NextDouble() * 2 - 1;
                b0 = .99886 * b0 + w * .0555179; b1 = .99332 * b1 + w * .0750759; b2 = .969 * b2 + w * .153852; b3 = .8665 * b3 + w * .3104856; b4 = .55 * b4 + w * .5329522; b5 = -.7616 * b5 - w * .016898;
                double value = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * .5362) * .11; b6 = w * .115926; return (float)value;
            }, 3, bars);
            var pinkAverage = Averages(pinkRun, 2, bars);
            pink &= pinkAverage.Min() > .15 && pinkAverage.Max() / pinkAverage.Min() < 3.5;
        }
        check("Spectrum bars: white and pink noise keep every bar of every length in a similar range", white && pink);

        // Silence, and dither of one bit, leave every bar at 0; so does the end of the sound.
        bool silent = true;
        var dither = new Random(7);
        foreach (int bars in new[] { 3, 5, 20 })
        {
            silent &= Play(new EqualizerSignal(Rate), _ => 0, 1, bars).All(s => s.Levels.All(l => l == 0));
            silent &= Play(new EqualizerSignal(Rate), _ => (float)((dither.Next(3) - 1) / 32768d), 2, bars).All(s => s.Levels.All(l => l == 0));
            var signal = new EqualizerSignal(Rate);
            var sounding = Play(signal, t => Sine(t, 440, .4) + Hit(t, 60, .5, .5, .08), 2, bars);
            var after = Play(signal, _ => 0, 1.5, bars, sounding[^1].Time);
            silent &= sounding.Max(s => s.Levels.Max()) > .3 && after[^1].Levels.All(l => l == 0) && after.Take(3).Any(s => s.Levels.Max() > 0);
        }
        check("Spectrum bars: silence and one-bit dither leave all the bars at 0 for any length, and they fall to 0 when the sound ends", silent);
        var idle = new EqualizerSignal(Rate); var idleLevels = new double[5];
        var idleRun = Play(idle, t => Sine(t, 440, .4), 1, 5);
        idle.Fill(idleLevels, idleRun[^1].Time, EqualizerMode.Spectrum);
        bool shownNow = idleLevels.Max() > .3;
        idle.Fill(idleLevels, idleRun[^1].Time + 1, EqualizerMode.Spectrum);
        check("Spectrum bars: an analysis that is long out of date (the capture stalled) shows nothing", shownNow && idleLevels.All(l => l == 0));

        // The length changes while the sound plays (the slider is dragged, the row gains room): the bars carry on.
        var growing = new EqualizerSignal(Rate); var fresh = new EqualizerSignal(Rate);
        var counts = new[] { 3, 20, 4, 12, 5, 20, 3 };
        double clock = 0; bool carries = true, equalsFresh = true; double biggestStep = 0;
        var before = Play(growing, Music, 3, 5);
        clock = before[^1].Time;
        foreach (int count in counts)
        {
            double earlier = Mean(before[^1].Levels), earlierPeak = before[^1].Levels.Max();
            // The first look at the new count comes before the next analysis.
            var asked = new double[count]; growing.Fill(asked, clock, EqualizerMode.Spectrum);
            carries &= asked.All(l => l > 0) && Math.Abs(Mean(asked) - earlier) < .2 && Math.Abs(asked.Max() - earlierPeak) < .3;
            before = Play(growing, Music, 1, count, clock); clock = before[^1].Time;
            // The bars of the next analyses move gently and never fall to rest while the music plays.
            carries &= before.All(s => s.Levels.Max() > .1);
            biggestStep = Math.Max(biggestStep, Math.Abs(Mean(before[0].Levels) - earlier));
        }
        // A signal that had that length all along shows the same bars as one that was changed to it.
        var reference = new EqualizerSignal(Rate);
        var referenceRun = Play(reference, Music, clock, 7);
        var lastRun = Play(growing, Music, 1.5, 7, clock); var referenceTail = Play(reference, Music, 1.5, 7, clock);
        equalsFresh = lastRun.Zip(referenceTail).Skip(10).All(pair => pair.First.Levels.Zip(pair.Second.Levels).All(l => Math.Abs(l.First - l.Second) < .25));
        check("Spectrum bars: changing the length while playing shows the bars at the new length at once, none at rest and none far from before", carries && biggestStep < .3);
        check("Spectrum bars: after a change of length the bars are those of a signal that had the new length all along", equalsFresh);

        // A length changed in silence makes the bars again from the narrow bands kept, so it must not bring back the last sound.
        var quieted = new EqualizerSignal(Rate);
        var toned = Play(quieted, t => Sine(t, 440, .5), 1.5, 5);
        var hushed = Play(quieted, _ => 0, 1, 5, toned[^1].Time);
        var regrown = Play(quieted, _ => 0, .1, 12, hushed[^1].Time);
        check("Spectrum bars: a length changed in silence does not bring back the level of the last sound",
            toned[^1].Levels.Max() > .3 && hushed[^1].Levels.Max() < .01 && regrown.All(s => s.Levels.Max() < .01));
    }
}

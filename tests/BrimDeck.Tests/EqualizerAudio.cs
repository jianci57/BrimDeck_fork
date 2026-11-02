using BrimDeck.Core;

// Sounds for the equalizer tests, and the way the capture hands them to EqualizerSignal: 10 ms blocks of stereo audio, an analysis
// every third block (30 a second), and the bars read as the panel reads them after each analysis.
internal static class EqualizerAudio
{
    public const int Rate = 44100;

    public static float Sine(double t, double hz, double amplitude) => (float)(amplitude * Math.Sin(2 * Math.PI * hz * t));
    // A decaying tone started every period seconds, like a drum hit; it swells over a few ms, as a struck drum does (a start
    // that jumps to full is a click, which has some of every pitch).
    public static float Hit(double t, double hz, double period, double amplitude, double decay)
    { double since = t % period; return (float)(amplitude * (1 - Math.Exp(-since / .004)) * Math.Exp(-since / decay) * Math.Sin(2 * Math.PI * hz * since)); }
    // A floor of noise that depends only on the time, so every signal hears the same.
    public static double Floor(double t) { double x = Math.Sin(t * 12345.6789) * 43758.5453; return (x - Math.Floor(x) - .5) * .1; }
    // Notes, drums and noise: something in most of the bars.
    public static float Music(double t) => (float)(Sine(t, 220, .25) + Sine(t, 1800, .12 * (1 + Math.Sin(t * 5)) / 2) + Hit(t, 60, .5, .5, .08) + Hit(t + .25, 6500, .5, .1, .03) + Floor(t));

    // Plays wave into the signal for seconds from start and returns the bars, as many as asked for, after every analysis.
    public static List<(double Time, double[] Levels)> Play(EqualizerSignal signal, Func<double, float> wave, double seconds, int bars, EqualizerMode mode, double start = 0)
    {
        var snapshots = new List<(double, double[])>(); var block = new float[441 * 2]; var levels = new double[bars];
        // The first look tells the analysis the way and the number of bars.
        signal.Fill(levels, start, mode);
        for (int n = 0; n < (int)Math.Round(seconds * 100); n++)
        {
            double t0 = start + n / 100d;
            for (int i = 0; i < 441; i++) { float v = wave(t0 + i / (double)Rate); block[i * 2] = v; block[i * 2 + 1] = v; }
            signal.Push(block, 2, t0 + .01);
            if (n % 3 != 2) continue;
            signal.Advance(t0 + .01); signal.Fill(levels, t0 + .01, mode); snapshots.Add((t0 + .01, (double[])levels.Clone()));
        }
        return snapshots;
    }
}

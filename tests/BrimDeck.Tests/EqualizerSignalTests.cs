using BrimDeck.Core;
using static EqualizerAudio;

// The Flow way of the equalizer (EqualizerMode.Flow): one value for the whole sound, shown at increasing delays from left to right.
internal static class EqualizerSignalTests
{
    public static void Run(Action<string, bool> check)
    {
        // A paused player can still send dither of one least significant bit.
        var random = new Random(7);
        var dither = Play(new EqualizerSignal(Rate), _ => (float)((random.Next(3) - 1) / 32768d), 4, 5, EqualizerMode.Flow);
        check("Flow: dither from a paused player keeps the bars at the bottom", dither.All(s => s.Levels.All(level => level <= .06)));

        var flowing = new EqualizerSignal(Rate);
        var played = Play(flowing, t => Sine(t, 220, .3) + Hit(t, 60, .5, .7, .08), 4, CompactMusicLayout.MaximumBars, EqualizerMode.Flow);
        check("Flow: every one of the most bars has a value while audio plays, the last one too", played[^1].Levels.All(level => level > 0));
        double now = played[^1].Time;
        for (int n = 1; n <= 100; n++) flowing.Advance(now + n / 100d);
        var after = new double[1]; flowing.Fill(after, now + 1);
        check("Flow: the bars fall when the player stops sending audio", played.Max(s => s.Levels[0]) > .2 && after[0] < .05);

        // One burst after silence: the leftmost bar rises first and each bar to its right one BarDelay later.
        var burst = Play(new EqualizerSignal(Rate), t => t is >= 1 and < 1.2 ? Sine(t, 220, .5) : 0, 3.5, CompactMusicLayout.MaximumBars, EqualizerMode.Flow);
        var peaks = Enumerable.Range(0, CompactMusicLayout.MaximumBars).Select(bar => burst.MaxBy(s => s.Levels[bar]).Time).ToList();
        bool inOrder = peaks.Zip(peaks.Skip(1), (left, right) => right >= left).All(x => x);
        double spread = peaks[^1] - peaks[0], expected = (CompactMusicLayout.MaximumBars - 1) * EqualizerSignal.BarDelay;
        check("Flow: a burst of sound runs along the bars from left to right, one BarDelay from each bar to the next", inOrder && Math.Abs(spread - expected) < .07);
    }
}

using System.Text.Json;
using BrimDeck.Core;
using static EqualizerAudio;

// The equalizer's two ways of showing the sound (EqualizerMode): one choice serves the collapsed and the expanded equalizer, and
// the way can change while the sound plays.
internal static class EqualizerModeTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        // Settings: Flow by default, saved as a number; a file without it, or with a number that is not a way, is Flow.
        var store = new SettingsStore(Path.Combine(root, "equalizer-mode"), "0.1.0");
        var saved = store.Load();
        bool fresh = saved.EqualizerMode == EqualizerMode.Flow;
        saved.EqualizerMode = EqualizerMode.Spectrum; store.Save(saved);
        var reread = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        bool number;
        using (var doc = JsonDocument.Parse(File.ReadAllText(store.FilePath))) number = doc.RootElement.GetProperty("EqualizerMode").GetInt32() == 1;
        check("Equalizer mode: Flow at first; saved as a number and read back",
            fresh && number && reread.EqualizerMode == EqualizerMode.Spectrum);
        // Earlier builds kept a separate way and color for the music page; the collapsed equalizer's are kept for both.
        File.WriteAllText(store.FilePath, """{"AppVersion":"1.2.4","EqualizerMode":0,"MusicEqualizerMode":1,"EqualizerColor":2,"MusicPageEqualizerColor":3}""");
        var merged = new SettingsStore(store.DirectoryPath, "1.2.4").Load();
        check("Equalizer mode: the music page's former way and color are ignored; the collapsed equalizer's serve both",
            merged.EqualizerMode == EqualizerMode.Flow && merged.EqualizerColor == ClockColor.Cyan);
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","EqualizerMode":7}""");
        var unknown = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        check("Equalizer mode: a number that is not a way, or no number at all, reads as Flow",
            unknown.EqualizerMode == EqualizerMode.Flow);

        // The way changes while music plays. Before the new way has made an analysis it shows the bars as they stood; after it the
        // bars carry on from them, without going to rest or jumping.
        foreach (int bars in new[] { 3, 5, 20 })
        {
            var signal = new EqualizerSignal(Rate); var mode = EqualizerMode.Flow;
            var run = Play(signal, Music, 2.5, bars, mode);
            bool asShown = true, standing = true; double biggestStep = 0, biggestAtSwitch = 0;
            for (int round = 0; round < 8; round++)
            {
                var shown = run[^1].Levels; double clock = run[^1].Time;
                mode = mode == EqualizerMode.Flow ? EqualizerMode.Spectrum : EqualizerMode.Flow;
                var look = new double[bars]; signal.Fill(look, clock, mode);
                asShown &= look.Zip(shown).All(pair => Math.Abs(pair.First - pair.Second) < 1e-9);
                run = Play(signal, Music, .8, bars, mode, clock);
                biggestAtSwitch = Math.Max(biggestAtSwitch, Math.Abs(run[0].Levels.Average() - shown.Average()));
                for (int i = 1; i < run.Count; i++) biggestStep = Math.Max(biggestStep, Math.Abs(run[i].Levels.Average() - run[i - 1].Levels.Average()));
                standing &= run.All(s => s.Levels.Max() > .05);
            }
            check($"Equalizer mode: switching 8 times while music plays, with {bars} bars, starts from the bars as they stood and never rests or jumps",
                asShown && standing && biggestStep < .25 && biggestAtSwitch < .35);
        }
    }
}

using BrimDeck.Core;

// The frame rate of the program in front: in the collapsed row a reading of its own at the end of the table, outside the presets,
// which joins the row with its first figure and stays away where frame rates cannot be read; on the page a line of its own.
static class FrameRateTests
{
    private const CompactMonitorItems Frames = CompactMonitorItems.Frames;

    public static void Run(Action<string, bool> check)
    {
        Table(check);
        Admission(check);
        Colors(check);
        Layout(check);
    }

    // The page's frame rate line: under memory and network and above the drives, only where it fits with the graph kept, after the
    // first power line; a page that asks for none is laid out as before.
    private static void Layout(Action<string, bool> check)
    {
        foreach (var (width, height) in new[] { (700.0, 290.0), (900.0, 400.0), (480.0, 160.0), (760.0, 240.0) })
        {
            var plain = MonitorLayout.For(width, height, true, 2, 1);
            check($"Frame line {width}×{height}: not asked for, nothing changes", plain == MonitorLayout.For(width, height, true, 2, 1, frames: false) && !plain.FrameLine && plain.Parts(height)[5].Height == 0);
        }
        var tall = MonitorLayout.For(700, 400, true, 2, 1, frames: true);
        var parts = tall.Parts(400);
        check("Frame line: a tall page has it, right under memory and network, the drives under it",
            tall.FrameLine && parts[5].Top == parts[2].Top + parts[2].Height && parts[3].Top == parts[5].Top + parts[5].Height && parts[5].Height > 0);
        check("Frame line: the first power line keeps its place", tall.PowerLines >= 1 == (MonitorLayout.For(700, 400, true, 2, 1).PowerLines >= 1));
        var small = MonitorLayout.For(480, 120, false, 2, 1, frames: true);
        check("Frame line: a page too short for it has none", !small.FrameLine && small.Parts(120)[1].Height >= 16);
        bool fits = true;
        for (double height = 100; height <= 420; height += 7)
        {
            var with = MonitorLayout.For(700, height, true, 3, 1, frames: true);
            if (with.FrameLine && with.Parts(height)[1].Height < MonitorLayout.MinimumGraph - 1e-9) fits = false;
        }
        check("Frame line: at every height it shows, the graph keeps its least height", fits);
    }

    private static void Table(Action<string, bool> check)
    {
        var table = CompactMonitor.Normalize(null);
        check("Frame rate: last in the table, hidden", table[^1].Item == Frames && !table[^1].Visible);

        var full = CompactMonitor.Normalize(null);
        full.First(reading => reading.Item == Frames).Figures = [];
        CompactMonitor.Apply(full, CompactMonitorMode.Full);
        check("Frame rate: Full shows it with the program's icon", full.First(reading => reading.Item == Frames) is { Visible: true } shownFrames
            && CompactFrames.Shows(shownFrames, CompactFrames.Icon) && CompactMonitor.Preset(full) == CompactMonitorMode.Full);
        full.First(reading => reading.Item == Frames).Figures = [];
        check("Frame rate: without its icon, Full no longer matches", CompactMonitor.Preset(full) is null);
        CompactMonitor.Apply(full, CompactMonitorMode.Brief);
        check("Frame rate: Brief hides it", !full.First(reading => reading.Item == Frames).Visible);

        var plain = CompactMonitor.Normalize(null).First(reading => reading.Item == Frames);
        check("Frame rate: without a choice it shows the program's icon", CompactFrames.Of(plain).SequenceEqual([CompactFrames.Icon]));
        var parts = CompactMonitor.Normalize([new CompactReading { Item = Frames, Figures = [" name ", "Icon", "Usage", "Icon"] }]).First(reading => reading.Item == Frames);
        check("Frame rate: a choice keeps the icon, once, and nothing else (a name chosen in a build that offered it is dropped)", parts.Figures is ["Icon"]);
        var bare = CompactMonitor.Normalize([new CompactReading { Item = Frames, Figures = [] }]).First(reading => reading.Item == Frames);
        check("Frame rate: an empty choice is the figure alone, not the default", bare.Figures is [] && !CompactFrames.Shows(bare, CompactFrames.Icon));

        var settings = new DeckSettings { MonitorFrames = true };
        settings.CompactReadings.First(reading => reading.Item == Frames).Visible = true;
        settings.CompactReadings.First(reading => reading.Item == Frames).Figures = [];
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck-Tests-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        DeckSettings copy;
        try { new SettingsStore(folder).Save(settings); copy = new SettingsStore(folder).Load(); }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        var kept = copy.CompactReadings.First(reading => reading.Item == Frames);
        check("Frame rate: it, the figure alone and the page's switch survive the settings file",
            kept.Visible && kept.Figures is [] && copy.MonitorFrames && (CompactMonitor.Shown(copy.CompactReadings) & Frames) != 0);

        var width = CompactMonitor.Widths(CompactMonitorItems.None, 16, _ => 0, 16, 40, 30, frames: 47);
        check("Frame rate: its own width", width(Frames) == 47 && CompactMonitor.Fit(CompactMonitorItems.Cpu | Frames, width, 6, 16 + 6 + 47 - 1) == CompactMonitorItems.Cpu);
    }

    private static void Admission(Action<string, bool> check)
    {
        var all = CompactMonitor.Normalize(null);
        foreach (var reading in all) reading.Visible = reading.Item == Frames;
        var admission = new StripAdmission();
        check("Frame rate: waits for its first figure", (admission.Hidden & Frames) != 0);
        check("Frame rate: keeps the sampler running while it is not known whether frame rates can be read", admission.Awaits(all, null, SensorStates.Unknown));
        check("Frame rate: ...and stops asking once they cannot", !admission.Awaits(all, null, SensorStates.Unknown with { Frames = SensorState.None }));

        var none = MonitorSnapshot.Empty(4, "t");
        var (waiting, _) = admission.Update(none, all, SensorStates.Unknown with { Frames = SensorState.Found });
        check("Frame rate: no figure yet, it waits", (waiting & Frames) != 0);
        var idle = none with { Frames = new FrameSample(0, "Explorer") };
        var (hidden, arriving) = admission.Update(idle, all, SensorStates.Unknown with { Frames = SensorState.Found });
        check("Frame rate: a program drawing nothing gives 0, a figure, and the reading joins with it", (hidden & Frames) == 0 && arriving == Frames);
        var (stays, _) = admission.Update(none, all, SensorStates.Unknown with { Frames = SensorState.Found });
        check("Frame rate: a reading missing for a moment keeps its place", (stays & Frames) == 0);
        var (gone, _) = admission.Update(null, all, SensorStates.Unknown with { Frames = SensorState.None });
        check("Frame rate: it leaves once frame rates cannot be read", (gone & Frames) != 0);
    }

    private static void Colors(Action<string, bool> check)
    {
        var sea = MonitorColors.For(MonitorPalette.Sea);
        check("Frame rate: without a color of its own it takes the GPU color", sea[MonitorRole.Frames] == sea.Gpu);
        var custom = new MonitorCustomColors { Base = MonitorPalette.Sea };
        custom.Set(MonitorRole.Frames, "#123456");
        check("Frame rate: the custom scheme gives it its own", custom.Changed && custom.Over(sea)[MonitorRole.Frames] == "#123456" && custom.Over(sea).Gpu == sea.Gpu);
    }
}

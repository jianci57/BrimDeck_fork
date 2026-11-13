using System.Text.Json;
using BrimDeck.Core;

// The three memory modes (MemoryPlan) and the switch they replaced (SettingsMigrations.MemoryModeFromSwitch).
internal static class MemoryModeTests
{
    public static void Run(Action<string, bool> check)
    {
        check("Memory modes: a new file takes the balanced mode", new DeckSettings().MemoryMode == MemoryMode.Balanced
            && SettingsMigrations.Read("""{"AppVersion":"1.3.0"}""").MemoryMode == MemoryMode.Balanced);
        check("Memory modes: freeing memory when idle becomes the saver mode",
            SettingsMigrations.Read("""{"AppVersion":"1.3.0","TrimMemoryWhenIdle":true}""").MemoryMode == MemoryMode.Saver
            && SettingsMigrations.Read("""{"AppVersion":"1.2.5","TrimMemoryWhenIdle":true}""").MemoryMode == MemoryMode.Saver);
        check("Memory modes: the switch turned off becomes the balanced mode",
            SettingsMigrations.Read("""{"AppVersion":"1.3.0","TrimMemoryWhenIdle":false}""").MemoryMode == MemoryMode.Balanced);
        check("Memory modes: a saved mode wins over the old switch written beside it",
            SettingsMigrations.Read("""{"AppVersion":"1.3.0","TrimMemoryWhenIdle":true,"MemoryMode":2}""").MemoryMode == MemoryMode.Smooth);
        check("Memory modes: an unknown mode falls back to balanced",
            SettingsMigrations.Read("""{"AppVersion":"1.3.0","MemoryMode":9}""").MemoryMode == MemoryMode.Balanced);

        var smooth = new DeckSettings { MemoryMode = MemoryMode.Smooth };
        var written = JsonSerializer.Serialize(smooth);
        var again = SettingsMigrations.Read(written);
        check("Memory modes: the mode is saved, and the old switch written to match it for older versions",
            again.MemoryMode == MemoryMode.Smooth && written.Contains("\"TrimMemoryWhenIdle\":false")
            && JsonSerializer.Serialize(new DeckSettings { MemoryMode = MemoryMode.Saver }).Contains("\"TrimMemoryWhenIdle\":true"));

        var (saver, balanced, fluent) = (MemoryPlan.For(MemoryMode.Saver), MemoryPlan.For(MemoryMode.Balanced), MemoryPlan.For(MemoryMode.Smooth));
        check("Memory modes: the saver and balanced modes give memory back while collapsed, and the smooth mode does not",
            saver.TrimWhenIdle && balanced.TrimWhenIdle && !fluent.TrimWhenIdle);
        check("Memory modes: the saver mode cleans up sooner than the balanced one, and the more a mode keeps, the longer readings linger",
            saver.TrimAfter < balanced.TrimAfter && saver.TrimEvery < balanced.TrimEvery
            && saver.ReadingsLinger < balanced.ReadingsLinger && balanced.ReadingsLinger < fluent.ReadingsLinger);
        check("Memory modes: the more a mode keeps, the longer a switched-off page keeps what it built, and none lets it go at once",
            saver.ReleaseAfter >= TimeSpan.FromSeconds(30) && saver.ReleaseAfter < balanced.ReleaseAfter && balanced.ReleaseAfter < fluent.ReleaseAfter);

        // The clean-up's schedule (TrimDue): used is the last use, trimmed the last clean-up.
        var used = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        check("Memory modes: what starting leaves behind is given back once in every mode, a minute after the last use at most",
            saver.TrimDue(used, null) == used + saver.TrimAfter && balanced.TrimDue(used, null) == used + MemoryPlan.OwedAfter
            && fluent.TrimDue(used, null) == used + MemoryPlan.OwedAfter && saver.TrimAfter < MemoryPlan.OwedAfter && MemoryPlan.OwedAfter < balanced.TrimAfter);
        check("Memory modes: after that the smooth mode gives nothing back",
            fluent.TrimDue(used, used) is null && fluent.TrimDue(used.AddHours(1), used) is null);
        check("Memory modes: a clean-up waits its quiet stretch after a use",
            balanced.TrimDue(used, used.AddHours(-1)) == used + balanced.TrimAfter && saver.TrimDue(used, used.AddHours(-1)) == used + saver.TrimAfter);
        check("Memory modes: with nothing used, the clean-up comes again every period, however often the usage is read meanwhile",
            balanced.TrimDue(used, used.AddMinutes(4)) == used.AddMinutes(4) + balanced.TrimEvery
            && saver.TrimDue(used, used.AddMinutes(4)) == used.AddMinutes(4) + saver.TrimEvery);
        check("Memory modes: a use just before the period ends still waits its quiet stretch",
            balanced.TrimDue(used.AddMinutes(13), used.AddMinutes(4)) == used.AddMinutes(13) + balanced.TrimAfter);
        check("Memory modes: the smooth mode still stops the readings after a while",
            fluent.ReadingsLinger <= TimeSpan.FromMinutes(1));
        check("Memory modes: the saver mode reads the collapsed row every 2 seconds, an open page at the chosen interval",
            saver.ReadingSeconds(1, pageOpen: false) == 2 && saver.ReadingSeconds(2, pageOpen: false) == 2 && saver.ReadingSeconds(1, pageOpen: true) == 1
            && balanced.ReadingSeconds(1, pageOpen: false) == 1 && fluent.ReadingSeconds(1, pageOpen: false) == 1);
        check("Memory modes: the more a mode keeps, the longer the bars' capture runs on and the more covers it keeps",
            saver.MediaLinger == TimeSpan.Zero && saver.MediaLinger < balanced.MediaLinger && balanced.MediaLinger < fluent.MediaLinger
            && saver.RecentCovers < balanced.RecentCovers && balanced.RecentCovers < fluent.RecentCovers);
        check("Memory modes: the saver mode lets the monitor page's extras go when it closes and checks a far pointer less often",
            !saver.LingerExtras && balanced.LingerExtras && fluent.LingerExtras
            && saver.PointerFarMilliseconds > balanced.PointerFarMilliseconds && balanced.PointerFarMilliseconds == fluent.PointerFarMilliseconds);
        check("Memory modes: only the smooth mode keeps caches, warms up at start and holds off full collections while open",
            fluent.KeepCaches && fluent.WarmAtStart && fluent.QuietWhileOpen && !balanced.KeepCaches && !balanced.WarmAtStart && !balanced.QuietWhileOpen
            && !saver.KeepCaches && !saver.WarmAtStart && !saver.QuietWhileOpen);
    }
}

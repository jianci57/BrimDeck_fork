using BrimDeck.Core;

// Whether the session's display is off while the session is unlocked (DisplayWatch): what Windows reports, and the user being
// there bringing it back whatever was reported last.
internal static class DisplayWatchTests
{
    public static void Run(Action<string, bool> check)
    {
        var watch = new DisplayWatch();
        check("Display: a report of 0 turns the display off", watch.Report(4, 0) && watch.Off);
        check("Display: the same report changes nothing", !watch.Report(4, 0) && watch.Off);
        check("Display: a dimmed display is still seen", watch.Report(4, 2) && !watch.Off);
        watch.Report(4, 0);
        check("Display: a short report is not a report", !watch.Report(2, 1) && watch.Off);
        check("Display: the user being there turns it back on", watch.Seen() && !watch.Off && !watch.Seen());    }
}

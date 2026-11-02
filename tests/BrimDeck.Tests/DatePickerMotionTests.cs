using BrimDeck.Core;

// The date range picker's motion (DatePickerMotion): where days lie in a month's grid, how the markers reach a new day, the
// range band, the validation message, the month change's direction and hand-over, and the durations from settings.
internal static class DatePickerMotionTests
{
    public static void Run(Action<string, bool> check)
    {
        // October 2026 starts on a Thursday: its grid starts on Monday 28 September.
        var october = new DateTime(2026, 10, 1);
        check("A month's grid starts on the Monday on or before its first day", DatePickerMotion.FirstShown(october) == new DateTime(2026, 9, 28));
        check("A month starting on a Monday starts its own grid", DatePickerMotion.FirstShown(new DateTime(2026, 6, 15)) == new DateTime(2026, 6, 1));
        check("A day's cell counts from the grid's first day", DatePickerMotion.CellOf(october, new DateTime(2026, 10, 24)) == 26
            && DatePickerMotion.Column(26) == 5 && DatePickerMotion.Row(26) == 3);
        check("Days outside the grid have no cell", DatePickerMotion.CellOf(october, new DateTime(2026, 9, 27)) == -1
            && DatePickerMotion.CellOf(october, new DateTime(2026, 11, 8, 15, 0, 0)) == 41 && DatePickerMotion.CellOf(october, new DateTime(2026, 11, 9)) == -1);

        check("Markers glide within a month", DatePickerMotion.Plan(10, 20, true) == MarkerMove.Glide);
        check("A marker on its day stays", DatePickerMotion.Plan(10, 10, true) == MarkerMove.Stay);
        check("A marker that was not shown appears in place", DatePickerMotion.Plan(-1, 12, true) == MarkerMove.Appear);
        check("A month change brings markers with the grid, never gliding across it", DatePickerMotion.Plan(10, 20, false) == MarkerMove.Appear);
        check("A marker whose day is not shown hides", DatePickerMotion.Plan(10, -1, true) == MarkerMove.Hide && DatePickerMotion.Plan(-1, -1, false) == MarkerMove.Hide);

        DateTime start = new(2026, 10, 5), end = new(2026, 10, 12);
        check("The band covers the days between the ends", DatePickerMotion.Band(new DateTime(2026, 10, 8), start, end) == BandPart.Full);
        check("The band runs into the ends' markers from the inside", DatePickerMotion.Band(start, start, end) == BandPart.Leading && DatePickerMotion.Band(end, start, end) == BandPart.Trailing);
        check("No band outside the range, for one day, or for an inverted range", DatePickerMotion.Band(new DateTime(2026, 10, 4), start, end) == BandPart.None
            && DatePickerMotion.Band(start, start, start) == BandPart.None && DatePickerMotion.Band(new DateTime(2026, 10, 8), end, start) == BandPart.None);

        check("The message shows only while the start is after the end", DatePickerMotion.ShowsError(end, start) && !DatePickerMotion.ShowsError(start, end)
            && !DatePickerMotion.ShowsError(start, start.AddHours(5)));

        check("Later months travel forward, earlier ones back", DatePickerMotion.Direction(october, new DateTime(2026, 11, 3)) == 1
            && DatePickerMotion.Direction(october, new DateTime(2025, 12, 1)) == -1 && DatePickerMotion.Direction(october, new DateTime(2026, 10, 30)) == 0
            && DatePickerMotion.Direction(new DateTime(2025, 12, 1), new DateTime(2026, 1, 1)) == 1);

        var timing = DatePickerTiming.Fixed;

        // The old month leaves before the new one arrives: whenever the new one shows, the old one is under a tenth. Checked for
        // the picker's own timing and for much shorter and longer ones, so the rule does not hang on one pair of lengths.
        bool apart = true, forward = true;
        foreach (var t in new[] { timing with { Leave = 50, Arrive = 120 }, timing, timing with { Leave = 400, Arrive = 1000 } })
            for (double ms = 0; ms <= t.MonthLength + 20; ms += 4)
            {
                var frame = DatePickerMotion.Slide(ms, 1, t);
                if (frame.InOpacity > 0 && frame.OutOpacity >= .1) apart = false;
                if (frame.OutX > 0 || frame.InX < 0) forward = false;
            }
        check("A month change never fades two months over each other", apart);
        check("Forward, the old month leaves to the left and the new one comes from the right", forward);
        var first = DatePickerMotion.Slide(0, -1, timing); var last = DatePickerMotion.Slide(timing.MonthLength, -1, timing);
        check("Back, it starts in place with the new month waiting on the left, and ends with the new month in place",
            first.OutOpacity == 1 && first.OutX == 0 && first.InOpacity == 0 && first.InX == -DatePickerMotion.Travel && last.Done && last.InX == 0 && last.InOpacity == 1);
        var mid = DatePickerMotion.Slide(timing.ArriveDelay + timing.Arrive / 2.0, 1, timing);
        check("Halfway through its arrival the new month is mostly there (the monitor's move)", mid.InOpacity is > .6 and < .75 && !mid.Done);
    }
}

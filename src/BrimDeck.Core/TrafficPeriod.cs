namespace BrimDeck.Core;

// The span the network's traffic is summed over (DeckSettings.MonitorTrafficPeriod): since midnight today, or the last day, 7 days or
// 30 days back from now, the moving windows the Data usage page in Windows Settings counts its own figures over.
public enum TrafficPeriod { Today, Day, Week, Month }

public static class TrafficPeriods
{
    // Where the span starts for a reading taken at now: midnight in the local time zone for Today (a day of 23 or 25 hours where the
    // clock changes), else a fixed length back from now.
    public static DateTimeOffset Start(TrafficPeriod period, DateTimeOffset now, TimeZoneInfo? zone = null) => period switch
    {
        TrafficPeriod.Day => now.AddDays(-1),
        TrafficPeriod.Week => now.AddDays(-7),
        TrafficPeriod.Month => now.AddDays(-30),
        _ => Midnight(now, zone ?? TimeZoneInfo.Local)
    };

    private static DateTimeOffset Midnight(DateTimeOffset now, TimeZoneInfo zone)
    {
        var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        return new DateTimeOffset(date, zone.GetUtcOffset(date));
    }

    // The span's name, before its figure on the page and in the tooltips: "当天", "1 天", "7 天", "30 天".
    public static string Name(TrafficPeriod period) => period switch
    {
        TrafficPeriod.Day => Loc.T("1 天", "1 day"),
        TrafficPeriod.Week => Loc.T("7 天", "7 days"),
        TrafficPeriod.Month => Loc.T("30 天", "30 days"),
        _ => Loc.T("当天", "Today")
    };
}

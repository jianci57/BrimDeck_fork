using System.Text.Json;
using BrimDeck.Core;

// The traffic over a period from Windows' record (TrafficPeriods: where each period starts, its name, the setting), the collapsed row's
// readings that show it (DownloadTotal, UploadTotal), how it is written and spoken.
static class TrafficTotalTests
{
    private const CompactMonitorItems Down = CompactMonitorItems.DownloadTotal, Up = CompactMonitorItems.UploadTotal, Cpu = CompactMonitorItems.Cpu;

    public static void Run(Action<string, bool> check)
    {
        Periods(check);
        Setting(check);
        Format(check);
        Readings(check);
        Saved(check);
        Speech(check);
    }

    private static void Periods(Action<string, bool> check)
    {
        var china = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var now = new DateTimeOffset(2026, 11, 15, 19, 0, 0, TimeSpan.FromHours(8));
        check("Traffic period: 1, 7 and 30 days count back from now", TrafficPeriods.Start(TrafficPeriod.Day, now, china) == now.AddDays(-1)
            && TrafficPeriods.Start(TrafficPeriod.Week, now, china) == now.AddDays(-7) && TrafficPeriods.Start(TrafficPeriod.Month, now, china) == now.AddDays(-30));
        var midnight = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.FromHours(8));
        check("Traffic period: today starts at local midnight", TrafficPeriods.Start(TrafficPeriod.Today, now, china) == midnight
            && TrafficPeriods.Start(TrafficPeriod.Today, now.ToUniversalTime(), china) == midnight);
        check("Traffic period: just after midnight, today is the new day though the UTC date is the day before",
            TrafficPeriods.Start(TrafficPeriod.Today, new DateTimeOffset(2026, 11, 14, 16, 30, 0, TimeSpan.Zero), china) == midnight);
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        check("Traffic period: on the day the clocks change, today starts at that midnight's offset",
            TrafficPeriods.Start(TrafficPeriod.Today, new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.FromHours(-7)), pacific) == new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-8)));
        var language = Loc.Language;
        try
        {
            Loc.Use(Loc.Chinese);
            check("Traffic period: named as the setting offers them", TrafficPeriods.Name(TrafficPeriod.Today) == "当天" && TrafficPeriods.Name(TrafficPeriod.Day) == "1 天"
                && TrafficPeriods.Name(TrafficPeriod.Week) == "7 天" && TrafficPeriods.Name(TrafficPeriod.Month) == "30 天");
            Loc.Use(Loc.English);
            check("Traffic period: in English", TrafficPeriods.Name(TrafficPeriod.Today) == "Today" && TrafficPeriods.Name(TrafficPeriod.Month) == "30 days");
        }
        finally { Loc.Use(language); }
    }

    private static void Setting(Action<string, bool> check)
    {
        check("Traffic setting: today by default, also for a file saved before it", new DeckSettings().MonitorTrafficPeriod == TrafficPeriod.Today
            && SettingsMigrations.Read("""{"AppVersion":"1.3.1"}""").MonitorTrafficPeriod == TrafficPeriod.Today);
        var settings = SettingsMigrations.Read("""{"AppVersion":"1.3.1"}""");
        settings.MonitorTrafficPeriod = TrafficPeriod.Week;
        check("Traffic setting: a choice round-trips", SettingsMigrations.Read(JsonSerializer.Serialize(settings)).MonitorTrafficPeriod == TrafficPeriod.Week);
        check("Traffic setting: an unknown value reads as today", SettingsMigrations.Read("""{"AppVersion":"1.3.1","MonitorTrafficPeriod":9}""").MonitorTrafficPeriod == TrafficPeriod.Today);
        check("Traffic setting: one for every profile, as the update interval", !Profiles.IsProfileKey(nameof(DeckSettings.MonitorTrafficPeriod)));
    }

    private static void Format(Action<string, bool> check)
    {
        string T(double bytes) => MonitorFormat.Total(bytes).Text;
        check("Total format: the reporter's figures read 21.0 MB and 202 MB", T(21_000_000) == "21.0 MB" && T(202_000_000) == "202 MB");
        check("Total format: bytes, kilobytes, gigabytes, terabytes", T(0) == "0 B" && T(512) == "512 B" && T(1_500) == "1.50 KB" && T(512_000) == "512 KB"
            && T(1_240_000_000) == "1.24 GB" && T(2_500_000_000_000) == "2.50 TB");
        check("Total format: the edges move up a unit, never show 1000", T(999) == "999 B" && T(999.6) == "1.00 KB" && T(999_400) == "999 KB" && T(999_600) == "1.00 MB"
            && T(999_600_000) == "1.00 GB");
        check("Total format: a bad value reads as zero", T(-5) == "0 B" && T(double.NaN) == "0 B" && T(double.PositiveInfinity) == "0 B");
        var compact = MonitorFormat.CompactTotal(21_000_000);
        check("Total format: the collapsed row's figure has a one-letter unit", compact.Number + compact.Unit == "21.0M" && MonitorFormat.CompactTotal(202_000_000) is { Number: "202", Unit: "M" }
            && MonitorFormat.CompactTotal(0) is { Number: "0", Unit: "B" } && MonitorFormat.CompactTotal(3_000_000_000) is { Number: "3.00", Unit: "G" });
    }

    private static MonitorSnapshot Snapshot(NetworkSample? network) => MonitorSnapshot.Empty(4, "test") with
    {
        Network = network is { } sample ? Reading<NetworkSample>.Of(sample) : Reading<NetworkSample>.Unavailable("waiting")
    };

    private static void Readings(Action<string, bool> check)
    {
        var order = CompactMonitor.Order.ToList();
        check("Traffic readings: each right under its rate", order.IndexOf(Down) == order.IndexOf(CompactMonitorItems.Download) + 1 && order.IndexOf(CompactMonitorItems.Upload) == order.IndexOf(Down) + 1
            && order.IndexOf(Up) == order.IndexOf(CompactMonitorItems.Upload) + 1);
        check("Traffic readings: one channel each, in their rate's color", CompactMonitor.Channels(Down) == 1 && CompactMonitor.Role(Down) == MonitorRole.Download
            && CompactMonitor.Role(Up) == MonitorRole.Upload);
        check("Traffic readings: no figure switch, as they are always text", !CompactMonitor.HasValueChoice(Down) && !CompactMonitor.HasValueChoice(Up));
        var leave = CompactMonitor.LeaveOrder.ToList();
        check("Traffic readings: they leave a short row before the rates, after the machine's battery", leave.IndexOf(Up) == leave.IndexOf(CompactMonitorItems.Charge) + 1
            && leave.IndexOf(Down) == leave.IndexOf(Up) + 1 && leave.IndexOf(CompactMonitorItems.Upload) == leave.IndexOf(Down) + 1);
        var widths = CompactMonitor.Widths(CompactMonitorItems.None, 16, _ => 0, 16, 30, 40, traffic: 50);
        check("Traffic readings: their own width, not the rate's", widths(Down) == 50 && widths(Up) == 50 && widths(CompactMonitorItems.Download) == 40);

        var sample = new NetworkSample(1_000, 2_000) { DownTotalBytes = 202_000_000, UpTotalBytes = 21_000_000, TotalPeriod = TrafficPeriod.Week };
        var snapshot = Snapshot(sample);
        check("Traffic total: each reading's own bytes in this snapshot", CompactTraffic.Of(Down, snapshot) == 202_000_000 && CompactTraffic.Of(Up, snapshot) == 21_000_000);
        check("Traffic total: none without a reading, or one that carries none, or for another reading",
            CompactTraffic.Of(Down, null) is null && CompactTraffic.Of(Down, Snapshot(null)) is null && CompactTraffic.Of(Down, Snapshot(new NetworkSample(1, 1))) is null
            && CompactTraffic.Of(CompactMonitorItems.Download, snapshot) is null && CompactTraffic.Of(Cpu, snapshot) is null);
        check("Traffic total: a sample made as before carries none", new NetworkSample(1, 2) is { DownTotalBytes: null, UpTotalBytes: null });
        check("Traffic total: the sample says which period it covers", snapshot.Network.OrDefault?.TotalPeriod == TrafficPeriod.Week);
    }

    private static void Saved(Action<string, bool> check)
    {
        var old = SettingsMigrations.Read("""{"AppVersion":"1.3.1","CompactReadings":[{"Item":1},{"Item":2},{"Item":4},{"Item":8},{"Item":16,"Figures":["Total"]},{"Item":32},{"Item":128},{"Item":512},{"Item":1024},{"Item":2048}]}""");
        var readings = old.CompactReadings;
        var items = readings.Select(r => r.Item).ToList();
        check("Traffic saved: a table saved before them gets both, hidden, each under its rate", items.IndexOf(Down) == items.IndexOf(CompactMonitorItems.Download) + 1
            && items.IndexOf(Up) == items.IndexOf(CompactMonitorItems.Upload) + 1 && !readings.Single(r => r.Item == Down).Visible && !readings.Single(r => r.Item == Up).Visible);
        var moved = SettingsMigrations.Read("""{"AppVersion":"1.3.1","CompactReadings":[{"Item":32},{"Item":2},{"Item":16}]}""").CompactReadings.Select(r => r.Item).ToList();
        check("Traffic saved: under the rate wherever the rate was dragged", moved.Take(5).SequenceEqual([CompactMonitorItems.Upload, Up, Cpu, CompactMonitorItems.Download, Down]));
        check("Traffic saved: a total chosen on the rate's menu before is dropped", readings.Single(r => r.Item == CompactMonitorItems.Download).Figures is null);

        var settings = SettingsMigrations.Read("""{"AppVersion":"1.3.1"}""");
        settings.CompactReadings.Single(r => r.Item == Up).Visible = true;
        var again = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("Traffic saved: a reading turned on round-trips, the other stays off", again.CompactReadings.Single(r => r.Item == Up).Visible
            && !again.CompactReadings.Single(r => r.Item == Down).Visible && (CompactMonitor.Shown(again.CompactReadings) & Up) != 0);

        var table = CompactMonitor.Normalize(null);
        CompactMonitor.Apply(table, CompactMonitorMode.Full);
        check("Traffic saved: Full does not turn them on", !table.Single(r => r.Item == Down).Visible && !table.Single(r => r.Item == Up).Visible);
        table.Single(r => r.Item == Down).Visible = true;
        check("Traffic saved: Full still matches with one turned on", CompactMonitor.Preset(table) == CompactMonitorMode.Full);
        CompactMonitor.Apply(table, CompactMonitorMode.Brief);
        check("Traffic saved: Brief leaves it on, and still matches", table.Single(r => r.Item == Down).Visible && CompactMonitor.Preset(table) == CompactMonitorMode.Brief);
    }

    private static void Speech(Action<string, bool> check)
    {
        var language = Loc.Language;
        try
        {
            Loc.Use(Loc.Chinese);
            check("Traffic speech: the traffic says it is traffic, and over which period", MonitorSpeech.StripTotal(true, 202_000_000, TrafficPeriod.Today) == "已下载 202 MB（当天）"
                && MonitorSpeech.StripTotal(false, 21_000_000, TrafficPeriod.Month) == "已上传 21.0 MB（30 天）");
            check("Traffic speech: a total not read yet says nothing", MonitorSpeech.StripTotal(true, null, TrafficPeriod.Today) == "");
            Loc.Use(Loc.English);
            check("Traffic speech: in English", MonitorSpeech.StripTotal(true, 202_000_000, TrafficPeriod.Week) == "Downloaded 202 MB (7 days)");
        }
        finally { Loc.Use(language); }
    }
}

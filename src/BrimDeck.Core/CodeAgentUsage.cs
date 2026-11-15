using System.Globalization;
using System.Text.Json;

namespace BrimDeck.Core;

// The CodeAgent CLI sign-in: the x-auth-token CodeAgent sends to its services, and when it expires. Held in memory only.
public sealed record CodeAgentCredential(string Token, DateTimeOffset? ExpiresAt)
{
    public bool Expired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;
    public override string ToString() => nameof(CodeAgentCredential);
}

// CodeAgent CLI keeps its sign-in in <CODEAGENT3_CONFIG_DIR or %USERPROFILE%\.cac>\.credentials.json as
// { idaasOAuth: { xAuthToken, xAuthExpires (Unix ms), mtoken, cookies, ... } }. Each value is the base64 of a Windows DPAPI
// blob for the current user, without extra entropy; only xAuthToken and xAuthExpires are read. xAuthToken lasts three days.
// Run while it is still valid, `codeagentcli auth status` only reports it; run once xAuthExpires has passed, it signs in again
// with the rest of the record within a few seconds, without a model request, and writes a new xAuthToken and xAuthExpires to
// the file. BrimDeck renews the sign-in that way (IDesktopSources.RenewCodeAgentAsync) when it has expired or the service
// refuses it; only a renewal that fails leaves signing in again to the user.
//
// The quota is the account's credit allowance, the same for every model: GET /api/v1/quota/usage with the X-Auth-Token header
// answers { quotas, external_quotas, internal_quotas }, each a list of { type, used_quota, total_quota, usage_ratio, reset_at }.
// quotas is the whole allowance, external_quotas the part for models from outside providers and internal_quotas the rest; the
// two parts add up to the whole. type is CPM (credits per month), CPD_Peak and CPD_Valley (credits per day in busy and idle
// hours). reset_at is "yyyy-MM-dd HH:mm:ss" without an offset, in China Standard Time: the daily quotas reset at the next
// midnight there.
public static class CodeAgentUsage
{
    // The address of the quota API, kept encoded so that it does not appear as plain text in the source. Anyone can decode it;
    // it is not a secret, and the sign-in is what grants access.
    public static readonly string QuotaUrl = Decode("MhUcHwVHq6Th98HXy9vbqqS09ryPiZXVYWp1OW5XQ1cXMjI5ezpKDwAY5+jgu+jMxd3Lka64tayHgN6UkWgjcmpIB1kHEjU+PS0BSBsGHeTv");
    private static string Decode(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        for (int i = 0; i < bytes.Length; i++) bytes[i] ^= (byte)(0x5A + i * 7);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
    // The busy and idle hours (CreditHours), encoded the same way; read with the same X-Auth-Token header.
    public static readonly string HoursUrl = Decode("MhUcHwVHq6Ti68/K3sHfpqSlva3IjJ2WcSdzdHsLXEFVJWYrJDwDBBxX9Ojh+e+NwsTZyKOk+riNhN+njGphY25iTUFCWDYYNysWDg0QU/W7vvvtw8ndz++qv7K4g4WQk2Rmezl/UVhLFCkjIjBxBw8XERHv+f30zJbd2NqgoIyzhdWImpJman5GR0JAUE4iJg41MAIIGA==");
    internal static readonly TimeSpan ServiceOffset = TimeSpan.FromHours(8);

    public static string CredentialFile(string home) => Path.Combine(home, ".credentials.json");
    // xAuthExpires as it stands in the file, read without decrypting anything; 0 when there is none.
    public static long StoredExpiry(string home)
    {
        using var doc = SharedFile.ReadJson(CredentialFile(home));
        return (long)(doc?.RootElement.Get("idaasOAuth").Get("xAuthExpires").Number() ?? 0);
    }
    // The CLI that renews the sign-in: bin\codeagentcli.exe of the installation, found under Program Files or through the
    // codeagent launcher on PATH; null when CodeAgent CLI is not installed.
    public static string? FindCli(string programFiles, string? path)
    {
        var folders = new List<string> { Path.Combine(programFiles, "CodeAgentCLI") };
        foreach (var folder in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (File.Exists(Path.Combine(folder, "codeagent.bat"))) folders.Add(folder);
        return folders.Select(folder => Path.Combine(folder, "bin", "codeagentcli.exe")).FirstOrDefault(File.Exists);
    }

    // unprotect turns a stored value into its text; null when it cannot (another user's file, or no DPAPI on this system).
    public static CodeAgentCredential? ReadCredential(string home, Func<string, string?> unprotect)
    {
        using var doc = SharedFile.ReadJson(CredentialFile(home));
        var auth = doc?.RootElement.Get("idaasOAuth") ?? default;
        var stored = auth.Get("xAuthToken").Text();
        if (stored.Length == 0) return null;
        var token = unprotect(stored);
        return string.IsNullOrEmpty(token) ? null : new(token, auth.Get("xAuthExpires").Date());
    }

    // The whole allowance first, then the parts for outside and internal models, each in the order month, busy-hour day,
    // idle-hour day. Labels are Chinese parts joined by "·" that Loc.Label translates.
    // hours, when known, is attached to the busy-hour and idle-hour daily quotas (UsageMetric.Hours).
    public static List<UsageMetric> Quotas(JsonElement root, CreditHours? hours = null)
    {
        var result = new List<UsageMetric>();
        foreach (var (field, group) in new[] { ("quotas", ""), ("external_quotas", "外部模型"), ("internal_quotas", "内部模型") })
            foreach (var item in root.Get(field).Items().OrderBy(item => Order(item.Get("type").Text())))
            {
                var type = item.Get("type").Text();
                if (item.Get("total_quota").Number() is not { } total || total <= 0 || item.Get("used_quota").Number() is not { } used) continue;
                var (name, window) = type switch
                {
                    "CPM" => ("每月额度", (int?)null),
                    "CPD_Peak" => ("忙时每日额度", 1440),
                    "CPD_Valley" => ("闲时每日额度", 1440),
                    _ => (type, (int?)null)
                };
                var label = group.Length == 0 ? name : group + " · " + name;
                result.Add(new UsageMetric(MetricKind.Count, label)
                {
                    Id = (field + ":" + type).ToLowerInvariant(), Window = window, Unit = "积分",
                    Used = (decimal)Math.Max(0, used), Total = (decimal)total, ResetAt = ResetAt(item.Get("reset_at").Text()),
                    Hours = hours, Period = type switch { "CPD_Peak" => CreditPeriod.Busy, "CPD_Valley" => CreditPeriod.Idle, _ => null }
                });
            }
        return result;
    }

    private static int Order(string type) => type switch { "CPM" => 0, "CPD_Peak" => 1, "CPD_Valley" => 2, _ => 3 };

    public static DateTimeOffset? ResetAt(string text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, ServiceOffset) : null;
}

public enum CreditPeriod { Busy, Normal, Idle }

// When CodeAgent credits cost more or less. The busy-idle definition answers { busy_period, normal_period, idle_period }, each with
// a coefficient ("3x") and weekday_coeff and weekend_coeff: lists of { min, max, value, label }, hours of the day in China Standard
// Time from min up to max. An hour that no busy or normal list covers is idle: idle_period lists the same idle hours, and its
// description names the days off ("非工作日0-24点") that no list covers. Weekdays are Monday to Friday; public holidays are not
// known to the definition.
public sealed class CreditHours
{
    private readonly Dictionary<(CreditPeriod Period, bool Weekend), List<(double From, double To)>> _hours = [];
    public IReadOnlyDictionary<CreditPeriod, double> Rates { get; }

    private CreditHours(Dictionary<CreditPeriod, double> rates) => Rates = rates;

    public static CreditHours? Parse(JsonElement root)
    {
        var rates = new Dictionary<CreditPeriod, double>();
        var result = new CreditHours(rates);
        foreach (var (field, period) in new[] { ("busy_period", CreditPeriod.Busy), ("normal_period", CreditPeriod.Normal), ("idle_period", CreditPeriod.Idle) })
        {
            var part = root.Get(field);
            if (double.TryParse(part.Get("coefficient").Text().TrimEnd('x', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)) rates[period] = rate;
            foreach (var (list, weekend) in new[] { ("weekday_coeff", false), ("weekend_coeff", true) })
                foreach (var range in part.Get(list).Items())
                    if (range.Get("min").Number() is { } from && range.Get("max").Number() is { } to && from >= 0 && to <= 24 && from < to)
                    {
                        if (!result._hours.TryGetValue((period, weekend), out var hours)) result._hours[(period, weekend)] = hours = [];
                        hours.Add((from, to));
                    }
        }
        return result._hours.Keys.Any(key => key.Period == CreditPeriod.Busy) ? result : null;
    }

    public IReadOnlyList<(double From, double To)> Ranges(CreditPeriod period, bool weekend) =>
        _hours.TryGetValue((period, weekend), out var hours) ? hours : [];

    public CreditPeriod At(DateTimeOffset time)
    {
        var china = time.ToOffset(CodeAgentUsage.ServiceOffset);
        bool weekend = china.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        double hour = china.TimeOfDay.TotalHours;
        foreach (var period in new[] { CreditPeriod.Busy, CreditPeriod.Normal })
            if (Ranges(period, weekend).Any(range => hour >= range.From && hour < range.To)) return period;
        return CreditPeriod.Idle;
    }

    // The next time at which the period changes, looked for on the quarter hour (the definition uses whole hours) over the next
    // eight days; null when it never changes.
    public DateTimeOffset? NextChange(DateTimeOffset time)
    {
        var now = At(time);
        var china = time.ToOffset(CodeAgentUsage.ServiceOffset);
        var step = new DateTimeOffset(china.Year, china.Month, china.Day, china.Hour, china.Minute / 15 * 15, 0, china.Offset).AddMinutes(15);
        for (var at = step; at < time.AddDays(8); at = at.AddMinutes(15))
            if (At(at) != now) return at;
        return null;
    }

    // When the given period next starts (or, while it is on, ends): (time, true) while it is on.
    public (DateTimeOffset? At, bool On) Next(CreditPeriod period, DateTimeOffset time)
    {
        bool on = At(time) == period;
        if (on) return (NextChange(time), true);
        for (var at = time; at < time.AddDays(8);)
        {
            if (NextChange(at) is not { } change) return (null, false);
            if (At(change) == period) return (change, false);
            at = change;
        }
        return (null, false);
    }
}


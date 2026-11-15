using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

// Command Code (commandcode.ai). Its CLI and desktop app share %USERPROFILE%\.commandcode: the sign-in is an API key of the account,
// kept as apiKey in auth.json, and the session records are projects\<project>\<session>.jsonl. Keys created in Command Code Studio
// also work in other clients through its Provider API, so a key entered by hand (CommandCodeApi) reads the same quota.
//
// The quota is read from the account endpoints that Command Code's own /usage reads, each with the key as a bearer token:
// /alpha/whoami gives the organization (none for a personal account), /alpha/billing/credits the credits left and the 5-hour and
// weekly windows, /alpha/billing/subscriptions the plan and its billing period, and /alpha/usage/summary what the period spent.
public static class CommandCodeUsage
{
    public const string Api = "https://api.commandcode.ai";

    // A key Command Code refused (HTTP 401): signed out, revoked or mistyped.
    public sealed class RejectedException() : Exception("HTTP 401");

    public sealed record Reading(List<UsageMetric> Metrics, string Plan, List<string> Warnings);

    // The key Command Code is signed in with; null when it is not signed in or the file cannot be read.
    public static string? ReadKey(string home)
    {
        try
        {
            using var doc = SharedFile.ReadJson(Path.Combine(home, "auth.json"));
            var key = doc?.RootElement.Get("apiKey").Text().Trim() ?? "";
            return key.Length > 0 ? key : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // The prices Command Code lists for its models, by model id and alias, matched without regard to case as Command Code matches
    // them. Command Code keeps its model catalog in cache\model-catalog\v1\catalog-<endpoint>.json and works out the costUsd of
    // each reply from it: {"fetchedAt","body":{"data":[{"id","aliases","pricing":{"inputCost","outputCost","cacheReadCost",
    // "cacheWriteCost","cacheWrite1hCost","contextTiers":[{"maxContext","rates":{…}}]}}]}}, in US dollars per million tokens.
    // A tier applies while a request's input, cache included, is at most its maxContext; the last tier has none. A missing
    // one-hour cache write costs the five-minute one. Of several catalogs, the one fetched last is read; empty when there is none.
    public static IReadOnlyDictionary<string, ModelPrice> ReadPrices(string home)
    {
        var prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        var folder = Path.Combine(home, "cache", "model-catalog", "v1");
        JsonDocument? latest = null;
        try
        {
            if (!Directory.Exists(folder)) return prices;
            double newest = double.MinValue;
            foreach (var file in Directory.EnumerateFiles(folder, "catalog-*.json"))
            {
                JsonDocument? doc;
                try { doc = SharedFile.ReadJson(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
                if (doc is null) continue;
                var fetched = doc.RootElement.Get("fetchedAt").Number() ?? doc.RootElement.Get("renderedAt").Number() ?? 0;
                if (latest is null || fetched > newest) { latest?.Dispose(); latest = doc; newest = fetched; }
                else doc.Dispose();
            }
            if (latest is null) return prices;
            static decimal? Rate(JsonElement rates, string name) => rates.Get(name).Number() is >= 0 and < 1e9 and var value ? (decimal)value / 1_000_000m : null;
            static TokenPrices? Rates(JsonElement rates)
            {
                if (Rate(rates, "inputCost") is not { } input || Rate(rates, "outputCost") is not { } output) return null;
                var write = Rate(rates, "cacheWriteCost");
                return new(input, output, Rate(rates, "cacheReadCost"), write, Rate(rates, "cacheWrite1hCost") ?? write);
            }
            foreach (var model in latest.RootElement.Get("body").Get("data").Items())
            {
                var id = model.Get("id").Text();
                var pricing = model.Get("pricing");
                if (id.Length == 0 || Rates(pricing) is not { } listed) continue;
                var tiers = new List<PriceTier>();
                long start = 0;
                foreach (var tier in pricing.Get("contextTiers").Items())
                {
                    if (Rates(tier.Get("rates")) is not { } rates) { tiers.Clear(); break; }
                    tiers.Add(new(start, rates));
                    if (tier.Get("maxContext").Number() is not (> 0 and var max)) break;
                    start = (long)max + 1;
                }
                var price = new ModelPrice(id, listed, tiers);
                foreach (var name in model.Get("aliases").Items().Select(alias => alias.Text()).Prepend(id))
                    if (name.Length > 0) prices.TryAdd(name, price);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { latest?.Dispose(); }
        return prices;
    }

    // get sends GET to Api + path with the key and returns the status and body. The credits are required; the plan and what the
    // period spent only add to them, so a failure there leaves those parts out.
    public static async Task<Reading> ReadAsync(Func<string, Task<(int Status, string Body)>> get)
    {
        async Task<JsonElement> Read(string path, bool required)
        {
            var (status, body) = await get(path);
            if (status == 401) throw new RejectedException();
            if (status != 200)
            {
                if (!required) return default;
                throw new HttpRequestException("HTTP " + status, null, (HttpStatusCode)status);
            }
            try { using var doc = JsonDocument.Parse(body); return doc.RootElement.Clone(); }
            catch (JsonException) when (!required) { return default; }
            catch (JsonException) { throw new InvalidDataException(Loc.T("Command Code 返回的额度无法识别。", "The quota Command Code returned is not recognized.")); }
        }
        static string Query(params (string Name, string Value)[] pairs) =>
            string.Join("&", pairs.Where(pair => pair.Value.Length > 0).Select(pair => pair.Name + "=" + Uri.EscapeDataString(pair.Value))) is { Length: > 0 } query ? "?" + query : "";
        var whoami = await Read("/alpha/whoami", true);
        var organization = whoami.Get("org").Get("id").Text();
        var creditsRead = Read("/alpha/billing/credits" + Query(("orgId", organization)), true);
        var subscriptionRead = Read("/alpha/billing/subscriptions" + Query(("orgId", organization)), false);
        var credits = await creditsRead;
        var subscription = await subscriptionRead;
        var start = subscription.Get("data").Get("currentPeriodStart").Text();
        var summary = await Read("/alpha/usage/summary" + Query(("orgId", organization), ("since", start)), false);
        return Parse(credits, subscription, summary);
    }

    // credits: {"credits":{"monthlyCredits","purchasedCredits","freeCredits"},"windowLimits":{"fiveHour":{"used","cap","exceeded",
    // "resetAt"},"weekly":{…}}}, in US dollars of credit, resetAt in milliseconds. monthlyCredits is what is left of the plan's
    // monthly credits, so the month is shown as what the period spent from them (summary.totalMonthlyCredits) out of that plus
    // what is left; it resets at the end of the billing period. Purchased and free credits never expire and the windows do not
    // limit them; they are shown as extra credits when there are any.
    public static Reading Parse(JsonElement credits, JsonElement subscription, JsonElement summary)
    {
        if (credits.Get("credits").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(Loc.T("Command Code 没有返回额度。", "Command Code returned no quota."));
        var metrics = new List<UsageMetric>();
        var warnings = new List<string>();
        var limits = credits.Get("windowLimits");
        foreach (var (field, label, minutes) in new[] { ("fiveHour", "5 小时额度", 300), ("weekly", "每周额度", 10080) })
        {
            var window = limits.Get(field);
            if (window.Get("cap").Number() is not (> 0 and var cap)) continue;
            var used = Math.Max(0, window.Get("used").Number() ?? 0);
            metrics.Add(new(MetricKind.Percent, label)
            {
                Id = field, Window = minutes, Percent = Math.Clamp(used / cap * 100, 0, 100), ResetAt = window.Get("resetAt").Date(),
                Note = Dollars(used) + " / " + Dollars(cap)
            });
            if (window.Get("exceeded").ValueKind == JsonValueKind.True)
                warnings.Add(minutes == 300 ? Loc.T("已用完 5 小时额度，重置前只能使用额外积分", "The 5-hour limit is used up; only extra credits can be used until it resets.")
                    : Loc.T("已用完每周额度，重置前只能使用额外积分", "The weekly limit is used up; only extra credits can be used until it resets."));
        }
        var balance = credits.Get("credits");
        double left = Math.Max(0, balance.Get("monthlyCredits").Number() ?? 0);
        double extra = Math.Max(0, balance.Get("purchasedCredits").Number() ?? 0) + Math.Max(0, balance.Get("freeCredits").Number() ?? 0);
        var period = subscription.Get("data");
        bool active = period.Get("status").Text() is "active" or "trialing" or "past_due";
        var periodEnd = active ? period.Get("currentPeriodEnd").Date() : null;
        if (summary.Get("totalMonthlyCredits").Number() is { } spent && Math.Max(0, spent) + left > 0)
        {
            spent = Math.Max(0, spent);
            metrics.Add(new(MetricKind.Percent, "每月额度")
            { Id = "monthly", Percent = Math.Clamp(spent / (spent + left) * 100, 0, 100), ResetAt = periodEnd, Note = Dollars(spent) + " / " + Dollars(spent + left) });
        }
        else if (left > 0)
            metrics.Add(new(MetricKind.Balance, "每月额度") { Id = "monthly", Amount = (decimal)left, Currency = "USD", ResetAt = periodEnd });
        if (extra > 0 || metrics.Count == 0)
            metrics.Add(new(MetricKind.Balance, "额外积分") { Id = "extra", Amount = (decimal)extra, Currency = "USD" });
        return new(metrics, active ? PlanName(period.Get("planId").Text()) : "", warnings);
    }

    // Plan ids look like individual-go-v1, individual-goat, individual-pro, individual-provider, individual-max, individual-ultra
    // and teams-pro; the version suffix is not part of the name.
    public static string PlanName(string id)
    {
        var plan = Regex.Replace(id.Trim().ToLowerInvariant().Replace('_', '-'), @"-v\d+$", "");
        static string Title(string text) => string.Join(" ", text.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
        return plan switch
        {
            "" => "",
            "individual-goat" => "GOAT",
            _ when plan.StartsWith("individual-", StringComparison.Ordinal) => Title(plan["individual-".Length..]),
            _ => Title(plan)
        };
    }

    private static string Dollars(double amount) => "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);
}

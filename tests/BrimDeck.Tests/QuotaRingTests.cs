using BrimDeck.Core;

// One collapsed ring per application (QuotaRing.For): the shortest stated window outside, every other quota inside, each with
// its highest share; a source with one window keeps one ring.
internal static class QuotaRingTests
{
    internal static void Run(Action<string, bool> check)
    {
        static QuotaRing Of(params Quota[] quotas) => QuotaRing.For(new ProviderSnapshot(ProviderId.Claude) { Quotas = [.. quotas] });

        var codex = Of(new("5 小时额度", 35, null, 300), new("每周额度", 88, null, 10080));
        check("Quota ring: five hours outside, the week inside", codex is { Outer: 35, Inner: 88 } && codex.Detail.Contains("35") && codex.Detail.Contains("88"));

        var claude = Of(new("每周额度", 41, null, 10080), new("Sonnet 每周", 55, null, 10080), new("5 小时额度", 72, null, 300));
        check("Quota ring: the inner ring takes the highest of the weekly quotas", claude is { Outer: 72, Inner: 55 });

        var antigravity = Of(new("Gemini · 5 小时", 60, null, 300), new("Gemini · 每周", 20, null, 10080), new("Claude · 5 小时", 12, null, 300), new("Claude · 每周", 8, null, 10080));
        check("Quota ring: four quotas in two windows make one pair", antigravity is { Outer: 60, Inner: 20 });

        var monthly = Of(new("每月", 10, null, 43200), new("每日", 40, null, 1440));
        check("Quota ring: a daily quota outside a monthly one", monthly is { Outer: 40, Inner: 10 });

        var unstated = Of(new("5 小时额度", 30, null, 300), new("Gemini Pro", 64, null));
        check("Quota ring: a quota without a window goes inside one with a window", unstated is { Outer: 30, Inner: 64 });

        check("Quota ring: one window keeps one ring with its highest share",
            Of(new Quota("每周额度", 70, null, 10080)) is { Outer: 70, Inner: null } && Of(new("A 每周", 20, null, 10080), new("B 每周", 45, null, 10080)) is { Outer: 45, Inner: null });
        check("Quota ring: quotas without windows keep one ring with the highest share", Of(new("Gemini Pro", 30, null), new("Claude", 50, null)) is { Outer: 50, Inner: null });
        check("Quota ring: a share that is not a number is left out", Of(new("5 小时额度", double.NaN, null, 300), new("每周额度", 25, null, 10080)) is { Outer: 25, Inner: null });

        var balance = QuotaRing.For(new ProviderSnapshot(ProviderId.Claude) { Metrics = [new UsageMetric(MetricKind.Balance, "账户余额") { Amount = 5m, Currency = "USD" }] });
        var nothing = QuotaRing.For(new ProviderSnapshot(ProviderId.Claude) { StatusLabel = "未登录" });
        check("Quota ring: a balance without a share fills one ring, a source with nothing leaves it empty",
            balance is { Outer: 100, Inner: null } && balance.Detail.Contains("5.00") && nothing is { Outer: 0, Inner: null, Detail: "未登录" });

        check("Quota ring: 12 DIP alone, 20 with a pair", QuotaRing.Diameter(false) == 12 && QuotaRing.Diameter(true) == 20);

        // The row takes the larger rings at its scale, and keeps 12 DIP without a pair.
        var single = CompactMusicLayout.Calculate(1000, 1, false, 3, false, 0);
        var pair = CompactMusicLayout.Calculate(1000, 2, false, 3, false, 0, ring: QuotaRing.Pair);
        check("Quota ring: the row's rings follow the diameter and the scale", single.RingSize == 12 && pair.RingSize == 40 && pair.RingWidth == 3 * 40 + 2 * 16);
        check("Quota ring: an auto-width row grows by the larger rings",
            CompactMusicLayout.NaturalWidth(1, false, 3, false, 0, 0, ring: QuotaRing.Pair) - CompactMusicLayout.NaturalWidth(1, false, 3, false, 0, 0) == 24);
    }
}

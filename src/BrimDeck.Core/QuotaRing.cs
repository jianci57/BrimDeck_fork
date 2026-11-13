namespace BrimDeck.Core;

// One application's collapsed quota ring. Shares: what each ring fills, outside in, 0 to 100 (one share for one ring); a null
// share is a quota chosen for the ring that has no share now (the source does not report it, or a balance or an amount spent has
// no target yet), drawn as the bare track; NaN is a place left out on purpose (AppEntry.RingQuotas), drawn as nothing, so the
// rings inside it keep their places. Detail is what the tooltip says after the application's name.
public sealed record QuotaRing(IReadOnlyList<double?> Shares, string Detail)
{
    // The nested style shows at most this many rings, one inside the other.
    public const int MaximumRings = 3;
    // Diameters at scale 1 (QuotaRingStyle). Nested rings are 20 DIP across whether one, two or three show, so every ring in the
    // row has one size and the row does not change width with the data; a single ring keeps the 12 DIP it had before.
    public const double Nested = 20, Single = 12;
    public static double Diameter(QuotaRingStyle style) => style == QuotaRingStyle.Single ? Single : Nested;

    // The default nested rings, drawn as used shares.
    public static QuotaRing For(ProviderSnapshot snapshot) => For(snapshot, null, QuotaRingStyle.Nested, false);

    // entry: the application's own choice of quotas (AppEntry.SingleRingQuota, AppEntry.RingQuotas) and targets (AppEntry.QuotaTargets),
    // null for the defaults. remaining: each ring fills with what is left (100 less the used share) instead of what is used.
    //
    // A ring's share (Used): the share the source gives, or for a balance or an amount spent with a target of its own, the part of
    // the target used (a balance of 20 against 100 is 80% used; 30 spent against 100 is 30% used). The account balance takes the
    // row's target balance (AppEntry.BalanceTarget), which the snapshot already carries as its total (DashboardUsage.Quota).
    //
    // Single: the chosen quota while the source reports it; else the account balance where the source has one, else the highest
    // used share, as the one ring was drawn before there were nested rings.
    // Nested, chosen quotas: one ring per chosen quota, outside in; a chosen quota the source does not report now keeps its ring,
    // empty, so the rings do not move. While the source reports none of them (another source, a quota renamed), the default rings
    // show instead.
    // Nested, by default: the first three quotas in their default order (Ordered), one per ring, outside in: for GLM five hours,
    // the week and the tool calls; for Claude five hours, the week and the first model's week (Fable); for Kimi and OpenCode five
    // hours, the week and the month; for New API the account balance, the key's balance and today's spending. Each ring keeps its
    // own quota, whichever is used more. A quota the source does not report lets the ones after it move out (GLM without a weekly
    // quota: five hours, then the tool calls).
    //
    // While no ring has a share, one ring shows: full while the source reports something (a balance with no target) and empty
    // while it reports nothing.
    public static QuotaRing For(ProviderSnapshot snapshot, AppEntry? entry, QuotaRingStyle style, bool remaining)
    {
        var choices = Choices(snapshot);
        double? Shown(UsageMetric metric) => Used(metric, entry) is { } used ? remaining ? 100 - used : used : null;
        string Share(double shown) => remaining ? Loc.T($"剩余 {shown:0.#}%", $"{shown:0.#}% left") : Loc.T($"已用 {shown:0.#}%", $"{shown:0.#}% used");
        string Text(UsageMetric metric) => metric.ValueText.Length > 0 && metric.ValueText != "—" ? metric.ValueText + " " + metric.ValueSuffix : Loc.Label(metric.Label);
        // The tooltip: every quota with a share, and every balance or amount spent, with its figure; a lone share needs no name.
        var listed = snapshot.Metrics.Where(metric => Shown(metric) is not null || metric.Kind is MetricKind.Balance or MetricKind.Spend && metric.Amount is not null).ToArray();
        string detail = snapshot.Metrics.Count == 0 ? snapshot.StatusLabel
            : listed.Length == 0 ? string.Join(Loc.T("；", "; "), snapshot.Metrics.Select(Text))
            : listed.Length == 1 && Shown(listed[0]) is { } lone ? Share(lone)
            : string.Join(Loc.T("；", "; "), listed.Select(metric => Shown(metric) is { } shown ? Loc.Label(metric.Label) + " " + Share(shown) : Text(metric)));
        QuotaRing Rings(IEnumerable<UsageMetric?> metrics)
        {
            var shares = metrics.Take(MaximumRings).Select(metric => metric is null ? null : Shown(metric)).ToList();
            return shares.Any(share => share is { } value && !double.IsNaN(value)) ? new(shares, detail) : new([snapshot.Metrics.Count > 0 ? 100 : 0], detail);
        }
        UsageMetric? Find(string id) => choices.FirstOrDefault(metric => metric.Id == id);

        if (style == QuotaRingStyle.Single)
        {
            if (entry?.SingleRingQuota is { Length: > 0 } single && Find(single) is { } chosen) return Rings([chosen]);
            if (choices.FirstOrDefault(IsAccountBalance) is { } account) return Rings([account]);
            return Rings([choices.Where(metric => Used(metric, entry) is not null).MaxBy(metric => Used(metric, entry))]);
        }

        if (entry?.RingQuotas is { Count: > 0 } picked && picked.Any(id => Find(id) is not null))
        {
            // A place left out draws nothing, while a chosen quota without a share keeps its track (Shares).
            var shares = picked.Take(MaximumRings).Select(id => id.Length == 0 ? double.NaN : Find(id) is { } metric ? Shown(metric) : null).ToList();
            return new(shares, detail);
        }
        return Rings(choices);
    }

    // The nested rings' choice by place (AppEntry.RingQuotas): at most three, an empty string for a place left out, a quota that
    // shows twice left out the second time, places left out at the end dropped; nothing chosen at all is the default rings ([]).
    public static List<string> Slots(IEnumerable<string>? quotas)
    {
        var slots = new List<string>();
        foreach (var quota in (quotas ?? []).Take(MaximumRings))
        {
            var id = quota?.Trim() ?? "";
            slots.Add(id.Length > 500 || slots.Contains(id) ? "" : id);
        }
        while (slots.Count > 0 && slots[^1].Length == 0) slots.RemoveAt(slots.Count - 1);
        return slots;
    }

    // A quota's used share for the rings, 0 to 100, or null without one: a balance or an amount spent with a target of its own
    // (AppEntry.QuotaTargets) is measured against it; anything else has the share its source gives.
    public static double? Used(UsageMetric metric, AppEntry? entry)
    {
        if (TakesTarget(metric) && metric.Amount is { } amount && entry?.QuotaTargets is { } targets && targets.TryGetValue(metric.Id, out var target) && target > 0)
            return Math.Clamp((double)((metric.Kind == MetricKind.Balance ? target - amount : amount) / target * 100), 0, 100);
        return metric.UsedPercent is { } used && double.IsFinite(used) ? Math.Clamp(used, 0, 100) : null;
    }

    // A balance or an amount spent can be given a target of its own for the rings (AppEntry.QuotaTargets). The account balance is
    // not: its target is the row's target balance (AppEntry.BalanceTarget), which the panel and the notices use too.
    public static bool TakesTarget(UsageMetric metric) => metric.Kind is MetricKind.Balance or MetricKind.Spend && metric.Amount is not null && !IsAccountBalance(metric);

    // The account balance, with or without the row's target balance as its total (DashboardUsage.Quota).
    public static bool IsAccountBalance(UsageMetric metric) => metric is { Kind: MetricKind.Balance, Label: "账户余额" } && (metric.Total is null || metric.TotalIsTarget);

    // The quotas the nested rings show by default, outside in (UsageMetric.Id), for the settings to start from.
    public static IReadOnlyList<string> Automatic(ProviderSnapshot snapshot) => [.. Choices(snapshot).Take(MaximumRings).Select(metric => metric.Id)];

    // The quotas an application's rings can be given (AppEntry.SingleRingQuota, AppEntry.RingQuotas), in their default order
    // (Ordered): every quota with a share, and every balance and amount spent, which a target gives a share.
    public static IReadOnlyList<UsageMetric> Choices(ProviderSnapshot snapshot) => Ordered(snapshot.Metrics.Where(metric =>
        metric.UsedPercent is { } used && double.IsFinite(used) || metric.Kind is MetricKind.Balance or MetricKind.Spend && metric.Amount is not null));

    // The default order of a source's quotas: the account balance first; then a source that groups its quotas ("Gemini · 5 小时",
    // "Claude · 每周", or one Claude organization's before another's) keeps its groups in its own order, and within a group, as for a
    // source without groups, the shortest window comes first (five hours, a day, a week, a month) and quotas without a window (tool
    // calls, a key's balance, an amount spent) come last. Quotas that tie keep the source's order, so Claude's week comes before its
    // models' weeks and New API's key balance before today's spending. Antigravity: Gemini's five hours and week, then Claude's five hours.
    private static IReadOnlyList<UsageMetric> Ordered(IEnumerable<UsageMetric> metrics)
    {
        var list = metrics.Where(metric => metric.Id.Length > 0).DistinctBy(metric => metric.Id).ToList();
        static string Group(UsageMetric metric) => metric.Label.Contains('·') ? metric.Label.Split('·')[0].Trim() : "";
        var groups = list.Select(Group).Distinct().ToList();
        // OrderBy is stable, so quotas that tie keep the source's order.
        return [.. list.OrderBy(metric => IsAccountBalance(metric) ? 0 : 1).ThenBy(metric => groups.IndexOf(Group(metric)))
            .ThenBy(metric => metric.Window is > 0 ? metric.Window.Value : int.MaxValue)];
    }
}

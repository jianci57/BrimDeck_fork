namespace BrimDeck.Core;

// Decides which connected monitor each panel window sits on. The main window always has a monitor: the primary one if
// it shows a panel, otherwise the first chosen monitor in the order given. Every other chosen monitor gets a window of its own.
// Each panel shows a profile (Profiles): the one chosen for its monitor, or the current one (null).
public static class DisplayLayout
{
    // Hidden: a rule hides every panel; the main window keeps its monitor but shows nothing.
    public sealed record Plan(string Main, IReadOnlyList<string> Others)
    {
        public IReadOnlyDictionary<string, Guid?> Profiles { get; init; } = new Dictionary<string, Guid?>();
        public bool Hidden { get; init; }
        public Guid? ProfileOf(string monitor) => Profiles.GetValueOrDefault(monitor);
    }

    public static Plan Arrange(DisplayPlacement placement, IReadOnlyCollection<string> chosen, IReadOnlyList<(string Id, bool Primary)> monitors)
        => Arrange(placement, chosen.Select(id => new ChosenDisplay(id, "")).ToList(), null, monitors);

    // rule: the profile rule that holds, if any; its displays are hidden or shown with their profile over the chosen ones.
    public static Plan Arrange(DisplayPlacement placement, IReadOnlyList<ChosenDisplay> chosen, ProfileRule? rule, IReadOnlyList<(string Id, bool Primary)> monitors)
    {
        if (monitors.Count == 0) return new Plan("", []);
        string primary = monitors.FirstOrDefault(m => m.Primary).Id ?? monitors[0].Id;
        var shown = new Dictionary<string, Guid?>();
        if (placement == DisplayPlacement.Chosen)
            foreach (var choice in chosen)
                if (monitors.Any(m => m.Id == choice.Id)) shown.TryAdd(choice.Id, choice.Profile);
        if (shown.Count == 0) shown[primary] = null;
        foreach (var display in rule?.Displays ?? [])
        {
            if (!monitors.Any(m => m.Id == display.Id)) continue;
            if (display.Hidden) shown.Remove(display.Id); else shown[display.Id] = display.Profile;
        }
        if (shown.Count == 0) return new Plan(primary, []) { Hidden = true };
        // In display order, so that the panels do not depend on the order the displays were chosen in.
        var order = monitors.Select(m => m.Id).Where(shown.ContainsKey).ToList();
        string main = order.Contains(primary) ? primary : order[0];
        return new Plan(main, order.Where(id => id != main).ToList()) { Profiles = shown };
    }
}

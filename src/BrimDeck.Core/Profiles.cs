using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrimDeck.Core;

// A named set of the panel's own settings: its looks, pages and what the collapsed row shows. What belongs to the whole app
// (startup, language, hotkeys, the applications and their keys, the players' full control, the clipboard's recording, the
// displays and the profile rules) is shared by every profile (Profiles.GlobalKeys). Every profile keeps its settings in Values,
// written as settings.json writes them. The current profile's settings are also the ordinary ones in settings.json; every change
// to them is the profile's at once (Profiles.Keep).
public sealed class DeckProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    // The saved settings.
    public JsonObject? Values { get; set; }
    public DeckProfile Copy() => new() { Id = Id, Name = Name, Values = (JsonObject?)Values?.DeepClone() };
}

// When a profile rule holds: while a process of the app runs, or while one of its windows is in front.
public enum ProfileTrigger { Running, Foreground }

// A display's panel while a rule holds: hidden, or shown with a profile (null: the current one).
public sealed class ProfileRuleDisplay
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Hidden { get; set; }
    public Guid? Profile { get; set; }
    public ProfileRuleDisplay Copy() => new() { Id = Id, Name = Name, Hidden = Hidden, Profile = Profile };
}

// Switches profiles while an app runs or is in front. The first rule in the list that holds applies: it may make another profile
// current (Profile) and hide or change the panels of some displays (Displays). When it stops holding, the profile it replaced
// comes back, unless the user chose a profile meanwhile.
public sealed class ProfileRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    // The app, as FullscreenRule names it: the process name, and the executable when it is known.
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public ProfileTrigger Trigger { get; set; }
    public Guid? Profile { get; set; }
    public List<ProfileRuleDisplay> Displays { get; set; } = [];
    public ProfileRule Copy() => new() { Id = Id, Enabled = Enabled, Name = Name, Path = Path, Trigger = Trigger, Profile = Profile, Displays = [.. Displays.Select(d => d.Copy())] };
    public bool Matches(string? processName, string? imagePath) => ScreenPolicy.Matches(new FullscreenRule(Name, Path), processName, imagePath);
}

public static class Profiles
{
    public const int Maximum = 20;
    public const int MaximumRules = 32;
    public const int MaximumName = 40;

    // The members every profile shares. Everything else in DeckSettings belongs to a profile, so a setting added later is a
    // profile's unless it is listed here.
    public static readonly IReadOnlySet<string> GlobalKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(DeckSettings.AppVersion), nameof(DeckSettings.LaunchAtStartup), nameof(DeckSettings.ShowTrayIcon), nameof(DeckSettings.Theme),
        nameof(DeckSettings.ThemeChoices), nameof(DeckSettings.Language), nameof(DeckSettings.UiFont), nameof(DeckSettings.SettingsFollowsFont),
        nameof(DeckSettings.HideHotkey), nameof(DeckSettings.ExpandHotkey), nameof(DeckSettings.ResizeHotkey), nameof(DeckSettings.MoveHotkey),
        nameof(DeckSettings.ClickThroughHotkey), nameof(DeckSettings.SettingsHotkey), nameof(DeckSettings.Page1Hotkey), nameof(DeckSettings.Page2Hotkey),
        nameof(DeckSettings.Page3Hotkey), nameof(DeckSettings.Page4Hotkey), nameof(DeckSettings.Page5Hotkey), nameof(DeckSettings.Page6Hotkey),
        nameof(DeckSettings.Page7Hotkey), nameof(DeckSettings.Page8Hotkey), nameof(DeckSettings.Page9Hotkey), nameof(DeckSettings.Page10Hotkey),
        nameof(DeckSettings.RefreshHotkey), nameof(DeckSettings.CycleStyleHotkey), nameof(DeckSettings.LineStyleHotkey), nameof(DeckSettings.NextProfileHotkey),
        nameof(DeckSettings.ClickThrough), nameof(DeckSettings.ClickThroughPanel), nameof(DeckSettings.ClickThroughKeyboard),
        nameof(DeckSettings.NeteaseFullControlEntry), nameof(DeckSettings.NeteaseAutoFullControl), nameof(DeckSettings.NeteaseFullControl),
        nameof(DeckSettings.KugouFullControl), nameof(DeckSettings.AppleMusicFullControl), nameof(DeckSettings.IgnoredMediaApps),
        nameof(DeckSettings.Display), nameof(DeckSettings.DisplayMonitors),
        nameof(DeckSettings.ClipboardRecord), nameof(DeckSettings.ClipboardExcluded), nameof(DeckSettings.ClipboardKeepHistory),
        nameof(DeckSettings.ClipboardKeepFor), nameof(DeckSettings.TransferDragOut),
        nameof(DeckSettings.MonitorInterval), nameof(DeckSettings.MonitorTrafficPeriod), nameof(DeckSettings.MonitorGpu), nameof(DeckSettings.MonitorGpuName), nameof(DeckSettings.MonitorGpuUsage),
        nameof(DeckSettings.DeviceChoices), nameof(DeckSettings.MemoryMode), nameof(DeckSettings.TrimMemoryWhenIdle), nameof(DeckSettings.UsageAutoSync),
        nameof(DeckSettings.SettingsWindowWidth), nameof(DeckSettings.SettingsWindowHeight), nameof(DeckSettings.SettingsWindowMaximized),
        nameof(DeckSettings.IncludeWsl), nameof(DeckSettings.Apps),
        nameof(DeckSettings.Profiles), nameof(DeckSettings.CurrentProfile), nameof(DeckSettings.ReturnProfile), nameof(DeckSettings.ProfileRules),
        nameof(DeckSettings.NotificationsEnabled),
        nameof(DeckSettings.FullscreenExceptions), nameof(DeckSettings.MouseWheelPages)
    };

    public static bool IsProfileKey(string key) => !GlobalKeys.Contains(key);

    // A profile's part of these settings, as settings.json writes it.
    public static JsonObject Values(DeckSettings settings)
    {
        var all = JsonSerializer.SerializeToNode(settings)!.AsObject();
        return new JsonObject(all.Where(pair => IsProfileKey(pair.Key)).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
    }

    // A new profile's values: what a profile holds when nothing was chosen.
    private static readonly Lazy<JsonObject> Defaults = new(() => Values(new DeckSettings()));

    // The settings with a profile's part replaced by these values. A member the values lack takes its default, as a member missing
    // from settings.json does: a profile saved by a version before the setting existed starts where a new user would, not with
    // whatever the current profile chose. (A setting renamed or changed in meaning is carried over by SettingsMigrations, which
    // changes every profile's values with the ordinary settings.)
    public static DeckSettings With(DeckSettings settings, JsonObject? values)
    {
        var all = JsonSerializer.SerializeToNode(settings)!.AsObject();
        if (values is not null)
        {
            foreach (var (key, value) in Defaults.Value)
                if (!values.ContainsKey(key)) all[key] = value?.DeepClone();
            foreach (var (key, value) in values)
                if (IsProfileKey(key)) all[key] = value?.DeepClone();
        }
        var merged = all.Deserialize<DeckSettings>() ?? new();
        merged.Normalize();
        return merged;
    }

    public static DeckProfile? Find(DeckSettings settings, Guid? id) => id is { } wanted ? settings.Profiles.FirstOrDefault(p => p.Id == wanted) : null;
    public static DeckProfile Current(DeckSettings settings) => Find(settings, settings.CurrentProfile) ?? settings.Profiles[0];
    public static string Name(DeckSettings settings, Guid? id) => Find(settings, id) is { } profile ? Name(profile) : "";
    public static string Name(DeckProfile profile) => profile.Name;
    // The name a new profile gets: "配置 1", "配置 2" and so on, the first number not taken.
    public static string NextName(DeckSettings settings)
    {
        for (int n = 1; ; n++)
        {
            var name = Loc.T($"配置 {n}", $"Profile {n}");
            if (!settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase))) return name;
        }
    }

    // The settings a panel shows with this profile: the profile's saved ones over the shared ones. The current profile is the settings
    // themselves, with their changes not saved yet. The applications' colors follow the profile's theme, as the theme page keeps them per theme (ThemeChoices).
    public static DeckSettings For(DeckSettings settings, Guid id)
    {
        if (id == settings.CurrentProfile || Find(settings, id) is not { } profile) return settings;
        var shown = With(settings, profile.Values);
        AppColorsFor(shown, settings);
        return shown;
    }

    // The applications' colors for the settings' theme: those kept for it (ThemeChoices), else the ones in use.
    private static void AppColorsFor(DeckSettings shown, DeckSettings current)
    {
        var theme = PanelThemes.NormalizeId(shown.PanelTheme);
        if (theme == PanelThemes.NormalizeId(current.PanelTheme) || !shown.ThemeChoices.TryGetValue(theme, out var kept)) return;
        foreach (var app in shown.Apps)
            if (kept.Apps.TryGetValue(app.InstanceId.ToString("N"), out var colors))
            { app.ThemeColor = colors.Theme; app.WarningColor = colors.Warning; app.CriticalColor = colors.Critical; }
    }

    // The current profile keeps the settings in use: every change to them is the profile's at once.
    public static void Keep(DeckSettings settings) => Current(settings).Values = Values(settings);

    // New settings where another profile is current, with its settings. The profile left keeps the settings it had in use. The
    // applications' colors are kept for the theme left and those kept for the new profile's theme come back (ThemeChoices).
    public static DeckSettings Switch(DeckSettings settings, Guid to)
    {
        if (to == settings.CurrentProfile || Find(settings, to) is not { } target) return settings.Copy();
        var left = settings.Copy(); Keep(left);
        var next = With(left, target.Values);
        var from = PanelThemes.NormalizeId(left.PanelTheme);
        var theme = PanelThemes.NormalizeId(next.PanelTheme);
        if (from != theme)
        {
            next.ThemeChoices[from] = ThemeChoices.From(left);
            if (next.ThemeChoices.TryGetValue(theme, out var kept))
                foreach (var app in next.Apps)
                {
                    var colors = kept.Apps.GetValueOrDefault(app.InstanceId.ToString("N"));
                    app.ThemeColor = colors?.Theme ?? ""; app.WarningColor = colors?.Warning ?? ""; app.CriticalColor = colors?.Critical ?? "";
                }
        }
        next.CurrentProfile = to;
        next.Normalize();
        return next;
    }

    // New settings after a profile that is not current was edited (edited is the profile as For gave it, changed). Its own part goes
    // into the profile; what every profile shares is taken from the edit. The applications' colors chosen under its theme are kept
    // for that theme when it is not the current one's.
    public static DeckSettings Store(DeckSettings settings, Guid id, DeckSettings edited)
    {
        if (id == settings.CurrentProfile) return edited;
        var next = With(edited, Values(settings));
        if (PanelThemes.NormalizeId(edited.PanelTheme) != PanelThemes.NormalizeId(settings.PanelTheme))
        {
            next.ThemeChoices[PanelThemes.NormalizeId(edited.PanelTheme)] = ThemeChoices.From(edited);
            foreach (var app in next.Apps)
                if (settings.Entry(app.InstanceId) is { } own) { app.ThemeColor = own.ThemeColor; app.WarningColor = own.WarningColor; app.CriticalColor = own.CriticalColor; }
        }
        // The profiles are the current settings' own: the edit carries the list as it was read.
        next.Profiles = [.. settings.Profiles.Select(p => p.Copy())];
        next.CurrentProfile = settings.CurrentProfile; next.ReturnProfile = settings.ReturnProfile;
        next.ProfileRules = [.. edited.ProfileRules.Select(r => r.Copy())];
        if (Find(next, id) is { } stored) stored.Values = Values(edited);
        next.Normalize();
        return next;
    }

    // A new profile with the settings of an existing one (the current one's in use), not made current.
    public static DeckProfile Duplicate(DeckSettings settings, Guid from, string name)
    {
        var source = Find(settings, from);
        var values = source is null || source.Id == settings.CurrentProfile ? Values(settings) : (JsonObject?)source.Values?.DeepClone() ?? Values(settings);
        return new DeckProfile { Name = name, Values = values };
    }
    // A new profile with every setting of its own at its default, as a new user starts.
    public static DeckProfile Blank(string name) => new() { Name = name, Values = (JsonObject)Defaults.Value.DeepClone() };

    // A name not yet taken: "专注", then "专注 2", "专注 3" and so on.
    public static string FreeName(DeckSettings settings, string name, Guid? except = null)
    {
        name = Clean(name);
        if (name.Length == 0) name = Loc.T("配置", "Profile");
        bool Taken(string candidate) => settings.Profiles.Any(p => p.Id != except && string.Equals(Name(p), candidate, StringComparison.CurrentCultureIgnoreCase));
        if (!Taken(name)) return name;
        for (int n = 2; ; n++) { var candidate = $"{name} {n}"; if (!Taken(candidate)) return candidate; }
    }

    public static string Clean(string? name)
    {
        var text = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > MaximumName ? text[..MaximumName] : text;
    }

    // The first enabled rule that holds, given whether its app runs and the app in front.
    public static ProfileRule? Holding(IEnumerable<ProfileRule> rules, Func<string, bool> runs, string? foregroundName, string? foregroundPath)
    {
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            bool holds = rule.Trigger == ProfileTrigger.Foreground
                ? (foregroundName is { Length: > 0 } || foregroundPath is { Length: > 0 }) && rule.Matches(foregroundName, foregroundPath)
                : RunName(rule) is { Length: > 0 } name && runs(name + ".exe");
            if (holds) return rule;
        }
        return null;
    }

    // The process name a running rule looks for: the one saved, else the executable's.
    public static string RunName(ProfileRule rule)
    {
        var name = ScreenPolicy.NormalizeName(rule.Name);
        return name.Length > 0 ? name : ScreenPolicy.NormalizeName(System.IO.Path.GetFileName(ScreenPolicy.NormalizePath(rule.Path)));
    }

    // Keeps the profiles and the rules consistent: one current profile with its values in the ordinary settings, every other with
    // values, names present and unique, and every reference to a profile pointing at one that exists.
    public static void Normalize(DeckSettings settings)
    {
        var seen = new HashSet<Guid>();
        var profiles = (settings.Profiles ?? []).OfType<DeckProfile>().Where(p => p.Id != Guid.Empty && seen.Add(p.Id)).Take(Maximum).ToList();
        // The first profile is made from the settings as they were (a file written before profiles).
        if (profiles.Count == 0) profiles.Add(new DeckProfile { Id = settings.CurrentProfile != Guid.Empty ? settings.CurrentProfile : Guid.NewGuid() });
        if (!profiles.Any(p => p.Id == settings.CurrentProfile)) settings.CurrentProfile = profiles[0].Id;
        // Every profile has a name of its own, unique; one without takes "配置 1", "配置 2" and so on.
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var profile in profiles) { profile.Name = Clean(profile.Name); if (profile.Name.Length > 0) names.Add(profile.Name); }
        var named = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var profile in profiles)
        {
            var name = profile.Name;
            if (name.Length == 0)
            {
                for (int n = 1; ; n++) { name = Loc.T($"配置 {n}", $"Profile {n}"); if (!names.Contains(name)) break; }
                names.Add(name);
            }
            var unique = name;
            for (int n = 2; !named.Add(unique); n++) unique = $"{name} {n}";
            profile.Name = unique;
            // The current profile's settings are the ones in use (a file written before profiles); another without any takes the defaults.
            if (profile.Id == settings.CurrentProfile) profile.Values ??= Values(settings);
            else profile.Values ??= [];
        }
        settings.Profiles = profiles;
        if (settings.ReturnProfile is { } back && (back == settings.CurrentProfile || !profiles.Any(p => p.Id == back))) settings.ReturnProfile = null;
        Guid? Known(Guid? id) => id is { } value && profiles.Any(p => p.Id == value) ? value : null;
        var ruleIds = new HashSet<Guid>();
        settings.ProfileRules = (settings.ProfileRules ?? []).OfType<ProfileRule>()
            .Select(rule =>
            {
                rule.Name = ScreenPolicy.NormalizeName(rule.Name); rule.Path = ScreenPolicy.NormalizePath(rule.Path);
                if (rule.Name.Length == 0 && rule.Path.Length > 0) rule.Name = System.IO.Path.GetFileNameWithoutExtension(rule.Path);
                if (!Enum.IsDefined(rule.Trigger)) rule.Trigger = ProfileTrigger.Running;
                if (rule.Id == Guid.Empty || !ruleIds.Add(rule.Id)) { rule.Id = Guid.NewGuid(); ruleIds.Add(rule.Id); }
                rule.Profile = Known(rule.Profile);
                rule.Displays = (rule.Displays ?? []).OfType<ProfileRuleDisplay>().Where(d => !string.IsNullOrWhiteSpace(d.Id))
                    .Select(d => new ProfileRuleDisplay { Id = d.Id.Trim(), Name = (d.Name ?? "").Trim(), Hidden = d.Hidden, Profile = d.Hidden ? null : Known(d.Profile) })
                    .DistinctBy(d => d.Id).ToList();
                return rule;
            })
            .Where(rule => rule.Name.Length > 0).Take(MaximumRules).ToList();
        settings.DisplayMonitors = settings.DisplayMonitors.Select(d => d.Profile is { } id && Known(id) is null ? d with { Profile = null } : d).ToList();
    }
}

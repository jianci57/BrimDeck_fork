using System.Text.Json.Nodes;
using BrimDeck.Core;

internal static class ProfileTests
{
    // The settings with a new profile made from the current one (Profiles.Duplicate), as the profiles page saves one.
    private static DeckSettings Concat(DeckSettings settings, string name, out Guid id)
    {
        var copy = settings.Copy();
        var profile = Profiles.Duplicate(copy, copy.CurrentProfile, name);
        copy.Profiles.Add(profile); copy.Normalize(); id = profile.Id;
        return copy;
    }

    internal static void Run(Action<string, bool> check)
    {
        // Without a settings file, and with one that cannot be read, the settings start with one current profile: the start keeps
        // the settings in it at once (Profiles.Keep), which failed on an empty list and left the app running without a panel.
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck-Profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (name, broken) in new[] { ("no settings file", false), ("an unreadable settings file", true) })
            {
                var firstStore = new SettingsStore(folder);
                if (broken) { Directory.CreateDirectory(folder); File.WriteAllText(firstStore.FilePath, "{ not json"); }
                var first = firstStore.Load();
                bool keptValues;
                try { Profiles.Keep(first); keptValues = true; } catch (ArgumentOutOfRangeException) { keptValues = false; }
                check($"With {name} the settings have one current profile", firstStore.IsFirstStart && first.Profiles.Count == 1 && first.Profiles[0].Id == first.CurrentProfile);
                check($"With {name} the start keeps the settings in the current profile", keptValues && first.Profiles[0].Values is not null);
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

        var settings = new DeckSettings();
        settings.Normalize();
        check("Settings without profiles get one current profile, named as the first, holding them",
            settings.Profiles.Count == 1 && settings.Profiles[0].Id == settings.CurrentProfile && settings.Profiles[0].Values is not null
            && settings.Profiles[0].Name == Loc.T("配置 1", "Profile 1"));
        check("The next profile is named after the last number taken", Profiles.NextName(settings) == Loc.T("配置 2", "Profile 2"));
        var values = Profiles.Values(settings);
        check("A profile holds the panel's own settings", values.ContainsKey(nameof(DeckSettings.Style)) && values.ContainsKey(nameof(DeckSettings.PanelTheme)) && values.ContainsKey(nameof(DeckSettings.UsagePage)));
        check("A profile leaves out what every profile shares",
            !values.ContainsKey(nameof(DeckSettings.Language)) && !values.ContainsKey(nameof(DeckSettings.Apps)) && !values.ContainsKey(nameof(DeckSettings.Profiles))
            && !values.ContainsKey(nameof(DeckSettings.DisplayMonitors)) && !values.ContainsKey(nameof(DeckSettings.HideHotkey)) && !values.ContainsKey(nameof(DeckSettings.KugouFullControl)));
        check("A profile holds the notice content mode but not the notice master switch",
            values.ContainsKey(nameof(DeckSettings.NotificationContentMode)) && !values.ContainsKey(nameof(DeckSettings.NotificationsEnabled)));

        // The notifications' master switch is shared by every profile: a profile's own copy of the switch (as a version that
        // still saved it per profile would leave behind) never overrides the root choice, while the content mode stays its own.
        // These run on their own fixture, so the settings used by the rest of the checks keep one current profile and "专注" second.
        var noticeSettings = settings.Copy();
        noticeSettings.NotificationsEnabled = true;
        var quiet = Profiles.Duplicate(noticeSettings, noticeSettings.CurrentProfile, "安静");
        quiet.Values![nameof(DeckSettings.NotificationsEnabled)] = false;
        quiet.Values[nameof(DeckSettings.NotificationContentMode)] = (int)NotificationContentMode.Full;
        noticeSettings.Profiles.Add(quiet);
        noticeSettings.Normalize();
        var quietShown = Profiles.For(noticeSettings, quiet.Id);
        check("A profile's stored notice switch off does not turn the shared switch off", noticeSettings.NotificationsEnabled && quietShown.NotificationsEnabled);
        check("A profile keeps its own notice content mode", quietShown.NotificationContentMode == NotificationContentMode.Full);
        noticeSettings.NotificationsEnabled = false;
        var loudShown = Profiles.For(noticeSettings, quiet.Id);
        check("Turning the shared notice switch off at the root holds for every profile", !loudShown.NotificationsEnabled && loudShown.NotificationContentMode == NotificationContentMode.Full);
        var switchedQuiet = Profiles.Switch(noticeSettings, quiet.Id);
        check("Switching to the profile keeps the shared switch and the profile's content mode",
            switchedQuiet.NotificationsEnabled == false && Profiles.Find(switchedQuiet, quiet.Id)!.Values![nameof(DeckSettings.NotificationContentMode)]!.GetValue<int>() == (int)NotificationContentMode.Full);
        // Editing a profile that is not current: the shared master switch the edit changes goes to the root, the profile keeps
        // only its content mode, and no copy of the switch is stored in the profile.
        var editedQuiet = Profiles.For(noticeSettings, quiet.Id);
        editedQuiet.NotificationsEnabled = true;
        var storedNotice = Profiles.Store(noticeSettings, quiet.Id, editedQuiet);
        check("Editing another profile writes the shared notice switch back to the root",
            storedNotice.NotificationsEnabled && Profiles.Find(storedNotice, quiet.Id)!.Values![nameof(DeckSettings.NotificationContentMode)]!.GetValue<int>() == (int)NotificationContentMode.Full
            && !Profiles.Find(storedNotice, quiet.Id)!.Values!.ContainsKey(nameof(DeckSettings.NotificationsEnabled)));

        // A second profile, made from the current one and then changed.
        var original = settings.CurrentProfile;
        var focus = Profiles.Duplicate(settings, original, "专注");
        focus.Values![nameof(DeckSettings.Style)] = (int)CompactStyle.Capsule;
        focus.Values[nameof(DeckSettings.PanelTheme)] = PanelThemes.Light;
        focus.Values[nameof(DeckSettings.UsagePage)] = false;
        settings.Profiles.Add(focus);
        settings.Language = Loc.Chinese;
        var claude = settings.Apps[0];
        claude.ThemeColor = "#112233";
        settings.Normalize();
        check("A new profile keeps its values and name", settings.Profiles.Count == 2 && settings.Profiles[1].Values is not null && settings.Profiles[1].Name == "专注");

        var shown = Profiles.For(settings, focus.Id);
        check("A panel with another profile shows that profile's settings", shown.Style == CompactStyle.Capsule && !shown.UsagePage && shown.PanelTheme == PanelThemes.Light);
        check("A panel with another profile shares the rest", shown.Language == Loc.Chinese && shown.Apps.Count == settings.Apps.Count);
        check("The current profile's panel shows the settings themselves", ReferenceEquals(Profiles.For(settings, original), settings));

        var switched = Profiles.Switch(settings, focus.Id);
        check("Switching makes the profile's settings the ordinary ones", switched.CurrentProfile == focus.Id && switched.Style == CompactStyle.Capsule && !switched.UsagePage);
        check("Switching keeps what every profile shares", switched.Language == Loc.Chinese && switched.Apps.Count == settings.Apps.Count);
        check("The profile left keeps its settings",
            Profiles.Find(switched, original)!.Values![nameof(DeckSettings.Style)]!.GetValue<int>() == (int)CompactStyle.Notch);
        check("The applications' colors are kept for the theme left", switched.ThemeChoices[PanelThemes.Dark].Apps[claude.InstanceId.ToString("N")].Theme == "#112233");
        check("The settings switched from are left as they were", settings.CurrentProfile == original && settings.Style == CompactStyle.Notch);
        var back = Profiles.Switch(switched, original);
        check("Switching back restores the first profile", back.CurrentProfile == original && back.Style == CompactStyle.Notch && back.UsagePage && back.PanelTheme == PanelThemes.Dark);
        check("Switching back restores the applications' colors of its theme", back.Entry(claude.InstanceId)!.ThemeColor == "#112233");

        // Colors chosen under the light theme follow a panel that shows it.
        switched.Entry(claude.InstanceId)!.ThemeColor = "#445566";
        back = Profiles.Switch(switched, original);
        check("A theme's application colors are kept when its profile is left", back.ThemeChoices[PanelThemes.Light].Apps[claude.InstanceId.ToString("N")].Theme == "#445566");
        check("A panel with a profile of another theme shows that theme's application colors", Profiles.For(back, focus.Id).Entry(claude.InstanceId)!.ThemeColor == "#445566");

        // Editing a profile that is not current.
        var edited = Profiles.For(back, focus.Id);
        edited.ClockSeconds = true; edited.Language = Loc.English; edited.Entry(claude.InstanceId)!.ThemeColor = "#778899";
        var stored = Profiles.Store(back, focus.Id, edited);
        check("Editing another profile stores its settings in the profile", Profiles.For(stored, focus.Id).ClockSeconds && !stored.ClockSeconds);
        check("Editing another profile changes what every profile shares", stored.Language == Loc.English && stored.CurrentProfile == original);
        check("Editing another profile keeps the current colors and the edited theme's own",
            stored.Entry(claude.InstanceId)!.ThemeColor == "#112233" && stored.ThemeChoices[PanelThemes.Light].Apps[claude.InstanceId.ToString("N")].Theme == "#778899");

        // The profile in use keeps every change: one made just before a switch comes back with it.
        var working = Profiles.Switch(back, original);
        working.ClockSeconds = !working.ClockSeconds;
        var returned = Profiles.Switch(Profiles.Switch(working, focus.Id), original);
        check("A change to the profile in use is kept when switching away and back", returned.ClockSeconds == working.ClockSeconds);
        var kept = working.Copy(); Profiles.Keep(kept);
        check("The profile in use holds the settings in use", Profiles.Find(kept, original)!.Values![nameof(DeckSettings.ClockSeconds)]!.GetValue<bool>() == working.ClockSeconds);
        check("A new profile from the one in use takes its settings", Profiles.For(Profiles.Switch(Concat(working, "副本", out var copyId), copyId), copyId).ClockSeconds == working.ClockSeconds);
        var blank = working.Copy(); var fresh = Profiles.Blank("空白"); blank.Profiles.Add(fresh); blank.Normalize();
        var blankShown = Profiles.Switch(blank, fresh.Id);
        check("A profile with default settings starts as a new user would and keeps what every profile shares",
            blankShown.ClockSeconds == new DeckSettings().ClockSeconds && blankShown.Style == new DeckSettings().Style && blankShown.Language == working.Language && blankShown.Apps.Count == working.Apps.Count);

        // Names and references.
        var messy = new DeckSettings
        {
            Profiles = [new DeckProfile { Name = "  专注  " }, new DeckProfile { Name = "专注" }, new DeckProfile { Name = new string('x', 80) }],
            ProfileRules = [new ProfileRule { Name = "focus.exe", Profile = Guid.NewGuid(), Displays = [new ProfileRuleDisplay { Id = "a", Profile = Guid.NewGuid() }] }, new ProfileRule { Name = " " }],
            Display = DisplayPlacement.Chosen, DisplayMonitors = [new ChosenDisplay("a", "A") { Profile = Guid.NewGuid() }]
        };
        messy.CurrentProfile = messy.Profiles[1].Id; messy.ReturnProfile = messy.Profiles[1].Id;
        messy.Normalize();
        check("Profile names are trimmed, unique and short", messy.Profiles[0].Name == "专注" && messy.Profiles[1].Name == "专注 2" && messy.Profiles[2].Name.Length == Profiles.MaximumName);
        check("The current profile's settings start as the ones in use", messy.Profiles[1].Values is not null && messy.Profiles[0].Values is not null);
        check("A return to the current profile is dropped", messy.ReturnProfile is null);
        check("A rule without an app is dropped and its name loses .exe", messy.ProfileRules.Count == 1 && messy.ProfileRules[0].Name == "focus");
        check("References to missing profiles are dropped", messy.ProfileRules[0].Profile is null && messy.ProfileRules[0].Displays[0].Profile is null && messy.DisplayMonitors[0].Profile is null);
        var unnamed = new DeckSettings { Profiles = [new DeckProfile { Name = Loc.T("配置 1", "Profile 1") }, new DeckProfile(), new DeckProfile()] };
        unnamed.Normalize();
        check("Profiles without a name take the numbers not taken", unnamed.Profiles.Select(p => p.Name).SequenceEqual([Loc.T("配置 1", "Profile 1"), Loc.T("配置 2", "Profile 2"), Loc.T("配置 3", "Profile 3")]));

        // Rules.
        var running = new ProfileRule { Name = "focus", Trigger = ProfileTrigger.Running };
        var front = new ProfileRule { Name = "editor", Path = @"C:\Tools\editor.exe", Trigger = ProfileTrigger.Foreground };
        var off = new ProfileRule { Name = "other", Enabled = false };
        bool Runs(string file) => file.Equals("focus.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("other.exe", StringComparison.OrdinalIgnoreCase);
        check("A running rule holds while its process runs", Profiles.Holding([off, running, front], Runs, null, null) == running);
        check("The first rule that holds applies", Profiles.Holding([front, running], Runs, "editor", @"C:\Tools\editor.exe") == front);
        check("A foreground rule matches by path", Profiles.Holding([front], _ => false, "renamed", @"c:/tools/EDITOR.exe") == front);
        check("A disabled rule never holds", Profiles.Holding([off], Runs, "other", null) is null);
        check("A foreground rule does not hold for a process only running", Profiles.Holding([front], file => file == "editor.exe", null, null) is null);
        check("A running rule saved with a path only looks for its executable", Profiles.RunName(new ProfileRule { Path = @"D:\Games\Hero.exe" }) == "Hero");

        // Displays with profiles and rules.
        (string, bool)[] three = [("left", false), ("main", true), ("right", false)];
        var focusId = Guid.NewGuid();
        var chosen = new List<ChosenDisplay> { new("main", "M"), new("right", "R") { Profile = focusId } };
        var plan = DisplayLayout.Arrange(DisplayPlacement.Chosen, chosen, null, three);
        check("Each chosen display keeps its profile", plan.Main == "main" && plan.Others.SequenceEqual(["right"]) && plan.ProfileOf("main") is null && plan.ProfileOf("right") == focusId);
        var hideMain = new ProfileRule { Name = "focus", Displays = [new ProfileRuleDisplay { Id = "main", Hidden = true }, new ProfileRuleDisplay { Id = "left", Profile = focusId }] };
        plan = DisplayLayout.Arrange(DisplayPlacement.Chosen, chosen, hideMain, three);
        check("A rule hides a display and shows another with a profile", plan.Main == "left" && plan.Others.SequenceEqual(["right"]) && plan.ProfileOf("left") == focusId && !plan.Hidden);
        plan = DisplayLayout.Arrange(DisplayPlacement.Primary, [], new ProfileRule { Name = "focus", Displays = [new ProfileRuleDisplay { Id = "right", Profile = focusId }] }, three);
        check("With the primary display only, a rule still shows a panel on another display with its profile",
            plan.Main == "main" && plan.Others.SequenceEqual(["right"]) && plan.ProfileOf("right") == focusId && plan.ProfileOf("main") is null);
        var hideAll = new ProfileRule { Name = "focus", Displays = [new ProfileRuleDisplay { Id = "main", Hidden = true }] };
        plan = DisplayLayout.Arrange(DisplayPlacement.Primary, [], hideAll, three);
        check("A rule that hides every panel keeps the main window on the primary display, hidden", plan.Main == "main" && plan.Others.Count == 0 && plan.Hidden);
        plan = DisplayLayout.Arrange(DisplayPlacement.Primary, [], new ProfileRule { Name = "x", Displays = [new ProfileRuleDisplay { Id = "gone", Hidden = true }] }, three);
        check("A rule's display that is not connected changes nothing", plan.Main == "main" && !plan.Hidden);

        // Saving and loading.
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "BrimDeck-Profiles-" + Guid.NewGuid().ToString("N")), "1.3.1");
        try
        {
            var saved = Profiles.Switch(back, focus.Id);
            saved.ReturnProfile = original;
            saved.ProfileRules = [new ProfileRule { Name = "focus", Profile = original, Displays = [new ProfileRuleDisplay { Id = "x", Hidden = true }] }];
            saved.Display = DisplayPlacement.Chosen; saved.DisplayMonitors = [new ChosenDisplay("x", "X") { Profile = original }];
            store.Save(saved);
            var loaded = store.Load();
            check("Profiles survive saving and loading", loaded.Profiles.Count == 2 && loaded.CurrentProfile == focus.Id && loaded.ReturnProfile == original
                && Profiles.For(loaded, original).Style == CompactStyle.Notch && loaded.Style == CompactStyle.Capsule);
            check("Profile rules and display profiles survive saving and loading",
                loaded.ProfileRules.Count == 1 && loaded.ProfileRules[0].Profile == original && loaded.ProfileRules[0].Displays[0].Hidden && loaded.DisplayMonitors[0].Profile == original);
            var copy = loaded.Copy(); copy.Profiles[0].Values?.Clear(); copy.ProfileRules.Clear();
            check("A settings copy has its own profiles and rules", Profiles.Find(loaded, original)!.Values!.Count > 0 && loaded.ProfileRules.Count == 1);
        }
        finally { Directory.Delete(store.DirectoryPath, true); }

        // A settings file written before profiles existed becomes the default profile.
        var old = SettingsMigrations.Read("""{"AppVersion":"1.3.0","Style":1,"Language":"zh-CN"}""", new Version(1, 3, 1), out _);
        check("Settings saved before profiles become the default profile", old.Profiles.Count == 1 && old.CurrentProfile == old.Profiles[0].Id && old.Style == CompactStyle.Capsule);

        // A file from before profiles with chosen displays: the same displays show a panel, each with the current profile.
        var chosenBefore = SettingsMigrations.Read("""
            {"AppVersion":"1.3.0","Style":1,"Display":1,"DisplayMonitors":[{"Id":"left","Name":"L"},{"Id":"main","Name":"M"}],"Maximized":2,"Language":"zh-CN"}
            """, new Version(1, 3, 1), out _);
        var chosenPlan = DisplayLayout.Arrange(chosenBefore.Display, chosenBefore.DisplayMonitors, null, [("left", false), ("main", true), ("right", false)]);
        check("Displays chosen before profiles keep showing the panel, with the current profile",
            chosenPlan.Main == "main" && chosenPlan.Others.SequenceEqual(["left"]) && chosenPlan.ProfileOf("left") is null && chosenPlan.ProfileOf("main") is null
            && chosenBefore.Maximized == WindowBehavior.Hide);

        // Upgrades: a profile saved before a setting existed takes the setting's default, not the current profile's choice.
        var partial = new DeckSettings { ClockSeconds = true };
        partial.Profiles = [new DeckProfile { Id = partial.CurrentProfile = Guid.NewGuid() }, new DeckProfile { Name = "旧", Values = new JsonObject { [nameof(DeckSettings.Style)] = (int)CompactStyle.Line } }];
        partial.Normalize();
        var older = Profiles.For(partial, partial.Profiles[1].Id);
        check("A setting a profile lacks takes its default", !older.ClockSeconds && older.Style == CompactStyle.Line);
        check("Switching to a profile that lacks a setting takes its default", !Profiles.Switch(partial, partial.Profiles[1].Id).ClockSeconds);
        // Every profile goes through the same upgrade steps as the ordinary settings (here the 1.2.3 step that gave the equalizer
        // its own switch, which starts as the music's was).
        var upgraded = SettingsMigrations.Read("""
            {"AppVersion":"1.2.2","NotchMusic":true,"CurrentProfile":"11111111-1111-1111-1111-111111111111",
             "Profiles":[{"Id":"11111111-1111-1111-1111-111111111111","Name":""},{"Id":"22222222-2222-2222-2222-222222222222","Name":"旧","Values":{"NotchMusic":false,"MusicCoverColor":false}}]}
            """, new Version(1, 3, 1), out _);
        var oldProfile = Profiles.For(upgraded, Guid.Parse("22222222-2222-2222-2222-222222222222"));
        check("Upgrade steps change every profile's settings", upgraded.NotchEqualizer && !oldProfile.NotchEqualizer && oldProfile.EqualizerColor == ClockColor.White
            && !Profiles.Find(upgraded, Guid.Parse("22222222-2222-2222-2222-222222222222"))!.Values!.ContainsKey("MusicCoverColor"));
        // A version without profiles reads the same file: the members it does not know are left alone, and its own settings are
        // those of the profile that was current.
        var plain = System.Text.Json.JsonSerializer.Deserialize<PlainSettings>(System.Text.Json.JsonSerializer.Serialize(Profiles.Switch(back, focus.Id)))!;
        check("A version without profiles reads the current profile's settings", plain.Style == (int)CompactStyle.Capsule && plain.Language == back.Language);
    }
}

// The few members an older version reads, to show it reads a file with profiles as before.
internal sealed class PlainSettings
{
    public int Style { get; set; }
    public string Language { get; set; } = "";
}

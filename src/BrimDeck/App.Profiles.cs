using System.Runtime.InteropServices;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Profiles (DeckProfile): the current one's settings are Settings (kept in the profile with every change); a panel whose display shows another profile reads that
// profile's settings (SettingsFor), kept here until the settings change. The settings window changes the profile in use.
// Profile rules (ProfileRule) switch the current profile and hide or change displays while an app runs or is in front.
public partial class App
{
    private readonly Dictionary<Guid, DeckSettings> _profileSettings = [];
    // The rule that holds now, if any; it is found again in the settings after every change, so an edit to it applies at once.
    internal ProfileRule? HoldingRule { get; private set; }
    // A holding rule hides every display; the main panel stays on its display but shows nothing (MainWindow.HiddenByUser).
    internal bool RuleHidesAll { get; private set; }
    private DispatcherTimer? _ruleTimer;
    private bool _ruleCheckQueued;

    // The settings a panel showing this profile reads: the current settings for the current profile (or a profile that no longer exists).
    internal DeckSettings SettingsFor(Guid? profile)
    {
        if (profile is not { } id || id == Settings.CurrentProfile || Profiles.Find(Settings, id) is null) return Settings;
        if (!_profileSettings.TryGetValue(id, out var shown)) _profileSettings[id] = shown = Profiles.For(Settings, id);
        return shown;
    }
    // Changes to a profile's settings made from a panel showing it.
    internal void UpdateSettingsFor(Guid? profile, DeckSettings edited)
    {
        if (profile is not { } id || id == Settings.CurrentProfile || Profiles.Find(Settings, id) is null) UpdateSettings(edited);
        else UpdateSettings(Profiles.Store(Settings, id, edited));
    }
    // A panel changed its settings in place (the page it shows, the monitor page's sorting): a panel of another profile stores them
    // in that profile. Nothing else changes, so no panel is rebuilt.
    internal void PanelSettingsEdited(MainWindow deck)
    {
        if (deck.Profile is { } id && id != Settings.CurrentProfile && _profileSettings.TryGetValue(id, out var own))
        {
            Settings = Profiles.Store(Settings, id, own);
            _profileSettings[id] = own;
        }
        QueueSave();
    }

    // A page chosen on one panel is the page of every panel that shows the same settings: they remember one page between them
    // (LastPage), and the panel of another display otherwise kept the page it had until it was restarted. A panel that shows
    // another profile keeps its own page, as it keeps its own settings.
    internal void PageChosen(MainWindow from, DeckPage page)
    {
        foreach (var deck in Decks.ToList())
            if (deck != from && ReferenceEquals(deck.PanelSettings, from.PanelSettings)) deck.FollowPage(page);
    }

    // The panel that shows the profile in use, for the settings window's previews (the settings window changes the profile in use):
    // the main panel when none does.
    internal MainWindow PreviewDeck => Decks.FirstOrDefault(deck => (deck.Profile ?? Settings.CurrentProfile) == Settings.CurrentProfile) ?? Deck;

    // The settings of every panel on screen; each profile once.
    internal IEnumerable<DeckSettings> ShownSettings => Decks.Select(deck => deck.PanelSettings).Distinct();
    internal bool QuotaWantedAnywhere => ShownSettings.Any(settings => settings.QuotaWanted);
    // The settings the usage is read with: the applications are shared, and they are read when any panel shows quotas.
    internal DeckSettings DataSettings()
    {
        var settings = Settings.Copy();
        if (!settings.QuotaWanted && QuotaWantedAnywhere) settings.UsagePage = true;
        return settings;
    }
    private string DataKey()
    {
        var settings = DataSettings();
        return string.Join("|", settings.IncludeWsl, string.Join(",", settings.RequiredProviders.Order()), string.Join(",", settings.EnabledApps.Select(e => e.ConfigurationKey).Order()));
    }

    // Makes another profile current, by the user's choice (the profiles page, the tray, the hotkey). A rule that holds no longer
    // brings the profile it replaced back.
    internal void ChooseProfile(Guid id)
    {
        if (Profiles.Find(Settings, id) is null) return;
        if (id == Settings.CurrentProfile) { if (Settings.ReturnProfile is not null) KeepProfile(); return; }
        var settings = Profiles.Switch(Settings, id);
        settings.ReturnProfile = null;
        UpdateSettings(settings);
    }
    // Keeps the profile a rule made current once the rule stops holding.
    internal void KeepProfile()
    {
        var settings = Settings.Copy(); settings.ReturnProfile = null;
        UpdateSettings(settings);
    }
    // A new profile, made current: a copy of the current one, or (blank) with every setting of its own at its default.
    internal void CreateProfile(Guid id, bool blank)
    {
        if (Settings.Profiles.Count >= Profiles.Maximum) return;
        var settings = Settings.Copy();
        var name = Profiles.NextName(settings);
        var made = blank ? Profiles.Blank(name) : Profiles.Duplicate(settings, settings.CurrentProfile, name);
        made.Id = id; settings.Profiles.Add(made);
        settings = Profiles.Switch(settings, made.Id);
        settings.ReturnProfile = null;
        UpdateSettings(settings);
    }
    // The next profile in the list, from the hotkey and the tray.
    internal void NextProfile()
    {
        var list = Settings.Profiles;
        if (list.Count < 2) return;
        int at = list.FindIndex(profile => profile.Id == Settings.CurrentProfile);
        ChooseProfile(list[(at + 1) % list.Count].Id);
    }

    // At start, a rule that held when BrimDeck last ended no longer does: the profile it replaced comes back before anything shows.
    private void ReturnFromRule()
    {
        if (Settings.ReturnProfile is not { } back || Profiles.Find(Settings, back) is null) return;
        Settings = Profiles.Switch(Settings, back);
        Settings.ReturnProfile = null;
        QueueSave();
    }

    // The rules are looked at every second and a half while any is on; with none, nothing runs.
    // The holding rule as the settings now have it, so an edit to it (its displays) applies with the settings.
    internal void RefreshHoldingRule()
    {
        if (HoldingRule is { } holding) HoldingRule = Settings.ProfileRules.FirstOrDefault(rule => rule.Id == holding.Id && rule.Enabled);
    }
    internal void ApplyProfileRules()
    {
        RefreshHoldingRule();
        bool wanted = Settings.ProfileRules.Any(rule => rule.Enabled);
        if (!wanted)
        {
            _ruleTimer?.Stop();
            if (_appliedRule is not null) QueueRuleCheck();
            return;
        }
        if (_ruleTimer is null)
        {
            _ruleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
            _ruleTimer.Tick += (_, _) => CheckProfileRules();
        }
        if (!_ruleTimer.IsEnabled) _ruleTimer.Start();
        QueueRuleCheck();
    }
    // Checked after the change that asked for it is done, not in the middle of applying settings.
    private void QueueRuleCheck()
    {
        if (_ruleCheckQueued) return;
        _ruleCheckQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _ruleCheckQueued = false; CheckProfileRules(); });
    }
    private void CheckProfileRules()
    {
        if (Exiting || Deck is null) return;
        var rules = Settings.ProfileRules;
        ProfileRule? holding = null;
        if (rules.Any(rule => rule.Enabled))
        {
            var (name, path) = rules.Any(rule => rule.Enabled && rule.Trigger == ProfileTrigger.Foreground) ? ForegroundApp() : ("", "");
            holding = Profiles.Holding(rules, ProcessSnapshot.AnyRecent, name, path);
        }
        if (holding?.Id == _appliedRule?.Id) { HoldingRule = holding; return; }
        RuleChanged(holding);
    }
    // The rule last acted on: a rule that keeps holding is not acted on again, so a profile the user picked meanwhile stays.
    private ProfileRule? _appliedRule;
    private void RuleChanged(ProfileRule? next)
    {
        var previous = _appliedRule;
        _appliedRule = next; HoldingRule = next;
        Guid? to = null, back = null;
        if (next?.Profile is { } wanted)
        {
            if (wanted != Settings.CurrentProfile) { to = wanted; back = Settings.ReturnProfile ?? Settings.CurrentProfile; }
        }
        else if (previous?.Profile is { } was && Settings.ReturnProfile is { } home && Settings.CurrentProfile == was) to = home;
        if (to is { } target && Profiles.Find(Settings, target) is not null)
        {
            // A rule that follows another keeps the profile the first one replaced to come back to.
            var settings = Profiles.Switch(Settings, target);
            settings.ReturnProfile = back;
            UpdateSettings(settings);
        }
        else ArrangePanels();
        _settingsWindow?.ProfilesChanged();
    }

    // The displays and their profiles again, after a rule changed them; every panel takes its settings.
    private void ArrangePanels()
    {
        var styles = Decks.ToDictionary(deck => deck, deck => deck.PanelSettings.Style);
        bool data = _dataKey != DataKey();
        AssignDisplays();
        ApplyToDecks(styles);
        StartMedia(); UpdateMonitorExtras();
        Shelf.Apply(ShelfSettings());
        if (data) { _dataKey = DataKey(); _ = Deck.RefreshAutomaticallyAsync(queueIfBusy: true); }
    }
    private string _dataKey = "";
    // Each panel takes its settings again; one whose collapsed style changed morphs into it (MainWindow.MorphShape).
    private void ApplyToDecks(IReadOnlyDictionary<MainWindow, CompactStyle> styles)
    {
        foreach (var deck in Decks.ToList())
        {
            deck.UseLook();
            if (styles.TryGetValue(deck, out var style) && style != deck.PanelSettings.Style) deck.MorphShape();
            else deck.ApplySettings();
        }
        // A panel's own look may have a night look with hours of its own.
        ScheduleDaylight();
    }
    // The clipboard is recorded while any panel shows its page.
    private DeckSettings ShelfSettings()
    {
        if (Settings.ClipboardPage || Deck is null || !ShownSettings.Any(settings => settings.ClipboardPage)) return Settings;
        var settings = Settings.Copy(); settings.ClipboardPage = true;
        return settings;
    }

    // The app whose window is in front: its process name and executable.
    private (string Name, string Path) ForegroundApp()
    {
        var window = GetForegroundWindow();
        // A panel that took the keyboard for a hotkey is in front (PanelKeyboard): the app the user was in is still the one in front.
        if (window != IntPtr.Zero && window == WindowsHost.KeyboardPanel) return _foreground;
        if (window == IntPtr.Zero) return ("", "");
        GetWindowThreadProcessId(window, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return ("", "");
        if (pid == _foregroundPid) return _foreground;
        _foregroundPid = pid; _foreground = AppIdentity.NameAndPath((int)pid);
        return _foreground;
    }
    private uint _foregroundPid;
    private (string Name, string Path) _foreground = ("", "");
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}

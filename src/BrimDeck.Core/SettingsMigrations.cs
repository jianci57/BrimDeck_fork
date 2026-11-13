using System.Text.Json;
using System.Text.Json.Nodes;
using NodeValue = System.Text.Json.Nodes.JsonValue;

namespace BrimDeck.Core;

// settings.json records AppVersion, the version of the BrimDeck that last saved it. Each step is labelled with the
// application version that introduced a format change and converts files saved by earlier versions. Read runs, in
// order, every step newer than the file's version and not newer than the running application; files without
// AppVersion count as 0.0.0.
// Reading stays tolerant: missing members take their defaults and unknown members are ignored, so a release that only
// adds settings needs no step. Add a step when a setting is renamed, split, merged or changes meaning, label it with the
// version the branch builds (its newest releases/<version>.md), and test it with a file in the earlier format. Release
// notes files are added by the maintainer, not for a step; the pull request check refuses a step with any other label.
public static class SettingsMigrations
{
    private static readonly (Version Version, Action<JsonObject> Apply)[] Steps =
    [
        (new Version(0, 1, 0), To0_1_0),
        (new Version(1, 2, 3), To1_2_3),
        (new Version(1, 3, 0), To1_3_0),
    ];

    // Upgrades the settings object in place and reads it. A file saved by a newer version runs no step.
    public static DeckSettings Read(JsonObject root, Version running, out Version saved)
    {
        saved = VersionOf(root);
        foreach (var (version, apply) in Steps)
            if (version > saved && version <= running) apply(root);
        MemoryModeFromSwitch(root);
        var settings = root.Deserialize<DeckSettings>() ?? new();
        settings.Normalize();
        return settings;
    }

    public static DeckSettings Read(string json, Version running, out Version saved) => Read(Parse(json), running, out saved);

    public static DeckSettings Read(string json) => Read(json, ApplicationVersion.Parse(ApplicationVersion.Current), out _);

    public static JsonObject Parse(string json) => JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Settings must be a JSON object.");

    public static Version VersionOf(JsonObject settings) =>
        ApplicationVersion.Parse(settings["AppVersion"] is NodeValue value && value.TryGetValue(out string? text) ? text : null);

    // 0.1.0: the conversions that earlier builds made implicitly while reading.
    private static void To0_1_0(JsonObject root)
    {
        // One pair of switches used to apply to both the notch and the capsule.
        Split(root, "CompactSummary", "NotchSummary", "CapsuleSummary");
        Split(root, "CompactMusic", "NotchMusic", "CapsuleMusic");
        // Earlier builds were Chinese only. Language now defaults to English, so a file without it keeps Chinese.
        if (!root.ContainsKey("Language")) root["Language"] = Loc.Chinese;
        // Four on/off switches preceded the ordered application list; their order matches ProviderCatalog.BuiltIns.
        string[] switches = ["Claude", "Codex", "Antigravity", "Cursor"];
        if (root["Apps"] is not JsonArray && switches.Any(root.ContainsKey))
            root["Apps"] = new JsonArray(ProviderCatalog.BuiltIns.Select((id, i) =>
                JsonSerializer.SerializeToNode(new AppEntry { QuotaSource = id, UsageSource = id, Enabled = Flag(root, switches[i]) })).ToArray());
        foreach (var name in switches) root.Remove(name);
        if (root["Apps"] is not JsonArray apps) return;
        foreach (var app in apps.OfType<JsonObject>())
        {
            // A row once had a single source, saved as Id.
            if (app["Id"] is { } id && !app.ContainsKey("QuotaSource")) app["QuotaSource"] = id.DeepClone();
            app.Remove("Id");
            // Statistics sources 8 and 9 were a short-lived split of Claude.
            if (app["UsageSource"] is NodeValue usage && usage.TryGetValue(out int source) && source is 8 or 9)
                app["UsageSource"] = (int)ProviderId.Claude;
        }
    }

    // 1.2.3: the equalizer has its own switch. It showed with the music, so it starts as the music's switch was. Its color was
    // the cover's or the theme's (MusicCoverColor); it is now chosen as the clock's is.
    private static void To1_2_3(JsonObject root)
    {
        foreach (var style in new[] { "Notch", "Capsule" })
            if (!root.ContainsKey(style + "Equalizer")) root[style + "Equalizer"] = Flag(root, style + "Music");
        if (!root.ContainsKey("EqualizerColor")) root["EqualizerColor"] = (int)(Flag(root, "MusicCoverColor") ? ClockColor.Cover : ClockColor.White);
        root.Remove("MusicCoverColor");
    }

    // 1.3.0: a device is no longer only on or off but shows in the collapsed row and on the page each, in an order and under a
    // name of the user's (DeviceChoices). A device switched off was off in both. The collapsed row's battery reading (256 among the
    // system readings) is a part of the row of its own (NotchDevices, CapsuleDevices), on where the readings showed it. Getting low
    // was ten points above low; it has its own threshold.
    private static void To1_3_0(JsonObject root)
    {
        if (root["CompactReadings"] is JsonArray readings)
        {
            var battery = readings.OfType<JsonObject>().FirstOrDefault(reading => reading["Item"] is NodeValue item && item.TryGetValue(out int number) && number == 256);
            bool shown = battery is not null && (battery["Visible"] is not NodeValue visible || !visible.TryGetValue(out bool on) || on);
            if (shown)
                foreach (var style in new[] { "Notch", "Capsule" })
                    if (!root.ContainsKey(style + "Devices")) root[style + "Devices"] = root[style + "Monitor"] is NodeValue monitor && monitor.TryGetValue(out bool showing) && showing;
            if (battery is not null) readings.Remove(battery);
        }
        if (!root.ContainsKey("DeviceWarningPercent") && root["DeviceLowPercent"] is NodeValue low && low.TryGetValue(out int percent)) root["DeviceWarningPercent"] = percent + 10;
        if (root["HiddenDevices"] is JsonArray hidden && !root.ContainsKey("DeviceChoices"))
            root["DeviceChoices"] = new JsonArray([.. hidden.OfType<JsonObject>().Select(device => (JsonNode)new JsonObject
            {
                ["Key"] = device["Key"]?.DeepClone(), ["Name"] = device["Name"]?.DeepClone(), ["Collapsed"] = false, ["Expanded"] = false
            })]);
        root.Remove("HiddenDevices");
        OrdinaryPanelRules(root);
    }

    // 1.3.0: the window rules apply to a panel that is not kept above other windows too; before, it was always left covered.
    // Such a panel keeps that: its three global rules are set to keep the style, which leaves it covered.
    private static void OrdinaryPanelRules(JsonObject root)
    {
        if (root["AlwaysOnTop"] is not NodeValue above || !above.TryGetValue(out bool on) || on) return;
        foreach (var name in new[] { "Maximized", "Borderless", "Exclusive" }) root[name] = (int)WindowBehavior.Normal;
    }

    // 1.3.0: "free memory when idle" (TrimMemoryWhenIdle) became one of three modes (MemoryMode). Switched on, it did what the
    // saver mode does; switched off, the file takes the balanced mode. Not a step: the switch changed during the 1.3.0 prereleases,
    // whose files count as 1.3.0 and would skip a 1.3.0 step. It runs on any file that has the switch and no mode, which only a
    // version before the change writes (later versions write both).
    private static void MemoryModeFromSwitch(JsonObject root)
    {
        if (root.ContainsKey("MemoryMode") || root["TrimMemoryWhenIdle"] is not NodeValue trim || !trim.TryGetValue(out bool on)) return;
        root["MemoryMode"] = (int)(on ? MemoryMode.Saver : MemoryMode.Balanced);
    }

    private static bool Flag(JsonObject root, string name) => root[name] is not NodeValue value || !value.TryGetValue(out bool flag) || flag;

    private static void Split(JsonObject root, string old, params string[] names)
    {
        if (root[old] is not { } value) return;
        foreach (var name in names)
            if (!root.ContainsKey(name)) root[name] = value.DeepClone();
        root.Remove(old);
    }
}

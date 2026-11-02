using System.Text.Json;
using BrimDeck.Core;

internal static class FullscreenRuleTests
{
    internal static void Run(Action<string, bool> check)
    {
        // Matching, unchanged from the exceptions it grew out of.
        var byName = new FullscreenRule("Sahhpire");
        check("A rule matches its process name regardless of case",
            ScreenPolicy.Matches(byName, "sahhpire", "") && ScreenPolicy.Matches(byName, "SAHHPIRE", null));
        check("A rule ignores the .exe suffix on either side",
            ScreenPolicy.Matches(byName, "Sahhpire.exe", "") && ScreenPolicy.Matches(new FullscreenRule("Sahhpire.exe"), "Sahhpire", ""));
        check("Another app does not match a rule", !ScreenPolicy.Matches(byName, "Sahhpire2", ""));
        var byPath = new FullscreenRule("Other", @"F:\Apps\Sahhpire\Sahhpire.exe");
        check("A rule matches its executable path whichever slash direction is reported",
            ScreenPolicy.Matches(byPath, "unrelated", @"f:\apps\sahhpire\SAHHPIRE.EXE") && ScreenPolicy.Matches(byPath, "unrelated", "F:/Apps/Sahhpire/Sahhpire.exe"));
        check("A rule still matches by name when the executable path cannot be read",
            ScreenPolicy.Matches(byPath, "Other", "") && ScreenPolicy.Matches(byPath, "Other", null));
        check("A rule does not match a different executable", !ScreenPolicy.Matches(byPath, "unrelated", @"F:\Other\Sahhpire.exe"));

        // The behavior a context calls for: the app's own rule first, the global rules everywhere else.
        var global = new DeckSettings();  // Maximized Line, Borderless Hide, Exclusive Hide
        check("Without a rule the global rules answer",
            global.Behavior(ScreenContext.Maximized) == WindowBehavior.Line && global.Behavior(ScreenContext.Borderless) == WindowBehavior.Hide
            && global.Behavior(ScreenContext.Desktop) == WindowBehavior.Normal && global.Behavior(ScreenContext.Maximized, "Any", "") == WindowBehavior.Line);
        var exempt = new FullscreenRule("Sahhpire") { Maximized = WindowBehavior.Normal, Borderless = WindowBehavior.Normal, Exclusive = WindowBehavior.Normal };
        var blanket = new DeckSettings { FullscreenRules = [exempt] };
        check("A rule that keeps the style everywhere exempts the app from all three",
            blanket.Behavior(ScreenContext.Maximized, "sahhpire", "") == WindowBehavior.Normal
            && blanket.Behavior(ScreenContext.Borderless, "Sahhpire", @"F:\x\Sahhpire.exe") == WindowBehavior.Normal
            && blanket.Behavior(ScreenContext.Exclusive, "sahhpire") == WindowBehavior.Normal
            && blanket.Behavior(ScreenContext.Borderless, "Other", "") == WindowBehavior.Hide);
        var mixed = new DeckSettings { FullscreenRules = [new FullscreenRule("Sahhpire") { Maximized = WindowBehavior.Hide, Exclusive = WindowBehavior.Line }] };
        check("A context left on Follow answers with the global rules",
            mixed.Behavior(ScreenContext.Maximized, "Sahhpire") == WindowBehavior.Hide
            && mixed.Behavior(ScreenContext.Borderless, "Sahhpire") == WindowBehavior.Hide   // follows the global rule
            && mixed.Behavior(ScreenContext.Exclusive, "Sahhpire") == WindowBehavior.Line);
        check("A desktop context has no rule and no override",
            mixed.Behavior(ScreenContext.Desktop, "Sahhpire") == WindowBehavior.Normal);

        // Saved settings: the rules round-trip, and the exceptions of an older file become rules.
        var saved = JsonSerializer.Deserialize<DeckSettings>("""{"FullscreenRules":[{"Name":"Sahhpire","Path":"","Maximized":2,"Borderless":null,"Exclusive":1}]}""")!;
        saved.Normalize();
        check("A rule's choices are saved and read back per context",
            saved.FullscreenRules is [var round] && round.Name == "Sahhpire" && round.Maximized == WindowBehavior.Hide
            && round.Borderless is null && round.Exclusive == WindowBehavior.Line);
        var legacy = JsonSerializer.Deserialize<DeckSettings>("""{"FullscreenExceptions":[{"Name":"Sahhpire","Path":"D:\\Sapphire\\Sapphire.exe"}]}""")!;
        legacy.Normalize();
        check("Exceptions saved before app rules become rules that keep the style in all three",
            legacy.FullscreenRules is [var moved] && moved.Name == "Sahhpire" && moved.Maximized == WindowBehavior.Normal
            && moved.Borderless == WindowBehavior.Normal && moved.Exclusive == WindowBehavior.Normal
            && legacy.FullscreenExceptions.Count == 0
            && legacy.Behavior(ScreenContext.Borderless, "Sapphire", @"D:\Sapphire\Sapphire.exe") == WindowBehavior.Normal);
        var settings = new DeckSettings
        {
            FullscreenRules =
            [
                new(" Sahhpire ", @"F:\Apps\Sahhpire\Sahhpire.EXE"),
                new("sahhpire", @"f:\apps\sahhpire\sahhpire.exe"),
                new("", ""),
                new("", @"F:\Apps\Other\Other.exe")
            ]
        };
        settings.Normalize();
        check("Duplicate and empty rules are cleaned up and a path-only rule takes its file name",
            settings.FullscreenRules.Count == 2
            && settings.FullscreenRules[0] is { Name: "Sahhpire", Maximized: null }
            && settings.FullscreenRules[1] is { Name: "Other" });
        var copy = settings.Copy();
        copy.FullscreenRules.Add(new FullscreenRule("Extra"));
        check("A settings copy keeps its own rules", settings.FullscreenRules.Count == 2 && copy.FullscreenRules.Count == 3);
        check("What changed: a context change outranks an app change",
            ScreenPolicy.WhatChanged(ScreenContext.Maximized, "a", "p", ScreenContext.Desktop, "b", "q") == ScreenPolicy.ForegroundChange.Context);
        check("What changed: the same context with another app is an app change",
            ScreenPolicy.WhatChanged(ScreenContext.Maximized, "app2", "p", ScreenContext.Maximized, "app1", "p") == ScreenPolicy.ForegroundChange.App
            && ScreenPolicy.WhatChanged(ScreenContext.Maximized, "app", "q", ScreenContext.Maximized, "app", "p") == ScreenPolicy.ForegroundChange.App);
        check("What changed: identical readings change nothing",
            ScreenPolicy.WhatChanged(ScreenContext.Desktop, "app", "p", ScreenContext.Desktop, "app", "p") == ScreenPolicy.ForegroundChange.None);
        var many = new DeckSettings { FullscreenRules = Enumerable.Range(0, 40).Select(i => new FullscreenRule("App" + i)).ToList() };
        many.Normalize();
        check("The rule list is capped", many.FullscreenRules.Count == AppPresets.MaximumFullscreenExceptions);
        // A full-screen window behind the foreground one has no rules of its own: the way it fills the screen picks the
        // rule, the app's own first and the global one after it, as if that window were in front.
        var bg = new DeckSettings { Maximized = WindowBehavior.Line, Borderless = WindowBehavior.Hide };
        check("A background full screen follows the global rule for the way it fills the screen",
            bg.Behavior(ScreenContext.BackgroundFullscreen, null, null, ScreenContext.Maximized) == WindowBehavior.Line
            && bg.Behavior(ScreenContext.BackgroundFullscreen, null, null, ScreenContext.Borderless) == WindowBehavior.Hide
            && bg.Behavior(ScreenContext.BackgroundFullscreen, "Player", @"C:\Apps\Player.exe", ScreenContext.Maximized) == WindowBehavior.Line);
        bg.Maximized = WindowBehavior.Normal;
        check("Changing the global rule changes the background answer with it",
            bg.Behavior(ScreenContext.BackgroundFullscreen, null, null, ScreenContext.Maximized) == WindowBehavior.Normal);
        check("A background full screen without a kind, and the desktop itself, leave the panel as it is",
            bg.Behavior(ScreenContext.BackgroundFullscreen) == WindowBehavior.Normal
            && bg.Behavior(ScreenContext.Desktop) == WindowBehavior.Normal);
        var browser = new DeckSettings
        {
            Maximized = WindowBehavior.Line, Borderless = WindowBehavior.Hide,
            FullscreenRules = [new FullscreenRule("chrome") { Maximized = WindowBehavior.Normal }]
        };
        check("An app that keeps the style while maximised keeps it behind the foreground window too",
            browser.Behavior(ScreenContext.BackgroundFullscreen, "chrome", "", ScreenContext.Maximized) == WindowBehavior.Normal
            && browser.Behavior(ScreenContext.Maximized, "chrome", "") == WindowBehavior.Normal);
        check("A background window its app's rule says nothing about takes the global rule",
            browser.Behavior(ScreenContext.BackgroundFullscreen, "firefox", "", ScreenContext.Maximized) == WindowBehavior.Line
            && browser.Behavior(ScreenContext.BackgroundFullscreen, "chrome", "", ScreenContext.Borderless) == WindowBehavior.Hide);
    }
}

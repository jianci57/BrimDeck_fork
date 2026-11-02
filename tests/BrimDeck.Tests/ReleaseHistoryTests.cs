using BrimDeck.Core;

internal static class ReleaseHistoryTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        var notes = ReleaseHistory.Parse("<!-- lang: en-US -->\r\nNew\r\n- One\r\n\r\n<!-- lang: zh-CN -->\r\n新功能\r\n- 一\r\n");
        check("release notes split by language marker", notes.For("en-US") == "New\n- One" && notes.For("zh-CN") == "新功能\n- 一");
        check("release notes without markers serve every language", ReleaseHistory.Parse("Fixes\n- A").For("zh-CN") == "Fixes\n- A");

        var bundled = ReleaseHistory.Bundled;
        var files = Directory.GetFiles(Path.Combine(Repository(), "releases"), "*.md").Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        check("every release notes file is built in", bundled.Select(e => e.Version).Order().SequenceEqual(files));
        check("every built-in release has Chinese and English notes", bundled.All(e =>
            e.Notes.Entries.Any(n => n.Language == "zh-CN") && e.Notes.Entries.Any(n => n.Language == "en-US")));

        ReleaseEntry Entry(string version) => new(version, ReleaseNotes.Of(version));
        var all = new[] { "1.0.0", "1.1.0-alpha.1", "1.1.0-beta.1", "1.1.0", "1.2.0-alpha.1", "1.2.0-beta.1", "1.2.0-beta.2", "1.2.0" }.Select(Entry).ToList();
        string Versions(IEnumerable<ReleaseEntry> entries) => string.Join(" ", entries.Select(e => e.Version));
        check("releases are listed newest first without prereleases",
            Versions(ReleaseHistory.Shown(all, "1.2.0", beta: false)) == "1.2.0 1.1.0 1.0.0");
        check("beta updates on lists no betas a release already gathered",
            Versions(ReleaseHistory.Shown(all, "1.2.0", beta: true)) == "1.2.0 1.1.0 1.0.0");
        check("beta updates on lists betas newer than the last release, never alphas",
            Versions(ReleaseHistory.Shown(all, "1.2.0-beta.2", beta: true)) == "1.2.0-beta.2 1.2.0-beta.1 1.1.0 1.0.0");
        check("beta updates off lists no betas even on a beta",
            Versions(ReleaseHistory.Shown(all, "1.2.0-beta.2", beta: false)) == "1.1.0 1.0.0");
        check("versions newer than this copy are not listed", Versions(ReleaseHistory.Shown(all, "1.1.0", beta: false)) == "1.1.0 1.0.0");

        check("versions since the previous one are marked new", ReleaseHistory.IsNew("1.2.0", "1.0.0", "1.2.0") &&
            ReleaseHistory.IsNew("1.1.0", "1.0.0", "1.2.0") && !ReleaseHistory.IsNew("1.0.0", "1.0.0", "1.2.0"));
        check("an unrecorded previous version marks only this one", ReleaseHistory.IsNew("1.2.0", "", "1.2.0") && !ReleaseHistory.IsNew("1.1.0", "", "1.2.0"));
        check("nothing is marked without an update", !ReleaseHistory.IsNew("1.2.0", null, "1.2.0"));

        var backend = new UpdateFixture { CurrentVersion = "1.0.0" };
        var fresh = Path.Combine(root, "release-history-fresh");
        check("a new installation announces nothing", new AppUpdates(backend, fresh).UpdatedFrom is null);
        backend.CurrentVersion = "1.1.0";
        var updated = new AppUpdates(backend, fresh);
        check("an update remembers the version it replaced", updated.UpdatedFrom == "1.0.0" && new AppUpdates(backend, fresh).UpdatedFrom == "1.0.0");
        backend.CurrentVersion = "1.2.0";
        check("a second update before the notes are opened keeps the earlier version", new AppUpdates(backend, fresh).UpdatedFrom == "1.0.0");
        var seen = new AppUpdates(backend, fresh); seen.MarkNotesSeen();
        check("opening the notes ends the notice", seen.UpdatedFrom is null && new AppUpdates(backend, fresh).UpdatedFrom is null);
        backend.CurrentVersion = "1.3.0"; new AppUpdates(backend, fresh);
        backend.CurrentVersion = "1.1.0";
        check("a downgrade clears the notice", new AppUpdates(backend, fresh).UpdatedFrom is null);

        var older = Path.Combine(root, "release-history-older"); Directory.CreateDirectory(older);
        File.WriteAllText(Path.Combine(older, "update-state.json"), """{"AutoCheck":true}""");
        check("an update from a version that did not record itself is announced", new AppUpdates(backend, older).UpdatedFrom == "");
    }

    private static string Repository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (Directory.Exists(Path.Combine(folder.FullName, "releases")) && Directory.Exists(Path.Combine(folder.FullName, "src"))) return folder.FullName;
        throw new DirectoryNotFoundException("The repository folder was not found.");
    }
}

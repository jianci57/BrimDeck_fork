using System.Globalization;
using BrimDeck.Core;

internal static class LanguageTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        string[] chinese = ["zh-CN", "zh-TW", "zh-HK", "zh-SG", "zh-Hans-CN", "zh-Hant-TW"];
        string[] other = ["en-US", "en-GB", "ja-JP", "fr-FR", "ko-KR"];
        check("every Chinese display language, Traditional included, chooses Simplified Chinese",
            chinese.All(name => Loc.SystemLanguage(new CultureInfo(name)) == Loc.Chinese));
        check("any other display language chooses English", other.All(name => Loc.SystemLanguage(new CultureInfo(name)) == Loc.English));
        var unknown = new DeckSettings { Language = "fr-FR" }; unknown.Normalize();
        check("an unknown saved language reads as English", unknown.Language == Loc.English);

        var fresh = new SettingsStore(Path.Combine(root, "language-first"), "0.1.0");
        var first = fresh.Load();
        check("a first start follows the Windows display language", fresh.IsFirstStart && first.Language == Loc.SystemLanguage());
        first.Language = Loc.SystemLanguage() == Loc.Chinese ? Loc.English : Loc.Chinese; fresh.Save(first);
        var second = new SettingsStore(fresh.DirectoryPath, "0.1.0");
        check("once saved, the language is read back and not detected again", second.Load().Language == first.Language && !second.IsFirstStart);

        var upgrade = new SettingsStore(Path.Combine(root, "language-upgrade"), "0.2.0");
        Directory.CreateDirectory(upgrade.DirectoryPath);
        File.WriteAllText(upgrade.FilePath, """{"AppVersion":"0.1.0","Language":"zh-CN","Width":900}""");
        check("an upgrade keeps the language of the earlier version", upgrade.Load().Language == Loc.Chinese && !upgrade.IsFirstStart);
        File.WriteAllText(upgrade.FilePath, """{"AppVersion":"0.1.0","Language":"en-US"}""");
        check("an upgrade keeps English when it was chosen", upgrade.Load().Language == Loc.English);
        File.WriteAllText(upgrade.FilePath, """{"Width":900}""");
        check("a file from before languages were saved stays Chinese", upgrade.Load().Language == Loc.Chinese && !upgrade.IsFirstStart);
        File.WriteAllText(upgrade.FilePath, "{ not json");
        var broken = upgrade.Load();
        check("unreadable settings fall back to the display language and warn", upgrade.IsFirstStart && broken.Language == Loc.SystemLanguage() && upgrade.LoadWarning is not null);

        try
        {
            Loc.Use(Loc.English);
            check("texts and the load warning follow the language when shown", Loc.T("设置", "Settings") == "Settings" && !upgrade.LoadWarning!.Any(c => c is >= '一' and <= '鿿'));
            check("labels a custom script returns are shown as they are", Loc.Label("我的额度") == "我的额度" && Loc.Label("Monthly") == "Monthly");
        }
        finally { Loc.Use(Loc.Chinese); }
        check("Chinese shows labels as they are", Loc.Label("5 小时额度") == "5 小时额度" && Loc.T("设置", "Settings") == "设置");
    }
}

using BrimDeck.Core;

// The clipboard and file transfer page's rules (Shelf, ShelfLayout): what kind a copy is, how the history is kept and cleared,
// how files are added, how the page sizes itself, and that the settings and the saved data read back.
internal static class ShelfTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("Shelf kinds: a single web address is a link, other text is not",
            Shelf.Classify("https://github.com/MoFeng2223/BrimDeck/pull/175") == ClipKind.Link && Shelf.Classify("see https://example.com") == ClipKind.Text
            && Shelf.Classify("ftp://example.com/file") != ClipKind.Link);
        check("Shelf kinds: colors in hex and rgb() are colors",
            Shelf.Classify("#0A84FF") == ClipKind.Color && Shelf.Classify("#fff") == ClipKind.Color && Shelf.Classify("rgb(10, 132, 255)") == ClipKind.Color
            && Shelf.Classify("#12345") != ClipKind.Color && Shelf.Classify("rgb(300, 0, 0)") != ClipKind.Color);
        check("Shelf kinds: code is told from prose",
            Shelf.Classify("public static void Main()\n{\n    Console.WriteLine(1);\n}") == ClipKind.Code
            && Shelf.Classify("var total = items.Sum(item => item.Price);") == ClipKind.Code
            && Shelf.Classify("明天下午三点开会，请准时参加。\n地点在三楼会议室。") == ClipKind.Text);
        Shelf.TryColor("#0A84FF", out var blue);
        var forms = Shelf.ColorForms(blue);
        check("Shelf colors: HEX, RGB and HSL forms", forms.Hex == "#0A84FF" && forms.Rgb == "rgb(10, 132, 255)" && forms.Hsl == "hsl(210, 100%, 52%)");
        check("Shelf links: the host and the rest are split, www dropped",
            Shelf.SplitLink("https://www.example.com/a/b?c=1") == ("example.com", "/a/b?c=1") && Shelf.SplitLink("https://example.com/") == ("example.com", ""));
        check("Shelf rows: the first line with text and how many follow", Shelf.FirstLine("\n  first\nsecond\n\nthird\n") == ("first", 3) && Shelf.FirstLine("one") == ("one", 0));

        // The same text copied again moves up and keeps its categories; pictures are always new entries.
        var data = new ShelfData();
        var category = new ShelfCategory { Name = "工作" };
        data.ClipCategories.Add(category);
        var now = DateTimeOffset.Now;
        var first = Shelf.Add(data, new ClipEntry { Text = "hello", Copied = now.AddMinutes(-5) });
        first.Categories.Add(category.Id);
        Shelf.Add(data, new ClipEntry { Text = "other", Copied = now.AddMinutes(-4) });
        var again = Shelf.Add(data, new ClipEntry { Text = "hello", Copied = now });
        check("Shelf history: copying the same text again moves its entry to the top and keeps its category",
            ReferenceEquals(first, again) && data.Clips.Count == 2 && data.Clips[0] == first && first.Categories.SequenceEqual([category.Id]) && first.Copied == now);

        var other = data.Clips[1];
        Shelf.Recopied(data, other, now.AddMinutes(1));
        check("Shelf history: an entry copied again from the page goes to the top with the new time and keeps its category",
            data.Clips[0] == other && other.Copied == now.AddMinutes(1) && data.Clips.Count == 2 && first.Categories.SequenceEqual([category.Id]));
        Shelf.Recopied(data, first, now);

        // The history goes when BrimDeck exits; phrases, 常用 and categories set to keep stay; a category not kept goes with the history.
        var history = new ShelfData();
        var kept = new ShelfCategory { Name = "保留" };
        var passing = new ShelfCategory { Name = "临时", Keep = false };
        history.ClipCategories.AddRange([kept, passing]);
        for (int i = 0; i < 5; i++) history.Clips.Add(new ClipEntry { Text = "t" + i, Copied = now });
        history.Clips.Add(new ClipEntry { Text = "phrase", Manual = true });
        history.Clips.Add(new ClipEntry { Text = "favorite", Categories = [ShelfData.Favorites] });
        history.Clips.Add(new ClipEntry { Text = "kept", Categories = [kept.Id] });
        history.Clips.Add(new ClipEntry { Text = "passing", Categories = [passing.Id] });
        history.Clips.Add(new ClipEntry { Text = "both", Categories = [passing.Id, kept.Id] });
        check("Shelf history: what goes on exit is the history and what is only in categories not kept",
            Shelf.History(history).Select(c => c.Text).Order().SequenceEqual(["passing", "t0", "t1", "t2", "t3", "t4"]));
        var made = Shelf.AddCategory(history, ShelfSide.Clipboard, "新", ["#111111"]);
        var reread = ShelfData.Deserialize(history.Serialize());
        check("Shelf history: a category made now is cleared on exit; one saved before the choice stays kept; the choice is saved",
            !made.Keep && ShelfData.Deserialize(System.Text.Encoding.UTF8.GetBytes("{\"ClipCategories\":[{\"Name\":\"a\"}]}")).OwnCategories(ShelfSide.Clipboard).Single().Keep
            && reread.ClipCategories.Single(c => c.Id == made.Id).Keep == false && reread.ClipCategories.Single(c => c.Id == kept.Id).Keep
            && reread.ClipCategories.Single(c => c.Id == passing.Id).Keep == false);

        // Files: newest first, a file dropped again moves up, the category is kept or given.
        var shelf = new ShelfData();
        Shelf.AddFiles(shelf, [@"C:\a.txt", @"C:\b.txt"], null, now);
        Shelf.AddFiles(shelf, [@"c:\A.TXT"], category.Id, now.AddMinutes(1));
        check("Shelf files: dropped files go to the top in their order, and a file dropped again moves up into the category",
            shelf.Files.Count == 2 && shelf.Files[0].Path == @"C:\a.txt" && shelf.Files[0].Categories.SequenceEqual([category.Id]) && shelf.Files[1].Path == @"C:\b.txt"
            && shelf.Files[0].Name == "a.txt");
        check("Shelf files: 清除 takes every file in 全部 and only a category's files in it",
            Shelf.ClearableFiles(shelf, null).Count == 2 && Shelf.ClearableFiles(shelf, category.Id).Single().Path == @"C:\a.txt");
        // A file can be in several categories; clearing one category removes the files only in it and takes the others out of it.
        var archive = new ShelfCategory { Name = "归档" };
        Shelf.AddFiles(shelf, [@"C:\b.txt"], archive.Id, now.AddMinutes(2));
        Shelf.AddFiles(shelf, [@"C:\b.txt"], category.Id, now.AddMinutes(3));
        check("Shelf files: a file dropped into a second category is in both",
            shelf.Files[0].Path == @"C:\b.txt" && shelf.Files[0].Categories.SequenceEqual([archive.Id, category.Id]));
        var cleared = Shelf.ClearFiles(shelf, category.Id);
        check("Shelf files: clearing a category removes the files only in it; a file also in another only leaves it",
            cleared.Single().Path == @"C:\a.txt" && shelf.Files.Single(f => f.Path == @"C:\b.txt").Categories.SequenceEqual([archive.Id])
            && shelf.Files.Single(f => f.Path == @"C:\a.txt").Categories.Count == 0);
        shelf.FileCategories.Add(category);
        shelf.FileCategories.Add(archive);
        Shelf.DeleteCategory(shelf, ShelfSide.Files, archive.Id);
        check("Shelf categories: deleting a category takes it off its files", shelf.FileCategories.Single() == category && shelf.Files.All(file => file.Categories.Count == 0));

        // Saved data reads back; an entry whose category is gone returns to all.
        data.Clips.Add(new ClipEntry { Kind = ClipKind.Image, PixelWidth = 10, PixelHeight = 5, Copied = now });
        data.Clips.Add(new ClipEntry { Text = "lost", Categories = [Guid.NewGuid()] });
        var read = ShelfData.Deserialize(data.Serialize());
        check("Shelf data: saved and read back, with a category that is gone dropped",
            read.Clips.Count == data.Clips.Count && read.Clips[0].Categories.SequenceEqual([category.Id]) && read.Clips.Single(c => c.Text == "lost").Categories.Count == 0
            && read.Clips.Any(c => c.Kind == ClipKind.Image && c.PixelWidth == 10) && read.OwnCategories(ShelfSide.Clipboard).Single().Name == "工作");

        // "常用": first in every history, old ones included; phrases written before it existed move into it; it is never deleted.
        var old = ShelfData.Deserialize(System.Text.Encoding.UTF8.GetBytes(
            "{\"Clips\":[{\"Text\":\"hi\",\"Manual\":true},{\"Text\":\"copied\"}],\"ClipCategories\":[{\"Id\":\"" + category.Id + "\",\"Name\":\"工作\"}]}"));
        check("Shelf favorites: an older history gains 常用 first, with its phrases in it",
            old.ClipCategories.Count == 2 && old.ClipCategories[0].Id == ShelfData.Favorites && old.ClipCategories[1].Id == category.Id
            && old.Clips.Single(c => c.Text == "hi").Categories.SequenceEqual([ShelfData.Favorites]) && old.Clips.Single(c => c.Text == "copied").Categories.Count == 0);
        Shelf.DeleteCategory(old, ShelfSide.Clipboard, ShelfData.Favorites);
        var phrase = Shelf.AddPhrase(old, "new phrase", now);
        check("Shelf favorites: 常用 cannot be deleted, and a new phrase goes to the top of it",
            old.ClipCategories[0].Id == ShelfData.Favorites && old.Clips[0] == phrase && phrase.Manual && phrase.Categories.SequenceEqual([ShelfData.Favorites]) && old.Kept(phrase));
        var second = Shelf.AddCategory(old, ShelfSide.Clipboard, "代码", ["#111111", "#222222"]);
        Shelf.Reorder(old, ShelfSide.Clipboard, [second.Id, category.Id]);
        check("Shelf categories: new ones take the next color and go last; reordering keeps 常用 first",
            second.Color == "#222222" && old.ClipCategories.Select(c => c.Id).SequenceEqual([ShelfData.Favorites, second.Id, category.Id]));
        old.Clips.Add(new ClipEntry { Text = "filed", Categories = [second.Id] });
        check("Shelf clearing: 全部 clears all but 常用, a category of the user's only its own entries, 常用 nothing",
            Shelf.Clearable(old, null).Select(c => c.Text).Order().SequenceEqual(["copied", "filed"])
            && Shelf.Clearable(old, second.Id).Single().Text == "filed" && Shelf.Clearable(old, ShelfData.Favorites).Count == 0);

        // Several categories: an entry is put in one without leaving the others; clearing a category deletes what is only in it.
        var both = Shelf.Add(old, new ClipEntry { Text = "both", Copied = now });
        Shelf.SetCategory([both.Categories], second.Id, add: true);
        Shelf.SetCategory([both.Categories], category.Id, add: true);
        Shelf.SetCategory([both.Categories], category.Id, add: true);
        Shelf.SetCategory([both.Categories, phrase.Categories], second.Id, add: true);
        check("Shelf categories: an entry can be in several, each once, and 常用 is one of them",
            both.Categories.SequenceEqual([second.Id, category.Id]) && phrase.Categories.SequenceEqual([ShelfData.Favorites, second.Id]));
        // "工作" was saved before the choice, so it is kept; "代码" was made now, so it is cleared on exit.
        check("Shelf clearing: 全部 keeps what is in 常用 or in any category kept, and clears what is only in categories not kept",
            !Shelf.Clearable(old, null).Contains(phrase) && !Shelf.Clearable(old, null).Contains(both) && Shelf.Clearable(old, null).Any(c => c.Text == "filed"));
        var gone = Shelf.Clear(old, second.Id);
        check("Shelf clearing: a category deletes the entries only in it; those also elsewhere leave it and stay there",
            gone.Single().Text == "filed" && both.Categories.SequenceEqual([category.Id]) && phrase.Categories.SequenceEqual([ShelfData.Favorites]));
        Shelf.SetCategory([both.Categories], category.Id, add: false);
        check("Shelf categories: taken out of its last category, an entry is history again", both.Categories.Count == 0 && !old.Kept(both));

        // Saved with several categories: the first is also written as "Category" for the versions that know one; their files read in.
        both.Categories.AddRange([category.Id, second.Id]);
        var json = System.Text.Encoding.UTF8.GetString(old.Serialize());
        var again2 = ShelfData.Deserialize(old.Serialize());
        check("Shelf data: several categories read back, and the first is written for older versions",
            json.Contains("\"Category\":\"" + category.Id + "\"") && again2.Clips.Single(c => c.Text == "both").Categories.SequenceEqual([category.Id, second.Id]));
        var legacy = ShelfData.Deserialize(System.Text.Encoding.UTF8.GetBytes(
            "{\"Clips\":[{\"Text\":\"one\",\"Category\":\"" + category.Id + "\"}],\"Files\":[{\"Path\":\"C:\\\\a.txt\",\"Category\":\"" + category.Id + "\"}],"
            + "\"ClipCategories\":[{\"Id\":\"" + category.Id + "\",\"Name\":\"工作\"}],\"FileCategories\":[{\"Id\":\"" + category.Id + "\",\"Name\":\"工作\"}]}"));
        check("Shelf data: a history saved with one category per entry reads into the list",
            legacy.Clips.Single().Categories.SequenceEqual([category.Id]) && legacy.Files.Single().Categories.SequenceEqual([category.Id]) && legacy.Kept(legacy.Clips.Single()));

        // Explicit empty lists survive saving, including phrases edited in settings with category None.
        var choices = new ShelfData();
        var unfiled = Shelf.AddPhrase(choices, "none", now);
        unfiled.Categories.Clear();
        var removedFavorite = Shelf.AddPhrase(choices, "removed", now);
        Shelf.SetCategory([removedFavorite.Categories], ShelfData.Favorites, add: false);
        var choicesRead = ShelfData.Deserialize(choices.Serialize());
        check("Shelf phrases: None and removing the last favorite survive repeated roundtrips and stay kept",
            choicesRead.Clips.All(c => c.Categories.Count == 0 && choicesRead.Kept(c))
            && ShelfData.Deserialize(choicesRead.Serialize()).Clips.All(c => c.Categories.Count == 0));
        var explicitOldEmpty = ShelfData.Deserialize(System.Text.Encoding.UTF8.GetBytes(
            "{\"Clips\":[{\"Text\":\"empty\",\"Manual\":true,\"Categories\":[],\"Category\":null}]}"));
        var oldManual = ShelfData.Deserialize(System.Text.Encoding.UTF8.GetBytes(
            "{\"Clips\":[{\"Text\":\"legacy\",\"Manual\":true,\"Category\":null}]}"));
        var oldFiled = ShelfData.Deserialize(System.Text.Encoding.UTF8.GetBytes(
            "{\"Clips\":[{\"Text\":\"legacy filed\",\"Manual\":true,\"Category\":\"" + category.Id + "\"}],"
            + "\"ClipCategories\":[{\"Id\":\"" + category.Id + "\",\"Name\":\"work\"}]}"));
        check("Shelf phrases: legacy omitted categories gain Favorites; explicit [] and legacy filed choices stay intact",
            explicitOldEmpty.Clips.Single().Categories.Count == 0
            && oldManual.Clips.Single().Categories.SequenceEqual([ShelfData.Favorites])
            && oldFiled.Clips.Single().Categories.SequenceEqual([category.Id]));
        var deletedCategory = ShelfData.Deserialize(System.Text.Encoding.UTF8.GetBytes(
            "{\"Clips\":[{\"Text\":\"orphan\",\"Manual\":true,\"Category\":\"" + Guid.NewGuid() + "\"}]}"));
        check("Shelf phrases: an obsolete explicit category returns to All instead of gaining Favorites",
            deletedCategory.Clips.Single().Categories.Count == 0);

        Rules(check, now);

        check("Shelf sizes: file sizes as the Explorer writes them",
            Shelf.Size(512) == "512 B" && Shelf.Size(1536) == "1.5 KB" && Shelf.Size(25 * 1024 * 1024) == "25 MB" && Shelf.Size(3L * 1024 * 1024 * 1024) == "3 GB");
        check("Shelf search: matches ignore case and are found in the line",
            Shelf.Matches("Hello World", "world") && !Shelf.Matches("Hello", " ") && Shelf.MatchesIn("abcABC", "bc").SequenceEqual([(1, 2), (4, 2)]));

        // The page's sizes (the design's table): three steps by height; the file column 40 % of the room within 170 and 340.
        var compact = ShelfLayout.For(440, 140);
        var standard = ShelfLayout.For(620, 220);
        var spacious = ShelfLayout.For(1200, 400);
        check("Shelf layout: steps by height", compact.Tier == ShelfTier.Compact && compact.Row == 22 && standard.Tier == ShelfTier.Standard && standard.Row == 26
            && spacious.Tier == ShelfTier.Spacious && spacious.Row == 30 && spacious.TwoLines && ShelfLayout.For(620, 179).Tier == ShelfTier.Compact && ShelfLayout.For(620, 280).Tier == ShelfTier.Spacious);
        check("Shelf layout: the file column is 40 % within 170 and 340, and shows sizes from 210 and dates from 300 when spacious",
            compact.FileColumn == 170 && !compact.FileSize && standard.FileColumn == Math.Round((620 - 40 - 32) * .4) && standard.FileSize
            && spacious.FileColumn == 340 && spacious.FileDate && !ShelfLayout.For(860, 260).FileDate);
        check("Shelf layout: the search box is 36 % of the width within 180 and 280, and the details have a side column from 560",
            ShelfLayout.SearchWidth(440) == 180 && ShelfLayout.SearchWidth(620) == 223 && ShelfLayout.SearchWidth(1200) == 280 && ShelfLayout.DetailSide(560) && !ShelfLayout.DetailSide(559));

        // Settings: the page is on and records from the start; the page has its own size; the count part shows only with the page.
        var settings = new DeckSettings();
        check("Shelf settings: the page is shown and recording is on",
            settings.ClipboardPage && settings.ClipboardRecord && DeckPages.Order.Contains(DeckPage.Clipboard));
        check("Shelf settings: a settings file that turned recording off keeps it off",
            !System.Text.Json.JsonSerializer.Deserialize<DeckSettings>("{\"ClipboardRecord\":false}")!.ClipboardRecord);
        var dragOut = new DeckSettings { TransferDragOut = (TransferDragOut)7 };
        dragOut.Normalize();
        check("Shelf settings: files dragged out are copied unless moving is chosen; an older file copies; an unknown choice copies",
            settings.TransferDragOut == TransferDragOut.Copy && System.Text.Json.JsonSerializer.Deserialize<DeckSettings>("{}")!.TransferDragOut == TransferDragOut.Copy
            && System.Text.Json.JsonSerializer.Deserialize<DeckSettings>("{\"TransferDragOut\":1}")!.TransferDragOut == TransferDragOut.Move && dragOut.TransferDragOut == TransferDragOut.Copy);
        DeckPages.SetSize(settings, DeckPage.Clipboard, 2000, 100);
        check("Shelf settings: the page's size is its own and kept within the limits", DeckPages.Size(settings, DeckPage.Clipboard) == (1200, 140) && settings.MonitorWidth == MonitorSizes.Default.Width);
        settings.ClipboardPage = false;
        check("Shelf settings: the file transfer's count needs the page", !settings.ShowsTransfer(CompactStyle.Notch) && new DeckSettings().ShowsTransfer(CompactStyle.Capsule)
            && !new DeckSettings().ShowsTransfer(CompactStyle.Line));
        var normalized = new DeckSettings { ClipboardExcluded = ["KeePass.exe", " keepass ", "", "1Password", @" C:\Apps\Bitwarden.exe ", "bitwarden", @"D:\.exe"], ClipboardWidth = double.NaN };
        normalized.Normalize();
        check("Shelf settings: excluded names lose .exe, paths stay whole, repeats of a process go, and a bad size returns to the default",
            normalized.ClipboardExcluded.SequenceEqual(["KeePass", "1Password", @"C:\Apps\Bitwarden.exe"]) && normalized.ClipboardWidth == ShelfSizes.Default.Width);
        check("Shelf settings: an excluded name or path matches its process, in any case",
            ClipboardExclusions.Excludes(normalized.ClipboardExcluded, "bitwarden") && ClipboardExclusions.Excludes(normalized.ClipboardExcluded, "KEEPASS")
            && !ClipboardExclusions.Excludes(normalized.ClipboardExcluded, "Bitwarden.exe") && !ClipboardExclusions.Excludes(normalized.ClipboardExcluded, ""));
        check("Shelf settings: the page order of an older file gains the page at the end",
            DeckPages.Normalize([DeckPage.Monitor, DeckPage.Usage, DeckPage.Music]).SequenceEqual([DeckPage.Monitor, DeckPage.Usage, DeckPage.Music, DeckPage.Clipboard, DeckPage.Devices]));
    }


    // Categories' rules (ShelfRules): what goes in by itself.
    private static void Rules(Action<string, bool> check, DateTimeOffset now)
    {
        RuleCondition Kinds(params ClipKind[] kinds) => new() { Field = RuleField.Kind, Values = [.. kinds.Select(k => k.ToString())] };
        RuleCondition Text(RuleTest test, string value) => new() { Field = RuleField.Text, Test = test, Value = value };
        ClipEntry Clip(string text, string source = "") => new() { Kind = Shelf.Classify(text), Text = text, Source = source, Copied = now };
        var picture = new ClipEntry { Kind = ClipKind.Image, PixelWidth = 4, PixelHeight = 4, Source = "Snipaste" };

        var linksAndPictures = new ShelfRule { Conditions = [Kinds(ClipKind.Link, ClipKind.Image)] };
        check("Shelf rules: kinds picked together are either of them",
            ShelfRules.Matches(linksAndPictures, picture) && ShelfRules.Matches(linksAndPictures, Clip("https://example.com/a")) && !ShelfRules.Matches(linksAndPictures, Clip("hello")));

        var repo = new ShelfRule { Match = RuleMatch.All, Conditions = [Kinds(ClipKind.Link),
            new() { Field = RuleField.Site, Value = "github.com" }, new() { Field = RuleField.Source, Value = "chrome, msedge.exe" }] };
        check("Shelf rules: all conditions must hold; a site covers its subdomains; a source is a process name, .exe or not",
            ShelfRules.Matches(repo, Clip("https://gist.github.com/x", "msedge")) && !ShelfRules.Matches(repo, Clip("https://github.com/x", "firefox"))
            && !ShelfRules.Matches(repo, Clip("https://notgithub.com/x", "chrome")) && ShelfRules.Matches(repo, Clip("https://www.github.com/x", "CHROME")));

        var any = new ShelfRule { Conditions = [Text(RuleTest.Contains, "合同"), new() { Field = RuleField.Source, Value = "OUTLOOK" }] };
        check("Shelf rules: any condition is enough; text compares ignoring case and pictures meet no text condition",
            ShelfRules.Matches(any, Clip("这份合同请查收")) && ShelfRules.Matches(any, Clip("hi", "outlook")) && !ShelfRules.Matches(any, Clip("hi", "chrome"))
            && ShelfRules.Matches(new ShelfRule { Conditions = [Text(RuleTest.StartsWith, "TODO")] }, Clip("  todo: write"))
            && ShelfRules.Matches(new ShelfRule { Conditions = [Text(RuleTest.EndsWith, ".md")] }, Clip("README.MD"))
            && !ShelfRules.Matches(new ShelfRule { Conditions = [Text(RuleTest.Contains, "a")] }, picture));

        var blank = new ShelfRule { Match = RuleMatch.All, Conditions = [Text(RuleTest.Contains, "  "), Kinds(), Kinds(ClipKind.Code)] };
        check("Shelf rules: a condition not filled in is passed over, and a rule with none filled in adds nothing",
            ShelfRules.Matches(blank, Clip("var total = items.Sum(item => item.Price);")) && !ShelfRules.Active(new ShelfRule { Conditions = [Text(RuleTest.Contains, ""), Kinds()] }, ShelfSide.Clipboard)
            && !ShelfRules.Active(null, ShelfSide.Clipboard));

        var code = new ShelfRule { Conditions = [Text(RuleTest.Regex, @"^\d{4,8}$")] };
        var broken = new ShelfRule { Conditions = [Text(RuleTest.Regex, @"^(\d{4,8}$"), Kinds(ClipKind.Text)] };
        check("Shelf rules: a regular expression searches the text; one that does not read is named and stops the whole rule",
            ShelfRules.Matches(code, Clip("204816")) && !ShelfRules.Matches(code, Clip("12")) && ShelfRules.Problem(broken, ShelfSide.Clipboard) is { Length: > 0 }
            && !ShelfRules.Matches(broken, Clip("hello")) && ShelfRules.Problem(code, ShelfSide.Clipboard) is null);
        var slow = new ShelfRule { Conditions = [Text(RuleTest.Regex, @"^(a+)+$")] };
        check("Shelf rules: a regular expression that runs too long does not match", !ShelfRules.Matches(slow, Clip(new string('a', 40) + "!")));

        bool Is(string text, TextFormat format) => ShelfRules.IsFormat(text, format);
        check("Shelf rules: the built-in formats take the whole text",
            Is("name@example.com", TextFormat.Email) && !Is("write to name@example.com", TextFormat.Email)
            && Is("138 0013 8000", TextFormat.Phone) && Is("+8613800138000", TextFormat.Phone) && !Is("12800138000", TextFormat.Phone)
            && Is(@"D:\Projects\BrimDeck", TextFormat.Path) && Is(@"\\server\share\a.txt", TextFormat.Path) && !Is("D:Projects", TextFormat.Path)
            && Is("192.168.1.10:8080", TextFormat.IpAddress) && Is("::1", TextFormat.IpAddress) && !Is("300.1.1.1", TextFormat.IpAddress) && !Is("1", TextFormat.IpAddress)
            && Is("1,280.50", TextFormat.Number) && Is("-42", TextFormat.Number) && !Is("12,34", TextFormat.Number)
            && Is("5e43f0f", TextFormat.Commit) && !Is("5e43f0", TextFormat.Commit) && !Is("hello12", TextFormat.Commit));

        // Files: kinds by extension, extensions typed in, names, and folders with what is inside them.
        var design = new ShelfRule { Conditions = [new() { Field = RuleField.Extension, Value = ".psd, FIG" }, new() { Field = RuleField.Folder, Value = @"D:\Design\" }] };
        check("Shelf rules: files by extension, in either case and with or without the dot, or anywhere inside a folder",
            ShelfRules.Matches(design, @"C:\a\b.PSD") && ShelfRules.Matches(design, @"C:\x.fig") && ShelfRules.Matches(design, @"D:\Design\sub\c.txt")
            && !ShelfRules.Matches(design, @"D:\Design") && !ShelfRules.Matches(design, @"D:\Designs\c.txt"));
        var installers = new ShelfRule { Conditions = [new() { Field = RuleField.FileType, Values = [nameof(FileKind.Installer), nameof(FileKind.Pdf)] }] };
        var named = new ShelfRule { Conditions = [new() { Field = RuleField.FileName, Test = RuleTest.Contains, Value = "发票" }] };
        check("Shelf rules: file kinds go by extension, and a file's name is only its name",
            ShelfRules.Matches(installers, @"C:\Downloads\setup.MSI") && ShelfRules.Matches(installers, @"C:\a.pdf") && !ShelfRules.Matches(installers, @"C:\a.zip")
            && ShelfRules.Matches(named, @"C:\票据\三月发票.pdf") && !ShelfRules.Matches(named, @"C:\发票\a.pdf"));
        check("Shelf rules: the clipboard's conditions mean nothing to files and the other way round",
            !ShelfRules.Matches(linksAndPictures, @"C:\a.png") && !ShelfRules.Matches(installers, picture));

        // Applying: a copy goes into every category it meets, keeps those it is in; phrases and what is already there are left alone.
        var data = new ShelfData();
        var media = new ShelfCategory { Name = "链接与图片", Rule = linksAndPictures };
        var web = new ShelfCategory { Name = "网页", Rule = new ShelfRule { Conditions = [Kinds(ClipKind.Link)] } };
        var plain = new ShelfCategory { Name = "手动" };
        data.ClipCategories.AddRange([media, web, plain]);
        var link = Shelf.Add(data, Clip("https://example.com"));
        link.Categories.Add(plain.Id);
        ShelfRules.Apply(data, link);
        var phrase = Shelf.AddPhrase(data, "https://example.org", now);
        ShelfRules.Apply(data, phrase);
        check("Shelf rules: a copy goes into every category whose rule it meets, beside those it was put in by hand",
            link.Categories.SequenceEqual([plain.Id, media.Id, web.Id]) && phrase.Categories.SequenceEqual([ShelfData.Favorites]));
        var hand = Shelf.Add(data, Clip("plain words"));
        hand.Categories.Add(media.Id);
        check("Shelf rules: anything can still be put in a category by hand", data.Clips.Count(c => c.Categories.Contains(media.Id)) == 2);
        link.Categories.Remove(web.Id);
        var later = Shelf.Add(data, Clip("https://example.net"));
        check("Shelf rules: what meets a rule but is not in its category is counted for putting in, phrases aside",
            ShelfRules.Outside(data, web).SequenceEqual([later, link]) && ShelfRules.Outside(data, plain).Count == 0);
        var files = new ShelfData();
        var pdfs = new ShelfCategory { Name = "PDF", Rule = installers };
        files.FileCategories.Add(pdfs);
        foreach (var file in Shelf.AddFiles(files, [@"C:\a.pdf", @"C:\b.txt"], null, now)) ShelfRules.Apply(files, file);
        check("Shelf rules: files added go into the categories they meet; AddFiles returns them",
            files.Files.Single(f => f.Path == @"C:\a.pdf").Categories.SequenceEqual([pdfs.Id]) && files.Files.Single(f => f.Path == @"C:\b.txt").Categories.Count == 0);

        // Saved and read back, with the summary each row shows.
        var read = ShelfData.Deserialize(data.Serialize());
        var readRule = read.OwnCategories(ShelfSide.Clipboard).First().Rule!;
        check("Shelf rules: a rule is saved and read back",
            readRule.Conditions.Single().Values.SequenceEqual(["Link", "Image"]) && read.OwnCategories(ShelfSide.Clipboard).Last().Rule is null);
        Loc.Use(Loc.Chinese);
        check("Shelf rules: the row's summary says each condition, joined by 或 or 且",
            ShelfRules.Summary(linksAndPictures, ShelfSide.Clipboard) == "链接或图片"
            && ShelfRules.Summary(repo, ShelfSide.Clipboard) == "链接，且github.com 的链接，且来自 chrome、msedge");
    }
}
